namespace XrplFile.App.Core;

/// <summary>
/// The system clipboard, behind a seam <see cref="RevealModel"/> can call without a MAUI reference.
/// Implemented once per platform in <c>XrplFile.App</c>.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="expiry"/> — see <see cref="CopySecretAsync"/> — is a parameter here rather than a
/// duration an implementation is left to remember on its own, because on two platforms a secret
/// leaves the device before any timer in this application could ever fire:
/// </para>
/// <list type="bullet">
/// <item>
/// On iOS, <c>UIPasteboard.setItems(_:options:)</c> without <c>localOnly</c> copies the contents to
/// the user's other devices at once, through Universal Clipboard, and the process may be suspended
/// before any application-side cleanup runs.
/// </item>
/// <item>
/// On Android 13 and later, a clip with no <c>ClipDescription.EXTRA_IS_SENSITIVE</c> flag appears in
/// the system clipboard preview and travels through the cross-device clipboard.
/// </item>
/// </list>
/// <para>
/// Windows has neither option. Its implementation says so in its own documentation rather than
/// pretending the options were honoured — see <see cref="CopySecretAsync"/>'s own remarks for what a
/// caller may and may not assume about <paramref name="expiry"/> as a result.
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
    /// How long the platform should keep the value on the clipboard before clearing it, where the
    /// platform offers such a setting at all. Not a guarantee this interface can make uniformly: a
    /// Windows implementation has no clipboard-expiry API to hand this to, and says so rather than
    /// silently ignoring it.
    /// </param>
    /// <param name="cancellationToken">Cancels the copy itself, not whatever the platform does with the value afterwards.</param>
    Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken);
}
