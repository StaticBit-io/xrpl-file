using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Pins <see cref="UnlockModel.CreateAsync"/>'s translation of the two failures
/// <see cref="IKeyVault.CreateAsync"/> can actually produce into a <see cref="CreateMessage"/> a
/// screen can show with no decision of its own. Before this fix, <c>CreateVault.razor</c> caught
/// the raw exception and displayed <c>ex.Message</c> directly — for a storage failure that message
/// is <c>DurableFile.WriteAsync</c>'s own text, which names the full local vault path. These tests
/// exercise the model, not the component, but they are what make that path unreachable: nothing
/// downstream of <see cref="UnlockModel.CreateAsync"/> ever sees the exception again.
/// </summary>
public class UnlockModelCreateFailureTests : VaultHarness
{
    [Fact]
    public async Task Creating_over_an_existing_vault_is_reported_with_no_path_in_it()
    {
        // Built directly through the vault, not the model: this reproduces the file already being
        // there regardless of what UnlockModel.Stage currently believes, exactly as a second
        // process — or a stale screen — racing this one would.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("already-here"), TestContext.Current.CancellationToken);
        UnlockModel model = new(vault);

        VaultCreateResult result = await model.CreateAsync(Password("new"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultCreateResult.VaultAlreadyExists, result);
        Assert.NotNull(model.LastCreateMessage);
        CreateMessage message = model.LastCreateMessage!;
        Assert.Equal(CreateMessages.For(VaultCreateResult.VaultAlreadyExists).Headline, message.Headline);
        Assert.DoesNotContain(VaultPath, message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultPath, message.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_write_is_reported_as_storage_unavailable_with_no_path_in_it()
    {
        // KeyVault has no seam this project can reach to provoke a real disk failure (the
        // VaultFileWriter delegate is internal to XrplFile.Keys, visible only to
        // XrplFile.Keys.Tests). A thin IKeyVault that throws exactly the VaultException
        // DurableFile.WriteAsync itself throws — with the same path-carrying message it actually
        // produces — exercises the model's translation of that failure without needing real IO.
        StorageFailingCreateVault vault = new(NewVault());
        UnlockModel model = new(vault);

        VaultCreateResult result = await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultCreateResult.StorageUnavailable, result);
        Assert.NotNull(model.LastCreateMessage);
        CreateMessage message = model.LastCreateMessage!;
        Assert.Equal(CreateMessages.For(VaultCreateResult.StorageUnavailable).Headline, message.Headline);
        Assert.DoesNotContain(vault.SimulatedPath, message.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(vault.SimulatedPath, message.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_over_an_existing_vault_does_not_advance_the_stage_to_unlocked()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("already-here"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UnlockStage.Locked, model.Stage);

        // Not a realistic screen path on its own (the screen only offers "create" from NoVault),
        // but the model must not claim success by moving Stage when the underlying vault refused.
        await model.CreateAsync(Password("new"), TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.False(vault.IsUnlocked);
    }

    /// <summary>
    /// Wraps a real <see cref="KeyVault"/> so <see cref="CreateAsync"/> can throw the exact
    /// <see cref="VaultException"/>, with the exact path-carrying message,
    /// <c>DurableFile.WriteAsync</c> throws for a refused write — without needing a real refused
    /// write. Every other member delegates to the inner vault unchanged.
    /// </summary>
    private sealed class StorageFailingCreateVault(IKeyVault inner) : IKeyVault
    {
        public string SimulatedPath { get; } = Path.Combine(Path.GetTempPath(), "xrpl-file-simulated-vault.json");

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            throw new VaultException(
                VaultError.StorageFailed, $"Writing the vault file at '{SimulatedPath}' failed.",
                new IOException("simulated disk failure"));

        public bool IsUnlocked => inner.IsUnlocked;

        public IReadOnlyCollection<string> References => inner.References;

        public IReadOnlyList<string> DamagedReferences => inner.DamagedReferences;

        public bool WritesAreRefused => inner.WritesAreRefused;

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
            inner.AcknowledgeDamageAsync(cancellationToken);

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
            inner.HasDeviceWrapAsync(cancellationToken);

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.UnlockAsync(password, cancellationToken);

        public Task LockAsync() => inner.LockAsync();

        public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
            inner.StoreAsync(entry, cancellationToken);

        public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
            inner.RevealAsync(reference, reason, cancellationToken);

        public Task<byte[]> ExportAsync(
            string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
            inner.ExportAsync(reference, containerPassword, cancellationToken);

        public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
            inner.UpdateAsync(reference, edit, cancellationToken);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
            inner.UnlockWithDeviceAsync(cancellationToken);

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.EnableDeviceUnlockAsync(cancellationToken);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}
