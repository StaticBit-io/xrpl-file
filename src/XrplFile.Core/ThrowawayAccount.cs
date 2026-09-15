using System.Globalization;

namespace XrplFile;

/// <summary>What the account needs before it can publish, and what it holds now.</summary>
/// <param name="Address">The classic address the money has to reach.</param>
/// <param name="RequiredDrops">What the run's estimate says the account must hold.</param>
/// <param name="BalanceDrops">What it holds at the moment this was read.</param>
/// <param name="LockedReserveDrops">
/// What the objects the account already owns lock out of that balance. Normally zero — the
/// account was made for this one run — but a run that was called off leaves its unspent tickets
/// live, and judging the funding by the raw balance would report "covered" for an amount
/// <see cref="PublicationEstimator.Require"/> then refuses.
/// </param>
public sealed record FundingRequest(string Address, long RequiredDrops, long BalanceDrops, long LockedReserveDrops)
{
    /// <summary>What the balance leaves for the run once the account's own objects are paid for.</summary>
    public long SpendableDrops => BalanceDrops - LockedReserveDrops;

    public bool Covered => SpendableDrops >= RequiredDrops;

    public long MissingDrops => Math.Max(0, RequiredDrops - SpendableDrops);

    /// <summary>
    /// What a QR code on the funding screen encodes: the address, and nothing else.
    ///
    /// It is deliberately not a payment URI. No amount-carrying scheme is honoured by every
    /// wallet, and one that a wallet reads as a bare string would put a query string into the
    /// destination field. A plain classic address is what every XRPL wallet's scanner accepts,
    /// and <see cref="RequiredDrops"/> is on the screen next to it for the person to type.
    /// </summary>
    public string QrPayload => Address;
}

/// <summary>How far a refund's submission actually got, which is not the same as where the money is.</summary>
public enum RefundOutcome
{
    /// <summary>Nothing was sendable: the balance did not clear the reserves and the fee, and no payment was made.</summary>
    NothingToSend,

    /// <summary>The node applied the payment to its open ledger.</summary>
    Submitted,

    /// <summary>
    /// The node only queued it. A queued submission may reach a later ledger or may be dropped
    /// from the queue instead, and until a ledger takes it every drop is still on the throwaway
    /// account. Reporting this as money that went home is a lie a person only discovers later.
    /// </summary>
    Queued,
}

/// <summary>What went back, and what could not.</summary>
/// <param name="Destination">The address the caller named. Nothing here ever chooses one.</param>
/// <param name="SentDrops">What the payment carries, or zero when there was nothing to send. It has actually moved only once <paramref name="Outcome"/> is <see cref="RefundOutcome.Submitted"/>.</param>
/// <param name="LeftBehindDrops">
/// What stays on the account once the payment reaches a ledger: the base reserve plus one owner
/// reserve for every object the account still owns, none of which a payment can recover. When
/// nothing was sendable this is the whole balance, because nothing moved at all.
/// </param>
/// <param name="TxHash">The refund payment's hash, or an empty string when no payment was made.</param>
/// <param name="Outcome">How far the submission got.</param>
public sealed record RefundResult(string Destination, long SentDrops, long LeftBehindDrops, string TxHash, RefundOutcome Outcome);

/// <summary>How far a deletion got, and why it got no further.</summary>
public enum DeletionOutcome
{
    /// <summary>Every check passed and nothing was submitted. Only a preview answers this.</summary>
    Ready,

    /// <summary>
    /// The account's sequence is too close to the current ledger, or it cannot cover the fee. Nothing
    /// was submitted, and <see cref="DeletionResult.DeletableFromLedger"/> says from which ledger the
    /// first of those stops being true.
    /// </summary>
    NotYet,

    /// <summary>The ledger carries no such destination. Nothing was submitted.</summary>
    DestinationMissing,

    /// <summary>
    /// The destination exists and will not take this payment: it wants a destination tag, or it
    /// authorises its depositors. Nothing was submitted.
    /// </summary>
    DestinationRefuses,

    /// <summary>The node applied it to its open ledger.</summary>
    Submitted,

