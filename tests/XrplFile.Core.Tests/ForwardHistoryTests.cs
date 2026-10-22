using Xunit;

namespace XrplFile.Tests;

public class ForwardHistoryTests
{
    private static readonly VectorFile Vector = Vectors.File(2500500);

    private static async Task<List<LedgerTransaction>> WalkAsync(ForwardHistory history, long from, Action? perRecord = null)
    {
        List<LedgerTransaction> seen = [];

        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, from, Vector.Ledger.Last, TestContext.Current.CancellationToken))
        {
            seen.Add(tx);
            perRecord?.Invoke();
        }

        return seen;
    }

    [Fact]
    public async Task What_it_holds_stays_under_its_cap_and_a_walk_from_what_it_let_go_asks_the_node_again()
    {
        FakeLedgerSource source = new FakeLedgerSource().AddRange(Vectors.Ledger(Vector));
        ForwardHistory history = new(source, maxRecords: 150);
        int most = 0;

        List<LedgerTransaction> whole = await WalkAsync(history, Vector.Ledger.First, () => most = Math.Max(most, history.Held));
        List<LedgerTransaction> again = await WalkAsync(history, Vector.Ledger.First);

        Assert.InRange(most, 1, 150);
        Assert.Equal(whole, again);
        Assert.Equal(2, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_walk_inside_a_history_read_to_its_end_asks_the_node_nothing()
    {
        FakeLedgerSource source = new FakeLedgerSource().AddRange(Vectors.Ledger(Vector));
        ForwardHistory history = new(source, maxRecords: 150);
        List<LedgerTransaction> whole = await WalkAsync(history, Vector.Ledger.First);
        long late = whole[^1].LedgerIndex;

        List<LedgerTransaction> tail = await WalkAsync(history, late);

        Assert.Equal(whole.Where(tx => tx.LedgerIndex >= late), tail);
        Assert.Equal(1, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_walk_starting_one_past_where_the_last_stopped_carries_it_on_past_the_rest_of_its_ledger()
    {
        FakeLedgerSource source = new FakeLedgerSource().AddRange(Vectors.Ledger(Vector));
        ForwardHistory history = new(source);
        long stopped = 0;

        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, Vector.Ledger.First, Vector.Ledger.Last, TestContext.Current.CancellationToken))
        {
            // The middle of the vector's second ledger: half of it is still ahead of the walk.
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 150)
            {
                stopped = tx.LedgerIndex;
                break;
            }
        }

        List<LedgerTransaction> next = await WalkAsync(history, stopped + 1);

        Assert.Equal(Vectors.Ledger(Vector).Where(tx => tx.LedgerIndex >= stopped + 1 && tx.LedgerIndex <= Vector.Ledger.Last), next);
        Assert.Equal(1, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task Another_writer_or_range_starts_over()
    {
        FakeLedgerSource source = new FakeLedgerSource().AddRange(Vectors.Ledger(Vector));
        ForwardHistory history = new(source);
        await WalkAsync(history, Vector.Ledger.First);

        await foreach (LedgerTransaction _ in history.WalkAsync("rSomebodyElse", Vector.Ledger.First, Vector.Ledger.Last, TestContext.Current.CancellationToken))
        {
        }

        List<LedgerTransaction> shorter = [];
        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, Vector.Ledger.First, Vector.Ledger.First + 1, TestContext.Current.CancellationToken))
        {
            shorter.Add(tx);
        }

        Assert.Equal(200, shorter.Count);
        Assert.Equal(3, source.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_source_that_refuses_the_walk_as_it_is_asked_for_leaves_nothing_behind()
    {
        List<LedgerTransaction> ledger = Vectors.Ledger(Vector);
        FakeLedgerSource source = new FakeLedgerSource().AddRange(ledger);
        ForwardHistory history = new(source);

        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, Vector.Ledger.First, Vector.Ledger.Last, TestContext.Current.CancellationToken))
        {
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) == 150) break;
        }

        // The fake refuses a call outright, before its first record, as a node that is down does.
        source.AccountTransactionsFailure = new XrplFileException(XrplFileError.NodeUnreachable, "refused");
        await Assert.ThrowsAsync<XrplFileException>(() => WalkAsync(history, Vector.Ledger.First + 5));
        source.AccountTransactionsFailure = null;
        List<LedgerTransaction> after = await WalkAsync(history, Vector.Ledger.First + 5);

        Assert.Equal(ledger.Where(tx => tx.LedgerIndex >= Vector.Ledger.First + 5 && tx.LedgerIndex <= Vector.Ledger.Last), after);
    }
}
