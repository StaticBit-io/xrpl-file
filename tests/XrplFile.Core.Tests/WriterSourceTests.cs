using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class WriterSourceTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_source_publishes_what_the_same_bytes_would_and_reads_back()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        CountingPublicationSource file = new(new VectorPublicationSource("writer-source", 2_500_500));

        PublishResult result = await new XrplFileWriter(strategy, ledger).PublishAsync(
            file, "application/octet-stream", "w.bin", Wallet.ClassicAddress, "testnet", null, null, Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("writer-source", 2_500_500)), result.Manifest.Root);
        Assert.Equal(VectorStream.Bytes("writer-source", 2_500_500), (await new XrplFileReader(ledger, new ReaderOptions { MaxBytes = long.MaxValue }).ReadAsync(result.Link, Token)).Bytes);

        // Hashing, sending and the full read-back each read every chunk once; nothing more.
        Assert.Equal(3 * 2_501, file.Reads);
    }

    [Theory]
    [InlineData(1_024_000)]
    [InlineData(1_024_001)]
    public async Task The_full_read_back_checks_a_file_at_a_block_boundary_against_the_source(int size)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        byte[] bytes = VectorStream.Bytes("writer-boundary", size);
        List<PublishProgress> reports = [];

        PublishResult result = await new XrplFileWriter(strategy, ledger).PublishAsync(
            new MemoryPublicationSource(bytes), "application/octet-stream", null, Wallet.ClassicAddress, "testnet", null,
            new SynchronousProgress<PublishProgress>(reports.Add), Token);

        // A mismatch in the last block (a short or an extra chunk) would have thrown BlockMismatch before the manifest.
        Assert.Contains(reports, report => report.Stage == "read-back");
        Assert.Equal(result.Manifest.Size, size);
        Assert.Equal(bytes, (await new XrplFileReader(ledger, new ReaderOptions { MaxBytes = long.MaxValue }).ReadAsync(result.Link, Token)).Bytes);
    }

    [Fact]
    public async Task Block_hashes_handed_in_are_not_computed_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        byte[] bytes = VectorStream.Bytes("writer-source", 3_000);
        CountingPublicationSource file = new(new MemoryPublicationSource(bytes));

        await new XrplFileWriter(strategy, ledger, new WriterOptions { ReadBack = false }).PublishAsync(
            file, FileTree.BlockHashes(FileTree.Split(bytes, MemoLayout.ChunkBytes)), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, Token);

        Assert.Equal(3, file.Reads);
    }
}
