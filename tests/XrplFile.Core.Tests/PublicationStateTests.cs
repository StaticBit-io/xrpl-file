using System.Text.Json;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationStateTests : IDisposable
{
    private static readonly string PayerAddress = XrplWallet.Generate().ClassicAddress;

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-state-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static PublicationState Version2(string? strategy) => new(
        PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "a.txt", 2500, 1000, "AB",
        42, "chunks", Strategy: strategy);

    private static PublicationState Version3() => new(
        PublicationState.BatchVersion, "testnet", "rWriter", "key-1", "text/plain", "a.txt", 2500, 1000, "AB",
        42, "chunks",
        Strategy: PublicationState.BatchStrategyName, SourcePath: @"C:\files\a.txt", Payer: PayerAddress, PayerKeyReference: "key-2", Fence: 0);

    private void WriteJson(string name, string json) => File.WriteAllText(PathOf(name), json);

    [Fact]
    public void A_version_2_state_keeps_its_strategy_and_writes_no_empty_new_fields()
    {
        Version2(PublicationState.TicketStrategyName).Save(PathOf("v2.json"));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PathOf("v2.json")));
        string[] properties = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        Assert.Contains("Strategy", properties);
        Assert.DoesNotContain("Payer", properties);
        Assert.DoesNotContain("PayerKeyReference", properties);
        Assert.DoesNotContain("Fence", properties);
        Assert.DoesNotContain("SourcePath", properties);
        Assert.DoesNotContain("ManifestHash", properties);
        Assert.Equal("tickets", PublicationState.Load(PathOf("v2.json")).Strategy);
    }

    [Fact]
    public void The_working_time_round_trips_and_is_absent_until_set()
    {
        Version3().Save(PathOf("unset.json"));
        Assert.DoesNotContain("WorkedSeconds", File.ReadAllText(PathOf("unset.json")), StringComparison.Ordinal);
        Assert.Null(PublicationState.Load(PathOf("unset.json")).WorkedSeconds);

        (Version3() with { WorkedSeconds = 645 }).Save(PathOf("set.json"));
        Assert.Contains("\"WorkedSeconds\": 645", File.ReadAllText(PathOf("set.json")), StringComparison.Ordinal);
        Assert.Equal(645, PublicationState.Load(PathOf("set.json")).WorkedSeconds);
    }

    [Fact]
    public void A_version_2_state_written_before_strategies_were_recorded_loads_with_none()
    {
        WriteJson("old.json", """
            {"Version":2,"Network":"testnet","Writer":"rWriter","KeyReference":"key-1","Type":"text/plain","Name":null,
             "Size":2500,"Chunk":1000,"Root":"AB","StartedInLedger":42,"Phase":"chunks","ChunkHashes":{}}
            """);

        Assert.Null(PublicationState.Load(PathOf("old.json")).Strategy);
    }

    [Fact]
    public void A_version_2_state_that_claims_to_be_a_batch_run_is_unreadable()
    {
        Version2("batch").Save(PathOf("v2batch.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("v2batch.json")));
    }

    [Fact]
    public void A_version_3_state_round_trips()
    {
        Version3().Save(PathOf("v3.json"));

        PublicationState loaded = PublicationState.Load(PathOf("v3.json"));

        Assert.Equal(PublicationState.BatchVersion, loaded.Version);
        Assert.Equal(PayerAddress, loaded.Payer);
        Assert.Equal("key-2", loaded.PayerKeyReference);
        Assert.Equal(0, loaded.Fence);
        Assert.Equal(@"C:\files\a.txt", loaded.SourcePath);
    }

    [Theory]
    [InlineData("no fence")]
    [InlineData("no payer")]
    [InlineData("no payer key")]
    [InlineData("payer is not an address")]
    [InlineData("not batch")]
    public void A_version_3_state_missing_or_misstating_a_required_field_is_unreadable(string defect)
    {
        PublicationState broken = defect switch
        {
            "no fence" => Version3() with { Fence = null },
            "no payer" => Version3() with { Payer = null },
            "no payer key" => Version3() with { PayerKeyReference = null },
            "payer is not an address" => Version3() with { Payer = "../../etc" },
            _ => Version3() with { Strategy = PublicationState.TicketStrategyName },
        };

        broken.Save(PathOf("broken.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("broken.json")));
    }

    [Fact]
    public void A_version_this_library_does_not_know_is_unreadable()
    {
        (Version3() with { Version = 4 }).Save(PathOf("v4.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("v4.json")));
    }

    [Fact]
    public void Unfinished_lists_a_batch_run_as_readable_and_a_broken_one_as_unreadable()
    {
        Version3().Save(PathOf("good.json"));
        (Version3() with { Fence = null }).Save(PathOf("bad.json"));

        IReadOnlyList<UnfinishedRun> runs = PublicationSession.Unfinished(_directory);

        Assert.NotNull(Assert.Single(runs, run => run.Path.EndsWith("good.json", StringComparison.Ordinal)).State);
        Assert.NotNull(Assert.Single(runs, run => run.Path.EndsWith("bad.json", StringComparison.Ordinal)).Unreadable);
    }
}
