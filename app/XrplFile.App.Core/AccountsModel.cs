using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The Accounts tab's list (design 4.1). Listing asks the vault only; a row learns its balance and
/// its deletable ledger when somebody asks for that row, because every account may be on a different
/// network and opening one connection per row to draw a list would make the list slower than it is
/// useful.
/// </summary>
public sealed class AccountsModel(IKeyVault vault, IConnectionFactory connections)
{
    private readonly Dictionary<string, AccountRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);

    public IReadOnlyList<AccountRow> Rows { get; private set; } = [];

    /// <summary>Why the last read from a node came back with nothing, when it did.</summary>
    public string? Message { get; private set; }

    /// <summary>
    /// Whether <see cref="RefreshFromLedgerAsync"/> currently has a network round trip in flight for
    /// this reference. The Accounts screen reads this to disable a row's own Check button rather than
    /// tracking "which row I last clicked" as local, untestable state of its own.
    /// </summary>
    public bool IsChecking(string reference) => _checking.Contains(reference);

    /// <summary>Re-reads the vault. Contacts no node: a list must be drawable with no network at all.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

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

        if (!_rows.TryGetValue(reference, out AccountRow? row) || row.Stage == AccountStage.Closed) return;

        void Apply(AccountRow updated)
        {
            _rows[reference] = updated;
            Rows = [.. Rows.Select(existing =>
                string.Equals(existing.Reference, reference, StringComparison.Ordinal) ? updated : existing)];
        }

        _checking.Add(reference);
        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(row.Network, cancellationToken).ConfigureAwait(false);

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
                Apply(row with
                {
                    Stage = AccountStage.KeyOnly,
                    BalanceDrops = 0,
                    DeletableFromLedger = null,
                    LedgersRemaining = null,
                });

                return;
            }

            long current = await connection.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
            uint sequence = await connection.Sink
                .SequenceAsync(row.Address, validated: true, cancellationToken).ConfigureAwait(false);

            long deletableFrom = sequence + ThrowawayAccount.DeletionSequenceDelta;
            long remaining = Math.Max(0, deletableFrom - current);

            // Closable here means only that the sequence threshold has passed — it does not repeat
            // the balance-versus-fee, destination-exists and destination-refuses checks
            // ThrowawayAccount.PreviewDeletionAsync also applies. That is deliberate: the preflight
            // on the closing screen is the authority on whether a close will actually go through,
            // and it will answer NotYet with the real reason when this stage alone would have said
            // yes. This is a rough "worth trying" signal for the list, not a promise.
            AccountStage stage = remaining == 0 ? AccountStage.Closable : AccountStage.Funded;

            Apply(row with
            {
                Stage = stage,
                BalanceDrops = state.BalanceDrops,
                DeletableFromLedger = deletableFrom,
                LedgersRemaining = remaining,
            });

            // Kept on the entry so the next launch can say roughly how long is left before the node
            // has answered. Never read back to decide anything (design G8).
            await vault.UpdateAsync(
                reference,
                new VaultEntryEdit(LastKnownSequence: sequence, DeletableFromLedger: deletableFrom),
                cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
        finally
        {
            _checking.Remove(reference);
        }
    }
}
