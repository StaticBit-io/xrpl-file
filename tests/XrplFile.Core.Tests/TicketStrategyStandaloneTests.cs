using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What only a node can answer: whether tickets are minted where the arithmetic says, whether
/// parallel submission on them works, and what happens at the edges the protocol defines.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class TicketStrategyStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task Two_hundred_memos_land_on_tickets_and_read_back_by_marker()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 500_000_000, token);

            IReadOnlyList<MemoSubmission> memos =
                [.. Enumerable.Range(0, 200).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)(i % 251)]), null))];

            await using TicketStrategy strategy = new(sink, source, writer, new TicketStrategyOptions { TicketsPerBatch = 100 });

            IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(memos, null, token);

            Assert.Equal(200, landed.Count);
            Assert.Equal([.. Enumerable.Range(0, 200)], landed.Select(memo => memo.Index).Order().ToArray());

            foreach (SubmittedMemo memo in landed.Take(5))
            {
                LedgerTransaction? tx = await source.TransactionAsync(memo.TxHash, token);
                Assert.NotNull(tx);
                Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx!.MemoTypeHex);
                Assert.True(tx.Succeeded);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_ticket_spent_before_its_mint_validated_is_refused_by_the_node()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

            // A ticket number nothing minted. This is the shape terPRE_TICKET exists for, and it
            // is why the strategy awaits its mint rather than assuming.
            uint sequence = await sink.SequenceAsync(writer.ClassicAddress, validated: false, token);

            Dictionary<string, object> onNothing = new()
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = writer.ClassicAddress,
                ["Sequence"] = 0u,
                ["TicketSequence"] = sequence + 5000,
                ["Fee"] = "12",
                ["SigningPubKey"] = writer.PublicKey,
                ["Memos"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["Memo"] = new Dictionary<string, object> { ["MemoType"] = MemoLayout.ChunkMarkerHex(0), ["MemoData"] = "00" },
                    },
                },
            };

            SubmitOutcome outcome = await sink.SubmitAsync(onNothing, writer, token);

            Assert.Equal("terPRE_TICKET", outcome.EngineResult);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
