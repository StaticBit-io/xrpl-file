namespace XrplFile.Keys;

/// <summary>
/// Base45 as defined by RFC 9285.
/// </summary>
/// <remarks>
/// Lifted from the Wallet project (<c>SB.WalletApp.Security</c>), file
/// <c>Encoding/Base45.cs</c>. Unchanged except for the namespace and this header.
///
/// Public rather than internal, unlike the Wallet's own copy: a later task puts
/// <c>ExportModel</c> in a separate assembly, <c>XrplFile.App.Core</c>, and that is the only
/// non-test caller — it needs to reach this encoder to turn a container into a QR code.
///
/// The alphabet fits entirely inside the QR alphanumeric mode (11 bits per two characters), which
/// is why the backup container travels as Base45 rather than raw bytes: both platform scanners
/// hand back a string, and bytes above 0x7F do not survive that round trip.
/// </remarks>
public static class Base45
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private const int Base = 45;
    private const int BaseSquared = Base * Base;

    public static string Encode(ReadOnlySpan<byte> data)
    {
        System.Text.StringBuilder builder = new(((data.Length + 1) / 2) * 3);

        int index = 0;
        while (index + 1 < data.Length)
        {
            int value = (data[index] << 8) | data[index + 1];
            builder.Append(Alphabet[value % Base]);
            builder.Append(Alphabet[(value / Base) % Base]);
            builder.Append(Alphabet[value / BaseSquared]);
            index += 2;
        }

        if (index < data.Length)
        {
            int value = data[index];
            builder.Append(Alphabet[value % Base]);
            builder.Append(Alphabet[value / Base]);
        }

        return builder.ToString();
    }

    public static bool TryDecode(string text, out byte[] data)
    {
        data = [];

        if (text is null || text.Length % 3 == 1)
        {
            return false;
        }

        int[] values = new int[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int position = Alphabet.IndexOf(text[i], StringComparison.Ordinal);
            if (position < 0)
            {
                return false;
            }

            values[i] = position;
        }

        List<byte> output = new(text.Length / 3 * 2 + 1);

        int index = 0;
        while (index + 2 < values.Length)
        {
            int value = values[index] + (values[index + 1] * Base) + (values[index + 2] * BaseSquared);
            if (value > ushort.MaxValue)
            {
                return false;
            }

            output.Add((byte)(value >> 8));
            output.Add((byte)(value & 0xFF));
            index += 3;
        }

        if (index < values.Length)
        {
            int value = values[index] + (values[index + 1] * Base);
            if (value > byte.MaxValue)
            {
                return false;
            }

            output.Add((byte)value);
        }

        data = output.ToArray();
        return true;
    }

    /// <summary>
    /// How many characters <see cref="Encode"/> produces for <paramref name="byteCount"/> bytes:
    /// three per full pair, two for a trailing odd byte.
    /// </summary>
    /// <remarks>
    /// Exposed so a capacity check can be done on a length alone, without building the payload
    /// first. It is exact, not an estimate — <see cref="Encode"/> emits nothing else.
    /// </remarks>
    public static int GetEncodedLength(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        return ((byteCount / 2) * 3) + ((byteCount % 2) * 2);
    }

    /// <summary>
    /// The largest byte count whose <see cref="Encode"/> output still fits into
    /// <paramref name="encodedLength"/> characters — the inverse of
    /// <see cref="GetEncodedLength"/>, rounded down.
    /// </summary>
    /// <remarks>
    /// The remainder matters: three left-over characters are one more full pair, two are one odd
    /// byte, and a single left-over character is not a valid Base45 tail at all and buys nothing.
    /// </remarks>
    public static int GetMaxDecodedLength(int encodedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(encodedLength);

        int bytes = (encodedLength / 3) * 2;

        return (encodedLength % 3) >= 2 ? bytes + 1 : bytes;
    }
}
