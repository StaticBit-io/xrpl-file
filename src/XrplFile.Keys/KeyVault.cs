using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XrplFile.Keys;

/// <summary>
/// The vault: one file, one password, and the keys that sign publications handed back one at a
/// time. See the design document for the key schedule (4), the record (5) and the concurrency
/// rules this class exists to enforce (6.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate.</b> Every public method takes one <see cref="SemaphoreSlim"/> and holds it for the
/// whole operation, <see cref="LockAsync"/> included. This is not tidiness. Idle auto-lock fires
/// on a timer and can land in the middle of an encryption; a lock that zeroed the data key
/// mid-operation would produce an entry encrypted under a half-wiped key — which the writing code
/// would then verify successfully against that same wiped array in memory, record as stored, and
/// never open again.
/// </para>
/// <para>
/// Three rules follow, and all three are enforced here rather than assumed: an operation takes its
/// own copy of the data key under the gate and works on the copy, so the array
/// <see cref="LockAsync"/> zeroes is never the array a cipher is reading; <see cref="LockAsync"/>
/// waits for the operation in flight instead of cutting it off; and a result produced by an
/// operation that began before a lock is discarded rather than returned.
/// </para>
/// <para>
/// <b>The cache.</b> An unlocked vault holds the parsed record and the unwrapped data key.
/// <see cref="RevealAsync"/> serves from that cache and never re-reads the file: a publication
/// signs thousands of times, and a file read per signature would put the disk on the ledger's
/// critical path. The cache is updated by every write the vault makes and cleared by
/// <see cref="LockAsync"/>.
/// </para>
/// </remarks>
public sealed class KeyVault : IKeyVault, IAsyncDisposable
{
    /// <summary>The record format this build writes and is willing to open.</summary>
    private const int CurrentVersion = 1;

    private const int SaltLength = 16;
    private const int DataKeyLength = 32;
    private const int IntegrityKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// Domain separation for the integrity key, so that it and the data key never coincide and so
    /// that an export container derives its own key rather than reusing this one.
    /// </summary>
    private static readonly byte[] IntegrityInfo = "xrpl-file.vault.integrity"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly IPasswordKdf _kdf;
    private readonly KdfParameters _parameters;

    private VaultRecord? _record;
    private byte[]? _dataKey;

    /// <summary>
    /// Bumped by every lock and every unlock. An operation reads it under the gate when it starts
    /// and again before it publishes a result; a change means the vault it was working on is gone.
    /// </summary>
    private long _generation;

    private volatile bool _isUnlocked;
    private volatile string[] _references = [];
    private volatile bool _disposed;

    /// <param name="path">The vault file. Its directory must exist.</param>
    /// <param name="kdf">Password stretching. Its identifier is written into the record.</param>
    /// <param name="devices">
    /// Where "remember on this device" keeps its secret. Defaults to
    /// <see cref="NoDeviceSecretStore"/>, so a vault works with no platform underneath it.
    /// </param>
    /// <param name="parameters">
    /// The cost to write new vaults at. Defaults to the measured <see cref="KdfParameters.Default"/>.
    /// Vaults already written are always opened at the cost recorded inside them, so this changes
    /// nothing about existing files.
    /// </param>
    public KeyVault(
        string path,
        IPasswordKdf kdf,
        IDeviceSecretStore? devices = null,
        KdfParameters? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(kdf);

        _path = Path.GetFullPath(path);
        _kdf = kdf;
        _parameters = parameters ?? KdfParameters.Default;
        DeviceSecrets = devices ?? new NoDeviceSecretStore();
    }

    /// <summary>
    /// The device-secret seam this vault was built with. Device unlock itself is not implemented
    /// yet; the store is held here because the vault, not its caller, owns that relationship.
    /// </summary>
    internal IDeviceSecretStore DeviceSecrets { get; }

    /// <inheritdoc/>
    public bool IsUnlocked => _isUnlocked;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> References => _references;

