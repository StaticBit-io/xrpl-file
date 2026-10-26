using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class SessionSourceTests : IDisposable
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-session-source-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _directory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        ledger.Balances[Wallet.ClassicAddress] = 500_000_000;
        return ledger;
    }

    [Fact]
    public async Task Starting_from_a_source_reports_hashing_in_chunks_and_records_the_streamed_root()
    {
        FakeLedgerSource ledger = Node();
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource file = new("session-source", 2_500_500);
        List<PublishProgress> preparation = [];

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options,
            new SynchronousProgress<PublishProgress>(preparation.Add), Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("session-source", 2_500_500)), session.State.Root);
        Assert.Equal(
            [new PublishProgress("hashing", 1_024, 2_501), new PublishProgress("hashing", 2_048, 2_501), new PublishProgress("hashing", 2_501, 2_501)],
            preparation);
    }

    [Fact]
    public async Task A_resume_from_another_file_is_refused_before_the_ledger_is_read()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource started = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, started, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await using VectorPublicationSource other = new("another", 3_000);
        int reads = ledger.AccountTransactionsCalls;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, other, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
        Assert.Equal(reads, ledger.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_resume_refused_for_its_chunk_size_does_not_blame_the_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource file = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        (PublicationState.Load(first.StatePath) with { Chunk = 999 }).Save(first.StatePath);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, file, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.False(error.SourceChanged);
    }

    [Fact]
    public async Task A_source_above_the_publication_limit_is_refused_before_anything_is_read()
    {
        FakeLedgerSource ledger = Node();
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using UnreadableSource file = new(PublicationSession.MaxPublicationBytes + 1);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token));

        Assert.Equal(XrplFileError.TooLarge, error.Error);
    }

    [Fact]
    public async Task Reconciliation_compares_landed_chunks_with_the_source()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10, ValidationPoll = TimeSpan.FromMilliseconds(1) });
        await using VectorPublicationSource file = new("session-source", 2_500);
        ScriptedStrategy dies = new(strategy) { FailAtCall = 2 };
        PublicationSession first = await PublicationSession.StartAsync(
            dies, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, Token));
        int submitted = sink.Submitted.Count;

        PublicationSession resumed = await PublicationSession.ResumeAsync(strategy, ledger, file, first.StatePath, Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        // The three chunks of the first session were adopted: only the manifest was sent (no list for one block).
        Assert.Equal(submitted + 1, sink.Submitted.Count);
        Assert.Equal(VectorStream.Bytes("session-source", 2_500), (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_resume_of_an_emptied_file_is_a_changed_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource started = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, started, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, new MemoryPublicationSource(ReadOnlyMemory<byte>.Empty), first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
    }

    [Fact]
    public async Task A_resume_of_a_file_grown_past_the_limit_is_a_changed_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource started = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, started, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await using UnreadableSource grown = new(PublicationSession.MaxPublicationBytes + 1);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, grown, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
    }

    [Fact]
    public async Task A_resume_of_a_file_of_another_size_is_a_changed_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource started = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, started, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await using VectorPublicationSource longer = new("session-source", 3_001);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, longer, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
    }

    /// <summary>A file too large to publish, which must never be read.</summary>
    private sealed class UnreadableSource(long length) : IPublicationSource
    {
        public long Length => length;

        public int ReadChunk(long index, Span<byte> destination) => throw new InvalidOperationException("read");

        public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("read");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
