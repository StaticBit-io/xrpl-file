using XrplFile.App.Core.Localization;
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Closing an account (design 5), a generated throwaway or an imported key alike. The most careful screen in this application: an
/// <c>AccountDelete</c> sends everything the account holds and cannot be undone, and its fee is one
/// owner reserve, which a refused submission keeps.
///
/// Nothing here ever chooses a destination (design G3), and nothing submits until a preflight has
/// said it can succeed (design G5).
///
/// This is a singleton, and a close outlives the screen that started it by as long as the node takes
/// to answer, so every call works on the <see cref="CloseState"/> that was selected when it began and
/// never on whatever is selected by the time an await returns. A close's outcome therefore lands on
/// its own account, and a state that has attempted a close is kept (see <see cref="SelectAsync"/>), so
/// the transaction hash and the "may already have been closed" warning are still there when that
/// account's screen is opened again — they cannot be lost and cannot show on another account's screen.
/// </summary>
public sealed class CloseAccountModel(IKeyVault vault, IConnectionFactory connections, TimeProvider time)
{
    /// <summary>What the screen reads. Replaced, never reset in place, by <see cref="SelectAsync"/>.</summary>
    private CloseState _state = new();

    /// <summary>
    /// Every state that has attempted a close, by reference, kept for the life of the process: a
    /// handful of small objects, and the only place a close whose screen is gone can report to.
    /// </summary>
    private readonly Dictionary<string, CloseState> _attempts = new(StringComparer.Ordinal);

    /// <summary>The state whose close is in flight, if any. At most one: see <see cref="CloseAsync"/>.</summary>
    private CloseState? _closing;

    /// <summary>The vault's other keys, offered as destinations. Never this account itself.</summary>
    public IReadOnlyList<VaultEntryInfo> Candidates => _state.Candidates;

    /// <summary>
    /// The label of the account being closed — set as soon as <see cref="SelectAsync"/> resolves it,
    /// with no node round trip, so the screen can say which account it is about to destroy before it
    /// has asked anything of a ledger.
    /// </summary>
    public string? AccountLabel => _state.AccountLabel;

    /// <summary>The address of the account being closed. See <see cref="AccountLabel"/>.</summary>
    public string? AccountAddress => _state.AccountAddress;

    /// <summary>What <see cref="ImportedWarning"/> says.</summary>
    public static readonly UiText ImportedWarningText = CloseText.ImportedWarning;

    /// <summary>
    /// An extra line for the confirm step when the account being closed is an imported key, and
    /// <see langword="null"/> for a generated one. An imported key may be an account the person has
    /// had for years, which a generated one never is; closing both is allowed, but only this one
    /// needs telling that the deletion removes something they may still be using. Set with the rest
    /// of the entry, so it is there before any node has been asked.
    /// </summary>
    public UiText? ImportedWarning => _state.Kind == VaultEntryKind.Imported ? ImportedWarningText : null;

    public string? Destination => _state.Destination;

    /// <summary>What the destination holds now — how a person recognises the account they meant (design G6).</summary>
    public long DestinationBalanceDrops => _state.DestinationBalanceDrops;

    /// <summary>What the deletion would do, as the library computed it. Null until a preflight has run.</summary>
    public DeletionResult? Preview => _state.Preview;

    /// <summary>
    /// How many ledgers remain before a <see cref="DeletionOutcome.NotYet"/> preview becomes
    /// closable, read alongside the preflight itself. Null until a preflight has run, and also null
    /// when the preview did not name a <see cref="DeletionResult.DeletableFromLedger"/> to count
    /// down to (design 5's "how long the wait is, not just when it ends" — a ledger number on its
    /// own gives a person only one of the two numbers the subtraction needs).
    /// </summary>
    public long? LedgersRemaining => _state.LedgersRemaining;

    /// <summary>
    /// Roughly how long <see cref="LedgersRemaining"/> is, in words — "not yet" without a sense of
    /// scale is indistinguishable from "never" to someone deciding whether to wait (design 5.2, 6:
    /// "how many ledgers remain and roughly how long that is"). Null under the same conditions
    /// <see cref="LedgersRemaining"/> is. The arithmetic and the wording both live here rather than
    /// in the screen that shows them — a component holds no decisions.
    /// </summary>
    public UiText? TimeRemainingDescription => LedgerTiming.Describe(LedgerTiming.Estimate(LedgersRemaining));

    public UiText? Message => _state.Message;

    /// <summary>The closure this screen recorded, once one happened.</summary>
    public AccountClosure? Closed => _state.Closed;

