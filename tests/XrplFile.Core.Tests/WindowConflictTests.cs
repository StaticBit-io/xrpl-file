using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class WindowConflictTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-conflict-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node) =>
        (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);

    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node, int stopAtOrdinal = 1)
    {
        byte[] file = VectorStream.Bytes("conflict", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= stopAtOrdinal) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;

        // The fake closes a ledger on each read; what the cancelled run had sent may still be open.
        for (int read = 0; read < 3; read++) await node.Ledger.CurrentLedgerAsync(Token);

        return (session, file);
    }

    /// <summary>A chunk memo of the writer, validated and successful, that another publication left in the window.</summary>
    private static void Plant(BatchTestNode node, long chunk, byte[] data, string hash) =>
        Plant(node.Ledger, node.Writer.Account, chunk, data, hash);

    private static void Plant(FakeLedgerSource ledger, string account, long chunk, byte[] data, string hash) =>
        ledger.Add(PlantedChunk(ledger, account, chunk, data, hash));

    private static LedgerTransaction PlantedChunk(FakeLedgerSource ledger, string account, long chunk, byte[] data, string hash) =>
        new(hash, ledger.CurrentLedger, account, true, true, "tesSUCCESS", MemoLayout.ChunkMarkerHex(chunk), MemoLayout.Hex(data), null)
        {
            TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
        };

    private static async Task<LedgerTransaction> FirstChunkAsync(FakeLedgerSource ledger, string account)
    {
        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(account, 0, ledger.CurrentLedger, true, Token))
        {
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0) return tx;
        }

        throw new InvalidOperationException("the run left no chunk 0");
    }

    [Fact]
    public async Task A_conflicting_copy_in_the_window_fails_the_resume_and_is_recorded()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, [1, 2, 3], new string('C', 64));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Equal(PublicationState.WriterWindowConflictFailure, PublicationState.Load(first.StatePath).Failure);
    }

    [Fact]
    public async Task A_conflict_whose_record_cannot_be_written_still_fails_with_the_conflict()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, [1, 2, 3], new string('C', 64));

        // A directory where Save writes its temporary file: a read-only attribute would not stop a write on Linux.
        Directory.CreateDirectory(first.StatePath + ".writing");

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Null(PublicationState.Load(first.StatePath).Failure);
    }

    [Fact]
    public async Task A_recorded_conflict_is_reported_again_without_reading_or_sending()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, [1, 2, 3], new string('C', 64));
        await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));
        int reads = node.Ledger.AccountTransactionsCalls;
        int snapshots = node.Ledger.SnapshotReads;
        int sent = node.Sink.Transmissions.Count;

        XrplFileException again = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, again.Error);
        Assert.Equal((reads, snapshots, sent), (node.Ledger.AccountTransactionsCalls, node.Ledger.SnapshotReads, node.Sink.Transmissions.Count));
    }

    [Fact]
    public async Task An_identical_duplicate_is_harmless()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node, stopAtOrdinal: 3);

        IReadOnlyDictionary<string, List<LedgerTransaction>> landed = await node.LandedByMarkerAsync();
        long originalOfChunk0 = Assert.Single(landed[MemoLayout.ChunkMarkerHex(0)]).LedgerIndex;

        // A block's first ledger is the earliest of its chunks' ledgers, and a batch closes up to eight chunks
        // into one ledger. Move every other chunk one ledger on so that chunk 0 is alone in the earliest one:
        // a later copy of chunk 0 adopted in place of the original would then move the block's first ledger.
        node.Ledger.Replace(
            tx => string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal) && MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index && index != 0,
            tx => tx with { LedgerIndex = tx.LedgerIndex + 1 });
        IReadOnlyDictionary<string, List<LedgerTransaction>> shifted = await node.LandedByMarkerAsync();
        Assert.All(
            shifted.Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is { } index && index != 0).SelectMany(pair => pair.Value),
            tx => Assert.True(tx.LedgerIndex > originalOfChunk0));

        node.Ledger.CurrentLedger += 4;
        Plant(node, 0, file.AsSpan(0, MemoLayout.ChunkBytes).ToArray(), new string('D', 64));
        Assert.True(node.Ledger.CurrentLedger > originalOfChunk0 + 1);

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(originalOfChunk0, report.Manifest.FirstLedger);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_record_of_another_account_carrying_this_files_marker_is_ignored()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        IReadOnlyDictionary<string, List<LedgerTransaction>> landed = await node.LandedByMarkerAsync();
        int unsent = Enumerable.Range(0, (int)ChunkReads.ChunkCount(file.Length)).First(index => !landed.ContainsKey(MemoLayout.ChunkMarkerHex(index)));

        // The payer's records reach the writer's account_tx the way any record touching the writer does.
        // One carries a chunk the run has not sent, with the bytes it would send: counted as the writer's,
        // the chunk would never be paid for. The other carries chunk 0 with other bytes: counted as the
        // writer's, it would be a window conflict.
        int from = unsent * MemoLayout.ChunkBytes;
        byte[] unsentBytes = file.AsSpan(from, Math.Min(MemoLayout.ChunkBytes, file.Length - from)).ToArray();
        node.Ledger.AddAffecting(PlantedChunk(node.Ledger, node.Payer.Account, unsent, unsentBytes, new string('A', 64)), node.Writer.Account);
        node.Ledger.AddAffecting(PlantedChunk(node.Ledger, node.Payer.Account, 0, [1, 2, 3], new string('B', 64)), node.Writer.Account);

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Null(PublicationState.Load(first.StatePath).Failure);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        IReadOnlyDictionary<string, List<LedgerTransaction>> after = await node.LandedByMarkerAsync();
        Assert.Single(after[MemoLayout.ChunkMarkerHex(unsent)], tx => string.Equals(tx.Account, node.Writer.Account, StringComparison.Ordinal));
    }

    /// <summary>A one-account run that died with every chunk landed and no manifest sent.</summary>
    private async Task<(FakeLedgerSource Ledger, FakeLedgerSink Sink, ITransactionSigner Signer, string Account, PublicationSession First, byte[] File)> DiedAtTheManifestAsync()
    {
        XrplWallet wallet = XrplWallet.Generate();
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        ledger.Balances[wallet.ClassicAddress] = 500_000_000;
        FakeLedgerSink sink = new(ledger);
        ITransactionSigner signer = new WalletSigner(wallet);
        byte[] file = VectorStream.Bytes("one-account", 2500);

        ScriptedStrategy dying = new(new SequenceStrategy(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 })) { FailAtCall = 2 };
        PublicationSession first = await PublicationSession.StartAsync(
            dying, ledger, file, "text/plain", null, wallet.ClassicAddress, "key-w", "testnet", Options, Token);
        await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, Token));

        return (ledger, sink, signer, wallet.ClassicAddress, first, file);
    }

    [Fact]
    public async Task A_one_account_resume_ignores_a_writer_copy_with_other_bytes()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();
        ledger.CurrentLedger += 2;
        Plant(ledger, account, 0, [1, 2, 3], new string('C', 64));

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        PublicationSession resumed = await PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Null(PublicationState.Load(first.StatePath).Failure);
        Assert.Equal(file, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    /// <summary>Every chunk record of the account, as the one-account run that died left them.</summary>
    private static async Task<List<LedgerTransaction>> ChunkRecordsAsync(FakeLedgerSource ledger, string account)
    {
        List<LedgerTransaction> records = [];

        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(account, 0, ledger.CurrentLedger, true, Token))
        {
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is not null) records.Add(tx);
        }

        return records;
    }

    /// <summary>
    /// Takes chunk 0's own copy out of the ledger, puts a copy of chunk 0 with other bytes in
    /// <paramref name="foreignLedger"/> and puts the own copy back after it (in <paramref name="ownLedger"/>).
    /// The foreign copy is then met for a chunk with no adopted copy.
    /// </summary>
    private static void ForeignCopyAheadOfChunk0(FakeLedgerSource ledger, LedgerTransaction own, long foreignLedger, long ownLedger)
    {
        ledger.Remove(tx => string.Equals(tx.Hash, own.Hash, StringComparison.Ordinal));
        ledger.Add(PlantedChunk(ledger, own.Account, 0, [1, 2, 3], new string('C', 64)) with { LedgerIndex = foreignLedger });
        ledger.Add(own with { LedgerIndex = ownLedger });
    }

    [Fact]
    public async Task A_one_account_resume_fails_on_a_copy_with_other_bytes_in_the_ledger_of_the_blocks_first_adopted_copy()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();

        // The block's first ledger is where a reader starts, and it reads that whole ledger: a foreign copy of
        // chunk 0 there is met before chunk 0's own copy, which lies later (spec S17).
        List<LedgerTransaction> records = await ChunkRecordsAsync(ledger, account);
        LedgerTransaction own = records.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        long blockFirst = records.Min(tx => tx.LedgerIndex);
        ForeignCopyAheadOfChunk0(ledger, own, blockFirst, own.LedgerIndex);
        int sent = sink.Submitted.Count;

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Equal(PublicationState.WriterWindowConflictFailure, PublicationState.Load(first.StatePath).Failure);
        Assert.Equal(sent, sink.Submitted.Count);

        // The record is on the state, so a second resume reports it without reading the history or sending.
        int reads = ledger.AccountTransactionsCalls;
        XrplFileException repeated = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, repeated.Error);
        Assert.Equal(reads, ledger.AccountTransactionsCalls);
        Assert.Equal(sent, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_one_account_resume_names_the_first_copy_with_other_bytes_it_met_inside_the_window()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();

        List<LedgerTransaction> records = await ChunkRecordsAsync(ledger, account);
        LedgerTransaction own = records.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        long blockFirst = records.Min(tx => tx.LedgerIndex);
        string firstMet = new('C', 64);
        string laterOne = new('E', 64);

        // Insertion order is canonical order, so both foreign copies have to go in before chunk 0's own.
        ledger.Remove(tx => string.Equals(tx.Hash, own.Hash, StringComparison.Ordinal));
        ledger.Add(PlantedChunk(ledger, own.Account, 0, [1, 2, 3], firstMet) with { LedgerIndex = blockFirst });
        ledger.Add(PlantedChunk(ledger, own.Account, 0, [4, 5, 6], laterOne) with { LedgerIndex = blockFirst });
        ledger.Add(own);

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Contains(firstMet, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(laterOne, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_account_resume_names_the_latest_copy_when_the_first_one_met_lies_before_the_window()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();

        List<LedgerTransaction> records = await ChunkRecordsAsync(ledger, account);
        LedgerTransaction own = records.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        long blockFirst = records.Min(tx => tx.LedgerIndex);
        Assert.True(blockFirst - 1 >= PublicationState.Load(first.StatePath).StartedInLedger, "the copy has to be inside the swept range, or the test proves nothing");
        string beforeWindow = new('C', 64);
        string inWindow = new('E', 64);

        ledger.Remove(tx => string.Equals(tx.Hash, own.Hash, StringComparison.Ordinal));
        ledger.Add(PlantedChunk(ledger, own.Account, 0, [1, 2, 3], beforeWindow) with { LedgerIndex = blockFirst - 1 });
        ledger.Add(PlantedChunk(ledger, own.Account, 0, [4, 5, 6], inWindow) with { LedgerIndex = blockFirst });
        ledger.Add(own);

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Contains(inWindow, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(beforeWindow, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_account_resume_fails_on_a_copy_with_other_bytes_after_the_blocks_first_adopted_ledger_and_ahead_of_the_chunks_own_copy()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();

        // Chunks 1 and 2 are adopted at the block's first ledger or later; chunk 0's own copy lies after everything,
        // and the foreign copy of chunk 0 is in the last ledger of the others.
        ledger.CurrentLedger += 5;
        List<LedgerTransaction> records = await ChunkRecordsAsync(ledger, account);
        LedgerTransaction own = records.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        long lastOfOthers = records.Where(tx => tx.Hash != own.Hash).Max(tx => tx.LedgerIndex);
        ForeignCopyAheadOfChunk0(ledger, own, lastOfOthers, lastOfOthers + 1);
        int sent = sink.Submitted.Count;

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Equal(sent, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_one_account_resume_passes_by_a_copy_with_other_bytes_that_lies_before_every_adopted_copy_of_its_block()
    {
        (FakeLedgerSource ledger, FakeLedgerSink sink, ITransactionSigner signer, string account, PublicationSession first, byte[] file) = await DiedAtTheManifestAsync();
        ledger.CurrentLedger += 2;

        // Strictly before the block's first ledger, which is where a reader starts: it never meets this copy.
        List<LedgerTransaction> records = await ChunkRecordsAsync(ledger, account);
        LedgerTransaction own = records.Single(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 0);
        long blockFirst = records.Min(tx => tx.LedgerIndex);
        Assert.True(blockFirst - 1 >= PublicationState.Load(first.StatePath).StartedInLedger, "the copy has to be inside the swept range, or the test proves nothing");
        ForeignCopyAheadOfChunk0(ledger, own, blockFirst - 1, own.LedgerIndex);

        await using SequenceStrategy again = new(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });
        PublicationSession resumed = await PublicationSession.ResumeAsync(again, ledger, file, first.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Null(PublicationState.Load(first.StatePath).Failure);
        Assert.Equal(file, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
    }
}
