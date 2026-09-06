using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace XrplFile.Tests;

public class VectorTests
{
    [Fact]
    public void The_stream_is_sha256_counter_mode_over_the_seed()
    {
        byte[] block0 = SHA256.HashData([.. Encoding.UTF8.GetBytes("s"), 0, 0, 0, 0]);
        byte[] block1 = SHA256.HashData([.. Encoding.UTF8.GetBytes("s"), 0, 0, 0, 1]);

        Assert.Equal(block0.Take(5).ToArray(), VectorStream.Bytes("s", 5));
        Assert.Equal(block0.Concat(block1.Take(3)).ToArray(), VectorStream.Bytes("s", 35));
        Assert.Empty(VectorStream.Bytes("s", 0));
    }

    [Fact]
    public void The_merkle_known_answers_are_in_the_file()
    {
        VectorMerkle merkle = Vectors.Load().Merkle;

        Assert.Equal("305DF59F9590C3C9AC63D2B2743C388E3792449078CEBF7FB3DBE6471643B2B7", merkle.LeafRoots["1"]);
        Assert.Equal("59564AA4292CEA48C658B67D5D4E0E04694BF5517C6D41122963399C66F97B5A", merkle.LeafRoots["1025"]);
        Assert.Equal("EED1B8043341A7F523DFF401B394D9C43B073226091649CD4F4C8BA018D5BFBD", merkle.BlockHashOfFiveChunks);
        Assert.Equal("3B6CCCD7E3E023FF393006F030315EE7AD9EB111B022B41FBA7E5B7A3973F688", merkle.RootOverThreeEntries);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1024000)]
    [InlineData(1024001)]
    [InlineData(2500500)]
    public void Every_file_vector_is_reproduced_by_the_library(long size)
    {
        VectorFile vector = Vectors.File(size);
        byte[] bytes = VectorStream.Bytes(vector.Seed, vector.Size);

        Assert.Equal(vector.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));

        IReadOnlyList<byte[]> chunks = FileTree.Split(bytes, vector.Chunk);
        Assert.Equal(vector.Chunks, chunks.Count);
        Assert.Equal(vector.Blocks, FileTree.BlockCount(chunks.Count));

        List<BlockEntry> entries = [];
        for (int block = 0; block < vector.Blocks; block++)
        {
            List<byte[]> members = [.. chunks.Skip(block * FileTree.BlockChunks).Take(FileTree.BlockChunks)];
            string hash = Convert.ToHexString(FileTree.BlockHash(members));
            Assert.Equal(vector.BlockHashes[block], hash);
            entries.Add(new BlockEntry(block, hash, vector.Ledger.BlockLedgers[block]));
        }

        Assert.Equal(vector.Root, FileTree.RootHex(entries));

        if (vector.Blocks == 1)
        {
            Assert.Empty(vector.Lists);
            Assert.Equal(vector.Root, vector.BlockHashes[0]);
        }
        else
        {
            Assert.Equal(FileTree.EncodeEntries(entries), vector.Lists);
        }

        Manifest manifest = Manifest.Decode(vector.Manifest);
        Assert.Null(manifest.Inconsistency());
        Assert.Equal(vector.Root, manifest.Root);
        Assert.Equal(vector.Size, manifest.Size);
        Assert.Equal(vector.Lists.Count, manifest.Lists);
        Assert.Equal(vector.Ledger.First, manifest.FirstLedger);
        Assert.Equal(vector.Ledger.Last, manifest.LastLedger);
        Assert.Equal(vector.Manifest, manifest.Encode());
    }
}
