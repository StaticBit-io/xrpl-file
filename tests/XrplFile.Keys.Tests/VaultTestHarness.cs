using System.Security.Cryptography;
using System.Text;

namespace XrplFile.Keys.Tests;

/// <summary>
/// What a vault test needs and should not have to restate: a temporary vault file, a vault built
/// over it, an in-memory device store, and the file-poking helpers that stand in for damage.
/// Test classes derive from it.
/// </summary>
/// <remarks>
/// The helpers reach the record through <see cref="VaultRecordCodec"/> rather than through the
/// file's bytes, so a test that damages one entry damages exactly that entry and leaves a record
/// the vault can still parse — which is what makes "this entry is damaged and the others are not"
/// a statement the suite can actually make.
/// </remarks>
public abstract class VaultTestHarness : IAsyncDisposable
{
    /// <summary>
    /// Argon2id at the smallest cost the implementation accepts. The real triple is 256 MiB over
    /// three passes and takes about half a second, which a suite that unlocks in most of its tests
    /// cannot afford; the cost is a recorded parameter rather than a constant precisely so it can
    /// be chosen here. What is exercised is still the real KDF — its output is pinned against RFC
    /// 9106 by <c>Argon2idKdfTests</c>, which is where cost belongs under test, not here.
    /// </summary>
    internal static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-vault-").FullName;
    private readonly List<KeyVault> _vaults = [];

    /// <summary>The file every vault this harness builds reads and writes.</summary>
    protected string VaultPath => Path.Combine(_directory, "vault.json");

    /// <summary>
    /// The copy a durable write leaves beside the vault. It is what makes the previous contents
    /// survivable, and — after a password change — what must not be left holding a record the old
    /// password still opens.
    /// </summary>
    protected string VaultBackupPath => VaultPath + ".bak";

    /// <summary>The device store every vault this harness builds is given.</summary>
    protected InMemoryDeviceSecretStore Devices { get; } = new();

    /// <summary>
    /// A cost high enough that a derivation is unmistakably still running when another call
    /// arrives — the width a test needs to say something about what happens during one.
    /// </summary>
    internal static readonly KdfParameters SlowEnoughToRaceParameters = new(MemoryKib: 65536, Iterations: 2, Parallelism: 1);

