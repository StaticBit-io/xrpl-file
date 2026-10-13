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
/// What the ledger already holds for one publication: chunks by chunk index, block lists by list
/// index.
///
/// Both are checked against the bytes this run would send before they are believed — the chunks by
/// the session's sweep, which has them, and the lists here in the writer, which is the only place
/// the list payloads exist. A marker names no publication of its own, so <c>xf/c/0</c> is
/// <c>xf/c/0</c> for every file an account has ever published; the bytes are what tell them apart.
/// </summary>
public sealed record LandedMemos(
    IReadOnlyDictionary<int, LandedMemo> Chunks,
    IReadOnlyDictionary<int, LandedMemo> Lists);

public sealed class WriterOptions
{
    /// <summary>Read everything back through the reader before writing the manifest. On by default; the format does not require it.</summary>
    public bool ReadBack { get; init; } = true;
}

public sealed record PublishProgress(string Stage, long Done, long Total);

public sealed record PublishResult(
    XrplFileLink Link,
    Manifest Manifest,
    string Writer,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<string> ListHashes,
    string ManifestHash);

/// <summary>
/// Puts a file into the ledger the way the reader will later verify it: chunks, then block
/// lists, then a read-back against the root, then the manifest. An upload that breaks half-way
/// leaves junk in an account's history, never a dead link.
/// </summary>
public sealed class XrplFileWriter(ISubmissionStrategy strategy, ILedgerSource source, WriterOptions? options = null)
{
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
    public async Task<PublishResult> PublishAsync(
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        if (file.IsEmpty) throw new ArgumentException("an empty file has no chunks and no root", nameof(file));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("a manifest needs a MIME type", nameof(type));

        // Before anything is submitted. The link is built from this at the very end, so a network
        // the link refuses used to be discovered after a thousand paid transactions and a manifest
        // in the ledger -- with the manifest's hash nowhere in the exception, so the file was
        // published and its link lost.
        string canonical = XrplFileLink.RequireNetwork(network);

        try
        {
            return await PublishCoreAsync(file, type, name, writer, canonical, alreadyLanded, progress, cancellationToken).ConfigureAwait(false);
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
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        int blocks = FileTree.BlockCount(chunks.Count);

        // Chunks. A resumed run is handed what the ledger already holds for some indices; those
        // are not sent again -- re-sending would be paid for twice and would put a second copy of
        // each chunk in the account's history.
        IReadOnlyList<MemoSubmission> chunkMemos =
            [.. chunks.Select((chunk, i) => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunk), null))];

        List<MemoSubmission> missingMemos = [];
        List<int> missingIndices = [];

        // Two maps, both keyed by chunk index rather than by position in a batch: the transaction
        // that carried each chunk, and the ledger it is in. The second one is what the manifest's
        // window is built from, so it has to cover every chunk -- an already-landed one brings its
        // ledger with it, and a freshly sent one is looked up below.
        Dictionary<int, string> chunkByIndex = [];
        Dictionary<int, long> chunkLedgers = [];

        for (int i = 0; i < chunkMemos.Count; i++)
        {
            if (alreadyLanded is not null && alreadyLanded.Chunks.TryGetValue(i, out LandedMemo? landed))
            {
                chunkByIndex[i] = landed.TxHash;
                chunkLedgers[i] = landed.LedgerIndex;
                continue;
            }

            missingMemos.Add(chunkMemos[i]);
            missingIndices.Add(i);
        }

