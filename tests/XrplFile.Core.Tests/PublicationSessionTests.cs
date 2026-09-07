using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionTests : IDisposable
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private SessionOptions Options(long? budget = null) =>
        new() { StateDirectory = _stateDirectory, BudgetDrops = budget, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node(long balanceDrops = 500_000_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = balanceDrops;
        return source;
    }

    [Fact]
    public async Task A_published_file_comes_back_with_a_link_and_a_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("testnet", report.Link.Network);
        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkHashes.Count);
        Assert.Empty(report.ListHashes);
        Assert.Equal(4, report.Transactions);
        Assert.True(report.FinishedAt >= report.StartedAt);
    }

    [Fact]
    public async Task A_run_that_costs_more_than_the_budget_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(budget: 100), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task An_account_that_cannot_carry_the_run_never_starts_it()
    {
        FakeLedgerSource ledger = Node(balanceDrops: 1_000);
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.InsufficientBalance, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task The_state_file_exists_before_the_first_transaction_and_names_the_key_not_the_seed()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(session.StatePath));
        string json = await File.ReadAllTextAsync(session.StatePath, TestContext.Current.CancellationToken);

        Assert.Contains("key-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Wallet.Seed, json, StringComparison.Ordinal);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task The_state_records_the_phase_as_the_run_moves_through_it()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await session.RunAsync(null, TestContext.Current.CancellationToken);

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal("done", state.Phase);
        Assert.NotNull(state.Root);
    }

    [Fact]
    public async Task An_unfinished_run_is_findable_by_its_directory()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        IReadOnlyList<PublicationState> unfinished = PublicationSession.Unfinished(_stateDirectory);

        PublicationState only = Assert.Single(unfinished);
        Assert.Equal("key-1", only.KeyReference);
        Assert.NotEqual("done", only.Phase);
    }

    [Fact]
    public async Task A_finished_run_is_no_longer_unfinished()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(PublicationSession.Unfinished(_stateDirectory));
    }

    [Fact]
    public async Task The_estimate_can_be_asked_for_without_starting_anything()
    {
        FakeLedgerSource ledger = Node();

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            ledger, Wallet.ClassicAddress, fileBytes: 2500, Options(), TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.Chunks);
        Assert.False(Directory.Exists(_stateDirectory));
    }
}
