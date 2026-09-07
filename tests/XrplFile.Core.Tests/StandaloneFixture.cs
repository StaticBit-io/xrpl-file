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

        await PayAsync(sink, genesis, fresh.ClassicAddress, drops, cancellationToken);

        return fresh;
    }

    /// <summary>
    /// Sends drops from one wallet to an address and waits for the payment to be validated,
    /// which the sender's own sequence advancing is proof of.
    /// </summary>
    public static async Task PayAsync(
        XrplClientLedgerSink sink, XrplWallet from, string destination, long drops, CancellationToken cancellationToken)
    {
        uint sequence = await sink.SequenceAsync(from.ClassicAddress, validated: false, cancellationToken);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = from.ClassicAddress,
            ["Destination"] = destination,
            ["Amount"] = drops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = "12",
            ["SigningPubKey"] = from.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(payment, from, cancellationToken);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        while (await sink.SequenceAsync(from.ClassicAddress, validated: true, cancellationToken) <= sequence)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// Mints tickets on an account and waits for the mint to be validated, so a test can put an
    /// account into the state an aborted publication leaves behind: ledger objects nothing will
    /// ever spend, each locking one owner reserve.
    /// </summary>
    public static async Task MintTicketsAsync(
        XrplClientLedgerSink sink, ILedgerSource source, XrplWallet wallet, uint count, long feeDrops, CancellationToken cancellationToken)
    {
        uint sequence = await sink.SequenceAsync(wallet.ClassicAddress, validated: false, cancellationToken);

        Dictionary<string, object> mint = new(StringComparer.Ordinal)
        {
            ["TransactionType"] = "TicketCreate",
            ["Account"] = wallet.ClassicAddress,
            ["Sequence"] = sequence,
            ["TicketCount"] = count,
            ["Fee"] = feeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = wallet.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(mint, wallet, cancellationToken);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        // A mint that landed moves the account's sequence past every ticket it created.
        while (await sink.SequenceAsync(wallet.ClassicAddress, validated: true, cancellationToken) <= sequence)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        AccountState state = await source.AccountStateAsync(wallet.ClassicAddress, cancellationToken);
        Assert.Equal((int)count, state.OwnerCount);
    }

    /// <summary>
    /// Polls until the account holds more than it did, so a test can wait for money sent to
    /// somebody else's account to arrive — where there is no sequence of one's own to watch.
    /// The caller's token is the only deadline.
    /// </summary>
    public static async Task<long> WaitForBalanceAboveAsync(
        ILedgerSource source, string account, long floorDrops, CancellationToken cancellationToken)
    {
        while (true)
        {
            long balance = await source.BalanceDropsAsync(account, cancellationToken);

            if (balance > floorDrops) return balance;

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
