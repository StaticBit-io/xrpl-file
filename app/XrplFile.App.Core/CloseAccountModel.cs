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

    public string? Destination { get; private set; }

    /// <summary>What the destination holds now — how a person recognises the account they meant (design G6).</summary>
    public long DestinationBalanceDrops { get; private set; }

    /// <summary>What the deletion would do, as the library computed it. Null until a preflight has run.</summary>
    public DeletionResult? Preview { get; private set; }

    public string? Message { get; private set; }

    /// <summary>The closure this screen recorded, once one happened.</summary>
    public AccountClosure? Closed { get; private set; }

    public bool CanClose => Preview is { Outcome: DeletionOutcome.Ready } && Destination is not null;

    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;
        Preview = null;
        Destination = null;
        DestinationBalanceDrops = 0;
        Closed = null;
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
        DestinationBalanceDrops = 0;
        Message = null;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        Message = null;
        Preview = null;

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

            Preview = preview;
            DestinationBalanceDrops = home.BalanceDrops;

            if (preview.Outcome != DeletionOutcome.Ready)
            {
                Message = CloseErrorMessages.For(preview.Outcome);
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
            Message = CloseErrorMessages.For(result.Outcome);

            // Recorded only for a deletion a ledger actually took. A queued one has not happened:
            // the account still exists and still holds everything, and the queue may yet drop it.
            if (result.Outcome != DeletionOutcome.Submitted) return;

            AccountClosure closure = new(time.GetUtcNow(), result.Destination, result.ArrivingDrops, result.TxHash);

            try
            {
                await vault.UpdateAsync(
                    _reference, new VaultEntryEdit(Closure: closure), cancellationToken).ConfigureAwait(false);

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
            Message = ex.Error == XrplFileError.SubmitRefused
                ? CloseErrorMessages.DeletionRefused
                : PublishErrorMessages.For(ex);
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
