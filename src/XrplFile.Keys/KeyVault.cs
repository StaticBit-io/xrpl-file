using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XrplFile.Keys;

/// <summary>
/// How the vault's bytes reach the disk. Production passes <see cref="DurableFile.WriteAsync"/>;
/// the seam exists so that a test can fail a write and pin the rollback that keeps the in-memory
/// record from claiming an entry the file does not hold.
/// </summary>
internal delegate Task VaultFileWriter(string path, byte[] contents, CancellationToken cancellationToken);

/// <summary>
/// Where the vault's fresh key material comes from. Production passes
/// <see cref="RandomNumberGenerator.GetBytes(int)"/>; the seam exists so that a test can keep hold
/// of the array the vault was handed and assert it was wiped.
/// </summary>
internal delegate byte[] VaultRandomBytes(int length);

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

    /// <summary>
    /// Domain separation for the device-unlock key, derived from the platform secret rather than
    /// from a password. It shares no key material with <see cref="IntegrityInfo"/> or with the
    /// password-derived key that wraps <see cref="VaultRecord.Dek"/>.
    /// </summary>
    private static readonly byte[] DeviceInfo = "xrpl-file.vault.device"u8.ToArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly IPasswordKdf _kdf;
    private readonly KdfParameters _parameters;
    private readonly VaultFileWriter _write;
    private readonly VaultRandomBytes _random;

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
        : this(path, kdf, devices, parameters, write: null, random: null)
    {
    }

    /// <summary>
    /// The same vault with its two side effects on the outside world handed in. Both default to
    /// what the public constructor uses; the seam exists so that the suite can fail a write and
    /// look at the key material the vault was given, neither of which is reachable otherwise.
    /// </summary>
    internal KeyVault(
        string path,
        IPasswordKdf kdf,
        IDeviceSecretStore? devices,
        KdfParameters? parameters,
        VaultFileWriter? write,
        VaultRandomBytes? random)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(kdf);

        _path = Path.GetFullPath(path);
        _kdf = kdf;
        _parameters = parameters ?? KdfParameters.Default;
        _write = write ?? DurableFile.WriteAsync;
        _random = random ?? RandomNumberGenerator.GetBytes;
        DeviceSecrets = devices ?? new NoDeviceSecretStore();
    }

    /// <summary>
    /// The device-secret seam this vault was built with. It is held here, rather than passed at
    /// each call, because the vault — not its caller — owns the relationship with the platform
    /// store: <see cref="EnableDeviceUnlockAsync"/> and <see cref="DisableDeviceUnlockAsync"/> are
    /// the only two places that reach into it.
    /// </summary>
    internal IDeviceSecretStore DeviceSecrets { get; }

    /// <inheritdoc/>
    public bool IsUnlocked => _isUnlocked;

    /// <inheritdoc/>
    /// <remarks>
    /// Wrapped rather than handed over: the field behind it is the vault's own idea of what it
    /// holds, and a caller who received the array itself could cast it back to <c>string[]</c> and
    /// rewrite that — with no write, and with the file left disagreeing.
    /// </remarks>
    public IReadOnlyCollection<string> References => Array.AsReadOnly(_references);

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

            byte[] salt = _random(SaltLength);
            byte[] dataKey = _random(DataKeyLength);
            try
            {
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
                await _write(_path, VaultRecordCodec.Serialise(record), cancellationToken).ConfigureAwait(false);

                Install(record, dataKey);
            }
            catch
            {
                // Install is the handover: until it runs, nothing else will ever wipe these bytes.
                // A derivation that was cancelled or a disk that refused the write would otherwise
                // leave a live data key lying in the heap of a vault that never even opened.
                CryptographicOperations.ZeroMemory(dataKey);
                throw;
            }
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

            if (!WrapCouldHoldADataKey(record.Dek))
            {
                // Damage, not a mistyped password: no password opens a wrap of an impossible
                // length, and only WrongPassword may advance a caller's failed-attempt counter.
                // Checked ahead of the derivation for the same reason as the version above.
                return VaultUnlockResult.VaultCorrupt;
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
    public async Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken)
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
                return VaultUnlockResult.VaultUnsupported;
            }

            if (record.DeviceDek is null)
            {
                // Nothing left to open with. Indistinguishable, from here, from a store that lost
                // its secret independently of the record — both mean the same thing to a caller:
                // turn the setting off.
                return VaultUnlockResult.VaultMissing;
            }

            byte[]? deviceSecret;
            try
            {
                deviceSecret = await DeviceSecrets.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The store itself failed. Transient, and never a wrong password: no password is
                // involved on this path at all.
                return VaultUnlockResult.VaultUnavailable;
            }

            if (deviceSecret is null)
            {
                return VaultUnlockResult.VaultMissing;
            }

            byte[]? dataKey = null;
            byte[] devicePasswordKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256, deviceSecret, DataKeyLength, salt: null, info: DeviceInfo);
            try
            {
                if (!TryOpen(devicePasswordKey, record.DeviceDek, VaultRecordCodec.Header(record), out dataKey))
                {
                    // The wrap does not open with the secret this store holds. Not a wrong
                    // password — there is no password here — so this is VaultMissing, not
                    // WrongPassword: the caller's job is to turn the setting off.
                    return VaultUnlockResult.VaultMissing;
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
                CryptographicOperations.ZeroMemory(devicePasswordKey);
                CryptographicOperations.ZeroMemory(deviceSecret);
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

    /// <inheritdoc/>
    /// <remarks>
    /// Reads and re-parses the file itself, rather than trusting a cached record, so that a
    /// damaged record throws here exactly as it would from <see cref="UnlockAsync"/> — a disk
    /// fault must never come back looking like a wrong password. If this instance already holds
    /// the vault open, its cache is updated with the new wrap once the write lands, so that a
    /// later store or delete does not resurrect the old one.
    /// <para>
    /// The copy the write keeps beside the vault is removed afterwards, and only here. It is what
    /// makes an interrupted write survivable everywhere else; on this one operation it is a record
    /// the password the user has just replaced still opens.
    /// </para>
    /// </remarks>
    public async Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await DurableFile.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                throw new InvalidOperationException("There is no vault at this path to change the password of.");
            }

            // A parse failure throws VaultException(Corrupt) and propagates unchanged: that is
            // exactly the "every other failure throws" this method promises.
            VaultRecord record = VaultRecordCodec.Parse(bytes);

            if (record.V != CurrentVersion || record.KdfId != _kdf.KdfId)
            {
                throw new VaultException(
                    VaultError.Unsupported, "This build cannot change the password of a record in this format.");
            }

            if (!WrapCouldHoldADataKey(record.Dek))
            {
                // Ahead of the derivation and the unwrap, because the unwrap answers a wrap of an
                // impossible length with the same refusal it gives a mistyped password — and the
                // refusal this method returns means one thing only.
                throw new VaultException(
                    VaultError.Corrupt, "The vault record's key wrap is not the shape of a wrapped data key.");
            }

            // Taken before the salt changes: it is the associated data both wraps on disk were
            // sealed under, and the device wrap must be opened under it to say whose it is.
            byte[] headerBeforeTheChange = VaultRecordCodec.Header(record);
            byte[]? deviceDekBeforeTheChange = record.DeviceDek;

            byte[] oldPasswordKey = await DerivePasswordKeyAsync(oldPassword, record.Salt, record.KdfParams, cancellationToken)
                .ConfigureAwait(false);
            byte[]? dataKey = null;
            try
            {
                if (!TryOpen(oldPasswordKey, record.Dek, headerBeforeTheChange, out dataKey))
                {
                    // The one and only condition this method reports rather than throws. Nothing
                    // on disk has been touched.
                    return VaultPasswordChange.WrongPassword;
                }

                // Not a rewrite of a single entry: the data key above is exactly what every entry
                // is already encrypted under, and it does not change here. Only its wrapping does.
                record.Salt = _random(SaltLength);

                byte[] newPasswordKey = await DerivePasswordKeyAsync(newPassword, record.Salt, record.KdfParams, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    record.Dek = Seal(newPasswordKey, dataKey, VaultRecordCodec.Header(record));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(newPasswordKey);
                }

                // The header just changed — it carries the salt — and the device wrap's associated
                // data is that same header (4.2). Left alone, a correct device secret would stop
                // opening its own wrap the moment the password changes. Re-seal it under the new
                // header when this machine holds the secret it was sealed under; drop it, rather
                // than leave a wrap no header will ever match again, when it does not. A vault that
                // never carried a device wrap gains none here: the platform secret is machine-wide
                // rather than per-vault, so "this machine holds one" is not consent.
                bool deviceUnlockDropped = false;
                if (deviceDekBeforeTheChange is not null)
                {
                    record.DeviceDek = await RewrapDeviceKeyAsync(
                            record, deviceDekBeforeTheChange, headerBeforeTheChange, dataKey, cancellationToken)
                        .ConfigureAwait(false);
                    deviceUnlockDropped = record.DeviceDek is null;
                }

                Reseal(record, dataKey);
                await _write(_path, VaultRecordCodec.Serialise(record), cancellationToken).ConfigureAwait(false);

                // Ahead of everything below, and immediately after the write: a later store or
                // delete reseals from this cache, so leaving it stale past a committed write is
                // how the old password's wrap gets written back over the new one.
                if (_record is not null)
                {
                    _record.Salt = record.Salt;
                    _record.Dek = record.Dek;
                    _record.DeviceDek = record.DeviceDek;
                    _record.Mac = record.Mac;
                }

                // The write kept the previous contents beside the vault, and those are a whole
                // record that the password just replaced still opens — reachable with no attacker
                // at all, since ReadAsync serves the copy when the main file is gone. Removed only
                // now, so that the durability of the write itself is unchanged.
                DurableFile.DiscardBackup(_path);

                if (!deviceUnlockDropped)
                {
                    return VaultPasswordChange.Changed;
                }

                await ForgetTheOrphanedDeviceSecretAsync(cancellationToken).ConfigureAwait(false);
                return VaultPasswordChange.ChangedAndDeviceUnlockDropped;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oldPasswordKey);
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
    public async Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                byte[] deviceSecret;
                try
                {
                    deviceSecret = await DeviceSecrets.GetAsync(cancellationToken).ConfigureAwait(false)
                        ?? await DeviceSecrets.CreateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    // The store refuses to make one — this build has no platform store at all.
                    // Nothing is written: the vault is exactly as it was before this call.
                    return false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A store that failed rather than refused. Typed, because every other failure
                    // this type can produce is a VaultException a caller can switch on, and a raw
                    // platform exception out of here is the one that escapes that.
                    throw new VaultException(
                        VaultError.StorageFailed,
                        "The device secret store could not be read or written, so device unlock was not enabled.",
                        ex);
                }

                try
                {
                    byte[] devicePasswordKey = HKDF.DeriveKey(
                        HashAlgorithmName.SHA256, deviceSecret, DataKeyLength, salt: null, info: DeviceInfo);

                    byte[]? previousDeviceDek = record.DeviceDek;
                    try
                    {
                        record.DeviceDek = Seal(devicePasswordKey, dataKey, VaultRecordCodec.Header(record));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(devicePasswordKey);
                    }

                    try
                    {
                        await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        record.DeviceDek = previousDeviceDek;
                        throw;
                    }

                    EnsureStillOpen(generation);
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(deviceSecret);
                }
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
    public async Task DisableDeviceUnlockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                if (record.DeviceDek is not null)
                {
                    byte[] previousDeviceDek = record.DeviceDek;
                    record.DeviceDek = null;

                    try
                    {
                        await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        record.DeviceDek = previousDeviceDek;
                        throw;
                    }

                    EnsureStillOpen(generation);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            // The vault owns the record above; the store owns its own item here. Asked for
            // unconditionally, and after the record write lands, so that a store failure above
            // leaves the platform secret in place rather than dropping it out from under a wrap
            // that a rolled-back write just put back.
            await DeviceSecrets.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-wraps the data key for "remember on this device" under a header that just changed
    /// because the password did. Returns <see langword="null"/> — dropping the device wrap rather
    /// than leaving one bound to a header nothing will ever present again — when this machine
    /// genuinely holds no secret, or holds one that is not the secret
    /// <paramref name="deviceDekBeforeTheChange"/> was sealed under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absence and failure are two different events and get two different answers. A store that
    /// says it holds nothing is the only one that may cost the user the setting; a store that
    /// could not answer is transient — the same event <see cref="UnlockWithDeviceAsync"/> reports
    /// as <see cref="VaultUnlockResult.VaultUnavailable"/> — and throws, so that a keychain locked
    /// for a moment cannot silently delete a feature. Nothing has been written when it does.
    /// </para>
    /// <para>
    /// The existing wrap is opened under the header it was sealed under before this change, as the
    /// proof that the secret this machine holds is the one device unlock was enabled with. Without
    /// that step a machine holding a secret for some other vault would re-seal this one under it
    /// and hand itself passwordless unlock nobody enabled — the secret is machine-wide (4.2), so
    /// its mere presence says nothing about this vault.
    /// </para>
    /// </remarks>
    private async Task<byte[]?> RewrapDeviceKeyAsync(
        VaultRecord record,
        byte[] deviceDekBeforeTheChange,
        byte[] headerBeforeTheChange,
        byte[] dataKey,
        CancellationToken cancellationToken)
    {
        byte[]? deviceSecret;
        try
        {
            deviceSecret = await DeviceSecrets.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VaultException(
                VaultError.StorageFailed,
                "The device secret store could not be read, so the device wrap could be neither re-sealed nor " +
                "safely dropped. The password was not changed.",
                ex);
        }

        if (deviceSecret is null)
        {
            return null;
        }

        byte[] devicePasswordKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, deviceSecret, DataKeyLength, salt: null, info: DeviceInfo);
        try
        {
            if (!TryOpen(devicePasswordKey, deviceDekBeforeTheChange, headerBeforeTheChange, out byte[]? enrolled))
            {
                return null;
            }

            CryptographicOperations.ZeroMemory(enrolled);
            return Seal(devicePasswordKey, dataKey, VaultRecordCodec.Header(record));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(devicePasswordKey);
            CryptographicOperations.ZeroMemory(deviceSecret);
        }
    }

    /// <summary>
    /// Asks the store to drop a secret the vault has just stopped pointing at, the way
    /// <see cref="DisableDeviceUnlockAsync"/> does: the vault owns the record, the store owns its
    /// item, and neither should leave the other holding something dead.
    /// </summary>
    /// <remarks>
    /// A failure here is swallowed on purpose, and it is the one place in this type where that is
    /// the right answer. The password change is already committed to disk; what is left behind is
    /// key material that opens nothing, while discarding the outcome of a committed change would
    /// leave the caller unable to say what happened — or, worse, retrying with a password that is
    /// no longer the vault's.
    /// </remarks>
    private async Task ForgetTheOrphanedDeviceSecretAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DeviceSecrets.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to do and nothing to say: see the remarks above.
        }
    }

    /// <summary>Locks the vault for good. Disposing twice is a no-op.</summary>
    /// <remarks>
    /// Disposal takes the gate like every other operation, so it cannot land in the middle of one
    /// — the same rule, and for the same reason, as <see cref="LockAsync"/>. The gate itself is
    /// deliberately not disposed: <see cref="SemaphoreSlim.Dispose()"/> only releases a wait
    /// handle this class never asks for, and disposing it would turn the typed refusal an
    /// operation arriving afterwards gets today into an <see cref="ObjectDisposedException"/>
    /// thrown out of the gate it happened to race.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            Forget();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
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
        await _write(_path, VaultRecordCodec.Serialise(record), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Whether a blob is even the shape of a wrapped data key: nonce, thirty-two sealed bytes,
    /// tag. Any other length is damage rather than a wrong password — no password would open it —
    /// and the two must be told apart before <see cref="TryOpen"/> is asked, because it answers
    /// both with the same refusal.
    /// </summary>
    private static bool WrapCouldHoldADataKey(byte[]? wrap) =>
        wrap is not null && wrap.Length == NonceLength + DataKeyLength + TagLength;

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