    /// <inheritdoc/>
    /// <remarks>
    /// Always empty in this build. The unlock-time sweep that fills it, and the
    /// <see cref="VaultUnlockResult.VaultDegraded"/> outcome that reports it, are not implemented
    /// yet; a reveal whose entry fails its tag still throws
    /// <see cref="VaultError.EntryDamaged"/>, so no damaged entry is ever handed back as a key.
    /// </remarks>
    public IReadOnlyList<string> DamagedReferences => [];

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return FileIsThere();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FileIsThere())
            {
                throw new InvalidOperationException(
                    "A vault already exists at this path. Creating over it would destroy every key inside it.");
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            byte[] dataKey = RandomNumberGenerator.GetBytes(DataKeyLength);

            VaultRecord record = new()
            {
                V = CurrentVersion,
                KdfId = _kdf.KdfId,
                KdfParams = _parameters,
                Salt = salt,
                Dek = [],
                Mac = [],
            };

            byte[] passwordKey = await DerivePasswordKeyAsync(password, salt, _parameters, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                record.Dek = Seal(passwordKey, dataKey, VaultRecordCodec.Header(record));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordKey);
            }

            Reseal(record, dataKey);
            await DurableFile.WriteAsync(_path, VaultRecordCodec.Serialise(record), cancellationToken)
                .ConfigureAwait(false);

            Install(record, dataKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await DurableFile.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return VaultUnlockResult.VaultMissing;
            }

            VaultRecord record;
            try
            {
                record = VaultRecordCodec.Parse(bytes);
            }
            catch (VaultException ex) when (ex.Error == VaultError.Corrupt)
            {
                return VaultUnlockResult.VaultCorrupt;
            }

            if (record.V != CurrentVersion || record.KdfId != _kdf.KdfId)
            {
                // Checked before the derivation: there is no point spending half a second of
                // Argon2 on a record this build could not open even with the right answer.
                return VaultUnlockResult.VaultUnsupported;
            }

            byte[]? dataKey = null;
            byte[] passwordKey = await DerivePasswordKeyAsync(password, record.Salt, record.KdfParams, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!TryOpen(passwordKey, record.Dek, VaultRecordCodec.Header(record), out dataKey))
                {
                    // Nothing has been touched: a failed attempt leaves an already-open vault open.
                    return VaultUnlockResult.WrongPassword;
                }

                if (dataKey.Length != DataKeyLength)
                {
                    return VaultUnlockResult.VaultCorrupt;
                }

                Install(record, dataKey);
                dataKey = null;
                return VaultUnlockResult.Success;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordKey);
                if (dataKey is not null)
                {
                    CryptographicOperations.ZeroMemory(dataKey);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task LockAsync()
    {
        // No cancellation token, and the gate is taken without one: a lock that could be refused
        // is not a lock. Whatever is in flight finishes first, and then the key goes.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Forget();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                List<VaultEntryInfo> listed = new(record.Entries.Count);
                foreach (KeyValuePair<string, byte[]> entry in record.Entries)
                {
                    // A damaged entry is left out rather than thrown over: one unreadable key must
                    // not make the others undrawable. What is in References and missing here is
                    // what is damaged.
                    if (TryReadEntry(dataKey, entry.Key, entry.Value, out VaultEntry? read))
                    {
                        listed.Add(read.Info);
                    }
                }

                EnsureStillOpen(generation);
                return listed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                string reference = Guid.NewGuid().ToString("N");
                VaultEntryInfo info = new(
                    reference,
                    entry.Kind,
                    entry.Label,
                    entry.Network,
                    entry.Address,
                    DateTimeOffset.UtcNow,
                    LastKnownSequence: null,
                    DeletableFromLedger: null,
                    Exported: false);

                record.Entries[reference] = SealEntry(dataKey, reference, new VaultEntry(info, entry.Secret));

                try
                {
                    await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    record.Entries.Remove(reference);
                    _references = [.. record.Entries.Keys];
                    throw;
                }

                // Throws rather than returning the reference: a reference handed back for an entry
                // that is not in the vault would be persisted against a publication, and that is
                // exactly the key-less publication this vault exists to prevent.
                EnsureStillOpen(generation);
                return reference;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // Carried for diagnostics and never written to disk, so it is required but not recorded:
        // a call site that cannot say why it wants a key is a call site worth noticing.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                VaultEntry entry = ReadEntryOrThrow(dataKey, reference, FindEntry(record, reference));
                EnsureStillOpen(generation);
                return entry;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(edit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                byte[] previous = FindEntry(record, reference);
                VaultEntry entry = ReadEntryOrThrow(dataKey, reference, previous);

                VaultEntryInfo edited = entry.Info with
                {
                    Label = edit.Label ?? entry.Info.Label,
                    LastKnownSequence = edit.LastKnownSequence ?? entry.Info.LastKnownSequence,
                    DeletableFromLedger = edit.DeletableFromLedger ?? entry.Info.DeletableFromLedger,
                    Exported = edit.Exported ?? entry.Info.Exported,
                };

                record.Entries[reference] = SealEntry(dataKey, reference, entry with { Info = edited });

                try
                {
                    await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    record.Entries[reference] = previous;
                    throw;
                }

                EnsureStillOpen(generation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                // Deleting what is not there is the end state the caller asked for, so it is
                // neither an error nor a write.
                if (!record.Entries.TryGetValue(reference, out byte[]? previous))
                {
                    return;
                }

                record.Entries.Remove(reference);

                try
                {
                    await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    record.Entries[reference] = previous;
                    _references = [.. record.Entries.Keys];
                    throw;
                }

                EnsureStillOpen(generation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Locks the vault and releases the gate. Disposing twice is a no-op.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await LockAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    // ---- state, touched only under the gate ----------------------------------------------------

    private bool FileIsThere() => File.Exists(_path) || File.Exists(_path + BackupSuffix);

    private VaultRecord RequireUnlocked() =>
        _record is not null && _dataKey is not null
            ? _record
            : throw new VaultException(VaultError.Locked, "The vault is locked.");

    /// <summary>
    /// Hands the operation its own copy of the data key. The copy is what the ciphers read, so
    /// that the array <see cref="Forget"/> zeroes can never be the array a cipher is midway
    /// through — the exact failure that would seal an entry under a half-wiped key.
    /// </summary>
    private byte[] BorrowDataKey(out long generation)
    {
        byte[] key = _dataKey ?? throw new VaultException(VaultError.Locked, "The vault is locked.");
        generation = _generation;
        return (byte[])key.Clone();
    }

    /// <summary>
    /// Refuses to publish a result produced by an operation that began before a lock. With the
    /// gate held end to end this cannot fire today; it is here so that the rule stays enforced
    /// rather than merely implied if a later change ever releases the gate for the slow part.
    /// </summary>
    private void EnsureStillOpen(long generation)
    {
        if (_generation != generation || _dataKey is null)
        {
            throw new VaultException(
                VaultError.Locked,
                "The vault was locked while the operation was in flight, so its result is discarded.");
        }
    }

    private void Install(VaultRecord record, byte[] dataKey)
    {
        Forget();
        _record = record;
        _dataKey = dataKey;
        _isUnlocked = true;
        _references = [.. record.Entries.Keys];
    }

    private void Forget()
    {
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey = null;
        }

        _record = null;
        _isUnlocked = false;
        _references = [];
        _generation++;
    }

    // ---- the record ----------------------------------------------------------------------------

    private async Task PersistAsync(VaultRecord record, byte[] dataKey, CancellationToken cancellationToken)
    {
        Reseal(record, dataKey);
        await DurableFile.WriteAsync(_path, VaultRecordCodec.Serialise(record), cancellationToken)
            .ConfigureAwait(false);
        _references = [.. record.Entries.Keys];
    }

    /// <summary>
    /// Recomputes the integrity code over the record as it now stands. It covers the header and
    /// every entry, so it catches what a per-entry tag cannot: an entry deleted from the file, and
    /// the file rolled back to an earlier version.
    /// </summary>
    private static void Reseal(VaultRecord record, byte[] dataKey)
    {
        byte[] integrityKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, dataKey, IntegrityKeyLength, salt: null, info: IntegrityInfo);
        try
        {
            record.Mac = VaultRecordCodec.Mac(integrityKey, record);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(integrityKey);
        }
    }

    private static byte[] FindEntry(VaultRecord record, string reference) =>
        record.Entries.TryGetValue(reference, out byte[]? entry)
            ? entry
            : throw new VaultException(VaultError.NoSuchEntry, $"No entry at reference '{reference}'.");

    private Task<byte[]> DerivePasswordKeyAsync(
        ReadOnlyMemory<byte> password, byte[] salt, KdfParameters parameters, CancellationToken cancellationToken)
    {
        IPasswordKdf kdf = _kdf;

        // Off the caller's thread on purpose. The call sites are user-interface event handlers on
        // a dispatcher thread, and a rule that every one of them must remember to move off it is a
        // rule that gets forgotten once.
        return Task.Run(() => kdf.Derive(password.Span, salt, parameters, DataKeyLength), cancellationToken);
    }

    // ---- entries -------------------------------------------------------------------------------

    private static byte[] SealEntry(byte[] dataKey, string reference, VaultEntry entry)
    {
        byte[] plaintext = EntryCodec.Write(entry);
        try
        {
            return Seal(dataKey, plaintext, Encoding.UTF8.GetBytes(reference));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static VaultEntry ReadEntryOrThrow(byte[] dataKey, string reference, byte[] sealedEntry) =>
        TryReadEntry(dataKey, reference, sealedEntry, out VaultEntry? entry)
            ? entry
            : throw new VaultException(
                VaultError.EntryDamaged,
                $"The entry at reference '{reference}' failed its authentication tag.");

    private static bool TryReadEntry(
        byte[] dataKey, string reference, byte[] sealedEntry, [NotNullWhen(true)] out VaultEntry? entry)
    {
        // The reference is the associated data, so a ciphertext moved from one entry to another
        // fails here rather than signing with the wrong key.
        if (!TryOpen(dataKey, sealedEntry, Encoding.UTF8.GetBytes(reference), out byte[]? plaintext))
        {
            entry = null;
            return false;
        }

        try
        {
            entry = EntryCodec.Read(reference, plaintext);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // ---- AES-256-GCM ---------------------------------------------------------------------------

    /// <summary>nonce (12) || ciphertext || tag (16), which is how the record's fields are shaped.</summary>
    private static byte[] Seal(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        byte[] blob = new byte[NonceLength + plaintext.Length + TagLength];
        Span<byte> nonce = blob.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        using AesGcm cipher = new(key, TagLength);
        cipher.Encrypt(
            nonce,
            plaintext,
            blob.AsSpan(NonceLength, plaintext.Length),
            blob.AsSpan(NonceLength + plaintext.Length, TagLength),
            associatedData);

        return blob;
    }

    private static bool TryOpen(
        ReadOnlySpan<byte> key, byte[] blob, ReadOnlySpan<byte> associatedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        if (blob.Length < NonceLength + TagLength)
        {
            plaintext = null;
            return false;
        }

        int length = blob.Length - NonceLength - TagLength;
        byte[] opened = new byte[length];

        try
        {
            using AesGcm cipher = new(key, TagLength);
            cipher.Decrypt(
                blob.AsSpan(0, NonceLength),
                blob.AsSpan(NonceLength, length),
                blob.AsSpan(NonceLength + length, TagLength),
                opened,
                associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(opened);
            plaintext = null;
            return false;
        }

        plaintext = opened;
        return true;
    }

    /// <summary>
    /// An entry's plaintext: the canonical JSON of the entry model, in a fixed field order. The
    /// order is part of the format — it is what an entry's bytes are — so changing it belongs with
    /// a bump of the record version, not with a refactor.
    /// </summary>
    private static class EntryCodec
    {
        internal static byte[] Write(VaultEntry entry)
        {
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                VaultEntryInfo info = entry.Info;

                writer.WriteStartObject();
                writer.WriteString("kind", info.Kind.ToString());
                writer.WriteString("label", info.Label);
                writer.WriteString("network", info.Network);
                writer.WriteString("address", info.Address);
                writer.WriteString("createdAt", info.CreatedAt);
                writer.WriteString("secret", entry.Secret);

                if (info.LastKnownSequence is uint sequence)
                {
                    writer.WriteNumber("lastKnownSequence", sequence);
                }
                else
                {
                    writer.WriteNull("lastKnownSequence");
                }

                if (info.DeletableFromLedger is long deletable)
                {
                    writer.WriteNumber("deletableFromLedger", deletable);
                }
                else
                {
                    writer.WriteNull("deletableFromLedger");
                }

                writer.WriteBoolean("exported", info.Exported);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        internal static VaultEntry Read(string reference, byte[] plaintext)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(plaintext);
                JsonElement root = document.RootElement;

                VaultEntryInfo info = new(
                    reference,
                    Enum.Parse<VaultEntryKind>(root.GetProperty("kind").GetString()!),
                    root.GetProperty("label").GetString()!,
                    root.GetProperty("network").GetString()!,
                    root.GetProperty("address").GetString()!,
                    root.GetProperty("createdAt").GetDateTimeOffset(),
                    Number(root, "lastKnownSequence") is long sequence ? (uint)sequence : null,
                    Number(root, "deletableFromLedger"),
                    root.GetProperty("exported").GetBoolean());

                return new VaultEntry(info, root.GetProperty("secret").GetString()!);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                          or ArgumentException or FormatException or OverflowException)
            {
                // The tag has already said these bytes are ours and unaltered, so anything
                // unreadable here is a shape this build does not know rather than damage.
                throw new VaultException(
                    VaultError.Unsupported,
                    $"The entry at reference '{reference}' is not in a shape this build reads.",
                    ex);
            }
        }

        private static long? Number(JsonElement root, string name)
        {
            JsonElement value = root.GetProperty(name);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
        }
    }
}
