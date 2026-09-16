// So a test file in this project can call PublishAsync unqualified, the way the reading tests
// (and Task 4's differential test, which needs this identical fixture) are written to.
global using static XrplFile.App.Core.Tests.PublishedFileFixture;

using Xrpl.Wallet;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Publishes a real file into a <see cref="FakeConnectionFactory"/>'s fake ledger and hands back
/// its link, so a read test has something true to read. Built from what
/// <c>tests/XrplFile.Core.Tests/PublicationSessionTests.cs</c> already does: a
/// <see cref="SequenceStrategy"/> over the fake sink, a signer, and
/// <see cref="PublicationSession.StartAsync"/> + <see cref="PublicationSession.RunAsync"/>.
///
/// Kept in its own file rather than inlined in a test class: Task 4's differential test needs the
/// identical fixture, and two copies of a publishing helper would drift the way this project has
/// already had to fix three times.
/// </summary>
public static class PublishedFileFixture
{
    /// <summary>What every <see cref="FakeConnectionFactory"/>'s <c>Ledger</c> charges by default
    /// (<c>new NetworkFees(10, ...)</c>). The strategy is priced from it, as a caller must, or
    /// <see cref="PublicationSession.StartAsync"/> refuses the run with <c>FeeMismatch</c>.</summary>
    private const int NodeFeeDrops = 10;

    private static readonly SequenceStrategyOptions StrategyOptions = new() { FeeDrops = NodeFeeDrops };

    /// <summary>
    /// Publishes <paramref name="size"/> deterministic bytes as <paramref name="name"/> of type
    /// <paramref name="type"/>, over <paramref name="connections"/>'s fake ledger, and returns the
    /// link the publication produced. Uses the <c>"testnet"</c> network: the one xrpl-file link
    /// format accepts that also has a built-in node URL, so <see cref="NodeEndpoint.Resolve"/>
    /// answers without a settings file.
    /// </summary>
    public static async Task<XrplFileLink> PublishAsync(
        FakeConnectionFactory connections, string name, string type, long size)
    {
        ArgumentNullException.ThrowIfNull(connections);

        XrplWallet wallet = XrplWallet.Generate();
        connections.Ledger.Balances[wallet.ClassicAddress] = 500_000_000;

        ITransactionSigner signer = new WalletSigner(wallet);
        await using SequenceStrategy strategy = new(connections.Sink, signer, StrategyOptions);

        string stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-read-fixture-").FullName;

        try
        {
            SessionOptions options = new() { StateDirectory = stateDirectory, TicketsPerBatch = 100 };

            PublicationSession session = await PublicationSession.StartAsync(
                strategy, connections.Ledger, VectorStream.Bytes(name, size), type, name,
                wallet.ClassicAddress, "fixture-key", "testnet", options, CancellationToken.None);

            PublicationReport report = await session.RunAsync(null, CancellationToken.None);

            return report.Link;
        }
        finally
        {
            // Matches PublicationSessionTests.Dispose() and ReadModelTests.Dispose() -- state files
            // are per-call scratch, not something a repeated local run should be left to accumulate.
            // Swallowed because this runs in a finally: a delete that fails on a file the operating
            // system still holds would replace whatever the publish above threw, hiding the failure
            // a test was written to diagnose behind an unrelated one from the cleanup.
            try
            {
                if (Directory.Exists(stateDirectory))
                {
                    Directory.Delete(stateDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
