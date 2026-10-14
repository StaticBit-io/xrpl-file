using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class UnlockModelTests : VaultHarness
{
    [Fact]
    public async Task A_fresh_directory_offers_to_create_a_vault()
    {
        UnlockModel model = new(NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task Creating_leaves_the_vault_open()
    {
        UnlockModel model = new(NewVault());
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        await model.CreateAsync(Password("passphrase"), TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }

    [Fact]
    public async Task A_lock_from_somewhere_else_is_noticed_rather_than_rendered_over()
    {
        // The idle lock, or the window-closing handler, shuts the vault without going through this
        // model. Nothing tells the shell -- IKeyVault raises no notification -- so until this call
        // existed the window went on drawing the key list and a Lock button over a shut vault, and
        // the first sign of it was a model refusing an action.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.CreateAsync(Password("passphrase"), token);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);

        await vault.LockAsync();

        Assert.True(model.NoticeLock());
        Assert.Equal(UnlockStage.Locked, model.Stage);
    }

    [Fact]
    public async Task Noticing_a_lock_says_nothing_happened_when_nothing_did()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.CreateAsync(Password("passphrase"), token);

        // An open vault: every tick but the one that matters.
        Assert.False(model.NoticeLock());
        Assert.Equal(UnlockStage.Unlocked, model.Stage);

        await vault.LockAsync();
        Assert.True(model.NoticeLock());

        // And the tick after that one: already noticed, nothing to report, no second re-render.
        Assert.False(model.NoticeLock());
        Assert.Equal(UnlockStage.Locked, model.Stage);
    }

    [Fact]
    public async Task Noticing_a_lock_never_unlocks_and_never_leaves_the_create_screen()
    {
        // A poll must only ever narrow what the screen claims. On a vault that does not exist yet
        // the stage is NoVault and IsUnlocked is false, and this must not read that as a lock and
        // send a first-run person to a password prompt for a vault nobody has created.
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UnlockStage.NoVault, model.Stage);

        Assert.False(model.NoticeLock());
        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task A_wrong_password_reports_it_and_leaves_the_vault_shut()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("right passphrase"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        VaultUnlockResult result = await model.UnlockAsync(Password("wrong passphrase"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.True(model.LastMessage!.CountsAsAFailedAttempt);
    }

    [Fact]
    public async Task A_degraded_unlock_leaves_the_user_inside()
    {
        // VaultDegraded means the password worked and something inside is damaged.
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("passphrase"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await model.LockAsync();
        FlipOneByteOfEntry(reference);

        VaultUnlockResult result = await model.UnlockAsync(Password("passphrase"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }

    [Fact]
    public async Task A_right_password_leaves_no_failed_attempt_and_a_message()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("right passphrase"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        VaultUnlockResult result = await model.UnlockAsync(Password("right passphrase"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
        Assert.False(model.LastMessage!.CountsAsAFailedAttempt);
    }

    [Fact]
    public async Task Locking_forgets_the_message_of_the_unlock_before_it()
    {
        // The unlock screen binds to LastMessage. Kept across a lock, it greeted the person with
        // "Vault unlocked" before they had typed anything.
        CancellationToken token = TestContext.Current.CancellationToken;
        UnlockModel model = new(NewVault());
        await model.CreateAsync(Password("passphrase"), token);
        await model.LockAsync();
        await model.UnlockAsync(Password("passphrase"), token);
        Assert.NotNull(model.LastMessage);

        await model.LockAsync();

        Assert.Null(model.LastMessage);
    }

    [Fact]
    public async Task A_lock_noticed_from_somewhere_else_forgets_the_message_too()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.CreateAsync(Password("passphrase"), token);
        await model.LockAsync();
        await model.UnlockAsync(Password("passphrase"), token);
        Assert.NotNull(model.LastMessage);

        await vault.LockAsync();

        Assert.True(model.NoticeLock());
        Assert.Null(model.LastMessage);
    }

    [Fact]
    public async Task Noticing_nothing_keeps_the_message_of_a_failed_attempt()
    {
        // A poll on a vault that never opened must not wipe "Wrong password" off the screen.
        CancellationToken token = TestContext.Current.CancellationToken;
        UnlockModel model = new(NewVault());
        await model.CreateAsync(Password("right passphrase"), token);
        await model.LockAsync();
        await model.UnlockAsync(Password("wrong passphrase"), token);

        Assert.False(model.NoticeLock());
        Assert.True(model.LastMessage!.CountsAsAFailedAttempt);
    }
}

public class UnlockModelDeviceUnlockTests : VaultHarness
{
    [Fact]
    public async Task No_vault_offers_no_device_unlock()
    {
        UnlockModel model = new(NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
        Assert.False(model.DeviceUnlockOffered);
    }

    [Fact]
    public async Task A_locked_vault_with_no_device_wrap_offers_none()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("passphrase"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.False(model.DeviceUnlockOffered);
    }

    [Fact]
    public async Task A_locked_vault_with_a_device_wrap_offers_it_without_opening()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("passphrase"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        // The refresh answered "is this possible" by actually trying it, and put the vault back
        // exactly as it found it: looking at the screen again must never be what lets someone in.
        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.True(model.DeviceUnlockOffered);
        Assert.False(vault.IsUnlocked);
        Assert.Null(model.LastMessage);

        VaultUnlockResult result = await model.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }
}
