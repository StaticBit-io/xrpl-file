using Xunit;

namespace XrplFile.Tests;

public class BatchCandidateTests
{
    private static BatchCandidate Candidate(string blob) =>
        new(0, true, "rP", 500, 1020, 165, [0, 1], new SignedTransaction(blob, "H0"));

    [Theory]
    [InlineData("1200002280000000")]
    [InlineData("")]
    public void An_upper_case_blob_is_sent_as_signed(string blob)
    {
        Assert.Equal(blob, Candidate(blob).TxBlob);
    }

    [Theory]
    [InlineData("1200002a80000000")]
    [InlineData("120")]
    [InlineData("not hex")]
    public void A_blob_that_is_not_upper_case_hex_is_sent_as_signed_too(string blob)
    {
        Assert.Equal(blob, Candidate(blob).TxBlob);
    }

    [Fact]
    public void The_hash_is_the_signers()
    {
        Assert.Equal("H0", Candidate("00").Hash);
    }
}
