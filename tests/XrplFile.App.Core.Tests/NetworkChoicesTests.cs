using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class NetworkChoicesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-networkchoices-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private FileNodeSettingsStore NewStore() => new(Path.Combine(_directory, "node-urls.json"));

    private static void Write(FileNodeSettingsStore store, params string[] networks) =>
        store.Write(networks.ToDictionary(network => network, _ => "ws://localhost:6006", StringComparer.Ordinal));

    [Fact]
    public void With_nothing_configured_the_known_networks_are_offered_in_their_fixed_order()
    {
        NetworkChoices choices = new(NewStore());
        choices.Refresh();

        Assert.Equal(["mainnet", "testnet", "devnet"], choices.Choices.Select(choice => choice.Value));
        Assert.Equal(["mainnet", "testnet", "devnet"], choices.Choices.Select(choice => choice.Label));
    }

    [Fact]
    public void A_network_id_from_the_settings_store_follows_the_known_ones_and_is_labelled_as_such()
    {
        FileNodeSettingsStore store = NewStore();
        Write(store, "1");

        NetworkChoices choices = new(store);
        choices.Refresh();

        Assert.Equal(["mainnet", "testnet", "devnet", "1"], choices.Choices.Select(choice => choice.Value));
        Assert.Equal("NetworkID 1", choices.Choices[^1].Label);
    }

    [Fact]
    public void A_stored_name_is_normalised_and_not_offered_twice()
    {
        FileNodeSettingsStore store = NewStore();
        Write(store, "TESTNET", "Devnet", "21337");

        NetworkChoices choices = new(store);
        choices.Refresh();

        Assert.Equal(["mainnet", "testnet", "devnet", "21337"], choices.Choices.Select(choice => choice.Value));
    }

    [Fact]
    public void A_stored_name_no_link_can_carry_is_skipped()
    {
        FileNodeSettingsStore store = NewStore();
        Write(store, "standalone", "1");

        NetworkChoices choices = new(store);
        choices.Refresh();

        Assert.Equal(["mainnet", "testnet", "devnet", "1"], choices.Choices.Select(choice => choice.Value));
    }

    [Fact]
    public void Refresh_picks_up_a_network_added_to_the_store_afterwards()
    {
        FileNodeSettingsStore store = NewStore();
        NetworkChoices choices = new(store);
        choices.Refresh();
        Assert.Equal(3, choices.Choices.Count);

        Write(store, "1");
        choices.Refresh();

        Assert.Equal(4, choices.Choices.Count);
    }

    [Theory]
    [InlineData("mainnet", "mainnet")]
    [InlineData("TESTNET", "testnet")]
    [InlineData("  devnet ", "devnet")]
    [InlineData("standalone", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Pick_returns_the_canonical_value_only_for_a_network_in_the_list(string? candidate, string? expected)
    {
        NetworkChoices choices = new(NewStore());
        choices.Refresh();

        Assert.Equal(expected, choices.Pick(candidate));
    }

    [Fact]
    public void Pick_does_not_return_a_valid_network_that_is_not_offered()
    {
        NetworkChoices choices = new(NewStore());
        choices.Refresh();

        Assert.Null(choices.Pick("1"));
    }
}
