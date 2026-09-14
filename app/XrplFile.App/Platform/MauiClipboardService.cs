using Windows.ApplicationModel.DataTransfer;

using XrplFile.App.Core;

// Microsoft.Maui.Controls declares its own DataPackage, DataPackageOperation, DataPackageView and
// Clipboard, and MAUI's implicit global usings bring them all into scope here. Every WinRT type
// this file touches is therefore aliased rather than named plainly -- the alternative is four
// ambiguous-reference errors and, worse, a future edit silently binding to the MAUI type that
// happens to compile.
using ClipboardContentOptions = Windows.ApplicationModel.DataTransfer.ClipboardContentOptions;
using DataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using DataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using DataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;
using StandardDataFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace XrplFile.App.Platform;

/// <summary>
/// The system clipboard on Windows, over the WinRT clipboard directly rather than MAUI's
/// cross-platform wrapper.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the earlier version of this class claimed, and why it was wrong.</b> It copied the text
/// with <c>Clipboard.Default.SetTextAsync</c>, dropped the expiry entirely, and said in its own
/// remarks that "Windows has neither a clipboard-expiry API nor a cross-device clipboard that races
/// an in-process timer". Both halves of that are false. Windows keeps a clipboard history (Win+V)
/// that retains whatever was copied long after the reveal screen has hidden it and long after the
/// vault has locked, and Cloud Clipboard roams it to the person's other machines — which is exactly
/// the exposure the expiry parameter exists to prevent, arriving on this platform by a different
/// mechanism than iOS's Universal Clipboard rather than not arriving at all.
/// </para>
/// <para>
/// <b>What this platform does offer.</b> <c>Clipboard.SetContentWithOptions</c> takes a
/// <see cref="ClipboardContentOptions"/> whose <see cref="ClipboardContentOptions.IsAllowedInHistory"/>
/// keeps the entry out of the Win+V history and whose
/// <see cref="ClipboardContentOptions.IsRoamable"/> keeps it off the person's other machines. Both
/// are set to <see langword="false"/> here. MAUI's own <c>Clipboard.SetTextAsync</c> reaches
/// neither, which is why this goes to the WinRT API directly — the same reason
/// <see cref="MauiFilePickerService"/> already does.
/// </para>
/// <para>
/// <b>What it does not offer, stated narrowly.</b> There is no Windows API that hands the system a
/// secret with a lifetime and has the system drop it on its own, the way
/// <c>UIPasteboard.setItems(_:options:)</c> does on iOS. So the expiry is enforced by this process
/// instead: a timer clears the clipboard once it elapses, and only if the clipboard still holds
/// exactly what was copied — clearing something the person copied afterwards would be taking away
/// work that is not ours to take. The honest limit is that a process killed before the timer fires
/// leaves the text on the clipboard; what survives is one local clipboard slot until the next copy,
/// not a synced entry in a list.
/// </para>
/// <para>
/// The <see cref="DataPackage"/> is deliberately handed over without <c>Clipboard.Flush()</c>.
/// Flushing asks Windows to keep the content available after this process exits, which for a seed
/// is the opposite of what is wanted: unflushed, the clipboard entry dies with the process, which
/// closes the gap the paragraph above leaves open for the ordinary case of the application being
/// closed.
/// </para>
/// </remarks>
public sealed class MauiClipboardService : IClipboardService, IDisposable
{
    /// <summary>
    /// Guards <see cref="_expiryTimer"/> and <see cref="_copied"/> together: a second copy has to
    /// replace the first one's pending clear rather than race it, or the earlier timer would fire
    /// against the later secret.
    /// </summary>
    private readonly Lock _gate = new();

    private Timer? _expiryTimer;
    private string? _copied;
    private bool _disposed;

    /// <inheritdoc/>
    public async Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(value);

            ClipboardContentOptions options = new()
            {
                IsAllowedInHistory = false,
                IsRoamable = false,
            };

            // Returns false when the shell refused the options rather than throwing. Falling back
            // to a plain SetContent is better than leaving the person with nothing on the clipboard
            // at all: the secret is then an ordinary clipboard entry, which the timer below still
            // clears when the window elapses.
            if (!WindowsClipboard.SetContentWithOptions(package, options))
            {
                WindowsClipboard.SetContent(package);
            }
        }).ConfigureAwait(false);

        ArmExpiry(value, expiry);
    }

    private void ArmExpiry(string value, TimeSpan expiry)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _expiryTimer?.Dispose();
            _copied = value;
            _expiryTimer = new Timer(_ => ClearIfStillOurs(), state: null, expiry, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Clears the clipboard if it still holds exactly what <see cref="CopySecretAsync"/> put there.
    /// </summary>
    /// <remarks>
    /// The comparison is the whole point: by the time the window elapses the person may have copied
    /// an address, a password, a paragraph of their own, and wiping that would be this application
    /// reaching outside itself. Reading the clipboard can fail outright while another process holds
    /// it open, and a failure here is not worth reporting anywhere — the entry was already marked
    /// history- and roaming-exempt, so the worst case is one local clipboard slot living until the
    /// next copy.
    /// </remarks>
    private void ClearIfStillOurs()
    {
        string? ours;
        lock (_gate)
        {
            ours = _copied;
            _copied = null;
        }

        if (ours is null)
        {
            return;
        }

        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                DataPackageView view = WindowsClipboard.GetContent();
                if (!view.Contains(StandardDataFormats.Text))
                {
                    return;
                }

                string current = await view.GetTextAsync();
                if (string.Equals(current, ours, StringComparison.Ordinal))
                {
                    WindowsClipboard.Clear();
                }
            }
            catch (Exception)
            {
                // Another process holding the clipboard open, or handing back something this call
                // cannot read. See the remarks: there is nothing here worth reporting to.
            }
        });
    }

    /// <summary>
    /// Stops any pending clear and drops the copy this instance was holding to compare against.
    /// Never clears the clipboard itself: disposal is this application shutting down, and taking
    /// the clipboard with it would be a surprise rather than a safeguard.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _copied = null;
        }
    }
}
