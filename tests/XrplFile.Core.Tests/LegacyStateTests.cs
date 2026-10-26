using System.Text.RegularExpressions;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec 4.5: unfinished version-2 and version-3 runs written by the application before this change keep resuming.</summary>
public sealed class LegacyStateTests : IDisposable
{
    private static readonly XrplWallet WriterWallet = XrplWallet.FromSeed("sEdSKaCy2JT7JaM7v95H9SxkhP9wS2r");
    private static readonly XrplWallet PayerWallet = XrplWallet.FromSeed("sEdV19BLfeQeKdEXyYA4NhjPJe6XBfG");
    private static readonly byte[] LegacyFile = VectorStream.Bytes("legacy-state", 2_500);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-legacy-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _directory, TicketsPerBatch = 100, FencePoll = TimeSpan.FromMilliseconds(1) };

    private string Fixture(string name)
    {
        string path = Path.Combine(_directory, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "state", name), path);
        return path;
    }

    private static FakeLedgerSource OneAccountNode()
    {
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = 4_999 };
        ledger.Balances[WriterWallet.ClassicAddress] = 500_000_000;
        return ledger;
    }

    /// <summary>
    /// The fixtures are hand-written; this pins them to the format: loaded and saved again, each is
    /// itself minus its <c>ChunkHashes</c>, byte for byte up to line endings.
    /// </summary>
    [Theory]
    [InlineData("v2-tickets.json")]
    [InlineData("v2-before-strategies.json")]
    [InlineData("v2-done.json")]
    [InlineData("v3-batch.json")]
    public void Fixtures_are_byte_for_byte_what_Save_writes(string name)
    {
        string path = Fixture(name);
        string fixture = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        // ChunkHashes in the middle of the object, then as its last member (no comma after it).
        string expected = Regex.Replace(fixture, "\n  \"ChunkHashes\": \\{[^}]*\\},", string.Empty);
        expected = Regex.Replace(expected, ",\n  \"ChunkHashes\": \\{[^}]*\\}\n", "\n");

        PublicationState.Load(path).Save(path);

        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void A_finished_version_2_file_with_chunk_hashes_loads_and_saves_without_them()
    {
        string path = Fixture("v2-done.json");

        PublicationState state = PublicationState.Load(path);
        state.Save(path);

        Assert.Equal("done", state.Phase);
        Assert.Equal(new string('D', 64), state.ManifestHash);
        Assert.Equal(PublicationState.SequenceStrategyName, state.Strategy);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v2-tickets.json")]
    [InlineData("v2-before-strategies.json")]
    public async Task An_unfinished_version_2_file_resumes_and_finishes(string name)
    {
        string path = Fixture(name);
        FakeLedgerSource ledger = OneAccountNode();
        FakeLedgerSink sink = new(ledger);
        WalletSigner signer = new(WriterWallet);
        await using ISubmissionStrategy strategy = name == "v2-tickets.json"
            ? new TicketStrategy(sink, ledger, signer, new TicketStrategyOptions { FeeDrops = 10 })
            : new SequenceStrategy(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });

        PublicationSession session = await PublicationSession.ResumeAsync(strategy, ledger, LegacyFile, path, Options, Token);
        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(LegacyFile, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.Equal(3, report.ChunkCount);
        Assert.Equal("done", PublicationState.Load(path).Phase);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfinished_version_3_file_resumes_and_finishes_on_Batch()
    {
        string path = Fixture("v3-batch.json");
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = 40 };
        FakeBatchLedgerSink sink = new(ledger);
        WalletSigner writer = new(WriterWallet);
        WalletSigner payer = new(PayerWallet);
        sink.SetSequence(writer.Account, 100);
        sink.SetSequence(payer.Account, 500);
        ledger.Balances[writer.Account] = 100_000_000;
        ledger.Balances[payer.Account] = 100_000_000;

        PublicationSession session = await PublicationSession.ResumeAsync(
            (budget, fence) => new BatchStrategy(sink, ledger, writer, payer, budget, fence, BatchTestNode.FastOptions),
            ledger, LegacyFile, path, payer.Account, "key-payer", Options, null, Token);
        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(LegacyFile, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.Equal(payer.Account, report.Payer);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
