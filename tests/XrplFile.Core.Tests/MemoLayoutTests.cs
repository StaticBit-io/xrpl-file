using Xunit;

namespace XrplFile.Tests;

public class MemoLayoutTests
{
    [Fact]
    public void The_budgets_follow_from_the_framing_model()
    {
        Assert.Equal(1000, MemoLayout.ChunkBytes);
        Assert.Equal(982, MemoLayout.ListBytes);
        Assert.Equal(995, MemoLayout.ManifestBytes);
        Assert.Equal(MemoLayout.MaxMemoBytes - MemoLayout.MemoTypeFraming - MemoLayout.MaxMarkerBytes, MemoLayout.ChunkBytes);
    }

    [Theory]
    [InlineData(0, "xf/c/0")]
    [InlineData(7, "xf/c/7")]
    [InlineData(999999999999, "xf/c/999999999999")]
    public void A_chunk_marker_is_the_prefix_and_the_decimal_index(long index, string marker)
    {
        Assert.Equal(MemoLayout.Utf8Hex(marker), MemoLayout.ChunkMarkerHex(index));
        Assert.Equal(index, MemoLayout.ChunkIndexOf(MemoLayout.ChunkMarkerHex(index)));
    }

    [Fact]
    public void A_list_marker_uses_its_own_prefix()
    {
        Assert.Equal(MemoLayout.Utf8Hex("xf/b/3"), MemoLayout.ListMarkerHex(3));
        Assert.Equal(3, MemoLayout.ListIndexOf(MemoLayout.ListMarkerHex(3)));
        Assert.Null(MemoLayout.ChunkIndexOf(MemoLayout.ListMarkerHex(3)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000000000000)]
    public void An_index_that_does_not_fit_the_marker_is_refused(long index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoLayout.ChunkMarkerHex(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoLayout.ListMarkerHex(index));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("78662F632F")]        // "xf/c/" with no digits
    [InlineData("78662F632F3031")]    // "xf/c/01" — a leading zero
    [InlineData("78662F632F2D31")]    // "xf/c/-1"
    [InlineData("78662F632F31 ")]     // trailing garbage in the hex
    [InlineData("76702F632F31")]      // "vp/c/1" — the platform's old marker
    public void Anything_that_is_not_a_chunk_marker_reads_as_null(string? memoTypeHex)
    {
        Assert.Null(MemoLayout.ChunkIndexOf(memoTypeHex));
    }

    [Fact]
    public void The_manifest_marker_is_recognised_exactly()
    {
        Assert.True(MemoLayout.IsManifest(MemoLayout.ManifestMarkerHex));
        Assert.True(MemoLayout.IsManifest(MemoLayout.ManifestMarkerHex.ToLowerInvariant()));
        Assert.False(MemoLayout.IsManifest(MemoLayout.Utf8Hex("xf/m/1")));
        Assert.False(MemoLayout.IsManifest(null));
    }

    [Fact]
    public void Hex_helpers_round_trip_and_emit_upper_case()
    {
        Assert.Equal("6170706C69636174696F6E2F6A736F6E", MemoLayout.JsonFormatHex);
        Assert.Equal("application/json", MemoLayout.FromHexUtf8("6170706c69636174696f6e2f6a736f6e"));
        Assert.Equal("00FF", MemoLayout.Hex([0x00, 0xFF]));
        Assert.Equal(new byte[] { 0x00, 0xFF }, MemoLayout.Bytes("00ff"));
    }
}
