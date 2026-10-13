using System.Globalization;

namespace XrplFile.App.Core;

/// <summary>
/// An amount of drops as a person reads it: in XRP, grouped, with no trailing zeros. Used where a
/// sentence names a reserve, which reads better as "0.2 XRP" than as "200,000 drops".
/// </summary>
public static class XrpAmount
{
    private const decimal DropsPerXrp = 1_000_000m;

    public static string Format(long drops) =>
        string.Create(CultureInfo.InvariantCulture, $"{drops / DropsPerXrp:#,0.######} XRP");
}
