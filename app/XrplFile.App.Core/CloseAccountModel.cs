using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Closing a throwaway account (design 5). The most careful screen in this application: an
/// <c>AccountDelete</c> sends everything the account holds and cannot be undone, and its fee is one
/// owner reserve, which a refused submission keeps.
///
/// Nothing here ever chooses a destination (design G3), and nothing submits until a preflight has
/// said it can succeed (design G5).
/// </summary>
public sealed class CloseAccountModel(IKeyVault vault, IConnectionFactory connections, TimeProvider time)
{
    private string? _reference;
    private string? _network;
    private bool _closing;

    /// <summary>The vault's other keys, offered as destinations. Never this account itself.</summary>
    public IReadOnlyList<VaultEntryInfo> Candidates { get; private set; } = [];

    /// <summary>
    /// The label of the account being closed — set as soon as <see cref="SelectAsync"/> resolves it,
    /// with no node round trip, so the screen can say which account it is about to destroy before it
    /// has asked anything of a ledger.
    /// </summary>
    public string? AccountLabel { get; private set; }

    /// <summary>The address of the account being closed. See <see cref="AccountLabel"/>.</summary>
    public string? AccountAddress { get; private set; }

    public string? Destination { get; private set; }

    /// <summary>What the destination holds now — how a person recognises the account they meant (design G6).</summary>
    public long DestinationBalanceDrops { get; private set; }

    /// <summary>What the deletion would do, as the library computed it. Null until a preflight has run.</summary>
    public DeletionResult? Preview { get; private set; }

    /// <summary>
    /// How many ledgers remain before a <see cref="DeletionOutcome.NotYet"/> preview becomes
    /// closable, read alongside the preflight itself. Null until a preflight has run, and also null
    /// when the preview did not name a <see cref="DeletionResult.DeletableFromLedger"/> to count
    /// down to (design 5's "how long the wait is, not just when it ends" — a ledger number on its
    /// own gives a person only one of the two numbers the subtraction needs).
    /// </summary>
    public long? LedgersRemaining { get; private set; }

    public string? Message { get; private set; }

    /// <summary>The closure this screen recorded, once one happened.</summary>
    public AccountClosure? Closed { get; private set; }

    public bool CanClose => Preview is { Outcome: DeletionOutcome.Ready } && Destination is not null;

    /// <summary>Whether a preflight can be run at all — a destination has been named. The screen
    /// reads this rather than comparing <see cref="Destination"/> to null itself.</summary>
    public bool CanPreflight => Destination is not null;

    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;
        Preview = null;
        LedgersRemaining = null;
        Destination = null;
        DestinationBalanceDrops = 0;
        Closed = null;
        AccountLabel = null;
        AccountAddress = null;
        _reference = null;
        _network = null;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            VaultEntryInfo? entry = listed.FirstOrDefault(
                info => string.Equals(info.Reference, reference, StringComparison.Ordinal));

            if (entry is null)
            {
                Message = "That key is no longer in the vault.";
                return;
            }

            _reference = reference;
            _network = entry.Network;
            AccountLabel = entry.Label;
            AccountAddress = entry.Address;

