using System.Text;
using Xunit;

namespace XrplFile.Tests;

public class ManifestTests
{
    private static readonly string Root = new('A', 64);

    private const string FormatUrl = "https://github.com/StaticBit-io/xrpl-file";

    private static Manifest Sample(string? name = "cover.png") =>
        new("image/png", 734512, 1000, Root, 106497159, 106497394, 1, 0, name);

    [Fact]
    public void Encode_writes_the_fields_in_the_documented_order()
    {
        Assert.Equal(
            "{\"format\":\"" + FormatUrl + "\",\"v\":1,\"type\":\"image/png\",\"size\":734512,\"chunk\":1000,\"root\":\"" + Root + "\"," +
            "\"ledgers\":[106497159,106497394],\"blocks\":1,\"lists\":0,\"name\":\"cover.png\"}",
            Sample().Encode());
    }

    [Fact]
    public void A_manifest_without_a_name_has_no_name_member()
    {
        Assert.DoesNotContain("\"name\"", Sample(name: null).Encode(), StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_round_trips_and_upper_cases_the_root()
    {
        string json = Sample().Encode().Replace(Root, Root.ToLowerInvariant(), StringComparison.Ordinal);

        Assert.Equal(Sample(), Manifest.Decode(json));
    }

    [Fact]
    public void A_name_that_does_not_fit_is_cut_to_the_longest_prefix_that_does()
    {
        string encoded = Sample(name: new string('n', 2000)).Encode();

        Assert.True(Encoding.UTF8.GetByteCount(encoded) <= MemoLayout.ManifestBytes);
        Manifest decoded = Manifest.Decode(encoded);
        Assert.NotNull(decoded.Name);
        // Encode() always self-trims to fit, so a second Encode() call can never exceed the
        // budget again — asserting it "does not fit" would be unsatisfiable by any conforming
        // implementation. What "longest prefix that fits" actually means here is that the
        // trim stopped at the exact byte budget: one 'n' character costs exactly one UTF-8
        // byte, so if any more of the original name had fit, encoded would be shorter than
        // ManifestBytes, not equal to it.
        Assert.Equal(MemoLayout.ManifestBytes, Encoding.UTF8.GetByteCount(encoded));
    }

    [Fact]
    public void A_name_is_cut_by_whole_characters_not_bytes()
    {
        string name = string.Concat(Enumerable.Repeat("😀", 400));

        Manifest decoded = Manifest.Decode(Sample(name: name).Encode());

        Assert.All(decoded.Name!, character => Assert.True(char.IsSurrogate(character)));
        Assert.Equal(0, decoded.Name!.Length % 2);
    }

    [Fact]
    public void A_type_too_long_to_fit_even_without_a_name_is_an_error()
    {
        Manifest absurd = Sample() with { Type = new string('t', 1200) };

        Assert.Throws<InvalidOperationException>(() => absurd.Encode());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1,2],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":2,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1,2],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"AA\",\"ledgers\":[1,2],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[2,1],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":\"1\",\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1,2],\"blocks\":1,\"lists\":0}")]
    // A ledger pair that is not numbers: TryGetInt64 throws InvalidOperationException on an
    // element of the wrong kind, and the reader catches FormatException and ArgumentException
    // only, so this used to leave ManifestAsync as a framework exception, not CorruptManifest.
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[\"a\",\"b\"],\"blocks\":1,\"lists\":0}")]
    [InlineData("{\"v\":1,\"type\":\"image/png\",\"size\":1,\"chunk\":1000,\"root\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\",\"ledgers\":[1,null],\"blocks\":1,\"lists\":0}")]
    public void A_manifest_that_is_not_one_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => Manifest.Decode(json));
    }

    private static readonly string CoverHash = new('C', 64);

    [Fact]
    public void A_cover_is_written_after_lists_and_before_the_name()
    {
        Assert.Equal(
            "{\"format\":\"" + FormatUrl + "\",\"v\":1,\"type\":\"image/png\",\"size\":734512,\"chunk\":1000,\"root\":\"" + Root + "\"," +
            "\"ledgers\":[106497159,106497394],\"blocks\":1,\"lists\":0,\"cover\":\"" + CoverHash + "\",\"name\":\"cover.png\"}",
            (Sample() with { Cover = CoverHash }).Encode());
    }

    [Fact]
    public void A_cover_round_trips_upper_cased()
    {
        string json = (Sample() with { Cover = CoverHash }).Encode().Replace(CoverHash, CoverHash.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(Sample() with { Cover = CoverHash }, Manifest.Decode(json));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"AB\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"")]   // 63 hex
    [InlineData("\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"")]  // 65 hex
    public void A_cover_that_is_not_a_hash_reads_as_absent(string value)
    {
        string json = Sample().Encode().Replace(",\"name\":", ",\"cover\":" + value + ",\"name\":", StringComparison.Ordinal);
        Assert.Equal(Sample(), Manifest.Decode(json));
    }

    [Fact]
    public void A_lower_case_cover_is_written_upper_case()
    {
        string encoded = (Sample() with { Cover = CoverHash.ToLowerInvariant() }).Encode();

        Assert.Contains("\"cover\":\"" + CoverHash + "\"", encoded, StringComparison.Ordinal);
        Assert.Equal(CoverHash, Manifest.Decode(encoded).Cover);
    }

    [Fact]
    public void A_cover_with_a_character_that_is_not_hex_reads_as_absent()
    {
        string json = Sample().Encode().Replace(",\"name\":", ",\"cover\":\"" + new string('C', 63) + "Z\",\"name\":", StringComparison.Ordinal);
        Assert.Null(Manifest.Decode(json).Cover);
    }

    [Fact]
    public void Members_this_version_does_not_define_are_ignored()
    {
        string json = Sample().Encode().Replace(",\"name\":", ",\"future\":{\"tags\":[\"a\",1],\"on\":true},\"x\":null,\"name\":", StringComparison.Ordinal);
        Assert.Equal(Sample(), Manifest.Decode(json));
    }

    [Fact]
    public void The_name_is_shortened_after_the_cover_is_placed()
    {
        string encoded = (Sample(name: new string('n', 2000)) with { Cover = CoverHash }).Encode();
        Assert.Equal(MemoLayout.ManifestBytes, Encoding.UTF8.GetByteCount(encoded));
        Assert.Equal(CoverHash, Manifest.Decode(encoded).Cover);
    }

    [Fact]
    public void A_cover_that_is_not_a_hash_is_never_written()
    {
        Assert.Throws<InvalidOperationException>(() => (Sample() with { Cover = "AB" }).Encode());
    }

    [Fact]
    public void The_format_is_the_repository_url_of_the_specification()
    {
        Assert.Equal(FormatUrl, Manifest.Format);
    }

    [Fact]
    public void The_format_is_the_first_member_and_costs_53_bytes()
    {
        string encoded = Sample().Encode();
        string withoutFormat = encoded.Replace("\"format\":\"" + FormatUrl + "\",", string.Empty, StringComparison.Ordinal);

        Assert.StartsWith("{\"format\":\"" + FormatUrl + "\",\"v\":1,", encoded, StringComparison.Ordinal);
        Assert.StartsWith("{\"v\":1,", withoutFormat, StringComparison.Ordinal);
        Assert.Equal(53, Encoding.UTF8.GetByteCount(encoded) - Encoding.UTF8.GetByteCount(withoutFormat));
    }

    [Fact]
    public void A_manifest_without_a_format_is_the_one_written_before_the_member_existed()
    {
        string json = Sample().Encode().Replace("\"format\":\"" + FormatUrl + "\",", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("\"format\"", json, StringComparison.Ordinal);
        Assert.Equal(Sample(), Manifest.Decode(json));
    }

    [Theory]
    [InlineData("\"https://example.com/other\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"https://github.com/StaticBit-io/xrpl-file\"]")]
    [InlineData("{}")]
    [InlineData("{\"url\":\"https://github.com/StaticBit-io/xrpl-file\"}")]
    public void A_reader_does_not_look_at_the_format_whatever_it_holds(string value)
    {
        string json = Sample().Encode().Replace("\"" + FormatUrl + "\"", value, StringComparison.Ordinal);

        Assert.Contains("\"format\":" + value, json, StringComparison.Ordinal);
        Assert.Equal(Sample(), Manifest.Decode(json));
    }

    [Fact]
    public void A_format_that_comes_last_or_twice_still_reads()
    {
        string last = Sample().Encode().Replace("\"format\":\"" + FormatUrl + "\",", string.Empty, StringComparison.Ordinal).Replace("}", ",\"format\":7}", StringComparison.Ordinal);

        Assert.Equal(Sample(), Manifest.Decode(last));
    }

    [Fact]
    public void The_name_is_shortened_after_the_format_and_the_cover_are_placed()
    {
        string encoded = (Sample(name: new string('n', 2000)) with { Cover = CoverHash }).Encode();
        Manifest decoded = Manifest.Decode(encoded);

        Assert.Equal(MemoLayout.ManifestBytes, Encoding.UTF8.GetByteCount(encoded));
        Assert.StartsWith("{\"format\":\"" + FormatUrl + "\",", encoded, StringComparison.Ordinal);
        Assert.Contains("\"cover\":\"" + CoverHash + "\"", encoded, StringComparison.Ordinal);
        Assert.Equal(CoverHash, decoded.Cover);

        // 995 - the fixed members with format and cover and no name - the ,"name":"" frame.
        string withoutName = (Sample(name: null) with { Cover = CoverHash }).Encode();
        int room = MemoLayout.ManifestBytes - Encoding.UTF8.GetByteCount(withoutName) - ",\"name\":\"\"".Length;
        Assert.Equal(room, decoded.Name!.Length);
    }

    [Fact]
    public void A_consistent_manifest_has_no_inconsistency()
    {
        Assert.Null(Sample().Inconsistency());
        Assert.Null((Sample() with { Size = 2500500, Blocks = 3, Lists = 1 }).Inconsistency());
    }

    [Theory]
    [InlineData(734512, 1000, 2, 1)]       // 735 chunks is one block, not two
    [InlineData(2500500, 1000, 3, 0)]      // three blocks need a list
    [InlineData(734512, 1000, 1, 1)]       // one block has no list
    [InlineData(734512, 1001, 1, 0)]       // a chunk larger than the budget
    [InlineData(734512, 0, 1, 0)]
    [InlineData(-1, 1000, 1, 0)]
    // A size past chunk * int.MaxValue: ChunkCount's own arithmetic overflows to a negative count,
    // which used to make BlockCount answer 0 and let "blocks":0 with a nonzero "lists" through
    // every check here -- and the reader then tried to allocate long.MaxValue bytes.
    [InlineData(long.MaxValue, 1000, 0, 1)]
    // A size whose block count overflows Int32 on the way out of BlockCount: the cast wraps to
    // int.MinValue, and a manifest naming that number agreed with itself.
    [InlineData(2199023255552000, 1000, -2147483648, 1)]
    // One chunk past what a chunk index can name, with a block count that does agree with it --
    // consistent by every other rule here, and refused by the bound alone. The browser reader
    // refuses the same manifest for the same reason.
    [InlineData(2147483648000, 1000, 2097152, 1)]
    public void Counts_that_disagree_are_named(long size, int chunk, int blocks, int lists)
    {
        Manifest manifest = Sample() with { Size = size, Chunk = chunk, Blocks = blocks, Lists = lists };

        Assert.NotNull(manifest.Inconsistency());
    }

    [Fact]
    public void The_largest_addressable_size_is_still_consistent()
    {
        long addressable = 1000L * int.MaxValue;

        Manifest manifest = Sample() with { Size = addressable, Chunk = 1000, Blocks = 2097152, Lists = 1 };

        Assert.Null(manifest.Inconsistency());
    }
}
