namespace XrplFile.App.Core;

/// <summary>
/// The platform's file dialogs, behind a seam <c>XrplFile.App.Core</c> can call without a MAUI
/// reference. Implemented once per platform in <c>XrplFile.App</c> in a later task.
/// </summary>
/// <remarks>
/// <para>
/// Not part of the original design table for this task; added here because export is the first
/// caller that needs to write a file the user chooses, and the seam belongs with the task that
/// needs it rather than with whichever later task first implements it. Kept to exactly the two
/// operations this plan's models actually need: <see cref="ExportModel.ToContainerAsync"/> produces
/// bytes that have to land somewhere the user picks, and <c>AddKeyModel.OpenContainer</c> needs
/// bytes read from wherever the user picks a container file from.
/// </para>
/// <para>
/// Both operations are bytes in, bytes out, not paths: a MAUI file picker or save dialog hands back
/// (or reads from) a platform-owned handle — a security-scoped resource on iOS, a content URI on
/// Android — that is not reliably re-openable as an ordinary file-system path a second time. A
/// caller that needs a container's or a vault's bytes gets them directly from the one read the
/// platform dialog already performed, rather than being hand a path and told to open it again.
/// </para>
/// <para>
/// <see cref="PickExistingFilePathAsync"/> is the one deliberate exception to that rule, added for
/// <c>VaultLocationModel</c>: opening a vault kept somewhere else (design E5's correction) is not
/// "read these bytes once" — it is "remember where this file is, and open it from there on every
/// future start" (<see cref="VaultLocation.Resolve"/>). That needs a path, and this application's
/// single target, <c>net10.0-windows</c>, is the one platform among those this interface has to
/// serve where the picker hands one back reliably. A seam that returned a path on every platform
/// would be dishonest about the other two; this one is honest about being Windows-only instead.
/// </para>
/// </remarks>
public interface IFilePickerService
{
    /// <summary>
    /// Asks the user where to save <paramref name="content"/>, suggesting
    /// <paramref name="suggestedFileName"/> as its name.
    /// </summary>
    /// <returns><see langword="true"/> once the file has been written; <see langword="false"/> when the user cancels.</returns>
    Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the user to choose an existing file and returns its bytes — the shape importing a
    /// backup container needs.
    /// </summary>
    /// <returns>The chosen file's bytes, or <see langword="null"/> when the user cancels.</returns>
    Task<byte[]?> PickFileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the user to choose an existing file and returns its full path rather than its bytes —
    /// the shape pointing the application at a vault kept somewhere else needs, since that is a
    /// location to remember rather than content to read once. See the type remarks for why this one
    /// method is a path and not bytes.
    /// </summary>
    /// <returns>
    /// The chosen file's full path, or <see langword="null"/> when the user cancels or the picker
    /// cannot resolve the chosen item to an ordinary file-system path.
    /// </returns>
    Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken);
}
