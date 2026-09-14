using Xunit;

namespace XrplFile.Keys.Tests;

public class Base45Tests
{
    [Theory]
    // RFC 9285 section 4.3 ("Encoding examples"). Copied verbatim from
    // https://www.rfc-editor.org/rfc/rfc9285.txt, not from memory.
    [InlineData("AB", "BB8")]
    [InlineData("Hello!!", "%69 VD92EX0")]
    [InlineData("base-45", "UJCLQE7W581")]
    public void The_encoding_matches_the_rfc(string text, string expected)
    {
        Assert.Equal(expected, Base45.Encode(System.Text.Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Every_byte_value_survives_a_round_trip()
    {
        byte[] all = new byte[256];
        for (int i = 0; i < all.Length; i++) all[i] = (byte)i;

        Assert.True(Base45.TryDecode(Base45.Encode(all), out byte[] back));
        Assert.Equal(all, back);
    }

    [Fact]
    public void A_string_outside_the_alphabet_is_refused_rather_than_throwing()
    {
        Assert.False(Base45.TryDecode("not base45!", out byte[] _));
    }

    [Fact]
    public void An_empty_input_round_trips()
    {
        Assert.True(Base45.TryDecode(Base45.Encode([]), out byte[] back));
        Assert.Empty(back);
    }

    // TryDecode is the only path untrusted text reaches: a QR code scanned from somewhere else, or
    // a backup file somebody edited. Every branch that refuses one has to be exercised, or a
    // change that turned a refusal into a throw -- or into a silent wrong answer -- would pass.

    [Theory]
    [InlineData("A")]
    [InlineData("BB8A")]
    [InlineData("BB8BB8A")]
    public void A_length_that_leaves_one_character_over_is_refused(string text)
    {
        // Base45 emits three characters per byte pair and two for a trailing odd byte, so a length
        // of 3n+1 cannot have been produced by any input at all.
        Assert.Equal(1, text.Length % 3);
        Assert.False(Base45.TryDecode(text, out byte[] _));
    }

    [Fact]
    public void A_null_input_is_refused_rather_than_throwing()
    {
        Assert.False(Base45.TryDecode(null!, out byte[] _));
    }

    [Fact]
    public void A_triple_that_would_decode_past_two_bytes_is_refused()
    {
        // ':' is the last character of the alphabet, value 44, so ":::" is 44 + 44*45 + 44*2025 =
        // 91,124 -- well past the 65,535 two bytes can hold. Every character is in the alphabet and
        // the length is legal, so this is the one check standing between a crafted code and a
        // wrapped-around byte pair.
        Assert.False(Base45.TryDecode(":::", out byte[] _));
    }

    [Fact]
    public void A_trailing_pair_that_would_decode_past_one_byte_is_refused()
    {
        // The same overflow on the odd-byte tail: 44 + 44*45 = 2,024, and one byte holds 255.
        Assert.False(Base45.TryDecode("::", out byte[] _));
        Assert.False(Base45.TryDecode("BB8::", out byte[] _));
    }

    [Fact]
    public void The_largest_values_that_do_fit_are_still_accepted()
    {
        // The other side of both checks, so a refusal that was simply made stricter than the format
        // would be caught here: 0xFFFF encodes as three characters and 0xFF as two.
        Assert.True(Base45.TryDecode(Base45.Encode([0xFF, 0xFF]), out byte[] pair));
        Assert.Equal<byte[]>([0xFF, 0xFF], pair);

        Assert.True(Base45.TryDecode(Base45.Encode([0xFF]), out byte[] single));
        Assert.Equal<byte[]>([0xFF], single);
    }

    [Fact]
    public void The_qr_budget_holds_at_its_boundary()
    {
        // The Wallet measured version 40 at correction level H as 1852 alphanumeric characters,
        // which is 1234 container bytes. A container at that size must still encode inside it.
        Assert.True(Base45.GetEncodedLength(1234) <= 1852);
        Assert.True(Base45.GetEncodedLength(1235) > 1852);
    }
}
