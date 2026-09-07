using System.Text.Json;
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

        // One test proves a failed save keeps the report by putting a file where the directory was.
        if (File.Exists(_stateDirectory)) File.Delete(_stateDirectory);

        GC.SuppressFinalize(this);
    }

    private SessionOptions Options(long? budget = null) =>
        new() { StateDirectory = _stateDirectory, BudgetDrops = budget, TicketsPerBatch = 100 };

    /// <summary>What the fake node charges. The strategies are built from it, as a caller must.</summary>
    private const int NodeFeeDrops = 10;

    private static readonly SequenceStrategyOptions StrategyOptions = new() { FeeDrops = NodeFeeDrops };

    private static FakeLedgerSource Node(long balanceDrops = 500_000_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(NodeFeeDrops, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = balanceDrops;
        return source;
    }

    [Fact]
    public async Task A_published_file_comes_back_with_a_link_and_a_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("testnet", report.Link.Network);
        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkHashes.Count);
        Assert.Empty(report.ListHashes);

        // Four memos and the one mint the approved estimate priced.
        Assert.Equal(5, report.Transactions);
        Assert.Equal(5 * NodeFeeDrops, report.FeeDropsEstimated);
        Assert.Null(report.StateSaveFailure);
        Assert.True(report.FinishedAt >= report.StartedAt);
    }

    [Fact]
    public async Task A_run_that_costs_more_than_the_budget_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        UnfinishedRun only = Assert.Single(PublicationSession.Unfinished(_stateDirectory));

        Assert.Null(only.Unreadable);
        Assert.Equal("key-1", only.State!.KeyReference);
        Assert.NotEqual("done", only.State.Phase);
    }

    [Fact]
    public async Task A_finished_run_is_no_longer_unfinished()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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

    [Fact]
    public async Task The_report_counts_every_transaction_the_estimate_priced()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            ledger, Wallet.ClassicAddress, fileBytes: 2500, Options(), TestContext.Current.CancellationToken);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        // The estimate the run was approved against counts the mints a ticketed strategy pays for,
        // not only the memos. A report that counts fewer states less than the account was debited.
        Assert.Equal(1, estimate.Mints);
        Assert.Equal(estimate.Transactions, report.Transactions);
    }

    [Fact]
    public async Task The_state_file_follows_the_run_through_its_stages()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        List<(string Stage, string Phase)> seen = [];
        SynchronousProgress<PublishProgress> progress = new(report =>
            seen.Add((report.Stage, PublicationState.Load(session.StatePath).Phase)));

        await session.RunAsync(progress, TestContext.Current.CancellationToken);

        // What is on disk while the run is in it, not only after: a crash between two stages has
        // to leave behind which stage it died in.
        Assert.Contains(("read-back", "read-back"), seen);
        Assert.Contains(("manifest", "manifest"), seen);
        Assert.Equal("done", PublicationState.Load(session.StatePath).Phase);
    }

    [Fact]
    public async Task Two_runs_started_in_one_ledger_keep_their_own_state_files()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy first = new(sink, Wallet, StrategyOptions);
        await using SequenceStrategy second = new(sink, Wallet, StrategyOptions);

        PublicationSession one = await PublicationSession.StartAsync(
            first, ledger, VectorStream.Bytes("a", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationSession two = await PublicationSession.StartAsync(
            second, ledger, VectorStream.Bytes("b", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-2", "testnet", Options(), TestContext.Current.CancellationToken);

        Assert.NotEqual(one.StatePath, two.StatePath);
        Assert.True(File.Exists(one.StatePath));
        Assert.True(File.Exists(two.StatePath));
        Assert.Equal(2, PublicationSession.Unfinished(_stateDirectory).Count);
    }

    [Fact]
    public async Task A_session_that_has_already_run_refuses_to_run_again()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await session.RunAsync(null, TestContext.Current.CancellationToken);
        int paidFor = sink.Submitted.Count;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => session.RunAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SessionAlreadyRun, error.Error);
        Assert.Equal(paidFor, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_state_file_that_cannot_be_written_does_not_cost_the_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        // A file standing where the state directory was: every later save fails, on any platform.
        Directory.Delete(_stateDirectory, recursive: true);
        await File.WriteAllTextAsync(_stateDirectory, "not a directory", TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkHashes.Count);

        // The save failed and said so, without taking the report down with it.
        Assert.NotNull(report.StateSaveFailure);
    }

    [Fact]
    public async Task The_state_file_carries_exactly_the_fields_it_is_documented_to_carry()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(session.StatePath, TestContext.Current.CancellationToken));

        string[] properties = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        // Pinned on purpose: a field added here is a field written to disk, and one added without
        // a thought for what a resume does with it should fail a test rather than leak quietly.
        Assert.Equal(
            [
                "Version", "Network", "Writer", "KeyReference", "Type", "Name",
                "Size", "Chunk", "Root", "StartedInLedger", "Phase", "ChunkHashes",
            ],
            properties);
    }

    [Fact]
    public async Task A_state_file_a_newer_version_wrote_is_listed_rather_than_hidden()
    {
        Directory.CreateDirectory(_stateDirectory);
        string path = Path.Combine(_stateDirectory, "9000-rSomebody-0a1b2c3d.json");

        // A run written by a version this one does not read. It may hold live tickets and an
        // unspent reserve; dropping it from the list is dropping the only record of that money.
        await File.WriteAllTextAsync(
            path,
            """{"Version":2,"Network":"testnet","Writer":"rSomebody","KeyReference":"key-9","Phase":"chunks"}""",
            TestContext.Current.CancellationToken);

        UnfinishedRun only = Assert.Single(PublicationSession.Unfinished(_stateDirectory));

        Assert.Equal(path, only.Path);
        Assert.Null(only.State);
        Assert.NotNull(only.Unreadable);
    }

    [Fact]
    public async Task A_strategy_that_charges_another_fee_never_starts_the_run()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        // The node prices a transaction at NodeFeeDrops; this strategy would pay one more. The
        // estimate that approves the run and the run itself would then be two different runs.
        await using SequenceStrategy strategy = new(sink, Wallet, new SequenceStrategyOptions { FeeDrops = NodeFeeDrops + 1 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.FeeMismatch, error.Error);
        Assert.Empty(sink.Submitted);
        Assert.False(Directory.Exists(_stateDirectory));
    }

    [Fact]
    public async Task A_writer_that_could_name_a_path_never_becomes_a_file_name()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            "../../etc/passwd", "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(_stateDirectory));
    }

    [Fact]
    public async Task A_resumed_run_does_not_re_send_the_chunks_the_ledger_already_holds()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        // A first run that dies after its chunks: the strategy refuses everything past the third
        // submission, which is where the manifest would have gone.
        sink.Refusals[3] = "tefBAD_AUTH";
        await using (SequenceStrategy dying = new(sink, Wallet, StrategyOptions))
        {
            PublicationSession first = await PublicationSession.StartAsync(
                dying, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
                Options(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, TestContext.Current.CancellationToken));
        }

        int submittedByTheFirstRun = sink.Submitted.Count;
        sink.Refusals.Clear();

        // The state file is all the second run is given, besides the file itself.
        string statePath = Directory.EnumerateFiles(_stateDirectory, "*.json").Single();

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, statePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Three chunks landed in the first run and are not sent again; only the manifest is.
        Assert.Equal(submittedByTheFirstRun + 1, sink.Submitted.Count);
        Assert.Equal(3, report.ChunkHashes.Count);
    }

    [Fact]
    public async Task A_resumed_run_reads_the_ledger_rather_than_believing_the_state_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // The chunks reach the ledger, and then the process dies before anything is written down:
        // the state file still says nothing has landed.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        await strategy.SubmitAllAsync(
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
            null, TestContext.Current.CancellationToken);

        int alreadyThere = sink.Submitted.Count;
        PublicationState onDisk = PublicationState.Load(first.StatePath);
        Assert.Empty(onDisk.ChunkHashes);

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Only the manifest was needed, because the sweep found the chunks the file did not know about.
        Assert.Equal(alreadyThere + 1, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_resume_with_a_different_file_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("one", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, VectorStream.Bytes("another", 2500), first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }

    [Fact]
    public async Task A_resume_of_a_finished_run_is_refused_rather_than_publishing_twice()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);
        await first.RunAsync(null, TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }
}
