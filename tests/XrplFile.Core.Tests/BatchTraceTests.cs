using System.Text.RegularExpressions;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public partial class BatchTraceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A key, a signature or a signed blob: hex longer than a 64-character hash.</summary>
    [GeneratedRegex("[0-9A-Fa-f]{65,}")]
    private static partial Regex LongHex();

    [Fact]
    public async Task Each_trace_entry_says_when_it_happened_and_carries_no_key_material()
    {
        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        BatchTestNode node = new(sink =>
        {
            for (int copy = 1; copy <= 4; copy++) sink.AnswerAt[(BatchTestNode.PayerStart, copy)] = "telCAN_NOT_QUEUE";
        });
        node.Sink.SetSequence(writer.ClassicAddress, BatchTestNode.WriterStart);
        node.Sink.SetSequence(payer.ClassicAddress, BatchTestNode.PayerStart);
        await using BatchStrategy strategy = node.Strategy(writer: new WalletSigner(writer), payer: new WalletSigner(payer));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        IReadOnlyList<BatchTraceEntry> entries = strategy.TraceEntries;
        Assert.Equal(strategy.Trace, entries.Select(entry => entry.Event));
        Assert.Contains(entries, entry => entry.Event.StartsWith("resend:", StringComparison.Ordinal));
        Assert.Equal(entries.Select(entry => entry.Elapsed).Order(), entries.Select(entry => entry.Elapsed));

        foreach (BatchTraceEntry entry in entries)
        {
            Assert.DoesNotContain(writer.Seed, entry.Event, StringComparison.Ordinal);
            Assert.DoesNotContain(payer.Seed, entry.Event, StringComparison.Ordinal);
            Assert.DoesNotMatch(LongHex(), entry.Event);
        }
    }
}
