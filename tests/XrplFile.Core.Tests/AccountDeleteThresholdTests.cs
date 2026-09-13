using System.Text.Json;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// What the ledger actually does about deleting an account that minted tickets.
///
/// The design reads three things out of rippled's source: AccountDelete refuses while
/// Sequence + 255 exceeds the current ledger index, TicketCreate moves the sequence by the
/// ticket count plus one, and the fee is one owner reserve. All three decide whether a
/// throwaway account's base reserve can ever come back, so none of them is taken on trust.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class AccountDeleteThresholdTests
{
    private const string GenesisSeed = "snoPBrXtMeMyMHUVTgbuqAfg1SUTb";
    private const int SeqDelta = 255;

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task Minting_tickets_pushes_the_deletion_threshold_away_and_only_closed_ledgers_bring_it_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet funder = XrplWallet.FromSeed(GenesisSeed);
            XrplWallet throwaway = XrplWallet.Generate();

            await FundAsync(client, sink, funder, throwaway.ClassicAddress, 50_000_000, token);

            // A new account's sequence is the index of the ledger that created it.
            uint created = await sink.SequenceAsync(throwaway.ClassicAddress, validated: true, token);
            long createdIn = await CurrentLedgerAsync(client, token);
            Assert.InRange(created, createdIn - 5, createdIn);

            // The mint moves the sequence by the count plus one, and by nothing else.
            const uint mint = 5;
            uint beforeMint = await sink.SequenceAsync(throwaway.ClassicAddress, validated: false, token);
            await MintTicketsAsync(client, sink, throwaway, beforeMint, mint, token);
            uint afterMint = await sink.SequenceAsync(throwaway.ClassicAddress, validated: true, token);

            Assert.Equal(beforeMint + mint + 1, afterMint);

            // The mint is proven by the tickets it left behind, not by the sequence delta alone:
            // a node that advanced the sequence by mint + 1 for an unrelated reason would also
            // pass the assertion above. Confirm the N Ticket objects actually exist, at exactly
            // the sequence numbers TicketCreate is supposed to reserve.
            await AssertTicketsExistAsync(client, throwaway.ClassicAddress, beforeMint + 1, mint, token);

            // Too soon: the threshold sits at Sequence + 255, well past the current index.
            long now = await CurrentLedgerAsync(client, token);
            Assert.True(afterMint + SeqDelta > now, $"the test needs the threshold ahead of ledger {now}");

            // Pin the boundary from both sides: one ledger short of the threshold must still
            // refuse, and reaching the threshold must succeed. AccountDelete's own check runs
            // against the ledger currently being built, whose index is one past the last
            // validated ledger that CurrentLedgerAsync reads — so the Sequence + 255 rule,
            // measured against a validated-ledger read, actually lands at Sequence + 254.
            // Probed directly against a live node before writing this: Sequence + 253 refuses,
            // Sequence + 254 succeeds.
            //
            // A refused AccountDelete is still a tec* result, which the ledger applies — it
            // claims the fee and consumes the account's sequence number like any other
            // transaction. That moves the threshold implied by the account's own sequence, so
            // it is recomputed from the account after the refusal rather than trusted from the
            // value captured before it.
            uint sequenceBeforeBoundary = await sink.SequenceAsync(throwaway.ClassicAddress, validated: false, token);
            long boundary = sequenceBeforeBoundary + SeqDelta - 1;
            await CloseLedgersUntilAsync(client, boundary - 1, token);

            long tooSoonLedger = await CurrentLedgerAsync(client, token);
            string tooSoon = await AttemptDeleteAsync(client, sink, throwaway, funder.ClassicAddress, token);
            Assert.Equal("tecTOO_SOON", tooSoon);

            uint sequenceAfterRefusal = await sink.SequenceAsync(throwaway.ClassicAddress, validated: false, token);
            long recomputedBoundary = sequenceAfterRefusal + SeqDelta - 1;
            await CloseLedgersUntilAsync(client, recomputedBoundary, token);

            long acceptedLedger = await CurrentLedgerAsync(client, token);
            string accepted = await AttemptDeleteAsync(client, sink, throwaway, funder.ClassicAddress, token);
            Assert.True(
                accepted is "tesSUCCESS" or "terQUEUED",
                $"refused at ledger {tooSoonLedger} with {tooSoon}, then at ledger {acceptedLedger} the node answered {accepted}");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task An_ordinary_transaction_moves_the_threshold_by_one_so_it_never_closes_the_gap()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet funder = XrplWallet.FromSeed(GenesisSeed);
            XrplWallet account = XrplWallet.Generate();

            await FundAsync(client, sink, funder, account.ClassicAddress, 50_000_000, token);

            uint before = await sink.SequenceAsync(account.ClassicAddress, validated: true, token);
            long ledgerBefore = await CurrentLedgerAsync(client, token);
            long gapBefore = before + SeqDelta - ledgerBefore;

            // An AccountSet with no fields: the cheapest transaction there is.
            uint sequence = await sink.SequenceAsync(account.ClassicAddress, validated: false, token);
            Dictionary<string, object> noop = new()
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = account.ClassicAddress,
                ["Sequence"] = sequence,
                ["Fee"] = "12",
                ["SigningPubKey"] = account.PublicKey,
            };
            SubmitOutcome outcome = await sink.SubmitAsync(noop, new WalletSigner(account), token);
            Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);
            await WaitForSequenceAsync(sink, account.ClassicAddress, sequence + 1, token);

            uint after = await sink.SequenceAsync(account.ClassicAddress, validated: true, token);
            long ledgerAfter = await CurrentLedgerAsync(client, token);
            long gapAfter = after + SeqDelta - ledgerAfter;

            // Every transaction raises the sequence, and so raises the threshold with it. Sending
            // transactions can therefore never close the gap; only ledgers closing can. This is
            // the claim the test is named for: prove it by reading the gap itself, not only the
            // sequence delta a world that closed the gap some other way could still produce.
            Assert.Equal(before + 1, after);
            Assert.True(gapAfter >= gapBefore, $"the gap shrank from {gapBefore} to {gapAfter}");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    private static async Task<long> CurrentLedgerAsync(IXrplClient client, CancellationToken token)
    {
        XrplResponse<object> response = await client.GRequest<object, LedgerRequest>(
            new LedgerRequest { LedgerIndex = new Xrpl.Models.Common.LedgerIndex(Xrpl.Models.Ledger.LedgerIndexType.Validated) }, token);

        return response.Raw.ToJsonElement().GetProperty("ledger_index").GetInt64();
    }

    /// <summary>
    /// Closes ledgers until the validated index reaches the target. The stand's sidecar closes one
    /// every few seconds on its own; this drives them as fast as the node will take them, which is
    /// what makes a 255-ledger wait a matter of seconds rather than a quarter of an hour.
    ///
    /// The SDK has no typed <c>ledger_accept</c> request (it is an admin-only, standalone-only
    /// command with no production use), so this sends it the way <c>XrplClientLedgerSink</c> and
    /// the XrplCSharp integration tests do: a bare <see cref="BaseRequest"/> with the command name,
    /// through <see cref="IXrplClient.AnyRequest"/>.
    /// </summary>
    private static async Task CloseLedgersUntilAsync(IXrplClient client, long target, CancellationToken token)
    {
        while (await CurrentLedgerAsync(client, token) < target)
        {
            token.ThrowIfCancellationRequested();
            await client.AnyRequest(new BaseRequest { Command = "ledger_accept" }, token);
        }
    }

    private static async Task MintTicketsAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet wallet, uint sequence, uint count, CancellationToken token)
    {
        Dictionary<string, object> mint = new()
        {
            ["TransactionType"] = "TicketCreate",
            ["Account"] = wallet.ClassicAddress,
            ["Sequence"] = sequence,
            ["TicketCount"] = count,
            ["Fee"] = "12",
            ["SigningPubKey"] = wallet.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(mint, new WalletSigner(wallet), token);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        await WaitForSequenceAsync(sink, wallet.ClassicAddress, sequence + count + 1, token);
    }

    /// <summary>
    /// Reads the account's ledger objects and asserts that exactly <paramref name="count"/>
    /// Ticket entries exist, carrying the <c>TicketSequence</c> values TicketCreate is supposed
    /// to reserve: <paramref name="firstTicket"/> through <paramref name="firstTicket"/> + count - 1.
    /// Reads the raw JSON rather than the typed <c>account_objects</c> model, the way the rest of
    /// this file confirms what the node actually said.
    /// </summary>
    private static async Task AssertTicketsExistAsync(
        IXrplClient client, string account, uint firstTicket, uint count, CancellationToken token)
    {
        XrplResponse<object> response = await client.GRequest<object, AccountObjectsRequest>(
            new AccountObjectsRequest(account)
            {
                Type = LedgerEntryType.Ticket,
                LedgerIndex = new Xrpl.Models.Common.LedgerIndex(Xrpl.Models.Ledger.LedgerIndexType.Validated),
            }, token);

        JsonElement objects = response.Raw.ToJsonElement().GetProperty("account_objects");

        HashSet<uint> found = new();
        foreach (JsonElement entry in objects.EnumerateArray())
        {
            found.Add(entry.GetProperty("TicketSequence").GetUInt32());
        }

        HashSet<uint> expected = new();
        for (uint i = 0; i < count; i++) expected.Add(firstTicket + i);

        Assert.Equal(expected, found);
    }

    /// <summary>Submits an AccountDelete and returns what the node said, without asserting on it.</summary>
    private static async Task<string> AttemptDeleteAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet wallet, string destination, CancellationToken token)
    {
        // The fee is one owner reserve, per AccountDelete::calculateBaseFee. Read it rather than
        // guess: the figure has changed before and a wrong fee is telINSUF_FEE_P, not tecTOO_SOON.
        long ownerReserve = await OwnerReserveDropsAsync(client, token);
        uint sequence = await sink.SequenceAsync(wallet.ClassicAddress, validated: false, token);

        Dictionary<string, object> delete = new()
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = wallet.ClassicAddress,
            ["Destination"] = destination,
            ["Sequence"] = sequence,
            ["Fee"] = ownerReserve.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = wallet.PublicKey,
        };

        return (await sink.SubmitAsync(delete, new WalletSigner(wallet), token)).EngineResult;
    }

    private static async Task<long> OwnerReserveDropsAsync(IXrplClient client, CancellationToken token)
    {
        XrplResponse<object> response = await client.GRequest<object, ServerStateRequest>(new ServerStateRequest(), token);
        JsonElement state = response.Raw.ToJsonElement().GetProperty("state").GetProperty("validated_ledger");

        return state.GetProperty("reserve_inc").GetInt64();
    }

    private static async Task FundAsync(
        IXrplClient client, XrplClientLedgerSink sink, XrplWallet funder, string destination, long drops, CancellationToken token)
    {
        uint sequence = await sink.SequenceAsync(funder.ClassicAddress, validated: false, token);

        Dictionary<string, object> payment = new()
        {
            ["TransactionType"] = "Payment",
            ["Account"] = funder.ClassicAddress,
            ["Destination"] = destination,
            ["Amount"] = drops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Sequence"] = sequence,
            ["Fee"] = "12",
            ["SigningPubKey"] = funder.PublicKey,
        };

        SubmitOutcome outcome = await sink.SubmitAsync(payment, new WalletSigner(funder), token);
        Assert.True(outcome.EngineResult is "tesSUCCESS" or "terQUEUED", outcome.EngineResult);

        await WaitForSequenceAsync(sink, funder.ClassicAddress, sequence + 1, token);
    }

    private static async Task WaitForSequenceAsync(
        XrplClientLedgerSink sink, string account, uint expected, CancellationToken token)
    {
        while (await sink.SequenceAsync(account, validated: true, token) < expected)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }
}
