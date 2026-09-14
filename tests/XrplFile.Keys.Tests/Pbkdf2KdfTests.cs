using Xunit;

namespace XrplFile.Keys.Tests;

public class Pbkdf2KdfTests
{
    [Fact]
    public void The_derivation_matches_the_published_vector()
    {
        // RFC 7914 section 11: PBKDF2-HMAC-SHA256, P = "passwd", S = "salt", c = 1, dkLen = 64.
        // Copied verbatim from https://www.rfc-editor.org/rfc/rfc7914.txt, not from memory.
        byte[] expected = Convert.FromHexString(
            "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dac" +
            "bc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a1" +
            "9783");

        byte[] actual = Pbkdf2Kdf.Derive("passwd"u8, "salt"u8, iterations: 1, outputLength: 64);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_same_inputs_derive_the_same_bytes()
    {
        byte[] password = "correct horse battery staple"u8.ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x07, 16).ToArray();

        Assert.Equal(
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32),
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32));
    }

    [Fact]
    public void A_different_iteration_count_derives_different_bytes()
    {
        byte[] password = "correct horse battery staple"u8.ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x07, 16).ToArray();

        Assert.NotEqual(
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32),
            Pbkdf2Kdf.Derive(password, salt, iterations: 1001, outputLength: 32));
    }

    [Fact]
    public void The_default_iteration_count_is_the_measured_one_and_inside_the_shared_range()
    {
        // Task 0 measured this: 1,000,000 at 122-126 ms on this machine, the top of the range the
        // shared format accepts. It travels inside every container's header, and the Wallet refuses
        // a count outside the range before it runs the derivation.
        Assert.Equal(1_000_000, Pbkdf2Kdf.DefaultIterations);
        Assert.InRange(Pbkdf2Kdf.DefaultIterations, Pbkdf2Kdf.IterationsFloor, Pbkdf2Kdf.IterationsCeiling);
    }
}