    /// <summary>
    /// The node only queued it. Until a ledger takes it every drop is still on the throwaway
    /// account, and the account still exists — reporting that as money that went home is a lie a
    /// person only discovers later. The same distinction <see cref="RefundOutcome.Queued"/> draws.
    /// </summary>
    Queued,
}

/// <summary>What a deletion will do, or did.</summary>
/// <param name="Destination">The address the caller named. Nothing here ever chooses one.</param>
/// <param name="BalanceDrops">What the account holds, as the node reported it.</param>
/// <param name="FeeDrops">One owner reserve — <c>AccountDelete</c>'s own price, read from the node.</param>
/// <param name="ArrivingDrops">What reaches the destination: the balance less the fee, or zero when nothing will be sent.</param>
/// <param name="TxHash">The deletion's hash, or an empty string when nothing was submitted.</param>
/// <param name="DeletableFromLedger">The first ledger at which the sequence rule stops refusing, when it currently refuses; otherwise <see langword="null"/>.</param>
/// <param name="Outcome">How far it got.</param>
public sealed record DeletionResult(
    string Destination,
    long BalanceDrops,
    long FeeDrops,
    long ArrivingDrops,
    string TxHash,
    long? DeletableFromLedger,
    DeletionOutcome Outcome);

/// <summary>
/// An account made for one publication and abandoned afterwards, so that a person's own account
/// does not carry a thousand transactions of somebody else's file forever.
///
/// This class never creates an account: on the XRP Ledger an account exists once its reserve has
/// been paid into it, so the key is made here and the funding makes the account. The funding
/// itself is not ours to send — the address goes on the screen, as text to copy and as a QR code
/// to scan, and the money arrives from whatever wallet the person already trusts. That is why
/// publishing this way needs no second key in any store: this library never asks for, holds, or
/// moves the account the money came from.
/// </summary>
public sealed class ThrowawayAccount(ILedgerSink sink, ILedgerSource source, ITransactionSigner signer)
{
    /// <summary>
    /// How far past an account's <c>Sequence</c> the ledger index must be before AccountDelete
    /// stops answering <c>tecTOO_SOON</c>, counted against a validated-ledger read.
    ///
    /// rippled's own precondition is written against the ledger it is building:
    /// <c>Sequence + 255 &gt; view.seq()</c> refuses. A client reads the last validated index,
    /// which is one behind the ledger being built, so the same rule measured from what a client
    /// can see lands at <c>Sequence + 254</c>. Probed against a live node before this constant
    /// was written — <c>Sequence + 253</c> refuses, <c>Sequence + 254</c> succeeds — and the
    /// probe is kept as <c>AccountDeleteThresholdTests</c>.
    /// </summary>
    public const int DeletionSequenceDelta = 254;

    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly ITransactionSigner _signer = signer;

    /// <summary>The classic address: what goes on the screen to copy, and what the QR code carries.</summary>
    public string Address => _signer.Account;

    public async Task<FundingRequest> FundingAsync(PublicationEstimate estimate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        // The same read the balance alone used to come from — BalanceDropsAsync is this call with
        // the owner count thrown away — so this costs the polling loop nothing and keeps the
        // funding screen's verdict and the session's own balance gate on one rule.
        AccountState state = await _source.AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

        return new FundingRequest(
            Address, estimate.RequiredDrops, state.BalanceDrops, state.OwnerCount * estimate.OwnerReserveDrops);
    }

    /// <summary>
    /// Polls until the account can carry the run. The money may arrive in a minute or in an hour,
    /// from a phone, so nothing here has a deadline of its own: the caller's token is the only
    /// thing that ends the wait.
    ///
    /// An account nobody has funded yet does not exist on the ledger at all, and asking a node
    /// about it is an error rather than a zero. <see cref="ILedgerSource.BalanceDropsAsync"/>
    /// already answers zero for an account the node does not know, which is exactly the state
    /// this loop starts in.
    /// </summary>
    public async Task<FundingRequest> AwaitFundingAsync(
        PublicationEstimate estimate, TimeSpan pollEvery, CancellationToken cancellationToken)
    {
        if (pollEvery <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollEvery), pollEvery, "a poll interval is a positive span");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FundingRequest request = await FundingAsync(estimate, cancellationToken).ConfigureAwait(false);

