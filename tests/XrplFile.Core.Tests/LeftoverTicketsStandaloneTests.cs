using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What spec section 1 did by hand, against a node: a ticketed publication stopped part-way leaves
/// tickets on its account, and what comes after spends every one of them while leaving the
/// account's own ticket alone. The account's own ticket is minted before the stopped run rather
/// than after it, as spec 7 orders it; the two are equivalent, and minting first keeps
/// <see cref="StandaloneFixture.MintTicketsAsync"/>'s owner-count check true.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LeftoverTicketsStandaloneTests : IDisposable
{
    /// <summary>A hundred-and-twenty-chunk file is one batch of a hundred tickets and then twenty.</summary>
    private const int FileBytes = 120_000;

    /// <summary>How many memos of the first batch reach the node before the run is stopped. The other sixty tickets stay live.</summary>
    private const int ForwardedBeforeStop = 40;

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>
    /// Forwards the first <paramref name="forwarded"/> memo submissions, then stops the run: every
    /// later one is cancelled before it reaches the node, so its ticket is certainly still live.
    /// Mints pass through untouched.
    /// </summary>
    private sealed class StopAfterSink(ILedgerSink inner, int forwarded, CancellationTokenSource stop) : ILedgerSink
    {
        private int _memos;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (transaction.ContainsKey("TicketSequence") && Interlocked.Increment(ref _memos) > forwarded)
            {
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }

            return inner.SubmitAsync(transaction, signer, cancellationToken);
        }
    }

    /// <summary>Counts the tickets the mints passing through it ask for.</summary>
    private sealed class MintCountingSink(ILedgerSink inner) : ILedgerSink
    {
        private int _minted;

        public int Minted => Volatile.Read(ref _minted);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if ((string)transaction["TransactionType"] == "TicketCreate")
            {
                Interlocked.Add(ref _minted, (int)Convert.ToUInt32(transaction["TicketCount"]));
            }

            return inner.SubmitAsync(transaction, signer, cancellationToken);
        }
    }

    /// <summary>
    /// A funded account holding one ticket of its own, minted without the library's mark, and the
    /// leftovers of a ticketed publication stopped part-way through its first batch.
    /// </summary>
    private static async Task<(XrplWallet Writer, uint Foreign, string StatePath, int Fee)> StrandedAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplClientLedgerSource source, byte[] file, SessionOptions options, CancellationToken token)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 700_000_000, token);
        ITransactionSigner signer = new WalletSigner(writer);

        PublicationEstimate priced = await PublicationSession.EstimateAsync(source, writer.ClassicAddress, file.Length, options, token);
        int fee = (int)priced.FeeDropsPerTransaction;

        // First, while the account owns nothing, so the fixture's owner-count check holds.
        await StandaloneFixture.MintTicketsAsync(sink, source, writer, 1, fee, token);
        uint foreign = Assert.Single(await source.LiveTicketsAsync(writer.ClassicAddress, token)).Sequence;

        string statePath;

        using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await using TicketStrategy first = new(
                new StopAfterSink(sink, ForwardedBeforeStop, stop), source, signer,
                new TicketStrategyOptions { TicketsPerBatch = 100, FeeDrops = fee });

            PublicationSession session = await PublicationSession.StartAsync(
                first, source, file, "application/octet-stream", "stopped.bin",
                writer.ClassicAddress, "key-standalone", "1", options, token);

            statePath = session.StatePath;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        }

        // Let the forty forwarded memos validate, so what is listed next is what is really left.
        await StandaloneFixture.CloseLedgersUntilAsync(client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 2, token);

        return (writer, foreign, statePath, fee);
    }

    [Fact]
    public async Task A_resumed_run_spends_what_the_stopped_one_left_and_leaves_a_foreign_ticket_alone()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };
            byte[] file = VectorStream.Bytes("leftovers-resume", FileBytes);

            (XrplWallet writer, uint foreign, string statePath, int fee) = await StrandedAsync(client, sink, source, file, options, token);

            // At least sixty: a counted submission that was cancelled before it reached the socket
            // leaves its ticket live too. What is invariant is that every ticket of the first batch
            // either carried a chunk that landed or is still here.
            TicketCensus stranded = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.InRange(stranded.Ours.Count, 100 - ForwardedBeforeStop, 100);
            Assert.Equal(1, stranded.Others);

            MintCountingSink counting = new(sink);
            await using TicketStrategy second = new(
                counting, source, new WalletSigner(writer), new TicketStrategyOptions { TicketsPerBatch = 100, FeeDrops = fee });

            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, statePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);

            // The resume needs a ticket for every chunk that did not land, and one for the manifest.
            // Every ticket of the first batch either carried a chunk that landed or is still on the
            // account, so landed + stranded = 100, and what is left to mint is 120 - 100 + 1 = 21
            // however many submissions the stop actually caught.
            Assert.Equal(21, counting.Minted);

            TicketCensus after = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.Empty(after.Ours);
            Assert.Contains(await source.LiveTicketsAsync(writer.ClassicAddress, token), ticket => ticket.Sequence == foreign);
            Assert.Equal(0, report.TicketsNotReleased);
            Assert.Null(report.TicketReleaseFailure);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_small_publication_after_a_stopped_large_one_releases_what_it_left()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };
            byte[] large = VectorStream.Bytes("leftovers-large", FileBytes);

            (XrplWallet writer, uint foreign, _, int fee) = await StrandedAsync(client, sink, source, large, options, token);

            TicketCensus stranded = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.NotEmpty(stranded.Ours);

            // A different, small file on sequence numbers (spec I7): its tidy-up is what clears them.
            byte[] small = VectorStream.Bytes("leftovers-small", 2_500);
            await using SequenceStrategy sequence = new(sink, new WalletSigner(writer), new SequenceStrategyOptions { FeeDrops = fee }, source);

            PublicationSession session = await PublicationSession.StartAsync(
                sequence, source, small, "text/plain", "small.txt",
                writer.ClassicAddress, "key-standalone", "1", options, token);
            PublicationReport report = await session.RunAsync(null, token);

            Assert.Equal(stranded.Ours.Count, report.TicketsReleased);
            Assert.Equal(0, report.TicketsAlreadyGone);
            Assert.Equal(0, report.TicketsNotReleased);
            Assert.Null(report.TicketReleaseFailure);

            TicketCensus after = await LeftoverTickets.FindAsync(source, writer.ClassicAddress, null, token);
            Assert.Empty(after.Ours);
            Assert.Contains(await source.LiveTicketsAsync(writer.ClassicAddress, token), ticket => ticket.Sequence == foreign);

            // The reserve those tickets locked is free: the account owns its one ticket and nothing else.
            AccountState state = await source.AccountStateAsync(writer.ClassicAddress, token);
            Assert.Equal(1, state.OwnerCount);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
