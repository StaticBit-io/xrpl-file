using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// A whole publication on tickets against a node, interrupted for real and picked back up.
/// Nothing less proves that the state file and the reconciliation sweep work together.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class PublicationSessionStandaloneTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "xrpl-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_run_interrupted_after_its_chunks_is_finished_by_a_second_session()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            // A ticketed strategy locks an owner reserve per live ticket on top of the memo fees;
            // for 1025 chunks plus the list and manifest that peaks well past what a SequenceStrategy
            // publish of the same file would need, so this account is funded more generously.
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 700_000_000, token);
            ITransactionSigner signer = new WalletSigner(writer);

            // 1024001 bytes: 1025 chunks over two blocks, so the list phase is exercised too.
            VectorFile vector = Vectors.File(1_024_001);
            byte[] file = VectorStream.Bytes(vector.Seed, vector.Size);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            // The node's own open-ledger fee, read once and held fixed for both strategies below:
            // PublicationSession.StartAsync and ResumeAsync each price their own estimate from it
            // and refuse a strategy that would charge anything else (see the remarks on
            // SessionOptions), so a real strategy talking to a real node has to be built from it
            // rather than from TicketStrategyOptions' own default.
            PublicationEstimate priced = await PublicationSession.EstimateAsync(
                source, writer.ClassicAddress, file.Length, options, token);
            TicketStrategyOptions strategyOptions =
                new() { TicketsPerBatch = 100, FeeDrops = (int)priced.FeeDropsPerTransaction };

            string statePath;

            // The first session publishes the chunks by hand and then stops, which is what a
            // process dying between the chunks and the manifest leaves behind.
            await using (TicketStrategy first = new(sink, source, signer, strategyOptions))
            {
                PublicationSession session = await PublicationSession.StartAsync(
                    first, source, file, "application/octet-stream", "resumed.bin",
                    writer.ClassicAddress, "key-standalone", "1", options, token);

                statePath = session.StatePath;

                IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
                await first.SubmitAllAsync(
                    [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))],
                    null, token);
            }

            // A second session, given only the state file and the bytes.
            await using TicketStrategy second = new(sink, source, signer, strategyOptions);

            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, statePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(vector.Root, report.Manifest.Root);

            // The proof is the read: a stranger's reader gets the file back byte for byte.
            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);

            Assert.Equal(file, read.Bytes);
            Assert.Equal(writer.ClassicAddress, read.Writer);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_resumed_run_with_a_hole_in_the_middle_reads_back_from_the_node()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 700_000_000, token);
            ITransactionSigner signer = new WalletSigner(writer);

            // Ten chunks in one block: few enough to run in a handful of ledgers, and enough to
            // leave a hole with landed chunks on both sides of it.
            byte[] file = VectorStream.Bytes("hole", 10_000);
            IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);

            SessionOptions options = new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

            PublicationEstimate priced = await PublicationSession.EstimateAsync(
                source, writer.ClassicAddress, file.Length, options, token);
            SequenceStrategyOptions strategyOptions = new() { FeeDrops = (int)priced.FeeDropsPerTransaction };

            string statePath;

            // Every chunk but one lands, and the run stops there. The missing index is not the
            // last, so what the second session sends lands after the highest index's ledger -- a
            // window taken from the first and last index would leave it outside, and the file
            // would no longer read back.
            await using (SequenceStrategy first = new(sink, signer, strategyOptions))
            {
                PublicationSession session = await PublicationSession.StartAsync(
                    first, source, file, "application/octet-stream", "hole.bin",
                    writer.ClassicAddress, "key-standalone", "1", options, token);

                statePath = session.StatePath;

                await first.SubmitAllAsync(
                    [.. chunks
                        .Select((chunk, index) => (Chunk: chunk, Index: index))
                        .Where(pair => pair.Index != 4)
                        .Select(pair => new MemoSubmission(MemoLayout.ChunkMarkerHex(pair.Index), MemoLayout.Hex(pair.Chunk), null))],
                    null, token);
            }

            await using SequenceStrategy second = new(sink, signer, strategyOptions);

            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, statePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            long resent = (await source.TransactionAsync(report.ChunkHashes[4], token))!.LedgerIndex;
            long highest = (await source.TransactionAsync(report.ChunkHashes[^1], token))!.LedgerIndex;
            Assert.True(resent > highest, "the hole's chunk has to land after the highest index's for this test to mean anything");

            foreach (string hash in report.ChunkHashes)
            {
                LedgerTransaction landed = (await source.TransactionAsync(hash, token))!;
                Assert.InRange(landed.LedgerIndex, report.Manifest.FirstLedger, report.Manifest.LastLedger);
            }

            // The proof is the read: a stranger's reader gets the file back byte for byte.
            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);

            Assert.Equal(file, read.Bytes);
            Assert.Equal(writer.ClassicAddress, read.Writer);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
