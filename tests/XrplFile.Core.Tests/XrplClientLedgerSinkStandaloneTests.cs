using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// The sink against a real node's error shape (spec I13): rippled answers a blob it cannot parse with
/// status "error", which the SDK raises as RippledException — the shape no fake can vouch for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class XrplClientLedgerSinkStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>Signs properly and then cuts four bytes off the blob, so it still decodes as hex but not as a transaction.</summary>
    private sealed class TruncatingSigner(ITransactionSigner inner) : ITransactionSigner
    {
        public string Account => inner.Account;

        public string PublicKey => inner.PublicKey;

        public async Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
        {
            SignedTransaction signed = await inner.SignAsync(transaction, cancellationToken);
            return signed with { TxBlob = signed.TxBlob[..^8] };
        }
    }

    [Fact]
    public async Task A_node_error_answer_comes_back_as_an_outcome_not_an_exception()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplWallet wallet = XrplWallet.Generate();
            XrplClientLedgerSink sink = new(client);

            SubmitOutcome outcome = await sink.SubmitAsync(
                new Dictionary<string, object>
                {
                    ["TransactionType"] = "AccountSet",
                    ["Account"] = wallet.ClassicAddress,
                    ["Sequence"] = 1u,
                    ["Fee"] = "12",
                    ["SigningPubKey"] = wallet.PublicKey,
                },
                new TruncatingSigner(new WalletSigner(wallet)),
                timeout.Token);

            Assert.Equal("error:invalidTransaction", outcome.EngineResult);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
