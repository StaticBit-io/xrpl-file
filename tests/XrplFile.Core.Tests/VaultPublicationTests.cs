using System.Text;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// A vault-signed publication against the fake node: the same story the standalone suite tells
/// against a real one, at the speed a unit suite can afford to run on every change.
///
/// It is here rather than only there because the fake node now signs through the signer it is
/// given. Before that, no unit test drove <see cref="ITransactionSigner.SignAsync"/> through a
/// strategy at all, and a strategy that quietly stopped passing its signer along — or a signer
/// that kept a wallet alive across a lock — would have been caught by nothing short of Docker.
/// </summary>
public class VaultPublicationTests : IAsyncDisposable
{
    /// <summary>Argon2id at the smallest cost the implementation accepts; the KDF is not what is under test.</summary>
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");

    private const int NodeFeeDrops = 10;

    private static readonly SequenceStrategyOptions StrategyOptions = new() { FeeDrops = NodeFeeDrops };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-vault-publish-").FullName;

    private readonly XrplWallet _wallet = XrplWallet.Generate();

    private KeyVault? _vault;

    private SessionOptions Options() => new() { StateDirectory = Path.Combine(_directory, "state") };

    public async ValueTask DisposeAsync()
    {
        if (_vault is not null) await _vault.DisposeAsync();

        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(NodeFeeDrops, 1_000_000, 200_000) };
        source.Balances[_wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    /// <summary>An unlocked vault holding this test's wallet, and a signer over that entry.</summary>
    private async Task<(KeyVault Vault, VaultSigner Signer)> VaultBackedSignerAsync(CancellationToken cancellationToken)
    {
        KeyVault vault = new(
            Path.Combine(_directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);

        _vault = vault;
        await vault.CreateAsync(Password, cancellationToken);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "publisher", "testnet", _wallet.ClassicAddress, _wallet.Seed),
            cancellationToken);

        return (vault, await VaultSigner.CreateAsync(vault, reference, cancellationToken));
    }

    [Fact]
    public async Task A_publication_signed_from_the_vault_reaches_the_ledger()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, VaultSigner signer) = await VaultBackedSignerAsync(token);

        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, signer, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("v", 2500), "text/plain", "v.txt",
            signer.Account, "key-vault", "testnet", Options(), token);

        PublicationReport report = await session.RunAsync(null, token);

        Assert.Equal(3, report.ChunkCount);
        Assert.Equal(_wallet.ClassicAddress, report.Writer);

        // Every transaction that reached the fake node was signed through the vault: three chunks
        // and the manifest. Nothing the strategy submits bypasses the signer.
        Assert.Equal(sink.Submitted.Count, sink.Signatures);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_lock_between_two_chunks_stops_the_run_where_it_stands()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, VaultSigner signer) = await VaultBackedSignerAsync(token);

        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        // Between the first chunk and the second: the run has spent money and has not finished.
        LockingSigner interrupted = new(signer, vault, lockAfter: 1);
        await using SequenceStrategy strategy = new(sink, interrupted, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("v", 2500), "text/plain", "v.txt",
            signer.Account, "key-vault", "testnet", Options(), token);

        VaultException refused = await Assert.ThrowsAsync<VaultException>(() => session.RunAsync(null, token));

        Assert.Equal(VaultError.Locked, refused.Error);
        Assert.False(vault.IsUnlocked);
        Assert.Equal(2, interrupted.Signatures);

        // One chunk in the ledger, and the refused signature left nothing behind it: the fake
        // records a submission only once its signature is taken, exactly as a node only ever sees
        // a transaction that was signed.
        Assert.Single(sink.Submitted);
        Assert.Equal(1, sink.Signatures);
        Assert.Equal("chunks", session.State.Phase);
    }
}
