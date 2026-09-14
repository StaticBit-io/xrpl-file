using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile.Keys;

/// <summary>
/// A key's only way back once the master password is forgotten (D10, section 8): a self-contained,
/// password-protected file — or the bytes behind a QR code — holding one or more entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout</b>, exactly as spec section 8, every integer big-endian:
/// <code>
/// "XFK1" (4) | version (1) | kdfId (1) | memoryKib (4) | iterations (4)
///            | parallelism (1) | salt (16) | nonce (12) | ciphertext (N) | tag (16)
/// </code>
/// The header — everything through <c>salt</c> — is the associated data of the AES-256-GCM seal.
/// Without that binding, an attacker holding a stolen container could rewrite <c>memoryKib</c> and
/// <c>iterations</c> to something cheap and brute-force the export password against the recorded
/// cost it no longer actually paid. Binding the header means a tampered header simply fails the
/// authentication tag, before a single guess is tried against anything real — the same reasoning
/// the vault record itself applies to its own header (<see cref="VaultRecordCodec.Header"/>).
/// </para>
/// <para>
/// The plaintext is the deflate-compressed JSON array of entries, compressed before encryption and
/// never after — compressing ciphertext buys nothing, since ciphertext is indistinguishable from
/// random bytes. The key that opens the seal is derived from the export password alone:
/// <c>HKDF-SHA256(Argon2id(password, salt, parameters), info = "xrpl-file.export.v1")</c>. It
/// shares no key material with the vault's data key or integrity key (<see cref="KeyVault"/>'s
/// <c>IntegrityInfo</c>) even if the same password happens to protect both — domain separation by
/// <c>info</c> is exactly what stops that coincidence from mattering. A leaked container must never
/// be equivalent to a leaked vault.
/// </para>
/// <para>
/// A wrong password and a damaged container are indistinguishable here, on purpose, for the same
/// reason D5 gives the vault no password verifier: there is nothing to check a guess against
/// except the one authentication tag a correct password would also have to satisfy. Both report
/// <see cref="VaultError.Corrupt"/>, and neither ever returns a partial result — every entry is
/// recovered from one authenticated plaintext, or <see cref="Open"/> throws before returning
/// anything at all.
/// </para>
/// <para>
/// This container is always password-protected. There is no passwordless variant: a container with
/// no password would be a seed sitting in plaintext, and D1's whole point was reducing the number
/// of things that open everything, not adding a second one that opens itself.
/// </para>
/// </remarks>
public static class ExportContainer
{
    private const string Magic = "XFK1";
    private const byte CurrentVersion = 1;

    private const int MagicLength = 4;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    // "XFK1"(4) + version(1) + kdfId(1) + memoryKib(4) + iterations(4) + parallelism(1) + salt(16)
    private const int HeaderLength = MagicLength + 1 + 1 + 4 + 4 + 1 + SaltLength;

    /// <summary>
    /// Domain separation for the container's key, so that it never coincides with the vault's data
    /// key or integrity key even when the very same Argon2id output would otherwise result from a
    /// reused password.
    /// </summary>
    private static readonly byte[] ExportInfo = "xrpl-file.export.v1"u8.ToArray();

    private static readonly IPasswordKdf Kdf = new Argon2idKdf();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Seals <paramref name="entries"/> into a portable container under <paramref name="password"/>.
    /// </summary>
    /// <remarks>
    /// Never touches a vault: the caller already holds <paramref name="entries"/> from
    /// <see cref="IKeyVault.RevealAsync"/>, one at a time or several at once, and it is the
    /// caller's job to mark each exported entry afterwards — this method has no vault reference to
    /// do that with, and marking an entry exported before the container it names is safely on disk
    /// would be a lie the vault told about itself.
    /// </remarks>
    public static byte[] Protect(
        IReadOnlyList<VaultEntry> entries, ReadOnlySpan<byte> password, KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(parameters);

        // Redundant since KdfParameters began refusing the same range in its own constructor, and
        // kept for the reason a lock on an inner door is kept: this is the guard that says what
        // *this* format can carry, and it must not quietly stop being checked if that type's
        // bounds ever move.
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Parallelism, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(parameters.MemoryKib);
        ArgumentOutOfRangeException.ThrowIfNegative(parameters.Iterations);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] header = BuildHeader(salt, parameters);

