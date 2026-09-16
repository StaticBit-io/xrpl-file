namespace XrplFile.App.Core;

/// <summary>
/// The block loop (design 4.2). It drives <see cref="XrplFileReader"/>'s per-block methods itself
/// rather than calling <see cref="XrplFileReader.ReadAsync"/>, so the screen can show progress and
/// the person can stop (design H2) — but every cryptographic check stays in the library
/// (design H3): <see cref="XrplFileReader.BlockListAsync"/> verifies the block list against the
/// manifest's root and <see cref="XrplFileReader.BlockAsync"/> verifies each block against its own
/// hash. Nothing here hashes anything.
///
/// It is deliberately a much smaller thing than <see cref="PublicationRun"/>: no state file, no
/// history, no resumption. The reason is one sentence — a read costs nothing and can simply be
/// started again, and an interrupted publication cannot. It does, however, need the same generation
/// pinning <see cref="PublicationRun"/> has: <see cref="SaveAsync"/> writes to the snapshot after
/// the terminal state is already published, which is exactly the window that lesson is about.
/// </summary>
public sealed class ReadRun(IConnectionFactory connections, IFilePickerService picker) : IDisposable
{
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task _completion = Task.CompletedTask;
    private ReadState _state = ReadState.Idle;
    private int _blocksDone;
    private int _blocksTotal;
    private long _bytesDone;
    private long _bytesTotal;
    private string? _name;
    private string? _failure;
    private bool _saved;
    private bool _saving;
    private string? _temporary;
    private bool _disposed;

    /// <summary>
    /// Bumped by <see cref="Start"/>, <see cref="Reset"/> and <see cref="Dispose"/> — anything that
    /// makes a write <see cref="SaveAsync"/> is about to make stale. <see cref="SaveAsync"/> pins
    /// this at its own start and only writes <c>_saved</c>/<c>_failure</c>/<c>_saving</c> back if it
    /// is still current, the same shape <see cref="PublicationRun"/>'s own generation protects its
    /// post-terminal writes with (see its remarks) and <see cref="ReadModel"/>'s protects a lookup
    /// with. <see cref="Start"/> and <see cref="Reset"/> both also baseline <c>_saving</c> to
    /// <see langword="false"/> for the generation they start, so a stale <see cref="SaveAsync"/>
    /// call finding itself superseded never needs to touch it.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Read under <see cref="_gate"/>, like every other piece of this run's state: an unsynchronised
    /// auto-property here would let a caller observe <see cref="Start"/>'s initial
    /// <c>Task.CompletedTask</c> after <see cref="Start"/> has already replaced it with the run's own
    /// task, on another thread, and wrongly conclude the read that was just started is already over —
    /// the same hazard <see cref="PublicationRun.Completion"/>'s own remarks describe.
    /// </summary>
    public Task Completion
    {
        get { lock (_gate) return _completion; }
    }

