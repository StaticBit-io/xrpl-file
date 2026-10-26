using Xrpl.Client.Exceptions;

namespace XrplFile;

/// <summary>
/// One memo a resumed run found already in the ledger: the transaction that carried it, the ledger
/// it landed in, and the bytes it holds.
///
/// The ledger is part of it because a manifest's window is made of ledgers, not of positions. A
/// chunk re-sent by a second run lands after the ones around it, so a window taken from the first
/// and last chunk *index* would leave it outside — and the reader gates every chunk on that window,
/// so the file would stop reading back.
/// </summary>
public sealed record LandedMemo(string TxHash, long LedgerIndex, string MemoDataHex);

/// <summary>
/// What the ledger already holds for one publication: the chunks compactly (spec S19), block lists
/// by list index with their bytes. The writer completes <see cref="Chunks"/> in place: it is the one
/// copy of the publication's chunk tracking.
/// </summary>
public sealed record LandedMemos(ChunkLanding Chunks, IReadOnlyDictionary<int, LandedMemo> Lists);

public sealed class WriterOptions
{
    /// <summary>Read everything back through the reader before writing the manifest. On by default; the format does not require it.</summary>
    public bool ReadBack { get; init; } = true;
}

public sealed record PublishProgress(string Stage, long Done, long Total);

/// <summary>A finished publication (spec S8): counts and the manifest's hash, never a per-chunk list.</summary>
public sealed record PublishResult(XrplFileLink Link, Manifest Manifest, string Writer, long ChunkCount, int ListCount, string ManifestHash);

/// <summary>
/// Puts a file into the ledger the way the reader will later verify it: chunks, then block
/// lists, then a read-back against the root, then the manifest. An upload that breaks half-way
/// leaves junk in an account's history, never a dead link.
/// </summary>
public sealed class XrplFileWriter(ISubmissionStrategy strategy, ILedgerSource source, WriterOptions? options = null)
{
    private const int ReadBackAttempts = 3;

    private static readonly TimeSpan ReadBackPause = TimeSpan.FromSeconds(1);

    private readonly ISubmissionStrategy _strategy = strategy;
    private readonly ILedgerSource _source = source;
    private readonly WriterOptions _options = options ?? new WriterOptions();

    /// <param name="progress">
    /// Where stage reports go, or <see langword="null"/> for none. Every report is made on the
    /// thread that produced it, synchronously: nothing here posts one somewhere else first, so a
    /// caller that wants reports marshalled — onto a UI thread, or onto the pool — passes an
    /// implementation that does that, and a caller that wants them where they happen passes one
    /// that does not. Handing this a <see cref="Progress{T}"/> is the first of those and is a fine
    /// thing to do; what this method will not do is make that choice on a caller's behalf.
    /// <para>
    /// Reports are frequent on the chunk and list stages — one per submitted memo — so an
    /// implementation that blocks blocks the publication. The stage a report names only ever moves
    /// forward, and the same stage is reported many times.
    /// </para>
    /// </param>
    public Task<PublishResult> PublishAsync(
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken) =>
        PublishAsync(new MemoryPublicationSource(file), blockHashes: null, type, name, writer, network, alreadyLanded, progress, cancellationToken);

    /// <summary>
    /// Publishes a file read a chunk at a time and never held whole (spec 4.3). The caller keeps
    /// owning <paramref name="file"/>; reports and failures are as for the
    /// <see cref="ReadOnlyMemory{T}"/> overload.
    /// </summary>
    public Task<PublishResult> PublishAsync(
        IPublicationSource file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken) =>
        PublishAsync(file, blockHashes: null, type, name, writer, network, alreadyLanded, progress, cancellationToken);

    /// <param name="blockHashes">The file's block hashes when the caller already streamed them (the session does, for the root), or null.</param>
    internal async Task<PublishResult> PublishAsync(
        IPublicationSource file, IReadOnlyList<byte[]>? blockHashes, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(file));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("a manifest needs a MIME type", nameof(type));

        // Before anything is submitted. The link is built from this at the very end, so a network
        // the link refuses used to be discovered after a thousand paid transactions and a manifest
        // in the ledger -- with the manifest's hash nowhere in the exception, so the file was
        // published and its link lost.
        string canonical = XrplFileLink.RequireNetwork(network);

