namespace XrplFile.App.Core;

/// <summary>
/// The first half of reading (design 4.1): the link, and what the ledger says the file is. The
/// manifest costs one transaction lookup and carries the name, the size and the type, so a person
/// sees what they are about to download before any of it moves (design H1).
/// </summary>
public sealed class ReadModel(IConnectionFactory connections, INodeSettingsStore nodes)
{
    /// <summary>
    /// The largest file this application reads — <see cref="ReaderOptions.MaxBytes"/>'s own default.
    /// With the file streamed to disk rather than held in memory this is policy rather than
    /// architecture (design H7): what argues against raising it is the node, not this application.
    /// Two hundred megabytes is roughly two hundred thousand requests.
    /// </summary>
    public const long MaxBytes = 20_000_000;

    private long _ceiling = MaxBytes;

    public string Link { get; set; } = string.Empty;

    public XrplFileLink? Parsed { get; private set; }

    public ManifestRecord? Manifest { get; private set; }

    /// <summary>The node the manifest came from, which is also the node the blocks will come from.</summary>
    public string? NodeUrl { get; private set; }

    public string? Message { get; private set; }

    public bool CanRead => Manifest is not null && Message is null;

    /// <summary>The ceiling, so a size refusal can be tested without publishing a large file.</summary>
    internal void SetCeilingForTests(long ceiling) => _ceiling = ceiling;

    public void Clear()
    {
        Parsed = null;
        Manifest = null;
        NodeUrl = null;
        Message = null;
    }

    /// <summary>
    /// Parses the link, finds the node for its own network, and fetches the manifest. Sets
    /// <see cref="Message"/> and leaves <see cref="CanRead"/> false when any of that cannot be done.
    /// </summary>
    public async Task LookUpAsync(CancellationToken cancellationToken)
    {
        Clear();

        if (!XrplFileLink.TryParse(Link, out XrplFileLink? parsed))
        {
            Message = ReadErrorMessages.For(XrplFileError.InvalidLink);
            return;
        }

        Parsed = parsed;

        // The link names its own network, so nothing else is asked and nothing else may disagree
        // (design H9).
        if (NodeEndpoint.Resolve(nodes, parsed.Network) is null)
        {
            Message =
                $"No node URL is known for the network '{parsed.Network}'. Add one in Settings.";
            return;
        }

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(parsed.Network, cancellationToken).ConfigureAwait(false);

            NodeUrl = connection.Url;

            XrplFileReader reader = new(connection.Source, new ReaderOptions { MaxBytes = _ceiling });
            ManifestRecord record = await reader.ManifestAsync(parsed, cancellationToken).ConfigureAwait(false);

            Manifest = record;

            // Refused here rather than in the run, so the person is told before they agree rather
            // than after they have waited.
            if (record.Manifest.Size > _ceiling)
            {
                Message = ReadErrorMessages.For(XrplFileError.TooLarge);
            }
        }
        catch (XrplFileException ex)
        {
            Message = ReadErrorMessages.For(ex);
        }
    }
}
