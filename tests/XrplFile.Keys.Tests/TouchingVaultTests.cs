using Xunit;

using XrplFile.Tests;

namespace XrplFile.Keys.Tests;

/// <summary>
/// The half of the idle lock that observes activity: every operation a person's action causes
/// resets the window, and every poll a screen makes does not. Both directions matter — a wrapper
/// that touched on nothing would leave the fifteen-minute lock firing mid-task, and one that
/// touched on everything would hold the data key open forever behind
/// <c>RevealSeed.razor</c>'s once-a-second poll.
/// </summary>
public class TouchingVaultTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A vault, its idle lock, and the wrapper under test, wired exactly as
    /// <c>MauiProgram.RegisterVault</c> wires them: the auto-lock is built over the undecorated
    /// vault, so its own lock never travels back through the wrapper.
    /// </summary>
    private static (ManualTimeProvider Time, CountingVault Inner, VaultAutoLock AutoLock, IKeyVault Vault) Build()
    {
        ManualTimeProvider time = new();
        CountingVault inner = new();
        VaultAutoLock autoLock = new(inner, Idle, time);
        return (time, inner, autoLock, new TouchingVault(inner, autoLock));
    }

    [Theory]
    [InlineData("ListAsync")]
    [InlineData("StoreAsync")]
    [InlineData("RevealAsync")]
    [InlineData("ExportAsync")]
    [InlineData("UpdateAsync")]
    [InlineData("DeleteAsync")]
    [InlineData("UnlockAsync")]
    [InlineData("UnlockWithDeviceAsync")]
    [InlineData("ChangePasswordAsync")]
    [InlineData("AcknowledgeDamageAsync")]
    [InlineData("EnableDeviceUnlockAsync")]
    [InlineData("DisableDeviceUnlockAsync")]
    [InlineData("CreateAsync")]
    public async Task An_operation_postpones_the_idle_lock(string operation)
    {
        (ManualTimeProvider time, CountingVault inner, VaultAutoLock autoLock, IKeyVault vault) = Build();
        await using VaultAutoLock lifetime = autoLock;

        // Fourteen minutes of nothing, then the person does something: the window must restart
        // from here, not keep counting toward the lock one minute away.
        time.Advance(TimeSpan.FromMinutes(14));
        await CallAsync(vault, operation);
        time.Advance(TimeSpan.FromMinutes(14));

        Assert.Equal(0, inner.LockCalls);

        // And the restarted window still ends: touching is a postponement, never a suspension.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, inner.LockCalls);
    }

    [Fact]
    public async Task Reading_a_property_does_not_postpone_the_idle_lock()
    {
        (ManualTimeProvider time, CountingVault inner, VaultAutoLock autoLock, IKeyVault vault) = Build();
        await using VaultAutoLock lifetime = autoLock;

        // A screen polling once a second for fifteen minutes, which is exactly what
        // RevealSeed.razor and Shell.razor do. An idle vault must still lock underneath it.
        for (int second = 0; second < 15 * 60; second++)
        {
            _ = vault.IsUnlocked;
            _ = vault.WritesAreRefused;
            _ = vault.References;
            _ = vault.DamagedReferences;
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, inner.LockCalls);
    }

    [Fact]
    public async Task Asking_whether_the_file_is_there_does_not_postpone_the_idle_lock()
    {
        (ManualTimeProvider time, CountingVault inner, VaultAutoLock autoLock, IKeyVault vault) = Build();
        await using VaultAutoLock lifetime = autoLock;

        time.Advance(TimeSpan.FromMinutes(14));
        await vault.ExistsAsync(TestContext.Current.CancellationToken);
        await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, inner.LockCalls);
    }

    [Fact]
    public async Task Locking_deliberately_does_not_postpone_the_idle_lock()
    {
        (ManualTimeProvider time, CountingVault inner, VaultAutoLock autoLock, IKeyVault vault) = Build();
        await using VaultAutoLock lifetime = autoLock;

        time.Advance(TimeSpan.FromMinutes(14));
        await vault.LockAsync();
        time.Advance(TimeSpan.FromMinutes(1));

        // One from the press, one from the window that was never postponed.
        Assert.Equal(2, inner.LockCalls);
    }

    [Fact]
    public async Task Every_member_forwards_to_the_vault_underneath()
    {
        (ManualTimeProvider time, CountingVault inner, VaultAutoLock autoLock, IKeyVault vault) = Build();
        await using VaultAutoLock lifetime = autoLock;

        CancellationToken token = TestContext.Current.CancellationToken;

        _ = vault.IsUnlocked;
        _ = vault.References;
        _ = vault.DamagedReferences;
        _ = vault.WritesAreRefused;
        await vault.ExistsAsync(token);
        await vault.HasDeviceWrapAsync(token);
        await vault.LockAsync();
        foreach (string operation in CountingVault.EveryOperation)
        {
            await CallAsync(vault, operation);
        }

        // Seven reads plus every operation named above: nothing may be answered by the wrapper
        // itself instead of by the vault it wraps.
        Assert.Equal(7 + CountingVault.EveryOperation.Length, inner.Calls.Count);
    }

    [Fact]
    public void The_constructor_rejects_a_null_vault_or_auto_lock()
    {
        ManualTimeProvider time = new();
        CountingVault inner = new();
        VaultAutoLock autoLock = new(inner, Idle, time);

        Assert.Throws<ArgumentNullException>(() => new TouchingVault(null!, autoLock));
        Assert.Throws<ArgumentNullException>(() => new TouchingVault(inner, null!));
    }

    private static async Task CallAsync(IKeyVault vault, string operation)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ReadOnlyMemory<byte> password = new byte[] { 1, 2, 3 };

        switch (operation)
        {
            case "ListAsync": await vault.ListAsync(token); break;
            case "StoreAsync": await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Imported, "l", "n", "a", "s"), token); break;
            case "RevealAsync": await vault.RevealAsync("r", "why", token); break;
            case "ExportAsync": await vault.ExportAsync("r", password, token); break;
            case "UpdateAsync": await vault.UpdateAsync("r", new VaultEntryEdit(), token); break;
            case "DeleteAsync": await vault.DeleteAsync("r", token); break;
            case "UnlockAsync": await vault.UnlockAsync(password, token); break;
            case "UnlockWithDeviceAsync": await vault.UnlockWithDeviceAsync(token); break;
            case "ChangePasswordAsync": await vault.ChangePasswordAsync(password, password, token); break;
            case "AcknowledgeDamageAsync": await vault.AcknowledgeDamageAsync(token); break;
            case "EnableDeviceUnlockAsync": await vault.EnableDeviceUnlockAsync(token); break;
            case "DisableDeviceUnlockAsync": await vault.DisableDeviceUnlockAsync(token); break;
            case "CreateAsync": await vault.CreateAsync(password, token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "No such vault operation.");
        }
    }
}

