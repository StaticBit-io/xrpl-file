using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile.Keys;

/// <summary>
/// Why <see cref="BackupContainer.TryUnpack"/> failed. The two "cannot decrypt" outcomes are kept
/// deliberately distinct: the header is parsed before anything is decrypted, so "this is not a
/// StaticBit backup at all" and "this is one, but the password is wrong" are different pieces of
/// information, and showing the second in place of the first sends the user hunting for a typo
/// that was never the problem.
/// </summary>
/// <remarks>
/// Lifted from StaticBit's Wallet project (a private repository), file <c>Backup/BackupContainer.cs</c>.
/// Unchanged except for the namespace and the cross-references. It replaces the single
/// <see cref="VaultError.Corrupt"/> the <c>XFK1</c> container reported for every failure.
/// </remarks>
public enum BackupUnpackError
{
    /// <summary>Unpacking succeeded; no error.</summary>
    None = 0,

    /// <summary>
    /// The container could not be taken apart. Usually the header itself did not parse -- wrong
    /// magic, unrecognized version, or data shorter than any valid container -- which is decided
    /// before any decryption is attempted. Also covers a payload that expands past
    /// <see cref="BackupContainer.MaxInflatedSize"/>, which is only visible after decryption and is
    /// deliberately not reported as <see cref="WrongPassword"/>: the password was right, the
    /// container is not something this build will open.
    /// </summary>
    NotOurContainer,

    /// <summary>
    /// The header parsed, but the data-key derivation did not yield the key the ciphertext was
    /// sealed under. This also covers a tampered header byte outside the password itself (e.g. a
    /// flipped <c>kdfId</c>): the whole header is authenticated data, so any change to it fails the
    /// GCM tag the same way a wrong password does, and the two are intentionally not told apart --
    /// the header is not attacker-editable content a user is expected to reason about, only the
    /// password is.
    /// </summary>
    WrongPassword,

    /// <summary>
    /// The container declares no password (<c>kdfId = 0</c>) and reached the unpacker from a source
    /// that is not allowed to carry such a container.
    /// </summary>
    /// <remarks>
    /// A password-less container has no nonce, no salt and no authentication tag: nothing about it
    /// is signed, so anyone can write one, and the byte that selects this parse branch is not itself
    /// covered by anything. That is acceptable for the on-screen QR one device shows to another,
    /// which is the only thing the Wallet ever produces without a password -- and it is why no path
    /// there writes one to a file, to the gallery or to the share sheet.
    ///
    /// <para>
    /// So a password-less <b>file</b> cannot be a backup either product made. Accepting one would
    /// let a stranger's file, six bytes of header and a payload of their choosing, arrive on the
    /// import screen as a list of addresses to pick from. This error is what the caller gets
    /// instead. It is deliberately distinct from <see cref="NotOurContainer"/>: the container parsed
    /// fine, the refusal is about where it came from, and the user is owed that difference.
    /// </para>
    /// </remarks>
    PasswordlessContainerNotAllowed,

    /// <summary>
    /// The header's iteration count is outside the accepted range: either it exceeds
    /// <see cref="Pbkdf2Kdf.IterationsCeiling"/>, or it is zero (no iterations at all, e.g. a
    /// tampered or truncated field). Rejected before running the KDF: a backup written on a fast
    /// device must not be able to hang an import on a slow one, and a zero count is never a value
    /// <see cref="BackupContainer.Pack"/> produces.
    /// </summary>
    IterationsOutOfRange,
}