    public bool CanClose => _state.CanClose;

    /// <summary>Whether a preflight can be run at all — a destination has been named. The screen
    /// reads this rather than comparing <see cref="Destination"/> to null itself.</summary>
    public bool CanPreflight => _state.Destination is not null;

    /// <summary>
    /// Whether an <c>AccountDelete</c> is in flight for any account. The shell reads this to keep its
    /// navigation and Lock disabled: leaving the screen cancels the token the close is awaiting on,
    /// possibly after the signed deletion is already on the wire, so there is nowhere to go until the
    /// node has answered. Seconds, not minutes.
    /// </summary>
    public bool IsClosing => _closing is not null;

    /// <summary>Raised after <see cref="IsClosing"/> changes, and only then.</summary>
    public event Action? ClosingChanged;

    /// <summary>
    /// The reference every property above actually belongs to — set only once <see cref="SelectAsync"/>
    /// has resolved it for that reference, mirroring <see cref="FundingModel.CurrentReference"/> and
    /// for the same reason: a screen freshly mounted for one account must not paint even one frame
    /// built from whatever a previous account's close left behind in this model, which nothing about
    /// a synchronous-looking field reset guarantees on every rendering host. The screen reads this
    /// rather than trusting that a completed <see cref="SelectAsync"/> call means its own data is on
    /// screen.
    /// </summary>
    public string? CurrentReference => _state.Reference;

    /// <summary>
    /// Selects the account the screen is about to show. An account whose close was attempted in this
    /// process gets its own state back, outcome and all, without asking the vault again: that outcome
    /// can be the only trace of a deletion the node accepted. Any other account starts from nothing.
    /// </summary>
    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        if (_attempts.TryGetValue(reference, out CloseState? attempted))
        {
            _state = attempted;
            return;
        }

        // Written to by this call alone, so a second select that replaces _state while the vault is
        // being read cannot be handed this one's half-built fields.
        CloseState state = new();
        _state = state;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            VaultEntryInfo? entry = listed.FirstOrDefault(
                info => string.Equals(info.Reference, reference, StringComparison.Ordinal));

            if (entry is null)
            {
                state.Message = VaultText.ErrorNoSuchEntry;
                return;
            }

            state.Network = entry.Network;
            state.Kind = entry.Kind;
            state.AccountLabel = entry.Label;
            state.AccountAddress = entry.Address;

            // Three ways an offered entry could not actually work as a destination: it is this same
            // account under a second reference, it is on a network this deletion cannot reach, or it
            // has already been closed and so no longer exists to receive anything. The preflight would
            // catch all three and burn no reserve, but a confirmation screen that suggests a
            // destination already known to be unusable is not one worth trusting.
            state.Candidates =
            [
                .. listed.Where(info =>
                    !string.Equals(info.Reference, reference, StringComparison.Ordinal)
                    && !string.Equals(info.Address, entry.Address, StringComparison.Ordinal)
                    && string.Equals(info.Network, entry.Network, StringComparison.Ordinal)
                    && info.Closure is null),
            ];