        if (missingMemos.Count > 0)
        {
            int carried = chunkByIndex.Count;

            IReadOnlyList<SubmittedMemo> sent = await _strategy.SubmitAllAsync(
                missingMemos,
                new InlineProgress(done => progress?.Report(new PublishProgress("chunks", carried + done, chunkMemos.Count))),
                cancellationToken).ConfigureAwait(false);

            foreach (SubmittedMemo memo in sent)
            {
                chunkByIndex[missingIndices[memo.Index]] = memo.TxHash;
            }

            // Per chunk, not per position. A strategy hands back a hash and nothing else, and the
            // ledger a hash is in is the only thing that puts it inside or outside the window.
            foreach (SubmittedMemo memo in sent)
            {
                int index = missingIndices[memo.Index];
                chunkLedgers[index] = await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            progress?.Report(new PublishProgress("chunks", chunkMemos.Count, chunkMemos.Count));
        }

        List<string> chunkHashes = [.. Enumerable.Range(0, chunkMemos.Count).Select(i => chunkByIndex[i])];

        // The window is the ledgers the chunks are actually in, not the ledgers of the first and
        // last index. Those agree only when one run sent every chunk in order; a resumed run whose
        // surviving set has a hole re-sends a low index late, and reading the window off the ends
        // would put that chunk -- or every chunk of the first run -- outside it.
        long firstLedger = chunkLedgers.Values.Min();
        long lastLedger = chunkLedgers.Values.Max();

        // Block entries: hash over the chunks in memory, first ledger from the ledger itself --
        // the earliest of the block's own chunks, which is where a reader has to start paging to
        // find all of them.
        List<BlockEntry> entries = new(blocks);
        for (int block = 0; block < blocks; block++)
        {
            int firstChunk = block * FileTree.BlockChunks;
            List<byte[]> members = [.. chunks.Skip(firstChunk).Take(FileTree.BlockChunks)];
            long ledger = Enumerable.Range(firstChunk, members.Count).Min(i => chunkLedgers[i]);
            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), ledger));
        }

        string root = FileTree.RootHex(entries);

        // Block lists, only when there is more than one block.
        List<string> listHashes = [];
        if (blocks > 1)
        {
            IReadOnlyList<string> payloads = FileTree.EncodeEntries(entries);
            IReadOnlyList<MemoSubmission> listMemos =
                [.. payloads.Select((payload, j) => new MemoSubmission(MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(payload), MemoLayout.JsonFormatHex))];

            List<MemoSubmission> missingLists = [];
            List<int> missingListIndices = [];
            Dictionary<int, string> listByIndex = [];
            Dictionary<int, long> listLedgers = [];

            for (int j = 0; j < listMemos.Count; j++)
            {
                // A run can die during the read-back or the manifest, which is after the lists
                // have landed -- so they are reconciled like the chunks rather than assumed
                // absent. The bytes decide: a list payload carries the per-block ledgers, so the
                // same index from an interrupted run can describe a different window, and only an
                // identical payload is the one this run would have sent.
                if (alreadyLanded is not null
                    && alreadyLanded.Lists.TryGetValue(j, out LandedMemo? landedList)
                    && string.Equals(landedList.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
                {
                    listByIndex[j] = landedList.TxHash;
                    listLedgers[j] = landedList.LedgerIndex;
                    continue;
                }

                missingLists.Add(listMemos[j]);
                missingListIndices.Add(j);
            }

            if (missingLists.Count > 0)
            {
                int carried = listByIndex.Count;

                IReadOnlyList<SubmittedMemo> listsLanded = await _strategy.SubmitAllAsync(
                    missingLists,
                    new InlineProgress(done => progress?.Report(new PublishProgress("lists", carried + done, listMemos.Count))),
                    cancellationToken).ConfigureAwait(false);

                foreach (SubmittedMemo memo in listsLanded)
                {
                    listByIndex[missingListIndices[memo.Index]] = memo.TxHash;
                }

                foreach (SubmittedMemo memo in listsLanded)
                {
                    int index = missingListIndices[memo.Index];
                    listLedgers[index] = await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                progress?.Report(new PublishProgress("lists", listMemos.Count, listMemos.Count));
            }

            listHashes = [.. Enumerable.Range(0, listMemos.Count).Select(j => listByIndex[j])];

            // Every list memo, not only the last one: they are reachable through the window too,
            // and a resumed run's lists need not be in one ledger or in index order.
            lastLedger = Math.Max(lastLedger, listLedgers.Values.Max());
        }

        Manifest manifest = new(type, file.Length, MemoLayout.ChunkBytes, root, firstLedger, lastLedger, blocks, listHashes.Count, name);

        // Read-back through the same reader a stranger would use, before the link exists.
        if (_options.ReadBack)
        {
            XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
            ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
            IReadOnlyList<BlockEntry> published = await reader.BlockListAsync(unpublished, cancellationToken).ConfigureAwait(false);

            long at = 0;
            for (int block = 0; block < published.Count; block++)
            {
                byte[] content = await reader.BlockAsync(unpublished, published[block], cancellationToken).ConfigureAwait(false);

                long remaining = file.Length - at;
                if (content.Length > remaining)
                {
                    throw new XrplFileException(XrplFileError.BlockMismatch,
                        $"block {block} read back {content.Length} bytes but only {remaining} remain in the file", block);
                }

                if (!content.AsSpan().SequenceEqual(file.Span.Slice((int)at, content.Length)))
                {
                    throw new XrplFileException(XrplFileError.BlockMismatch, $"block {block} read back differs from the file", block);
                }

                at += content.Length;
                progress?.Report(new PublishProgress("read-back", block + 1, published.Count));
            }
        }

        // The manifest, last.
        IReadOnlyList<SubmittedMemo> manifestLanded = await _strategy.SubmitAllAsync(
            [new MemoSubmission(MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex)],
            null, cancellationToken).ConfigureAwait(false);
        string manifestHash = manifestLanded[0].TxHash;
        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(new XrplFileLink(network, manifestHash), manifest, writer, chunkHashes, listHashes, manifestHash);
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
