using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Pins that <see cref="ReadModel"/> and <see cref="PublishModel"/> say the exact same sentence for
/// "no node URL is known for this network" (design 4.1: a person who hits this refusal on one
/// screen must see the same words on the other). Deliberately not turned into a shared helper or
/// constant -- H8 gives reading its own sentences, and coupling the two models through one shared
/// string would cut against that -- so this test is what catches the two independent copies
/// drifting apart instead, the way nothing previously did.
/// </summary>
public sealed class NodeUrlMessageParityTests : VaultHarness
{
    private readonly FakeFilePicker _picker = new();

    [Fact]
    public async Task The_no_node_url_sentence_is_identical_on_both_screens()
    {
        // A NetworkID the xrpl-file link format accepts, but Endpoints.For knows no built-in URL
        // for, and this test's settings store is never written to -- so both models resolve no URL
        // at all for it, the same situation PublishModelTests's own
        // "A_network_with_no_node_url_refuses_to_estimate_and_says_so" exercises for PublishModel
        // alone.
        const string network = "1";

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        NewVaultEntry entry = Entry("no-url key") with { Network = network };
        string reference = await vault.StoreAsync(entry, TestContext.Current.CancellationToken);

        FileNodeSettingsStore settings = new(
            Path.Combine(Path.GetTempPath(), "xrpl-file-node-parity-" + Guid.NewGuid().ToString("N"), "node-urls.json"));

        ReadModel readModel = new(new FakeConnectionFactory(), settings);
        readModel.Link = "xrpl-file://" + network + "/" + new string('A', 64);
        await readModel.LookUpAsync(TestContext.Current.CancellationToken);

        PublishModel publishModel = new(vault, _picker, settings);
        await publishModel.RefreshKeysAsync(TestContext.Current.CancellationToken);
        publishModel.SelectKey(reference);
        publishModel.DescribeFileForTests("photo.png", 2048);

        Assert.NotNull(readModel.Message);
        Assert.NotNull(publishModel.Message);
        Assert.Contains("Settings", readModel.Message, StringComparison.Ordinal);
        Assert.Equal(TestText.En(publishModel.Message), readModel.Message);
    }
}
