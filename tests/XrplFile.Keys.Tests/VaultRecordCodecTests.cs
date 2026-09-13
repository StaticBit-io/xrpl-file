using System.Text;
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
}
