using Xrpl.Client.Exceptions;

namespace XrplFile;

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

    public async Task<PublishResult> PublishAsync(
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        IReadOnlyDictionary<int, string>? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
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
        IReadOnlyDictionary<int, string>? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
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

        for (int i = 0; i < chunkMemos.Count; i++)
        {
            if (alreadyLanded is not null && alreadyLanded.ContainsKey(i)) continue;

            missingMemos.Add(chunkMemos[i]);
            missingIndices.Add(i);
        }

        Dictionary<int, string> chunkByIndex = alreadyLanded is null ? [] : new Dictionary<int, string>(alreadyLanded);

        if (missingMemos.Count > 0)
        {
            IReadOnlyList<SubmittedMemo> sent = await _strategy.SubmitAllAsync(
                missingMemos,
                new Progress<int>(done => progress?.Report(new PublishProgress("chunks", chunkByIndex.Count + done, chunkMemos.Count))),
                cancellationToken).ConfigureAwait(false);

            foreach (SubmittedMemo memo in sent)
            {
                chunkByIndex[missingIndices[memo.Index]] = memo.TxHash;
            }
        }
        else
        {
            progress?.Report(new PublishProgress("chunks", chunkMemos.Count, chunkMemos.Count));
        }

        List<string> chunkHashes = [.. Enumerable.Range(0, chunkMemos.Count).Select(i => chunkByIndex[i])];

        long firstLedger = await LedgerOfAsync(chunkHashes[0], cancellationToken).ConfigureAwait(false);
        long lastLedger = await LedgerOfAsync(chunkHashes[^1], cancellationToken).ConfigureAwait(false);

        // Block entries: hash over the chunks in memory, first ledger from the ledger itself.
        List<BlockEntry> entries = new(blocks);
        for (int block = 0; block < blocks; block++)
        {
            int firstChunk = block * FileTree.BlockChunks;
            List<byte[]> members = [.. chunks.Skip(firstChunk).Take(FileTree.BlockChunks)];
            long ledger = await LedgerOfAsync(chunkHashes[firstChunk], cancellationToken).ConfigureAwait(false);
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

            IReadOnlyList<SubmittedMemo> listsLanded = await _strategy.SubmitAllAsync(
                listMemos,
                new Progress<int>(done => progress?.Report(new PublishProgress("lists", done, listMemos.Count))),
                cancellationToken).ConfigureAwait(false);

            listHashes = [.. listsLanded.OrderBy(memo => memo.Index).Select(memo => memo.TxHash)];
            lastLedger = Math.Max(lastLedger, await LedgerOfAsync(listHashes[^1], cancellationToken).ConfigureAwait(false));
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
