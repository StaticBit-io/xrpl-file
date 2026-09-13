namespace XrplFile.Keys;

/// <summary>
/// The vault file, decoded to a mutable DTO. No behaviour and no validation live here: those
/// belong to <see cref="VaultRecordCodec"/> and to the vault itself.
/// </summary>
internal sealed class VaultRecord
{
    /// <summary>Format version. A caller other than <see cref="VaultRecordCodec"/> decides what to do with it.</summary>
    public int V { get; set; }

    /// <summary>Which <see cref="IPasswordKdf"/> derived <see cref="Dek"/> and <see cref="DeviceDek"/> from the password.</summary>
    public int KdfId { get; set; }

    /// <summary>The cost the KDF was run at. Carried in the record so a future recalibration still opens it.</summary>
    public required KdfParameters KdfParams { get; set; }

    /// <summary>Salt passed to the KDF.</summary>
    public required byte[] Salt { get; set; }

    /// <summary>The data-encrypting key, wrapped under the password-derived key.</summary>
    public required byte[] Dek { get; set; }

    /// <summary>The data-encrypting key wrapped a second way, for a device that unlocks without the password. Absent when no such device is set up.</summary>
    public byte[]? DeviceDek { get; set; }

    /// <summary>The integrity code over <see cref="VaultRecordCodec.Canonical"/> of this same record.</summary>
    public required byte[] Mac { get; set; }

    /// <summary>Reference to encrypted-entry-blob. Ordinal-ordered so the canonical form needs no sort of its own.</summary>
    public SortedDictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);
}
