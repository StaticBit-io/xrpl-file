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
