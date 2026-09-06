using System.Security.Cryptography;
using System.Text;

namespace XrplFile;

/// <summary>
/// A deterministic byte stream both test suites regenerate instead of storing megabytes of
/// fixtures: block k is SHA256(seed ‖ k as a big-endian uint32), blocks concatenated, cut to size.
/// </summary>
public static class VectorStream
{
    public static byte[] Bytes(string seed, long size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

        byte[] seedBytes = Encoding.UTF8.GetBytes(seed);
        byte[] input = new byte[seedBytes.Length + 4];
        seedBytes.CopyTo(input, 0);

        byte[] output = new byte[size];
        long at = 0;
        uint counter = 0;

        while (at < size)
        {
            input[^4] = (byte)(counter >> 24);
            input[^3] = (byte)(counter >> 16);
            input[^2] = (byte)(counter >> 8);
            input[^1] = (byte)counter;

            byte[] block = SHA256.HashData(input);
            int take = (int)Math.Min(block.Length, size - at);
            Array.Copy(block, 0, output, at, take);

            at += take;
            counter++;
        }

        return output;
    }
}
