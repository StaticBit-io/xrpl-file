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

    public IReadOnlyList<AccountRow> Rows { get; private set; } = [];

    /// <summary>Why the last read from a node came back with nothing, when it did.</summary>
    public string? Message { get; private set; }

    /// <summary>Re-reads the vault. Contacts no node: a list must be drawable with no network at all.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

        List<AccountRow> rows = [];
        foreach (VaultEntryInfo info in listed.Where(entry => entry.Kind == VaultEntryKind.Generated))
        {
            // A row already refreshed from a node keeps what it learned; a new one starts as a key
            // with no account, which is what an unfunded address is.
            rows.Add(_rows.TryGetValue(info.Reference, out AccountRow? known) && info.Closure is null
                ? known with { Label = info.Label, Closure = info.Closure }
                : new AccountRow(
                    info.Reference, info.Label, info.Address, info.Network,
                    info.Closure is null ? AccountStage.KeyOnly : AccountStage.Closed,
                    0, info.DeletableFromLedger, null, info.Closure));
        }

        _rows.Clear();
        foreach (AccountRow row in rows) _rows[row.Reference] = row;

        Rows = rows;
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

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(row.Network, cancellationToken).ConfigureAwait(false);

            AccountState state = await connection.Source
                .AccountStateAsync(row.Address, cancellationToken).ConfigureAwait(false);

            long current = await connection.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
            uint sequence = await connection.Sink
                .SequenceAsync(row.Address, validated: true, cancellationToken).ConfigureAwait(false);

            long deletableFrom = sequence + ThrowawayAccount.DeletionSequenceDelta;
            long remaining = Math.Max(0, deletableFrom - current);

            AccountStage stage = state.BalanceDrops == 0
                ? AccountStage.KeyOnly
                : remaining == 0 ? AccountStage.Closable : AccountStage.Funded;

            AccountRow updated = row with
            {
                Stage = stage,
                BalanceDrops = state.BalanceDrops,
                DeletableFromLedger = deletableFrom,
                LedgersRemaining = remaining,
            };

            _rows[reference] = updated;
            Rows = [.. Rows.Select(existing => existing.Reference == reference ? updated : existing)];

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
    }
}
