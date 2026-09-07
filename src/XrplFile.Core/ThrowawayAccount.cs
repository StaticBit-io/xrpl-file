using System.Globalization;
using Xrpl.Wallet;

namespace XrplFile;

/// <summary>What the account needs before it can publish, and what it holds now.</summary>
/// <param name="Address">The classic address the money has to reach.</param>
/// <param name="RequiredDrops">What the run's estimate says the account must hold.</param>
/// <param name="BalanceDrops">What it holds at the moment this was read.</param>
public sealed record FundingRequest(string Address, long RequiredDrops, long BalanceDrops)
{
    public bool Covered => BalanceDrops >= RequiredDrops;

    public long MissingDrops => Math.Max(0, RequiredDrops - BalanceDrops);

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

/// <summary>What went back, and what could not.</summary>
/// <param name="Destination">The address the caller named. Nothing here ever chooses one.</param>
/// <param name="SentDrops">What the payment carried, or zero when there was nothing to send.</param>
/// <param name="LeftBehindDrops">What stays on the account: the base reserve, which only AccountDelete could recover.</param>
/// <param name="TxHash">The refund payment's hash, or an empty string when no payment was made.</param>
public sealed record RefundResult(string Destination, long SentDrops, long LeftBehindDrops, string TxHash);

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
public sealed class ThrowawayAccount(ILedgerSink sink, ILedgerSource source, XrplWallet wallet)
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
    private readonly XrplWallet _wallet = wallet;

    /// <summary>The classic address: what goes on the screen to copy, and what the QR code carries.</summary>
    public string Address => _wallet.ClassicAddress;

    public async Task<FundingRequest> FundingAsync(PublicationEstimate estimate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        long balance = await _source.BalanceDropsAsync(Address, cancellationToken).ConfigureAwait(false);

        return new FundingRequest(Address, estimate.RequiredDrops, balance);
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
    /// Sends everything above the base reserve and the fee to the destination the caller names.
    ///
    /// The destination is never guessed. <see cref="FunderAsync"/> can propose the address the
    /// money came from, but a proposal is for a person to look at; what goes into the transaction
    /// is what the caller passed, and a call with nothing to pass is refused rather than filled in.
    ///
    /// The base reserve stays behind. Returning it would need AccountDelete, and that refuses
    /// while the account's sequence plus <see cref="DeletionSequenceDelta"/> exceeds the ledger
    /// index — which minting tickets pushes hours or days into the future. What is left is a
    /// payment, and it is honest about what it cannot recover.
    /// </summary>
    public async Task<RefundResult> RefundAsync(string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("a refund needs a destination; nothing is assumed", nameof(destination));
        }

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);
        long balance = await _source.BalanceDropsAsync(Address, cancellationToken).ConfigureAwait(false);
        long sendable = balance - fees.BaseReserveDrops - fees.OpenLedgerFeeDrops;

        if (sendable <= 0)
        {
            return new RefundResult(destination, 0, Math.Min(balance, fees.BaseReserveDrops), string.Empty);
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
            ["SigningPubKey"] = _wallet.PublicKey,
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(payment, _wallet, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the refund of {sendable} drops to {destination}");
        }

        return new RefundResult(destination, sendable, fees.BaseReserveDrops, outcome.TxHash);
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
}
