using Microsoft.AspNetCore.Components;
using XrplFile.App.Core.Localization;

namespace XrplFile.App.Components;

/// <summary>
/// The base of every screen (set in _Imports.razor): <see cref="T"/> is the catalog Shell cascades, so
/// a changed language re-renders every screen that reads it, with its component state kept.
/// </summary>
public abstract class LocalizedComponent : ComponentBase
{
    [Inject]
    private Localizer Localizer { get; set; } = default!;

    [CascadingParameter]
    private TextCatalog? Cascaded { get; set; }

    /// <summary>The current language's phrases. Shell, above the cascade, reads the service itself.</summary>
    protected TextCatalog T => Cascaded ?? Localizer.Text;
}
