namespace XrplFile;

public sealed class SequenceStrategyOptions
{
    /// <summary>
    /// The fee on every transaction. A caller that has asked the network for its open-ledger fee
    /// sets this from it.
    /// </summary>
    public int FeeDrops { get; init; } = 12;

    /// <summary>
    /// After this many submissions the strategy waits for validation, so an open ledger never
    /// holds more than a batch of this account's transactions. A node was measured refusing one
    /// account beyond about a thousand of them.
    /// </summary>
    public int BatchSize { get; init; } = 200;

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ValidationPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The clock the tidy-up measures its validated index's age against (spec 4.6); a test passes its own.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// One transaction at a time on the account's sequence: about three a second, and no reserve
/// beyond the account's own. What the library did before tickets, moved behind the interface
/// without a change in behaviour. One difference is visible off the ledger: each phase (chunks,
/// lists, manifest) now re-reads the account's unvalidated sequence at its own start, where the
/// old writer read it once for the whole publication.
/// </summary>
public sealed class SequenceStrategy(
    ILedgerSink sink, ITransactionSigner signer, SequenceStrategyOptions? options = null, ILedgerSource? source = null) : ISubmissionStrategy
{
    private readonly ILedgerSink _sink = sink;
    private readonly ITransactionSigner _signer = signer;
    private readonly SequenceStrategyOptions _options = options ?? new SequenceStrategyOptions();

    /// <summary>
    /// What the tidy-up reads the account's tickets from (spec I7). Optional so that a caller with
    /// no source keeps compiling, but without it this strategy cannot find a leftover ticket and
    /// releases none — <c>PublishPolicy</c> in the application always passes one.
    /// </summary>
    private readonly ILedgerSource? _source = source;

    public long FeeDropsPerTransaction => _options.FeeDrops;

    public string Account => _signer.Account;

    public int TicketMints => 0;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        string account = _signer.Account;
        uint sequence = await _sink.SequenceAsync(account, validated: false, cancellationToken).ConfigureAwait(false);

        List<SubmittedMemo> landed = new(memos.Count);
        int sinceValidation = 0;

        for (int i = 0; i < memos.Count; i++)
        {
            landed.Add(new SubmittedMemo(i, await SubmitAsync(memos[i], sequence++, cancellationToken).ConfigureAwait(false)));
            submitted?.Report(landed.Count);

            if (++sinceValidation >= _options.BatchSize)
            {
                await WaitForValidationAsync(account, sequence, cancellationToken).ConfigureAwait(false);
                sinceValidation = 0;
            }
        }

        await WaitForValidationAsync(account, sequence, cancellationToken).ConfigureAwait(false);

        return landed;
    }

    public async Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
        _source is null
            ? TicketRelease.None
            : await LeftoverTickets.ReleaseAsync(_sink, _source, _signer, _options.FeeDrops, _options.ValidationPoll, _options.Time, cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> SubmitAsync(MemoSubmission memo, uint sequence, CancellationToken cancellationToken)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = _signer.Account,
            ["Sequence"] = sequence,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _signer.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = fields } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, _signer, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the transaction at sequence {sequence}");
        }

        return outcome.TxHash;
    }

    /// <summary>
    /// Waits until the account's validated sequence reaches the one after the last submission.
    /// </summary>
    private async Task WaitForValidationAsync(string account, uint nextSequence, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ValidationTimeout;

        while (true)
        {
            uint validated;

            try
            {
                validated = await _sink.SequenceAsync(account, validated: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                // One unanswered poll is not a failed publication; the deadline below is.
                validated = 0;
            }

            if (validated >= nextSequence) return;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the validated sequence of {account} is {validated} and {nextSequence} was expected after {_options.ValidationTimeout}");
            }

            await Task.Delay(_options.ValidationPoll, cancellationToken).ConfigureAwait(false);
        }
    }
}
