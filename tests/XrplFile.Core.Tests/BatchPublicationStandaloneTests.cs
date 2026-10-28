using System.Text;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec 7.4: the Batch path end to end on the stand, with keys that never leave a vault.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class BatchPublicationStandaloneTests : IDisposable
{
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");
    private static readonly BatchStrategyOptions Options = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(500),
        ProgressSweep = TimeSpan.FromSeconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-batch-stand-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    private SessionOptions Session => new() { StateDirectory = Path.Combine(_directory, "state"), FencePoll = TimeSpan.FromMilliseconds(500) };

    private sealed record Keys(KeyVault Vault, VaultSigner Writer, VaultSigner Payer, VaultSigner OtherPayer, XrplWallet WriterWallet) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Writer.ClearCache();
            Payer.ClearCache();
            OtherPayer.ClearCache();
            return Vault.DisposeAsync();
        }
    }

    private async Task<Keys> KeysAsync(IXrplClient client, XrplClientLedgerSink sink, CancellationToken token)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
        XrplWallet payer = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
        XrplWallet other = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);

        KeyVault vault = new(Path.Combine(_directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);
        await vault.CreateAsync(Password, token);

        async Task<VaultSigner> StoreAsync(string label, XrplWallet wallet) =>
            await VaultSigner.CreateAsync(
                vault, await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, label, "1", wallet.ClassicAddress, wallet.Seed), token), token,
                cacheWallet: true);

        return new Keys(vault, await StoreAsync("writer", writer), await StoreAsync("payer", payer), await StoreAsync("other", other), writer);
    }

    private static BatchStrategyFactory Factory(ILedgerSink sink, ILedgerSource source, Keys keys, VaultSigner payer, List<BatchStrategy>? built = null) =>
        (budget, fence) =>
        {
            BatchStrategy strategy = new(sink, source, keys.Writer, payer, budget, fence, Options);
            built?.Add(strategy);
            return strategy;
        };

    private Task<PublicationSession> StartAsync(ILedgerSink sink, ILedgerSource source, Keys keys, byte[] file, CancellationToken token, List<BatchStrategy>? built = null) =>
        PublicationSession.StartAsync(
            Factory(sink, source, keys, keys.Payer, built), source, file, "application/octet-stream", "batch.bin",
            keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, token);

    /// <summary>Every chunk index the writer's successful records carry from <paramref name="from"/>, with duplicates.</summary>
    private static async Task<List<(long Index, string? Parent)>> ChunkRecordsAsync(ILedgerSource source, string writer, long from, CancellationToken token)
    {
        List<(long, string?)> chunks = [];
        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, from, await source.CurrentLedgerAsync(token), true, token))
        {
            if (tx.Succeeded && MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index) chunks.Add((index, tx.ParentBatchID));
        }

        return chunks;
    }

    private static async Task<int> ManifestRecordsAsync(ILedgerSource source, string writer, long from, CancellationToken token)
    {
        int count = 0;
        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, from, await source.CurrentLedgerAsync(token), true, token))
        {
            if (tx.Succeeded && MemoLayout.IsManifest(tx.MemoTypeHex)) count++;
        }

        return count;
    }

    /// <summary>A crash between the manifest validating and "done" being written.</summary>
    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(statePath);
    }

    [Fact]
    public async Task A_megabyte_goes_through_two_vault_signers_and_reads_back_and_signing_is_measured()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-1mb", 1_000_000);
            List<BatchStrategy> built = [];

            PublicationSession session = await StartAsync(sink, source, keys, file, token, built);
            PublicationReport report = await session.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);
            Assert.Equal(keys.Writer.Account, read.Writer);

            double share = built[0].SigningTime / report.Took;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"1 MB: {report.Took.TotalSeconds:F1} s, signing {built[0].SigningTime.TotalSeconds:F2} s ({share:P0}); payer paid {report.PayerFeeDrops} drops");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_whose_connection_the_node_closes_in_the_middle_still_lands_every_chunk()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        await using DroppingProxy proxy = new(new Uri(Endpoint));
        IXrplClient client = ClientKeeper.CreateClient(proxy.Endpoint.ToString());
        await client.Connect(token);

        try
        {
            XrplClientLedgerSource source = new(client);
            DroppingSink sink = new(new XrplClientLedgerSink(client), proxy, dropAt: 20);
            await using Keys keys = await KeysAsync(client, new XrplClientLedgerSink(client), token);
            byte[] file = VectorStream.Bytes("batch-drop", 500_000);

            PublicationSession session = await StartAsync(sink, source, keys, file, token);
            PublicationReport report = await session.RunAsync(null, token);

            Assert.True(sink.Dropped);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_stopped_with_batches_in_flight_resumes_with_another_payer_behind_its_fence_and_pays_no_chunk_twice()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            RecordingSource source = new(new XrplClientLedgerSource(client));
            await using Keys keys = await KeysAsync(client, inner, token);
            byte[] file = VectorStream.Bytes("batch-stop", 500_000);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            StoppingSink stopping = new(inner, stop, stopAfter: 10);
            PublicationSession first = await StartAsync(stopping, source, keys, file, stop.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));

            long fence = PublicationState.Load(first.StatePath).Fence!.Value;
            Assert.True(fence > 0);
            source.StartRecording();

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(inner, source, keys, keys.OtherPayer), source, file, first.StatePath,
                keys.OtherPayer.Account, "other", Session, null, token);

            Assert.True(source.FirstHistoryReadTo > fence + 2, $"history read up to {source.FirstHistoryReadTo} with the fence at {fence}");

            PublicationReport report = await resumed.RunAsync(null, token);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);

            List<(long Index, string? Parent)> chunks = await ChunkRecordsAsync(source, keys.Writer.Account, first.State.StartedInLedger, token);
            Assert.Equal(chunks.Count, chunks.Select(chunk => chunk.Index).Distinct().Count());
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_batch_run_stopped_after_its_manifest_validated_is_finished_without_sending()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-b27", 50_000);

            PublicationSession run = await StartAsync(sink, source, keys, file, token);
            PublicationReport original = await run.RunAsync(null, token);
            ForgetTheEnding(run.StatePath);
            uint writerSequence = await sink.SequenceAsync(keys.Writer.Account, validated: true, token);

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(sink, source, keys, keys.Payer), source, file, run.StatePath, keys.Payer.Account, "payer", Session, null, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(original.Link, report.Link);
            Assert.Equal(writerSequence, await sink.SequenceAsync(keys.Writer.Account, validated: true, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_ticket_run_stopped_after_its_manifest_validated_is_finished_without_sending()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("ticket-b27", 120_000);
            SessionOptions options = Session;

            PublicationEstimate priced = await PublicationSession.EstimateAsync(source, keys.Writer.Account, file.Length, options, token);
            TicketStrategyOptions ticketOptions = new() { FeeDrops = (int)priced.FeeDropsPerTransaction };

            await using TicketStrategy first = new(sink, source, keys.Writer, ticketOptions);
            PublicationSession run = await PublicationSession.StartAsync(
                first, source, file, "application/octet-stream", null, keys.Writer.Account, "writer", "1", options, token);
            PublicationReport original = await run.RunAsync(null, token);
            ForgetTheEnding(run.StatePath);
            int manifests = await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token);

            await using TicketStrategy second = new(sink, source, keys.Writer, ticketOptions);
            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, run.StatePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(original.Link, report.Link);
            Assert.Equal(manifests, await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_stopped_with_its_manifest_in_flight_waits_for_the_fence_then_finds_it()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, inner, token);
            byte[] file = VectorStream.Bytes("batch-manifest-flight", 50_000);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            ManifestSink manifestSink = new(inner, stop, transmitFirst: true);
            PublicationSession run = await StartAsync(manifestSink, source, keys, file, stop.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.RunAsync(null, stop.Token));
            Assert.True(manifestSink.SawManifest);

            List<PublishProgress> waited = [];
            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(inner, source, keys, keys.Payer), source, file, run.StatePath, keys.Payer.Account, "payer", Session,
                new SynchronousProgress<PublishProgress>(waited.Add), token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.NotEmpty(waited);
            Assert.Equal(1, await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token));
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_second_publication_of_the_same_bytes_by_the_same_writer_does_not_adopt_the_firsts_manifest()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-twins", 20_000);
            SessionOptions options = Session;

            // One: a one-account publication that stops before its manifest.
            PublicationEstimate priced = await PublicationSession.EstimateAsync(source, keys.Writer.Account, file.Length, options, token);
            ScriptedStrategy one = new(new SequenceStrategy(sink, keys.Writer, new SequenceStrategyOptions { FeeDrops = (int)priced.FeeDropsPerTransaction }))
            {
                PauseAtCall = 2,
            };
            PublicationSession first = await PublicationSession.StartAsync(
                one, source, file, "application/octet-stream", "batch.bin", keys.Writer.Account, "writer", "1", options, token);
            Task<PublicationReport> firstRun = first.RunAsync(null, token);
            await one.Paused.WaitAsync(TimeSpan.FromMinutes(5), token);

            // Two: a Batch publication of the same bytes, started now, so one's manifest lands in its window.
            await StandaloneFixture.CloseLedgersUntilAsync(client, await source.CurrentLedgerAsync(token) + 2, token);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            ManifestSink beforeManifest = new(sink, stop, transmitFirst: false);
            PublicationSession second = await StartAsync(beforeManifest, source, keys, file, stop.Token);

            one.Resume();
            PublicationReport firstReport = await firstRun;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.RunAsync(null, stop.Token));

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(sink, source, keys, keys.Payer), source, file, second.StatePath, keys.Payer.Account, "payer", Session, null, token);
            PublicationReport secondReport = await resumed.RunAsync(null, token);

            Assert.Equal(firstReport.Manifest.Root, secondReport.Manifest.Root);
            Assert.NotEqual(firstReport.Link, secondReport.Link);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(secondReport.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    /// <summary>The reader's own default stops at 20 MB; these files are larger, and the library's default is not what is tested.</summary>
    private static ReaderOptions FiftyMegabytes { get; } = new() { MaxBytes = 64L * 1024 * 1024 };

    private string WriteFile(string name, int size)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, VectorStream.Bytes(name, size));
        return path;
    }

    private static async Task AssertReadsBackAsync(ILedgerSource source, XrplFileLink link, string path, CancellationToken token)
    {
        ReadResult read = await new XrplFileReader(source, FiftyMegabytes).ReadAsync(link, token);
        byte[] expected = await File.ReadAllBytesAsync(path, token);
        Assert.True(read.Bytes.AsSpan().SequenceEqual(expected), "the bytes read back differ from the file");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Fifty_megabytes_go_through_Batch_from_a_held_file_and_read_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(90));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            string path = WriteFile("stand-50mb.bin", 50_000_000);
            PublicationReport report;

            await using (FilePublicationSource file = FilePublicationSource.Open(path))
            {
                PublicationSession session = await PublicationSession.StartAsync(
                    Factory(sink, source, keys, keys.Payer), source, file, "application/octet-stream", "stand-50mb.bin",
                    keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, null, token);
                report = await session.RunAsync(null, token);
            }

            Assert.Equal(50_000, report.ChunkCount);
            await AssertReadsBackAsync(source, report.Link, path, token);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"50 MB: {report.Took.TotalMinutes:F1} min, payer paid {report.PayerFeeDrops:N0} drops");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_stopped_fifty_megabyte_run_resumes_from_its_path()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(90));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, inner, token);
            string path = WriteFile("stand-50mb-resume.bin", 50_000_000);
            string statePath;
            long startedIn;

            using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                await using FilePublicationSource file = FilePublicationSource.Open(path);
                StoppingSink stopping = new(inner, stop, stopAfter: 1_000);
                PublicationSession first = await PublicationSession.StartAsync(
                    Factory(stopping, source, keys, keys.Payer), source, file, "application/octet-stream", "stand-50mb-resume.bin",
                    keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, null, stop.Token);
                statePath = first.StatePath;
                startedIn = first.State.StartedInLedger;

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
            }

            // Opened again by its path, as the application's resume opens the file it recorded (spec S7).
            PublicationReport report;

            await using (FilePublicationSource reopened = FilePublicationSource.Open(path))
            {
                PublicationSession resumed = await PublicationSession.ResumeAsync(
                    Factory(inner, source, keys, keys.Payer), source, reopened, statePath, keys.Payer.Account, "payer", Session, null, token);
                report = await resumed.RunAsync(null, token);
            }

            await AssertReadsBackAsync(source, report.Link, path, token);

            List<(long Index, string? Parent)> chunks = await ChunkRecordsAsync(source, keys.Writer.Account, startedIn, token);
            Assert.Equal(50_000, chunks.Select(chunk => chunk.Index).Distinct().Count());
        }
        finally
        {
            await client.Disconnect();
        }
    }

    /// <summary>Forwards everything, and has the proxy drop every connection at its <paramref name="dropAt"/>-th blob.</summary>
    private sealed class DroppingSink(ILedgerSink inner, DroppingProxy proxy, int dropAt) : ILedgerSink
    {
        private int _count;

        public bool Dropped { get; private set; }

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == dropAt)
            {
                proxy.DropAll();
                Dropped = true;
            }

            return inner.SubmitBlobAsync(txBlob, cancellationToken);
        }
    }

    /// <summary>
    /// Stops the run at the manifest's blob: after transmitting it (<paramref name="transmitFirst"/>)
    /// — the manifest in flight — or instead of transmitting it.
    /// </summary>
    private sealed class ManifestSink(ILedgerSink inner, CancellationTokenSource stop, bool transmitFirst) : ILedgerSink
    {
        public bool SawManifest { get; private set; }

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
            bool manifest = tx["Memos"] is JsonArray memos && memos.Count > 0
                && string.Equals((string?)memos[0]?["Memo"]?["MemoType"], MemoLayout.ManifestMarkerHex, StringComparison.OrdinalIgnoreCase);

            if (!manifest) return await inner.SubmitBlobAsync(txBlob, cancellationToken);

            SawManifest = true;

            if (transmitFirst)
            {
                string answer = await inner.SubmitBlobAsync(txBlob, cancellationToken);
                await stop.CancelAsync();
                return answer;
            }

            await stop.CancelAsync();
            throw new OperationCanceledException(stop.Token);
        }
    }

    /// <summary>
    /// Forwards, and remembers how far the first history read after <see cref="StartRecording"/> reached.
    /// A resume reads its range as several ledger ranges at once, so that read is the first call and
    /// the calls for later ranges after it, up to the next call from the same first ledger.
    /// </summary>
    private sealed class RecordingSource(ILedgerSource inner) : ILedgerSource
    {
        private readonly Lock _gate = new();
        private bool _recording;
        private long? _firstFrom;
        private bool _firstOver;
        private long _firstTo;

        public long FirstHistoryReadTo
        {
            get { lock (_gate) return _firstTo; }
        }

        public long LoadWarnings => inner.LoadWarnings;

        public void StartRecording()
        {
            lock (_gate) _recording = true;
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => inner.TransactionAsync(hash, cancellationToken);

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_recording && !_firstOver)
                {
                    if (_firstFrom is null)
                    {
                        _firstFrom = fromLedger;
                    }
                    else if (fromLedger == _firstFrom)
                    {
                        _firstOver = true;
                    }

                    if (!_firstOver && fromLedger >= _firstFrom) _firstTo = Math.Max(_firstTo, toLedger);
                }
            }

            return inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);
        }

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);

        public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) => inner.SnapshotAsync(accounts, cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) => inner.IsAmendmentEnabledAsync(name, cancellationToken);
    }
}
