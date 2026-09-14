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

    [Fact]
    public void The_qr_budget_holds_at_its_boundary()
    {
        // The Wallet measured version 40 at correction level H as 1852 alphanumeric characters,
        // which is 1234 container bytes. A container at that size must still encode inside it.
        Assert.True(Base45.GetEncodedLength(1234) <= 1852);
        Assert.True(Base45.GetEncodedLength(1235) > 1852);
    }
}
