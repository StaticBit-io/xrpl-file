using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    internal static byte[] Canonical(VaultRecord record)
    {
        using MemoryStream buffer = new();

        void Text(string value) => buffer.Write(Encoding.UTF8.GetBytes(value));
        void Raw(byte[]? value) { if (value is not null) buffer.Write(value); }

        Text("v=");        Text(record.V.ToString(CultureInfo.InvariantCulture));
        Text(";kdfId=");   Text(record.KdfId.ToString(CultureInfo.InvariantCulture));
        Text(";m=");       Text(record.KdfParams.MemoryKib.ToString(CultureInfo.InvariantCulture));
        Text(";t=");       Text(record.KdfParams.Iterations.ToString(CultureInfo.InvariantCulture));
        Text(";p=");       Text(record.KdfParams.Parallelism.ToString(CultureInfo.InvariantCulture));
        Text(";salt=");    Raw(record.Salt);
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
            MemoryKib = record.KdfParams.MemoryKib,
            Iterations = record.KdfParams.Iterations,
            Parallelism = record.KdfParams.Parallelism,
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

        if (dto.Salt is null || dto.Dek is null || dto.Mac is null || dto.Entries is null)
        {
            throw new VaultException(VaultError.Corrupt, "The vault record is missing a required field.");
        }

        try
        {
            VaultRecord record = new()
            {
                V = dto.V,
                KdfId = dto.KdfId,
                KdfParams = new KdfParameters(dto.MemoryKib, dto.Iterations, dto.Parallelism),
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

    private sealed class RecordDto
    {
        public int V { get; set; }
        public int KdfId { get; set; }
        public int MemoryKib { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public string? Salt { get; set; }
        public string? Dek { get; set; }
        public string? DeviceDek { get; set; }
        public string? Mac { get; set; }
        public Dictionary<string, string?>? Entries { get; set; }
    }
}