        try
        {
            return await PublishCoreAsync(file, blockHashes, type, name, writer, canonical, alreadyLanded, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancellation nobody asked for: the SDK cancels requests in flight when the network
            // drops, and a strategy's wait for validation reads sequences through the SDK's typed
            // path, which nothing below renames. Reporting it as cancelled would tell the person
            // they stopped a run they did not stop (spec I14, 4.8).
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the connection to the node dropped: {e.Message}", inner: e);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            // Only a node that did not answer is unreachable: a connection never made or lost, and
            // a request that timed out. The original exception rides along rather than being lost.
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node did not answer: {e.Message}", inner: e);
        }
        catch (Xrpl.Client.Exceptions.RippledException e)
        {
            // The node answered, and precisely: actNotFound for an unfunded wallet, invalidParams
            // for a request it would not take. Calling that "the node did not answer" is false and
            // throws away the one thing worth knowing. It is a refusal, and it is named.
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node refused the publication with {e.Classify().RawError}: {e.Message}", inner: e);
        }
        catch (Xrpl.Client.Exceptions.XrplException e)
        {
            // Everything else the SDK raises. A RippledException above is the only shape in which
            // the node itself speaks; the rest are ways of not getting an answer -- a socket the
            // SDK could not clean up, a connection it created as null, a response it could not
            // read -- and calling those a refusal blames the node for saying no when it said
            // nothing at all. This is the rule the reading side already applies in
            // XrplClientLedgerSource.IsNodeFailure, so both halves of the library now give the
            // same SDK exception the same name.
            throw new XrplFileException(XrplFileError.NodeUnreachable,
                $"the node did not answer usably: {e.Message}", inner: e);
        }
    }

    /// <summary>
    /// A node that did not answer: never connected, hung up, or took longer than the request
    /// timeout. Each of these is an <see cref="Xrpl.Client.Exceptions.XrplException"/> in the SDK,
    /// the same base the node's own answers derive from, which is why they are named one by one.
    /// </summary>
    private static bool IsTransportFailure(Exception e) =>
        e is Xrpl.Client.Exceptions.NotConnectedException
            or Xrpl.Client.Exceptions.DisconnectedException
            or Xrpl.Client.Exceptions.ConnectionException
            or Xrpl.Client.Exceptions.TimeoutException
            or System.TimeoutException;