/// <summary>
/// The wire format shared by every backup wrapper (file, QR, on-screen): a small authenticated
/// header followed by a deflate-compressed, AES-256-GCM-sealed <see cref="BackupPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from StaticBit's Wallet project (a private repository), file
/// <c>Backup/BackupContainer.cs</c>, so that a key can move between the two products. The Wallet's
/// assembly is deliberately not referenced -- it carries MAUI and wallet-domain types, and coupling
/// two applications through a security library makes both harder to change. What is adopted is a
/// format and a file, not a dependency. The changes made on the way in are listed on the members
/// that carry them; the format itself is byte for byte theirs, which
/// <c>WalletCompatibilityTests</c> proves against a container their own build wrote.
/// </para>
/// <para>
/// Layout, all multi-byte integers big-endian:
/// <code>
/// "SBW1" (4) | version (1) | kdfId (1) | iterations (4) | salt (16) | nonce (12) | ciphertext (N) | tag (16)
/// </code>
/// <c>iterations</c> and <c>salt</c> are present only when <c>kdfId != 0</c>; see the "no password"
/// remarks below. The prefix through <c>salt</c> -- i.e. everything before the nonce -- is fed to
/// AES-GCM as associated data in its entirety. That is what makes tampering <c>kdfId</c> (or a salt
/// byte) detectable at all: neither field would otherwise be covered by anything, since only
/// <c>salt</c> and <c>iterations</c> are protected indirectly by taking part in key derivation.
/// </para>
/// <para>
/// <b>No-password mode.</b> <c>kdfId = 0</c> means the payload was never encrypted: there is no
/// <c>iterations</c>, no <c>salt</c>, no <c>nonce</c> and no <c>tag</c> -- the header is just
/// <c>"SBW1" | version | 0</c>, and the deflate-compressed plaintext follows immediately. It is part
/// of the format, so this reader has to understand it; nothing in this application writes one, and
/// <see cref="TryUnpack"/> refuses to open one that arrived as a file.
/// </para>
/// </remarks>
public static class BackupContainer
{
    private const int NoPasswordKdfId = 0;

    private static readonly byte[] Magic = "SBW1"u8.ToArray();
    private const int MagicLength = 4;

    // magic + version + kdfId
    private const int HeaderPrefixLength = MagicLength + 1 + 1;

    private const int IterationsFieldLength = 4;

    // Local rather than the Wallet's VaultParameters.*, with the same values: this application's own
    // vault parameters are Argon2id's (memory, passes, lanes) and have no size constants to share.
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;

    // header used when kdfId != 0: prefix + iterations + salt
    private const int PasswordHeaderLength = HeaderPrefixLength + IterationsFieldLength + SaltSize;

    public const int CurrentVersion = 1;

    /// <summary>
    /// The largest number of keys a backup is assumed to be able to carry. Not a limit enforced
    /// anywhere else -- it is a bound for sizing <see cref="MaxInflatedSize"/>, and it is set an
    /// order of magnitude above anything a hand-maintained key list reaches.
    /// </summary>
    private const int AssumedMaxWallets = 1_000;

    /// <summary>
    /// The assumed upper bound on one <see cref="BackupWalletEntry"/> once serialized, in bytes.
    /// </summary>
    /// <remarks>
    /// Worst case per field, the longest being a mnemonic wallet with a passphrase:
    /// <list type="bullet">
    /// <item><c>keyValue</c>: a 24-word mnemonic; BIP-39's longest English word is 8 characters, so
    /// 24 words plus 23 separators is at most 215 bytes -- longer than any seed or secret key.</item>
    /// <item><c>mnemonicPassphrase</c> and <c>description</c>: free text the user types, allowed 256
    /// characters each at UTF-8's worst case of 4 bytes per character, so 1024 bytes each.</item>
    /// <item><c>classicAddress</c> and <c>masterAddress</c>: XRPL base58 addresses, 35 bytes each.</item>
    /// <item><c>algorithm</c> 32, <c>derivationPath</c> 64, <c>keyType</c> 11 (a signed int).</item>
    /// <item>JSON field names, quotes, colons, commas and braces for the eight fields: 200.</item>
    /// </list>
    /// That sums to roughly 2.6 KiB; rounded up to 4 KiB so the estimate has room to be wrong -- and
    /// the four fields this application added to the record are spent out of that same room.
    /// </remarks>
    private const int AssumedMaxEntrySize = 4 * 1024;

    /// <summary>
    /// Room for everything in the plaintext that is not a key entry -- the <c>{"wallets":[...]}</c>
    /// envelope, the separators between entries, and any field a later payload version adds.
    /// </summary>
    private const int PayloadEnvelopeAllowance = 4 * 1024;

