using System.Diagnostics;
using System.Security.Cryptography;
using Xrpl.Client;
using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Publications through the application's own run and connection factory, against the stand.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public sealed class PublicationRunStandaloneTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-run-stand-").FullName;

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// The acceptance run's defect 1 (testnet, 2026-10-19): a publication after a finished one sat on
    /// "Starting" and then failed with "The node did not answer" although the node answered a new
    /// connection at once. A connection that dies without a close still reads as open; the run must
    /// replace it after one unanswered request, not keep sending into it until the SDK's own ping
    /// notices -- measured here before the fix: about 85 s of silence, 147 s for the whole run.
    /// </summary>
    [Fact]
    public async Task A_second_publication_whose_connection_goes_silent_finishes_over_a_new_one()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(6));
        CancellationToken token = timeout.Token;

        IXrplClient funding = new XrplClient(Endpoint);
        await funding.Connect(token);
        XrplWallet writer;
        XrplWallet payer;

        try
        {
            XrplClientLedgerSink sink = new(funding);
            writer = await StandaloneFixture.FundedAccountAsync(funding, sink, 1_000_000_000, token);
            payer = await StandaloneFixture.FundedAccountAsync(funding, sink, 1_000_000_000, token);
        }
        finally
        {
            await funding.Disconnect();
        }

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        string writerKey = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "1", writer.ClassicAddress, writer.Seed), token);
        string payerKey = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "payer", "1", payer.ClassicAddress, payer.Seed), token);

        await using DroppingProxy proxy = new(new Uri(Endpoint.Replace("localhost", "127.0.0.1", StringComparison.Ordinal)));
        FileNodeSettingsStore nodes = new(Path.Combine(_directory, "node-urls.json"));
        nodes.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["1"] = proxy.Endpoint.ToString() });
        XrplConnectionFactory connections = new(nodes);
        RunPaths paths = new(_directory);
        PublicationRun run = new(connections, vault, paths, new PublicationHistory(paths.HistoryPath));

        PublishRequest Request(string name) => Requests.Fresh(
            RandomNumberGenerator.GetBytes(256 * 1024), "application/octet-stream", name, writerKey, writer.ClassicAddress, "1",
            payerKey, payer.ClassicAddress);

        Assert.True(run.Start(Request("first.bin")));
        await run.Completion.WaitAsync(token);
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);

        run.Reset();
        Stopwatch second = Stopwatch.StartNew();
        Assert.True(run.Start(Request("second.bin")));

        while (run.Snapshot.Progress is null && !run.Completion.IsCompleted) await Task.Delay(50, token);

        proxy.SilenceAll();
        await run.Completion.WaitAsync(token);

        RunSnapshot snapshot = run.Snapshot;
        Assert.True(snapshot.State == PublicationRunState.Succeeded, TestText.En(snapshot.Failure));

        // One SDK request timeout (40 s) to learn of the silence, then the rest of a ~10 s run.
        Assert.True(second.Elapsed < TimeSpan.FromSeconds(100), $"the second publication took {second.Elapsed}");
    }
}
