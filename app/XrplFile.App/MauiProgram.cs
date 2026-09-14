using Microsoft.Extensions.Logging;

using XrplFile.App.Core;
using XrplFile.App.Platform;
using XrplFile.Keys;

namespace XrplFile.App;

public static class MauiProgram
{
    /// <summary>
    /// How long the vault may sit untouched before <see cref="VaultAutoLock"/> locks it (design
    /// section 1's "idle lock"; see that type's own remarks on why a test never sees this literal
    /// value — it is handed a <see cref="TimeProvider"/> it controls instead).
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        RegisterVault(builder.Services);

        // The idle lock is armed for the whole process lifetime, including the time before
        // anything unlocks the vault. It needs no eager resolve here to achieve that any more:
        // RegisterVault constructs it outright and registers the instance, so it is already
        // running by the time this method returns, whether or not anything ever asks for it.
        return builder.Build();
    }

    /// <summary>
    /// Wires the one vault this application ever constructs (design section 3.1: "nothing
    /// constructs a vault anywhere else") and everything that reads it, over whichever path
    /// <see cref="VaultLocation.Resolve"/> reports for this start — <see cref="VaultLocation.DefaultPath"/>
    /// until a person has chosen a different vault through <see cref="VaultLocationModel.ChoosePath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Singleton, not scoped or transient — for the vault, its idle lock, and every model.</b>
    /// A .NET MAUI Blazor Hybrid app scopes a "Scoped" registration to the root component's own
    /// service scope, which lives as long as the single <c>BlazorWebView</c> this application ever
    /// creates — in practice indistinguishable from a singleton today, with exactly one window.
    /// Singleton is chosen anyway, explicitly, rather than resting on that coincidence.
    /// </para>
    /// <para>
    /// <see cref="IKeyVault"/> holds one resident data key and one concurrency gate per instance
    /// (<see cref="KeyVault"/>'s own remarks on "the gate"). A second instance constructed over the
    /// same file — which a "Transient" registration would hand to every model separately, and which
    /// even "Scoped" would hand out again the day this application ever opens a second window —
    /// would hold a second, independent data key and a second, independent gate: an idle lock
    /// firing on one would never touch the key resident in the other, and unlocking through
    /// <see cref="UnlockModel"/> would leave every other model's own vault instance still locked,
    /// pointed at a file the first instance may already be writing to. Singleton is the only
    /// lifetime under which "the vault is unlocked" means the same thing to every model that asks,
    /// and the only one under which <see cref="VaultAutoLock"/> locks the vault every model actually
    /// reads rather than an instance nothing else ever sees.
    /// </para>
    /// <para>
    /// Every model below is singleton for the same reason at a smaller scale: two components that
    /// each resolve, say, <see cref="VaultListModel"/> must see the same
    /// <see cref="VaultListModel.Rows"/> after a refresh rather than two independent copies that
    /// can silently disagree about what the vault currently holds.
    /// </para>
    /// <para>
    /// <b>Teardown is the other half of the singleton decision, and it does not happen by itself.</b>
    /// Nothing in this process calls <see cref="IAsyncDisposable.DisposeAsync"/> on
    /// <see cref="VaultAutoLock"/> or <see cref="IKeyVault.LockAsync"/> when a window closes — a
    /// singleton lives for the process, not for the window, so MAUI's own container disposal (which
    /// only runs when the whole <c>MauiApp</c> is disposed) never reaches it either. In a
    /// single-window desktop build that is always killed outright, that is harmless: the data key
    /// dies with the process either way. It stops being harmless the first time a window closes
    /// while the process lives on — ordinary on desktop (closing the window without quitting is a
    /// window manager away) and the normal case on every other platform MAUI can target, where the
    /// OS backgrounds an app instead of killing its process. <c>App.xaml.cs</c> is where this is
    /// closed: its constructor takes <see cref="IKeyVault"/> and <see cref="VaultAutoLock"/> from
    /// this same DI container (MAUI resolves <c>App</c> through it, exactly like every service
    /// registered here), and its <see cref="Window.Destroying"/> handler calls
    /// <see cref="IKeyVault.LockAsync"/> and then <see cref="VaultAutoLock.DisposeAsync"/>.
    /// <see cref="Window.Destroying"/> is .NET MAUI's own cross-platform window-lifecycle event —
    /// not a Windows-specific hook reached for because it was the one available — and on this
    /// project's Windows target it corresponds to the native window's <c>Closed</c> event: an
    /// ordinary window close reaches it, and so does the application shutting down through any
    /// other in-process path that closes its one window.
    /// </para>
    /// <para>
    /// What it does not cover, stated rather than left implied: a process ended outright — killed
    /// from the task manager, crashed, or the machine losing power — runs no hook at all, on any
    /// platform; the resident data key is reclaimed only because the process's memory is. That gap
    /// is exactly what <see cref="VaultAutoLock"/>'s idle timer already exists to bound from the
    /// other direction — a key nobody explicitly locked is still gone within
    /// <see cref="IdleTimeout"/> — so <see cref="Window.Destroying"/> is an improvement on top of
    /// that bound for the ordinary case, not a replacement for it.
    /// </para>
    /// </remarks>
    private static void RegisterVault(IServiceCollection services)
    {
        // VaultLocation.Resolve reads IVaultLocationStore, so the store is built here, once, ahead
        // of everything else that depends on the path it produces -- see VaultLocationModel's own
        // remarks on why a running process resolves this exactly once rather than on every access.
        IVaultLocationStore locationStore = new FileVaultLocationStore();
        string vaultPath = VaultLocation.Resolve(locationStore);

        // KeyVault.CreateAsync writes through DurableFile, which opens its temporary file directly
        // under this path and does not create the directory itself (KeyVault's own constructor
        // documents the directory as a precondition). VaultLocation.DefaultPath is under the
        // platform's application-data directory, which is not guaranteed to already contain an
        // "XrplFile" folder on a machine that has never run this application before. A chosen path
        // (VaultLocationModel.ChoosePath) was picked from an existing file, so its directory
        // existed at pick time -- but removable media unplugged, or a synced folder not mounted yet
        // by the time this process starts, can make it gone again. Creating it is therefore
        // attempted and, unlike the default path, allowed to fail quietly: nothing here can tell
        // "definitely gone" apart from "not mounted yet", and this is process startup, not a screen
        // that could show either outcome. IKeyVault.ExistsAsync and CreateAsync already turn a
        // missing directory into VaultUnlockResult.VaultMissing / VaultError.StorageFailed for the
        // unlock screen to report normally (DurableFile.ReadAsync treats DirectoryNotFoundException
        // as absence; DurableFile.WriteAsync turns it into StorageFailed) -- so nothing is lost by
        // not crashing the whole application over it here.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(vaultPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // IPasswordKdf and IDeviceSecretStore are no longer registered: the one vault this process
        // builds is constructed below rather than resolved, so nothing would ever ask the
        // container for either, and a registration nobody resolves only invites a second vault to
        // be built from them one day. TimeProvider stays -- RevealModel takes one.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton(locationStore);

        // The vault, its idle lock, and the wrapper that feeds that lock are built here as one
        // unit, rather than as three independent registrations, because they cannot be resolved
        // independently: VaultAutoLock has to hold the *undecorated* vault, so that its own
        // LockAsync never travels back through the wrapper and resets the very window it is
        // firing, while every model has to hold the *decorated* one, so that each operation a
        // person's action causes resets that window (see TouchingVault for what does and does not
        // count as use). Registering a bare KeyVault as IKeyVault, as this method did before, left
        // VaultSigner as the only caller of VaultAutoLock.Touch anywhere -- and this application
        // never constructs one, so the idle window fired once, fifteen minutes into the process,
        // and the vault was left with no idle bound at all from the next unlock onward.
        KeyVault vault = new(
            vaultPath,
            new Argon2idKdf(),
            new SecureStorageDeviceSecretStore());
        VaultAutoLock autoLock = new(vault, IdleTimeout, TimeProvider.System);

        services.AddSingleton(autoLock);
        services.AddSingleton<IKeyVault>(new TouchingVault(vault, autoLock));

        services.AddSingleton(provider => new UnlockModel(
            provider.GetRequiredService<IKeyVault>(),
            VaultLocation.IsDefaultPath(vaultPath)));
        services.AddSingleton<VaultListModel>();
        services.AddSingleton<AddKeyModel>();
        services.AddSingleton<ExportModel>();
        services.AddSingleton<RevealModel>();
        services.AddSingleton<DeviceUnlockModel>();
        services.AddSingleton<DeleteKeyModel>();
        services.AddSingleton(provider => new PasswordChangeModel(
            provider.GetRequiredService<IKeyVault>(), vaultPath));
        services.AddSingleton(new VaultLocationModel(locationStore, vaultPath));
    }
}
