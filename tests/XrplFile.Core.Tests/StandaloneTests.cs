using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Against a real rippled in standalone mode, brought up with
/// docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d.
/// Without it these fail on connection — the right outcome: skipping the check silently is worse.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class StandaloneTests
{
    private static readonly string Endpoint =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL"))
            ? "ws://localhost:6006"
            : Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL")!;

    private const string GenesisSeed = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

    [Theory]
    [InlineData("xf/c/999999999999", null, 1000, true)]
    [InlineData("xf/c/999999999999", null, 1001, false)]
    [InlineData("xf/b/999999999999", "application/json", 982, true)]
    [InlineData("xf/b/999999999999", "application/json", 983, false)]
    [InlineData("xf/m", "application/json", 995, true)]
    [InlineData("xf/m", "application/json", 996, false)]
    public async Task The_memo_budgets_hold_on_a_node(string memoType, string? memoFormat, int dataBytes, bool accepted)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet writer = await FundedAccountAsync(client, sink, timeout.Token);
            uint sequence = await sink.SequenceAsync(writer.ClassicAddress, validated: false, timeout.Token);

            Dictionary<string, object> memo = new()
            {
                ["MemoType"] = MemoLayout.Utf8Hex(memoType),
                ["MemoData"] = Convert.ToHexString(new byte[dataBytes]),
            };
            if (memoFormat is not null) memo["MemoFormat"] = MemoLayout.Utf8Hex(memoFormat);

            Dictionary<string, object> tx = new()
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = writer.ClassicAddress,
                ["Sequence"] = sequence,
                ["Fee"] = "12",
                ["SigningPubKey"] = writer.PublicKey,
                ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = memo } },
            };

            string outcome;
            try
            {
                outcome = (await sink.SubmitAsync(tx, writer, timeout.Token)).EngineResult;
            }
            catch (Exception e)
            {
                // The SDK measures the Memos array as rippled does and refuses to sign one over the cap.
                outcome = "error:" + e.Message;
            }

            if (accepted)
            {
                Assert.True(outcome is "tesSUCCESS" or "terQUEUED", $"{memoType} with {dataBytes} bytes: {outcome}");
            }
            else
            {
                Assert.False(outcome is "tesSUCCESS" or "terQUEUED", $"{memoType} with {dataBytes} bytes was accepted");
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(1024001)]
    public async Task A_vector_file_round_trips_through_a_node(long size)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        VectorFile vector = Vectors.File(size);
        byte[] file = VectorStream.Bytes(vector.Seed, vector.Size);

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writer = await FundedAccountAsync(client, sink, timeout.Token);

            PublishResult published = await new XrplFileWriter(sink, source).PublishAsync(
                file, "application/octet-stream", $"vector-{size}.bin", writer, "1", null, timeout.Token);

            Assert.Equal(vector.Root, published.Manifest.Root);
            Assert.Equal(vector.Blocks, published.Manifest.Blocks);

            ReadResult read = await new XrplFileReader(source).ReadAsync(published.Link, timeout.Token);

            Assert.Equal(file, read.Bytes);
            Assert.Equal(writer.ClassicAddress, read.Writer);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    private static async Task<XrplWallet> FundedAccountAsync(IXrplClient client, XrplClientLedgerSink sink, CancellationToken token)
    {
        XrplWallet genesis = XrplWallet.FromSeed(GenesisSeed);
        XrplWallet fresh = XrplWallet.Generate();
        uint sequence = await sink.SequenceAsync(genesis.ClassicAddress, validated: false, token);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = genesis.ClassicAddress,
            ["Destination"] = fresh.ClassicAddress,
            ["Amount"] = "500000000",
            ["Sequence"] = sequence,
            ["Fee"] = "12",
            ["SigningPubKey"] = genesis.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(payment, genesis, token);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        while (await sink.SequenceAsync(genesis.ClassicAddress, validated: true, token) <= sequence)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        return fresh;
    }
}
