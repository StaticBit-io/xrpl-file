using System.Runtime.CompilerServices;
using Xunit;

namespace XrplFile.Tests;

public class BatchRoundStartTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stale_snapshot_at_the_start_of_a_round_is_not_planned_from()
    {
        // Memo 3's inner ends in tec, so a second round sends it again as a solo. The node answering
        // that round's first snapshots is one that is behind: it reports the ledger and sequences of
        // the first round's start, which the first round has long consumed.
        BatchTestNode node = new(sink => sink.InnerResult[MemoLayout.ChunkMarkerHex(3)] = "tecFAKE");
        BatchStrategy? strategy = null;
        StaleAfterSettleSource source = new(node.Ledger, () => strategy is { } running
            && running.Trace.Any(entry => entry.StartsWith("settled:", StringComparison.Ordinal))
            && running.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)) == 1, staleAnswers: 2);
        await using BatchStrategy running = strategy = new BatchStrategy(
            node.Sink, source, node.Writer, node.Payer, BatchTestNode.Budget, node.Fence, BatchTestNode.FastOptions);

        IReadOnlyList<SubmittedMemo> landed = await running.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        Assert.Equal(16, landed.Count);
        Assert.Equal(2, source.StaleServed);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.Answer == "tefPAST_SEQ");
        Assert.Equal(2, running.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)));
        Assert.Equal(3, running.Trace.Count(entry => entry.StartsWith("register:", StringComparison.Ordinal)));
        Assert.Single(node.Sink.Transmissions, transmission => transmission.TransactionType == "AccountSet");
        Assert.DoesNotContain(running.Trace, entry => entry.StartsWith("halt:", StringComparison.Ordinal));
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_node_that_stays_behind_fails_the_run_instead_of_planning_from_it()
    {
        BatchTestNode node = new(sink => sink.InnerResult[MemoLayout.ChunkMarkerHex(3)] = "tecFAKE");
        BatchStrategy? strategy = null;
        StaleAfterSettleSource source = new(node.Ledger, () => strategy is { } running
            && running.Trace.Any(entry => entry.StartsWith("settled:", StringComparison.Ordinal))
            && running.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)) == 1, staleAnswers: int.MaxValue);
        await using BatchStrategy running = strategy = new BatchStrategy(
            node.Sink, source, node.Writer, node.Payer, BatchTestNode.Budget, node.Fence,
            BatchTestNode.FastOptions with { NodeOutageLimit = TimeSpan.FromMilliseconds(300) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => running.SubmitAllAsync(BatchTestNode.Memos(16), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.TransactionType == "AccountSet");
    }

    /// <summary>
    /// A source whose snapshots answer, while <c>behind</c> says so, with the first snapshot it ever
    /// gave: an older validated ledger and the sequences from before anything was sent.
    /// </summary>
    private sealed class StaleAfterSettleSource(FakeLedgerSource inner, Func<bool> behind, int staleAnswers) : ILedgerSource
    {
        private LedgerSnapshot? _first;
        private int _served;

        public int StaleServed => Volatile.Read(ref _served);

        public async Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
        {
            if (_first is { } first && behind() && Volatile.Read(ref _served) < staleAnswers)
            {
                Interlocked.Increment(ref _served);
                return first;
            }

            LedgerSnapshot snapshot = await inner.SnapshotAsync(accounts, cancellationToken).ConfigureAwait(false);
            _first ??= snapshot;
            return snapshot;
        }

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (LedgerTransaction tx in inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return tx;
            }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
            inner.TransactionAsync(hash, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
            inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) =>
            inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
            inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
            inner.IsAmendmentEnabledAsync(name, cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
            inner.DestinationAsync(account, cancellationToken);
    }
}
