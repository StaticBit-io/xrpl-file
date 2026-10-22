using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LedgerDataStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_snapshot_reads_two_accounts_at_one_ledger()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet first = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            XrplWallet second = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);

            LedgerSnapshot snapshot = await source.SnapshotAsync([first.ClassicAddress, second.ClassicAddress], token);

            foreach (XrplWallet wallet in new[] { first, second })
            {
                XrplResponse<object> pinned = await client.GRequest<object, AccountInfoRequest>(
                    new AccountInfoRequest(wallet.ClassicAddress) { LedgerIndex = new Xrpl.Models.Common.LedgerIndex((uint)snapshot.LedgerIndex) }, token);

                uint expected = pinned.Raw.ToJsonElement().GetProperty("account_data").GetProperty("Sequence").GetUInt32();
                Assert.Equal(expected, snapshot.SequenceOf(wallet.ClassicAddress));
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task The_open_ledger_fee_is_at_least_the_base_fee()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            OpenLedgerFee fee = await new XrplClientLedgerSource(client).OpenLedgerFeeAsync(timeout.Token);

            Assert.True(fee.BaseFeeDrops > 0);
            Assert.True(fee.OpenLedgerFeeDrops >= fee.BaseFeeDrops);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task The_stand_enables_BatchV1_1_and_not_a_made_up_amendment()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplClientLedgerSource source = new(client);

            Assert.True(await source.IsAmendmentEnabledAsync(Amendments.Batch, timeout.Token));
            Assert.False(await source.IsAmendmentEnabledAsync("XrplFileMadeUpAmendment", timeout.Token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task An_inner_is_read_back_with_its_parent_batch_and_a_zero_fee()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writerWallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            XrplWallet payerWallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            ITransactionSigner writer = new WalletSigner(writerWallet);
            ITransactionSigner payer = new WalletSigner(payerWallet);

            uint writerSequence = await sink.SequenceAsync(writer.Account, validated: false, token);
            uint payerSequence = await sink.SequenceAsync(payer.Account, validated: false, token);
            long from = await source.CurrentLedgerAsync(token);

            MemoSubmission[] memos =
            [
                new(MemoLayout.ChunkMarkerHex(0), MemoLayout.Hex([1, 2, 3]), null),
                new(MemoLayout.ChunkMarkerHex(1), MemoLayout.Hex([4, 5, 6]), null),
            ];

            SignedTransaction signed = await BatchTransactions.SignAsync(
                BatchTransactions.Outer(payer.Account, payer.PublicKey, payerSequence, 5 * 15, (uint)(from + 20),
                    [.. memos.Select((memo, i) => BatchTransactions.Inner(writer.Account, writerSequence + (uint)i, memo))]),
                writer, payer, token);

            XrplResponse<object> submitted = await client.GRequest<object, SubmitRequest>(new SubmitRequest { TxBlob = signed.TxBlob }, token);
            Assert.Equal("tesSUCCESS", submitted.Raw.ToJsonElement().GetProperty("engine_result").GetString());

            while (await sink.SequenceAsync(writer.Account, validated: true, token) < writerSequence + 2)
            {
                await StandaloneFixture.CloseLedgersUntilAsync(client, await source.CurrentLedgerAsync(token) + 1, token);
            }

            List<LedgerTransaction> inners = [];
            await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer.Account, from, await source.CurrentLedgerAsync(token), forward: true, token))
            {
                if (tx.ParentBatchID is not null) inners.Add(tx);
            }

            Assert.Equal(2, inners.Count);
            Assert.All(inners, inner =>
            {
                Assert.Equal(signed.Hash, inner.ParentBatchID);
                Assert.Equal("AccountSet", inner.TransactionType);
                Assert.Equal(0L, inner.Fee);
                Assert.True(inner.Succeeded);
            });
            Assert.Equal([writerSequence, writerSequence + 1], inners.Select(inner => inner.Sequence).Order().ToArray());
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
