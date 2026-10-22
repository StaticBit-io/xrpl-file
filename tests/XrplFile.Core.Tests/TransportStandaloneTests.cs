using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

[Trait("Category", "Integration")]
[Collection("Standalone")]
public class TransportStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>
    /// The call that meets the drop may fail as NodeUnreachable; spec B24 asks only that the next
    /// call reconnects. Three tries cover the one that fails and the one after it.
    /// </summary>
    private static async Task<T> AfterAtMostOneFailureAsync<T>(Func<Task<T>> call)
    {
        int failures = 0;

        while (true)
        {
            try
            {
                return await call();
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable && ++failures <= 1)
            {
            }
        }
    }

    [Fact]
    public async Task A_signed_blob_is_transmitted_once_and_a_second_copy_meets_its_used_sequence()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            uint sequence = await sink.SequenceAsync(wallet.ClassicAddress, validated: false, token);

            SignedTransaction signed = await new WalletSigner(wallet).SignAsync(new Dictionary<string, object>
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = wallet.ClassicAddress,
                ["Sequence"] = sequence,
                ["Fee"] = "12",
                ["SigningPubKey"] = wallet.PublicKey,
            }, token);

            Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(signed.TxBlob, token));
            Assert.Contains(await sink.SubmitBlobAsync(signed.TxBlob, token), new[] { "tefPAST_SEQ", "tefALREADY" });
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_client_its_owner_retired_is_refused_rather_than_reconnected()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = ClientKeeper.CreateClient(Endpoint);
        await client.Connect(timeout.Token);
        XrplClientLedgerSource source = new(client);

        ClientKeeper.Retire(client);
        await client.Disconnect();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => source.CurrentLedgerAsync(timeout.Token));
        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.False(client.IsConnected());
    }

    [Fact]
    public async Task A_socket_the_node_dropped_is_reconnected_on_the_next_call()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        await using DroppingProxy proxy = new(new Uri(Endpoint));
        IXrplClient client = ClientKeeper.CreateClient(proxy.Endpoint.ToString());
        await client.Connect(token);

        try
        {
            XrplClientLedgerSource source = new(client);
            XrplClientLedgerSink sink = new(client);
            long before = await source.CurrentLedgerAsync(token);

            proxy.DropAll();
            long after = await AfterAtMostOneFailureAsync(() => source.CurrentLedgerAsync(token));
            Assert.True(after >= before);

            proxy.DropAll();
            string answer = await AfterAtMostOneFailureAsync(() => sink.SubmitBlobAsync("00", token));
            Assert.StartsWith("error:", answer, StringComparison.Ordinal);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    /// <summary>
    /// A connection that dies without a close -- behind a NAT or a load balancer -- still reads as
    /// open, so only an unanswered request shows it is gone. The request that meets it fails after
    /// the SDK's request timeout; the next call must be made over a new connection, not sent into
    /// the same silence (spec B24).
    /// </summary>
    [Fact]
    public async Task A_socket_that_stopped_answering_is_replaced_on_the_next_call()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        await using DroppingProxy proxy = new(new Uri(Endpoint));
        IXrplClient client = ClientKeeper.CreateClient(proxy.Endpoint.ToString());
        await client.Connect(token);

        try
        {
            XrplClientLedgerSource source = new(client);
            XrplClientLedgerSink sink = new(client);
            long before = await source.CurrentLedgerAsync(token);

            proxy.SilenceAll();
            XrplFileException unanswered = await Assert.ThrowsAsync<XrplFileException>(() => source.CurrentLedgerAsync(token));
            Assert.Equal(XrplFileError.NodeUnreachable, unanswered.Error);

            System.Diagnostics.Stopwatch next = System.Diagnostics.Stopwatch.StartNew();
            long after = await source.CurrentLedgerAsync(token);
            Assert.True(after >= before);
            Assert.True(next.Elapsed < TimeSpan.FromSeconds(10), $"the next call took {next.Elapsed}");

            proxy.SilenceAll();
            await Assert.ThrowsAsync<XrplFileException>(() => sink.SubmitBlobAsync("00", token));

            next.Restart();
            Assert.StartsWith("error:", await sink.SubmitBlobAsync("00", token), StringComparison.Ordinal);
            Assert.True(next.Elapsed < TimeSpan.FromSeconds(10), $"the next submit took {next.Elapsed}");
        }
        finally
        {
            ClientKeeper.Retire(client);
            await client.Disconnect();
        }
    }
}
