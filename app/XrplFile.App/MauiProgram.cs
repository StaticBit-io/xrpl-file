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

        MauiApp app = builder.Build();

        // Resolved here, eagerly, rather than left for whichever component first asks for it: the
        // idle lock has to be armed for the whole process lifetime, including the time before
        // anything unlocks the vault, and a singleton nobody has resolved yet has not been
        // constructed at all.
        _ = app.Services.GetRequiredService<VaultAutoLock>();

        return app;
    }

    /// <summary>
    /// Wires the one vault this application ever constructs (design section 3.1: "nothing
    /// constructs a vault anywhere else") and everything that reads it.
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
    /// </remarks>
    private static void RegisterVault(IServiceCollection services)
    {
        // KeyVault.CreateAsync writes through DurableFile, which opens its temporary file directly
        // under this path and does not create the directory itself (KeyVault's own constructor
        // documents the directory as a precondition). VaultLocation.DefaultPath is under the
        // platform's application-data directory, which is not guaranteed to already contain an
        // "XrplFile" folder on a machine that has never run this application before.
        Directory.CreateDirectory(Path.GetDirectoryName(VaultLocation.DefaultPath)!);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordKdf, Argon2idKdf>();
        services.AddSingleton<IDeviceSecretStore, SecureStorageDeviceSecretStore>();
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();

        services.AddSingleton<IKeyVault>(provider => new KeyVault(
            VaultLocation.DefaultPath,
            provider.GetRequiredService<IPasswordKdf>(),
            provider.GetRequiredService<IDeviceSecretStore>()));

        services.AddSingleton(provider => new VaultAutoLock(
            provider.GetRequiredService<IKeyVault>(),
            IdleTimeout,
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<UnlockModel>();
        services.AddSingleton<VaultListModel>();
        services.AddSingleton<AddKeyModel>();
        services.AddSingleton<ExportModel>();
        services.AddSingleton<RevealModel>();
        services.AddSingleton<DeviceUnlockModel>();
        services.AddSingleton<DeleteKeyModel>();
        services.AddSingleton(provider => new PasswordChangeModel(
            provider.GetRequiredService<IKeyVault>(), VaultLocation.DefaultPath));
    }
}