/// <summary>
/// An <see cref="IKeyVault"/> double that answers every member with something harmless and records
/// that it was asked. Unlike <c>RecordingVault</c>, which throws from everything the auto-lock must
/// never call, this one has to answer everything: the wrapper under test forwards the whole
/// interface, and what is being proved is which of those calls reset an idle window.
/// </summary>
internal sealed class CountingVault : IKeyVault
{
    /// <summary>Every operation <c>TouchingVault</c> treats as use, by name.</summary>
    internal static readonly string[] EveryOperation =
    [
        "ListAsync", "StoreAsync", "RevealAsync", "ExportAsync", "UpdateAsync", "DeleteAsync",
        "UnlockAsync", "UnlockWithDeviceAsync", "ChangePasswordAsync", "AcknowledgeDamageAsync",
        "EnableDeviceUnlockAsync", "DisableDeviceUnlockAsync", "CreateAsync",
    ];

    private readonly List<string> _calls = [];

    /// <summary>Every member this double was asked for, in order.</summary>
    internal IReadOnlyList<string> Calls => _calls;

    /// <summary>How many times anything asked this vault to lock — the auto-lock's own firings included.</summary>
    internal int LockCalls { get; private set; }

    /// <inheritdoc/>
    public bool IsUnlocked
    {
        get
        {
            _calls.Add(nameof(IsUnlocked));
            return true;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> References
    {
        get
        {
            _calls.Add(nameof(References));
            return [];
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DamagedReferences
    {
        get
        {
            _calls.Add(nameof(DamagedReferences));
            return [];
        }
    }

    /// <inheritdoc/>
    public bool WritesAreRefused
    {
        get
        {
            _calls.Add(nameof(WritesAreRefused));
            return false;
        }
    }

    /// <inheritdoc/>
    public Task LockAsync()
    {
        _calls.Add(nameof(LockAsync));
        LockCalls++;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Record(nameof(ExistsAsync), true);

    /// <inheritdoc/>
    public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
        Record(nameof(HasDeviceWrapAsync), false);

    /// <inheritdoc/>
    public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) => Record(nameof(AcknowledgeDamageAsync));

    /// <inheritdoc/>
    public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        Record(nameof(CreateAsync));

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
        Record(nameof(UnlockAsync), VaultUnlockResult.Success);

    /// <inheritdoc/>
    public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
        Record(nameof(UnlockWithDeviceAsync), VaultUnlockResult.Success);

    /// <inheritdoc/>
    public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
        Record(nameof(ListAsync), (IReadOnlyList<VaultEntryInfo>)[]);

    /// <inheritdoc/>
    public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
        Record(nameof(StoreAsync), "reference");

    /// <inheritdoc/>
    public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
        Record(
            nameof(RevealAsync),
            new VaultEntry(
                new VaultEntryInfo(
                    reference, VaultEntryKind.Imported, "l", "n", "a", DateTimeOffset.UnixEpoch, null, null, false),
                "secret"));

    /// <inheritdoc/>
    public Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
        Record(nameof(ExportAsync), Array.Empty<byte>());

    /// <inheritdoc/>
    public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
        Record(nameof(UpdateAsync));

    /// <inheritdoc/>
    public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Record(nameof(DeleteAsync));

    /// <inheritdoc/>
    public Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
        Record(nameof(ChangePasswordAsync), VaultPasswordChange.Changed);

    /// <inheritdoc/>
    public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
        Record(nameof(EnableDeviceUnlockAsync), true);

    /// <inheritdoc/>
    public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
        Record(nameof(DisableDeviceUnlockAsync));

    private Task Record(string member)
    {
        _calls.Add(member);
        return Task.CompletedTask;
    }

    private Task<T> Record<T>(string member, T result)
    {
        _calls.Add(member);
        return Task.FromResult(result);
    }
}
