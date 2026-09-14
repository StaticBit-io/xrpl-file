using Konscious.Security.Cryptography;
using Xunit;

namespace XrplFile.Keys.Tests;

public class Argon2idKdfTests
{
    [Fact]
    public void The_library_computes_standard_Argon2id()
    {
        // RFC 9106 section 5.3. Copied verbatim from the RFC text, not from memory.
        byte[] password = Enumerable.Repeat((byte)0x01, 32).ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x02, 16).ToArray();
        byte[] secret = Enumerable.Repeat((byte)0x03, 8).ToArray();
        byte[] associatedData = Enumerable.Repeat((byte)0x04, 12).ToArray();
        byte[] expected = Convert.FromHexString("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");

        using Argon2id argon = new(password)
        {
            Salt = salt,
            KnownSecret = secret,
            AssociatedData = associatedData,
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };

        Assert.Equal(expected, argon.GetBytes(32));
    }

    [Fact]
    public void The_same_password_and_salt_derive_the_same_bytes()
    {
        Argon2idKdf kdf = new();
        KdfParameters parameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
        byte[] password = "correct horse battery staple"u8.ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x07, 16).ToArray();

        byte[] first = kdf.Derive(password, salt, parameters, 32);
        byte[] second = kdf.Derive(password, salt, parameters, 32);

        Assert.Equal(2, kdf.KdfId);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_different_salt_derives_different_bytes()
    {
        Argon2idKdf kdf = new();
        KdfParameters parameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
        byte[] password = "correct horse battery staple"u8.ToArray();

        byte[] first = kdf.Derive(password, Enumerable.Repeat((byte)0x07, 16).ToArray(), parameters, 32);
        byte[] second = kdf.Derive(password, Enumerable.Repeat((byte)0x08, 16).ToArray(), parameters, 32);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(KdfParameterBounds.MinimumMemoryKib, KdfParameterBounds.MinimumIterations, KdfParameterBounds.MinimumParallelism)]
    [InlineData(
        KdfParameterBounds.MinimumMemoryKibPerLane * KdfParameterBounds.MaximumParallelism,
        KdfParameterBounds.MinimumIterations,
        KdfParameterBounds.MaximumParallelism)]
    [InlineData(2048, KdfParameterBounds.MaximumIterations, KdfParameterBounds.MinimumParallelism)]
    public void Every_corner_of_the_accepted_cost_range_is_one_argon2id_can_actually_run(
        int memoryKib, int iterations, int parallelism)
    {
        // The bounds a record's cost is checked against are only honest if every cost they admit
        // is one the KDF will accept — otherwise a header edited to a value inside the range
        // still reaches Argon2id and still comes back as something other than VaultCorrupt. The
        // memory ceiling is left out on purpose: allocating four gibibytes is not something a
        // unit suite should do, and it is the one bound whose failure would be the allocator's
        // rather than the KDF's.
        Argon2idKdf kdf = new();
        KdfParameters parameters = new(memoryKib, iterations, parallelism);

        byte[] derived = kdf.Derive(
            "correct horse battery staple"u8, Enumerable.Repeat((byte)0x07, 16).ToArray(), parameters, 32);

        Assert.Equal(32, derived.Length);
    }

    [Fact]
    public void The_default_parameters_are_the_measured_ones()
    {
        // Task 0 measured these. A change here is a change to every vault already written,
        // so it must be a deliberate bump of kdfId, not an edit of this constant.
        Assert.Equal(262144, KdfParameters.Default.MemoryKib);
        Assert.Equal(3, KdfParameters.Default.Iterations);
        Assert.Equal(4, KdfParameters.Default.Parallelism);
    }
}