    private async Task<PublishResult> PublishCoreAsync(
        IPublicationSource file, IReadOnlyList<byte[]>? blockHashes, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(file.Length);
        ChunkLanding landing = alreadyLanded?.Chunks ?? new ChunkLanding(chunkCount);

        if (landing.ChunkCount != chunkCount)
        {
            throw new ArgumentException($"the landed chunks describe {landing.ChunkCount} chunks and the file has {chunkCount}", nameof(alreadyLanded));
        }

        IReadOnlyList<byte[]> blocks = blockHashes ?? await FileTree.BlockHashesAsync(file, null, cancellationToken).ConfigureAwait(false);

        if (blocks.Count != landing.BlockCount)
        {
            throw new ArgumentException($"{blocks.Count} block hashes for a file of {landing.BlockCount} blocks", nameof(blockHashes));
        }

        // Chunks. What a resumed run found in the ledger is not sent again: it would be paid for
        // twice and put a second copy in the account's history.
        IndexRanges missing = landing.Missing();

        if (missing.Count > 0)
        {
            long carried = landing.Landed;

            SubmittedMemos sent = await _strategy.SubmitAllAsync(
                new LazyMemoList(file, missing),
                new InlineProgress(done => progress?.Report(new PublishProgress("chunks", carried + done, chunkCount))),
                cancellationToken).ConfigureAwait(false);

            await FoldAsync(sent, call => missing[call], landing, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new PublishProgress("chunks", chunkCount, chunkCount));
        }

        // The window is the ledgers the chunks are actually in, not those of the first and last
        // index: a resumed run re-sends a low index late.
        LedgerRange window = landing.Window();
        long lastLedger = window.Last;
        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(blocks, landing.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);

        if (listMemos.Count > 0)
        {
            lastLedger = Math.Max(lastLedger, await PublishListsAsync(listMemos, alreadyLanded, progress, cancellationToken).ConfigureAwait(false));
        }

        Manifest manifest = ManifestPlan.Build(type, name, file.Length, entries, window.First, lastLedger, listMemos.Count);

        PublicationCheck.RequireReachable(entries, landing, manifest);

        if (_options.ReadBack)
        {
            // Batch publications are sampled (spec S9, S21); ticket and sequence ones, at most 64 MiB
            // and with no bytes checked as their memos land, are read back in full.
            if (_strategy is BatchStrategy)
            {
                await ReadSampleBackAsync(file, entries, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReadEverythingBackAsync(file, entries, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        SubmittedMemos manifestLanded = await _strategy.SubmitAllAsync([ManifestPlan.Memo(manifest)], null, cancellationToken).ConfigureAwait(false);

        if (!manifestLanded.TryHashOf(0, out string? manifestHash))
        {
            throw new InvalidOperationException("the strategy kept no hash for the manifest, and the link is that hash");
        }

        XrplFileLink link = new(network, manifestHash);
        await PublicationCheck.RequireAcceptedAsync(_source, link, manifest, writer, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(link, manifest, writer, chunkCount, listMemos.Count, manifestHash);
    }

    /// <summary>
    /// Folds a chunk pass into the landing (spec S19, S23): a memo's ledger when the strategy kept it,
    /// else the ledger of its hash (the sequence path), else only its bit — and then every block range
    /// the strategy reported. The two agree where both exist, and ranges only ever widen.
    /// </summary>
    private async Task FoldAsync(SubmittedMemos sent, Func<int, long> chunkOf, ChunkLanding landing, CancellationToken cancellationToken)
    {
        if (sent.Landed.Count != sent.Count)
        {
            throw new InvalidOperationException($"the strategy returned with {sent.Count - sent.Landed.Count} of {sent.Count} memos not landed");
        }

        for (int call = 0; call < sent.Count; call++)
        {
            long chunk = chunkOf(call);

            if (sent.TryLedgerOf(call, out long ledger)) landing.Land(chunk, ledger);
            else if (sent.TryHashOf(call, out string? hash)) landing.Land(chunk, await LedgerOfAsync(hash, cancellationToken).ConfigureAwait(false));
            else landing.MarkLanded(chunk);
        }

        foreach ((int block, LedgerRange range) in sent.BlockLedgers) landing.Widen(block, range);
    }

    /// <summary>
    /// Sends the block-list memos a resume did not find byte for byte, and returns the last ledger
    /// any list memo is in: they are reachable through the window too.
    /// </summary>
    private async Task<long> PublishListsAsync(
        IReadOnlyList<MemoSubmission> listMemos, LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        List<int> missing = [];
        long last = long.MinValue;

        for (int j = 0; j < listMemos.Count; j++)
        {
            // A list payload carries per-block ledgers, so the same index from an interrupted run can
            // describe another window; only an identical payload is the one this run would send.
            if (alreadyLanded is not null
                && alreadyLanded.Lists.TryGetValue(j, out LandedMemo? landed)
                && string.Equals(landed.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                last = Math.Max(last, landed.LedgerIndex);
                continue;
            }

            missing.Add(j);
        }

        if (missing.Count == 0)
        {
            progress?.Report(new PublishProgress("lists", listMemos.Count, listMemos.Count));
            return last;
        }

        int carried = listMemos.Count - missing.Count;
        SubmittedMemos sent = await _strategy.SubmitAllAsync(
            [.. missing.Select(j => listMemos[j])],
            new InlineProgress(done => progress?.Report(new PublishProgress("lists", carried + done, listMemos.Count))),
            cancellationToken).ConfigureAwait(false);

        for (int call = 0; call < sent.Count; call++)
        {
            long ledger = sent.TryLedgerOf(call, out long known)
                ? known
                : sent.TryHashOf(call, out string? hash)
                    ? await LedgerOfAsync(hash, cancellationToken).ConfigureAwait(false)
                    : throw new InvalidOperationException("the strategy kept neither a ledger nor a hash for a block-list memo");

            last = Math.Max(last, ledger);
        }

        return last;
    }

    /// <summary>
    /// Reads every block back through the reader a stranger would use, before the link exists, and
    /// compares it with the source chunk by chunk, one block in memory at a time. The ticket and
    /// sequence paths keep this full read-back (spec S9).
    /// </summary>
    private async Task ReadEverythingBackAsync(
        IPublicationSource file, IReadOnlyList<BlockEntry> plan, Manifest manifest, string writer, long lastLedger,
        IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
        ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
        IReadOnlyList<BlockEntry> published = await ReadBackAsync(
            () => reader.BlockListAsync(unpublished, cancellationToken), cancellationToken).ConfigureAwait(false);
        PublicationCheck.RequireListed(plan, published);

        for (int block = 0; block < published.Count; block++)
        {
            BlockEntry entry = published[block];
            byte[] content = await ReadBackAsync(
                () => reader.BlockAsync(unpublished, entry, cancellationToken), cancellationToken).ConfigureAwait(false);

            await RequireSameAsSourceAsync(file, entry.Index, content, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PublishProgress("read-back", block + 1, published.Count));
        }
    }

    /// <summary>The full block list rooted to the manifest root and compared with the plan, then the blocks of spec S21, each compared with the source.</summary>
    private async Task ReadSampleBackAsync(
        IPublicationSource file, IReadOnlyList<BlockEntry> plan, Manifest manifest, string writer, long lastLedger,
        IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
        ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
        IReadOnlyList<BlockEntry> published = await ReadBackAsync(
            () => reader.BlockListAsync(unpublished, cancellationToken), cancellationToken).ConfigureAwait(false);
        PublicationCheck.RequireListed(plan, published);
        IReadOnlyList<int> sample = PublicationCheck.SampleBlocks(manifest.Root, published.Count);

        for (int k = 0; k < sample.Count; k++)
        {
            BlockEntry entry = published[sample[k]];
            byte[] content = await ReadBackAsync(
                () => reader.BlockAsync(unpublished, entry, cancellationToken), cancellationToken).ConfigureAwait(false);

            await RequireSameAsSourceAsync(file, entry.Index, content, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PublishProgress("read-back", k + 1, sample.Count));
        }
    }

    /// <summary>A block read back is exactly the source's chunks of that block, no more and no fewer bytes.</summary>
    private static async Task RequireSameAsSourceAsync(IPublicationSource file, int block, byte[] content, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(file.Length);
        long first = (long)block * FileTree.BlockChunks;
        long end = Math.Min(first + FileTree.BlockChunks, chunkCount);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        int at = 0;

        for (long chunk = first; chunk < end; chunk++)
        {
            int length = await file.ReadChunkAsync(chunk, buffer, cancellationToken).ConfigureAwait(false);

            if (at + length > content.Length || !content.AsSpan(at, length).SequenceEqual(buffer.AsSpan(0, length)))
            {
                throw new XrplFileException(XrplFileError.BlockMismatch, $"block {block} read back differs from the file", block);
            }

            at += length;
        }

        if (at != content.Length)
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"block {block} read back {content.Length} bytes and the file has {at} in it", block);
        }
    }

    /// <summary>
    /// Adapts the per-memo count a strategy reports into this class's own stage report, and does
    /// it on the strategy's own thread.
    ///
    /// <see cref="Progress{T}"/> would be the obvious way to write this and is the wrong one. Built
    /// without a <see cref="SynchronizationContext"/> — which is every context this library runs
    /// in — it posts each report to the thread pool, so what a caller passed as its
    /// <see cref="IProgress{T}"/> is invoked at some later, unpredictable time on a thread of the
    /// pool's choosing. That throws away the synchronisation the caller chose by supplying its own
    /// implementation, and it is the only place this class did it: the stage reports around these
    /// two calls have always gone straight to <c>progress</c>. It matters beyond tidiness — a
    /// caller that treats a report as a sign of life (the desktop application touches its vault's
    /// idle lock on every one) sees those signs arrive late under load, and acts on a run it
    /// believes has stalled.
    /// </summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>
    /// Takes a read-back read again when the node did not answer it (spec B24): a request left
    /// unanswered costs its connection, and the next one goes over a new connection. Without this, a
    /// socket that went silent during the read-back failed a run whose chunks had all landed and been
    /// paid for. A node that answers none of the attempts still fails the run as unreachable.
    /// </summary>
    private static async Task<T> ReadBackAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable && attempt < ReadBackAttempts)
            {
                await Task.Delay(ReadBackPause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> LedgerOfAsync(string hash, CancellationToken cancellationToken)
    {
        LedgerTransaction? tx = await _source.TransactionAsync(hash, cancellationToken).ConfigureAwait(false);

        if (tx is null || !tx.Validated)
        {
            throw new XrplFileException(XrplFileError.ValidationTimeout, $"transaction {hash} was accepted but is not in a validated ledger");
        }

        if (!tx.Succeeded)
        {
            throw new XrplFileException(XrplFileError.SubmitRefused, $"transaction {hash} was validated but did not succeed");
        }

        return tx.LedgerIndex;
    }
}
