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
    private volatile string[] _damagedReferences = [];
    private volatile bool _disposed;

    /// <summary>
    /// Whether the record this vault is holding open failed its own integrity code at the unlock
    /// that opened it, and the caller has not yet said it has seen that. While it is set, every
    /// write is refused — see <see cref="RequireTheDamageAcknowledged"/> for why the first write
    /// would otherwise be the end of the only evidence there is.
    /// </summary>
    private volatile bool _recordIntegrityFailed;

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

    /// <summary>
    /// Which vault the store is being asked about (4.2). It is this vault's full path: the store
    /// holds one secret per identity, so two vaults on one machine cannot see, seal under, or
    /// delete each other's — and the setting being turned off on one cannot quietly take it from
    /// the other.
    /// </summary>
    /// <remarks>
    /// The path is the identity because it needs no field in the record, and so no bump of the
    /// format version to introduce. What that costs is one case: a vault file moved or renamed
    /// asks for a secret nobody stored, so device unlock on that machine is gone and the user
    /// turns it back on. That is the same convenience a vault loses when it is copied to another
    /// machine, and it is the password wrap — never removable, never machine-bound — that carries
    /// a moved file.
    /// </remarks>
    private string DeviceSecretId => _path;

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
    /// Filled by <see cref="VerifyAndInstall"/>, which both <see cref="UnlockAsync"/> and
    /// <see cref="UnlockWithDeviceAsync"/> run once they hold a data key that opened the record: a
    /// reference is added here when its entry fails its own authentication tag during that sweep,
    /// rather than the sweep throwing over it — one unreadable key must not stop the others from
    /// being counted, or from opening. Cleared by <see cref="Forget"/> and replaced wholesale by
    /// the next unlock that gets far enough to look; a failed attempt on an already-open vault
    /// (<see cref="VaultUnlockResult.WrongPassword"/>) never reaches that point, so it leaves this
    /// list exactly as it was. A reveal whose entry fails its tag still throws
    /// <see cref="VaultError.EntryDamaged"/> independently of this list.
    /// <para>
    /// Wrapped rather than handed over: the field behind it is the vault's own idea of what it
    /// holds, and a caller who received the array itself could cast it back to <c>string[]</c> and
    /// rewrite that — with no write, and with the file left disagreeing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DamagedReferences => Array.AsReadOnly(_damagedReferences);

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
            RequireNotDisposed();

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
            RequireNotDisposed();

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

                VaultUnlockResult outcome = VerifyAndInstall(record, dataKey);
                dataKey = null;
                return outcome;
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
            RequireNotDisposed();

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
                deviceSecret = await DeviceSecrets.GetAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
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

                VaultUnlockResult outcome = VerifyAndInstall(record, dataKey);
                dataKey = null;
                return outcome;
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
                if (HoldsAddressAlready(dataKey, record, entry.Network, entry.Address))
                {
                    // Refuses rather than silently holding the same key twice — an import of a
                    // container or a bare seed the vault already has is a no-op the caller should
                    // be told about, not a second entry nobody asked for (design section 8).
                    throw new VaultException(
                        VaultError.DuplicateAddress,
                        $"An entry for address '{entry.Address}' on network '{entry.Network}' already exists.");
                }

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
    /// <remarks>
    /// Reveals under the same gate every other public method uses, so a concurrent idle-lock cannot
    /// land between the reveal and the seal (see the type's remarks on the gate). Sealing happens
    /// before the record is touched at all: a throw out of <see cref="ExportContainer.Protect"/>
    /// leaves <c>Exported</c> exactly as it was, satisfying the same "not before the container
    /// exists" rule <see cref="UpdateAsync"/> would otherwise let a caller violate by calling it
    /// ahead of a <see cref="ExportContainer.Protect"/> that never returns.
    /// </remarks>
    public async Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VaultRecord record = RequireUnlocked();
            byte[] dataKey = BorrowDataKey(out long generation);
            try
            {
                VaultEntry entry = ReadEntryOrThrow(dataKey, reference, FindEntry(record, reference));

                // Sealed before a single byte of the record is edited. If this throws, nothing
                // below ever runs, so the entry cannot end up claiming an export that never
                // happened — the one lie Exported cannot afford.
                byte[] container = ExportContainer.Protect(
                    [entry], containerPassword.Span, _parameters);

                byte[] previousSealedEntry = record.Entries[reference];
                VaultEntryInfo exported = entry.Info with { Exported = true };
                record.Entries[reference] = SealEntry(dataKey, reference, entry with { Info = exported });

                try
                {
                    await PersistAsync(record, dataKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The container above is already sealed and correct — the only thing that
                    // failed is recording that it happened, so the cache is rolled back to agree
                    // with the file (Exported stays false, matching what is actually on disk).
                    // That rollback happens whatever the failure was, exactly as it does in
                    // StoreAsync, UpdateAsync and DeleteAsync: a cache left claiming an export the
                    // file does not record is committed by the next write of any kind, and the
                    // vault then permanently says a key was backed up that never left it — which
                    // silences the very warning a design with no recovery phrase depends on.
                    record.Entries[reference] = previousSealedEntry;

                    // A cancellation is then let through unchanged (this class never swallows one,
                    // see EnableDeviceUnlockAsync for the same rule): the caller asked to stop, and
                    // never receives the container, so nothing was exported anywhere.
                    if (ex is OperationCanceledException)
                    {
                        throw;
                    }

                    // Every other failure here is storage refusing the write, and the container is
                    // handed back anyway rather than discarding the export's actual deliverable. A
                    // caller who reads Exported afterwards is nagged to export this key again,
                    // which is the safe direction to be wrong in; throwing here instead would make
                    // the one guaranteed recovery path unreliable exactly when storage already is.
                    EnsureStillOpen(generation);
                    return container;
                }

                EnsureStillOpen(generation);
                return container;
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

                // Asked here, before a single field moves, because it is a question about the
                // record as the file holds it — and because the write below is what ends the only
                // chance to ask it. Resealing recomputes the code over whatever the record now
                // says, so a mismatch that is not read out now is gone for good, and with it the
                // only witness to an entry removed from the file or to a rollback (11).
                //
                // It does not refuse the change. A user whose password has leaked needs the new
                // password more than the evidence, and refusing over one damaged byte is the worse
                // failure of the two. Telling them is not the same as refusing them.
                bool integrityFailed = !MacMatches(record, dataKey);

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
                // never carried a device wrap gains none here: a secret stored under this vault's
                // identity is not consent either, because the file at a path can be replaced.
                DeviceRewrap deviceOutcome = DeviceRewrap.NothingToRewrap;
                if (deviceDekBeforeTheChange is not null)
                {
                    (deviceOutcome, byte[]? rewrapped) = await RewrapDeviceKeyAsync(
                            record, deviceDekBeforeTheChange, headerBeforeTheChange, dataKey, cancellationToken)
                        .ConfigureAwait(false);
                    record.DeviceDek = rewrapped;
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
                //
                // A failure here is carried in the result rather than thrown. The record above is
                // already committed: a throw would tell the caller the password was not changed,
                // which sends it back to a password the vault no longer has — and leaves the copy
                // that password does open lying there with nobody told about it.
                bool theOldCopyRemains = !DurableFile.TryDiscardBackup(_path);

                // Only a secret this vault has established is its own may be dropped. The store
                // said it holds nothing for this vault, so this asks it to stay that way and
                // destroys no key material — the case where a secret is present but opens no wrap
                // of ours deliberately leaves it where it is (see RewrapDeviceKeyAsync).
                if (deviceOutcome == DeviceRewrap.ThisMachineHoldsNoSecret)
                {
                    await ForgetTheOrphanedDeviceSecretAsync(cancellationToken).ConfigureAwait(false);
                }

                bool deviceUnlockDropped = deviceOutcome is DeviceRewrap.ThisMachineHoldsNoSecret
                    or DeviceRewrap.TheSecretWasEnrolledForAnotherFile;

                // Assembled rather than chosen from a list of named combinations: the three facts
                // are independent, and a list would have to name eight cases to leave none of them
                // unsayable. Changed is set here and nowhere else, so it means exactly "the record
                // below reached the disk".
                VaultPasswordChange outcome = VaultPasswordChange.Changed;
                if (deviceUnlockDropped)
                {
                    outcome |= VaultPasswordChange.DeviceUnlockDropped;
                }

                if (theOldCopyRemains)
                {
                    outcome |= VaultPasswordChange.TheOldCopyRemains;
                }

                if (integrityFailed)
                {
                    outcome |= VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch;
                }

                return outcome;
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
    public async Task AcknowledgeDamageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Requires an open vault, so that acknowledging is always about the damage this
            // session was actually told of. Nothing is written here: the file keeps the mismatch
            // until a write genuinely needs to happen, so a vault locked and opened again reports
            // VaultDegraded once more and the user is told again rather than quietly forgotten.
            RequireUnlocked();
            _recordIntegrityFailed = false;
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
                    deviceSecret = await DeviceSecrets.GetAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false)
                        ?? await DeviceSecrets.CreateAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
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
            bool theSecretIsThisVaultsOwn = false;
            try
            {
                if (record.DeviceDek is not null)
                {
                    // Asked before the wrap is cleared, because the wrap is the only proof there
                    // is that the secret stored under this path was enrolled for the file that is
                    // here now. A vault that never had a wrap, or one copied over a path where
                    // something else enrolled a secret, reaches the deletion below with this still
                    // false — and whatever enrolled that secret keeps its setting.
                    theSecretIsThisVaultsOwn = await ThisMachinesSecretOpensAsync(
                        record, record.DeviceDek, cancellationToken).ConfigureAwait(false);

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

            // The vault owns the record above; the store owns its own item here. Asked for only
            // once this vault's own wrap has opened under the machine's secret, and after the
            // record write lands, so that a store failure above leaves the platform secret in
            // place rather than dropping it out from under a wrap that a rolled-back write just
            // put back.
            if (theSecretIsThisVaultsOwn)
            {
                await DeviceSecrets.DeleteAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether the secret stored for this vault is the one <paramref name="deviceDek"/> was sealed
    /// under — the only evidence available that it was enrolled for the file that is here now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store answers per vault (4.2), so the secret it returns was at least stored under this
    /// vault's identity. That is not the same as belonging to this record: a file is replaceable
    /// at a path, and a vault copied over another leaves a secret enrolled for the file that used
    /// to be there. Asking the wrap is what tells the two apart.
    /// </para>
    /// <para>
    /// A store that cannot answer counts as "not proved", and so leaves the secret alone: the two
    /// outcomes of being wrong are not symmetrical. A secret left behind that opens nothing is
    /// litter; a secret deleted that was not ours costs whatever enrolled it its device unlock,
    /// and only the user can put that back.
    /// </para>
    /// </remarks>
    private async Task<bool> ThisMachinesSecretOpensAsync(
        VaultRecord record, byte[] deviceDek, CancellationToken cancellationToken)
    {
        byte[]? deviceSecret;
        try
        {
            deviceSecret = await DeviceSecrets.GetAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (deviceSecret is null)
        {
            return false;
        }

        byte[] devicePasswordKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, deviceSecret, DataKeyLength, salt: null, info: DeviceInfo);
        try
        {
            if (!TryOpen(devicePasswordKey, deviceDek, VaultRecordCodec.Header(record), out byte[]? enrolled))
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(enrolled);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(devicePasswordKey);
            CryptographicOperations.ZeroMemory(deviceSecret);
        }
    }

    /// <summary>
    /// Re-wraps the data key for "remember on this device" under a header that just changed
    /// because the password did. Hands back no wrap — dropping the device wrap rather than
    /// leaving one bound to a header nothing will ever present again — when this machine genuinely
    /// holds no secret, or holds one that is not the secret
    /// <paramref name="deviceDekBeforeTheChange"/> was sealed under, and says which of the two it
    /// was, because only the first of them may cost the machine its stored secret.
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
    /// proof that the secret stored for this vault is the one device unlock was enabled with.
    /// Without that step, a secret left over from a different file at this same path would be
    /// re-sealed against, handing out passwordless unlock nobody enabled: the store answers per
    /// vault (4.2), but a path outlives the file that was at it.
    /// </para>
    /// </remarks>
    private async Task<(DeviceRewrap Outcome, byte[]? Wrap)> RewrapDeviceKeyAsync(
        VaultRecord record,
        byte[] deviceDekBeforeTheChange,
        byte[] headerBeforeTheChange,
        byte[] dataKey,
        CancellationToken cancellationToken)
    {
        byte[]? deviceSecret;
        try
        {
            deviceSecret = await DeviceSecrets.GetAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
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
            return (DeviceRewrap.ThisMachineHoldsNoSecret, null);
        }

        byte[] devicePasswordKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, deviceSecret, DataKeyLength, salt: null, info: DeviceInfo);
        try
        {
            if (!TryOpen(devicePasswordKey, deviceDekBeforeTheChange, headerBeforeTheChange, out byte[]? enrolled))
            {
                // Reported separately from absence, and the difference matters to the store: the
                // secret stored here is demonstrably not the one this wrap was sealed under, so it
                // was enrolled for something else that may still be using it. The wrap goes; it stays.
                return (DeviceRewrap.TheSecretWasEnrolledForAnotherFile, null);
            }

            CryptographicOperations.ZeroMemory(enrolled);
            return (DeviceRewrap.Resealed, Seal(devicePasswordKey, dataKey, VaultRecordCodec.Header(record)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(devicePasswordKey);
            CryptographicOperations.ZeroMemory(deviceSecret);
        }
    }

    /// <summary>
    /// What became of the device wrap across a password change, and — the part a caller cannot
    /// infer from the wrap alone — what that says about the secret on this machine.
    /// </summary>
    private enum DeviceRewrap
    {
        /// <summary>The record carried no device wrap, so there was nothing to re-seal or drop.</summary>
        NothingToRewrap,

        /// <summary>Re-sealed under the new header. The setting survives the change.</summary>
        Resealed,

        /// <summary>
        /// The store holds nothing. The wrap is dropped rather than left bound to a header nobody
        /// will present again, and the store may be asked to stay empty — there is no key material
        /// to destroy in doing so.
        /// </summary>
        ThisMachineHoldsNoSecret,

        /// <summary>
        /// A secret is stored for this vault's identity and the wrap does not open under it, so it
        /// was enrolled for a different file at this same path. The wrap is dropped and the secret
        /// is left strictly alone: whatever enrolled it may still be using it.
        /// </summary>
        TheSecretWasEnrolledForAnotherFile,
    }

    /// <summary>
    /// Asks the store to drop a secret the vault has just stopped pointing at, the way
    /// <see cref="DisableDeviceUnlockAsync"/> does: the vault owns the record, the store owns its
    /// item, and neither should leave the other holding something dead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on exactly one outcome — <see cref="DeviceRewrap.ThisMachineHoldsNoSecret"/>, where
    /// the store has just said it holds nothing for this vault, so this asks it to stay that way
    /// and no key material is destroyed by asking. It is deliberately not called when a secret is
    /// present but does not open this record's wrap: it was enrolled for a different file at this
    /// path, may still be in use, and the failure to open it is the proof that it is not ours.
    /// </para>
    /// <para>
    /// A failure here is swallowed on purpose, and it is the one place in this type where that is
    /// the right answer. The password change is already committed to disk; what is left behind is
    /// key material that opens nothing, while discarding the outcome of a committed change would
    /// leave the caller unable to say what happened — or, worse, retrying with a password that is
    /// no longer the vault's.
    /// </para>
    /// </remarks>
    private async Task ForgetTheOrphanedDeviceSecretAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DeviceSecrets.DeleteAsync(DeviceSecretId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing to do and nothing to say: see the remarks above.
        }
    }

    /// <summary>Locks the vault for good. Disposing twice is a no-op.</summary>
    /// <remarks>
    /// <para>
    /// Disposal takes the gate like every other operation, so it cannot land in the middle of one
    /// — the same rule, and for the same reason, as <see cref="LockAsync"/>. The gate itself is
    /// deliberately not disposed: <see cref="SemaphoreSlim.Dispose()"/> only releases a wait
    /// handle this class never asks for, and disposing it would turn the typed refusal an
    /// operation arriving afterwards gets today into an <see cref="ObjectDisposedException"/>
    /// thrown out of the gate it happened to race.
    /// </para>
    /// <para>
    /// "For good" is enforced rather than described: <see cref="CreateAsync"/>,
    /// <see cref="UnlockAsync"/> and <see cref="UnlockWithDeviceAsync"/> refuse a disposed vault,
    /// so nothing can put a data key back into one. <see cref="Forget"/> runs on every disposal
    /// rather than only the first, so that the second call cannot be the one that returns with key
    /// material still resident — the failure a first-one-wins early return would make permanent,
    /// since disposal is also what takes the idle lock away.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
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

    /// <summary>
    /// Refuses to hand a disposed vault a data key. Disposal is documented as locking the vault
    /// for good, and it is what sign-out calls — together with disposing the idle lock, so a vault
    /// that opened again afterwards would hold a resident key with no timer left to take it away.
    /// </summary>
    private void RequireNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Refuses to write over a record whose integrity code did not match when it was opened, until
    /// <see cref="AcknowledgeDamageAsync"/> has said the damage has been seen.
    /// </summary>
    /// <remarks>
    /// Every write reseals, and resealing computes a fresh <c>mac</c> over the record as it now
    /// stands — including whatever was done to it. The mismatch is the only witness there is to an
    /// entry removed from the file or to bytes edited under the code that covers them: a per-entry
    /// tag says nothing about an entry that is no longer there, and the design concedes it cannot
    /// see a whole-file rollback at all (11). Writing over it is therefore not merely losing
    /// evidence, it is the one action that destroys it permanently, so it is the user's to take
    /// and not the application's to take silently. Reading is untouched: listing, revealing and
    /// exporting a degraded vault need no acknowledgement, because rescuing the keys is what a
    /// degraded vault is for.
    /// </remarks>
    private void RequireTheDamageAcknowledged()
    {
        if (_recordIntegrityFailed)
        {
            throw new VaultException(
                VaultError.Degraded,
                "The vault's integrity code did not match its contents when it was opened. Writing would " +
                "recompute it over the damaged record, so the damage must be acknowledged first.");
        }
    }

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
        _damagedReferences = [];
        _recordIntegrityFailed = false;
        _generation++;
    }

    // ---- the record ----------------------------------------------------------------------------

    private async Task PersistAsync(VaultRecord record, byte[] dataKey, CancellationToken cancellationToken)
    {
        RequireTheDamageAcknowledged();
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

    /// <summary>
    /// Runs once a password or a device secret has produced a data key that opens the record:
    /// walks every entry's own authentication tag, checks the record's <c>mac</c> against one
    /// freshly computed over its current bytes, and installs the vault open either way. Nothing
    /// found from here on is ever reported as a wrong password — the unwrap has already succeeded,
    /// so the caller is, correctly, in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two checks catch different damage. A per-entry tag fails only for the entry whose own
    /// sealed bytes changed, and a failure there is collected rather than thrown: one unreadable
    /// key must not stop the sweep from counting the others. <see cref="VaultRecordCodec.Mac"/>
    /// covers the header and every entry's raw bytes together (see <see cref="Reseal"/>), so it
    /// also fails when an entry disappears from the file entirely, or the file is rolled back to
    /// an earlier version — damage no per-entry tag can see, because there is nothing left at that
    /// reference to open. A caller told "the vault is degraded" with an empty
    /// <see cref="DamagedReferences"/> still learns something true: the record's own bytes disagree
    /// with its own integrity code, even though nothing it could still name is unreadable.
    /// </para>
    /// <para>
    /// Cost: one AES-256-GCM decrypt per entry, its plaintext zeroed immediately, plus one
    /// HMAC-SHA256 over the whole canonical record. Both are linear in the vault's size and paid
    /// once per unlock — not per <see cref="RevealAsync"/> — which is why this runs here rather
    /// than being left to accumulate from callers drawing entries one at a time.
    /// </para>
    /// </remarks>
    private VaultUnlockResult VerifyAndInstall(VaultRecord record, byte[] dataKey)
    {
        List<string> damaged = [];
        foreach (KeyValuePair<string, byte[]> entry in record.Entries)
        {
            if (TryOpen(dataKey, entry.Value, Encoding.UTF8.GetBytes(entry.Key), out byte[]? plaintext))
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            else
            {
                damaged.Add(entry.Key);
            }
        }

        bool macMatches = MacMatches(record, dataKey);

        Install(record, dataKey);
        _damagedReferences = damaged.ToArray();

        // Set after Install, which clears it along with the rest of the previous session's state.
        // It stays set until the caller acknowledges the damage, and is what stops the next write
        // from recomputing the code over the record it disagrees with.
        _recordIntegrityFailed = !macMatches;

        return macMatches && damaged.Count == 0 ? VaultUnlockResult.Success : VaultUnlockResult.VaultDegraded;
    }

    /// <summary>
    /// Whether the record's stored <c>mac</c> matches one freshly computed over its current
    /// canonical bytes. Needs the data key, and so the integrity key derived from it (see
    /// <see cref="Reseal"/>) — which is exactly why this can run only after a wrap has already
    /// opened, and never ahead of it.
    /// </summary>
    private static bool MacMatches(VaultRecord record, byte[] dataKey)
    {
        byte[] integrityKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, dataKey, IntegrityKeyLength, salt: null, info: IntegrityInfo);
        try
        {
            byte[] expected = VaultRecordCodec.Mac(integrityKey, record);
            return CryptographicOperations.FixedTimeEquals(expected, record.Mac);
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

    /// <summary>
    /// Whether some entry already carries this exact (network, address) pair. Checked by
    /// <see cref="StoreAsync"/> before a new reference is even assigned, so that a bare seed or a
    /// container reimported for a key the vault already holds is refused rather than filed under a
    /// second reference nobody asked for. A damaged existing entry cannot be compared and is
    /// skipped — the same tolerance <see cref="ListAsync"/> and <see cref="VerifyAndInstall"/> give
    /// damage elsewhere: one unreadable entry must not block every future store.
    /// </summary>
    private static bool HoldsAddressAlready(byte[] dataKey, VaultRecord record, string network, string address)
    {
        foreach (KeyValuePair<string, byte[]> existing in record.Entries)
        {
            if (TryReadEntry(dataKey, existing.Key, existing.Value, out VaultEntry? read))
            {
                bool matches = string.Equals(read.Info.Network, network, StringComparison.Ordinal) &&
                               string.Equals(read.Info.Address, address, StringComparison.Ordinal);
                if (matches)
                {
                    return true;
                }
            }
        }

        return false;
    }

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
