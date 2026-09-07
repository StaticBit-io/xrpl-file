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

        // Four memos and the two mints the approved estimate priced — one batching pass for the
        // chunks and one for the manifest.
        Assert.Equal(6, report.Transactions);
        Assert.Equal(6 * NodeFeeDrops, report.FeeDropsEstimated);
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
        Assert.Equal(2, estimate.Mints);
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

    [Fact]
    public async Task A_resumed_run_with_a_hole_in_the_middle_puts_every_chunk_inside_the_window()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("hole", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
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

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        long resent = await LedgerOfAsync(ledger, report.ChunkHashes[1]);
        long lastIndexed = await LedgerOfAsync(ledger, report.ChunkHashes[^1]);
        Assert.True(resent > lastIndexed, "the re-sent chunk has to land after the last index's for this test to mean anything");

        foreach (string hash in report.ChunkHashes)
        {
            Assert.InRange(await LedgerOfAsync(ledger, hash), report.Manifest.FirstLedger, report.Manifest.LastLedger);
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

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
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

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
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

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // Every chunk and the block list land, and then the run dies -- in the read-back or the
        // manifest, both of which come after the lists. That a partial run never reaches the lists
        // is only true of a run that died earlier than this one.
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        IReadOnlyList<SubmittedMemo> sent = await strategy.SubmitAllAsync(
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
            null, TestContext.Current.CancellationToken);

        Dictionary<int, string> byIndex = sent.ToDictionary(memo => memo.Index, memo => memo.TxHash);
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

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);
        PublicationSession second = await PublicationSession.ResumeAsync(
            resumed, ledger, file, first.StatePath, Options(), TestContext.Current.CancellationToken);

        PublicationReport report = await second.RunAsync(null, TestContext.Current.CancellationToken);

        // Only the manifest. The list is adopted only because the writer rebuilt the same payload,
        // per-block ledger included -- which is also what pins a block's first ledger to the
        // earliest of its own chunks rather than to the ledger of its first index.
        Assert.Equal(beforeTheResume + 1, sink.Submitted.Count);
        Assert.Single(report.ListHashes);

        ReadResult read = await new XrplFileReader(ledger).ReadAsync(report.Link, TestContext.Current.CancellationToken);
        Assert.Equal(file, read.Bytes);
    }

    [Fact]
    public async Task A_resume_signed_by_another_account_is_refused()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s", 2500);

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        await using SequenceStrategy elsewhere = new(sink, XrplWallet.Generate(), StrategyOptions);

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

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);

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

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // A root is the only thing a resume can check the bytes it was handed against. Without one
        // the check is refused, not skipped.
        (PublicationState.Load(first.StatePath) with { Root = null }).Save(first.StatePath);

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);

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

        await using SequenceStrategy strategy = new(sink, Wallet, StrategyOptions);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);

        // The writer always re-splits at the current constant, so an old run's indices would be
        // matched against bytes that are no longer the ones at those indices.
        (PublicationState.Load(first.StatePath) with { Chunk = MemoLayout.ChunkBytes - 1 }).Save(first.StatePath);

        await using SequenceStrategy resumed = new(sink, Wallet, StrategyOptions);

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

    /// <summary>The ledger a transaction is in, for a test that has to know where a memo landed.</summary>
    private static async Task<long> LedgerOfAsync(FakeLedgerSource ledger, string hash) =>
        (await ledger.TransactionAsync(hash, TestContext.Current.CancellationToken))!.LedgerIndex;
}