    /// <summary>
    /// Hard ceiling on how much <see cref="Inflate"/> will produce, in bytes. A container that wants
    /// more is rejected as <see cref="BackupUnpackError.NotOurContainer"/>.
    /// </summary>
    /// <remarks>
    /// Deflate is free to expand a few kilobytes into gigabytes, and an import path puts no bound on
    /// its input at all: without this, a "backup file" someone sends a user under the guise of
    /// helping them restore -- an entirely ordinary phishing shape -- exhausts memory and takes the
    /// application down. The bound is derived rather than picked: <see cref="AssumedMaxWallets"/>
    /// entries of <see cref="AssumedMaxEntrySize"/> plus <see cref="PayloadEnvelopeAllowance"/>,
    /// which comes to about 4 MiB. It is left as the arithmetic, not folded into a literal, so that
    /// a future reader disagreeing with the number can see which assumption to change instead of
    /// guessing a new one. The check lives here, in the one function both unpack paths inflate
    /// through, so the two cannot drift apart.
    /// </remarks>
    public const int MaxInflatedSize =
        (AssumedMaxWallets * AssumedMaxEntrySize) + PayloadEnvelopeAllowance;

    /// <summary>
    /// Slack over <see cref="MaxInflatedSize"/> for the compressed form, in bytes.
    /// </summary>
    /// <remarks>
    /// Deflate is not obliged to make anything smaller: on incompressible input it falls back to
    /// stored blocks, which cost five bytes of framing per 64 KiB plus a couple for the stream, so
    /// the worst case is a shade over the input, not under it. A backup's plaintext is JSON and
    /// compresses well in practice, but nothing enforces that. 64 KiB is far more than the roughly
    /// 350 bytes the arithmetic actually calls for, deliberately: this is the slack in a bound, and
    /// a bound that is a little loose costs nothing while one that is a little tight rejects a real
    /// backup.
    /// </remarks>
    private const int DeflateExpansionAllowance = 64 * 1024;

    /// <summary>
    /// The largest a container of ours can legitimately be on the wire, in bytes -- the bound
    /// <see cref="TryUnpack"/> refuses a container's length by before anything proportional to it
    /// is allocated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lifted from the Wallet, where it is the bound a caller applies to a file's length before
    /// reading it into memory at all -- the file picker refuses anything past it, so their
    /// container code never sees an oversized array in the first place. The first port left
    /// this unlifted, reasoning that nothing here read a container from a file yet; that stopped
    /// being true once container import entered scope (design 5.1, 5.3), so the bound now guards
    /// <see cref="TryUnpack"/> itself instead of a caller this library does not have yet. Without
    /// it, a container declaring the right six header bytes and an arbitrary tail is taken exactly
    /// as far as <see cref="TryUnpackWithPassword"/>'s ciphertext and plaintext buffers, each sized
    /// off the input length, before the GCM tag gets a say -- the shape of an ordinary phishing
    /// backup, not a theoretical one.
    /// </para>
    /// <para>
    /// It is derived from the format rather than named separately, for the same reason
    /// <see cref="MaxInflatedSize"/> is: two independent ceilings agree on the day they are written
    /// and drift apart at the first change to the container, in the direction nobody notices -- a
    /// backup this code itself produced and then refuses to read back. The wire overhead is the
    /// password header, nonce and tag; the body is <see cref="MaxInflatedSize"/> plus
    /// <see cref="DeflateExpansionAllowance"/>. The password-less form is smaller by construction
    /// (no iterations, salt, nonce or tag), so this covers it too.
    /// </para>
    /// </remarks>
    public const int MaxContainerSize =
        PasswordHeaderLength
        + NonceSize
        + TagSize
        + MaxInflatedSize
        + DeflateExpansionAllowance;

    private const int InflateBufferSize = 8 * 1024;

    /// <summary>
    /// How the payload is written before it is compressed and encrypted.
    /// </summary>
    /// <remarks>
    /// Absent fields are left out rather than written as <c>null</c>, and on this format that is not
    /// tidiness: most keys have no derivation path, no master address, no folder and no destination
    /// tag, so the nulls were four keys per entry spent out of a budget that decides how many keys
    /// fit in one QR code. Reading is unaffected -- a missing key and an explicit null both
    /// deserialise to the record's default, which is what every optional field here already declares.
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Packs <paramref name="payload"/> into the wire format above. Passing <see langword="null"/>
    /// for <paramref name="password"/> produces the no-password container described in the type
    /// remarks, and <paramref name="iterations"/> is unused on that path.
    /// </summary>
    /// <remarks>
    /// The Wallet's signature also takes an <c>IPasswordKdf</c>. Here the algorithm is fixed by the
    /// <c>kdfId</c> the format writes, so there is nothing to dispatch on and the derivation is a
    /// direct call to <see cref="Pbkdf2Kdf.Derive"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="iterations"/> is not positive, or exceeds
    /// <see cref="Pbkdf2Kdf.IterationsCeiling"/> -- the most this build will run when opening a
    /// container, so a container above it would report success and never open again.
    /// </exception>
    public static byte[] Pack(BackupPayload payload, string? password, int iterations)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (password is null)
        {
            return PackWithoutPassword(payload);
        }

