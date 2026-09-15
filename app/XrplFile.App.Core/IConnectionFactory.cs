namespace XrplFile.App.Core;

/// <summary>
/// The application's one seam to a node (design F10). Tests substitute a factory over
/// <c>FakeLedgerSource</c> and <c>FakeLedgerSink</c>, which leaves <see cref="PublicationSession"/>,
/// the strategy choice and the progress reporting inside the tested path — everything except the
/// socket itself.
/// </summary>
public interface IConnectionFactory
{
    /// <summary>
    /// Opens a connection for the given network.
    /// </summary>
    /// <exception cref="XrplFileException">
    /// <see cref="XrplFileError.NodeUnreachable"/> when the network has no URL at all, or when the
    /// node does not answer. Those are the same remedy — a URL to check — and a caller that had to
    /// tell them apart could not act differently on the answer.
    /// </exception>
    Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken);
}
