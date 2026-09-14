using System.Text;
using System.Text.Json;
using Xunit;

namespace XrplFile.Keys.Tests;

public class VaultRecordCodecTests
{
    private static VaultRecord Sample()
    {
        VaultRecord record = new()
        {
            V = 1,
            KdfId = 2,
            KdfParams = new KdfParameters(MemoryKib: 65536, Iterations: 3, Parallelism: 4),
            Salt = Enumerable.Repeat((byte)0x11, 16).ToArray(),
            Dek = Enumerable.Repeat((byte)0x22, 60).ToArray(),
            DeviceDek = null,
            Mac = new byte[32],
        };
        record.Entries["b"] = Enumerable.Repeat((byte)0x33, 40).ToArray();
        record.Entries["a"] = Enumerable.Repeat((byte)0x44, 40).ToArray();
        return record;
    }

    [Fact]
    public void The_canonical_form_is_this_exact_byte_string()
    {
        // Pinned deliberately. The integrity code is computed over these bytes, so a change to the
        // encoding stops every installed build from opening records written by any other build.
        // Changing this expectation is only correct alongside a bump of VaultRecord.V.
        byte[] canonical = VaultRecordCodec.Canonical(Sample());

        string expected =
            "v=1;kdfId=2;m=65536;t=3;p=4;salt=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x11, 16).ToArray())
            + ";dek=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x22, 60).ToArray())
            + ";device=;entries="
            + "a=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x44, 40).ToArray()) + ";"
            + "b=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x33, 40).ToArray()) + ";";

        Assert.Equal(Encoding.Latin1.GetBytes(expected), canonical);
    }

    [Fact]
    public void The_canonical_form_pins_a_non_null_device_dek()
    {
        // Pinned deliberately, alongside the null-device vector above. Every other test in this
        // file uses a sample with DeviceDek == null, so nothing would fail if a future edit
        // dropped the device-wrap bytes from the canonical form or moved them to another
        // position — a vault with device unlock configured would then have a mac computed over
        // the wrong bytes.
        VaultRecord record = Sample();
        record.DeviceDek = Enumerable.Repeat((byte)0x66, 60).ToArray();

        byte[] canonical = VaultRecordCodec.Canonical(record);

        string expected =
            "v=1;kdfId=2;m=65536;t=3;p=4;salt=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x11, 16).ToArray())
            + ";dek=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x22, 60).ToArray())
            + ";device=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x66, 60).ToArray())
            + ";entries="
            + "a=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x44, 40).ToArray()) + ";"
            + "b=" + Encoding.Latin1.GetString(Enumerable.Repeat((byte)0x33, 40).ToArray()) + ";";

        Assert.Equal(Encoding.Latin1.GetBytes(expected), canonical);
    }

    [Fact]
    public void Entries_are_ordered_by_reference_not_by_insertion()
    {
        VaultRecord inserted = Sample();

        VaultRecord reversed = Sample();
        reversed.Entries.Clear();
        reversed.Entries["a"] = Enumerable.Repeat((byte)0x44, 40).ToArray();
        reversed.Entries["b"] = Enumerable.Repeat((byte)0x33, 40).ToArray();

        Assert.Equal(VaultRecordCodec.Canonical(inserted), VaultRecordCodec.Canonical(reversed));
    }

