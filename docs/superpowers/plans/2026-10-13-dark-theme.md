# Dark Theme and Responsive Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the desktop application xrpl-vanity's dark look and a layout that holds from a 1280 px window down to a phone's width, without changing any text, flow or decision.

**Architecture:** Bootstrap 5.3.3 stays as the precompiled file. A new `wwwroot/app.css` remaps Bootstrap's root variables, overrides every component variant the application uses, and adds a small `xf-` class layer for the shell and pages. `ShellModel` gains the History tab and the phone-width More menu, tested on Linux; `Shell.razor` draws a side rail or a bottom tab bar chosen by CSS breakpoints; every other screen gets mechanical class and wrapper changes only.

**Tech Stack:** .NET 10 MAUI Blazor Hybrid (BlazorWebView over WebView2), Bootstrap 5.3.3, xUnit v3, MauiLab Hyper-V VM for visual checks.

**Spec:** `docs/superpowers/specs/2026-10-13-dark-theme-design.md`

## Global Constraints

- No existing user-visible text changes. The only new texts: navigation labels "History" and "More", accessible names "Lock the vault", "Main" (navigation label) and "More" (its button).
- Every application class carries the `xf-` prefix; Bootstrap class names are restyled in place, never renamed.
- All CSS lives in `app/XrplFile.App/wwwroot/app.css`; no `.razor.css` file is added.
- Breakpoints: `min-width: 1100px` (wide), `720px`–`1099px` (medium), `max-width: 719.98px` (phone).
- Targets: every navigation entry, button and form control has a hit area of at least 44 × 44 CSS px; inline links in running text are exempt.
- Error text never sits on `--chips`.
- Fonts ship as files: Inter 400/500/600 and Cascadia Code 400, woff2, with their OFL licence texts.
- Components hold no decisions (C1 E2): presentational markup changes only; a decision, a text or a binding never moves out of a model.
- `dotnet test` does not work in this repository: run test projects as executables, e.g. `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ShellModelTests"`.
- Warnings are errors. C#: explicit types, no `var`.
- Desktop GUI runs only in the MauiLab VM (`D:\Lab\maui-vm-lab`), never on the host. Docker only with `-p xrpl-file-ci`; never touch other compose projects.
- Commits: Conventional Commits, English, no AI attribution of any kind. Do not push.

## Review Focus

1. **A long unbroken address, link or path at 600 px** must wrap inside its card; the page must never scroll sideways. Pinned in Task 8, step 4 (History with a real link at 600 px, `document.scrollingElement.scrollWidth` check).
2. **A focused rail item hidden by shrinking the window below 720 px** must hand focus to the content region, not to nothing. Pinned in Task 8, step 5.
3. **The sticky action bar at 600 px** must never hide the last alert or validation message on Estimate or Run; scrolling to the bottom shows it above the bar. Pinned in Task 8, step 4.
4. **A Bootstrap variant nobody overrode** (`.list-group-item.active`, `.form-check-input:checked`, a focus ring) would show Bootstrap's stock blue on a dark page. Pinned in Task 8, step 6 (scan of computed colours for `rgb(13, 110, 253)`).
5. **Windows text size 150%** must not clip rail labels or push the tab bar off-screen. Pinned in Task 8, step 7.

---

### Task 1: The theme stylesheet, fonts and token tests

**Files:**
- Create: `app/XrplFile.App/wwwroot/fonts/` (six files copied from xrpl-vanity)
- Replace: `app/XrplFile.App/wwwroot/app.css`
- Modify: `app/XrplFile.App/wwwroot/index.html`
- Test: `tests/XrplFile.App.Core.Tests/ThemeTokensTests.cs`

**Interfaces:**
- Produces: CSS custom properties `--bg --box-bg --chips --dark-gray --control-line --secondary-dark --text --text-secondary --mint --beige --negative --yellow --radius-card --radius-chip --font-text --font-mono --ease`; classes `xf-shell xf-rail xf-rail-mark xf-rail-item xf-rail-spacer xf-topbar xf-icon-button xf-content xf-tabbar xf-tab xf-more xf-more-item xf-more-backdrop xf-page xf-title xf-section-title xf-card xf-kv-table xf-actionbar xf-vault xf-mono xf-num xf-figure xf-muted` (`xf-num` and `xf-figure` are the spec's classes for figures; no screen task applies them, since today's screens show their figures inside label/value tables that `xf-kv-table` already styles); `aria-current="page"` styling on `.xf-rail-item` and `.xf-tab`.

- [ ] **Step 1: Write the failing token test**

Create `tests/XrplFile.App.Core.Tests/ThemeTokensTests.cs`:

