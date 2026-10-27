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

    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node)
    {
        byte[] file = VectorStream.Bytes("conflict", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= 1) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;
        return (session, file);
    }

    /// <summary>A chunk memo of the writer, validated and successful, that another publication left in the window.</summary>
    private static void Plant(BatchTestNode node, long chunk, byte[] data, string hash) =>
        node.Ledger.Add(new LedgerTransaction(hash, node.Ledger.CurrentLedger, node.Writer.Account, true, true, "tesSUCCESS",
            MemoLayout.ChunkMarkerHex(chunk), MemoLayout.Hex(data), null)
        {
            TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
        });

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
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, file.AsSpan(0, MemoLayout.ChunkBytes).ToArray(), new string('D', 64));

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }
}