    /// <summary>A vault over <see cref="VaultPath"/>. Call it twice for two instances over one file.</summary>
    protected KeyVault NewVault(KdfParameters? parameters = null)
    {
        KeyVault vault = new(VaultPath, new Argon2idKdf(), Devices, parameters ?? FastParameters);
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>A vault over <see cref="VaultPath"/> built on a KDF of the caller's choosing.</summary>
    internal KeyVault NewVault(IPasswordKdf kdf, KdfParameters? parameters = null)
    {
        KeyVault vault = new(VaultPath, kdf, Devices, parameters ?? FastParameters);
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>
    /// A vault over <see cref="VaultPath"/> built on a device secret store of the caller's
    /// choosing, rather than the harness's shared <see cref="Devices"/> — what a test needs to put
    /// two vaults over the same file on two different "machines".
    /// </summary>
    protected KeyVault NewVault(IDeviceSecretStore devices, KdfParameters? parameters = null)
    {
        KeyVault vault = new(VaultPath, new Argon2idKdf(), devices, parameters ?? FastParameters);
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>
    /// A vault whose writes — and, when asked, whose key material — come from the caller. These are
    /// the seams a test needs to fail a write and to look at the array the vault was handed.
    /// </summary>
    internal KeyVault NewVault(VaultFileWriter write, VaultRandomBytes? random = null, KdfParameters? parameters = null)
    {
        KeyVault vault = new(VaultPath, new Argon2idKdf(), Devices, parameters ?? FastParameters, write, random);
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>A password as the vault takes one: the UTF-8 bytes of what a user typed.</summary>
    protected static ReadOnlyMemory<byte> Password(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>An entry whose every field is derived from <paramref name="label"/>, so a test can tell two apart.</summary>
    protected static NewVaultEntry Entry(
        string label,
        VaultEntryKind kind = VaultEntryKind.Generated,
        string network = "standalone",
        string? address = null,
        string? secret = null) =>
        new(kind, label, network, address ?? "rAddressOf" + label, secret ?? "sSecretOf" + label);

    /// <summary>The sealed bytes of one entry, straight out of the file.</summary>
    protected byte[] EntryCiphertextFromFile(string reference) => ReadRecordFromFile().Entries[reference];

    /// <summary>
    /// Flips one bit in the ciphertext of one entry — past the nonce, so what is damaged is the
    /// entry's contents rather than the parameters it was sealed with.
    /// </summary>
    protected void FlipOneByteOfEntry(string reference) =>
        MutateRecordInFile(record =>
        {
            byte[] entry = record.Entries[reference];
            const int pastTheNonce = 12;
            entry[pastTheNonce] ^= 0x01;
        });

    /// <summary>
    /// Swaps two entries' sealed bytes. Each stays authentic; neither belongs where it now sits,
    /// which is the damage a per-entry tag exists to catch and a whole-file one cannot.
    /// </summary>
    protected void SwapEntriesInFile(string first, string second) =>
        MutateRecordInFile(record =>
        {
            (record.Entries[first], record.Entries[second]) = (record.Entries[second], record.Entries[first]);
        });

    /// <summary>
    /// Copies the vault file into a directory of its own and returns the copy's path — a vault
    /// carried to another machine, as far as anything but the device secret can tell.
    /// </summary>
    protected string CopyVaultFileToAnotherDirectory()
    {
        string elsewhere = Directory.CreateDirectory(Path.Combine(_directory, "elsewhere")).FullName;
        string copy = Path.Combine(elsewhere, "vault.json");
        File.Copy(VaultPath, copy, overwrite: true);
        return copy;
    }

    /// <summary>The record as it currently sits on disk.</summary>
    internal VaultRecord ReadRecordFromFile() =>
        VaultRecordCodec.Parse(File.ReadAllBytes(VaultPath));

    /// <summary>Writes a record over the file, with no integrity code recomputed: that is the caller's business.</summary>
    internal void WriteRecordToFile(VaultRecord record) =>
        File.WriteAllBytes(VaultPath, VaultRecordCodec.Serialise(record));

    /// <summary>Reads the record, lets <paramref name="mutate"/> change it, and writes it back.</summary>
    internal void MutateRecordInFile(Action<VaultRecord> mutate)
    {
        VaultRecord record = ReadRecordFromFile();
        mutate(record);
        WriteRecordToFile(record);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (KeyVault vault in _vaults)
        {
            await vault.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The disk, with a switch. It lives here rather than being restated in each test class: three of
/// them need to fail a write, and three copies of one double are three places for the failure they
/// stand for to drift apart.
/// </summary>
internal sealed class FailableWrites
{
    internal bool Fail { get; set; }

    internal Task WriteAsync(string path, byte[] contents, CancellationToken cancellationToken) =>
        Fail
            ? Task.FromException(new VaultException(VaultError.StorageFailed, "The disk refused the write."))
            : DurableFile.WriteAsync(path, contents, cancellationToken);
}

/// <summary>A device secret store that cannot even answer whether it holds a secret.</summary>
public sealed class FailingDeviceSecretStore : IDeviceSecretStore
{
    /// <inheritdoc/>
    public Task<byte[]?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromException<byte[]?>(new IOException("The platform secret store is unavailable."));

    /// <inheritdoc/>
    public Task<byte[]> CreateAsync(CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(new IOException("The platform secret store is unavailable."));

    /// <inheritdoc/>
    public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A device secret store that keeps its secret in a field. It is what lets the unit suite exercise
/// "remember on this device" without a platform secret store — and therefore without the operating
/// system having any say in whether the suite passes.
/// </summary>
public sealed class InMemoryDeviceSecretStore : IDeviceSecretStore
{
    private byte[]? _secret;

    /// <summary>How many times a secret has been created. A test can assert a device was enrolled once.</summary>
    public int Creations { get; private set; }

    /// <summary>
    /// How many times the vault asked for the secret to be dropped. A test can assert that a vault
    /// which stopped pointing at the machine's secret also stopped leaving it behind.
    /// </summary>
    public int Deletions { get; private set; }

    /// <inheritdoc/>
    public Task<byte[]?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_secret is null ? null : (byte[])_secret.Clone());

    /// <inheritdoc/>
    public Task<byte[]> CreateAsync(CancellationToken cancellationToken)
    {
        _secret = RandomNumberGenerator.GetBytes(32);
        Creations++;
        return Task.FromResult((byte[])_secret.Clone());
    }

    /// <inheritdoc/>
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        _secret = null;
        Deletions++;
        return Task.CompletedTask;
    }
}
