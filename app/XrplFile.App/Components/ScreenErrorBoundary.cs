using Microsoft.AspNetCore.Components.Web;

namespace XrplFile.App.Components;

/// <summary>
/// The boundary around the open screen. Without one, BlazorWebView treats an exception escaping a
/// component as fatal: it shows its error bar and stops handling events, so the window freezes on
/// whatever it last drew until the application is restarted, and nothing reaches the log. Inside
/// this boundary the failure is written to <see cref="StartupLog"/> and only the screen is replaced;
/// Shell.razor calls <c>Recover</c> when a tab is chosen.
/// </summary>
public sealed class ScreenErrorBoundary : ErrorBoundary
{
    protected override Task OnErrorAsync(Exception exception)
    {
        StartupLog.Write("Screen failed", exception);
        return Task.CompletedTask;
    }
}
