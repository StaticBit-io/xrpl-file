using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// What a model test needs and should not have to restate: a temporary vault file, a vault built
/// over it, and an in-memory device store. Mirrors
/// <c>tests/XrplFile.Keys.Tests/VaultTestHarness.cs</c> for the surface this project needs; the
/// two live in separate test projects and are not shared code, so a change to one does not have
/// to touch the other.
/// </summary>
public abstract class VaultHarness : IAsyncDisposable
{
    /// <summary>
    /// Argon2id at the smallest cost the implementation accepts, matching
    /// <c>VaultTestHarness.FastParameters</c>: a suite that unlocks in most of its tests cannot
    /// afford the real cost, and the real KDF is already pinned under test elsewhere.
    /// </summary>
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-app-vault-").FullName;
    private readonly List<KeyVault> _vaults = [];

    /// <summary>The file a vault built with no explicit path reads and writes.</summary>
    protected string VaultPath => Path.Combine(_directory, "vault.json");

    /// <summary>The device store every vault this harness builds is given.</summary>
    protected InMemoryDeviceSecretStore Devices { get; } = new();

    /// <summary>
    /// A vault over <paramref name="path"/>, or over <see cref="VaultPath"/> when
    /// <paramref name="path"/> is <see langword="null"/>.
    /// </summary>
    protected KeyVault NewVault(string? path = null)
    {
        KeyVault vault = new(path ?? VaultPath, new Argon2idKdf(), Devices, FastParameters);
        _vaults.Add(vault);
        return vault;
    }

    /// <summary>A password as the vault takes one: the UTF-8 bytes of what a user typed.</summary>
    protected static ReadOnlyMemory<byte> Password(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// An entry whose every field is derived from <paramref name="label"/>, so a test can tell two
    /// apart. Defaults to <see cref="VaultEntryKind.Imported"/>, not
    /// <see cref="VaultEntryKind.Generated"/>: a model test is more often about an account the user
    /// already had than one this application minted for them.
    /// </summary>
    protected static NewVaultEntry Entry(string label, VaultEntryKind kind = VaultEntryKind.Imported) =>
        new(kind, label, "standalone", "rAddressOf" + label, "sSecretOf" + label);

    /// <summary>
    /// Flips one bit in the ciphertext of one entry, past the nonce, so what is damaged is the
    /// entry's contents rather than the parameters it was sealed with. Mirrors the effect of
    /// <c>VaultTestHarness.FlipOneByteOfEntry</c> in <c>tests/XrplFile.Keys.Tests</c>, but reaches
    /// the bytes through the file's JSON rather than through <c>VaultRecordCodec</c>: that type is
    /// <c>internal</c> to <c>XrplFile.Keys</c> and visible only to <c>XrplFile.Keys.Tests</c> — not
    /// to this project — so the same nonce-relative offset is applied at the wire encoding instead.
    /// </summary>
    protected void FlipOneByteOfEntry(string reference)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllBytes(VaultPath))
            ?? throw new InvalidOperationException("The vault file to damage is not JSON.");
        JsonObject entries = root["entries"]?.AsObject()
            ?? throw new InvalidOperationException("The vault file to damage carries no entries.");
        string base64 = entries[reference]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The vault file has no entry '{reference}' to damage.");

        byte[] ciphertext = Convert.FromBase64String(base64);
        const int pastTheNonce = 12;
        ciphertext[pastTheNonce] ^= 0x01;
        entries[reference] = Convert.ToBase64String(ciphertext);

        File.WriteAllBytes(VaultPath, Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
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
/// A device secret store that keeps its secrets in a dictionary — one machine, one secret per
/// vault. Mirrors <c>VaultTestHarness.InMemoryDeviceSecretStore</c>: it lets this suite exercise
/// "remember on this device" with no platform secret store, and therefore with no operating
/// system having any say in whether the suite passes.
/// </summary>
public sealed class InMemoryDeviceSecretStore : IDeviceSecretStore
{
    private readonly Dictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<byte[]?> GetAsync(string vaultId, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.TryGetValue(vaultId, out byte[]? secret) ? (byte[])secret.Clone() : null);

    /// <inheritdoc/>
    public Task<byte[]> CreateAsync(string vaultId, CancellationToken cancellationToken)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        _secrets[vaultId] = secret;
        return Task.FromResult((byte[])secret.Clone());
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string vaultId, CancellationToken cancellationToken)
    {
        _secrets.Remove(vaultId);
        return Task.CompletedTask;
    }
}
