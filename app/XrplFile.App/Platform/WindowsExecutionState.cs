using System.Runtime.InteropServices;

using XrplFile.App.Core;

namespace XrplFile.App.Platform;

/// <summary><c>SetThreadExecutionState</c> for <see cref="ThreadKeepAwake"/>'s thread (spec S13).</summary>
internal sealed class WindowsExecutionState : IExecutionState
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>The previous state on success; zero when Windows refused.</summary>
    public bool Require() => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;

    public void Clear() => SetThreadExecutionState(EsContinuous);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