            // Assigned last, once every field above it has actually been set: a caller comparing
            // this to the reference it asked for is trusting that a match means all of it belongs
            // to that account, not merely that the lookup found an entry.
            state.Reference = reference;
        }
        catch (VaultException ex)
        {
            state.Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Names the destination. Taking a bare address rather than a vault reference is deliberate: the
    /// commonest destination is an exchange or another wallet, which the vault has never heard of.
    /// </summary>
    public void ChooseDestination(string address)
    {
        CloseState state = _state;

        state.Destination = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        state.Preview = null;
        state.LedgersRemaining = null;
        state.DestinationBalanceDrops = 0;
        state.Message = null;

        // Whatever preflight is still waiting on the node was asked about the previous destination.
        state.Generation++;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        CloseState state = _state;

        state.Message = null;
        state.Preview = null;
        state.LedgersRemaining = null;

        if (state.Reference is not { } reference || state.Network is not { } network
            || state.Destination is not { } destination)
        {
            state.Message = CloseText.ChooseDestination;
            return;
        }

        // Taken after the guard above, which returns without asking the node anything. Every write
        // below checks it again: a destination chosen (or a newer preflight started) while this one
        // waited for the node makes its answer a statement about a recipient nobody is looking at, and
        // showing it would put a Ready verdict, and a Close button, on the wrong address.
        int generation = ++state.Generation;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult preview = await account
                .PreviewDeletionAsync(destination, cancellationToken).ConfigureAwait(false);

            AccountState home = await connection.Source
                .AccountStateAsync(destination, cancellationToken).ConfigureAwait(false);

            if (generation != state.Generation) return;

            // Assigned as soon as the node has actually answered both calls a confirmation needs --
            // before the extra read below, which is a nice-to-have on top of a preflight that is
            // already good. A node that answers these two and then drops the countdown read must not
            // lose the preflight it already gave: the catch around this whole method would otherwise
            // leave Preview null and show a generic error in place of a confirmation the node had
            // already earned.
            state.Preview = preview;
            state.DestinationBalanceDrops = home.BalanceDrops;

            if (preview.Outcome != DeletionOutcome.Ready)
            {
                state.Message = CloseErrorMessages.For(preview.Outcome);
            }

            // Mirrors AccountsModel.RefreshFromLedgerAsync's own Math.Max(0, deletableFrom -
            // current): "not yet" without a count of how long is meaningless to a person weighing
            // whether to wait, since DeletableFromLedger alone names where the wait ends but not how
            // far away that is from here. Only attempted when there is a deletable ledger to count
            // down to -- Ready never carries one, and neither does the balance-too-low NotYet. Its
            // own failure is caught locally and leaves LedgersRemaining simply absent: a node flaky
            // enough to answer the two calls above and then refuse this one is not a reason to throw
            // away a preview it already gave.
            if (preview.DeletableFromLedger is { } deletableFrom)
            {
                try
                {
                    long current = await connection.Source
                        .CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

                    if (generation == state.Generation)
                    {
                        state.LedgersRemaining = Math.Max(0, deletableFrom - current);
                    }
                }
                catch (XrplFileException)
                {
                    state.LedgersRemaining = null;
                }
            }
        }
        catch (XrplFileException ex)
        {
            if (generation == state.Generation) state.Message = PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            if (generation == state.Generation) state.Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Submits the deletion. Does nothing unless a preflight has said it can succeed — pressing a
    /// button is not a reason to skip the checks that keep an owner reserve from being burned. A
    /// second call for the account already being closed does nothing either, so a double-click cannot
    /// sign and submit the same deletion twice against the same sequence; the first call's outcome is
    /// the one that will show.
    /// </summary>
    /// <remarks>
    /// One close at a time across accounts. A call for a different account while one is in flight
    /// says so on that account's own state, rather than doing nothing or touching the account that is
    /// closing. The screen never gets there — the shell is locked while <see cref="IsClosing"/> — but
    /// the model does not rely on that.
    /// </remarks>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        // Everything below works from this state and the copies taken from it, never from the
        // fields: this model is shared, and a SelectAsync for another account can run while an await
        // here is pending. The closure record above all must land on the account the node actually
        // deleted, and so must every word the person is told about it.
        CloseState state = _state;

        if (ReferenceEquals(_closing, state)) return;

        if (_closing is not null)
        {
            state.Message = CloseText.AnotherAccountClosing;
            return;
        }

        state.Message = null;

        if (!state.CanClose || state.Reference is not { } reference || state.Network is not { } network
            || state.Destination is not { } destination) return;

        _closing = state;
        _attempts[reference] = state;
        ClosingChanged?.Invoke();

        // A recipient changed while the node works makes this deletion's Preview a statement about
        // an address nobody is looking at; the outcome message and the closure still say what happened.
        int generation = state.Generation;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult result = await account.DeleteAsync(destination, cancellationToken).ConfigureAwait(false);

            if (generation == state.Generation) state.Preview = result;

            // Stale from whatever the last preflight measured against a ledger that has since
            // moved on: CanClose required Ready, which never carries a DeletableFromLedger, so
            // this was already null every time this line runs — cleared explicitly anyway so nothing
            // here depends on that coincidence if DeleteAsync's own internal preview ever disagrees.
            state.LedgersRemaining = null;
            UiText outcomeMessage = CloseErrorMessages.For(result.Outcome);
            state.Message = outcomeMessage;

            // Recorded only for a deletion a ledger actually took. A queued one has not happened:
            // the account still exists and still holds everything, and the queue may yet drop it.
            if (result.Outcome != DeletionOutcome.Submitted) return;

            AccountClosure closure = new(time.GetUtcNow(), result.Destination, result.ArrivingDrops, result.TxHash);

            try
            {
                // CancellationToken.None, deliberately not cancellationToken: by the time this runs,
                // the node has already accepted an irreversible deletion, and this entry is the only
                // place that will remember where the money went. A caller tearing the screen down, or
                // a token that simply expires while this write is in flight, must not stop the one
                // write that matters from landing. Cancellation is still honoured everywhere above
                // this point, where stopping genuinely means nothing happened.
                await vault.UpdateAsync(
                    reference, new VaultEntryEdit(Closure: closure), CancellationToken.None).ConfigureAwait(false);

                state.Closed = closure;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The node has already accepted the deletion: the account is gone and the balance
                // has already moved. That fact outranks whatever went wrong recording it here, so
                // the message must keep saying the account was closed rather than being replaced by
                // the vault's own error — telling somebody the vault is locked about money that is
                // already gone, and irrecoverably so, is the wrong thing to say. The hash is what
                // is left to find the closure by, since this screen could not save it.
                state.Message = UiText.Of(CloseText.RecordNotSaved, ("outcome", outcomeMessage), ("hash", result.TxHash));
            }
        }
        catch (XrplFileException ex)
        {
            // Whatever this is, it came out of the try block above — after DeleteAsync was already
            // called, which means the node may already have seen a signed AccountDelete. Leaving
            // Preview (and so CanClose) as it was would let a second click submit again against a
            // Ready conclusion this exception has already made unreliable, whether or not the node
            // actually refused: a NodeUnreachable, say, does not say the node never received the
            // blob, only that this call never learned what happened to it. Clearing it forces a
            // fresh preflight either way, which is also the only way to learn what is actually true
            // now.
            state.Preview = null;

            // ThrowawayAccount.DeleteAsync throws XrplFileError.SubmitRefused only after signing and
            // submitting, carrying the node's own engine result: PublishErrorMessages.For's own
            // sentence for that code is written for the publishing tab, where a refusal costs the
            // open-ledger fee, and it says nothing about whether a fee was actually claimed. A
            // refused AccountDelete can cost one owner reserve instead — but only when the node's
            // answer was a tec* result; CloseErrorMessages.ClaimsFee is what tells the two apart, so
            // this cannot claim a fee was burned when the node's own answer says otherwise (temDST_IS_SRC
            // above all, which never reaches the ledger and never costs anything).
            state.Message = ex.Error == XrplFileError.SubmitRefused
                ? CloseErrorMessages.DeletionRefused(CloseErrorMessages.ClaimsFee(ex.EngineResult))
                : PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            state.Message = VaultErrorMessages.For(ex.Error);
        }
        catch (OperationCanceledException)
        {
            // Cancellation reaching here started somewhere inside ThrowawayAccount.DeleteAsync's own
            // call, which by then may already have signed and put the AccountDelete on the wire — a
            // submit request the node received but whose answer this call never waited for is not
            // "nothing happened": the deletion may already have applied. The model's own reasoning
            // for writing the closure record with CancellationToken.None above applies one await
            // earlier here: a submission already on the wire must not be abandoned in silence just
            // because the screen showing it was torn down. Preview is discarded for the same reason
            // the XrplFileException branch discards it — its Ready conclusion can no longer be
            // trusted either way — and the person is told to check rather than left looking at a
            // screen that simply stopped responding.
            state.Preview = null;
            state.Message = CloseText.Interrupted;
        }
        finally
        {
            _closing = null;
            ClosingChanged?.Invoke();
        }
    }

    /// <summary>
    /// Everything the screen shows about one account's close. Private so that nothing outside this
    /// class can hold the object a call is writing to; the properties above are views of
    /// <c>_state</c>.
    /// </summary>
    private sealed class CloseState
    {
        /// <summary>Set last by <see cref="CloseAccountModel.SelectAsync"/>: non-null means resolved.</summary>
        public string? Reference { get; set; }

        public string? Network { get; set; }

        public VaultEntryKind? Kind { get; set; }

        public string? AccountLabel { get; set; }

        public string? AccountAddress { get; set; }

        public IReadOnlyList<VaultEntryInfo> Candidates { get; set; } = [];

        public string? Destination { get; set; }

        public long DestinationBalanceDrops { get; set; }

        public DeletionResult? Preview { get; set; }

        public long? LedgersRemaining { get; set; }

        public UiText? Message { get; set; }

        public AccountClosure? Closed { get; set; }

        /// <summary>
        /// Bumped whenever the destination changes or a preflight starts. A node answer tagged with an
        /// older value is about a destination or a check nobody is waiting on any more.
        /// </summary>
        public int Generation { get; set; }

        public bool CanClose => Preview is { Outcome: DeletionOutcome.Ready } && Destination is not null;
    }
}