        // Scrubbed below, like the derived key. The password arrived as a string and that string
        // cannot be erased, so this buys less than it looks like it does -- but leaving an extra copy
        // of it on the heap on every container packed is a cost with no benefit at all.
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return PackWithPassword(payload, passwordBytes, iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Packs under a password this application already holds as UTF-8 bytes rather than as a string.
    /// </summary>
    /// <remarks>
    /// Not in the Wallet, which takes passwords as strings throughout. Everything in this library
    /// carries a password as bytes it can erase, and <see cref="IKeyVault.ExportAsync"/> is handed
    /// one that way; routing it through the string overload would mean building an immutable copy of
    /// the export password that nothing can ever clear. There is no no-password form of this: a
    /// caller holding a password is not asking for a container that has none.
    /// </remarks>
    internal static byte[] PackWithPassword(BackupPayload payload, ReadOnlySpan<byte> password, int iterations)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        // The same ceiling TryUnpackWithPassword refuses to run a KDF above. Without it this method
        // will happily write a header that our own importer then rejects as IterationsOutOfRange
        // before the first round -- a backup that reports success and can never be opened, which is
        // worse than a refusal here because the user only finds out when they need it.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, Pbkdf2Kdf.IterationsCeiling);

        byte[] compressed = CompressPayload(payload);
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] header = BuildPasswordHeader(Pbkdf2Kdf.KdfId, iterations, salt);

            byte[] key = Pbkdf2Kdf.Derive(password, salt, iterations, KeySize);
            try
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
                byte[] ciphertext = new byte[compressed.Length];
                byte[] tag = new byte[TagSize];

                using AesGcm aes = new(key, TagSize);
                aes.Encrypt(nonce, compressed, ciphertext, tag, header);

                byte[] container = new byte[header.Length + nonce.Length + ciphertext.Length + tag.Length];
                int offset = 0;
                header.CopyTo(container, offset); offset += header.Length;
                nonce.CopyTo(container, offset); offset += nonce.Length;
                ciphertext.CopyTo(container, offset); offset += ciphertext.Length;
                tag.CopyTo(container, offset);

