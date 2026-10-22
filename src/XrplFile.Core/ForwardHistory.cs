namespace XrplFile;

/// <summary>
/// One read's forward walks over a writer's history, each taking from the node only what the walk
/// before it did not.
///
/// A block is read from its own first ledger, and a block usually starts in the ledger the block
/// before it ended in: on testnet a Batch ledger holds about 2,048 chunks, two blocks. Walking each
/// block afresh read that ledger twice and paged about 9,600 records for a 7,124-chunk file. Here a
/// walk that stops keeps its place, and the next walk replays what this holds from its own first
/// ledger and then carries the stopped walk on, so a file read block by block in order takes each
/// record from the node once.
///
/// What a walk yields is exactly what a fresh <see cref="ILedgerSource.AccountTransactionsAsync"/>
/// from its first ledger would yield, in the same canonical order, so the chunk rule applied to it
/// is unchanged. That rests on validated history not changing between two walks of one read.
///
/// Memory is bounded by <c>maxRecords</c>: a walk starting further on drops everything before its
/// first ledger, and once more than that many records are held the oldest ledgers are let go
/// whole. A walk from a ledger that was let go, from another writer, to another last ledger or
/// under another token starts over with a fresh walk. A walk that fails forgets everything.
///
/// One walk at a time takes part. A walk started while another is in progress goes to the node
/// directly, so callers that read blocks concurrently still get correct, if unshared, walks.
/// </summary>
internal sealed class ForwardHistory(ILedgerSource source, int maxRecords = ForwardHistory.DefaultMaxRecords)
{
    /// <summary>
    /// Four blocks' worth: two testnet Batch ledgers of chunks. A record carries up to 2,000
    /// characters of memo hex, so this holds about 17 MB at the most.
    /// </summary>
    public const int DefaultMaxRecords = 4 * FileTree.BlockChunks;

    private readonly ILedgerSource _source = source;
    private readonly int _maxRecords = maxRecords;

    // Every record a walk from _from would yield, in canonical order, up to where the walk has
    // got: to its end when _cursor is null and _account is set, otherwise to the cursor's place.
    private readonly List<LedgerTransaction> _held = [];
    private string? _account;
    private long _toLedger;
    private long _from;

    // The ledger of the last record the cursor gave; the rest of that ledger may still be ahead.
    private long _position;
    private IAsyncEnumerator<LedgerTransaction>? _cursor;
    private CancellationToken _cursorToken;
    private int _walking;

    /// <summary>How many records it holds now.</summary>
    public int Held => _held.Count;

    /// <summary>
    /// The account's transactions in [<paramref name="fromLedger"/>, <paramref name="toLedger"/>],
    /// ascending in canonical order, as <see cref="ILedgerSource.AccountTransactionsAsync"/> answers
    /// a forward walk.
    /// </summary>
    public async IAsyncEnumerable<LedgerTransaction> WalkAsync(
        string account, long fromLedger, long toLedger, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A range that ends before it starts is the node's to refuse, as it always was.
        if (fromLedger > toLedger || Interlocked.CompareExchange(ref _walking, 1, 0) != 0)
        {
            await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(account, fromLedger, toLedger, forward: true, cancellationToken).ConfigureAwait(false))
            {
                yield return tx;
            }

            yield break;
        }

        try
        {
            if (!Covers(account, fromLedger, toLedger, cancellationToken))
            {
                await StartOverAsync(account, fromLedger, toLedger, cancellationToken).ConfigureAwait(false);
            }

            int before = _held.FindIndex(tx => tx.LedgerIndex >= fromLedger);
            _held.RemoveRange(0, before < 0 ? _held.Count : before);
            _from = fromLedger;

            // Replayed by index: nothing is added to or dropped from _held until the replay is done.
            for (int i = 0; i < _held.Count; i++)
            {
                yield return _held[i];
            }

            while (_cursor is not null)
            {
                LedgerTransaction? tx = await NextAsync().ConfigureAwait(false);
                if (tx is null) break;

                _position = tx.LedgerIndex;

                // Only when this walk starts one past where the last one stopped: the rest of the
                // stopped walk's last ledger is behind this one's first.
                if (tx.LedgerIndex < fromLedger) continue;

                _held.Add(tx);
                LetGo();
                yield return tx;
            }
        }
        finally
        {
            Volatile.Write(ref _walking, 0);
        }
    }

    private bool Covers(string account, long fromLedger, long toLedger, CancellationToken cancellationToken)
    {
        if (!string.Equals(_account, account, StringComparison.Ordinal) || _toLedger != toLedger || fromLedger < _from)
        {
            return false;
        }

        if (_cursor is null) return true;

        return fromLedger <= _position + 1 && cancellationToken.Equals(_cursorToken);
    }

    private async Task StartOverAsync(string account, long fromLedger, long toLedger, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<LedgerTransaction>? stale = _cursor;
        _cursor = null;
        _account = null;
        _held.Clear();

        if (stale is not null)
        {
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        // A source may refuse the walk as it is asked for rather than on the first record; until
        // the cursor exists nothing here may claim to hold anything.
        IAsyncEnumerator<LedgerTransaction> cursor =
            _source.AccountTransactionsAsync(account, fromLedger, toLedger, forward: true, cancellationToken).GetAsyncEnumerator(cancellationToken);

        _cursor = cursor;
        _account = account;
        _toLedger = toLedger;
        _from = fromLedger;
        _position = fromLedger - 1;
        _cursorToken = cancellationToken;
    }

    /// <summary>The cursor's next record, or null at the end of its range, when it is let go.</summary>
    private async Task<LedgerTransaction?> NextAsync()
    {
        bool more;

        try
        {
            more = await _cursor!.MoveNextAsync().ConfigureAwait(false);
        }
        catch
        {
            // Where the failed walk stopped inside its last ledger is not known, so nothing it
            // read is kept. An enumerator that threw is finished; it is not disposed again.
            _cursor = null;
            _account = null;
            _held.Clear();
            throw;
        }

        if (more) return _cursor.Current;

        await _cursor.DisposeAsync().ConfigureAwait(false);
        _cursor = null;
        return null;
    }

    /// <summary>
    /// Drops the oldest ledgers whole while more than the cap is held, never the ledger the walk
    /// is in, so what is left is still every record from <see cref="_from"/> on.
    /// </summary>
    private void LetGo()
    {
        while (_held.Count > _maxRecords && _held[0].LedgerIndex < _position)
        {
            long oldest = _held[0].LedgerIndex;
            int end = _held.FindIndex(tx => tx.LedgerIndex != oldest);
            _held.RemoveRange(0, end);
            _from = oldest + 1;
        }
    }
}
