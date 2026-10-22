namespace XrplFile.App.Core;

/// <summary>
/// The cancellation a screen hands to everything it awaits. A screen calls <see cref="End"/> when it
/// is disposed, and the source behind it is cancelled but never disposed.
/// </summary>
/// <remarks>
/// A disposed <see cref="CancellationTokenSource"/> throws <see cref="ObjectDisposedException"/>
/// from its <c>Token</c> property. A lifecycle method that is still awaiting when its screen closes
/// (Funding's first refresh, Publish's key list) reads the token again once it resumes, and that
/// exception escaped the component. BlazorWebView treats an unhandled component exception as fatal:
/// it shows its error bar and stops handling events, so the window froze on whatever it last drew
/// until the application was restarted. A cancelled token instead ends such a method with
/// <see cref="OperationCanceledException"/>, and <c>ComponentBase</c> ignores a cancelled task.
/// The source holds no timer and no wait handle, so leaving it undisposed releases nothing late.
/// </remarks>
public sealed class ScreenLifetime
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>Not cancelled while the screen is open; cancelled, and still readable, once it has ended.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>The screen is gone: cancels whatever it is still awaiting. Safe to call more than once.</summary>
    public void End() => _source.Cancel();
}
