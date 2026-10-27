using Konscious.Security.Cryptography;
using Xunit;

namespace XrplFile.Keys.Tests;

public class Argon2idKdfTests
{
    [Theory]
    [InlineData(16_384, 1, 1, "8a06458623ef3ebec7e6704bf05b32eb942ca25b0aeae0be0ded08e750685169")]
    [InlineData(16_383, 1, 1, "d972ede7cab4bf85576585f6595a7f6b4f21c5f130a2e6c8c1c5acfdabb74e93")]
    [InlineData(16_384, 1, 4, "beff63dcb56d703ac0e56cc4f095fdfc371d0fbd4cf22bb1499be2b48798fc0f")]
    public void A_derivation_at_and_below_the_collection_threshold_is_standard_argon2id(int memoryKib, int iterations, int parallelism, string expectedHex)
    {
        byte[] derived = new Argon2idKdf().Derive(
            "correct horse battery staple"u8, Enumerable.Repeat((byte)0x07, 16).ToArray(), new KdfParameters(memoryKib, iterations, parallelism), 32);

        Assert.Equal(Convert.FromHexString(expectedHex), derived);
    }

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

    [Theory]
    [InlineData(0, 3, 4)]
    [InlineData(7, 3, 4)]
    [InlineData(65536, 0, 4)]
    [InlineData(65536, 3, 0)]
    [InlineData(2_000_000_000, 3, 4)]
    [InlineData(65536, 2_000_000_000, 4)]
    [InlineData(65536, 3, 256)]
    [InlineData(8, 1, 3)]
    public void A_cost_no_reader_would_accept_cannot_be_held_at_all(int memoryKib, int iterations, int parallelism)
    {
        // The same eight costs VaultRecordCodecTests watches a reader call damage. A vault written
        // at one of them answers VaultCorrupt for ever, with its keys inside and no password able
        // to reach them — so the place to refuse it is where it is named, before a file exists.
        // These are exactly the values a constructor used to take happily.
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfParameters(memoryKib, iterations, parallelism));
    }

    [Fact]
    public void A_cost_cannot_be_edited_out_of_range_after_it_is_built()
    {
        // The properties have no setters, so `with` cannot reach past the constructor — a second
        // doorway to the same unopenable file. This is a compile-time guarantee; the assertion
        // here is that the value object really is closed, not merely validated on the way in.
        KdfParameters parameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

        Assert.DoesNotContain(typeof(KdfParameters).GetProperties(), property => property.CanWrite);
        Assert.Equal(new KdfParameters(MemoryKib: 8, Iterations: 1, Parallelism: 1), parameters);
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
