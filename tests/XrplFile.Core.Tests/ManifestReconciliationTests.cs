using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec B27: a resume finds the manifest a dead run already sent, and only that one.</summary>
public class ManifestReconciliationTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-b27-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    /// <summary>What a crash between the manifest validating and "done" being written leaves on disk.</summary>
    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(statePath);
    }

    [Fact]
    public async Task A_run_that_died_after_its_manifest_validated_is_finished_by_the_resume_without_sending()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27", 2500);

        await using SequenceStrategy first = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession run = await PublicationSession.StartAsync(
            first, ledger, file, "text/plain", "f.txt", Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);
        int submitted = sink.Submitted.Count;

        // Drained: the resume needs no money when there is nothing to send.
        ledger.Balances[Wallet.ClassicAddress] = 0;

        await using SequenceStrategy second = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession resumed = await PublicationSession.ResumeAsync(second, ledger, file, run.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal("done", PublicationState.Load(run.StatePath).Phase);
        Assert.Equal(original.ManifestHash, PublicationState.Load(run.StatePath).ManifestHash);
    }

    [Fact]
    public async Task A_manifest_broadcast_before_the_format_member_existed_is_found_and_not_sent_twice()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27", 2500);

        await using SequenceStrategy first = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession run = await PublicationSession.StartAsync(
            first, ledger, file, "text/plain", "f.txt", Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);
        BecomePreFormat(ledger);
        int submitted = sink.Submitted.Count;

        await using SequenceStrategy second = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession resumed = await PublicationSession.ResumeAsync(second, ledger, file, run.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.ManifestHash, report.ManifestHash);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal(original.ManifestHash, PublicationState.Load(run.StatePath).ManifestHash);
        Assert.Equal(1, await ManifestRecordsAsync(ledger));
    }

    /// <summary>Rewrites the landed manifest memo into the bytes an older version of the library wrote.</summary>
    internal static void BecomePreFormat(FakeLedgerSource ledger)
    {
        ledger.Replace(
            tx => MemoLayout.IsManifest(tx.MemoTypeHex),
            tx => tx with { MemoDataHex = MemoLayout.Utf8Hex(Manifest.Decode(MemoLayout.FromHexUtf8(tx.MemoDataHex!)).EncodeLegacy()) });
    }

    internal static async Task<int> ManifestRecordsAsync(FakeLedgerSource ledger)
    {
        int count = 0;
        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(Wallet.ClassicAddress, 0, ledger.CurrentLedger, true, Token))
        {
            if (tx.Succeeded && MemoLayout.IsManifest(tx.MemoTypeHex)) count++;
        }

        return count;
    }

    [Fact]
    public async Task The_ticket_path_finds_the_manifest_too()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27-tickets", 30_000);
        TicketStrategyOptions options = new() { FeeDrops = Fee, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1) };

        await using TicketStrategy first = new(sink, ledger, Signer, options);
        PublicationSession run = await PublicationSession.StartAsync(
            first, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);
        int submitted = sink.Submitted.Count;

        await using TicketStrategy second = new(sink, ledger, Signer, options);
        PublicationSession resumed = await PublicationSession.ResumeAsync(second, ledger, file, run.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
    }

    [Fact]
    public async Task Another_publication_of_the_same_bytes_with_the_same_root_is_not_adopted()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27-twins", 2500);

        // Run one sends its chunks and stops before its manifest.
        ScriptedStrategy one = new(new SequenceStrategy(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee })) { PauseAtCall = 2 };
        PublicationSession first = await PublicationSession.StartAsync(
            one, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        Task<PublicationReport> firstRun = first.RunAsync(null, Token);
        await one.Paused.WaitAsync(TimeSpan.FromSeconds(20), Token);

        // Run two starts after run one's chunks, so its window holds run one's manifest but not its chunks.
        sink.CloseLedger();
        ledger.CurrentLedger += 1;
        ScriptedStrategy two = new(new SequenceStrategy(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee })) { FailAtCall = 2 };
        PublicationSession second = await PublicationSession.StartAsync(
            two, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);

        one.Resume();
        PublicationReport firstReport = await firstRun;
        Assert.True(firstReport.Manifest.FirstLedger < second.State.StartedInLedger);

        // Run two sends its own chunks and dies before its manifest.
        await Assert.ThrowsAsync<XrplFileException>(() => second.RunAsync(null, Token));

        await using SequenceStrategy again = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession resumed = await PublicationSession.ResumeAsync(again, ledger, file, second.StatePath, Options, Token);
        PublicationReport secondReport = await resumed.RunAsync(null, Token);

        Assert.Equal(firstReport.Manifest.Root, secondReport.Manifest.Root);
        Assert.NotEqual(firstReport.Link, secondReport.Link);
        Assert.Equal(file, (await new XrplFileReader(ledger).ReadAsync(secondReport.Link, Token)).Bytes);
    }
}
