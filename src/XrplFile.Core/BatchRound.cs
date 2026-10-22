using System.Runtime.ExceptionServices;

namespace XrplFile;

/// <summary>
/// One round of a Batch run (spec 4.1, B5): a snapshot, a plan from it, the batches sent through the
/// window while the chain is checked, settlement, the round's solo memo, and the final sweep. Once a
/// round has settled nothing it sent can still land, so the next round cannot collide with it.
/// </summary>
internal sealed class BatchRound(
    BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IDictionary<int, int> candidatesPerMemo,
    BatchProgress progress)
{
    /// <summary>Transmissions of one blob, the first included (spec submit-answer table).</summary>
    private const int MaxTransmissions = 4;

    private readonly Lock _latestGate = new();
    private readonly Lock _historyGate = new();
    private readonly List<LedgerTransaction> _writerHistory = [];
    private readonly HashSet<string> _writerHashes = new(StringComparer.OrdinalIgnoreCase);
    private long _writerReadFrom;
    private LedgerSnapshot? _latest;
    private DateTimeOffset _latestAt;
    private long _highestLedger;
    private DateTimeOffset _advancedAt;
    private long _allowance;
    private Exception? _fatal;

    private string Writer => context.Writer.Account;

    private string Payer => context.Payer.Account;

    private BatchStrategyOptions Options => context.Options;

    public async Task<RoundResult> RunAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot start = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        long unit = await UnitAsync(cancellationToken).ConfigureAwait(false);
        RoundPlan plan = BatchGrouping.Plan(pending, start.SequenceOf(Writer), start.SequenceOf(Payer));
        BatchRegistry registry = new(context.Fence, candidatesPerMemo, context.Trace);

        context.Trace.Add($"round:{start.LedgerIndex}");

        using CancellationTokenSource sweeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sweeps = ProgressLoopAsync(start, registry, sweeping.Token);

        try
        {
            LedgerSnapshot settled = plan.Batches.Count == 0
                ? start
                : await SendBatchesAsync(plan, start, unit, registry, cancellationToken).ConfigureAwait(false);

            // A's own transaction never shares a ledger window with batches in flight (spec B11), and a
            // halted round sends no more: the memo goes to the next round.
            if (plan.SoloMemo is int solo && !registry.IsHalted)
            {
                settled = await SendSoloAsync(solo, unit, registry, cancellationToken).ConfigureAwait(false);
            }

            RoundResult result = await BatchSweep.FinalAsync(
                context, memos, pending, registry.Candidates, start, settled, KnownWriterHistory(), cancellationToken).ConfigureAwait(false);
            progress.Found(result.Landed.Keys);

            return result;
        }
        finally
        {
            await sweeping.CancelAsync().ConfigureAwait(false);

            try
            {
                await sweeps.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the writer's history forward from its last position every
    /// <see cref="BatchStrategyOptions.ProgressSweep"/> while the round runs, and reports the memos it
    /// finds with tesSUCCESS (spec B20). Progress is display-only: a read that fails, whatever the
    /// failure, is skipped and never replaces the round's own result; the final sweep decides anyway.
    /// </summary>
    private async Task ProgressLoopAsync(LedgerSnapshot start, BatchRegistry registry, CancellationToken cancellationToken)
    {
        long from = start.LedgerIndex + 1;

        while (true)
        {
            await Task.Delay(Options.ProgressSweep, Options.Time, cancellationToken).ConfigureAwait(false);

            try
            {
                long to = await context.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
                if (to < from) continue;

                IReadOnlyList<LedgerTransaction> records = await BatchSweep.HistoryAsync(
                    context.Source, Writer, from, to, cancellationToken).ConfigureAwait(false);

                progress.Found(BatchSweep.Outcome(context, memos, pending, registry.Candidates, records, []).Landed.Keys);
                from = Math.Max(from, records.Count == 0 ? from : records.Max(tx => tx.LedgerIndex));
                RememberWriterHistory(records, from);
            }
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // The next sweep tries again. Whatever the read threw -- a transport error, or a page the
                // parser could not make sense of -- progress is display-only and must not replace the
                // round's own result; only the loop's own cancellation ends it.
            }
        }
    }

    /// <summary>
    /// Keeps what a progress sweep read for the final sweep (spec B20, B30). The next sweep starts at
    /// <paramref name="readFrom"/> again, so only the ledgers below it are complete here.
    /// </summary>
    private void RememberWriterHistory(IReadOnlyList<LedgerTransaction> records, long readFrom)
    {
        lock (_historyGate)
        {
            foreach (LedgerTransaction tx in records)
            {
                if (_writerHashes.Add(tx.Hash)) _writerHistory.Add(tx);
            }

            _writerReadFrom = readFrom;
        }
    }

    private BatchSweep.KnownHistory? KnownWriterHistory()
    {
        lock (_historyGate)
        {
            if (_writerReadFrom == 0) return null;

            return new BatchSweep.KnownHistory(
                [.. _writerHistory.Where(tx => tx.LedgerIndex < _writerReadFrom).OrderBy(tx => tx.LedgerIndex)], _writerReadFrom);
        }
    }

    private async Task<LedgerSnapshot> SendBatchesAsync(
        RoundPlan plan, LedgerSnapshot start, long unit, BatchRegistry registry, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _allowance, start.SequenceOf(Payer) + (long)Options.MaxAhead);

        List<Task> transmissions = [];
        Lock resendGate = new();
        List<Task> resends = [];
        using CancellationTokenSource transmitting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource checking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Resend(BatchCandidate outer)
        {
            lock (resendGate) resends.Add(TransmitAsync(outer, registry, slotHeld: false, transmitting, transmitting.Token));
        }

        Task checks = ChainCheckLoopAsync(plan, start, registry, Resend, checking.Token);
        ExceptionDispatchInfo? failure = null;
        LedgerSnapshot? settled = null;

        try
        {
            try
            {
                foreach (PlannedBatch batch in plan.Batches)
                {
                    if (!await WaitForAllowanceAsync(batch.OuterSequence, registry, cancellationToken).ConfigureAwait(false)) break;

                    // A batch is built and signed only once the window has a slot for it (spec 4.1
                    // step 3, B10); the slot passes to its first transmission.
                    // A halt does not wake this wait; it ends when a holder's submit does, and a reconnect inside one is bounded by the keeper's client options (ClientKeeper.CreateClient: one 20 s connection attempt, then the 40 s request timeout of Connect's server_info).
                    await context.Window.AcquireAsync(cancellationToken).ConfigureAwait(false);
                    bool handedOver = false;

                    try
                    {
                        if (registry.IsHalted || Volatile.Read(ref _fatal) is not null) break;

                        LedgerSnapshot basis = await FreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                        long fee = BatchFees.BatchFee(batch.Inners.Count, unit);
                        uint lastLedger = checked((uint)(basis.LedgerIndex + Options.DeadlineLedgers));

                        Dictionary<string, object> outer = BatchTransactions.Outer(
                            Payer, context.Payer.PublicKey, batch.OuterSequence, fee, lastLedger,
                            [.. batch.Inners.Select(inner => BatchTransactions.Inner(Writer, inner.Sequence, memos[inner.MemoIndex]))]);

                        context.Spending.CommitPayer(fee);
                        SignedTransaction signed = await context.Trace.TimeSigningAsync(
                            () => BatchTransactions.SignAsync(outer, context.Writer, context.Payer, cancellationToken)).ConfigureAwait(false);

                        BatchCandidate candidate = new(
                            batch.Ordinal, isOuter: true, Payer, batch.OuterSequence, lastLedger, fee,
                            [.. batch.Inners.Select(inner => inner.MemoIndex)], signed);

                        if (!await registry.TryRegisterAsync(candidate, cancellationToken).ConfigureAwait(false)) break;

                        transmissions.Add(TransmitAsync(candidate, registry, slotHeld: true, transmitting, transmitting.Token));
                        handedOver = true;
                    }
                    finally
                    {
                        if (!handedOver) context.Window.Return();
                    }
                }
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }

            await checking.CancelAsync().ConfigureAwait(false);

            try
            {
                await checks.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (checking.IsCancellationRequested)
            {
            }

            // A run that failed here fails unsettled; the fence already covers everything registered (spec B23).
            if (failure is null && Volatile.Read(ref _fatal) is null)
            {
                // The last outers are often still waiting on one that was exhausted when sending ended.
                settled = await SettleAsync(registry.Candidates, cancellationToken, snapshot =>
                {
                    if (!registry.IsHalted && BatchChain.Verdict(plan, start, registry.Outers, snapshot, Writer, Payer) is null)
                    {
                        ResendExhausted(registry.Outers, (long)snapshot.SequenceOf(Payer) - start.SequenceOf(Payer), Resend);
                    }
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever ended the round -- settlement, a failure, the caller's cancellation -- no
            // transmission outlives it.
            await transmitting.CancelAsync().ConfigureAwait(false);
            await DrainAsync(transmissions).ConfigureAwait(false);

            List<Task> resent;
            lock (resendGate) resent = [.. resends];
            await DrainAsync(resent).ConfigureAwait(false);
        }

        failure?.Throw();
        if (Volatile.Read(ref _fatal) is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();

        return settled!;
    }

    private async Task<LedgerSnapshot> SendSoloAsync(int memoIndex, long unit, BatchRegistry registry, CancellationToken cancellationToken)
    {
        LedgerSnapshot basis = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        uint sequence = basis.SequenceOf(Writer);
        uint lastLedger = checked((uint)(basis.LedgerIndex + Options.DeadlineLedgers));

        Dictionary<string, object> solo = BatchTransactions.Solo(Writer, context.Writer.PublicKey, sequence, unit, lastLedger, memos[memoIndex]);
        context.Spending.CommitWriter(unit);
        SignedTransaction signed = await context.Trace.TimeSigningAsync(
            () => context.Writer.SignAsync(solo, cancellationToken)).ConfigureAwait(false);

        BatchCandidate candidate = new(BatchCandidate.SoloOrdinal, isOuter: false, Writer, sequence, lastLedger, unit, [memoIndex], signed);

        if (!await registry.TryRegisterAsync(candidate, cancellationToken).ConfigureAwait(false)) return basis;

        using CancellationTokenSource transmitting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task transmission = TransmitAsync(candidate, registry, slotHeld: false, transmitting, transmitting.Token);
        LedgerSnapshot settled;

        try
        {
            settled = await SettleAsync([candidate], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transmitting.CancelAsync().ConfigureAwait(false);
            await DrainAsync([transmission]).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _fatal) is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();

        return settled;
    }

    /// <summary>
    /// Transmits one registered candidate (spec submit-answer table): busy answers and lost answers
    /// are transport retries of the same blob, <see cref="BatchStrategyOptions.TransportRetryStep"/> Г—
    /// (retry + 1) apart, up to four transmissions in all; <c>telINSUF_FEE_P</c> is not resent, since
    /// the next round reads the unit again; any other answer leaves it to settlement; a malformed one
    /// fails the run. A halted round sends no first copy of anything: the candidate stays registered
    /// and counted, its deadline is already in the fence, and settlement waits it out (Spec question 24).
    /// </summary>
    private async Task TransmitAsync(
        BatchCandidate candidate, BatchRegistry registry, bool slotHeld, CancellationTokenSource transmitting, CancellationToken cancellationToken)
    {
        try
        {
            for (int transmission = 1; transmission <= MaxTransmissions; transmission++)
            {
                bool first = transmission == 1;

                if (candidate.IsResolved || (first && registry.IsHalted))
                {
                    if (first && slotHeld) context.Window.Return();
                    if (first && !candidate.IsResolved) context.Trace.Add($"unsent:{candidate.Ordinal}");
                    return;
                }

                if (!(first && slotHeld)) await context.Window.AcquireAsync(cancellationToken).ConfigureAwait(false);
                string? answer = null;
                bool unreachable = false;

                try
                {
                    answer = await context.Sink.SubmitBlobAsync(candidate.Signed.TxBlob, cancellationToken).ConfigureAwait(false);
                }
                catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
                {
                    // No answer: the next call reconnects (spec B24), and the blob is sent again.
                    unreachable = true;
                }
                finally
                {
                    // Only a node that did not answer, or answered busy, narrows the window; a
                    // transmission cancelled by its round's end says nothing about the node.
                    if (answer is null && !unreachable) context.Window.Return();
                    else context.Window.Release(busy: unreachable || IsBusy(answer!));
                }

                context.Trace.Add($"transmit:{candidate.Ordinal}:{answer ?? "no answer"}");
                if (answer is not null) progress.Sent();

                if (answer is not null && IsMalformed(answer))
                {
                    throw new XrplFileException(XrplFileError.SubmitRefused,
                        $"the node answered {answer} to {candidate.Hash}; a malformed transaction is a defect, not a condition",
                        engineResult: answer);
                }

                if (answer == "telINSUF_FEE_P") return;

                if (answer is not null && !IsBusy(answer)) return;

                if (transmission < MaxTransmissions)
                {
                    await Task.Delay(Options.TransportRetryStep * transmission, Options.Time, cancellationToken).ConfigureAwait(false);
                }
            }

            candidate.MarkExhausted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _fatal, e, null);
            await registry.HaltAsync("fatal", CancellationToken.None).ConfigureAwait(false);
            await transmitting.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A <c>tel</c> answer, <c>tooBusy</c>, <c>slowDown</c> (spec B10), or a node briefly out of step
    /// with its network -- <c>noNetwork</c>, <c>noCurrent</c>, <c>noClosed</c>, <c>notSynced</c>
    /// (Spec question 13). <c>amendmentBlocked</c> and any other error are left to settlement.
    /// </summary>
    private static bool IsBusy(string answer) =>
        answer.StartsWith("tel", StringComparison.Ordinal)
        || answer is "error:tooBusy" or "error:slowDown" or "error:noNetwork" or "error:noCurrent" or "error:noClosed" or "error:notSynced";

    /// <summary>A <c>tem</c> answer, or a blob the node could not read at all.</summary>
    private static bool IsMalformed(string answer) =>
        answer.StartsWith("tem", StringComparison.Ordinal) || answer == "error:invalidTransaction";

    /// <summary>
    /// The chain check of spec B8, every <see cref="BatchStrategyOptions.ChainCheck"/>. A check that
    /// cannot be made -- the node unreachable past its limit, a ledger that stopped -- is fatal: it
    /// halts the round, so a sender waiting for allowance is released rather than left waiting.
    /// </summary>
    private async Task ChainCheckLoopAsync(
        RoundPlan plan, LedgerSnapshot start, BatchRegistry registry, Action<BatchCandidate> resend, CancellationToken cancellationToken)
    {
        try
        {
            while (!registry.IsHalted)
            {
                await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);

                LedgerSnapshot snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);

                if (BatchChain.Verdict(plan, start, registry.Outers, snapshot, Writer, Payer) is { } verdict)
                {
                    await registry.HaltAsync(verdict, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // Raised only by a snapshot that passed the check (spec B10).
                Interlocked.Exchange(ref _allowance, snapshot.SequenceOf(Payer) + (long)Options.MaxAhead);

                ResendExhausted(registry.Outers, (long)snapshot.SequenceOf(Payer) - start.SequenceOf(Payer), resend);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _fatal, e, null);
            await registry.HaltAsync("fatal", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends again the outer B's sequence stopped at when its transmissions are exhausted, and the
    /// exhausted outers behind it, in order, as many as the window holds now (spec B8 as amended
    /// 2026-10-22). Left to its deadline, it held every later outer for about 70 s, though the
    /// ledger had room again a few ledgers later. It is the same signed blob, so it stays one candidate.
    /// </summary>
    private void ResendExhausted(IReadOnlyList<BatchCandidate> outers, long consumed, Action<BatchCandidate> resend)
    {
        if (consumed < 0 || consumed >= outers.Count || !outers[(int)consumed].IsExhausted) return;

        int room = context.Window.Size;

        for (int k = (int)consumed; k < outers.Count && room > 0; k++)
        {
            if (!outers[k].TryResume()) continue;

            context.Trace.Add($"resend:{outers[k].Ordinal}");
            resend(outers[k]);
            room--;
        }
    }

    private async Task<bool> WaitForAllowanceAsync(uint outerSequence, BatchRegistry registry, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (registry.IsHalted || Volatile.Read(ref _fatal) is not null) return false;
            if (outerSequence < Interlocked.Read(ref _allowance)) return true;

            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until every candidate is resolved (spec B9), then <see cref="BatchStrategyOptions.SettleLedgers"/>
    /// more ledgers, and returns the settling snapshot.
    /// </summary>
    private async Task<LedgerSnapshot> SettleAsync(
        IReadOnlyList<BatchCandidate> candidates, CancellationToken cancellationToken, Action<LedgerSnapshot>? waiting = null)
    {
        LedgerSnapshot snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);

        while (!candidates.All(candidate => candidate.IsResolvedBy(snapshot)))
        {
            waiting?.Invoke(snapshot);
            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
            snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (BatchCandidate candidate in candidates) candidate.MarkResolved();

        long target = snapshot.LedgerIndex + Options.SettleLedgers;

        while (snapshot.LedgerIndex < target)
        {
            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
            snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        context.Trace.Add($"settled:{snapshot.LedgerIndex}");
        return snapshot;
    }

    private async Task<LedgerSnapshot> FreshSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (_latestGate)
        {
            if (_latest is { } latest && Options.Time.GetUtcNow() - _latestAt <= Options.SnapshotMaxAge) return latest;
        }

        return await SnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<LedgerSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot snapshot = await ReadAsync(
            token => context.Source.SnapshotAsync([Writer, Payer], token), cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = Options.Time.GetUtcNow();
        bool stuck = false;

        lock (_latestGate)
        {
            if (snapshot.LedgerIndex > _highestLedger)
            {
                _highestLedger = snapshot.LedgerIndex;
                _advancedAt = now;
            }
            else if (now - _advancedAt >= Options.NodeOutageLimit)
            {
                stuck = true;
            }

            if (_latest is null || snapshot.LedgerIndex >= _latest.LedgerIndex)
            {
                _latest = snapshot;
                _latestAt = now;
            }
        }

        if (stuck)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable,
                $"the node's validated ledger has stayed at {snapshot.LedgerIndex} for {Options.NodeOutageLimit}");
        }

        return snapshot;
    }

    /// <summary>
    /// The round's unit (spec B15). Above the approved ceiling the round waits
    /// <see cref="BatchStrategyOptions.FeeWait"/> for fees to fall; if they have not, the run fails
    /// with <see cref="XrplFileError.FeeTooHigh"/> and can be resumed later.
    /// </summary>
    private async Task<long> UnitAsync(CancellationToken cancellationToken)
    {
        long ceiling = context.Budget.FeeCeilingDrops;
        long unit = BatchFees.Unit(await ReadAsync(context.Source.OpenLedgerFeeAsync, cancellationToken).ConfigureAwait(false));

        if (unit <= ceiling) return unit;

        context.Trace.Add($"fee-wait:{unit}");
        await Task.Delay(Options.FeeWait, Options.Time, cancellationToken).ConfigureAwait(false);

        unit = BatchFees.Unit(await ReadAsync(context.Source.OpenLedgerFeeAsync, cancellationToken).ConfigureAwait(false));

        if (unit <= ceiling) return unit;

        throw new XrplFileException(XrplFileError.FeeTooHigh,
            $"the fee unit is {unit} drops and the approved ceiling is {ceiling}; fees did not fall within {Options.FeeWait}");
    }

    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        BatchReads.RetryAsync(read, Options, cancellationToken);

    private static async Task DrainAsync(IReadOnlyList<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A transmission cancelled with its round; nothing it was can be in doubt past the fence.
        }
    }
}
