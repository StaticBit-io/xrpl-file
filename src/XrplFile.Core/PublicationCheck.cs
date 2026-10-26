using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XrplFile;

/// <summary>
/// The writer's check of its own publication (spec S9, S21, S22). The root covers block hashes and
/// not each block's first ledger, so a list that roots correctly does not prove its blocks are
/// reachable; the deterministic checks here do, from the run's own data and without a read.
/// </summary>
internal static class PublicationCheck
{
    private const int ManifestReadAttempts = 3;

    private static readonly TimeSpan ManifestReadPause = TimeSpan.FromSeconds(1);

    /// <summary>Spec S9(a), for every block.</summary>
    public static void RequireReachable(IReadOnlyList<BlockEntry> plan, ChunkLanding landing, Manifest manifest)
    {
        for (int block = 0; block < plan.Count; block++)
        {
            if (!landing.TryBlockLedgers(block, out LedgerRange range))
            {
                throw new XrplFileException(XrplFileError.ChunkMissing, $"no chunk of block {block} is known to have landed", block);
            }

            if (plan[block].FirstLedger != range.First)
            {
                throw new XrplFileException(XrplFileError.BlockMismatch,
                    $"block {block} names ledger {plan[block].FirstLedger} as its first and its first chunk is in {range.First}", block);
            }

            if (range.First < manifest.FirstLedger || range.Last > manifest.LastLedger)
            {
                throw new XrplFileException(XrplFileError.BlockMismatch,
                    $"block {block}'s chunks lie in ledgers {range.First}..{range.Last}, outside the window {manifest.FirstLedger}..{manifest.LastLedger}", block);
            }
        }
    }

    /// <summary>
    /// The block list as read from the ledger equals the plan, ledgers included (spec S9). The root
    /// covers block hashes only, so a list that roots correctly can still send a reader to the wrong
    /// ledger; the list the writer built cannot show that, because it was built from the plan.
    /// </summary>
    public static void RequireListed(IReadOnlyList<BlockEntry> plan, IReadOnlyList<BlockEntry> published)
    {
        bool same = plan.Count == published.Count
            && plan.Zip(published).All(pair => pair.First.Index == pair.Second.Index
                && pair.First.FirstLedger == pair.Second.FirstLedger
                && string.Equals(pair.First.HashHex, pair.Second.HashHex, StringComparison.OrdinalIgnoreCase));

        if (!same)
        {
            throw new XrplFileException(XrplFileError.ListMismatch, "the block list on the ledger is not the plan");
        }
    }

    /// <summary>
    /// Spec S21. The sample is the writer's check of its own publication, not a proof for third
    /// parties: a seed from the root is deterministic and recomputable and needs no extra read.
    /// </summary>
    public static IReadOnlyList<int> SampleBlocks(string rootHex, int blocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, 1);

        if (blocks <= 4) return [.. Enumerable.Range(0, blocks)];

        byte[] input = new byte[MerkleTree.HashBytes + 4];
        Convert.FromHexString(rootHex).CopyTo(input, 0);
        List<int> sample = [0, blocks - 1];

        for (uint counter = 0; sample.Count < 4; counter++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(MerkleTree.HashBytes), counter);
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(input));
            int block = 1 + (int)(value % (ulong)(blocks - 2));

            if (!sample.Contains(block)) sample.Add(block);
        }

        return sample;
    }

    /// <summary>
    /// Spec S22: the link as a public reader takes it. Only the real transaction proves a reader
    /// accepts the link, and a manifest a resume recovered deserves the same proof.
    /// </summary>
    public static async Task RequireAcceptedAsync(
        ILedgerSource source, XrplFileLink link, Manifest planned, string writer, CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(source);
        ManifestRecord record;

        try
        {
            record = await ReadAsync(reader, link, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException e) when (e.Error == XrplFileError.CorruptManifest)
        {
            throw Rejected(link, e.Message, e);
        }

        if (!string.Equals(record.Writer, writer, StringComparison.Ordinal))
        {
            throw Rejected(link, $"it was written by {record.Writer}, not by {writer}");
        }

        if (!string.Equals(record.Manifest.Encode(), planned.Encode(), StringComparison.Ordinal))
        {
            throw Rejected(link, "it describes another file than the one written");
        }
    }

    /// <summary>
    /// A node that did not answer, or does not show the transaction yet, is asked again: neither is a
    /// rejection, which is permanent. A node whose history starts late answers a missing <c>tx</c>
    /// with <see cref="XrplFileError.NodeLacksHistory"/>; a manifest that just landed is missing in
    /// the same way. Still missing after that, the run stops resumable.
    /// </summary>
    private static async Task<ManifestRecord> ReadAsync(XrplFileReader reader, XrplFileLink link, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await reader.ManifestAsync(link, cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error is XrplFileError.NodeUnreachable or XrplFileError.ManifestNotFound or XrplFileError.NodeLacksHistory
                && attempt < ManifestReadAttempts)
            {
                await Task.Delay(ManifestReadPause, cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error is XrplFileError.ManifestNotFound or XrplFileError.NodeLacksHistory)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the manifest {link.TxHash} landed and the node does not show it to a reader yet: {e.Message}", inner: e);
            }
        }
    }

    private static XrplFileException Rejected(XrplFileLink link, string why, Exception? inner = null) =>
        new(XrplFileError.ManifestRejected, $"the manifest {link.TxHash} landed and a reader does not accept it: {why}", inner: inner, link: link);
}