    public ReadSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ReadSnapshot(
                    _state, _blocksDone, _blocksTotal, _bytesDone, _bytesTotal, _name, _failure, _saved, _saving);
            }
        }
    }

    /// <summary>Where the blocks are being written. Exists so a test can prove it is cleaned up.</summary>
    internal string? TemporaryPathForTests
    {
        get { lock (_gate) return _temporary; }
    }

    /// <summary>Starts a read. <see langword="false"/> when one is already going.</summary>
    /// <exception cref="ObjectDisposedException">This instance has already been disposed.</exception>
    public bool Start(XrplFileLink link, ManifestRecord manifest)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(manifest);

        CancellationTokenSource cancellation = new();

        lock (_gate)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                throw new ObjectDisposedException(nameof(ReadRun));
            }

            if (_state == ReadState.Running)
            {
                cancellation.Dispose();
                return false;
            }

            DeleteTemporary();

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _state = ReadState.Running;
            _blocksDone = 0;
            _blocksTotal = manifest.Manifest.Blocks;
            _bytesDone = 0;
            _bytesTotal = manifest.Manifest.Size;
            _name = manifest.Manifest.Name;
            _failure = null;
            _saved = false;
            _saving = false;
            ++_generation;
            _completion = Task.Run(() => RunAsync(link, manifest, cancellation.Token));
        }

        return true;
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>Cancels and waits for the read to stop. What the window-close handler calls.</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task running;
        lock (_gate)
        {
            _cancellation?.Cancel();
            running = _completion;
        }

        if (!running.IsCompleted)
        {
            Task finished = await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, running)) return false;
        }

        Reset();
        return true;
    }

    /// <summary>
    /// Hands the finished file to the save dialog. Only a succeeded read has anything to save: the
    /// temporary file of a failed or cancelled one is incomplete, and nothing incomplete reaches
    /// the place the person chose (design H4).
    ///
    /// Carries its own error boundary, unlike the brief's own version of this method: it opens a
    /// file on disk and calls into a platform picker, either of which can throw for reasons that have
    /// nothing to do with the read itself (the temporary file already deleted by a concurrent
    /// <see cref="Reset"/> or <see cref="StopAsync"/>; a full disk; a picker failure) — and this is a
    /// public method a screen calls directly, with nothing upstream of it to catch what escapes.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        string path;
        string suggested;
        int generation;

        lock (_gate)
        {
            // Disposed is folded into the same guard as "nothing to save" rather than thrown: a
            // screen that raced a Dispose() against an already-in-flight SaveAsync call should see
            // that save quietly do nothing, not fault the Task it is awaiting.
            // _saving is part of the same guard rather than left to the screen's disabled button:
            // that button is the only thing stopping a second dialog today, and a run that can be
            // made to open two file dialogs by a fast enough second click is not correct on its own
            // terms. Quietly doing nothing is right here for the same reason it is right above.
            if (_disposed || _saving || _state != ReadState.Succeeded || _temporary is null) return;

            path = _temporary;

            // A file may have been published with no name at all: Manifest.Name is nullable. A
            // suggestion derived from nothing is still better than an empty dialog.
            suggested = string.IsNullOrWhiteSpace(_name) ? "xrpl-file.bin" : _name;

            // Pinned before the dialog opens, which can stay open for as long as the person takes:
            // a Reset() or a new Start() during that window is not refused (state is Succeeded, not
            // Running), and without this the write below could land on a run the person has already
            // put away, or on a different read entirely.
            generation = _generation;

            // Set under the same lock as the pin above, and cleared below only once this call's own
            // generation is confirmed still current -- so a Reset() or Start() that runs while this
            // dialog is still open is what actually clears it (both already baseline it to false for
            // their own, newer generation), not this call racing back in to stamp a stale false over
            // a save that generation now owns.
            _saving = true;
        }

        bool saved = false;
        string? failure = null;

        try
        {
            await using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            saved = await picker.SaveStreamAsync(suggested, content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not a failure worth reporting: the save itself, or a closing window, was cancelled.
            // _saved stays false, and failure stays null below -- clearing a stale one is still
            // correct here: whatever went wrong on some earlier attempt is not what happened on
            // this one, and this one ending without a fresh error of its own does not mean the old
            // sentence is still the right thing for the snapshot to say.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "The file could not be opened to save, or could not be written to where you chose.";
        }
        catch (Exception)
        {
            failure = "Something went wrong while saving that this application does not have a specific message for.";
        }

        lock (_gate)
        {
            // The run this save was about has since been reset or superseded by a new one: nothing
            // here belongs on its snapshot any more -- including _saving, which Reset()/Start() have
            // already baselined to false for whatever generation is current now.
            if (_generation != generation) return;

            _saved = saved;

            // Unconditional, not "if failure is not null": a successful save (or a cancelled one)
            // has to clear a sentence a previous, failed attempt on this same run left behind, or
            // Saved: true could sit next to an error that no longer describes anything real.
            _failure = failure;

            // Cleared on every outcome that reaches here -- success, failure and a cancelled dialog
            // alike -- so the Save button Read.razor disables while this is true never stays disabled
            // past the call that set it.
            _saving = false;
        }
    }

    /// <summary>Puts a finished read away and deletes what it left on disk.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_state == ReadState.Running) return;

            DeleteTemporary();
            _state = ReadState.Idle;
            _blocksDone = 0;
            _blocksTotal = 0;
            _bytesDone = 0;
            _bytesTotal = 0;
            _name = null;
            _failure = null;
            _saved = false;
            _saving = false;
            ++_generation;

            // Already disposed and nulled by RunAsync's own terminal-state publication once the run
            // in question is not Running any more — this is a second, defensive clearing, mirroring
            // PublicationRun.Reset, so a stale source can never be reached through Cancel once the
            // run it belonged to is done.
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Releases what an abandoned run left behind, for a host that drops this object without ever
    /// calling <see cref="Reset"/> or <see cref="StopAsync"/>. Best-effort and synchronous: it does
    /// not wait for a run that is still going — <see cref="StopAsync"/> is what a caller that needs
    /// to wait uses — it only cancels it and cleans up whatever can be cleaned up immediately. If a
    /// background <see cref="RunAsync"/> is still mid-write when this runs, the delete below is a
    /// no-op (the file is still open) and the run's own terminal block finishes the job once it
    /// notices the cancellation, the same way an abandoned <see cref="StopAsync"/> timeout already
    /// relies on it to.
    ///
    /// Idempotent, and marks this instance disposed: without that flag, <see cref="Start"/> would
    /// happily begin a brand-new run on an object its owner considers gone, and
    /// <see cref="SaveAsync"/> could still pass its own guard afterward — reachable specifically when
    /// the delete above fails (the state this leaves behind is otherwise unchanged: <c>_state</c>
    /// stays whatever it was, so a run that had already succeeded still reads as
    /// <see cref="ReadState.Succeeded"/> with <c>_temporary</c> still set).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            ++_generation;

            DeleteTemporary();
        }
    }

    private async Task RunAsync(XrplFileLink link, ManifestRecord manifest, CancellationToken cancellationToken)
    {
        ReadState finalState;
        string? failure = null;

        try
        {
            ReaderOptions options = new();

            // The first of the four things design 3.1 moves out of ReadAsync
            // (XrplFileReader.cs:29-32), checked before a connection is even opened. ReadModel's own
            // ceiling check is what keeps the person from being offered a button for an oversized
            // file, but Start is public and can be handed a ManifestRecord from anywhere else, so
            // this loop has to be correct about its own ceiling too, not merely rely on a caller
            // upstream having already checked it.
            if (manifest.Manifest.Size > options.MaxBytes)
            {
                throw new XrplFileException(
                    XrplFileError.TooLarge,
                    $"the file is {manifest.Manifest.Size} bytes and this reader stops at {options.MaxBytes}");
            }

            ILedgerConnection connection =
                await connections.ConnectAsync(link.Network, cancellationToken).ConfigureAwait(false);

            try
            {
                // One of the four things that moved out of ReadAsync (design 3.1). EarliestLedgerAsync
                // is public on ILedgerSource, so this needs nothing added to the library.
                long? earliest = await connection.Source.EarliestLedgerAsync(cancellationToken).ConfigureAwait(false);

                if (earliest is long first && first > manifest.Manifest.FirstLedger)
                {
                    throw new XrplFileException(
                        XrplFileError.NodeLacksHistory,
                        $"this node keeps history from ledger {first} and the file starts at {manifest.Manifest.FirstLedger}");
                }

                XrplFileReader reader = new(connection.Source, options);

                // Verified against the manifest's root inside the library before a single block is read.
                IReadOnlyList<BlockEntry> blocks =
                    await reader.BlockListAsync(manifest, cancellationToken).ConfigureAwait(false);

                string temporary = Path.Combine(
                    Directory.CreateTempSubdirectory("xrpl-file-read-").FullName, "download");

                lock (_gate)
                {
                    _temporary = temporary;
                    _blocksTotal = blocks.Count;
                }

                long written = 0;

                await using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (BlockEntry block in blocks)
                    {
                        byte[] content = await reader.BlockAsync(manifest, block, cancellationToken).ConfigureAwait(false);
                        await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                        written += content.Length;

                        lock (_gate)
                        {
                            _blocksDone++;
                            _bytesDone = written;
                        }
                    }
                }

                // The last of the four. BlockAsync holds every block to the length the manifest implies
                // for it, so this can only fail if the block list did — which the library checked before
                // any block was read. Kept anyway: it is one comparison standing between a truncated
                // file and a file somebody is told is theirs.
                if (written != manifest.Manifest.Size)
                {
                    throw new XrplFileException(
                        XrplFileError.BlockMismatch,
                        $"the blocks assemble to {written} bytes and the file's description says {manifest.Manifest.Size}",
                        blocks[^1].Index);
                }

                finalState = ReadState.Succeeded;
            }
            finally
            {
                // Not an `await using` on purpose. A connection whose DisposeAsync throws on the
                // success path — reachable, since IConnectionFactory is public even though
                // XrplConnectionFactory's own Connection.DisposeAsync swallows everything today —
                // must not turn a completed, already-assembled read into Failed and delete the
                // finished file. PublicationRun routes both of its own disposals through the same
                // kind of quiet wrapper for the identical reason; see its own remarks.
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Guarded by the token that was actually cancelled here, not any OperationCanceledException
            // whatsoever: an OCE this run did not ask for is not something this run should quietly
            // call Cancelled — it falls through to the catch-all below instead.
            finalState = ReadState.Cancelled;
        }
        catch (XrplFileException ex)
        {
            finalState = ReadState.Failed;

            try
            {
                failure = ReadErrorMessages.For(ex);
            }
            catch (ArgumentOutOfRangeException)
            {
                // ReadErrorMessages.For throws for an XrplFileError it has no sentence for — closed
                // today only by ReadErrorMessagesTests' own exhaustiveness check, not by anything the
                // compiler enforces. A throw raised inside this catch would otherwise escape past
                // every handler below it, including the catch-all, and leave this run's own terminal
                // state undecided.
                failure = "Something went wrong that this application does not have a specific message for.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            finalState = ReadState.Failed;
            failure = "The file could not be written to a temporary location while it was being read.";
        }
        catch (Exception)
        {
            finalState = ReadState.Failed;
            failure = "Something went wrong that this application does not have a specific message for.";
        }

        lock (_gate)
        {
            // Published before the delete below, not after: DeleteTemporary is best-effort cleanup
            // and must never be able to leave _state stuck at Running if it somehow throws anyway —
            // exactly the failure mode PublicationRun's own catch-all remarks describe as the reason
            // it exists. With the state already decided, a throw here (there should not be one; see
            // DeleteTemporary's own remarks) can no longer strand this run.
            _failure = failure;
            _state = finalState;

            // A read that did not finish leaves nothing behind: its temporary file is a partial
            // download, and there is no honest use for one.
            if (finalState != ReadState.Succeeded) DeleteTemporary();

            // Mirrors PublicationRun's own terminal-block disposal: a run that finishes and is never
            // explicitly Reset() or disposed must not leak the token source it started with.
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Disposes something this run acquired without letting whatever it throws be mistaken for the
    /// run's own outcome. Mirrors <see cref="PublicationRun"/>'s own helper of the same name and for
    /// the same reason.
    /// </summary>
    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed on purpose — see the call site's own remarks.
        }
    }

    /// <summary>
    /// Called under <see cref="_gate"/>. Removes the temporary file and the directory holding it, and
    /// forgets <see cref="_temporary"/> — but only once the delete has actually succeeded: this is
    /// reachable while <see cref="SaveAsync"/> still holds the file open (<c>FileShare.Read</c>
    /// across a save dialog the person can leave open for as long as they like), and on Windows a
    /// directory containing an open handle cannot be deleted. Leaving the field set on failure is
    /// what lets the next <see cref="Start"/>, <see cref="Reset"/> or terminal-state publication
    /// retry it, rather than silently forgetting a file nothing will ever attempt to remove again.
    /// </summary>
    private void DeleteTemporary()
    {
        if (_temporary is null) return;

        try
        {
            Directory.Delete(Path.GetDirectoryName(_temporary)!, recursive: true);
            _temporary = null;
        }
        catch (Exception)
        {
            // Scratch in the operating system's temporary directory. Widened to every exception, not
            // only IOException/UnauthorizedAccessException: this is best-effort cleanup with no
            // honest reason to ever throw out of the terminal lock that calls it (see RunAsync's own
            // remarks at that call site), and a delete we could not perform is not worth failing a
            // finished read over. Left un-nulled above so it is retried later.
        }
    }
}