    [Fact]
    public void The_mac_changes_when_an_entry_is_removed()
    {
        byte[] key = Enumerable.Repeat((byte)0x55, 32).ToArray();
        VaultRecord full = Sample();
        byte[] before = VaultRecordCodec.Mac(key, full);

        full.Entries.Remove("a");
        byte[] after = VaultRecordCodec.Mac(key, full);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_mac_changes_when_the_kdf_parameters_are_lowered()
    {
        byte[] key = Enumerable.Repeat((byte)0x55, 32).ToArray();
        VaultRecord record = Sample();
        byte[] before = VaultRecordCodec.Mac(key, record);

        record.KdfParams = new KdfParameters(MemoryKib: 8, Iterations: 1, Parallelism: 1);

        Assert.NotEqual(before, VaultRecordCodec.Mac(key, record));
    }

    [Fact]
    public void A_record_survives_a_round_trip()
    {
        VaultRecord original = Sample();

        VaultRecord parsed = VaultRecordCodec.Parse(VaultRecordCodec.Serialise(original));

        Assert.Equal(VaultRecordCodec.Canonical(original), VaultRecordCodec.Canonical(parsed));
    }

    [Fact]
    public void Bytes_that_are_not_a_record_are_refused_as_corrupt()
    {
        VaultException error = Assert.Throws<VaultException>(
            () => VaultRecordCodec.Parse("not json at all"u8));

        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public void Parse_throws_corrupt_when_a_required_field_is_missing()
    {
        // Valid JSON, but "salt" is entirely absent (not merely null).
        const string json = """
            {
              "v": 1,
              "kdfId": 2,
              "kdfParams": { "memoryKib": 65536, "iterations": 3, "parallelism": 4 },
              "dek": "AAAA",
              "mac": "AAAA",
              "entries": {}
            }
            """;

        VaultException error = Assert.Throws<VaultException>(
            () => VaultRecordCodec.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(VaultError.Corrupt, error.Error);
        Assert.DoesNotContain(json, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_corrupt_when_salt_is_not_base64()
    {
        const string json = """
            {
              "v": 1,
              "kdfId": 2,
              "kdfParams": { "memoryKib": 65536, "iterations": 3, "parallelism": 4 },
              "salt": "not-valid-base64!!",
              "dek": "AAAA",
              "mac": "AAAA",
              "entries": {}
            }
            """;

        VaultException error = Assert.Throws<VaultException>(
            () => VaultRecordCodec.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(VaultError.Corrupt, error.Error);
        Assert.DoesNotContain("not-valid-base64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialise_writes_the_spec_wire_shape()
    {
        // Section 5 of the design draws the on-disk record as lowercase properties with a nested
        // "kdfParams" object. Pinning the shape here is what stops a future serializer-options
        // edit from silently drifting away from it again.
        byte[] bytes = VaultRecordCodec.Serialise(Sample());

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(2, root.GetProperty("kdfId").GetInt32());

        JsonElement kdfParams = root.GetProperty("kdfParams");
        Assert.Equal(65536, kdfParams.GetProperty("memoryKib").GetInt32());
        Assert.Equal(3, kdfParams.GetProperty("iterations").GetInt32());
        Assert.Equal(4, kdfParams.GetProperty("parallelism").GetInt32());

        Assert.Equal(JsonValueKind.String, root.GetProperty("salt").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("dek").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("deviceDek").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("mac").ValueKind);

        JsonElement entries = root.GetProperty("entries");
        Assert.Equal(JsonValueKind.Object, entries.ValueKind);
        Assert.True(entries.TryGetProperty("a", out _));
        Assert.True(entries.TryGetProperty("b", out _));

        Assert.False(root.TryGetProperty("V", out _));
        Assert.False(root.TryGetProperty("KdfId", out _));
        Assert.False(root.TryGetProperty("MemoryKib", out _));
        Assert.False(root.TryGetProperty("DeviceDek", out _));
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
    public void A_kdf_cost_outside_what_this_format_can_carry_is_refused_as_corrupt(
        int memoryKib, int iterations, int parallelism)
    {
        // Unchecked, these reach Argon2id: the low end comes back as a raw
        // ArgumentOutOfRangeException out of a method whose contract is an enumeration, and the
        // high end tries to allocate the machine out of existence — both of them before the
        // header binding that would have rejected the tampering has had any chance to speak. The
        // export container already calls a cost like this corruption; this is the vault saying
        // the same word.
        VaultRecord record = Sample();
        record.KdfParams = new KdfParameters(memoryKib, iterations, parallelism);

        VaultException error = Assert.Throws<VaultException>(
            () => VaultRecordCodec.Parse(VaultRecordCodec.Serialise(record)));

        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Theory]
    [InlineData(262144, 3, 4)]
    [InlineData(8, 1, 1)]
    [InlineData(65536, 2, 1)]
    [InlineData(4_194_304, 1024, 255)]
    public void Every_cost_a_legitimate_build_could_have_written_still_parses(
        int memoryKib, int iterations, int parallelism)
    {
        // The measured production triple, the two the suite writes, and the widest corner of the
        // accepted range. A bound that refused any of these would be refusing a vault rather than
        // an attack — the failure that costs a user their keys outright.
        VaultRecord record = Sample();
        record.KdfParams = new KdfParameters(memoryKib, iterations, parallelism);

        VaultRecord parsed = VaultRecordCodec.Parse(VaultRecordCodec.Serialise(record));

        Assert.Equal(record.KdfParams, parsed.KdfParams);
    }

    [Fact]
    public void An_absent_device_dek_round_trips_as_null_not_an_empty_array()
    {
        // An empty array would change the canonical bytes (and therefore the mac) relative to
        // null, since VaultRecordCodec.Canonical treats "no device wrap configured" as nothing
        // written at all. Null must stay null across the wire.
        VaultRecord original = Sample();
        Assert.Null(original.DeviceDek);

        VaultRecord parsed = VaultRecordCodec.Parse(VaultRecordCodec.Serialise(original));

        Assert.Null(parsed.DeviceDek);
    }
}
