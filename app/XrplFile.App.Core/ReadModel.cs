namespace XrplFile.App.Core;

/// <summary>
/// The first half of reading (design 4.1): the link, and what the ledger says the file is. The
/// manifest costs one transaction lookup and carries the name, the size and the type, so a person
/// sees what they are about to download before any of it moves (design H1).
/// </summary>
public sealed class ReadModel(IConnectionFactory connections, INodeSettingsStore nodes)
{
    /// <summary>
    /// The largest file this application reads — read from <see cref="ReaderOptions.MaxBytes"/>'s
    /// own default rather than copied as a literal, so this and <see cref="ReadRun"/> (which
    /// constructs its own <see cref="ReaderOptions"/> and reads the same property) cannot drift
    /// apart the way a hard-coded copy of the same number already had within this one part. With the
    /// file streamed to disk rather than held in memory this is policy rather than architecture
    /// (design H7): what argues against raising it is the node, not this application. Two hundred
    /// megabytes is roughly two hundred thousand requests.
    /// </summary>
    public static readonly long MaxBytes = new ReaderOptions().MaxBytes;

    private static readonly ReadLookupSnapshot Empty = new(null, null, null, null);

    private readonly Lock _gate = new();

    private long _ceiling = MaxBytes;
    private string _link = string.Empty;
    private ReadLookupSnapshot _snapshot = Empty;

    /// <summary>
    /// Bumped by anything that makes an in-flight <see cref="LookUpAsync"/> answer stale: the
    /// <see cref="Link"/> setter actually changing the text, and <see cref="Clear"/>. A lookup pins
    /// this value at its own start and only publishes its result if it is still current when the
    /// node answers — otherwise the manifest it fetched is for a link nobody is looking at any more,
    /// and is dropped rather than written over whatever the change already left behind.
    /// </summary>
    private int _generation;

    /// <summary>Whether a lookup is currently running, guarding against a second one starting.</summary>
    private bool _lookingUp;

    public string Link
    {
        get
        {
            lock (_gate) return _link;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (_gate)
            {
                // Re-assigning the identical text is not a change: a component that reflects a
                // two-way-bound text box back onto this property on every keystroke must not blank
                // a manifest that is already loaded just because nothing actually moved.
                if (string.Equals(_link, value, StringComparison.Ordinal)) return;

                _link = value;
                InvalidateLocked();
            }
        }
    }

    /// <summary>
    /// One consistent view of the last lookup (design 4.1). A caller takes this once rather than
    /// reading <see cref="Parsed"/>, <see cref="Manifest"/>, <see cref="NodeUrl"/> and
    /// <see cref="Message"/> one property at a time — <see cref="LookUpAsync"/> publishes all four
    /// together, from a thread-pool continuation, and separate unsynchronised reads of them could
    /// otherwise land on different instants of the same or even different lookups. See
    /// <see cref="ReadLookupSnapshot"/>'s own remarks.
    /// </summary>
    public ReadLookupSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// Whether <see cref="LookUpAsync"/> is currently running. Read.razor's "Look up" button disables
    /// itself off this, the same way it already disables ReadRun's Save button off
    /// <see cref="ReadSnapshot.Saving"/> — a second click while one is already in flight does nothing
    /// today (<see cref="LookUpAsync"/> refuses it outright), which without this flag a person had no
    /// way to tell apart from the button simply not working.
    /// </summary>
    public bool LookingUp
    {
        get { lock (_gate) return _lookingUp; }
    }

    public XrplFileLink? Parsed => Snapshot.Parsed;

    public ManifestRecord? Manifest => Snapshot.Manifest;

    public string? NodeUrl => Snapshot.NodeUrl;

    public string? Message => Snapshot.Message;

    public bool CanRead => Snapshot.CanRead;

    /// <summary>The ceiling, so a size refusal can be tested without publishing a large file.</summary>
    internal void SetCeilingForTests(long ceiling) => _ceiling = ceiling;