            if (request.Covered) return request;

            await Task.Delay(pollEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Who paid, read from the first transaction to reach this account. It is a proposal for the
    /// refund's destination and never more than that: the person confirms it or names another,
    /// and <see cref="RefundAsync"/> takes the value they chose rather than this one.
    /// </summary>
    public async Task<string?> FunderAsync(long fromLedger, CancellationToken cancellationToken)
    {
        long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in _source
            .AccountTransactionsAsync(Address, fromLedger, to, forward: true, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (string.IsNullOrEmpty(tx.Account)) continue;
            if (string.Equals(tx.Account, Address, StringComparison.Ordinal)) continue;

            return tx.Account;
        }

        return null;
    }

    /// <summary>
    /// Sends everything the account may actually spend to the destination the caller names.
    ///
    /// The destination is never guessed. <see cref="FunderAsync"/> can propose the address the
    /// money came from, but a proposal is for a person to look at; what goes into the transaction
    /// is what the caller passed, and a call with nothing to pass is refused rather than filled in.
    ///
    /// What may be spent is the balance less the fee and less every reserve the account is under:
    /// the base reserve, and one owner reserve for each object it owns. A run that finished spent
    /// every ticket it minted and owns nothing, so the base reserve is the whole floor — but a run
    /// that was called off leaves its unspent tickets live (see <see cref="TicketStrategy"/>'s
    /// DisposeAsync: dropping the queue does not remove the objects), and pricing the payment
    /// against the base reserve alone would ask for more than the account may give. The node
    /// answers <c>tecUNFUNDED_PAYMENT</c>, claims the fee, and answers the same to every retry
    /// until there is nothing left — which is the state a person most wants their money out of.
    ///
    /// The floor stays behind. Returning it would need AccountDelete, and that refuses while the
    /// account's sequence plus <see cref="DeletionSequenceDelta"/> exceeds the ledger index —
    /// which minting tickets pushes hours or days into the future. What is left is a payment, and
    /// it is honest about what it cannot recover.
    /// </summary>
    public async Task<RefundResult> RefundAsync(string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("a refund needs a destination; nothing is assumed", nameof(destination));
        }

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);

        // Balance and owner count come from one answer, so the sum below describes one ledger.
        AccountState state = await _source.AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

        long unspendable = fees.BaseReserveDrops + (state.OwnerCount * fees.OwnerReserveDrops);
        long sendable = state.BalanceDrops - unspendable - fees.OpenLedgerFeeDrops;

        if (sendable <= 0)
        {
            // Nothing moves, so what stays behind is the whole balance. Naming the floor here
            // would under-report an account holding a few drops more than it — real drops, on a
            // real account, that the report would have quietly forgotten.
            return new RefundResult(destination, 0, state.BalanceDrops, string.Empty, RefundOutcome.NothingToSend);
        }

        uint sequence = await _sink.SequenceAsync(Address, validated: false, cancellationToken).ConfigureAwait(false);

        Dictionary<string, object> payment = new(StringComparer.Ordinal)
        {
            ["TransactionType"] = "Payment",
            ["Account"] = Address,
            ["Destination"] = destination,
            ["Amount"] = sendable.ToString(CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = fees.OpenLedgerFeeDrops.ToString(CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _signer.PublicKey,
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(payment, _signer, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the refund of {sendable} drops to {destination}");
        }

        // terQUEUED is not arrival, and saying so would be a claim the caller cannot check.
        RefundOutcome reached = string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal)
            ? RefundOutcome.Queued
            : RefundOutcome.Submitted;

        return new RefundResult(destination, sendable, unspendable, outcome.TxHash, reached);
    }

    /// <summary>
    /// The first validated ledger index at which deleting this account would be allowed.
    ///
    /// Every transaction that rides the account's sequence raises it, and so raises this number
    /// with it; TicketCreate raises it by the ticket count plus one. A ticketed transaction moves
    /// the sequence by nothing at all, so a publication that ran entirely on tickets leaves the
    /// sequence where its last mint left it, and submitting more of them brings the moment no
    /// closer. Nothing an account does shortens the wait; only ledgers closing do.
    /// </summary>
    public async Task<long> DeletableFromLedgerAsync(CancellationToken cancellationToken)
    {
        uint sequence = await _sink.SequenceAsync(Address, validated: true, cancellationToken).ConfigureAwait(false);

        return sequence + DeletionSequenceDelta;
    }

    /// <summary>
    /// Every check a deletion makes, and every figure it would use, without submitting anything.
    /// This is what the confirmation screen is built from: showing a person the destination's
    /// balance and what will move, before they agree, is only possible if the checks can be run
    /// without the transaction.
    /// </summary>
    /// <exception cref="ArgumentException">No destination. Nothing is ever assumed — see <see cref="RefundAsync"/>.</exception>
    public async Task<DeletionResult> PreviewDeletionAsync(string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("a deletion needs a destination; nothing is assumed", nameof(destination));
        }

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);
        AccountState state = await _source.AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

