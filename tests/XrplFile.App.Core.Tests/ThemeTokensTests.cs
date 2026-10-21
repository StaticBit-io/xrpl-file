using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// The theme's tokens and the contrast design 2026-10-13 section 2 promises, read from app.css
/// itself: a colour edited in the stylesheet without the design being re-measured fails here, on
/// Linux, rather than on somebody's screen.
/// </summary>
public sealed partial class ThemeTokensTests
{
    private static readonly IReadOnlyDictionary<string, string> Tokens = ReadTokens();

    [Theory]
    [InlineData("bg", "#0D0D0D")]
    [InlineData("box-bg", "#1A1A1A")]
    [InlineData("chips", "#2D2D2D")]
    [InlineData("dark-gray", "#282828")]
    [InlineData("control-line", "#6B6B6B")]
    [InlineData("secondary-dark", "#202222")]
    [InlineData("text", "#F9F9F9")]
    [InlineData("text-secondary", "#A8A8A8")]
    [InlineData("mint", "#64CF95")]
    [InlineData("beige", "#EBAF8E")]
    [InlineData("negative", "#D6776A")]
    [InlineData("yellow", "#D3E7AB")]
    public void Each_token_has_the_value_the_design_gives_it(string name, string value)
    {
        Assert.True(Tokens.ContainsKey(name), $"app.css :root has no --{name}");
        Assert.Equal(value, Tokens[name]);
    }

    [Theory]
    [InlineData("text", "bg", 4.5)]
    [InlineData("text", "box-bg", 4.5)]
    [InlineData("text", "chips", 4.5)]
    [InlineData("text-secondary", "bg", 4.5)]
    [InlineData("text-secondary", "box-bg", 4.5)]
    [InlineData("text-secondary", "chips", 4.5)]
    [InlineData("mint", "bg", 4.5)]
    [InlineData("mint", "box-bg", 4.5)]
    [InlineData("mint", "chips", 4.5)]
    [InlineData("beige", "bg", 4.5)]
    [InlineData("beige", "box-bg", 4.5)]
    [InlineData("beige", "chips", 4.5)]
    [InlineData("negative", "bg", 4.5)]
    [InlineData("negative", "box-bg", 4.5)]
    [InlineData("yellow", "bg", 4.5)]
    [InlineData("yellow", "box-bg", 4.5)]
    [InlineData("yellow", "chips", 4.5)]
    [InlineData("control-line", "bg", 3.0)]
    [InlineData("control-line", "box-bg", 3.0)]
    public void Every_pair_the_design_uses_meets_its_threshold(string foreground, string background, double minimum)
    {
        double ratio = Contrast(Tokens[foreground], Tokens[background]);

        Assert.True(ratio >= minimum, $"--{foreground} on --{background} is {ratio:F2}:1, below {minimum}:1");
    }

    /// <summary>
    /// The one pair the design measured and refused: negative text on a chip. Pinned so that a palette
    /// change that lifted it is noticed, and the "never on chips" rule can be revisited on purpose.
    /// </summary>
    [Fact]
    public void Negative_on_chips_is_the_pair_the_design_refuses()
    {
        Assert.True(Contrast(Tokens["negative"], Tokens["chips"]) < 4.5);
    }

    [Theory]
    [InlineData("mint")]
    [InlineData("negative")]
    public void Dark_text_on_a_filled_button_is_readable(string fill)
    {
        Assert.True(Contrast("#0D0D0D", Tokens[fill]) >= 4.5);
    }

    /// <summary>
    /// A banner draws its text in its own colour over a tint of that colour, laid over whatever the
    /// banner sits on: the page or a card. "Danger on a card" passes by under a tenth, so a token
    /// change that tips it must fail here. The alphas are app.css's `.alert-*` backgrounds.
    /// </summary>
    [Theory]
    [InlineData("yellow", 0.12, "bg")]
    [InlineData("yellow", 0.12, "box-bg")]
    [InlineData("negative", 0.14, "bg")]
    [InlineData("negative", 0.14, "box-bg")]
    [InlineData("mint", 0.12, "bg")]
    [InlineData("mint", 0.12, "box-bg")]
    public void Banner_text_reads_on_its_own_tint(string colour, double alpha, string surface)
    {
        string tint = Composite(Tokens[colour], alpha, Tokens[surface]);
        double ratio = Contrast(Tokens[colour], tint);

        Assert.True(ratio >= 4.5, $"--{colour} on its {alpha:P0} tint over --{surface} is {ratio:F2}:1");
    }

    private static string Composite(string top, double alpha, string bottom)
    {
        int Mix(int start)
        {
            int t = int.Parse(top.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(bottom.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (int)Math.Round((alpha * t) + ((1 - alpha) * b), MidpointRounding.AwayFromZero);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{Mix(1):X2}{Mix(3):X2}{Mix(5):X2}");
    }

    /// <summary>
    /// XRPL addresses are case-sensitive, and the estimate's section titles are upper-cased: an
    /// address inside one used to read as a different, invalid one.
    /// </summary>
    [Fact]
    public void Monospaced_text_is_never_case_transformed()
    {
        Match mono = MonoRule().Match(ReadStylesheet());

        Assert.True(mono.Success, "app.css has no .xf-mono rule");
        Assert.Contains("text-transform: none", mono.Groups["body"].Value, StringComparison.Ordinal);
    }

    private static string ReadStylesheet() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "app", "XrplFile.App", "wwwroot", "app.css"));

    private static IReadOnlyDictionary<string, string> ReadTokens()
    {
        string css = ReadStylesheet();
        Match root = RootBlock().Match(css);
        Assert.True(root.Success, "app.css has no :root block");

        Dictionary<string, string> tokens = new(StringComparer.Ordinal);
        foreach (Match token in ColourToken().Matches(root.Groups["body"].Value))
        {
            tokens[token.Groups["name"].Value] = token.Groups["value"].Value.ToUpperInvariant();
        }

        return tokens;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XrplFile.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"XrplFile.sln was not found above {AppContext.BaseDirectory}");
    }

    private static double Contrast(string first, string second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double r = Channel(hex, 1);
        double g = Channel(hex, 3);
        double b = Channel(hex, 5);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Channel(string hex, int start)
    {
        double value = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    [GeneratedRegex(@":root\s*\{(?<body>[^}]*)\}")]
    private static partial Regex RootBlock();

    [GeneratedRegex(@"--(?<name>[a-z-]+)\s*:\s*(?<value>#[0-9A-Fa-f]{6})\s*;")]
    private static partial Regex ColourToken();

    [GeneratedRegex(@"(?m)^\.xf-mono\s*\{(?<body>[^}]*)\}")]
    private static partial Regex MonoRule();
}