    public void Clear()
    {
        lock (_gate) InvalidateLocked();
    }

    /// <summary>Clears the published snapshot and bumps <see cref="_generation"/>. Call under <see cref="_gate"/>.</summary>
    private void InvalidateLocked()
    {
        _snapshot = Empty;
        ++_generation;
    }

    /// <summary>
    /// Parses the link, finds the node for its own network, and fetches the manifest. Sets
    /// <see cref="Message"/> and leaves <see cref="CanRead"/> false when any of that cannot be done.
    ///
    /// A second call made while one is already running is refused outright — it returns immediately
    /// without touching the node or any field — the same "one at a time" contract
    /// <see cref="PublicationRun.Begin"/> enforces for a publication. Independently of that: this
    /// call's own result is published only if <see cref="Link"/> (or an explicit <see cref="Clear"/>)
    /// has not moved on since this call started, so a lookup a person has effectively abandoned by
    /// typing over it cannot land its answer on a screen already showing something else.
    /// </summary>
    public async Task LookUpAsync(CancellationToken cancellationToken)
    {
        int generation;
        string link;

        lock (_gate)
        {
            if (_lookingUp) return;

            _lookingUp = true;
            InvalidateLocked();
            generation = _generation;
            link = _link;
        }

        XrplFileLink? parsedResult = null;
        ManifestRecord? manifestResult = null;
        string? nodeUrlResult = null;
        string? messageResult = null;
        bool completed = false;

        try
        {
            if (!XrplFileLink.TryParse(link, out XrplFileLink? parsed))
            {
                messageResult = ReadErrorMessages.For(XrplFileError.InvalidLink);
                completed = true;
                return;
            }

            parsedResult = parsed;

            // The link names its own network, so nothing else is asked and nothing else may
            // disagree (design H9).
            if (NodeEndpoint.Resolve(nodes, parsed.Network) is null)
            {
                messageResult =
                    $"No node URL is known for the network '{parsed.Network}'. Add one in Settings.";
                completed = true;
                return;
            }

            try
            {
                await using ILedgerConnection connection =
                    await connections.ConnectAsync(parsed.Network, cancellationToken).ConfigureAwait(false);

                nodeUrlResult = connection.Url;

                // MaxBytes is inert here: ManifestAsync never consults ReaderOptions.MaxBytes (only
                // ReadAsync does), so passing the ceiling through the constructor buys nothing for
                // this call by itself. It is passed anyway because this is the same reader a later
                // read of the actual blocks would reuse. The explicit size check just below this is
                // what actually refuses an oversized file here — do not remove it on the assumption
                // that the line above already covers it.
                XrplFileReader reader = new(connection.Source, new ReaderOptions { MaxBytes = _ceiling });
                ManifestRecord record = await reader.ManifestAsync(parsed, cancellationToken).ConfigureAwait(false);

                manifestResult = record;

                // Refused here rather than in the run, so the person is told before they agree
                // rather than after they have waited.
                if (record.Manifest.Size > _ceiling)
                {
                    messageResult = ReadErrorMessages.For(XrplFileError.TooLarge);
                }
            }
            catch (XrplFileException ex)
            {
                messageResult = ReadErrorMessages.For(ex);
            }

            completed = true;
        }
        finally
        {
            lock (_gate)
            {
                _lookingUp = false;

                // Nothing else moved the link (or cleared the model) while this call was running:
                // this call's answer is still the one the screen is waiting for. Written as one
                // record rather than four separate field writes, so a reader taking Snapshot under
                // the same lock can never observe three of these fields from this lookup sitting
                // next to a fourth from whatever InvalidateLocked or an earlier lookup left behind.
                if (completed && _generation == generation)
                {
                    _snapshot = new ReadLookupSnapshot(parsedResult, manifestResult, nodeUrlResult, messageResult);
                }
            }
        }
    }
}