        byte[] plaintext = Compress(Serialise(entries));
        byte[] exportKey = DeriveExportKey(password, salt, parameters);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagLength];

            using (AesGcm cipher = new(exportKey, TagLength))
            {
                cipher.Encrypt(nonce, plaintext, ciphertext, tag, header);
            }

            byte[] container = new byte[header.Length + NonceLength + ciphertext.Length + TagLength];
            int offset = 0;
            header.CopyTo(container, offset);
            offset += header.Length;
            nonce.CopyTo(container, offset);
            offset += NonceLength;
            ciphertext.CopyTo(container, offset);
            offset += ciphertext.Length;
            tag.CopyTo(container, offset);

            return container;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(exportKey);
        }
    }

    /// <summary>
    /// Opens a container sealed by <see cref="Protect"/>.
    /// </summary>
    /// <exception cref="VaultException">
    /// <see cref="VaultError.Corrupt"/> when the container is not one this format could ever have
    /// produced (too short, a wrong magic, a header that fails to authenticate — which is also what
    /// a wrong password looks like from here) or <see cref="VaultError.Unsupported"/> when the
    /// magic and length are right but the version or KDF identifier are not ones this build knows.
    /// </exception>
    public static IReadOnlyList<VaultEntry> Open(ReadOnlySpan<byte> container, ReadOnlySpan<byte> password)
    {
        if (container.Length < HeaderLength + NonceLength + TagLength)
        {
            throw new VaultException(VaultError.Corrupt, "The export container is too short to be one.");
        }

        ReadOnlySpan<byte> header = container[..HeaderLength];
        if (!header[..MagicLength].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new VaultException(VaultError.Corrupt, "This is not an xrpl-file export container.");
        }

        byte version = header[4];
        byte kdfId = header[5];

        if (version != CurrentVersion || kdfId != Kdf.KdfId)
        {
            throw new VaultException(
                VaultError.Unsupported,
                "This build cannot open an export container of this format version or KDF.");
        }

        int memoryKib;
        int iterations;
        byte parallelism;
        byte[] salt;
        try
        {
            memoryKib = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[6..10]));
            iterations = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[10..14]));
            parallelism = header[14];
            salt = header[15..HeaderLength].ToArray();
        }
        catch (OverflowException ex)
        {
            // A field this format could never have written — corruption, not a legitimate cost
            // this build merely disagrees with.
            throw new VaultException(VaultError.Corrupt, "The export container's header is malformed.", ex);
        }

        if (!KdfParameterBounds.CouldHaveBeenWritten(memoryKib, iterations, parallelism))
        {
            // The same refusal the vault record makes, and for the same reason: the catch below
            // covers only what the KDF itself rejects outright, which leaves every cost that is
            // merely unpayable — a header asking for two terabytes is answered here rather than by
            // the allocator. Asked before KdfParameters is built from these numbers, which refuses
            // the same range as an argument fault; a damaged container is damage.
            throw new VaultException(
                VaultError.Corrupt, "The export container's KDF cost is not one any build could have written.");
        }

        KdfParameters parameters = new(memoryKib, iterations, parallelism);

        ReadOnlySpan<byte> nonce = container.Slice(HeaderLength, NonceLength);
        ReadOnlySpan<byte> ciphertext = container[(HeaderLength + NonceLength)..^TagLength];
        ReadOnlySpan<byte> tag = container[^TagLength..];

        byte[] exportKey;
        try
        {
            exportKey = DeriveExportKey(password, salt, parameters);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // The header carried a cost outside what the KDF accepts at all (e.g. zero memory) —
            // still corruption, not a real cost a legitimate write would ever have used.
            throw new VaultException(VaultError.Corrupt, "The export container's header is malformed.", ex);
        }

        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using (AesGcm cipher = new(exportKey, TagLength))
            {
                try
                {
                    cipher.Decrypt(nonce, ciphertext, tag, plaintext, header);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    // Indistinguishable from a damaged file, on purpose: the same reasoning as D5 —
                    // there is no verifier a wrong-password guess could be checked against instead,
                    // and the header binding above means a tampered header lands here too.
                    throw new VaultException(
                        VaultError.Corrupt, "The export container is damaged, or the password is wrong.", ex);
                }
            }

            byte[] json = Decompress(plaintext);
            try
            {
                return Deserialise(json);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exportKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] BuildHeader(byte[] salt, KdfParameters parameters)
    {
        byte[] header = new byte[HeaderLength];
        Encoding.ASCII.GetBytes(Magic, header.AsSpan(0, MagicLength));
        header[4] = CurrentVersion;
        header[5] = (byte)Kdf.KdfId;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), (uint)parameters.MemoryKib);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10, 4), (uint)parameters.Iterations);
        header[14] = (byte)parameters.Parallelism;
        salt.CopyTo(header.AsSpan(15, SaltLength));
        return header;
    }

    /// <summary>
    /// <c>HKDF-SHA256(Argon2id(password, salt, parameters), info = "xrpl-file.export.v1")</c> — the
    /// container's own key schedule, independent of any vault. See the type's remarks for why the
    /// second step exists at all rather than using the Argon2id output directly.
    /// </summary>
    private static byte[] DeriveExportKey(ReadOnlySpan<byte> password, byte[] salt, KdfParameters parameters)
    {
        byte[] passwordKey = Kdf.Derive(password, salt, parameters, KeyLength);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, passwordKey, KeyLength, salt: null, info: ExportInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordKey);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using MemoryStream input = new(data);
        using DeflateStream deflate = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Serialise(IReadOnlyList<VaultEntry> entries)
    {
        EntryDto[] dtos = new EntryDto[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            VaultEntryInfo info = entries[i].Info;
            dtos[i] = new EntryDto
            {
                Reference = info.Reference,
                Kind = info.Kind.ToString(),
                Label = info.Label,
                Network = info.Network,
                Address = info.Address,
                CreatedAt = info.CreatedAt,
                LastKnownSequence = info.LastKnownSequence,
                DeletableFromLedger = info.DeletableFromLedger,
                Exported = info.Exported,
                Secret = entries[i].Secret,
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(dtos, SerializerOptions);
    }

    private static IReadOnlyList<VaultEntry> Deserialise(byte[] json)
    {
        EntryDto[]? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<EntryDto[]>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new VaultException(
                VaultError.Unsupported, "The export container's plaintext is not entries this build reads.", ex);
        }

        if (dtos is null)
        {
            return [];
        }

        VaultEntry[] entries = new VaultEntry[dtos.Length];
        for (int i = 0; i < dtos.Length; i++)
        {
            entries[i] = ToEntry(dtos[i]);
        }

        return entries;
    }

    private static VaultEntry ToEntry(EntryDto dto)
    {
        if (dto.Reference is null || dto.Kind is null || dto.Label is null || dto.Network is null ||
            dto.Address is null || dto.Secret is null)
        {
            throw new VaultException(
                VaultError.Unsupported, "An entry in the export container is missing a required field.");
        }

        VaultEntryKind kind;
        try
        {
            kind = Enum.Parse<VaultEntryKind>(dto.Kind);
        }
        catch (ArgumentException ex)
        {
            throw new VaultException(
                VaultError.Unsupported, "An entry in the export container has a kind this build does not know.", ex);
        }

        VaultEntryInfo info = new(
            dto.Reference,
            kind,
            dto.Label,
            dto.Network,
            dto.Address,
            dto.CreatedAt,
            dto.LastKnownSequence,
            dto.DeletableFromLedger,
            dto.Exported);

        return new VaultEntry(info, dto.Secret);
    }

    /// <summary>
    /// The container's wire shape for one entry. Kept distinct from <see cref="VaultEntryInfo"/>
    /// for the same reason <c>VaultRecordCodec</c> keeps its DTO distinct from <see cref="VaultRecord"/>:
    /// this one is a format another build, or another language, may one day read.
    /// </summary>
    private sealed class EntryDto
    {
        [JsonPropertyName("reference")]
        public string? Reference { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("network")]
        public string? Network { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("lastKnownSequence")]
        public uint? LastKnownSequence { get; set; }

        [JsonPropertyName("deletableFromLedger")]
        public long? DeletableFromLedger { get; set; }

        [JsonPropertyName("exported")]
        public bool Exported { get; set; }

        [JsonPropertyName("secret")]
        public string? Secret { get; set; }
    }
}
