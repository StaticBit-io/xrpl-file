using System.Runtime.CompilerServices;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class ManifestAcceptanceTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-s22-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    [Fact]
    public async Task A_landed_manifest_the_reader_rejects_fails_the_run_records_it_and_names_the_link()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        TamperingSource tampering = new(ledger);
        PublicationSession session = await PublicationSession.StartAsync(
            strategy, tampering, VectorStream.Bytes("s22", 2_500), "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => session.RunAsync(null, Token));

        Assert.Equal(XrplFileError.ManifestRejected, error.Error);
        Assert.NotNull(error.Link);
        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.ManifestLandedRejected, state.ManifestStatus);
        Assert.Equal(error.Link!.TxHash, state.ManifestHash);
        Assert.NotEqual("done", state.Phase);
    }

    [Fact]
    public async Task A_resume_of_a_rejected_manifest_is_refused_without_reading_or_sending()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        byte[] file = VectorStream.Bytes("s22", 2_500);
        PublicationSession session = await PublicationSession.StartAsync(
            strategy, new TamperingSource(ledger), file, "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);
        await Assert.ThrowsAsync<XrplFileException>(() => session.RunAsync(null, Token));
        int submitted = sink.Submitted.Count;
        int reads = ledger.AccountTransactionsCalls;

        XrplFileException again = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(strategy, ledger, file, session.StatePath, Options, Token));

        Assert.Equal(XrplFileError.ManifestRejected, again.Error);
        Assert.Equal((submitted, reads), (sink.Submitted.Count, ledger.AccountTransactionsCalls));
    }

    [Fact]
    public async Task A_manifest_a_resume_recovers_is_checked_too_and_a_rejection_is_not_success()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        byte[] file = VectorStream.Bytes("s22-recovered", 2_500);
        PublicationSession run = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);
        await run.RunAsync(null, Token);
        PublicationState finished = PublicationState.Load(run.StatePath);
        (finished with { Phase = "manifest", ManifestHash = null }).Save(run.StatePath);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(strategy, new TamperingSource(ledger), file, run.StatePath, Options, Token));

        Assert.Equal(XrplFileError.ManifestRejected, error.Error);
        Assert.Equal(PublicationState.ManifestLandedRejected, PublicationState.Load(run.StatePath).ManifestStatus);
    }

    /// <summary>The node answers a tx lookup of a manifest with another file's description, as a reader would see a broken link.</summary>
    private sealed class TamperingSource(FakeLedgerSource inner) : ILedgerSource
    {
        public async Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
        {
            LedgerTransaction? tx = await inner.TransactionAsync(hash, cancellationToken);
            if (tx is null || !MemoLayout.IsManifest(tx.MemoTypeHex) || tx.MemoDataHex is null) return tx;

            Manifest real = Manifest.Decode(MemoLayout.FromHexUtf8(tx.MemoDataHex));
            return tx with { MemoDataHex = MemoLayout.Utf8Hex((real with { Name = "someone else's" }).Encode()) };
        }

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
            inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);
    }
}
