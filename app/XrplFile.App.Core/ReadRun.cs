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
/// history, no generation pinning, no resumption. The reason is one sentence — a read costs nothing
/// and can simply be started again, and an interrupted publication cannot.
/// </summary>
public sealed class ReadRun(IConnectionFactory connections, IFilePickerService picker)
{
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private ReadState _state = ReadState.Idle;
    private int _blocksDone;
    private int _blocksTotal;
    private long _bytesDone;
    private long _bytesTotal;
    private string? _name;
    private string? _failure;
    private bool _saved;
    private string? _temporary;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public ReadSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ReadSnapshot(
                    _state, _blocksDone, _blocksTotal, _bytesDone, _bytesTotal, _name, _failure, _saved);
            }
        }
    }

    /// <summary>Where the blocks are being written. Exists so a test can prove it is cleaned up.</summary>
    internal string? TemporaryPathForTests
    {
        get { lock (_gate) return _temporary; }
    }

    /// <summary>Starts a read. <see langword="false"/> when one is already going.</summary>
    public bool Start(XrplFileLink link, ManifestRecord manifest)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(manifest);

        CancellationTokenSource cancellation = new();

        lock (_gate)
        {
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
            Completion = Task.Run(() => RunAsync(link, manifest, cancellation.Token));
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
            running = Completion;
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
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        string path;
        string suggested;

        lock (_gate)
        {
            if (_state != ReadState.Succeeded || _temporary is null) return;

            path = _temporary;

            // A file may have been published with no name at all: Manifest.Name is nullable. A
            // suggestion derived from nothing is still better than an empty dialog.
            suggested = string.IsNullOrWhiteSpace(_name) ? "xrpl-file.bin" : _name;
        }

        await using FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool saved = await picker.SaveStreamAsync(suggested, content, cancellationToken).ConfigureAwait(false);

        lock (_gate) _saved = saved;
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
        }
    }

    private async Task RunAsync(XrplFileLink link, ManifestRecord manifest, CancellationToken cancellationToken)
    {
        ReadState finalState;
        string? failure = null;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(link.Network, cancellationToken).ConfigureAwait(false);

            // One of the four things that moved out of ReadAsync (design 3.1). EarliestLedgerAsync
            // is public on ILedgerSource, so this needs nothing added to the library.
            long? earliest = await connection.Source.EarliestLedgerAsync(cancellationToken).ConfigureAwait(false);

            if (earliest is long first && first > manifest.Manifest.FirstLedger)
            {
                throw new XrplFileException(
                    XrplFileError.NodeLacksHistory,
                    $"this node keeps history from ledger {first} and the file starts at {manifest.Manifest.FirstLedger}");
            }

            XrplFileReader reader = new(connection.Source);

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
                    $"the blocks assemble to {written} bytes and the file's description says {manifest.Manifest.Size}");
            }

            finalState = ReadState.Succeeded;
        }
        catch (OperationCanceledException)
        {
            finalState = ReadState.Cancelled;
        }
        catch (XrplFileException ex)
        {
            finalState = ReadState.Failed;
            failure = ReadErrorMessages.For(ex);
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
            // A read that did not finish leaves nothing behind: its temporary file is a partial
            // download, and there is no honest use for one.
            if (finalState != ReadState.Succeeded) DeleteTemporary();

            _failure = failure;
            _state = finalState;
        }
    }

    /// <summary>Called under <see cref="_gate"/>. Removes the temporary file and the directory holding it.</summary>
    private void DeleteTemporary()
    {
        if (_temporary is null) return;

        try
        {
            Directory.Delete(Path.GetDirectoryName(_temporary)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Scratch in the operating system's temporary directory, which the system sweeps. A
            // file we could not remove is not worth failing a finished read over.
        }

        _temporary = null;
    }
}