                return container;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            // The compressed plaintext is the seeds themselves. The Wallet leaves it to the
            // collector; this library erases every buffer that has held key material since the vault
            // record codec did, and a container is the one artifact that handles a seed in the open.
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    private static byte[] PackWithoutPassword(BackupPayload payload)
    {
        byte[] compressed = CompressPayload(payload);
        try
        {
            byte[] container = new byte[HeaderPrefixLength + compressed.Length];
            WriteHeaderPrefix(container, NoPasswordKdfId);
            compressed.CopyTo(container, HeaderPrefixLength);
            return container;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    /// <summary>
    /// Whether <paramref name="container"/>'s header says it carries no password, and so will be
    /// taken apart with no key derivation, no decryption and no authentication check.
    /// </summary>
    /// <remarks>
    /// Answered from the header alone, before anything is unpacked, so that a caller can refuse the
    /// container on the strength of where it came from -- see
    /// <see cref="BackupUnpackError.PasswordlessContainerNotAllowed"/>. It says nothing about
    /// whether the payload is valid; a container this returns <see langword="false"/> for is simply
    /// worth handing to <see cref="TryUnpack"/>.
    ///
    /// <para>
    /// <see langword="false"/> for anything that is not one of our containers at all -- too short to
    /// have a header, foreign magic, or a version this build does not know. The KDF byte only means
    /// anything behind our own header, and a caller that skipped that check would describe an
    /// arbitrary file whose sixth byte happens to be zero -- a ZIP, for one -- as an unprotected
    /// backup. <see cref="TryUnpack"/> turns all of those away as
    /// <see cref="BackupUnpackError.NotOurContainer"/>, which is what the user needs to be told.
    /// </para>
    /// </remarks>
    public static bool DeclaresNoPassword(ReadOnlySpan<byte> container)
    {
        return HasSupportedHeader(container) && container[MagicLength + 1] == NoPasswordKdfId;
    }

    /// <summary>
    /// Reverses <see cref="Pack"/>. Returns <see langword="false"/> with <paramref name="error"/>
    /// set on any failure; never throws for malformed or foreign input.
    /// </summary>
    /// <param name="data">The container's bytes.</param>
    /// <param name="password">The password to open it with, or <see langword="null"/> for none.</param>
    /// <param name="fromFile">
    /// Whether these bytes arrived as a file. The Wallet's signature has no such flag; refusing a
    /// password-less container is a judgement about where it came from, and the container cannot
    /// know that for itself. <see langword="true"/> refuses one with
    /// <see cref="BackupUnpackError.PasswordlessContainerNotAllowed"/>.
    /// </param>
    /// <param name="payload">The payload, when this returns <see langword="true"/>.</param>
    /// <param name="error">Why it failed, when this returns <see langword="false"/>.</param>
    public static bool TryUnpack(
        byte[] data,
        string? password,
        bool fromFile,
        out BackupPayload? payload,
        out BackupUnpackError error)
    {
        ArgumentNullException.ThrowIfNull(data);

        payload = null;

        if (data.Length < HeaderPrefixLength || !data.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }

        int version = data[MagicLength];
        int kdfId = data[MagicLength + 1];

        if (version != CurrentVersion)
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }

        // Refused here, before either branch below allocates a buffer sized off data.Length: a
        // container announcing nothing but a valid six-byte header can otherwise carry an
        // arbitrary tail, and TryUnpackWithPassword copies that tail into ciphertext and
        // plaintext buffers of its own size before the GCM tag gets a chance to say no. The
        // password was never the question for a container this large, so it is reported the same
        // way any other header this build does not recognise is.
        if (data.Length > MaxContainerSize)
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }

        if (kdfId == NoPasswordKdfId)
        {
            if (fromFile)
            {
                error = BackupUnpackError.PasswordlessContainerNotAllowed;
                return false;
            }

            return TryUnpackNoPassword(data, out payload, out error);
        }

        return TryUnpackWithPassword(data, password, out payload, out error);
    }

    /// <summary>
    /// Whether <paramref name="container"/> begins with a header this build can read: our magic and
    /// a version it knows. Says nothing about the payload.
    /// </summary>
    /// <remarks>
    /// Private, unlike the Wallet's: there it is public so a QR recogniser can reject a scanned code
    /// while the camera is still open, and this application has no such caller.
    /// </remarks>
    private static bool HasSupportedHeader(ReadOnlySpan<byte> container)
    {
        return container.Length >= HeaderPrefixLength
               && container[..MagicLength].SequenceEqual(Magic)
               && container[MagicLength] == CurrentVersion;
    }

