namespace XrplFile.App.Core;

/// <summary>
/// The system clipboard, behind a seam <see cref="RevealModel"/> can call without a MAUI reference.
/// Implemented once per platform in <c>XrplFile.App</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>expiry</c> — see <see cref="CopySecretAsync"/> — is a parameter here rather than a
/// duration an implementation is left to remember on its own, because on every platform this
/// interface has to serve a secret can leave the machine, or outlive the screen that showed it,
/// before any timer in this application could matter:
/// </para>
/// <list type="bullet">
/// <item>
/// On iOS, <c>UIPasteboard.setItems(_:options:)</c> without <c>localOnly</c> copies the contents to
/// the user's other devices at once, through Universal Clipboard, and the process may be suspended
/// before any application-side cleanup runs. The same call takes an expiry the system enforces.
/// </item>
/// <item>
/// On Android 13 and later, a clip with no <c>ClipDescription.EXTRA_IS_SENSITIVE</c> flag appears in
/// the system clipboard preview and travels through the cross-device clipboard.
/// </item>
/// <item>
/// On Windows, the Win+V clipboard history retains what was copied and Cloud Clipboard roams it to
/// the user's other machines. <c>Clipboard.SetContentWithOptions</c> exposes
/// <c>ClipboardContentOptions.IsAllowedInHistory</c> and <c>IsRoamable</c> for exactly those two,
/// which is what the Windows implementation sets. What that platform has no API for is an expiry
/// the system itself honours, so its implementation enforces the one given here with a timer of its
/// own and clears the clipboard when it elapses — see <c>MauiClipboardService</c> for what that can
/// and cannot promise.
/// </item>
/// </list>
/// <para>
/// An earlier revision of this paragraph said "Windows has neither option", and the Windows
/// implementation repeated it. That was wrong in both places, and wrong in the direction that
/// mattered: a revealed seed sat in the clipboard history after the sixty-second hide and after the
/// vault locked, which is the exposure this parameter was introduced to prevent.
/// </para>
/// </remarks>
public interface IClipboardService
{
    /// <summary>
    /// Copies <paramref name="value"/> to the clipboard, asking the platform to tighten and expire it
    /// as far as that platform allows.
    /// </summary>
    /// <param name="value">The secret to copy. Never logged, never echoed back.</param>
    /// <param name="expiry">
    /// How long the value may stay on the clipboard. Must be a positive duration. Enforced by the
    /// platform where the platform offers it (iOS), and by the implementation itself where it does
    /// not (Windows). Not a guarantee this interface can make uniformly: no implementation can
    /// promise anything about a process that is killed before the window elapses, and each one's
    /// own documentation says what it does and does not cover.
    /// </param>
    /// <param name="cancellationToken">Cancels the copy itself, not whatever the platform does with the value afterwards.</param>
    Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken);
}
