using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

using WinRT.Interop;

using XrplFile.App.Core;

namespace XrplFile.App.Platform;

/// <summary>
/// The platform's file dialogs on Windows, over the WinRT pickers this application's single
/// target — <c>net10.0-windows</c> — provides directly rather than through a cross-platform MAUI
/// abstraction: MAUI Essentials has no save-file dialog at all, and its own pick-file dialog is
/// this same WinRT picker underneath on this platform.
/// </summary>
/// <remarks>
/// Both methods hand back bytes, never a path, matching <see cref="IFilePickerService"/>'s own
/// contract: the file a picker hands back is not reliably re-openable as an ordinary path on every
/// platform this interface has to serve, even though on Windows today it usually would be.
/// </remarks>
public sealed class MauiFilePickerService : IFilePickerService
{
    /// <inheritdoc/>
    public async Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(content);

        FileSavePicker picker = new()
        {
            SuggestedFileName = suggestedFileName,
        };

        string extension = Path.GetExtension(suggestedFileName) is { Length: > 0 } value ? value : ".bin";
        picker.FileTypeChoices.Add("Backup file", [extension]);

        InitializeWithWindow.Initialize(picker, GetActiveWindowHandle());

        StorageFile? file = await picker.PickSaveFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return false;
        }

        await FileIO.WriteBytesAsync(file, content).AsTask(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> PickFileAsync(CancellationToken cancellationToken)
    {
        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, GetActiveWindowHandle());

        StorageFile? file = await picker.PickSingleFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        IBuffer buffer = await FileIO.ReadBufferAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// The native handle of this application's one window. Both WinRT pickers refuse to open
    /// without one on an unpackaged desktop app such as this (<c>WindowsPackageType=None</c>).
    /// </summary>
    private static nint GetActiveWindowHandle()
    {
        Application? application = Application.Current;
        if (application is null || application.Windows.Count == 0)
        {
            throw new InvalidOperationException("There is no active window to attach a file dialog to.");
        }

        if (application.Windows[0].Handler?.PlatformView is not Microsoft.UI.Xaml.Window platformWindow)
        {
            throw new InvalidOperationException("The active window has no platform handle yet.");
        }

        return WindowNative.GetWindowHandle(platformWindow);
    }
}
