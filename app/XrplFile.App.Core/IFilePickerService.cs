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
/// bytes that have to land somewhere the user picks, and <c>AddKeyModel.OpenContainer</c> — and,
/// eventually, opening a vault kept at a non-default <see cref="VaultLocation"/> — need bytes read
/// from wherever the user picks a file from.
/// </para>
/// <para>
/// Both operations are bytes in, bytes out, not paths: a MAUI file picker or save dialog hands back
/// (or reads from) a platform-owned handle — a security-scoped resource on iOS, a content URI on
/// Android — that is not reliably re-openable as an ordinary file-system path a second time. A
/// caller that needs a container's or a vault's bytes gets them directly from the one read the
/// platform dialog already performed, rather than being hand a path and told to open it again.
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
    /// Asks the user to choose an existing file and returns its bytes — the shape both importing a
    /// backup container and opening a vault kept at a non-default location need.
    /// </summary>
    /// <returns>The chosen file's bytes, or <see langword="null"/> when the user cancels.</returns>
    Task<byte[]?> PickFileAsync(CancellationToken cancellationToken);
}
