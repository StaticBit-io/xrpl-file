using System.Runtime.CompilerServices;
using Xunit;

namespace XrplFile.Tests;

public class SampledReadBackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<List<PublishProgress>> PublishAsync(long bytes)
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();
        List<PublishProgress> reports = [];
        await using VectorPublicationSource file = new("sampled", bytes);

        PublishResult result = await new XrplFileWriter(strategy, node.Ledger).PublishAsync(
            file, "application/octet-stream", null, node.Writer.Account, "testnet", null,
            new SynchronousProgress<PublishProgress>(reports.Add), Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("sampled", bytes)), result.Manifest.Root);
        return reports;
    }

    [Fact]
    public async Task A_Batch_file_of_five_blocks_reads_back_four_sampled_blocks()
    {
        List<PublishProgress> reports = await PublishAsync(5 * 1_024_000);

        Assert.Equal(
            [new PublishProgress("read-back", 1, 4), new PublishProgress("read-back", 2, 4), new PublishProgress("read-back", 3, 4), new PublishProgress("read-back", 4, 4)],
            reports.Where(progress => progress.Stage == "read-back"));
    }

    [Fact]
    public async Task A_Batch_file_of_three_blocks_reads_back_every_block()
    {
        List<PublishProgress> reports = await PublishAsync(2 * 1_024_000 + 1);

        Assert.Equal(3, reports.Where(progress => progress.Stage == "read-back").Select(progress => progress.Total).Distinct().Single());
        Assert.Equal(3, reports.Count(progress => progress.Stage == "read-back"));
    }

    [Fact]
    public async Task A_block_list_on_the_ledger_whose_ledgers_differ_from_the_plan_fails_the_read_back()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();
        await using VectorPublicationSource file = new("sampled-list", 2 * 1_024_000);

        // The strategy writes to the honest ledger; only the writer's read-back sees the shifted list.
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(strategy, new ListShiftingSource(node.Ledger)).PublishAsync(
            file, "application/octet-stream", null, node.Writer.Account, "testnet", null, null, Token));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
        Assert.Contains("is not the plan", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shows every block-list memo with its first entry's ledger moved one earlier. The root covers
    /// block hashes and not ledgers, so the list still roots; it is not the plan.
    /// </summary>
    private sealed class ListShiftingSource(FakeLedgerSource inner) : ILedgerSource
    {
        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (LedgerTransaction tx in inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken))
            {
                if (MemoLayout.ListIndexOf(tx.MemoTypeHex) is null || tx.MemoDataHex is null
                    || !FileTree.TryDecodeFragment(MemoLayout.FromHexUtf8(tx.MemoDataHex), out IReadOnlyList<BlockEntry>? entries))
                {
                    yield return tx;
                    continue;
                }

                List<BlockEntry> shifted = [entries[0] with { FirstLedger = entries[0].FirstLedger - 1 }, .. entries.Skip(1)];
                yield return tx with { MemoDataHex = MemoLayout.Utf8Hex(FileTree.EncodeEntries(shifted)[0]) };
            }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => inner.TransactionAsync(hash, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);
    }
}
