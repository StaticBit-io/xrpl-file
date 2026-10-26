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

            await using TicketStrategy strategy = new(sink, source, new WalletSigner(writer), new TicketStrategyOptions { TicketsPerBatch = 100 });

            SubmittedMemos landed = await strategy.SubmitAllAsync(memos, null, token);

            Assert.Equal(200, landed.Count);
            Assert.Equal([.. Enumerable.Range(0, 200)], landed.Each().Select(memo => memo.Index).Order().ToArray());

            foreach (LandedMemoFacts memo in landed.Each().Take(5))
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
    public async Task A_run_mints_around_the_tickets_an_earlier_one_left_on_the_account()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            NetworkFees fees = await source.FeesAsync(token);

            // 250 tickets is the protocol's ceiling and every one of them locks an owner reserve --
            // 500 XRP of it on the stand's 2 XRP increment. An account funded for the memos alone
            // would fail this on money and say nothing about headroom.
            long funding = fees.BaseReserveDrops + (TicketStrategy.MaxLiveTickets * fees.OwnerReserveDrops) + 100_000_000;
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, funding, token);

            // What two cancelled runs leave behind. Minted straight onto the account, so no
            // strategy instance has ever heard of them.
            const int Leaked = 200;
            await StandaloneFixture.MintTicketsAsync(sink, source, writer, Leaked, fees.OpenLedgerFeeDrops, token);

            AccountState carrying = await source.AccountStateAsync(writer.ClassicAddress, token);

            // The field the mint's headroom is now computed from, read back off a real AccountRoot.
            Assert.Equal(Leaked, carrying.TicketCount);
            Assert.Equal(Leaked, carrying.OwnerCount);

            IReadOnlyList<MemoSubmission> memos =
                [.. Enumerable.Range(0, 60).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i]), null))];

            await using TicketStrategy strategy = new(sink, source, new WalletSigner(writer), new TicketStrategyOptions
            {
                TicketsPerBatch = 100,
                FeeDrops = (int)fees.OpenLedgerFeeDrops,
            });

            // 200 live and a batch of a hundred: asking for a hundred would be tecDIR_FULL. The
            // run has 50 tickets of headroom and has to make do with them.
            SubmittedMemos landed = await strategy.SubmitAllAsync(memos, null, token);

            Assert.Equal(60, landed.Count);

            AccountState after = await source.AccountStateAsync(writer.ClassicAddress, token);

            // The leftovers are untouched — this run spent only what it minted for itself.
            Assert.Equal(Leaked, after.TicketCount);
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

            SubmitOutcome outcome = await sink.SubmitAsync(onNothing, new WalletSigner(writer), token);

            Assert.Equal("terPRE_TICKET", outcome.EngineResult);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
