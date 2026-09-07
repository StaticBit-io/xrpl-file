using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Funds a fresh wallet from the standalone node's genesis account. Shared by every integration
/// test class that needs an account to write from, so there is exactly one place that knows the
/// genesis seed and the shape of a funding Payment.
/// </summary>
internal static class StandaloneFixture
{
    private const string GenesisSeed = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";

    public static async Task<XrplWallet> FundedAccountAsync(
        IXrplClient client, XrplClientLedgerSink sink, long drops, CancellationToken cancellationToken)
    {
        XrplWallet genesis = XrplWallet.FromSeed(GenesisSeed);
        XrplWallet fresh = XrplWallet.Generate();
        uint sequence = await sink.SequenceAsync(genesis.ClassicAddress, validated: false, cancellationToken);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = genesis.ClassicAddress,
            ["Destination"] = fresh.ClassicAddress,
            ["Amount"] = drops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = "12",
            ["SigningPubKey"] = genesis.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(payment, genesis, cancellationToken);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        while (await sink.SequenceAsync(genesis.ClassicAddress, validated: true, cancellationToken) <= sequence)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return fresh;
    }
}