    private static bool TryUnpackNoPassword(byte[] data, out BackupPayload? payload, out BackupUnpackError error)
    {
        payload = null;

        try
        {
            byte[] plaintext = Inflate(data.AsSpan(HeaderPrefixLength).ToArray());
            try
            {
                payload = DeserializePayload(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (InflatedPayloadTooLargeException)
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }

        error = BackupUnpackError.None;
        return true;
    }

    private static bool TryUnpackWithPassword(
        byte[] data, string? password, out BackupPayload? payload, out BackupUnpackError error)
    {
        payload = null;

        int minimumLength = PasswordHeaderLength + NonceSize + TagSize;
        if (data.Length < minimumLength)
        {
            error = BackupUnpackError.NotOurContainer;
            return false;
        }

        // Read as unsigned: a tampered or attacker-supplied header can set the top bit, which would
        // otherwise turn into a negative int and slip past a "> ceiling" comparison.
        uint iterationsRaw = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(HeaderPrefixLength, IterationsFieldLength));
        byte[] salt = data.AsSpan(HeaderPrefixLength + IterationsFieldLength, SaltSize).ToArray();

        // Checked, and rejected, before a single round of the KDF runs: that is the entire point of
        // a ceiling -- a cost paid on a fast device must not become an import that never finishes on
        // a slow one.
        if (iterationsRaw == 0 || iterationsRaw > (uint)Pbkdf2Kdf.IterationsCeiling)
        {
            error = BackupUnpackError.IterationsOutOfRange;
            return false;
        }

        int iterations = (int)iterationsRaw;

        if (password is null)
        {
            // No password to try against a container that needs one; nothing else can be concluded
            // about it without attempting a derivation that would never succeed.
            error = BackupUnpackError.WrongPassword;
            return false;
        }

        byte[] header = data.AsSpan(0, PasswordHeaderLength).ToArray();
        byte[] nonce = data.AsSpan(PasswordHeaderLength, NonceSize).ToArray();

        int cipherStart = PasswordHeaderLength + NonceSize;
        int cipherLength = data.Length - cipherStart - TagSize;
        byte[] ciphertext = data.AsSpan(cipherStart, cipherLength).ToArray();
        byte[] tag = data.AsSpan(cipherStart + cipherLength, TagSize).ToArray();

        // Same reasoning as Pack: erased in the finally below. Allocated and handed to Derive
        // inside its own try, unlike Pack's outer allocation of the same buffer, because here it is
        // Derive -- not the encoding call before it -- that can fail: nothing here may leave a
        // password on the heap for a throw out of the derivation to skip past.
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] key = Pbkdf2Kdf.Derive(passwordBytes, salt, iterations, KeySize);
            try
            {
                byte[] compressed = new byte[cipherLength];
                try
                {
                    using AesGcm aes = new(key, TagSize);
                    try
                    {
                        aes.Decrypt(nonce, ciphertext, tag, compressed, header);
                    }
                    catch (Exception exception) when (exception is CryptographicException or ArgumentException)
                    {
                        error = BackupUnpackError.WrongPassword;
                        return false;
                    }

                    byte[] plaintext;
                    try
                    {
                        plaintext = Inflate(compressed);
                    }
                    catch (InflatedPayloadTooLargeException)
                    {
                        // Ahead of the wrong-password catch below on purpose: the bytes did decrypt,
                        // so the password was right, and reporting it as wrong would send the user
                        // retyping a correct password against a container that will never open.
                        // Reported the same way as any other container that could not be taken apart.
                        error = BackupUnpackError.NotOurContainer;
                        return false;
                    }
                    catch (Exception exception) when (IsMalformedPayloadException(exception))
                    {
                        // The GCM tag already authenticated these exact bytes, so reaching here means
                        // they are not what Pack ever produces -- practically unreachable, but
                        // classified the same as a wrong password rather than crashing the caller.
                        error = BackupUnpackError.WrongPassword;
                        return false;
                    }

                    try
                    {
                        payload = DeserializePayload(plaintext);
                    }
                    catch (Exception exception) when (IsMalformedPayloadException(exception))
                    {
                        error = BackupUnpackError.WrongPassword;
                        return false;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(compressed);
                }

                error = BackupUnpackError.None;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool IsMalformedPayloadException(Exception exception) =>
        exception is InvalidDataException or JsonException or ArgumentException;

    /// <summary>Deserializes the plaintext and refuses anything that is not a whole payload.</summary>
    /// <remarks>
    /// The shape checks are not belt-and-braces over what the type system already says. <c>required</c>
    /// makes System.Text.Json insist the key is <em>present</em>; it says nothing about its value, so
    /// <c>{"wallets":null}</c> deserializes happily. And <see cref="BackupWalletEntry"/> is a
    /// positional record, so its members come from a constructor whose parameters carry no
    /// <c>required</c> at all -- an absent or explicitly null <c>keyValue</c> arrives as
    /// <see langword="null"/> in a member the rest of the application reads as non-nullable. This
    /// runs on bytes from a file the user picked or a QR code they scanned, i.e. untrusted input, and
    /// the failure it prevents is not theoretical: an import screen indexes by <c>ClassicAddress</c>,
    /// so a null there is a <see cref="NullReferenceException"/> raised well after
    /// <see cref="TryUnpack"/> returned success -- past the one place that turns a bad container into
    /// a message the user can act on, and therefore a crash rather than "this file is damaged".
    /// </remarks>
    /// <exception cref="InvalidDataException">The payload is structurally incomplete.</exception>
    private static BackupPayload DeserializePayload(byte[] plaintext)
    {
        BackupPayload payload = JsonSerializer.Deserialize<BackupPayload>(plaintext, SerializerOptions)
            ?? throw new InvalidDataException("Backup payload deserialized to null.");

        if (payload.Wallets is null)
        {
            throw new InvalidDataException("Backup payload has no key list.");
        }

        for (int index = 0; index < payload.Wallets.Count; index++)
        {
            BackupWalletEntry? entry = payload.Wallets[index];

            if (entry is null)
            {
                throw new InvalidDataException($"Backup key {index} is null.");
            }

            if (entry.KeyValue is null)
            {
                throw new InvalidDataException($"Backup key {index} has no key value.");
            }

            if (entry.ClassicAddress is null)
            {
                throw new InvalidDataException($"Backup key {index} has no classic address.");
            }

            if (entry.Algorithm is null)
            {
                throw new InvalidDataException($"Backup key {index} has no signing algorithm.");
            }
        }

        return payload;
    }

    private static void WriteHeaderPrefix(byte[] header, int kdfId)
    {
        Magic.CopyTo(header, 0);
        header[MagicLength] = (byte)CurrentVersion;
        header[MagicLength + 1] = (byte)kdfId;
    }

    private static byte[] BuildPasswordHeader(int kdfId, int iterations, byte[] salt)
    {
        byte[] header = new byte[PasswordHeaderLength];
        WriteHeaderPrefix(header, kdfId);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(HeaderPrefixLength, IterationsFieldLength), (uint)iterations);
        salt.CopyTo(header, HeaderPrefixLength + IterationsFieldLength);
        return header;
    }

    /// <summary>
    /// Serializes and compresses the payload in one step.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializer.SerializeToUtf8Bytes{TValue}(TValue, JsonSerializerOptions)"/>
    /// rather than the Wallet's <c>Serialize</c> plus <c>GetBytes</c>: the intermediate would be a
    /// string holding every seed in the payload, and a string cannot be erased.
    /// </remarks>
    private static byte[] CompressPayload(BackupPayload payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        try
        {
            return Deflate(json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses <paramref name="compressed"/>, refusing to produce more than
    /// <see cref="MaxInflatedSize"/> bytes.
    /// </summary>
    /// <remarks>
    /// Reads in fixed chunks and checks the running total before each write, rather than inflating
    /// first and measuring afterwards: measuring afterwards is the very allocation being prevented.
    /// A container landing exactly on the ceiling is accepted -- the ceiling is the largest
    /// legitimate backup this build expects, not one byte below it.
    /// </remarks>
    /// <exception cref="InvalidDataException">The stream is not valid deflate data.</exception>
    /// <exception cref="InflatedPayloadTooLargeException">
    /// The stream expands past <see cref="MaxInflatedSize"/>.
    /// </exception>
    private static byte[] Inflate(byte[] compressed)
    {
        using MemoryStream input = new(compressed);
        using DeflateStream deflate = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();

        byte[] buffer = new byte[InflateBufferSize];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxInflatedSize)
            {
                throw new InflatedPayloadTooLargeException();
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Signals that a container's payload expands past <see cref="MaxInflatedSize"/>.
    /// </summary>
    /// <remarks>
    /// A type of its own, deliberately outside the <see cref="IsMalformedPayloadException"/> set,
    /// because the two unpack paths must classify it the same way and the encrypted path classifies
    /// every other post-decryption failure as <see cref="BackupUnpackError.WrongPassword"/>. That
    /// default is right there -- those bytes passed the GCM tag, so nothing but a wrong password
    /// explains them -- but it is exactly wrong here: an oversized payload decrypted with the correct
    /// password, which is the phishing case, would tell the user their password is wrong and send
    /// them retyping it. Private because it never leaves this class: both callers turn it into
    /// <see cref="BackupUnpackError.NotOurContainer"/>. (It cannot simply subclass
    /// <see cref="InvalidDataException"/> -- that type is sealed -- which is just as well, since
    /// being caught by the malformed-payload filter is the one thing it must not be.)
    /// </remarks>
    private sealed class InflatedPayloadTooLargeException : Exception
    {
        public InflatedPayloadTooLargeException()
            : base($"The backup payload expands past the {MaxInflatedSize}-byte ceiling.")
        {
        }
    }
}