        // AccountDelete's own price, not the open-ledger fee: rippled's AccountDelete::calculateBaseFee
        // charges one owner reserve. Read from the node rather than assumed, the way
        // AccountDeleteThresholdTests reads it.
        long fee = fees.OwnerReserveDrops;
        long arriving = Math.Max(0, state.BalanceDrops - fee);

        DeletionResult Refused(DeletionOutcome outcome, long? deletableFrom = null) =>
            new(destination, state.BalanceDrops, fee, 0, string.Empty, deletableFrom, outcome);

        long deletableFrom = await DeletableFromLedgerAsync(cancellationToken).ConfigureAwait(false);
        long current = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        if (current < deletableFrom) return Refused(DeletionOutcome.NotYet, deletableFrom);

        // A balance that cannot cover the fee cannot be deleted now either. It is the same sentence
        // to the person -- not yet -- and inventing a separate outcome for it would split one
        // remedy in two.
        if (state.BalanceDrops <= fee) return Refused(DeletionOutcome.NotYet, deletableFrom);

        DestinationState home = await _source.DestinationAsync(destination, cancellationToken).ConfigureAwait(false);

        if (!home.Exists) return Refused(DeletionOutcome.DestinationMissing);

        if (home.RequiresDestinationTag || home.AuthorisesDepositors)
        {
            return Refused(DeletionOutcome.DestinationRefuses);
        }

        return new DeletionResult(
            destination, state.BalanceDrops, fee, arriving, string.Empty, null, DeletionOutcome.Ready);
    }

    /// <summary>
    /// Closes the account, sending everything it holds less the fee to the named destination.
    /// Runs <see cref="PreviewDeletionAsync"/> first and submits nothing unless it answers
    /// <see cref="DeletionOutcome.Ready"/>: a <c>tec</c> result claims the fee, and this fee is one
    /// owner reserve — the same amount this whole operation exists to recover.
    /// </summary>
    /// <exception cref="ArgumentException">No destination.</exception>
    /// <exception cref="XrplFileException">
    /// <see cref="XrplFileError.SubmitRefused"/> when the node answered something other than
    /// <c>tesSUCCESS</c> or <c>terQUEUED</c>.
    /// </exception>
    public async Task<DeletionResult> DeleteAsync(string destination, CancellationToken cancellationToken)
    {
        DeletionResult preview = await PreviewDeletionAsync(destination, cancellationToken).ConfigureAwait(false);

        if (preview.Outcome != DeletionOutcome.Ready) return preview;

        uint sequence = await _sink.SequenceAsync(Address, validated: false, cancellationToken).ConfigureAwait(false);

        Dictionary<string, object> deletion = new(StringComparer.Ordinal)
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = Address,
            ["Destination"] = destination,
            ["Sequence"] = sequence,
            ["Fee"] = preview.FeeDrops.ToString(CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _signer.PublicKey,
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(deletion, _signer, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the deletion of {Address} to {destination}");
        }

        DeletionOutcome reached = string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal)
            ? DeletionOutcome.Queued
            : DeletionOutcome.Submitted;

        return preview with { TxHash = outcome.TxHash, Outcome = reached };
    }
}
