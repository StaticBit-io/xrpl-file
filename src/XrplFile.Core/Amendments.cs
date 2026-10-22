using System.Security.Cryptography;
using System.Text;

namespace XrplFile;

/// <summary>The amendments this library asks about (spec B14).</summary>
public static class Amendments
{
    /// <summary>The amendment the Batch path needs.</summary>
    public const string Batch = "BatchV1_1";

    /// <summary>The fixed index of the ledger's singleton <c>Amendments</c> object.</summary>
    public const string SingletonIndex = "7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4";

    /// <summary>An amendment's id: the first half of SHA-512 over its name, upper-case hex.</summary>
    /// <param name="name">The amendment's name, for example <see cref="Batch"/>.</param>
    public static string IdOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Convert.ToHexString(SHA512.HashData(Encoding.ASCII.GetBytes(name)).AsSpan(0, 32));
    }
}