```csharp
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

    private static IReadOnlyDictionary<string, string> ReadTokens()
    {
        string css = File.ReadAllText(Path.Combine(RepositoryRoot(), "app", "XrplFile.App", "wwwroot", "app.css"));
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
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ThemeTokensTests"`
Expected: FAIL — "app.css has no :root block" (today's app.css has none).

- [ ] **Step 3: Copy the fonts**

```bash
mkdir -p app/XrplFile.App/wwwroot/fonts
cp E:/GIT/XRPL/xrpl-vanity/src/XrplVanity.App.Core/wwwroot/fonts/{Inter-Regular.woff2,Inter-Medium.woff2,Inter-SemiBold.woff2,CascadiaCode.woff2,LICENSE-Inter.txt,LICENSE-CascadiaCode.txt} app/XrplFile.App/wwwroot/fonts/
ls app/XrplFile.App/wwwroot/fonts
```

Expected: the six files listed.

- [ ] **Step 4: Replace `app/XrplFile.App/wwwroot/app.css`**

```css
/* The xrpl-file theme: xrpl-vanity-generator's palette and type on top of Bootstrap 5.3.3.
   Contract: docs/superpowers/specs/2026-10-13-dark-theme-design.md. ThemeTokensTests reads the
   :root block below; keep every colour token a six-digit hex value. */

@font-face { font-family: "Inter"; font-style: normal; font-weight: 400; font-display: swap; src: url("fonts/Inter-Regular.woff2") format("woff2"); }
@font-face { font-family: "Inter"; font-style: normal; font-weight: 500; font-display: swap; src: url("fonts/Inter-Medium.woff2") format("woff2"); }
@font-face { font-family: "Inter"; font-style: normal; font-weight: 600; font-display: swap; src: url("fonts/Inter-SemiBold.woff2") format("woff2"); }
@font-face { font-family: "Cascadia Code"; font-style: normal; font-weight: 400; font-display: swap; src: url("fonts/CascadiaCode.woff2") format("woff2"); }

:root {
    color-scheme: dark;
    --bg: #0D0D0D;
    --box-bg: #1A1A1A;
    --chips: #2D2D2D;
    --dark-gray: #282828;
    --control-line: #6B6B6B;
    --secondary-dark: #202222;
    --text: #F9F9F9;
    --text-secondary: #A8A8A8;
    --mint: #64CF95;
    --beige: #EBAF8E;
    --negative: #D6776A;
    --yellow: #D3E7AB;
    --radius-card: 12px;
    --radius-chip: 10px;
    --font-text: "Inter", "Segoe UI", system-ui, sans-serif;
    --font-mono: "Cascadia Code", "Consolas", monospace;
    --ease: 180ms ease-out;
}

/* ---- Bootstrap root remapping ---- */

[data-bs-theme="dark"] {
    --bs-body-bg: var(--bg); --bs-body-bg-rgb: 13, 13, 13;
    --bs-body-color: var(--text); --bs-body-color-rgb: 249, 249, 249;
    --bs-body-font-family: var(--font-text);
    --bs-body-font-size: 0.875rem;
    --bs-emphasis-color: var(--text); --bs-emphasis-color-rgb: 249, 249, 249;
    --bs-secondary-color: var(--text-secondary); --bs-secondary-color-rgb: 168, 168, 168;
    --bs-tertiary-color: var(--text-secondary); --bs-tertiary-color-rgb: 168, 168, 168;
    --bs-secondary-bg: var(--chips); --bs-secondary-bg-rgb: 45, 45, 45;
    --bs-tertiary-bg: var(--box-bg); --bs-tertiary-bg-rgb: 26, 26, 26;
    --bs-border-color: var(--dark-gray);
    --bs-border-color-translucent: rgba(249, 249, 249, 0.08);
    --bs-primary: var(--mint); --bs-primary-rgb: 100, 207, 149;
    --bs-success: var(--mint); --bs-success-rgb: 100, 207, 149;
    --bs-danger: var(--negative); --bs-danger-rgb: 214, 119, 106;
    --bs-warning: var(--yellow); --bs-warning-rgb: 211, 231, 171;
    --bs-info: var(--text-secondary); --bs-info-rgb: 168, 168, 168;
    --bs-primary-text-emphasis: var(--mint);
    --bs-success-text-emphasis: var(--mint);
    --bs-danger-text-emphasis: var(--negative);
    --bs-warning-text-emphasis: var(--yellow);
    --bs-info-text-emphasis: var(--text);
    --bs-secondary-text-emphasis: var(--text);
    --bs-primary-bg-subtle: rgba(100, 207, 149, 0.12);
    --bs-success-bg-subtle: rgba(100, 207, 149, 0.12);
    --bs-danger-bg-subtle: rgba(214, 119, 106, 0.14);
    --bs-warning-bg-subtle: rgba(211, 231, 171, 0.12);
    --bs-info-bg-subtle: var(--chips);
    --bs-secondary-bg-subtle: var(--chips);
    --bs-primary-border-subtle: transparent;
    --bs-success-border-subtle: transparent;
    --bs-danger-border-subtle: transparent;
    --bs-warning-border-subtle: transparent;
    --bs-info-border-subtle: transparent;
    --bs-secondary-border-subtle: transparent;
    --bs-link-color: var(--text-secondary); --bs-link-color-rgb: 168, 168, 168;
    --bs-link-hover-color: var(--mint); --bs-link-hover-color-rgb: 100, 207, 149;
    --bs-border-radius: var(--radius-chip);
    --bs-border-radius-sm: 8px;
    --bs-border-radius-lg: var(--radius-card);
    --bs-font-sans-serif: var(--font-text);
    --bs-font-monospace: var(--font-mono);
    --bs-focus-ring-color: rgba(100, 207, 149, 1);
    --bs-code-color: var(--text);
}

html, body { height: 100%; margin: 0; background: var(--bg); color: var(--text); font-family: var(--font-text); }
html { font-synthesis-weight: none; }
strong, b, th { font-weight: 600; }
h1, h2, h3, h4, h5, h6 { color: var(--text); font-weight: 600; letter-spacing: -0.01em; }
.alert h1, .alert h2, .alert h3, .alert h4, .alert h5, .alert h6 { color: inherit; }
.d-flex > *, .xf-actionbar > * { min-width: 0; }
code { font-family: var(--font-mono); font-size: 0.8125rem; color: var(--text); overflow-wrap: anywhere; }

:focus-visible { outline: 2px solid var(--mint); outline-offset: 2px; }
.btn:focus-visible, .nav-link:focus-visible, .list-group-item-action:focus-visible { outline: 2px solid var(--mint); outline-offset: 2px; box-shadow: none; }

/* ---- Buttons ---- */

.btn {
    --bs-btn-font-weight: 600;
    --bs-btn-font-size: 0.875rem;
    --bs-btn-padding-x: 1.1rem;
    --bs-btn-padding-y: 0.5rem;
    --bs-btn-border-radius: var(--radius-chip);
    --bs-btn-disabled-opacity: 0.45;
    --bs-btn-focus-box-shadow: none;
    min-height: 44px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    transition: background-color var(--ease), color var(--ease), border-color var(--ease);
}
.btn-sm { --bs-btn-padding-x: 0.75rem; --bs-btn-padding-y: 0.25rem; --bs-btn-font-size: 0.8125rem; min-height: 36px; position: relative; }
.btn-sm::after { content: ""; position: absolute; inset: -4px 0; }

.btn-primary, .btn-success {
    --bs-btn-color: #0D0D0D; --bs-btn-bg: var(--mint); --bs-btn-border-color: var(--mint);
    --bs-btn-hover-color: #0D0D0D; --bs-btn-hover-bg: #78D7A4; --bs-btn-hover-border-color: #78D7A4;
    --bs-btn-active-color: #0D0D0D; --bs-btn-active-bg: #58B985; --bs-btn-active-border-color: #58B985;
    --bs-btn-disabled-color: #0D0D0D; --bs-btn-disabled-bg: var(--mint); --bs-btn-disabled-border-color: var(--mint);
}
.btn-outline-primary {
    --bs-btn-color: var(--mint); --bs-btn-border-color: var(--mint);
    --bs-btn-hover-color: var(--mint); --bs-btn-hover-bg: rgba(100, 207, 149, 0.10); --bs-btn-hover-border-color: var(--mint);
    --bs-btn-active-color: var(--mint); --bs-btn-active-bg: rgba(100, 207, 149, 0.18); --bs-btn-active-border-color: var(--mint);
    --bs-btn-disabled-color: var(--mint); --bs-btn-disabled-bg: transparent; --bs-btn-disabled-border-color: var(--mint);
}
.btn-outline-secondary {
    --bs-btn-color: var(--text); --bs-btn-bg: var(--secondary-dark); --bs-btn-border-color: var(--control-line);
    --bs-btn-hover-color: var(--text); --bs-btn-hover-bg: #2A2C2C; --bs-btn-hover-border-color: var(--text-secondary);
    --bs-btn-active-color: var(--text); --bs-btn-active-bg: var(--chips); --bs-btn-active-border-color: var(--text-secondary);
    --bs-btn-disabled-color: var(--text); --bs-btn-disabled-bg: var(--secondary-dark); --bs-btn-disabled-border-color: var(--control-line);
}
.btn-danger {
    --bs-btn-color: #0D0D0D; --bs-btn-bg: var(--negative); --bs-btn-border-color: var(--negative);
    --bs-btn-hover-color: #0D0D0D; --bs-btn-hover-bg: #DE8B80; --bs-btn-hover-border-color: #DE8B80;
    --bs-btn-active-color: #0D0D0D; --bs-btn-active-bg: #C66A5E; --bs-btn-active-border-color: #C66A5E;
    --bs-btn-disabled-color: #0D0D0D; --bs-btn-disabled-bg: var(--negative); --bs-btn-disabled-border-color: var(--negative);
}
.btn-outline-danger {
    --bs-btn-color: var(--negative); --bs-btn-border-color: var(--negative);
    --bs-btn-hover-color: var(--negative); --bs-btn-hover-bg: rgba(214, 119, 106, 0.12); --bs-btn-hover-border-color: var(--negative);
    --bs-btn-active-color: var(--negative); --bs-btn-active-bg: rgba(214, 119, 106, 0.20); --bs-btn-active-border-color: var(--negative);
    --bs-btn-disabled-color: var(--negative); --bs-btn-disabled-bg: transparent; --bs-btn-disabled-border-color: var(--negative);
}
.btn-outline-warning {
    --bs-btn-color: var(--yellow); --bs-btn-border-color: var(--yellow);
    --bs-btn-hover-color: var(--yellow); --bs-btn-hover-bg: rgba(211, 231, 171, 0.10); --bs-btn-hover-border-color: var(--yellow);
    --bs-btn-active-color: var(--yellow); --bs-btn-active-bg: rgba(211, 231, 171, 0.18); --bs-btn-active-border-color: var(--yellow);
    --bs-btn-disabled-color: var(--yellow); --bs-btn-disabled-bg: transparent; --bs-btn-disabled-border-color: var(--yellow);
}
.btn-link {
    --bs-btn-color: var(--text-secondary); --bs-btn-hover-color: var(--mint); --bs-btn-active-color: var(--mint);
    --bs-btn-disabled-color: var(--text-secondary);
    --bs-btn-font-weight: 500;
    text-decoration: none;
}
.btn-link:focus-visible { text-decoration: underline; }

/* ---- Alerts (banners) ---- */

.alert { --bs-alert-border-radius: var(--radius-chip); --bs-alert-padding-x: 0.875rem; --bs-alert-padding-y: 0.625rem; border-width: 0; }
.alert-warning { --bs-alert-color: var(--yellow); --bs-alert-bg: rgba(211, 231, 171, 0.12); --bs-alert-link-color: var(--yellow); }
.alert-danger { --bs-alert-color: var(--negative); --bs-alert-bg: rgba(214, 119, 106, 0.14); --bs-alert-link-color: var(--negative); }
.alert-success { --bs-alert-color: var(--mint); --bs-alert-bg: rgba(100, 207, 149, 0.12); --bs-alert-link-color: var(--mint); }
.alert-info, .alert-secondary { --bs-alert-color: var(--text); --bs-alert-bg: var(--chips); --bs-alert-link-color: var(--text); }
/* A banner inside a banner (Read's failed save inside its success banner) sits on the page colour,
   not on a second tint: two tints stacked put danger text at 4.22:1. */
.alert .alert { --bs-alert-bg: var(--bg); }

/* ---- Forms ---- */

.form-control, .form-select {
    background-color: var(--chips); color: var(--text); border: 1px solid var(--control-line);
    border-radius: var(--radius-chip); min-height: 44px; font-size: 0.875rem;
}
.form-control:focus, .form-select:focus { background-color: var(--chips); color: var(--text); border-color: var(--mint); box-shadow: none; outline: 2px solid var(--mint); outline-offset: 2px; }
.form-control:disabled, .form-select:disabled { opacity: 0.45; }
.form-control::placeholder { color: var(--text-secondary); opacity: 1; }
.form-label { color: var(--text-secondary); font-size: 0.8125rem; margin-bottom: 6px; }
.form-check { min-height: 44px; display: flex; align-items: center; gap: 0.5rem; }
.form-check-label { min-height: 44px; display: inline-flex; align-items: center; cursor: pointer; }
.form-check-input { background-color: var(--chips); border-color: var(--control-line); margin-top: 0; }
.form-check-input:focus { box-shadow: none; outline: 2px solid var(--mint); outline-offset: 2px; border-color: var(--control-line); }
.form-check-input:checked, .form-check-input:checked:focus { background-color: var(--mint); border-color: var(--mint); }
/* Switch knobs: Bootstrap paints a light-blue knob on focus and a faint white one in dark mode
   (2.26:1 against the track). Grey when off, near-black on mint when on. */
.form-switch .form-check-input:not(:checked),
.form-switch .form-check-input:not(:checked):focus {
    --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='%23A8A8A8'/%3e%3c/svg%3e");
}
.form-switch .form-check-input:checked,
.form-switch .form-check-input:checked:focus {
    --bs-form-switch-bg: url("data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='-4 -4 8 8'%3e%3ccircle r='3' fill='%230D0D0D'/%3e%3c/svg%3e");
}

/* ---- Tables, lists, tabs, progress, badges ---- */

.table {
    --bs-table-bg: transparent; --bs-table-color: var(--text); --bs-table-border-color: var(--dark-gray);
    --bs-table-hover-bg: var(--chips); --bs-table-hover-color: var(--text);
    margin-bottom: 0;
}
.table > thead th { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.08em; color: var(--text-secondary); font-weight: 500; }
.xf-kv-table th { color: var(--text-secondary); font-weight: 400; }
.xf-kv-table td { text-align: right; font-variant-numeric: tabular-nums; }

.list-group {
    --bs-list-group-bg: var(--box-bg); --bs-list-group-color: var(--text); --bs-list-group-border-color: var(--dark-gray);
    --bs-list-group-border-radius: var(--radius-chip);
    --bs-list-group-action-color: var(--text); --bs-list-group-action-hover-color: var(--text); --bs-list-group-action-hover-bg: var(--chips);
    --bs-list-group-action-active-color: var(--text); --bs-list-group-action-active-bg: var(--chips);
    --bs-list-group-active-color: var(--mint); --bs-list-group-active-bg: rgba(100, 207, 149, 0.16); --bs-list-group-active-border-color: var(--mint);
}
.list-group-item { min-height: 44px; display: flex; align-items: center; }

.nav-tabs {
    --bs-nav-tabs-border-color: var(--dark-gray);
    --bs-nav-tabs-link-hover-border-color: transparent transparent var(--control-line);
    --bs-nav-tabs-link-active-color: var(--mint);
    --bs-nav-tabs-link-active-bg: transparent;
    --bs-nav-tabs-link-active-border-color: transparent transparent var(--mint);
    --bs-nav-link-color: var(--text-secondary);
    --bs-nav-link-hover-color: var(--text);
}
.nav-tabs .nav-link { min-height: 44px; border-width: 0 0 2px; }

.progress {
    --bs-progress-height: 20px; --bs-progress-bg: var(--chips); --bs-progress-border-radius: 999px;
    --bs-progress-bar-bg: var(--mint); --bs-progress-bar-color: #0D0D0D; --bs-progress-font-size: 0.75rem;
}
/* Wide enough for "100%" at any value, so the percentage the bar carries is never clipped; the
   bar therefore never looks narrower than its label, a trade made for legibility. */
.progress-bar { font-weight: 600; min-width: 3.25em; }
progress { accent-color: var(--mint); }

.badge { --bs-badge-border-radius: 999px; --bs-badge-font-weight: 600; }
.text-bg-danger { background-color: var(--negative) !important; color: #0D0D0D !important; }

/* ---- Shell ---- */

.xf-shell { display: grid; grid-template-columns: 72px minmax(0, 1fr); grid-template-rows: minmax(0, 1fr); height: 100vh; }
.xf-topbar, .xf-tabbar { display: none; }

.xf-rail { display: flex; flex-direction: column; align-items: center; gap: 6px; padding: 14px 0; border-right: 1px solid var(--dark-gray); box-sizing: border-box; min-height: 0; overflow-y: auto; }
.xf-rail-mark { width: 36px; height: 36px; border-radius: 10px; background: #141414; display: grid; place-items: center; margin-bottom: 14px; flex: none; }
.xf-rail-spacer { flex-grow: 1; }
.xf-rail-item, .xf-tab {
    background: transparent; border: 0; color: var(--text-secondary); cursor: pointer;
    display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px;
    font: inherit; font-size: 0.6875rem; letter-spacing: 0.02em; border-radius: 10px;
    transition: background-color var(--ease), color var(--ease);
}
.xf-rail-item { width: 60px; min-height: 52px; padding: 8px 0 6px; }
.xf-rail-item svg, .xf-tab svg { width: 20px; height: 20px; stroke: currentColor; fill: none; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.xf-rail-item:hover, .xf-tab:hover { color: var(--text); background: var(--chips); }
.xf-rail-item[aria-current="page"], .xf-tab[aria-current="page"] { color: var(--mint); background: rgba(100, 207, 149, 0.10); }

.xf-content { min-width: 0; min-height: 0; overflow-y: auto; overflow-x: hidden; outline: none; }

.xf-icon-button { width: 44px; height: 44px; border-radius: 10px; background: transparent; border: 0; color: var(--text-secondary); display: grid; place-items: center; cursor: pointer; }
.xf-icon-button:hover { color: var(--text); background: var(--chips); }
.xf-icon-button svg { width: 22px; height: 22px; stroke: currentColor; fill: none; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }

.xf-tabbar { position: relative; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 2px; padding: 6px 6px calc(10px + env(safe-area-inset-bottom)); border-top: 1px solid var(--dark-gray); background: #111111; }
.xf-tab { min-height: 52px; }
.xf-more-backdrop { position: fixed; inset: 0; z-index: 40; }
.xf-more {
    position: absolute; right: 8px; bottom: calc(100% + 6px); z-index: 41; min-width: 200px;
    background: var(--box-bg); border: 1px solid var(--control-line); border-radius: var(--radius-card);
    padding: 6px; display: flex; flex-direction: column; gap: 2px;
}
.xf-more-item { min-height: 44px; text-align: left; padding: 0 14px; background: transparent; border: 0; border-radius: 8px; color: var(--text); font: inherit; cursor: pointer; }
.xf-more-item:hover, .xf-more-item[aria-current="page"] { background: var(--chips); }
.xf-more-item[aria-current="page"] { color: var(--mint); }

/* ---- Pages ---- */

.xf-page { box-sizing: border-box; max-width: 920px; padding: 20px 26px; min-width: 0; }
.xf-title { font-size: 1.25rem; font-weight: 600; letter-spacing: -0.01em; margin: 0 0 14px; }
.xf-section-title { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.08em; color: var(--text-secondary); font-weight: 500; margin: 0 0 12px; }
.xf-card {
    background: var(--box-bg); border: 1px solid var(--dark-gray); border-radius: var(--radius-card);
    padding: 16px 18px; margin-bottom: 14px; min-width: 0;
}
.xf-card:has(> .table) { overflow-x: auto; }
.xf-card .bg-body-tertiary { background-color: var(--chips) !important; }
.xf-topbar .xf-rail-mark { margin-bottom: 0; }
.xf-actionbar { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 16px; }

.xf-vault { min-height: 100vh; box-sizing: border-box; display: grid; place-items: center; padding: 16px; }
.xf-vault > .xf-card { width: min(420px, 100%); box-sizing: border-box; padding: 28px 30px; margin: 0; }

.xf-mono { font-family: var(--font-mono); overflow-wrap: anywhere; }
.xf-num { font-variant-numeric: tabular-nums; }
.xf-figure { font-variant-numeric: tabular-nums; color: var(--beige); font-weight: 600; }
.xf-muted { color: var(--text-secondary); }

/* ---- Medium windows ---- */

@media (max-width: 1099.98px) {
    .xf-page { max-width: none; padding: 20px 22px; }
}

/* ---- Phone width ---- */

@media (max-width: 719.98px) {
    .xf-shell { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto minmax(0, 1fr) auto; }
    .xf-rail { display: none; }
    .xf-topbar { display: flex; align-items: center; justify-content: space-between; height: 56px; padding: 0 8px 0 16px; border-bottom: 1px solid var(--dark-gray); }
    .xf-tabbar { display: grid; }
    .xf-page { padding: 16px; }
    .xf-page:has(> .xf-actionbar) { padding-bottom: 0; }
    .xf-content { scroll-padding-bottom: 72px; }
    .xf-actionbar {
        position: sticky; bottom: 0; z-index: 5; margin: 16px -16px 0; padding: 10px 16px;
        background: var(--bg); border-top: 1px solid var(--dark-gray);
    }
    .xf-actionbar > .btn { flex: 1 1 0; }
}

@media (min-width: 720px) {
    .xf-more, .xf-more-backdrop { display: none; }
}

@media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { transition: none !important; animation: none !important; }
}

/* ---- Blazor's own error bar ---- */

#blazor-error-ui {
    background: var(--box-bg); color: var(--text); border-top: 2px solid var(--negative);
    bottom: 0; box-sizing: border-box; display: none; left: 0; padding: 0.6rem 1.25rem 0.7rem 1.25rem;
    position: fixed; width: 100%; z-index: 1000;
}
#blazor-error-ui .dismiss { cursor: pointer; position: absolute; right: 0.75rem; top: 0.5rem; }
.blazor-error-boundary { background: rgba(214, 119, 106, 0.14); color: var(--negative); padding: 1rem; border-radius: var(--radius-chip); }
.blazor-error-boundary::after { content: "An error has occurred."; }
```

Today's `app.css` is the MAUI Blazor template's own stylesheet; it is replaced whole. Every rule in it is dropped — the light-theme link, button and focus colours, `.content`, `h1:focus { outline: none; }` (it would remove the focus ring), the form-validation and `.form-floating` rules, `.darker-border-checkbox`, `.status-bar-safe-area` and the `@supports (-webkit-touch-callout: none)` block that paints a light status bar — except `#blazor-error-ui` and `.blazor-error-boundary`, which the file above restates with the same texts.

- [ ] **Step 5: Turn on Bootstrap's dark mode and load the shell script**

In `app/XrplFile.App/wwwroot/index.html` replace `<html lang="en">` with `<html lang="en" data-bs-theme="dark">`, delete the line `<div class="status-bar-safe-area"></div>` (its rule is gone with the template's stylesheet), and add `<script src="shell.js"></script>` directly after `<script src="qr.js"></script>`. (`shell.js` is created in Task 3; until then the browser logs a 404 for it, which nothing depends on.)

- [ ] **Step 6: Run the token test, then the App.Core suite**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ThemeTokensTests"`
Expected: PASS (all cases).
Run: `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Build the application**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add app/XrplFile.App/wwwroot/app.css app/XrplFile.App/wwwroot/index.html app/XrplFile.App/wwwroot/fonts tests/XrplFile.App.Core.Tests/ThemeTokensTests.cs
git commit -m "feat(app): the xrpl-vanity dark theme over Bootstrap, with its tokens pinned by test"
```

---

### Task 2: ShellModel learns History and the More menu

**Files:**
- Modify: `app/XrplFile.App.Core/ShellModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/ShellModelTests.cs`

**Interfaces:**
- Produces: `AppTab.History`; `bool ShellModel.MoreOpen { get; }`; `bool ShellModel.IsMoreCurrent { get; }` (true for Accounts and Settings); `void ShellModel.ToggleMore()`; `void ShellModel.CloseMore()`; `Show(AppTab)` now also closes More. `Changed` is raised whenever `Tab` or `MoreOpen` changes, and only then.

- [ ] **Step 1: Write the failing tests**

Append inside `ShellModelTests`:

```csharp
    [Fact]
    public void History_is_a_tab_of_its_own()
    {
        ShellModel model = new();

        model.Show(AppTab.History);

        Assert.Equal(AppTab.History, model.Tab);
    }

    [Fact]
    public void More_starts_closed()
    {
        Assert.False(new ShellModel().MoreOpen);
    }

    [Fact]
    public void Toggling_More_opens_and_closes_it_and_says_so_each_time()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.ToggleMore();
        Assert.True(model.MoreOpen);

        model.ToggleMore();
        Assert.False(model.MoreOpen);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Choosing_a_tab_closes_More()
    {
        ShellModel model = new();
        model.ToggleMore();

        model.Show(AppTab.Accounts);

        Assert.False(model.MoreOpen);
        Assert.Equal(AppTab.Accounts, model.Tab);
    }

    /// <summary>
    /// Settings is current, More is opened and Settings chosen again: the tab does not change, but
    /// the menu must still close, and the shell must hear about it or the panel stays drawn.
    /// </summary>
    [Fact]
    public void Choosing_the_current_tab_from_More_still_closes_it_and_says_so()
    {
        ShellModel model = new();
        model.Show(AppTab.Settings);
        model.ToggleMore();
        int raised = 0;
        model.Changed += () => raised++;

        model.Show(AppTab.Settings);

        Assert.False(model.MoreOpen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Closing_More_when_it_is_open_says_so()
    {
        ShellModel model = new();
        model.ToggleMore();
        int raised = 0;
        model.Changed += () => raised++;

        model.CloseMore();

        Assert.False(model.MoreOpen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Closing_More_when_it_is_already_closed_says_nothing()
    {
        ShellModel model = new();
        int raised = 0;
        model.Changed += () => raised++;

        model.CloseMore();

        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(AppTab.Accounts, true)]
    [InlineData(AppTab.Settings, true)]
    [InlineData(AppTab.Keys, false)]
    [InlineData(AppTab.Publish, false)]
    [InlineData(AppTab.Read, false)]
    [InlineData(AppTab.History, false)]
    public void More_counts_as_current_exactly_for_the_tabs_it_holds(AppTab tab, bool expected)
    {
        ShellModel model = new();

        model.Show(tab);

        Assert.Equal(expected, model.IsMoreCurrent);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ShellModelTests"`
Expected: build FAILS — `AppTab.History`, `MoreOpen`, `ToggleMore`, `CloseMore`, `IsMoreCurrent` do not exist.

- [ ] **Step 3: Implement**

In `app/XrplFile.App.Core/ShellModel.cs`, add `History,` to `AppTab` after `Read,`. Replace the body of `Show` and add the new members:

```csharp
    /// <summary>
    /// Whether the phone-width More menu (Accounts, Settings) is open. Only <see cref="ToggleMore"/>
    /// opens it; choosing any tab, <see cref="CloseMore"/>, and the window widening past the phone
    /// layout close it (design 2026-10-13, section 4).
    /// </summary>
    public bool MoreOpen { get; private set; }

    /// <summary>
    /// Whether the More entry is the one to mark current: the tab showing is one More holds. A phone
    /// user who chose Settings from More would otherwise see no entry marked at all.
    /// </summary>
    public bool IsMoreCurrent => Tab is AppTab.Accounts or AppTab.Settings;

    /// <summary>Shows <paramref name="tab"/> and closes the More menu.</summary>
    /// <param name="tab">
    /// The tab to draw. Nothing is validated: <see cref="AppTab"/> is closed, every caller passes
    /// one of its members by name, and the shell's render chain names each tab it draws rather than
    /// falling through to a default, so a member nothing has taught it to draw draws nothing.
    /// </param>
    public void Show(AppTab tab)
    {
        bool changed = MoreOpen;
        MoreOpen = false;

        if (Tab != tab)
        {
            Tab = tab;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Opens the More menu when it is closed, and closes it when it is open.</summary>
    public void ToggleMore()
    {
        MoreOpen = !MoreOpen;
        Changed?.Invoke();
    }

    /// <summary>Closes the More menu; says nothing when it was already closed.</summary>
    public void CloseMore()
    {
        if (!MoreOpen)
        {
            return;
        }

        MoreOpen = false;
        Changed?.Invoke();
    }
```

Update the `Changed` event's summary to "Raised after <see cref="Tab"/> or <see cref="MoreOpen"/> changes, and only then." Leave its remarks as they are.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ShellModelTests"`
Expected: PASS (existing five tests and the new ones).
Run: `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/ShellModel.cs tests/XrplFile.App.Core.Tests/ShellModelTests.cs
git commit -m "feat(app): History becomes a tab and the shell model owns the phone More menu"
```

---

### Task 3: The shell — rail, tab bar, More, History tab

**Files:**
- Modify: `app/XrplFile.App/Components/Shell.razor`
- Modify: `app/XrplFile.App/Components/Publish.razor` (remove `<History />`)
- Create: `app/XrplFile.App/wwwroot/shell.js`

**Interfaces:**
- Consumes: Task 1's classes; Task 2's `AppTab.History`, `MoreOpen`, `IsMoreCurrent`, `ToggleMore()`, `CloseMore()`.
- Produces: `window.xfShell.watchWidth(dotnetRef)` calling `[JSInvokable] Shell.OnWideLayout()`; the content region `<main id="xf-content" class="xf-content" tabindex="-1">`.

- [ ] **Step 1: Create `app/XrplFile.App/wwwroot/shell.js`**

```js
// Phone-width More menu and focus housekeeping for Shell.razor (design 2026-10-13, sections 3–4).
// The layout itself is pure CSS; this file only reacts when the window crosses the 720 px line.
window.xfShell = {
    watchWidth: function (dotnet) {
        const wide = window.matchMedia('(min-width: 720px)');
        wide.addEventListener('change', function () {
            if (wide.matches) {
                dotnet.invokeMethodAsync('OnWideLayout').catch(function () { });
            }

            // The navigation that held focus may just have become display:none; hand focus to the
            // content region instead of leaving it on nothing.
            const active = document.activeElement;
            if (active && active !== document.body && active.offsetParent === null) {
                const content = document.getElementById('xf-content');
                if (content) {
                    content.focus();
                }
            }
        });
    }
};
```

- [ ] **Step 2: Replace the shell's markup**

In `Shell.razor` add `@inject IJSRuntime JS` after the existing `@inject` lines. Replace the failure branch's `<div class="container py-4"><div class="alert alert-danger" role="alert">…</div></div>` wrapper with `<div class="xf-vault"><section class="xf-card"><div class="alert alert-danger mb-0" role="alert">…</div></section></div>` (inner paragraphs unchanged), and the loading branch's `<div class="container py-4"><p>Loading&hellip;</p></div>` with `<div class="xf-vault"><section class="xf-card"><p class="xf-muted mb-0">Loading&hellip;</p></section></div>`.

Replace everything inside `case UnlockStage.Unlocked:` up to its `break;` with:

```razor
            <div class="xf-shell">
                <header class="xf-topbar">
                    <span class="xf-rail-mark" aria-hidden="true">@Mark</span>
                    <button type="button" class="xf-icon-button" aria-label="Lock the vault" @onclick="LockAsync">@LockIcon</button>
                </header>

                <nav class="xf-rail" aria-label="Main">
                    <span class="xf-rail-mark" aria-hidden="true">@Mark</span>
                    @foreach (AppTab tab in RailTabs)
                    {
                        <button type="button" class="xf-rail-item" aria-current="@(Tabs.Tab == tab ? "page" : null)"
                                @onclick="() => Tabs.Show(tab)">
                            @Icon(tab)<span>@tab</span>
                        </button>
                    }
                    <span class="xf-rail-spacer"></span>
                    <button type="button" class="xf-rail-item" aria-label="Lock the vault" @onclick="LockAsync">
                        @LockIcon<span aria-hidden="true">Lock</span>
                    </button>
                </nav>

                <main id="xf-content" class="xf-content" tabindex="-1" @ref="_content">
                    @if (Tabs.Tab == AppTab.Keys)
                    {
                        <KeyList />
                    }
                    else if (Tabs.Tab == AppTab.Publish)
                    {
                        <Publish />
                    }
                    else if (Tabs.Tab == AppTab.History)
                    {
                        <History />
                    }
                    else if (Tabs.Tab == AppTab.Settings)
                    {
                        <Settings />
                    }
                    else if (Tabs.Tab == AppTab.Accounts)
                    {
                        <Accounts />
                    }
                    else if (Tabs.Tab == AppTab.Read)
                    {
                        <Read />
                    }
                </main>

                <nav class="xf-tabbar" aria-label="Main" @onkeydown="OnTabbarKey">
                    @foreach (AppTab tab in TabbarTabs)
                    {
                        <button type="button" class="xf-tab" aria-current="@(Tabs.Tab == tab ? "page" : null)"
                                @onclick="() => Tabs.Show(tab)">
                            @Icon(tab)<span>@tab</span>
                        </button>
                    }
                    <button type="button" class="xf-tab" @ref="_moreButton"
                            aria-expanded="@(Tabs.MoreOpen ? "true" : "false")" aria-controls="xf-more"
                            aria-current="@(Tabs.IsMoreCurrent ? "page" : null)" @onclick="ToggleMore">
                        @MoreIcon<span>More</span>
                    </button>

                    @if (Tabs.MoreOpen)
                    {
                        <div class="xf-more-backdrop" @onclick="CloseMoreAndReturnFocus"></div>
                        <div id="xf-more" class="xf-more">
                            <button type="button" class="xf-more-item" @ref="_firstMoreItem"
                                    aria-current="@(Tabs.Tab == AppTab.Accounts ? "page" : null)"
                                    @onclick="() => ChooseFromMore(AppTab.Accounts)">Accounts</button>
                            <button type="button" class="xf-more-item"
                                    aria-current="@(Tabs.Tab == AppTab.Settings ? "page" : null)"
                                    @onclick="() => ChooseFromMore(AppTab.Settings)">Settings</button>
                        </div>
                    }
                </nav>
            </div>
```

Note: `@tab` renders the enum member's name — exactly the labels the old buttons showed (Keys, Publish, Settings, Accounts, Read) plus the new "History".

- [ ] **Step 3: Add the shell's code**

Inside `@code`, after the existing fields:

```csharp
    private static readonly AppTab[] RailTabs = [AppTab.Publish, AppTab.Read, AppTab.History, AppTab.Keys, AppTab.Accounts, AppTab.Settings];
    private static readonly AppTab[] TabbarTabs = [AppTab.Publish, AppTab.Read, AppTab.History, AppTab.Keys];

    private ElementReference _moreButton;
    private ElementReference _firstMoreItem;
    private ElementReference _content;
    private bool _focusFirstMoreItem;
    private bool _focusMoreButton;
    private bool _focusContent;
    private bool _watchingWidth;
    private DotNetObjectReference<Shell>? _self;

    private static readonly RenderFragment Mark =
        @<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h8l4 4v14H6z" stroke="#64CF95" stroke-width="2"/><path d="M14 3v4h4" stroke="#64CF95" stroke-width="2"/><circle cx="12" cy="15" r="2" fill="#EBAF8E"/></svg>;

    private static readonly RenderFragment LockIcon =
        @<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>;

    private static readonly RenderFragment MoreIcon =
        @<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></svg>;

    private static RenderFragment Icon(AppTab tab) => tab switch
    {
        AppTab.Publish => @<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V4"/><path d="M7 9l5-5 5 5"/><path d="M4 20h16"/></svg>,
        AppTab.Read => @<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v12"/><path d="M7 11l5 5 5-5"/><path d="M4 20h16"/></svg>,
        AppTab.History => @<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 8v4l3 2"/><circle cx="12" cy="12" r="9"/></svg>,
        AppTab.Keys => @<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="8" cy="15" r="4"/><path d="M11 12l9-9"/><path d="M17 6l3 3"/></svg>,
        AppTab.Accounts => @<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="6" width="18" height="13" rx="2"/><path d="M3 10h18"/><path d="M16 15h2"/></svg>,
        _ => @<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1"/></svg>,
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_watchingWidth && _ready && Model.Stage == UnlockStage.Unlocked)
        {
            _watchingWidth = true;
            _self = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("xfShell.watchWidth", _self);
        }

        if (_focusFirstMoreItem)
        {
            _focusFirstMoreItem = false;
            await _firstMoreItem.FocusAsync();
        }

        if (_focusMoreButton)
        {
            _focusMoreButton = false;
            await _moreButton.FocusAsync();
        }

        if (_focusContent)
        {
            _focusContent = false;
            await _content.FocusAsync();
        }
    }

    /// <summary>
    /// Choosing Accounts or Settings removes the panel and the button that had focus with it; focus
    /// goes to the screen that was just opened rather than falling to the page body.
    /// </summary>
    private void ChooseFromMore(AppTab tab)
    {
        Tabs.Show(tab);
        _focusContent = true;
    }

    /// <summary>Called by shell.js when the window widens past the phone layout, where More has no place.</summary>
    [JSInvokable]
    public void OnWideLayout() => Tabs.CloseMore();

    private void ToggleMore()
    {
        Tabs.ToggleMore();
        _focusFirstMoreItem = Tabs.MoreOpen;
    }

    private void CloseMoreAndReturnFocus()
    {
        Tabs.CloseMore();
        _focusMoreButton = true;
    }

    private void OnTabbarKey(KeyboardEventArgs e)
    {
        if (Tabs.MoreOpen && string.Equals(e.Key, "Escape", StringComparison.Ordinal))
        {
            CloseMoreAndReturnFocus();
        }
    }
```

In `Dispose()`, after `Tabs.Changed -= OnTabChanged;`, add `_self?.Dispose();`. If the component lacks `@using Microsoft.JSInterop` / `@using Microsoft.AspNetCore.Components.Web` (check `_Imports.razor`), add the missing one at the top of `Shell.razor`.

- [ ] **Step 4: Publish stops drawing History**

In `app/XrplFile.App/Components/Publish.razor` delete the line `    <History />` (line 71). `<Unfinished />` stays.

- [ ] **Step 5: Build, run App.Core tests**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug`
Expected: Build succeeded, 0 warnings.
Run: `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App/Components/Shell.razor app/XrplFile.App/Components/Publish.razor app/XrplFile.App/wwwroot/shell.js
git commit -m "feat(app): a navigation rail on wide windows and a tab bar with More on narrow ones"
```

---

### Task 4: The Publish and History screens

**Files:**
- Modify: `app/XrplFile.App/Components/Publish.razor`, `Estimate.razor`, `Run.razor`, `Unfinished.razor`, `History.razor`

**Interfaces:**
- Consumes: Task 1's `xf-page xf-title xf-card xf-kv-table xf-actionbar xf-mono` classes.

No text, binding or condition changes. Every change below is a class swap, a wrapper, or a move of an existing element within its own screen.

- [ ] **Step 1: `Publish.razor`**
  - `<div class="container py-3">` → `<div class="xf-page">`.
  - `<h1 class="h3">Publish a file</h1>` → `<h1 class="xf-title">Publish a file</h1>`.
  - Wrap the "Choose a file…" button, the `@if (Model.FileName is not null)` paragraph and the `<ul class="list-group my-3">…</ul>` together in `<section class="xf-card">…</section>`; change the list's class to `list-group mt-3`.
  - Each key row `<li class="list-group-item @(…active…)" @onclick="…">` becomes `<button type="button" class="list-group-item list-group-item-action @(…active…)" @onclick="…">` with the same content and the same `@onclick`, closing with `</button>`; the `<ul>` becomes `<div class="list-group mt-3">`. (Today the rows are not keyboard-reachable; CloseAccount's candidate list already uses this exact shape.) Inside each row, `@key.Address` becomes `<span class="xf-mono">@key.Address</span>`.
  - The Estimate button is **not** pinned (spec section 5: the picker is followed by Unfinished runs on the same scroll, so a sticky bar would ride over them); leave it where it is.

- [ ] **Step 2: `Estimate.razor`**
  - `<div class="container py-3">` → `<div class="xf-page">`; `<h1 class="h3">Estimate</h1>` → `<h1 class="xf-title">Estimate</h1>`.
  - The opening paragraph's `<code>@Request.Account</code>` → `<code class="xf-mono">@Request.Account</code>`.
  - Inside `@if (Model.Estimate is { } estimate)` — so no empty card renders while estimating — wrap the `<table class="table">…</table>` and the `<p>Node: …</p>` that follows it in `<section class="xf-card">…</section>`; the table's class becomes `table xf-kv-table`; `<code>@Model.NodeUrl</code>` → `<code class="xf-mono">@Model.NodeUrl</code>`; the Node paragraph gains `class="mt-3 mb-0 xf-muted"`.
  - Wrap the Back and Publish buttons in `<div class="xf-actionbar">…</div>`.

- [ ] **Step 3: `Run.razor`**
  - `<div class="container py-3">` → `<div class="xf-page">`; `<h1 class="h3">Publishing</h1>` → `<h1 class="xf-title">Publishing</h1>`.
  - Wrap `<p>@view.StageText</p>` and the `<div class="progress" …>…</div>` in `<section class="xf-card">…</section>`.
  - Move the Cancel button out from under the progress bar to the end of the screen, inside a new `<div class="xf-actionbar">` that also holds the Dismiss button. The Dismiss button keeps its `@if (view.IsFinished)` condition — move the button, not the condition: the actionbar is `<div class="xf-actionbar">` + Cancel + `@if (view.IsFinished) { <button … Dismiss … /> }` + `</div>`, placed after the last `@if (view.StateNote …)` block and outside the `@if (view.IsFinished)` that holds the alerts. Remove `mt-3` from both buttons.
  - The link's `<code class="d-block p-2 bg-body-tertiary text-break user-select-all">` gains `xf-mono`.

- [ ] **Step 4: `Unfinished.razor`**
  - `<div class="container py-3">` → `<div class="xf-page pt-0">` (it continues the Publish page above it on the same scroll, so it carries no top padding of its own).
  - `<h2 class="h5">Unfinished runs</h2>` → `<h2 class="xf-section-title">Unfinished runs</h2>`.
  - Wrap `<table class="table align-middle">…</table>` in `<section class="xf-card">…</section>`.
  - `<code>@row.Path</code>` → `<code class="xf-mono">@row.Path</code>`.

- [ ] **Step 5: `History.razor`**
  - `<div class="container py-3">` → `<div class="xf-page">`.
  - `<h2 class="h5 mb-0">Published files</h2>` → `<h1 class="xf-title mb-0">Published files</h1>` (it now heads its own tab).
  - The header `<div class="d-flex justify-content-between align-items-center">` gains `mb-3`.
  - Wrap `<table class="table align-middle">…</table>` in `<section class="xf-card">…</section>`.
  - `<code class="text-break">@record.Link</code>` → `<code class="xf-mono">@record.Link</code>`.

- [ ] **Step 6: Build and test**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug` — Expected: 0 warnings.
Run: `dotnet run --project tests/XrplFile.App.Core.Tests` — Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App/Components/Publish.razor app/XrplFile.App/Components/Estimate.razor app/XrplFile.App/Components/Run.razor app/XrplFile.App/Components/Unfinished.razor app/XrplFile.App/Components/History.razor
git commit -m "feat(app): publishing and history screens take the dark theme"
```

---

### Task 5: The Read screen

**Files:**
- Modify: `app/XrplFile.App/Components/Read.razor`

- [ ] **Step 1: Reading (`ReadState.Running`)**
  - `<div class="container py-3">` → `<div class="xf-page">`; `<h1 class="h3">Reading</h1>` → `<h1 class="xf-title">Reading</h1>`.
  - Wrap the two paragraph-and-`<progress>` pairs in `<section class="xf-card">…</section>`.
  - Wrap the Cancel button in `<div class="xf-actionbar">…</div>` and remove its `mt-3`.

- [ ] **Step 2: Finished (succeeded, failed or cancelled)**
  - `<div class="container py-3">` → `<div class="xf-page">`; `<h1 class="h3">Reading</h1>` → `<h1 class="xf-title">Reading</h1>`.
  - Wrap the Dismiss button in `<div class="xf-actionbar">…</div>` and remove its `mt-3`.

- [ ] **Step 3: Link entry**
  - `<div class="container py-3">` → `<div class="xf-page">`; `<h1 class="h3">Read a file</h1>` → `<h1 class="xf-title">Read a file</h1>`.
  - Wrap the `<div class="mb-3">` field group and the look-up button in `<section class="xf-card">…</section>`; the input gains `xf-mono` (`class="form-control xf-mono"`).
  - The manifest block `<div class="mt-3">` becomes `<section class="xf-card">`; inside it, `Written by @manifest.Writer` → `Written by <span class="xf-mono">@manifest.Writer</span>` and `Node: @lookup.NodeUrl` → `Node: <span class="xf-mono">@lookup.NodeUrl</span>`. The Read button stays after the card.

- [ ] **Step 4: Build and test**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug` — Expected: 0 warnings.
Run: `dotnet run --project tests/XrplFile.App.Core.Tests` — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App/Components/Read.razor
git commit -m "feat(app): the read screen takes the dark theme"
```

---

### Task 6: Keys and Accounts screens

**Files:**
- Modify: `app/XrplFile.App/Components/KeyList.razor`, `AddKey.razor`, `ExportKey.razor`, `RevealSeed.razor`, `Damage.razor`, `Accounts.razor`, `NewAccount.razor`, `Funding.razor`, `CloseAccount.razor`

For each file, the same rules (spec section 5's mechanical table):

- [ ] **Step 1: Page roots and titles**
  - `KeyList.razor`: `<div class="container py-3" style="max-width: 760px;">` → `<div class="xf-page">`; `<h1 class="h3 mb-0">Keys</h1>` → `<h1 class="xf-title mb-0">Keys</h1>`.
  - `Accounts.razor`: `<div class="container py-3" style="max-width: 900px;">` → `<div class="xf-page">`; `<h1 class="h3 mb-0">Accounts</h1>` → `<h1 class="xf-title mb-0">Accounts</h1>`.
  - `AddKey.razor`, `ExportKey.razor`, `RevealSeed.razor`, `NewAccount.razor`, `Funding.razor`, `CloseAccount.razor`: every `<h1 class="h3">` → `<h1 class="xf-title">` (they render inside KeyList's or Accounts' page and have no root container of their own).

- [ ] **Step 2: Cards**
  - `KeyList.razor` and `Accounts.razor`: wrap each `<table class="table align-middle">…</table>` in `<section class="xf-card">…</section>`.
  - `AddKey.razor`: the Problem `alert-danger` stays above, outside any card. The card opens immediately before the first mode-specific block (the first `@if`/`else if` that tests `_mode`) and closes immediately after the last one ends — so each mode's field groups, its button, the container-keys `<table class="table">` and the preview `alert-info` are inside it. The "Back to keys" `btn-link` stays after the card.
  - `ExportKey.razor` and `NewAccount.razor`: the card opens before the first `<div class="mb-3">` field group and closes right after the submit button (`Create backup` / `Create`); the `btn-link` that follows it ("Skip for now" / "Back to accounts") stays outside, after the card.
  - `CloseAccount.razor`: the card opens before the `<div class="mb-3">` holding "Send the remainder to" and closes right after the preflight (`btn-outline-primary`) button. The preflight `<table class="table mb-2">` stays where it is, inside its `alert-info`, and its class becomes `table mb-2 xf-kv-table`.
  - `Funding.razor`: wrap the address paragraph and its Copy button in `<section class="xf-card">…</section>`.

- [ ] **Step 3: Monospace values**
  - Every `<code>` around an address, reference or hash in these files gains `xf-mono`: KeyList (3), AddKey (4: lines 98, 115, 141, 148 today), Accounts (1), Funding (1), CloseAccount (1), Damage (1). `RevealSeed.razor`'s `<code class="d-block p-2 bg-body-tertiary user-select-all">` gains `xf-mono`.
  - Addresses written as plain text get `<span class="xf-mono">…</span>` around the expression, nothing else changed: `Accounts.razor` `@closure.Destination` (line 107 today); `CloseAccount.razor` the destination, the account address, each candidate address and the `<strong>` destination (lines 53, 68, 92, 112 today — inside `<strong>`, the span goes inside it).

`Damage.razor` is not a page: it is a warning banner KeyList renders at its top. It keeps its `alert-warning`; only its `<code>` changes (above).

- [ ] **Step 4: Build and test**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug` — Expected: 0 warnings.
Run: `dotnet run --project tests/XrplFile.App.Core.Tests` — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App/Components/KeyList.razor app/XrplFile.App/Components/AddKey.razor app/XrplFile.App/Components/ExportKey.razor app/XrplFile.App/Components/RevealSeed.razor app/XrplFile.App/Components/Damage.razor app/XrplFile.App/Components/Accounts.razor app/XrplFile.App/Components/NewAccount.razor app/XrplFile.App/Components/Funding.razor app/XrplFile.App/Components/CloseAccount.razor
git commit -m "feat(app): key and account screens take the dark theme"
```

---

### Task 7: Settings and the vault screens

**Files:**
- Modify: `app/XrplFile.App/Components/Settings.razor`, `ChangePassword.razor`, `CreateVault.razor`, `Unlock.razor`, `VaultUnreachable.razor`

- [ ] **Step 1: `Settings.razor`**
  - `<div class="container py-3" style="max-width: 560px;">` → `<div class="xf-page">`; `<h1 class="h3">Settings</h1>` → `<h1 class="xf-title">Settings</h1>`.
  - Each `<section class="mb-4">` → `<section class="xf-card">`; each `<h2 class="h5">` inside them → `<h2 class="xf-section-title">`.
  - `<code class="d-block p-2 bg-body-tertiary text-break">` gains `xf-mono`.

- [ ] **Step 2: `ChangePassword.razor`**
  - `<h1 class="h3">` → `<h1 class="xf-title">`; wrap the form's two `<div class="mb-3">` groups and its submit button in `<section class="xf-card">…</section>` (inside the `<form>`, so submission is unchanged).

- [ ] **Step 3: The vault screens**
  - `CreateVault.razor`, `Unlock.razor`: `<div class="container py-4" style="max-width: 480px;">` → `<div class="xf-vault"><section class="xf-card">`, and the matching closing `</div>` → `</section></div>`; `<h1>` → `<h1 class="xf-title">`.
  - `VaultUnreachable.razor`: the same root change; `<h1>` → `<h1 class="xf-title">`; `<h2 class="h5">Start fresh instead</h2>` → `<h2 class="xf-section-title mt-4">Start fresh instead</h2>`.

- [ ] **Step 4: Build and test**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug` — Expected: 0 warnings.
Run: `dotnet run --project tests/XrplFile.App.Core.Tests` — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App/Components/Settings.razor app/XrplFile.App/Components/ChangePassword.razor app/XrplFile.App/Components/CreateVault.razor app/XrplFile.App/Components/Unlock.razor app/XrplFile.App/Components/VaultUnreachable.razor
git commit -m "feat(app): settings and vault screens take the dark theme"
```

---

### Task 8: Verification in the MauiLab VM

**Files:**
- Create: `docs/superpowers/reports/2026-10-13-dark-theme/` (screenshots and `report.md`)
- Scratch only (never committed): the CDP helper `vm-cdp.ps1` below, in your scratchpad directory

**Interfaces:**
- Consumes: everything above. The MauiLab lab at `D:\Lab\maui-vm-lab` — read the `maui-vm-lab` skill first. From it: `06-deploy-and-run.ps1`, `08-vm-screen.ps1`, PowerShell Direct (`Invoke-Command -VMName MauiLab -Credential …`, credentials as the skill shows), WebView2 DevTools on `http://mauilab.mshome.net:9222`.
- Widths are produced with CDP `Emulation.setDeviceMetricsOverride` inside the app's own WebView: it changes the layout viewport and fires `matchMedia` changes exactly as a resize does, so it exercises the breakpoints and `shell.js`. Nothing in the lab resizes the native window; this is the width mechanism that counts for this task. An emulated width lasts only while its CDP connection is open, which is why the helper sets the width, runs the checks and takes the screenshot in one connection.

- [ ] **Step 1: Deploy, and launch non-elevated with DevTools**

On the host:

```powershell
cd D:\Lab\maui-vm-lab
.\06-deploy-and-run.ps1 -ProjectPath E:\GIT\XRPL\xrpl-file\app\XrplFile.App\XrplFile.App.csproj -ExeName XrplFile.App.exe -Action Deploy
```

File pickers fail in an elevated process, so the app must run non-elevated, and DevTools must still be on. Through PowerShell Direct, in the guest:

```powershell
[Environment]::SetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', '--remote-debugging-port=9222', 'User')
$exe = (Get-ChildItem C:\Lab -Recurse -Filter XrplFile.App.exe | Select-Object -First 1).FullName
$action = New-ScheduledTaskAction -Execute $exe
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName XrplPlain -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName XrplPlain
```

(The environment variable is the user's, so the interactive session's process inherits it; if `http://mauilab.mshome.net:9222/json` answers nothing after ~40 s of cold start, sign the lab user out and in once — autologon — so the new variable is in the session, then start the task again.) Unlock the vault (`labtestvault1`, a throwaway lab value). In Settings › Node URLs set network `1` to the stand's address as `docker ps` shows `rippled-service` bound on the Default Switch (today `ws://172.29.240.1:6006`).

- [ ] **Step 2: Write the CDP helper (scratch)**

Save as `vm-cdp.ps1` in your scratchpad:

```powershell
param(
    [int]$Width = 0,
    [int]$Height = 800,
    [string[]]$Eval = @(),
    [string]$Screenshot = ''
)

$page = (Invoke-RestMethod 'http://mauilab.mshome.net:9222/json') | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
$script:next = 0

function Invoke-Cdp([string]$method, [hashtable]$params) {
    $script:next++
    $id = $script:next
    $json = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
    while ($true) {
        $buffer = New-Object byte[] 4194304
        $text = New-Object Text.StringBuilder
        do {
            $result = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
            [void]$text.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
        } while (-not $result.EndOfMessage)
        $message = $text.ToString() | ConvertFrom-Json
        if ($message.id -eq $id) { return $message }
    }
}

if ($Width -gt 0) {
    [void](Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = $Width; height = $Height; deviceScaleFactor = 0; mobile = $false })
    Start-Sleep -Milliseconds 400
}
foreach ($expression in $Eval) {
    $answer = Invoke-Cdp 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true; awaitPromise = $true }
    $answer.result.result.value | ConvertTo-Json -Depth 10 -Compress
}
if ($Screenshot) {
    $shot = Invoke-Cdp 'Page.captureScreenshot' @{ format = 'png' }
    [IO.File]::WriteAllBytes($Screenshot, [Convert]::FromBase64String($shot.result.data))
}
$ws.Dispose()
```

Check it: `.\vm-cdp.ps1 -Eval 'document.title'` prints the page title.

- [ ] **Step 3: Produce the states and screenshot each at 1280 px**

Screenshots go to `docs/superpowers/reports/2026-10-13-dark-theme/NN-<state>-1280.png` via `.\vm-cdp.ps1 -Width 1280 -Screenshot <path>`. Drive the app with mouse and keyboard through the lab's interactive tooling, or with `-Eval` clicks (`document.querySelector('…').click()`). States and how to reach them:

| State | How |
|---|---|
| Keys, Add key (three modes), Export key, Reveal seed | Keys tab; Add key; each tab; a key's Export / Reveal buttons |
| Accounts, New account, Funding | Accounts tab; New account; the new row's Fund |
| Close account (preflight table) | fund the throwaway account (below), then Close, pick a destination, Check |
| Publish picker, Estimate | Publish; choose a ≥ 98,001-byte file; select a funded key; Estimate |
| Estimate with its message | the same with a key whose account is unfunded |
| Run running, Run finished | Publish a 3 MB file; screenshot while running and after |
| Unfinished runs | start a 3 MB publication, Cancel it, back to Publish |
| Read entry with a manifest, Read reading, Read finished | Read; paste the link from Run; look up; Read |
| History | History tab after the publication |
| Settings, Change password | Settings; Change password |
| Unlock | the rail's Lock |
| Create vault | close the app; in the guest rename the vault file (path shown in Settings) to `vault.json.bak`; start XrplPlain; screenshot; close; rename back |
| Vault unreachable | Settings › A different vault: choose a copy of the vault file; close the app; delete the copy; start XrplPlain |
| Damage | not reproducible without corrupting a vault file — record "not covered" in the report |

Funding on the standalone stand: pay 1,000 XRP from the stand's genesis account (seed `snoPBrXtMeMyMHUVTgbuqAfg1SUTb`, a public test value of every standalone rippled) to the address the Funding screen shows, with a throwaway host-side console (`dotnet new console` in your scratchpad, package `Xrpl`, `XrplWallet.FromSeed`, a `Payment` submitted with `ws://localhost:6006`) — never from the app.

- [ ] **Step 4: 900 px and 600 px, and Review Focus 1 and 3**

For Publish/estimate, Run (finished, with its notes), Read finished, Keys, History and Unlock, at 900 and at 600:

```powershell
.\vm-cdp.ps1 -Width 600 -Eval "[document.scrollingElement.scrollWidth <= document.scrollingElement.clientWidth, document.getElementById('xf-content') ? document.getElementById('xf-content').scrollWidth <= document.getElementById('xf-content').clientWidth : true]" -Screenshot <path>-600.png
```

Expected: `[true,true]` everywhere (Review Focus 1). On Estimate and Run at 600, also run `document.getElementById('xf-content').scrollTop = 1e6` before the screenshot: the last alert sits fully above the action bar (Review Focus 3). At 600 also screenshot the More panel open (`document.querySelector('.xf-tabbar [aria-controls]').click()`).

- [ ] **Step 5: Review Focus 2 — focus survives the 720 px line**

In one connection: width 1280, focus the current rail item, then width 600, then read the focus:

```powershell
.\vm-cdp.ps1 -Width 1280 -Eval "document.querySelector('.xf-rail-item[aria-current=page]').focus(); document.activeElement.className"
```

then, without closing between steps (extend the helper's `-Eval` list if needed, or call `Emulation.setDeviceMetricsOverride` again inside one run):

```powershell
.\vm-cdp.ps1 -Width 1280 -Eval "document.querySelector('.xf-rail-item[aria-current=page]').focus(); 'focused'", "new Promise(r => setTimeout(r, 50))" ; .\vm-cdp.ps1 -Width 600 -Eval "new Promise(r => setTimeout(() => r(document.activeElement.id), 300))"
```

Expected: `"xf-content"`. If the second connection starts from the default width and the media change is not seen, put both widths in one run by adding a `-Width2` parameter to the helper that applies a second `setDeviceMetricsOverride` after the first `-Eval` list.

- [ ] **Step 6: Review Focus 4 — no stock Bootstrap blue, in focus states too**

On Publish (a key selected), Settings (the device switch both on and off), Add key (its tabs) and Estimate:

```powershell
.\vm-cdp.ps1 -Width 1280 -Eval "(() => { const bad = /13, 110, 253|134, 183, 254|49, 132, 253|110, 168, 254|%2386b7fe|%230d6efd/i; const hits = []; for (const el of document.querySelectorAll('button, input, select, textarea, a, .list-group-item, .nav-link')) { el.focus(); const s = getComputedStyle(el); for (const p of ['color', 'backgroundColor', 'borderTopColor', 'outlineColor', 'boxShadow', 'backgroundImage']) { if (bad.test(s[p])) { hits.push(p + ' ' + el.outerHTML.slice(0, 100)); } } } return hits; })()"
```

Expected: `[]`. A hit is a missing override: add it to the matching section of `app.css`, rebuild, redeploy and re-run. (`:active` cannot be computed this way; it is covered by `--bs-btn-focus-box-shadow: none` and the per-variant `--bs-btn-active-*` values.)

- [ ] **Step 7: Review Focus 5 — Windows text size 150%**

Record `.\vm-cdp.ps1 -Eval "[getComputedStyle(document.documentElement).fontSize, devicePixelRatio]"`. In the guest set `HKCU:\Software\Microsoft\Accessibility` `TextScaleFactor` (DWORD) to `150`, restart the VM (autologon restores the session), start XrplPlain, unlock, and record the same values. If they did not change, WebView2 does not apply Windows text size to the page: report Review Focus 5 as **N/A**, not pass. If they changed, screenshot Keys and Publish at 1280 and 600: rail labels unclipped, tab bar fully visible. Set the value back to `100` and restart.

- [ ] **Step 8: Keyboard pass and contrast gate**

With the mouse untouched (keyboard into the VM, or `-Eval` with `dispatchEvent` of `KeyboardEvent('keydown', {key:'Tab'})` is not reliable — use the lab's interactive keyboard input): Tab through the shell at 1280 and at 600 (emulated): every control shows the mint ring; at 600 More opens with Enter, focus lands on Accounts, Escape closes it and focus returns to More; choosing Settings from More puts focus on the content region. Then run the `ui-a11y-gates` skill against `app/XrplFile.App/wwwroot/app.css` and the rendered hover, disabled and banner states.

- [ ] **Step 9: Write the report and commit**

`docs/superpowers/reports/2026-10-13-dark-theme/report.md`: one row per check above (pass / fail / N/A, with the screenshot name), the `-Eval` outputs verbatim, the a11y gate's findings, every state not covered and why (Damage), and anything fixed during this task with its commit.

```bash
git add docs/superpowers/reports/2026-10-13-dark-theme
git commit -m "docs(report): the dark theme checked in the VM at three widths"
```
