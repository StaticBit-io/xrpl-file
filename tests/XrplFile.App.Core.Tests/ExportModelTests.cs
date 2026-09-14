using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class ExportModelTests : VaultHarness
{
    private const string ContainerPassword = "export password";

    [Fact]
    public async Task Exporting_produces_a_container_the_same_password_opens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault);

        byte[]? container = await model.ToContainerAsync(reference, Password(ContainerPassword), token);

        Assert.Null(model.Problem);
        Assert.NotNull(container);
        Assert.True(
            BackupContainer.TryUnpack(
                container, ContainerPassword, fromFile: true, out BackupPayload? payload, out BackupUnpackError error),
            $"The container did not open: {error}.");
        Assert.Equal("sSecretOfone", payload!.Wallets.Single().KeyValue);
    }

    [Fact]
    public async Task An_empty_backup_password_is_refused_and_nothing_is_sealed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault);

        // Pressing "Create backup" without touching the password field. Nothing in this
        // application refused it before: the container was sealed under PBKDF2 of the empty string
        // and the entry was marked Exported, so the key list stopped warning that the key exists
        // only in this vault -- for a key whose only copy outside it anybody can open.
        byte[]? container = await model.ToContainerAsync(reference, Password(string.Empty), token);

        Assert.Null(container);
        Assert.NotNull(model.Problem);
        Assert.False((await vault.ListAsync(token)).Single(entry => entry.Reference == reference).Exported);
    }

    [Fact]
    public async Task A_backup_password_one_character_short_of_the_minimum_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault);

        string tooShort = new('x', PasswordPolicy.MinimumLength - 1);

        Assert.Null(await model.ToContainerAsync(reference, Password(tooShort), token));
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public async Task A_backup_password_exactly_at_the_minimum_is_accepted()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault);

        string justLongEnough = new('x', PasswordPolicy.MinimumLength);

        Assert.NotNull(await model.ToContainerAsync(reference, Password(justLongEnough), token));
        Assert.Null(model.Problem);
    }

    [Fact]
    public async Task A_refusal_is_cleared_by_the_export_that_follows_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault);

        await model.ToContainerAsync(reference, Password(string.Empty), token);
        Assert.NotNull(model.Problem);

        await model.ToContainerAsync(reference, Password(ContainerPassword), token);
        Assert.Null(model.Problem);
    }

    [Fact]
    public async Task A_vault_failure_becomes_a_sentence_rather_than_an_exception_for_a_component_to_map()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        ExportModel model = new(vault);

        Assert.Null(await model.ToContainerAsync("no-such-reference", Password(ContainerPassword), token));
        Assert.Equal(VaultErrorMessages.For(VaultError.NoSuchEntry), model.Problem);
    }

    [Fact]
    public void The_qr_payloads_text_decodes_back_to_the_container_through_Base45()
    {
        byte[] container = [1, 2, 3, 4, 5, 250, 251, 252, 0, 255];
        ExportModel model = new(NewVault());

        QrPayload qr = model.ToQr(container);

        Assert.True(Base45.TryDecode(qr.Text, out byte[] decoded));
        Assert.Equal(container, decoded);
        Assert.Equal(container.Length, qr.ContainerBytes);
    }

    [Fact]
    public void FitsOneCode_is_true_for_a_container_the_size_of_one_key()
    {
        // Nowhere near the 1234-byte boundary: a single exported key's container, well within it.
        byte[] container = new byte[300];
        ExportModel model = new(NewVault());

        Assert.True(model.ToQr(container).FitsOneCode);
    }

    [Fact]
    public void FitsOneCode_is_true_exactly_at_the_measured_boundary()
    {
        // Version 40, correction level H: 1852 alphanumeric characters, measured as 1234 container
        // bytes (design section 8). Base45.GetEncodedLength(1234) <= 1852 is pinned in the Keys
        // suite; this is the same boundary from ExportModel's side of it.
        byte[] atBoundary = new byte[1234];
        ExportModel model = new(NewVault());

        Assert.True(model.ToQr(atBoundary).FitsOneCode);
    }

    [Fact]
    public void FitsOneCode_is_false_one_byte_past_the_measured_boundary()
    {
        byte[] pastBoundary = new byte[1235];
        ExportModel model = new(NewVault());

        Assert.False(model.ToQr(pastBoundary).FitsOneCode);
    }
}
