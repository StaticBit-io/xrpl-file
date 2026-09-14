using XrplFile.App.Core;

namespace XrplFile.App.Platform;

/// <summary>
/// The system clipboard on Windows, over MAUI <see cref="Clipboard"/>.
/// </summary>
/// <remarks>
/// <see cref="IClipboardService"/>'s own remarks explain why <paramref name="expiry"/> — see
/// <see cref="CopySecretAsync"/> — exists as a parameter at all: on iOS and Android a secret can
/// leave the device, or become visible in a system preview, before any timer this application owns
/// could fire, so those platforms are handed the expiry to enforce themselves. Windows has neither
/// a clipboard-expiry API nor a cross-device clipboard that races an in-process timer, so this
/// implementation copies the text and otherwise ignores <paramref name="expiry"/> — it does not
/// pretend to honour a setting there is nothing on this platform to honour it with.
/// </remarks>
public sealed class MauiClipboardService : IClipboardService
{
    /// <inheritdoc/>
    public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        return Clipboard.Default.SetTextAsync(value);
    }
}
