using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The funding screen (design 4.1): the address to send to, a QR of it, and what the account holds
/// now. It computes no target amount — what a publication needs depends on the file, and C2's
/// estimate screen already answers that exactly, against the real bytes (design G10).
/// </summary>
public sealed class FundingModel(IKeyVault vault, IConnectionFactory connections, IClipboardService clipboard)
{
    /// <summary>
    /// How long <see cref="PollAsync"/> waits between two lookups. Every lookup opens a new WebSocket
    /// and public nodes rate-limit per address, so this is a floor to stay above, not a default to
    /// shorten for responsiveness: the "Check now" button is the impatient person's way in.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshesPending;

    public string? Address { get; private set; }

    /// <summary>
    /// What the QR carries: the address, and nothing else. No scheme, no amount, no destination tag
    /// — a wallet scanning it must be free to send whatever the person decided, and an amount baked
    /// in here would be this application guessing a number it does not have.
    /// </summary>
    public string? QrPayload => Address;

    public string? Network { get; private set; }

    public long BalanceDrops { get; private set; }

    /// <summary>Whether the ledger carries this account at all. An address nobody funded is not on it.</summary>
    public bool AccountExists { get; private set; }

    public string? Message { get; private set; }

    /// <summary>
    /// The reference the data above actually belongs to — set only once <see cref="SelectAsync"/>
    /// has resolved it, and cleared whenever a select fails to. <see cref="Address"/> alone cannot
    /// say this: a failed lookup deliberately leaves it holding whatever the previous selection put
    /// there (see <see cref="SelectAsync"/>'s own remarks), so a caller that wants to know "is this
    /// really the account I just opened, or is it what an earlier screen left behind" has to compare
    /// this property to the reference it asked for, not read <see cref="Address"/> on faith.
    /// </summary>
    public string? CurrentReference { get; private set; }

    /// <summary>
    /// Whether a lookup is running or queued behind one. The screen disables "Check now" on it, and
    /// <see cref="RefreshAsync"/> itself never runs two at once either way.
    /// </summary>
    public bool IsRefreshing => Volatile.Read(ref _refreshesPending) > 0;

    /// <summary>
    /// Whether the screen should keep asking the node: a selection that resolved and whose account
    /// is not on the ledger yet. Once the account exists nothing changes by itself that this screen
    /// shows, so polling stops there.
    /// </summary>
    public bool ShouldPoll => CurrentReference is not null && !AccountExists;

    /// <summary>
    /// Resolves <paramref name="reference"/> before touching anything shown, so a failed or empty
    /// lookup leaves the previous selection on screen rather than blanking it. The address may be
    /// the one thing a person is halfway through copying when, say, the vault locks itself between
    /// two visits to this screen — a lookup that cannot answer must not be the reason it disappears.
    /// <see cref="CurrentReference"/> still moves to <see langword="null"/> on a failure, though: the
    /// screen keeps showing the previous address, but nothing here vouches for it belonging to
    /// <paramref name="reference"/> any more.
    /// </summary>
    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;

        VaultEntryInfo? entry;
        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            entry = listed.FirstOrDefault(info => string.Equals(info.Reference, reference, StringComparison.Ordinal));
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            CurrentReference = null;
            return;
        }

        if (entry is null)
        {
            Message = "That key is no longer in the vault.";
            CurrentReference = null;
            return;
        }

        Address = entry.Address;
        Network = entry.Network;
        BalanceDrops = 0;
        AccountExists = false;
        CurrentReference = reference;
    }

    /// <summary>
    /// Does nothing when <see cref="CurrentReference"/> is null: either nothing has resolved a
    /// selection yet, or the last <see cref="SelectAsync"/> failed and left <see cref="Address"/>
    /// holding a previous selection this method has no business querying the ledger about — and,
    /// just as importantly, clearing <see cref="Message"/> in that state would erase the very
    /// explanation the failed select just set.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshesPending);
        try
        {
            // One lookup at a time: a poll tick and a "Check now" click landing together would
            // otherwise open two sockets for one answer. The second waits and then reads the
            // selection as it is by then, not as it was when it was asked for.
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _refreshesPending);
        }
    }

    /// <summary>
    /// Asks the node again every <see cref="PollInterval"/> for as long as <see cref="ShouldPoll"/>
    /// holds, and returns once the account exists, the selection stops resolving, or
    /// <paramref name="cancellationToken"/> is cancelled. <paramref name="changed"/> runs before and
    /// after every lookup so a screen can show that one is in flight. <paramref name="delay"/>
    /// replaces <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in tests.
    /// </summary>
    public async Task PollAsync(
        Func<Task>? changed, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        try
        {
            while (ShouldPoll)
            {
                await delay(PollInterval, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // A "Check now" during the wait may have found the account: no lookup is left to make.
                if (!ShouldPoll) break;

                if (changed is not null) await changed().ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (changed is not null) await changed().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (CurrentReference is null || Address is null || Network is null) return;

        Message = null;

        // Captured together, at entry, before any await: reference is what the guard below checks
        // against, and address is what the query itself is issued for. Reading Address live after
        // the first await (ConnectAsync) -- as this used to do -- pins neither: a select for a
        // different account resolving during that first await, then reverting back to this one
        // during the second, would have this method read the OTHER account's address for the query
        // while the guard, checked only after, sees the reference has already settled back to this
        // one and passes -- the wrong balance landing under the right address. Capturing address
        // here closes that the same way capturing reference already closes the reference race.
        string reference = CurrentReference;
        string address = Address;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(Network, cancellationToken).ConfigureAwait(false);

            AccountState state = await connection.Source
                .AccountStateAsync(address, cancellationToken).ConfigureAwait(false);

            // The selection can change while this call is in flight -- a later SelectAsync, for a
            // different reference, resolving before this one's own network round trip does. Writing
            // this answer in that case would land the PREVIOUS account's balance under whatever
            // address is now on screen, which is exactly the failure CurrentReference exists to let
            // this method notice and refuse.
            if (!string.Equals(CurrentReference, reference, StringComparison.Ordinal)) return;

            BalanceDrops = state.BalanceDrops;
            AccountExists = state.BalanceDrops > 0;
        }
        catch (XrplFileException ex)
        {
            if (!string.Equals(CurrentReference, reference, StringComparison.Ordinal)) return;

            Message = PublishErrorMessages.For(ex);
        }
    }

    public Task CopyAddressAsync(CancellationToken cancellationToken) =>
        Address is null ? Task.CompletedTask : clipboard.CopyAsync(Address, cancellationToken);
}
