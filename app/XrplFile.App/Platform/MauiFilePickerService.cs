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
/// <see cref="SaveFileAsync"/> and <see cref="PickFileAsync"/> are bytes in / bytes out, matching
/// <see cref="IFilePickerService"/>'s own contract: the file a picker hands back is not reliably
/// re-openable as an ordinary path on every platform this interface has to serve, even though on
/// Windows today it usually would be. <see cref="SaveStreamAsync"/> takes a stream rather than an
/// array for the same reason — the caller's content may already be a file on disk — but still never
/// hands back a path itself. <see cref="PickExistingFilePathAsync"/> is the one deliberate exception
/// to "never a path"; see its own remarks and <see cref="IFilePickerService"/>'s for why.
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
    public async Task<bool> SaveStreamAsync(string suggestedFileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(content);

        FileSavePicker picker = new()
        {
            SuggestedFileName = suggestedFileName,
        };

        string extension = Path.GetExtension(suggestedFileName) is { Length: > 0 } value ? value : ".bin";
        picker.FileTypeChoices.Add("File", [extension]);

        InitializeWithWindow.Initialize(picker, GetActiveWindowHandle());

        StorageFile? file = await picker.PickSaveFileAsync().AsTask(cancellationToken).ConfigureAwait(false);

        if (file is null)
        {
            return false;
        }

        // A plain write stream (OpenStreamForWriteAsync) truncates the destination the moment it is
        // opened, so a copy that then fails -- cancellation, a full disk, an I/O error reading the
        // caller's already-on-disk source -- would leave a half-written file sitting at the path the
        // person chose, with nothing telling them. A transacted write defers replacing the file's
        // contents until CommitAsync: an abandoned transaction -- exception, cancellation, never
        // committed -- leaves the destination untouched. That is the same guarantee
        // FileIO.WriteBytesAsync (SaveFileAsync, above) already gets for free by writing its buffer
        // in one WinRT call -- this method needs the transaction explicitly because streaming the
        // copy is the whole point of it.
        using StorageStreamTransaction transaction =
            await file.OpenTransactedWriteAsync().AsTask(cancellationToken).ConfigureAwait(false);

        transaction.Stream.Size = 0;

        // Disposed before the transaction it wraps, so the wrapper lets go of the WinRT stream
        // before that stream's owner does.
        using Stream destination = transaction.Stream.AsStreamForWrite();
        await content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

        // The wrapper Stream can buffer bytes that a commit would not otherwise see; flush it into
        // the transaction's IRandomAccessStream before committing.
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync().AsTask(cancellationToken).ConfigureAwait(false);

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

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="StorageFile.Path"/> is populated for an item this picker hands back on this
    /// platform: on an unpackaged Win32 desktop app the file open picker only ever returns ordinary
    /// filesystem items, never a content-provider item with no path of its own the way Android's
    /// picker can. <see cref="IFilePickerService"/>'s own remarks say why that platform difference
    /// is exactly why this method exists at all rather than being folded into
    /// <see cref="PickFileAsync"/>.
    /// </remarks>
    public async Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken)
    {
        FileOpenPicker picker = new();
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, GetActiveWindowHandle());

        StorageFile? file = await picker.PickSingleFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return file?.Path is { Length: > 0 } path ? path : null;
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
