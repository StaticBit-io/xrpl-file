using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The Accounts tab's list (design 4.1). Listing asks the vault only, so the list draws with no
/// network at all. A row learns its balance and its deletable ledger from a node afterwards: for
/// every row once when the screen opens, with one connection per network
/// (<see cref="RefreshAllFromLedgerAsync"/>), and for one row when somebody presses Check.
/// </summary>
public sealed class AccountsModel(IKeyVault vault, IConnectionFactory connections)
{
    /// <summary>What the Balance column says while a node is being asked.</summary>
    public const string CheckingBalance = "Checking…";

    /// <summary>What the Balance column says when the last read of the row failed.</summary>
    public const string UnreadBalance = "Not available. Press Check to try again.";

    /// <summary>
    /// Why a read from a node came back with nothing. Its own sentence, not the publication one:
    /// a balance check spends nothing, so "nothing was spent" would answer a question nobody asked.
    /// </summary>
    public const string ReadFailedMessage =
        "The node did not answer, so the balance could not be read. Check the node URL in Settings, then press Check to try again.";

    // One gate for _rows, _checking and Rows: the model is a singleton, its continuations run on
    // pool threads (ConfigureAwait(false)) while the renderer and Check clicks touch the same state.
    // Never held across an await.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AccountRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);

    public IReadOnlyList<AccountRow> Rows { get; private set; } = [];

    /// <summary>Why the last read from a node came back with nothing, when it did.</summary>
    public string? Message { get; private set; }

    /// <summary>
    /// Whether <see cref="RefreshFromLedgerAsync"/> or <see cref="RefreshAllFromLedgerAsync"/>
    /// currently has a network round trip in flight for this reference. The Accounts screen reads
    /// this to disable a row's own Check button rather than tracking "which row I last clicked" as
    /// local, untestable state of its own.
    /// </summary>
    public bool IsChecking(string reference)
    {
        lock (_gate) return _checking.Contains(reference);
    }

    /// <summary>Re-reads the vault. Contacts no node: a list must be drawable with no network at all.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

            // Built and committed under one hold of the gate: a read that lands between "what the
            // rows knew" and "the new list" would otherwise be overwritten by the older copy.
            lock (_gate)
            {
                List<AccountRow> rows = [];
                foreach (VaultEntryInfo info in listed.Where(entry => entry.Kind == VaultEntryKind.Generated))
                {
                    // A row already refreshed from a node keeps what it learned. A new one — or one no
                    // node has ever been asked about — starts as Unknown: a cached DeletableFromLedger may
                    // still ride along for display (it is what it is for), but it is not an answer, and
                    // claiming KeyOnly from the vault alone would say "nothing on the ledger" about an
                    // account that cache could only exist for if something had once been funded. Closed is
                    // the one stage the vault gets to declare outright — a closure is a fact it owns, and
                    // no node is needed to confirm it.
                    rows.Add(_rows.TryGetValue(info.Reference, out AccountRow? known) && info.Closure is null
                        ? known with { Label = info.Label, Closure = info.Closure }
                        : new AccountRow(
                            info.Reference, info.Label, info.Address, info.Network,
                            info.Closure is null ? AccountStage.Unknown : AccountStage.Closed,
                            null, info.DeletableFromLedger, null, info.Closure));
                }

                _rows.Clear();
                foreach (AccountRow row in rows) _rows[row.Reference] = row;

                Rows = rows;
            }
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Asks the node about one account: what it holds, and from which ledger it can be closed. A
    /// closed account is not asked about — there is nothing on the ledger to ask.
    /// </summary>
    public async Task RefreshFromLedgerAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;

        AccountRow? row;
        lock (_gate)
        {
            row = OpenRowLocked(reference);

            // A row someone is already checking is theirs to finish: this call's finally would
            // clear their hold, and reading it twice would race two writes to the same row.
            if (row is null || !_checking.Add(reference)) return;
        }

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(row.Network, cancellationToken).ConfigureAwait(false);

