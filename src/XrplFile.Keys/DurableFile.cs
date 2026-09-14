namespace XrplFile.Keys;

/// <summary>
/// How the bytes of one file are read. Production passes
/// <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/>; the seam exists so that a test
/// can make a file vanish between two statements — the one thing a reader has to survive and the
/// one thing no test can arrange on a real filesystem without racing it.
/// </summary>
internal delegate Task<byte[]> VaultFileContents(string path, CancellationToken cancellationToken);

/// <summary>
/// Crash-safe single-file storage: a write either lands whole or does not land at all, and the
/// previous good contents are never the bytes being overwritten.
/// </summary>
/// <remarks>
/// The sequence is: write the new contents to a sibling <c>.tmp</c> file, flush those bytes past
/// the operating system's cache and onto the device, then swap it into place with
/// <see cref="File.Replace(string, string, string?)"/> — which renames the current file to
/// <c>.bak</c> and moves the temporary file into its place as one atomic filesystem operation (or
/// with <see cref="File.Move(string, string)"/> when there is no current file yet to rename out of
/// the way). A process killed at any point before that single call still leaves either the
/// previous file or the previous <c>.bak</c> intact; a leftover <c>.tmp</c> from an interrupted
/// write is never read back, and the next successful write overwrites it in place.
/// </remarks>
internal static class DurableFile
{
    private const string TemporarySuffix = ".tmp";
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// Writes <paramref name="contents"/> so that a crash at any point leaves either the new
    /// contents in place or the previous contents undisturbed — never a partial file.
    /// </summary>
    internal static async Task WriteAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contents);

        string temporaryPath = path + TemporarySuffix;
        string backupPath = path + BackupSuffix;

        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };

            await using (FileStream stream = new(temporaryPath, options))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);

                // FileStream has no async overload that flushes past the OS cache to the device;
                // this synchronous call is the only way to get that guarantee before the swap below.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultError.StorageFailed, $"Writing the vault file at '{path}' failed.", ex);
        }
    }

    /// <summary>
    /// Removes the copy a previous write kept, if there is one, and reports whether the copy is
    /// gone afterwards. Returns <see langword="true"/> when there was none: the caller is asking
    /// for an end state, not for an event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backup is what makes an interrupted write survivable, so this is not something an
    /// ordinary write does. It exists for the one operation whose backup is a liability rather
    /// than a safety net — a password change, after which the kept copy is a whole vault that the
    /// password the user just replaced still opens. Called only once the new record is confirmed
    /// on disk, so a crash between the two leaves the previous file, which is what the write
    /// already guarantees; what it must not leave is a permanent one.
    /// </para>
    /// <para>
    /// It answers with a <see langword="bool"/> rather than a throw because of exactly when it
    /// runs. The change it belongs to is already committed by then, and a failure thrown out of
    /// the method that committed it reads as "the change did not happen" — which sends the caller
    /// back to the old password and leaves the copy that password opens lying there unmentioned.
    /// Whoever calls this has to carry the answer into its own result instead.
    /// </para>
    /// </remarks>
    internal static bool TryDiscardBackup(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string backupPath = path + BackupSuffix;

        try
        {
            File.Delete(backupPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An indexer or an antivirus holding the file open is enough, and on Windows it is
            // routine. The exception is deliberately not carried anywhere: what the caller needs
            // is the fact, and the path is one it already knows.
            return false;
        }
    }

    /// <summary>
    /// Reads the current contents, falling back to the kept backup when the current file is gone.
    /// Returns <see langword="null"/> when neither exists — a missing vault is not a failure.
    /// A leftover <c>.tmp</c> from an interrupted write is never considered.
    /// </summary>
    internal static Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken) =>
        ReadAsync(path, File.ReadAllBytesAsync, cancellationToken);

    /// <summary>
    /// The same read with the filesystem handed in. Production passes
    /// <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/>; the seam exists so that a
    /// test can make a file vanish between two statements, which nothing can do to a real
    /// filesystem without racing it.
    /// </summary>
    internal static async Task<byte[]?> ReadAsync(
        string path, VaultFileContents read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            // Each file is read rather than asked about. Asking first and then reading leaves a
            // window between the two, and a file that goes in that window is answered by the read
            // with "not found" — which, trusted as a failure, became StorageFailed while the whole
            // good copy beside it went unread. Absence found by reading is still absence.
            return await ContentsOrAbsent(path, read, cancellationToken).ConfigureAwait(false)
                ?? await ContentsOrAbsent(path + BackupSuffix, read, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Everything that is not absence. A refused disk must never read as "there is no
            // vault here", which is what a caller acts on by offering to create one over the top.
            throw new VaultException(VaultError.StorageFailed, $"Reading the vault file at '{path}' failed.", ex);
        }
    }

    /// <summary>
    /// One file's contents, or <see langword="null"/> where it is not there. Only the two
    /// not-found failures are absence; every other one is left to the caller above to report.
    /// </summary>
    private static async Task<byte[]?> ContentsOrAbsent(
        string path, VaultFileContents read, CancellationToken cancellationToken)
    {
        try
        {
            return await read(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
