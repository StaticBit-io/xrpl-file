using System.Globalization;

namespace XrplFile.App.Core;

/// <summary>
/// An amount of drops as a person reads it: in XRP, grouped, with no trailing zeros. Used where a
/// sentence names a reserve, which reads better as "0.2 XRP" than as "200,000 drops". As a
/// <see cref="Localization.UiText"/> value it is formatted in the language's locale ("0,2 XRP").
/// </summary>
public readonly record struct XrpAmount(long Drops)
{
    private const decimal DropsPerXrp = 1_000_000m;

    /// <summary>The amount with the invariant culture's separators, as logs and the English tests read it.</summary>
    public static string Format(long drops) => new XrpAmount(drops).ToString(CultureInfo.InvariantCulture);

    public string ToString(IFormatProvider culture) =>
        string.Create(culture, $"{Drops / DropsPerXrp:#,0.######} XRP");

    public override string ToString() => ToString(CultureInfo.InvariantCulture);
}