            // Three ways an offered entry could not actually work as a destination: it is this same
            // account under a second reference, it is on a network this deletion cannot reach, or it
            // has already been closed and so no longer exists to receive anything. The preflight would
            // catch all three and burn no reserve, but a confirmation screen that suggests a
            // destination already known to be unusable is not one worth trusting.
            Candidates =
            [
                .. listed.Where(info =>
                    !string.Equals(info.Reference, reference, StringComparison.Ordinal)
                    && !string.Equals(info.Address, entry.Address, StringComparison.Ordinal)
                    && string.Equals(info.Network, entry.Network, StringComparison.Ordinal)
                    && info.Closure is null),
            ];
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Names the destination. Taking a bare address rather than a vault reference is deliberate: the
    /// commonest destination is an exchange or another wallet, which the vault has never heard of.
    /// </summary>
    public void ChooseDestination(string address)
    {
        Destination = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Preview = null;
        LedgersRemaining = null;
        DestinationBalanceDrops = 0;
        Message = null;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        Message = null;
        Preview = null;
        LedgersRemaining = null;

        if (_reference is null || _network is null || Destination is null)
        {
            Message = "Choose where the remainder should go.";
            return;
        }

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(_network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, _reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult preview = await account
                .PreviewDeletionAsync(Destination, cancellationToken).ConfigureAwait(false);

            AccountState home = await connection.Source
                .AccountStateAsync(Destination, cancellationToken).ConfigureAwait(false);

            // Assigned as soon as the node has actually answered both calls a confirmation needs --
            // before the extra read below, which is a nice-to-have on top of a preflight that is
            // already good. A node that answers these two and then drops the countdown read must not
            // lose the preflight it already gave: the catch around this whole method would otherwise
            // leave Preview null and show a generic error in place of a confirmation the node had
            // already earned.
            Preview = preview;
            DestinationBalanceDrops = home.BalanceDrops;

            if (preview.Outcome != DeletionOutcome.Ready)
            {
                Message = CloseErrorMessages.For(preview.Outcome);
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

                    LedgersRemaining = Math.Max(0, deletableFrom - current);
                }
                catch (XrplFileException)
                {
                    LedgersRemaining = null;
                }
            }
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

    /// <summary>
    /// Submits the deletion. Does nothing unless a preflight has said it can succeed — pressing a
    /// button is not a reason to skip the checks that keep an owner reserve from being burned. Also
    /// does nothing while a previous call is still in flight, so a double-click cannot sign and
    /// submit the same deletion twice against the same sequence.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_closing) return;

        Message = null;

        if (!CanClose || _reference is null || _network is null || Destination is null) return;

        _closing = true;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(_network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, _reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult result = await account.DeleteAsync(Destination, cancellationToken).ConfigureAwait(false);

            Preview = result;

            // Stale from whatever the last preflight measured against a ledger that has since
            // moved on: CanClose required Ready, which never carries a DeletableFromLedger, so
            // this was already null every time this line runs — cleared explicitly anyway so nothing
            // here depends on that coincidence if DeleteAsync's own internal preview ever disagrees.
            LedgersRemaining = null;
            Message = CloseErrorMessages.For(result.Outcome);

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
                    _reference, new VaultEntryEdit(Closure: closure), CancellationToken.None).ConfigureAwait(false);

                Closed = closure;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The node has already accepted the deletion: the account is gone and the balance
                // has already moved. That fact outranks whatever went wrong recording it here, so
                // the message must keep saying the account was closed rather than being replaced by
                // the vault's own error — telling somebody the vault is locked about money that is
                // already gone, and irrecoverably so, is the wrong thing to say. The hash is what
                // is left to find the closure by, since this screen could not save it.
                Message = $"{Message} The record of it could not be saved to the vault. " +
                    $"Its transaction hash is {result.TxHash} — look the deletion up with that.";
            }
        }
        catch (XrplFileException ex)
        {
            // ThrowawayAccount.DeleteAsync throws this only for XrplFileError.SubmitRefused, and only
            // after signing and submitting: PublishErrorMessages.For's own sentence for that code is
            // written for the publishing tab, where a refusal costs the open-ledger fee. A refused
            // AccountDelete costs one owner reserve instead, and deserves to say so.
            if (ex.Error == XrplFileError.SubmitRefused)
            {
                // The node itself has just proved the preflight's Ready conclusion false — that is
                // what a refusal after signing and submitting means. Leaving Preview (and so
                // CanClose) as it was would let a second click submit again against the same stale
                // conclusion and burn a second owner reserve to be refused a second time. Clearing it
                // forces a fresh preflight, which is also the only way to learn whether the reason
                // changed.
                Preview = null;
                Message = CloseErrorMessages.DeletionRefused;
            }
            else
            {
                Message = PublishErrorMessages.For(ex);
            }
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
        finally
        {
            _closing = false;
        }
    }
}