            await CheckOnAsync(connection, new LedgerReading(connection), row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReadFailure(ex, cancellationToken))
        {
            Fail(reference);
        }
        finally
        {
            lock (_gate) _checking.Remove(reference);
        }
    }

    /// <summary>
    /// Asks the node about every account that is not closed, once, with one connection per network.
    /// A network that cannot be reached marks only its own rows <see cref="AccountRow.CheckFailed"/>;
    /// the others carry on. Cancelling stops the reads and leaves no row checking.
    /// </summary>
    public async Task RefreshAllFromLedgerAsync(CancellationToken cancellationToken)
    {
        // Message is not cleared here: the screen calls this right after RefreshAsync, and a vault
        // error that call just set must reach the person.

        // A row someone is already checking is theirs to finish; reading it twice would race two
        // writes to the same row. Every other row reads as checking from the start, not as its
        // group's turn comes, so the list says honestly that all of it is being read.
        List<AccountRow> targets;
        HashSet<string> held = new(StringComparer.Ordinal);
        lock (_gate)
        {
            targets = [.. Rows.Where(row =>
                row.Stage != AccountStage.Closed && !_checking.Contains(row.Reference))];

            foreach (AccountRow row in targets)
            {
                _checking.Add(row.Reference);
                held.Add(row.Reference);
            }
        }

        // Releases a row this read still holds; a row it let go of may be a Check click's by now.
        // The networks are read at the same time, so `held` is touched under the gate as well.
        void Release(string reference)
        {
            lock (_gate)
            {
                if (held.Remove(reference)) _checking.Remove(reference);
            }
        }

        // Concurrently: a node that never answers (the connect has no timeout of its own but the
        // socket's) must not keep every other network's rows at "Checking…". The groups hold
        // disjoint rows, and every write to a row goes through the gate.
        try
        {
            await Task.WhenAll(targets
                .GroupBy(row => row.Network, StringComparer.Ordinal)
                .Select(group => ReadNetworkAsync(group.Key, [.. group], Release, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            string[] leftover;
            lock (_gate) leftover = [.. held];

            foreach (string reference in leftover) Release(reference);
        }
    }

    /// <summary>One network's rows over one connection. A connection that cannot be made marks the rows and ends here.</summary>
    private async Task ReadNetworkAsync(
        string network, List<AccountRow> rows, Action<string> release, CancellationToken cancellationToken)
    {
        ILedgerConnection connection;
        try
        {
            connection = await connections.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReadFailure(ex, cancellationToken))
        {
            foreach (AccountRow row in rows)
            {
                Fail(row.Reference);
                release(row.Reference);
            }

            return;
        }

        await using (connection.ConfigureAwait(false))
        {
            LedgerReading ledger = new(connection);

            foreach (AccountRow row in rows)
            {
                // Re-read: the list may have been rebuilt since the snapshot the caller took.
                AccountRow? latest;
                lock (_gate) latest = OpenRowLocked(row.Reference);

                if (latest is not null)
                {
                    await CheckOnAsync(connection, ledger, latest, cancellationToken).ConfigureAwait(false);
                }

                release(row.Reference);
            }
        }
    }

    /// <summary>
    /// What the Balance column says for a row: the balance once a node has answered, and nothing
    /// where another column already says it (a closed account, a key with no account yet) or where
    /// no answer has come yet.
    /// </summary>
    public string BalanceTextOf(AccountRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (IsChecking(row.Reference)) return CheckingBalance;
        if (row.Stage == AccountStage.Closed) return string.Empty;
        if (row.CheckFailed) return UnreadBalance;
        if (row.BalanceDrops is null || row.Stage == AccountStage.KeyOnly) return string.Empty;

        return XrpAmount.Format(row.BalanceDrops.Value);
    }

    /// <summary>What the Status column says for a row no node has answered about.</summary>
    public const string NotCheckedStatus = "Not checked yet";

    /// <summary>
    /// What the Status column says for a row of the <see cref="AccountStage.Unknown"/> stage: that
    /// nothing has asked a node yet, and nothing else. While a read is under way, and after one has
    /// failed, the Balance column already says so, and a second line would contradict it.
    /// </summary>
    public string UnknownStatusTextOf(AccountRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.Stage == AccountStage.Unknown && !row.CheckFailed && !IsChecking(row.Reference)
            ? NotCheckedStatus
            : string.Empty;
    }

    /// <summary>
    /// Whether the Status column says anything for the row yet. While a node is being asked the
    /// Balance column says "Checking…", and the status left over from the previous read would be a
    /// second, older answer to the same question.
    /// </summary>
    public bool ShowsStatus(AccountRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return !IsChecking(row.Reference);
    }

    private void Apply(string reference, Func<AccountRow, AccountRow> change)
    {
        // Applied to the row as it is now, not as the caller saw it: a list rebuilt meanwhile may
        // carry a renamed label that an older copy would put back.
        lock (_gate)
        {
            if (!_rows.TryGetValue(reference, out AccountRow? current)) return;

            AccountRow updated = change(current);
            _rows[reference] = updated;
            Rows = [.. Rows.Select(existing =>
                string.Equals(existing.Reference, reference, StringComparison.Ordinal) ? updated : existing)];
        }
    }

    /// <summary>The row for this reference unless it is gone or closed. Caller holds <c>_gate</c>.</summary>
    private AccountRow? OpenRowLocked(string reference) =>
        _rows.TryGetValue(reference, out AccountRow? row) && row.Stage != AccountStage.Closed ? row : null;

    /// <summary>
    /// A failure to read one account or connect to its network, as opposed to the screen being left:
    /// any exception, unless the caller's own token is what ended it. A connect timeout surfaces as an
    /// <see cref="OperationCanceledException"/> that is not the screen closing.
    /// </summary>
    private static bool IsReadFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is XrplFileException || !cancellationToken.IsCancellationRequested;

    private void Fail(string reference)
    {
        // Whatever the exception, the person's next step is the same: a read returned nothing, so
        // check the node. The publication sentences describe runs and spending, none of which a
        // read has.
        Message = ReadFailedMessage;

        Apply(reference, current => current with { CheckFailed = true });
    }

    /// <summary>
    /// The ledger a connection is at, asked for once: every funded row of a read counts its
    /// countdown from the same number, and a public node answers a request in about half a second.
    /// </summary>
    private sealed class LedgerReading(ILedgerConnection connection)
    {
        private long? _ledger;

        public async Task<long> CurrentAsync(CancellationToken cancellationToken) =>
            _ledger ??= await connection.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckOnAsync(ILedgerConnection connection, LedgerReading ledger, AccountRow row, CancellationToken cancellationToken)
    {
        try
        {
            AccountState state = await connection.Source
                .AccountStateAsync(row.Address, cancellationToken).ConfigureAwait(false);

            if (state.BalanceDrops == 0)
            {
                // Nobody has funded this address yet -- the state a freshly generated key is in
                // until somebody pays into it, and the ordinary reason Check gets pressed at all.
                // The account does not exist on the ledger, and XrplClientLedgerSink.SequenceAsync
                // has no absent-account handling of its own -- unlike ILedgerSource, whose
                // AskOrAbsentAsync treats actNotFound as an answer -- so asking it for this
                // account's sequence would let a RippledException escape uncaught instead of the
                // KeyOnly stage this row already has. There is also nothing to compute: an account
                // that does not exist has no sequence to count 254 ledgers from, so a cached
                // countdown from an earlier session is retired here rather than carried forward as
                // if it still meant something (design G8).
                Apply(row.Reference, latest => latest with
                {
                    Stage = AccountStage.KeyOnly,
                    BalanceDrops = 0,
                    DeletableFromLedger = null,
                    LedgersRemaining = null,
                    CheckFailed = false,
                });

                return;
            }

            long currentLedger = await ledger.CurrentAsync(cancellationToken).ConfigureAwait(false);
            uint sequence = await connection.Sink
                .SequenceAsync(row.Address, validated: true, cancellationToken).ConfigureAwait(false);

            long deletableFrom = sequence + ThrowawayAccount.DeletionSequenceDelta;
            long remaining = Math.Max(0, deletableFrom - currentLedger);

            // Closable here means only that the sequence threshold has passed — it does not repeat
            // the balance-versus-fee, destination-exists and destination-refuses checks
            // ThrowawayAccount.PreviewDeletionAsync also applies. That is deliberate: the preflight
            // on the closing screen is the authority on whether a close will actually go through,
            // and it will answer NotYet with the real reason when this stage alone would have said
            // yes. This is a rough "worth trying" signal for the list, not a promise.
            AccountStage stage = remaining == 0 ? AccountStage.Closable : AccountStage.Funded;

            Apply(row.Reference, latest => latest with
            {
                Stage = stage,
                BalanceDrops = state.BalanceDrops,
                DeletableFromLedger = deletableFrom,
                LedgersRemaining = remaining,
                CheckFailed = false,
            });

            // Kept on the entry so the next launch can say roughly how long is left before the node
            // has answered. Never read back to decide anything (design G8). Skipped when the entry
            // already holds this value: every write re-seals the vault file, and a list that reads
            // every row on open would otherwise rewrite it each time the screen is shown.
            if (deletableFrom != row.DeletableFromLedger)
            {
                await vault.UpdateAsync(
                    row.Reference,
                    new VaultEntryEdit(LastKnownSequence: sequence, DeletableFromLedger: deletableFrom),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
        catch (Exception ex) when (IsReadFailure(ex, cancellationToken))
        {
            Fail(row.Reference);
        }
    }
}
