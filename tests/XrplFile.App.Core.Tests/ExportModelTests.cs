using System.Runtime.InteropServices;

using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class ExportModelTests : VaultHarness
{
    private const string ContainerPassword = "export password";

    /// <summary>
    /// A picker every test in this class other than the <c>SaveToFileAsync</c> tests below is not
    /// exercising -- filling <see cref="ExportModel"/>'s second constructor parameter without giving
    /// those tests a reason to care what it does.
    /// </summary>
    private static readonly IFilePickerService NoPicker =
        new ScriptedFilePickerService(() => throw new InvalidOperationException("This test does not save to a file."));

    [Fact]
    public async Task Exporting_produces_a_container_the_same_password_opens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault, NoPicker);

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
    public async Task Sealing_a_container_does_not_mark_the_entry_exported()
    {
        // The defect: the flag was set the instant the container existed in memory, before the
        // save dialog the person can cancel and before the disk that can refuse the bytes. Press
        // "Create backup", then "Back to keys", and the badge saying the key exists only here was
        // gone for a key that no file holds.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, NoPicker);

        Assert.NotNull(await model.ToContainerAsync(reference, Password(ContainerPassword), token));

        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.True(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task Recording_a_saved_backup_is_what_marks_the_entry_exported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, NoPicker);

        await model.ToContainerAsync(reference, Password(ContainerPassword), token);
        await model.RecordSavedAsync(reference, token);

        Assert.Null(model.Problem);

        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.False(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task A_save_the_platform_refused_leaves_the_key_flagged_as_existing_only_here()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, NoPicker);

        await model.ToContainerAsync(reference, Password(ContainerPassword), token);
        model.ReportSaveFailed();

        Assert.NotNull(model.Problem);

        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.True(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task SaveToFileAsync_writes_the_container_and_records_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, new ScriptedFilePickerService(() => true));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        bool saved = await model.SaveToFileAsync(reference, container, token);

        Assert.True(saved);
        Assert.Null(model.Problem);
        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.False(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task SaveToFileAsync_reports_neither_success_nor_failure_when_the_dialog_is_cancelled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, new ScriptedFilePickerService(() => false));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        bool saved = await model.SaveToFileAsync(reference, container, token);

        Assert.False(saved);
        Assert.Null(model.Problem);
        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.True(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task SaveToFileAsync_reports_a_write_the_platform_refused_with_a_bare_IOException()
    {
        // The .NET-typed half of the finding: IOException was already caught before this fix, so
        // this pins that the refactor did not lose it while widening the catch for the WinRT half
        // below.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, new ScriptedFilePickerService(() => throw new IOException("disk full")));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        bool saved = await model.SaveToFileAsync(reference, container, token);

        Assert.False(saved);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public async Task SaveToFileAsync_reports_a_full_disk_surfaced_as_COMException_rather_than_crashing()
    {
        // The finding itself: FileIO.WriteBytesAsync on a full disk surfaces ERROR_DISK_FULL
        // (0x80070070) as a bare COMException, because CsWinRT's ExceptionHelpers.GetExceptionForHR
        // maps only a fixed table of HRESULTs onto specific .NET exception types and 0x80070070 is
        // not one of them -- confirmed by reading WinRT.Runtime/ExceptionHelpers.cs in the CsWinRT
        // source, not assumed. Before this fix, ExportKey.razor's catch filter was
        // "IOException or UnauthorizedAccessException", which a COMException does not satisfy, so
        // this would have escaped uncaught into Blazor's own unhandled-error page. This test cannot
        // reach an actual full disk, so it reproduces the shape CsWinRT is documented to produce
        // instead: a COMException carrying that exact HRESULT.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        const int errorDiskFull = unchecked((int)0x80070070);
        ExportModel model = new(
            vault, new ScriptedFilePickerService(() => throw new COMException("0x80070070", errorDiskFull)));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        bool saved = await model.SaveToFileAsync(reference, container, token);

        Assert.False(saved);
        Assert.NotNull(model.Problem);
        VaultListModel list = new(vault);
        await list.RefreshAsync(token);
        Assert.True(list.Rows.Single().NeedsExporting);
    }

    [Fact]
    public async Task SaveToFileAsync_does_not_let_a_defect_in_this_applications_own_code_disappear_into_a_message()
    {
        // The other half of "where the line sits": a bug that is not a platform write failure --
        // here, standing in for one in this application's own code -- must not be swallowed by the
        // same catch that reports a full disk.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(
            vault, new ScriptedFilePickerService(() => throw new InvalidOperationException("not a platform failure")));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.SaveToFileAsync(reference, container, token));
    }

    [Fact]
    public async Task A_save_that_fails_after_an_earlier_one_succeeded_does_not_report_the_earlier_success()
    {
        // The nit alongside the finding: ExportKey.razor's own _saved flag used to survive a later,
        // failed attempt, showing "Saved." next to the new failure alert. This model holds no such
        // flag -- SaveToFileAsync's return value is the only thing the flag is ever set from -- so
        // this pins the property the component's fix now relies on: consecutive calls never carry a
        // stale true into a call that actually failed.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        bool firstAttempt = true;
        ExportModel model = new(vault, new ScriptedFilePickerService(() =>
        {
            if (firstAttempt)
            {
                firstAttempt = false;
                return true;
            }

            throw new IOException("disk full");
        }));
        byte[] container = (await model.ToContainerAsync(reference, Password(ContainerPassword), token))!;

        Assert.True(await model.SaveToFileAsync(reference, container, token));
        Assert.False(await model.SaveToFileAsync(reference, container, token));
    }

    [Fact]
    public async Task Recording_a_save_against_a_locked_vault_says_so_rather_than_throwing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ExportModel model = new(vault, NoPicker);

        await model.ToContainerAsync(reference, Password(ContainerPassword), token);
        await vault.LockAsync();
        await model.RecordSavedAsync(reference, token);

        // The bytes are already on disk by then, so the export itself succeeded; what failed is
        // remembering it, and the screen has to be able to say that without the component catching
        // an exception and composing the sentence itself.
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public async Task An_empty_backup_password_is_refused_and_nothing_is_sealed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one", VaultEntryKind.Generated), token);
        ExportModel model = new(vault, NoPicker);

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
        ExportModel model = new(vault, NoPicker);

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
        ExportModel model = new(vault, NoPicker);

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
        ExportModel model = new(vault, NoPicker);

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
        ExportModel model = new(vault, NoPicker);

        Assert.Null(await model.ToContainerAsync("no-such-reference", Password(ContainerPassword), token));
        Assert.Equal(VaultErrorMessages.For(VaultError.NoSuchEntry), model.Problem);
    }

    [Fact]
    public void The_qr_payloads_text_decodes_back_to_the_container_through_Base45()
    {
        byte[] container = [1, 2, 3, 4, 5, 250, 251, 252, 0, 255];
        ExportModel model = new(NewVault(), NoPicker);

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
        ExportModel model = new(NewVault(), NoPicker);

        Assert.True(model.ToQr(container).FitsOneCode);
    }

    [Fact]
    public void FitsOneCode_is_true_exactly_at_the_measured_boundary()
    {
        // Version 40, correction level H: 1852 alphanumeric characters, measured as 1234 container
        // bytes (design section 8). Base45.GetEncodedLength(1234) <= 1852 is pinned in the Keys
        // suite; this is the same boundary from ExportModel's side of it.
        byte[] atBoundary = new byte[1234];
        ExportModel model = new(NewVault(), NoPicker);

        Assert.True(model.ToQr(atBoundary).FitsOneCode);
    }

    [Fact]
    public void FitsOneCode_is_false_one_byte_past_the_measured_boundary()
    {
        byte[] pastBoundary = new byte[1235];
        ExportModel model = new(NewVault(), NoPicker);

        Assert.False(model.ToQr(pastBoundary).FitsOneCode);
    }
}

/// <summary>
/// A save dialog whose one exercised member returns or throws whatever <paramref name="save"/>
/// hands back, standing in for a cancelled dialog (<see langword="false"/>), a successful write
/// (<see langword="true"/>), or -- by throwing -- whatever a real platform failure would surface as,
/// disk-full included. Mirrors <c>RefusingVaultLocationStore</c> in <c>VaultLocationModelTests</c>:
/// a fake that lets a test script one seam's outcome directly, rather than reaching a real disk.
/// </summary>
internal sealed class ScriptedFilePickerService(Func<bool> save) : IFilePickerService
{
    public Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken) =>
        Task.FromResult(save());

    public Task<bool> SaveStreamAsync(string suggestedFileName, Stream content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only scripts SaveFileAsync.");

    public Task<byte[]?> PickFileAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only scripts SaveFileAsync.");

    public Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only scripts SaveFileAsync.");
}
