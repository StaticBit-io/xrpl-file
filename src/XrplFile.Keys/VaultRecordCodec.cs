using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrplFile.Keys;

/// <summary>
/// The record's bytes, in both directions, and the canonical form its integrity code covers.
/// </summary>
/// <remarks>
/// The canonical form is pinned by a fixed vector and must not change outside a bump of
/// <c>V</c>: it is what the integrity code is computed over, so an encoding that drifts stops
/// every installed build from opening records any other build wrote.
///
/// Known debt, to pay at the next bump of <c>V</c>: binary fields are joined by separators with
/// no declared length, so distinct field sets producing identical canonical bytes exist in
/// principle. The application cannot produce such a pair — references are GUIDs of fixed shape —
/// and exploiting it needs write access to the user's own files.
/// </remarks>
internal static class VaultRecordCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The header: everything that describes how the key wraps were made, and nothing that was
    /// made with them. It is the associated data of both wraps, which is what stops an attacker
    /// lowering the KDF cost recorded in the file and having the wrap still open — a silent
    /// downgrade of the only defence a single password leaves standing.
    /// </summary>
    /// <remarks>
    /// It is the leading run of <see cref="Canonical"/>, written by the same code, so the two
    /// cannot drift into a file whose integrity code and whose wraps disagree about the header.
    /// </remarks>
    internal static byte[] Header(VaultRecord record)
    {
        using MemoryStream buffer = new();
        WriteHeader(buffer, record);
        return buffer.ToArray();
    }

    private static void WriteHeader(Stream buffer, VaultRecord record)
    {
        void Text(string value) => buffer.Write(Encoding.UTF8.GetBytes(value));
        void Raw(byte[]? value) { if (value is not null) buffer.Write(value); }

        Text("v=");        Text(record.V.ToString(CultureInfo.InvariantCulture));
        Text(";kdfId=");   Text(record.KdfId.ToString(CultureInfo.InvariantCulture));
        Text(";m=");       Text(record.KdfParams.MemoryKib.ToString(CultureInfo.InvariantCulture));
        Text(";t=");       Text(record.KdfParams.Iterations.ToString(CultureInfo.InvariantCulture));
        Text(";p=");       Text(record.KdfParams.Parallelism.ToString(CultureInfo.InvariantCulture));
        Text(";salt=");    Raw(record.Salt);
    }

    internal static byte[] Canonical(VaultRecord record)
    {
        using MemoryStream buffer = new();

        void Text(string value) => buffer.Write(Encoding.UTF8.GetBytes(value));
        void Raw(byte[]? value) { if (value is not null) buffer.Write(value); }

        WriteHeader(buffer, record);

        Text(";dek=");     Raw(record.Dek);
        Text(";device=");  Raw(record.DeviceDek);
        Text(";entries=");

        foreach (KeyValuePair<string, byte[]> entry in record.Entries)
        {
            Text(entry.Key);
            Text("=");
            Raw(entry.Value);
            Text(";");
        }

        return buffer.ToArray();
    }

    internal static byte[] Mac(ReadOnlySpan<byte> integrityKey, VaultRecord record) =>
        HMACSHA256.HashData(integrityKey, Canonical(record));

    internal static byte[] Serialise(VaultRecord record)
    {
        Dictionary<string, string?> entries = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, byte[]> entry in record.Entries)
        {
            entries[entry.Key] = Convert.ToBase64String(entry.Value);
        }

        RecordDto dto = new()
        {
            V = record.V,
            KdfId = record.KdfId,
            KdfParams = new KdfParamsDto
            {
                MemoryKib = record.KdfParams.MemoryKib,
                Iterations = record.KdfParams.Iterations,
                Parallelism = record.KdfParams.Parallelism,
            },
            Salt = Convert.ToBase64String(record.Salt),
            Dek = Convert.ToBase64String(record.Dek),
            DeviceDek = record.DeviceDek is null ? null : Convert.ToBase64String(record.DeviceDek),
            Mac = Convert.ToBase64String(record.Mac),
            Entries = entries,
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, SerializerOptions);
    }

    internal static VaultRecord Parse(ReadOnlySpan<byte> bytes)
    {
        RecordDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RecordDto>(bytes, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new VaultException(VaultError.Corrupt, "The vault record is not valid JSON.", ex);
        }

        if (dto is null)
        {
            throw new VaultException(VaultError.Corrupt, "The vault record is empty.");
        }

        if (dto.Salt is null || dto.Dek is null || dto.Mac is null || dto.Entries is null || dto.KdfParams is null)
        {
            throw new VaultException(VaultError.Corrupt, "The vault record is missing a required field.");
        }

        KdfParameters parameters = new(dto.KdfParams.MemoryKib, dto.KdfParams.Iterations, dto.KdfParams.Parallelism);
        if (!KdfParameterBounds.CouldHaveBeenWritten(parameters))
        {
            // Refused here rather than by whoever derives from it. The record's header is the
            // associated data of both wraps, so a tampered cost is caught — but only once the
            // derivation it describes has been paid for, and a cost outside these bounds is
            // chosen precisely so that it cannot be. See KdfParameterBounds for the figures.
            throw new VaultException(
                VaultError.Corrupt, "The vault record's KDF cost is not one any build could have written.");
        }

        try
        {
            VaultRecord record = new()
            {
                V = dto.V,
                KdfId = dto.KdfId,
                KdfParams = parameters,
                Salt = Convert.FromBase64String(dto.Salt),
                Dek = Convert.FromBase64String(dto.Dek),
                DeviceDek = dto.DeviceDek is null ? null : Convert.FromBase64String(dto.DeviceDek),
                Mac = Convert.FromBase64String(dto.Mac),
            };

            foreach (KeyValuePair<string, string?> entry in dto.Entries)
            {
                if (entry.Value is null)
                {
                    throw new VaultException(VaultError.Corrupt, "The vault record has an entry with no value.");
                }

                record.Entries[entry.Key] = Convert.FromBase64String(entry.Value);
            }

            return record;
        }
        catch (FormatException ex)
        {
            throw new VaultException(VaultError.Corrupt, "The vault record has a malformed binary field.", ex);
        }
    }

    /// <summary>
    /// The on-disk shape, per the design's section 5: lowercase properties, <c>kdfParams</c>
    /// nested. Kept distinct from <see cref="VaultRecord"/> deliberately — this type's shape is a
    /// wire contract another language may one day read, while <see cref="VaultRecord"/> is free
    /// to change as the library's internals do.
    /// </summary>
    private sealed class RecordDto
    {
        [JsonPropertyName("v")]
        public int V { get; set; }

        [JsonPropertyName("kdfId")]
        public int KdfId { get; set; }

        [JsonPropertyName("kdfParams")]
        public KdfParamsDto? KdfParams { get; set; }

        [JsonPropertyName("salt")]
        public string? Salt { get; set; }

        [JsonPropertyName("dek")]
        public string? Dek { get; set; }

        [JsonPropertyName("deviceDek")]
        public string? DeviceDek { get; set; }

        [JsonPropertyName("mac")]
        public string? Mac { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, string?>? Entries { get; set; }
    }

    private sealed class KdfParamsDto
    {
        [JsonPropertyName("memoryKib")]
        public int MemoryKib { get; set; }

        [JsonPropertyName("iterations")]
        public int Iterations { get; set; }

        [JsonPropertyName("parallelism")]
        public int Parallelism { get; set; }
    }
}
