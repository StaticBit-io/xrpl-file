using Xunit;

namespace XrplFile.Tests;

public class AmendmentsTests
{
    [Fact]
    public void An_amendment_id_is_the_sha512_half_of_its_name()
    {
        // The id .ci-config/rippled.cfg lists for BatchV1_1.
        Assert.Equal("9F287AED3CDB50A7BD1ACEC24296A30C9B5230CCD136219317AC790E3B884377", Amendments.IdOf(Amendments.Batch));
    }

    [Fact]
    public void The_amendments_object_is_read_by_its_fixed_index()
    {
        Assert.Equal("7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4", Amendments.SingletonIndex);
    }
}
