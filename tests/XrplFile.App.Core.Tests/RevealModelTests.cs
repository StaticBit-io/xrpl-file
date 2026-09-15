using Xunit;

using XrplFile.Keys;
using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// <see cref="RevealModel"/>'s idle window is tested against a clock the test itself moves, never
/// one it waits on — see <see cref="ManualTimeProvider"/>. Every scenario here runs in well under a
/// millisecond of wall-clock time regardless of how many simulated seconds it advances through.
/// </summary>
public class RevealModelTests : VaultHarness
{
    [Fact]
    public async Task Shown_is_null_before_reveal_the_entrys_secret_after_it_and_null_again_after_hide()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        RevealModel model = new(vault, TimeProvider.System);

        Assert.Null(model.Shown);

        await model.ShowAsync(reference, token);
        Assert.Equal("sSecretOfone", model.Shown);

        model.Hide();
        Assert.Null(model.Shown);
    }

    [Fact]
    public async Task A_shown_seed_hides_itself_once_the_idle_window_passes()
    {
        // The Wallet hides after 60 seconds; the value is ours, the discipline is theirs.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(reference, token);
        Assert.NotNull(model.Shown);

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(model.Shown);
    }

    [Fact]
    public async Task The_secret_stays_shown_before_the_idle_window_passes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(reference, token);
        time.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal("sSecretOfone", model.Shown);
    }

    [Fact]
    public async Task Showing_a_second_entry_cancels_the_first_entrys_hide_timer()
    {
        // Otherwise the first timer would still fire and clear Shown out from under the second
        // reveal — see ShowAsync's remarks on why the previous window is torn down first.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string first = await vault.StoreAsync(Entry("one"), token);
        string second = await vault.StoreAsync(Entry("two"), token);
        ManualTimeProvider time = new();
        RevealModel model = new(vault, time);

        await model.ShowAsync(first, token);
        time.Advance(TimeSpan.FromSeconds(45));
        await model.ShowAsync(second, token);
        time.Advance(TimeSpan.FromSeconds(45));

        // 90 simulated seconds since the first reveal, but only 45 since the second: the first
        // timer must not have fired and cleared what the second call just showed.
        Assert.Equal("sSecretOftwo", model.Shown);
    }

    [Fact]
    public async Task CopyAsync_passes_a_non_zero_expiry_to_the_clipboard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        RevealModel model = new(vault, TimeProvider.System);
        RecordingClipboard clipboard = new();

        await model.ShowAsync(reference, token);
        await model.CopyAsync(clipboard, token);

        Assert.Equal("sSecretOfone", clipboard.CopiedValue);
        Assert.True(clipboard.CopiedExpiry > TimeSpan.Zero);
    }

    [Fact]
    public async Task CopyAsync_throws_when_nothing_is_shown()
    {
        RevealModel model = new(NewVault(), TimeProvider.System);
        RecordingClipboard clipboard = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.CopyAsync(clipboard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Shown_goes_null_the_instant_the_vault_locks_itself_with_no_reads_and_before_the_idle_window()
    {
        // Design section 8: "cleared immediately when the vault locks." The vault locks on its own
        // here (VaultAutoLock's idle timer would do the same via LockAsync) with no call into
        // RevealModel and well before its own 60-second window — the exact sequence the rule exists
        // to cover: reveal, step away, the vault locks itself, and the plaintext must not still be
        // handed out by a stale Shown.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        RevealModel model = new(vault, TimeProvider.System);

        await model.ShowAsync(reference, token);
        Assert.NotNull(model.Shown);

        await vault.LockAsync();

        Assert.Null(model.Shown);
    }

    [Fact]
    public async Task Revealing_from_a_locked_vault_surfaces_Locked_rather_than_returning_null_silently()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        await vault.LockAsync();
        RevealModel model = new(vault, TimeProvider.System);

        VaultException error = await Assert.ThrowsAsync<VaultException>(() => model.ShowAsync(reference, token));

        Assert.Equal(VaultError.Locked, error.Error);
        Assert.Null(model.Shown);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? CopiedValue { get; private set; }

        public TimeSpan CopiedExpiry { get; private set; }

        public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
        {
            CopiedValue = value;
            CopiedExpiry = expiry;
            return Task.CompletedTask;
        }

        public Task CopyAsync(string value, CancellationToken cancellationToken)
        {
            CopiedValue = value;
            CopiedExpiry = TimeSpan.Zero;
            return Task.CompletedTask;
        }
    }
}
