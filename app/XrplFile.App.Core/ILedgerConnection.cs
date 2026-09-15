namespace XrplFile.App.Core;

/// <summary>
/// One open connection to a node, as the two halves the library asks for. Disposing it closes the
/// connection: every screen that connects owns its own and disposes it when it is done (design 5),
/// so a socket idle since an estimate made ten minutes ago is never the one a publication starts on.
/// </summary>
public interface ILedgerConnection : IAsyncDisposable
{
    ILedgerSource Source { get; }

    ILedgerSink Sink { get; }

    /// <summary>The URL this connection went to, so a screen can name it before money is spent.</summary>
    string Url { get; }
}
