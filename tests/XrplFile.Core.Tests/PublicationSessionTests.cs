using System.Text.Json;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionTests : IDisposable
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("testnet", report.Link.Network);
        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkCount);
        Assert.Equal(0, report.ListCount);

        // Four memos. A sequence strategy mints nothing, and a fresh run counts what its own
        // strategy minted, not the estimate's two (spec I18).
        Assert.Equal(4, report.Transactions);
        Assert.Equal(4 * NodeFeeDrops, report.FeeDropsEstimated);
        Assert.Null(report.StateSaveFailure);
        Assert.True(report.FinishedAt >= report.StartedAt);
    }

    [Fact]
    public async Task A_run_records_its_strategy_and_where_its_bytes_came_from()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            new SessionOptions { StateDirectory = _stateDirectory, TicketsPerBatch = 100, SourcePath = @"D:\in\s.txt" },
            TestContext.Current.CancellationToken);

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.CurrentVersion, state.Version);
        Assert.Equal(PublicationState.TicketStrategyName, state.Strategy);
        Assert.Equal(@"D:\in\s.txt", state.SourcePath);
    }

    [Fact]
    public async Task A_finished_run_records_its_manifest_hash()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, PublicationState.Load(session.StatePath).ManifestHash);
    }

    [Fact]
    public async Task A_sequence_run_writes_the_cover_too()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        string cover = Vectors.Hash("cover");
        SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100, Cover = cover.ToLowerInvariant() };

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            options, TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(cover, report.Manifest.Cover);
        Assert.Equal(cover, (await new XrplFileReader(ledger).ManifestAsync(report.Link, TestContext.Current.CancellationToken)).Manifest.Cover);
        Assert.Equal(cover, PublicationState.Load(session.StatePath).Cover);
    }

    [Fact]
    public async Task A_run_that_costs_more_than_the_budget_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using TicketStrategy inner = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });
        LedgerClosingStrategy strategy = new(inner, sink);

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            ledger, Wallet.ClassicAddress, fileBytes: 2500, Options(), TestContext.Current.CancellationToken);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        // A fresh ticketed run with no leftovers mints exactly what the estimate priced — one
        // batching pass for the chunks and one for the manifest — and the report counts those
        // mints (spec I18).
        Assert.Equal(2, estimate.Mints);
        Assert.Equal(estimate.Transactions, report.Transactions);
    }

    [Fact]
    public async Task The_state_file_follows_the_run_through_its_stages()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy first = new(sink, Signer, StrategyOptions);
        await using SequenceStrategy second = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        // A file standing where the state directory was: every later save fails, on any platform.
        Directory.Delete(_stateDirectory, recursive: true);
        await File.WriteAllTextAsync(_stateDirectory, "not a directory", TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(3, report.ChunkCount);

        // The save failed and said so, without taking the report down with it.
        Assert.NotNull(report.StateSaveFailure);
    }

    [Fact]
    public async Task The_state_file_carries_exactly_the_fields_it_is_documented_to_carry()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
                "Size", "Chunk", "Root", "StartedInLedger", "Phase", "Strategy",
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
            """{"Version":9000,"Network":"testnet","Writer":"rSomebody","KeyReference":"key-9","Phase":"chunks"}""",
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
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = NodeFeeDrops + 1 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.FeeMismatch, error.Error);
        Assert.Empty(sink.Submitted);
        Assert.False(Directory.Exists(_stateDirectory));
    }

    [Fact]
    public async Task A_strategy_left_on_its_default_fee_never_starts_the_run()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        // TicketStrategyOptions.FeeDrops defaults to a hardcoded 12 and the node here charges 10,
        // as mainnet, testnet and the CI stand all do. A caller who builds the strategy without
        // saying what it costs is the caller the documented flow used to describe.
        await using TicketStrategy defaulted = new(sink, ledger, Signer);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            defaulted, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.FeeMismatch, error.Error);
    }

    [Fact]
    public async Task The_documented_order_builds_a_strategy_the_session_accepts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        // Exactly the order the README publishes in: estimate, then a strategy priced from that
        // estimate, then StartAsync. What the examples do is what this proves.
        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            ledger, Wallet.ClassicAddress, fileBytes: 2500, Options(), TestContext.Current.CancellationToken);

        await using TicketStrategy strategy = new(sink, ledger, Signer,
            new TicketStrategyOptions
            {
                FeeDrops = (int)estimate.FeeDropsPerTransaction,
                SweepPoll = TimeSpan.FromMilliseconds(1),
                BatchTimeout = TimeSpan.FromSeconds(30),
                ValidationPoll = TimeSpan.FromMilliseconds(1),
            });

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.ChunkCount);
        Assert.Equal("testnet", report.Link.Network);
    }

    [Fact]
    public async Task A_writer_that_could_name_a_path_never_becomes_a_file_name()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

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
        await using (SequenceStrategy dying = new(sink, Signer, StrategyOptions))
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

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, statePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Three chunks landed in the first run and are not sent again; only the manifest is.
        Assert.Equal(submittedByTheFirstRun + 1, sink.Submitted.Count);
        Assert.Equal(3, report.ChunkCount);
    }

    [Fact]
    public async Task A_resumed_run_reads_the_ledger_rather_than_believing_the_state_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
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

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Only the manifest was needed, because the sweep found the chunks the file did not know about.
        Assert.Equal(alreadyThere + 1, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_resumed_run_tidies_up_the_leftovers_too()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("resume", 2500);

        sink.Refusals[3] = "tefBAD_AUTH";
        await using (SequenceStrategy dying = new(sink, Signer, StrategyOptions))
        {
            PublicationSession first = await PublicationSession.StartAsync(
                dying, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
                Options(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, TestContext.Current.CancellationToken));
        }

        sink.Refusals.Clear();

        // What an earlier, interrupted ticketed run left on the account, and one ticket that is not
        // this library's. The resume needs none of them: they are for the tidy-up (spec 4.6, I6).
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        IReadOnlyList<uint> foreign = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: false);

        string statePath = Directory.EnumerateFiles(_stateDirectory, "*.json").Single();

        await using SequenceStrategy resumed = new(
            sink, Signer, new SequenceStrategyOptions { FeeDrops = NodeFeeDrops, ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, statePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(3, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(0, report.TicketsNotReleased);
        Assert.Null(report.TicketReleaseFailure);
        Assert.Equal(3 * NodeFeeDrops, report.TicketReleaseFeeDrops);
        Assert.Equal(0, report.TicketReleaseFeeDropsInDoubt);
        Assert.Equal([.. foreign], sink.LiveTickets.Order().ToArray());
        Assert.All(ours, ticket => Assert.DoesNotContain(ticket, sink.LiveTickets));
    }

    [Fact]
    public async Task A_resume_with_a_different_file_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("one", 2500), "text/plain", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
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

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);
        await first.RunAsync(null, TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }

    [Fact]
    public async Task A_resumed_run_with_a_hole_in_the_middle_puts_every_chunk_inside_the_window()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("hole", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // Chunks 0 and 2 land and the run dies. The hole is in the middle on purpose: what the
        // second run sends is not the last index, so it lands after the last index's ledger, and a
        // window read off the first and last index leaves it outside.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        await strategy.SubmitAllAsync(
            [
                new MemoSubmission(MemoLayout.ChunkMarkerHex(0), MemoLayout.Hex(chunks[0]), null),
                new MemoSubmission(MemoLayout.ChunkMarkerHex(2), MemoLayout.Hex(chunks[2]), null),
            ],
            null, TestContext.Current.CancellationToken);

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        IReadOnlyList<LedgerTransaction> landedChunks = await LedgerRecords.ChunksAsync(
            ledger, Wallet.ClassicAddress, PublicationState.Load(first.StatePath).StartedInLedger, TestContext.Current.CancellationToken);

        long resent = landedChunks[1].LedgerIndex;
        long lastIndexed = landedChunks[^1].LedgerIndex;
        Assert.True(resent > lastIndexed, "the re-sent chunk has to land after the last index's for this test to mean anything");

        foreach (LedgerTransaction landed in landedChunks)
        {
            Assert.InRange(landed.LedgerIndex, report.Manifest.FirstLedger, report.Manifest.LastLedger);
        }

        ReadResult read = await new XrplFileReader(ledger).ReadAsync(report.Link, TestContext.Current.CancellationToken);
        Assert.Equal(file, read.Bytes);
    }

    [Fact]
    public async Task A_resumed_run_does_not_adopt_a_chunk_the_next_publication_wrote()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("mine", 2500);
        byte[] theirs = VectorStream.Bytes("theirs", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // The run dies before submitting anything, and the same account then starts a different
        // file. Its chunks carry xf/c/0..2 as well and are inside the swept range -- a marker names
        // no publication of its own, so only the bytes tell one from the other.
        IReadOnlyList<byte[]> others = FileTree.Split(theirs, MemoLayout.ChunkBytes);
        await strategy.SubmitAllAsync(
            [.. others.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
            null, TestContext.Current.CancellationToken);

        int beforeTheResume = sink.Submitted.Count;

        // Read-back off on purpose: the sweep has to be right on its own rather than because a
        // check the caller is allowed to turn off catches it afterwards.
        SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100, ReadBack = false };

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, options, TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Three chunks of this file, and the manifest: nothing was adopted.
        Assert.Equal(beforeTheResume + 4, sink.Submitted.Count);

        ReadResult read = await new XrplFileReader(ledger).ReadAsync(report.Link, TestContext.Current.CancellationToken);
        Assert.Equal(file, read.Bytes);
    }

    [Fact]
    public async Task A_resumed_run_does_not_re_send_the_block_lists_the_ledger_already_holds()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        // 1024001 bytes: 1025 chunks over two blocks, so there is a block list to reconcile.
        byte[] file = VectorStream.Bytes("lists", 1_024_001);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // Every chunk and the block list land, and then the run dies -- in the read-back or the
        // manifest, both of which come after the lists. That a partial run never reaches the lists
        // is only true of a run that died earlier than this one.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        SubmittedMemos sent = await strategy.SubmitAllAsync(
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
            null, TestContext.Current.CancellationToken);

        Dictionary<int, string> byIndex = sent.Each().ToDictionary(memo => memo.Index, memo => memo.TxHash);
        List<BlockEntry> entries = [];

        for (int block = 0; block < FileTree.BlockCount(chunks.Count); block++)
        {
            int firstChunk = block * FileTree.BlockChunks;
            List<byte[]> members = [.. chunks.Skip(firstChunk).Take(FileTree.BlockChunks)];
            long earliest = long.MaxValue;

            for (int i = firstChunk; i < firstChunk + members.Count; i++)
            {
                earliest = Math.Min(earliest, await LedgerOfAsync(ledger, byIndex[i]));
            }

            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), earliest));
        }

        await strategy.SubmitAllAsync(
            [.. FileTree.EncodeEntries(entries).Select((payload, j) =>
                new MemoSubmission(MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(payload), MemoLayout.JsonFormatHex))],
            null, TestContext.Current.CancellationToken);

        int beforeTheResume = sink.Submitted.Count;

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Only the manifest. The list is adopted only because the writer rebuilt the same payload,
        // per-block ledger included -- which is also what pins a block's first ledger to the
        // earliest of its own chunks rather than to the ledger of its first index.
        Assert.Equal(beforeTheResume + 1, sink.Submitted.Count);
        Assert.Equal(1, report.ListCount);

        ReadResult read = await new XrplFileReader(ledger).ReadAsync(report.Link, TestContext.Current.CancellationToken);
        Assert.Equal(file, read.Bytes);
    }

    [Fact]
    public async Task A_resume_signed_by_another_account_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        await using SequenceStrategy elsewhere = new(sink, new WalletSigner(XrplWallet.Generate()), StrategyOptions);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            elsewhere, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_run_recorded_for_one_account_and_signed_by_another_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null,
            XrplWallet.Generate().ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_resume_of_a_state_file_that_records_no_root_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // A root is the only thing a resume can check the bytes it was handed against. Without one
        // the check is refused, not skipped.
        (PublicationState.Load(first.StatePath) with { Root = null }).Save(first.StatePath);

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }

    [Fact]
    public async Task A_resume_of_a_state_file_written_at_another_chunk_size_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // The writer always re-splits at the current constant, so an old run's indices would be
        // matched against bytes that are no longer the ones at those indices.
        (PublicationState.Load(first.StatePath) with { Chunk = MemoLayout.ChunkBytes - 1 }).Save(first.StatePath);

        await using SequenceStrategy resumed = new(sink, Signer, StrategyOptions);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }

    [Fact]
    public void A_state_file_from_before_the_root_was_recorded_at_the_start_is_not_read()
    {
        // Version 1 is the shape whose Root stayed null until "done". Reading one as if it were
        // current would resume it with no identity to check the bytes against.
        Directory.CreateDirectory(_stateDirectory);
        string path = Path.Combine(_stateDirectory, "old.json");

        File.WriteAllText(path, $$"""
            {
              "Version": 1,
              "Network": "testnet",
              "Writer": "{{Wallet.ClassicAddress}}",
              "KeyReference": "key-1",
              "Type": "text/plain",
              "Name": null,
              "Size": 2500,
              "Chunk": {{MemoLayout.ChunkBytes}},
              "Root": null,
              "StartedInLedger": 5000,
              "Phase": "chunks",
              "ChunkHashes": {}
            }
            """);

        Assert.Throws<FormatException>(() => PublicationState.Load(path));

        UnfinishedRun listed = Assert.Single(PublicationSession.Unfinished(_stateDirectory));
        Assert.Null(listed.State);
        Assert.NotNull(listed.Unreadable);
    }

    /// <summary>A strategy that publishes through <paramref name="inner"/> and tidies up however the test says.</summary>
    private sealed class ScriptedReleaseStrategy(ISubmissionStrategy inner, Func<CancellationToken, Task<TicketRelease>> release) : ISubmissionStrategy
    {
        public bool ReleaseCalled { get; private set; }

        public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

        public string Account => inner.Account;

        public int TicketMints => inner.TicketMints;

        public Task<SubmittedMemos> SubmitAllAsync(
            IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken) =>
            inner.SubmitAllAsync(memos, submitted, cancellationToken);

        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken)
        {
            ReleaseCalled = true;
            return release(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A strategy that closes a fake ledger after each of the writer's calls. On a node the ledger a
    /// sweep has seen validated is closed, so the writer's next call cannot land in it; this fake
    /// closes a ledger only every hundred submissions, and a call that mints nothing anchors its
    /// sweep one past the validated ledger, where the fake would never put its memo.
    /// </summary>
    private sealed class LedgerClosingStrategy(ISubmissionStrategy inner, FakeLedgerSink sink) : ISubmissionStrategy
    {
        public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

        public string Account => inner.Account;

        public int TicketMints => inner.TicketMints;

        public async Task<SubmittedMemos> SubmitAllAsync(
            IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
        {
            SubmittedMemos landed = await inner.SubmitAllAsync(memos, submitted, cancellationToken);
            sink.CloseLedger();
            return landed;
        }

        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
            inner.ReleaseLeftoverTicketsAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task A_publication_spends_the_leftovers_it_finds_and_says_so_on_the_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = NodeFeeDrops, ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(0, report.TicketsNotReleased);
        Assert.Null(report.TicketReleaseFailure);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_tidy_up_that_throws_leaves_the_publication_successful_and_says_so()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            _ => throw new InvalidOperationException("the tidy-up broke"));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal("the tidy-up broke", Assert.IsType<InvalidOperationException>(report.TicketReleaseFailure).Message);
        Assert.Empty(PublicationSession.Unfinished(_stateDirectory));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_tidy_up_that_does_not_finish_in_time_does_not_hold_the_report_back()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TicketRelease.None;
            });

        SessionOptions options = new()
        {
            StateDirectory = _stateDirectory,
            TicketsPerBatch = 100,
            TicketReleaseTimeout = TimeSpan.FromMilliseconds(100),
        };

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", options, TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Null(report.TicketReleaseFailure);
        Assert.Null(report.TicketReleaseFeeDrops);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_tidy_up_cut_off_by_its_deadline_keeps_the_counts_it_reached()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new TicketRelease(1, 0, 2);
            });

        PublicationReport report = await RunWithReleaseTimeoutAsync(strategy, ledger, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(1, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(2, report.TicketsNotReleased);
        Assert.Null(report.TicketReleaseFailure);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_tidy_up_that_ignores_its_deadline_cannot_hold_the_report_back()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            _ => new TaskCompletionSource<TicketRelease>().Task);

        PublicationReport report = await RunWithReleaseTimeoutAsync(strategy, ledger, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(0, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(0, report.TicketsNotReleased);
        TimeoutException timeout = Assert.IsType<TimeoutException>(report.TicketReleaseFailure);
        Assert.Contains("within 5 s of its 0.1 s deadline", timeout.Message, StringComparison.Ordinal);
        Assert.Null(report.TicketReleaseFeeDrops);
    }

    [Fact]
    public async Task A_tidy_up_timeout_that_is_not_a_time_span_does_not_cost_the_publication()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        PublicationReport report = await RunWithReleaseTimeoutAsync(strategy, ledger, TimeSpan.FromMilliseconds(-5), TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.IsType<ArgumentOutOfRangeException>(report.TicketReleaseFailure);
    }

    private async Task<PublicationReport> RunWithReleaseTimeoutAsync(
        ISubmissionStrategy strategy, FakeLedgerSource ledger, TimeSpan releaseTimeout, CancellationToken cancellationToken)
    {
        SessionOptions options = new()
        {
            StateDirectory = _stateDirectory,
            TicketsPerBatch = 100,
            TicketReleaseTimeout = releaseTimeout,
        };

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", options, cancellationToken);

        return await session.RunAsync(null, cancellationToken);
    }

    [Fact]
    public async Task A_fresh_run_counts_the_mints_it_made_not_the_estimates()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 100, marked: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });
        byte[] file = VectorStream.Bytes("mints", 2500);

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(ledger, Wallet.ClassicAddress, file.Length, Options(), TestContext.Current.CancellationToken);
        PublicationSession session = await PublicationSession.StartAsync(
            new LedgerClosingStrategy(strategy, sink), ledger, file, "text/plain", "m.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        long memos = report.ChunkCount + report.ListCount + 1;
        Assert.Equal(memos + strategy.TicketMints, report.Transactions);
        Assert.True(report.Transactions < memos + estimate.Mints, "the picked-up tickets cost nothing to mint");
        Assert.Equal(report.Transactions * NodeFeeDrops, report.FeeDropsEstimated);
    }

    [Fact]
    public async Task A_reused_strategy_counts_only_this_sessions_mints()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            FeeDrops = NodeFeeDrops, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1),
        });
        LedgerClosingStrategy closing = new(strategy, sink);

        PublicationSession first = await PublicationSession.StartAsync(
            closing, ledger, VectorStream.Bytes("one", 2500), "text/plain", "1.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        await first.RunAsync(null, TestContext.Current.CancellationToken);
        int afterFirst = strategy.TicketMints;

        PublicationSession second = await PublicationSession.StartAsync(
            closing, ledger, VectorStream.Bytes("two", 2500), "text/plain", "2.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        long memos = report.ChunkCount + report.ListCount + 1;
        Assert.Equal(memos + (strategy.TicketMints - afterFirst), report.Transactions);
    }

    [Fact]
    public async Task The_report_prices_the_tidy_ups_charged_and_in_doubt_spends()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy inner = new(sink, Signer, StrategyOptions);
        ScriptedReleaseStrategy strategy = new(inner, _ => Task.FromResult(new TicketRelease(3, 1, 2) { Charged = 4, InDoubt = 2 }));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("fees", 2500), "text/plain", "f.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(4 * NodeFeeDrops, report.TicketReleaseFeeDrops);
        Assert.Equal(2 * NodeFeeDrops, report.TicketReleaseFeeDropsInDoubt);
    }

    [Fact]
    public async Task A_tidy_up_that_throws_leaves_its_cost_unknown()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy inner = new(sink, Signer, StrategyOptions);
        ScriptedReleaseStrategy strategy = new(inner, _ => throw new InvalidOperationException("the tidy-up broke"));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("fees", 2500), "text/plain", "f.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(report.TicketReleaseFeeDrops);
        Assert.Null(report.TicketReleaseFeeDropsInDoubt);
    }

    [Fact]
    public async Task A_cancelled_publication_does_not_tidy_up()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> leftovers = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);

        // A ticket strategy, because it checks the token before every batch: the writer and the
        // sequence strategy never look at an already-cancelled token against this fake.
        await using ScriptedReleaseStrategy strategy = new(
            new TicketStrategy(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops }),
            _ => Task.FromResult(TicketRelease.None));

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, cancelled.Token));

        // The leftovers are exactly what a resume will pick up; spending them would charge fees
        // for a run the person stopped.
        Assert.False(strategy.ReleaseCalled);
        Assert.All(leftovers, ticket => Assert.Contains(ticket, sink.LiveTickets));
    }

    [Fact]
    public async Task A_ticketed_run_with_no_free_slot_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, TicketStrategy.MaxLiveTickets);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 100_000), "application/octet-stream", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_ticketed_run_with_no_free_slot_is_refused_for_the_slot_even_when_poor()
    {
        FakeLedgerSource ledger = Node(balanceDrops: 1);
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, TicketStrategy.MaxLiveTickets);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });

        // Topping up the balance would not make this run possible; freeing a slot would. The
        // refusal has to name the reason a person can act on.
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 100_000), "application/octet-stream", null,
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_resumed_ticketed_run_with_no_free_slot_never_starts()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        sink.LeakTickets(Wallet.ClassicAddress, TicketStrategy.MaxLiveTickets);

        await using TicketStrategy resumed = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_ticketed_publication_spends_the_leftovers_it_picked_up_but_did_not_need()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 10, marked: true);
        await using LedgerClosingStrategy strategy = new(
            new TicketStrategy(sink, ledger, Signer, new TicketStrategyOptions
            {
                FeeDrops = NodeFeeDrops,
                SweepPoll = TimeSpan.FromMilliseconds(1),
                BatchTimeout = TimeSpan.FromSeconds(5),
                ValidationPoll = TimeSpan.FromMilliseconds(1),
            }),
            sink);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        // Three chunks and a manifest ride four of the ten; the tidy-up spends the other six, so
        // the run leaves no ticket of its own behind and mints none.
        Assert.DoesNotContain(sink.Submitted, tx => (string)tx["TransactionType"] == "TicketCreate");
        Assert.Equal(6, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(0, report.TicketsNotReleased);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_caller_cancelling_during_the_tidy_up_still_gets_the_report()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using ScriptedReleaseStrategy strategy = new(
            new SequenceStrategy(sink, Signer, StrategyOptions),
            async token =>
            {
                // The manifest is in: a person closing the window now stops the tidy-up, not the
                // publication they already paid for (spec I8).
                await caller.CancelAsync();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                }

                return new TicketRelease(0, 0, 3);
            });

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", "s.txt",
            Wallet.ClassicAddress, "key-1", "testnet", Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await session.RunAsync(null, caller.Token);

        Assert.Equal(report.ManifestHash, report.Link.TxHash);
        Assert.Equal(0, report.TicketsReleased);
        Assert.Equal(0, report.TicketsAlreadyGone);
        Assert.Equal(3, report.TicketsNotReleased);
    }

    /// <summary>The ledger a transaction is in, for a test that has to know where a memo landed.</summary>
    private static async Task<long> LedgerOfAsync(FakeLedgerSource ledger, string hash) =>
        (await ledger.TransactionAsync(hash, TestContext.Current.CancellationToken))!.LedgerIndex;
}
