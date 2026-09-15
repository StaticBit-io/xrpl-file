namespace XrplFile.App.Core.Tests;

/// <summary>
/// An <see cref="ILedgerSink"/> that holds its <paramref name="gateAt"/>-th
/// <see cref="SubmitAsync"/> call open until <see cref="Release"/> is called, deliberately not
/// looking at the transaction's own <see cref="CancellationToken"/> until then — standing in for
/// a submission already in flight against a real socket, which <see cref="PublicationRun.Cancel"/>
/// cannot recall until the socket actually answers. <see cref="Reached"/> resolves the instant
/// that call begins, before it waits, so a test can know the run is held at exactly that point
/// without polling anything or waiting on the wall clock.
///
/// Replaces an earlier version of this double that instead slept a fixed real delay: that made
/// the one test using it cost two real seconds and, more importantly, made every gated
/// cancellation test using it vulnerable to the same problem <see cref="Reached"/> exists to
/// avoid — a poll of <see cref="RunSnapshot.Progress"/> is a lagging indicator (progress is
/// reported through <see cref="System.Progress{T}"/>, which posts to the thread pool rather than
/// calling inline), so on a contended machine the run can finish before a polling loop ever
/// observes the report it was waiting for.
///
/// Shared across <see cref="PublicationRunTests"/>, <see cref="UnfinishedModelTests"/> and
/// <see cref="RunModelTests"/>: it used to be copied line-for-line into each of the three, each
/// copy carrying its own justification for not sharing it — <c>UnfinishedModelTests</c> argued the
/// files' needs "are not identical enough", and <c>RunModelTests</c> then cited that same claim as
/// its own reason too. Both were wrong: the type these three files needed was exactly the same
/// one every time, which is the same test <see cref="NeverVault"/>'s own remarks already applied
/// when it was promoted out of <c>RunModelTests</c> "once a second test class needed the exact
/// same double" — three copies of a concurrency double is a worse bet than one, not a safer one:
/// a fix to the gating logic applied to one copy and not the other two is exactly the kind of
/// divergence a shared type forecloses.
/// </summary>
internal sealed class GatedSubmitSink(ILedgerSink inner, int gateAt) : ILedgerSink
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    /// <summary>Resolves the instant the gated submission begins, before it waits to be released.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the gated submission proceed.</summary>
    public void Release() => _release.TrySetResult();

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
        inner.SequenceAsync(account, validated, cancellationToken);

    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _count) == gateAt)
        {
            _reached.TrySetResult();

            // Deliberately not awaited against cancellationToken: the test decides exactly when
            // this proceeds by calling Release(), so nothing here depends on real time or on
            // how fast the fake ledger happens to run.
            await _release.Task.ConfigureAwait(false);

            // Once released, honour cancellation exactly where a real submission's own checks
            // would, rather than silently carrying on with a transaction nobody wants sent any
            // more -- this is what lets a test call Cancel() while the run is held here and see
            // it actually take effect once Release() lets the run notice.
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await inner.SubmitAsync(transaction, signer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A connection factory that hands out <see cref="FakeConnectionFactory"/>'s own ledger, wrapped
/// so its sink's submissions go through a <see cref="GatedSubmitSink"/>. See
/// <see cref="GatedSubmitSink"/>'s own remarks for why this is shared rather than duplicated.
/// </summary>
internal sealed class GatedConnectionFactory(FakeConnectionFactory inner, GatedSubmitSink sink) : IConnectionFactory
{
    public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
        return new Connection(connection, sink);
    }

    private sealed class Connection(ILedgerConnection inner, ILedgerSink sink) : ILedgerConnection
    {
        public ILedgerSource Source => inner.Source;

        public ILedgerSink Sink => sink;

        public string Url => inner.Url;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal static class GatedWait
{
    /// <summary>
    /// Waits for <paramref name="gate"/> to be reached, but never past the run finishing some other
    /// way first. <c>await gate.Reached</c> alone has no such check: if anything before the gated
    /// submission fails (the connection, the estimate, <c>VaultSigner.CreateAsync</c>,
    /// <c>PublicationSession.StartAsync</c>), <see cref="GatedSubmitSink.Reached"/> is never set at
    /// all, and a bare await on it would hang forever with nothing in this repository to time it out
    /// — a regression that used to fail loudly and quickly, back when this used a polling helper with
    /// its own timeout, would hang CI instead. Racing the gate against <paramref name="run"/>'s own
    /// <see cref="PublicationRun.Completion"/> means a run that ends any other way is what stops the
    /// wait, immediately, and names what it was actually waiting for when it does.
    /// </summary>
    public static async Task WaitForGateAsync(GatedSubmitSink gate, PublicationRun run)
    {
        Task finished = await Task.WhenAny(gate.Reached, run.Completion).ConfigureAwait(false);

        if (!ReferenceEquals(finished, gate.Reached))
        {
            throw new InvalidOperationException(
                $"the run finished ({run.Snapshot.State}) before reaching the gated submission, so there is "
                + "nothing left to release");
        }
    }
}
