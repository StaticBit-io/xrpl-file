using Xrpl.Client.Exceptions;
using Xrpl.Wallet;

namespace XrplFile;

public sealed class WriterOptions
{
    /// <summary>The fee on every transaction. A caller that has asked the network for its open-ledger fee sets this from it.</summary>
    public int FeeDrops { get; init; } = 12;

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan ValidationPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Read everything back through the reader before writing the manifest. On by default; the format does not require it.</summary>
    public bool ReadBack { get; init; } = true;

    /// <summary>
    /// After this many submissions the writer waits for validation before continuing, so an open
    /// ledger never holds more than a batch of this account's transactions.
    /// </summary>
    public int BatchSize { get; init; } = 200;
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
public sealed class XrplFileWriter(ILedgerSink sink, ILedgerSource source, WriterOptions? options = null)
{
    private readonly ILedgerSink _sink = sink;
    private readonly ILedgerSource _source = source;
    private readonly WriterOptions _options = options ?? new WriterOptions();

    public async Task<PublishResult> PublishAsync(
        ReadOnlyMemory<byte> file, string type, string? name, XrplWallet wallet, string network,
        IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
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
            return await PublishCoreAsync(file, type, name, wallet, canonical, progress, cancellationToken).ConfigureAwait(false);
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
            // Everything else the SDK raises -- an answer it could not read, a request it would not
            // build. The node is not silent, so not NodeUnreachable; still one error to switch on.
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the publication could not be carried out: {e.Message}", inner: e);
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
        ReadOnlyMemory<byte> file, string type, string? name, XrplWallet wallet, string network,
        IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        string writer = wallet.ClassicAddress;
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        int blocks = FileTree.BlockCount(chunks.Count);

        uint sequence = await _sink.SequenceAsync(writer, validated: false, cancellationToken).ConfigureAwait(false);

        // Chunks. A batch of transactions waits for validation before the next batch starts, so an
        // open ledger never holds more than a batch of this account's queued transactions.
        List<string> chunkHashes = new(chunks.Count);
        int chunksSinceValidation = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            chunkHashes.Add(await SubmitMemoAsync(wallet, sequence++, MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex(chunks[i]), null, cancellationToken).ConfigureAwait(false));
            progress?.Report(new PublishProgress("chunks", i + 1, chunks.Count));

            if (++chunksSinceValidation >= _options.BatchSize)
            {
                await WaitForValidationAsync(writer, sequence, cancellationToken).ConfigureAwait(false);
                chunksSinceValidation = 0;
            }
        }

        await WaitForValidationAsync(writer, sequence, cancellationToken).ConfigureAwait(false);

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
            int listsSinceValidation = 0;
            for (int j = 0; j < payloads.Count; j++)
            {
                listHashes.Add(await SubmitMemoAsync(wallet, sequence++, MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(payloads[j]), MemoLayout.JsonFormatHex, cancellationToken).ConfigureAwait(false));
                progress?.Report(new PublishProgress("lists", j + 1, payloads.Count));

                if (++listsSinceValidation >= _options.BatchSize)
                {
                    await WaitForValidationAsync(writer, sequence, cancellationToken).ConfigureAwait(false);
                    listsSinceValidation = 0;
                }
            }

            await WaitForValidationAsync(writer, sequence, cancellationToken).ConfigureAwait(false);
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
        string manifestHash = await SubmitMemoAsync(wallet, sequence++, MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex, cancellationToken).ConfigureAwait(false);
        await WaitForValidationAsync(writer, sequence, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(new XrplFileLink(network, manifestHash), manifest, writer, chunkHashes, listHashes, manifestHash);
    }

    private async Task<string> SubmitMemoAsync(XrplWallet wallet, uint sequence, string memoTypeHex, string memoDataHex, string? memoFormatHex, CancellationToken cancellationToken)
    {
        Dictionary<string, object> memo = new() { ["MemoType"] = memoTypeHex, ["MemoData"] = memoDataHex };
        if (memoFormatHex is not null) memo["MemoFormat"] = memoFormatHex;

        Dictionary<string, object> transaction = new()
        {
            ["TransactionType"] = "AccountSet",
            ["Account"] = wallet.ClassicAddress,
            ["Sequence"] = sequence,
            ["Fee"] = _options.FeeDrops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SigningPubKey"] = wallet.PublicKey,
            ["Memos"] = new List<object> { new Dictionary<string, object> { ["Memo"] = memo } },
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(transaction, wallet, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the transaction at sequence {sequence}");
        }

        return outcome.TxHash;
    }

    /// <summary>Waits until the account's validated sequence reaches the one after the last submission.</summary>
    private async Task WaitForValidationAsync(string account, uint nextSequence, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ValidationTimeout;
        uint validated = 0;

        while (true)
        {
            try
            {
                validated = await _sink.SequenceAsync(account, validated: true, cancellationToken).ConfigureAwait(false);
                if (validated >= nextSequence) return;
            }
            catch (Xrpl.Client.Exceptions.TimeoutException)
            {
                // A single poll the node did not answer in time means "not yet", not "stop" — the
                // deadline below is what actually ends the wait.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the validated sequence is {validated} and {nextSequence} was expected after {_options.ValidationTimeout}");
            }

            await Task.Delay(_options.ValidationPoll, cancellationToken).ConfigureAwait(false);
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
