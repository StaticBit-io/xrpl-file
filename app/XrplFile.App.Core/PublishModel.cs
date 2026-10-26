using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The first publish screen (design 4.1): which file, which key, and every reason those two are not
/// yet enough to ask a node anything.
/// </summary>
public sealed class PublishModel(IKeyVault vault, IFilePickerService picker, INodeSettingsStore nodes, BatchAvailability? batch = null, IPayerSettingsStore? payers = null)
{
    /// <summary>
    /// Read from <see cref="ReaderOptions.MaxBytes"/>'s own default rather than copied as a literal
    /// — round 2's N1/M1 finding: this was still a bare <c>20_000_000</c> after <see cref="ReadModel.MaxBytes"/>
    /// was fixed the same way, and the one test touching it compared it to itself rather than to the
    /// library, so a drift here would have left the suite green. A larger file publishes correctly
    /// and then produces a link the library's reader refuses unless its caller raises the ceiling, so
    /// this is a warning rather than a refusal (design 4.1) — the limit is the reader's default, not
    /// the format's, and the writer's read-back lifts it for itself.
    /// </summary>
    public static readonly long ReaderDefaultMaxBytes = new ReaderOptions().MaxBytes;

    /// <summary>
    /// The largest file this application will load. The whole file is held in memory as
    /// <see cref="ReadOnlyMemory{T}"/> and encoded into memos on top of that, and at roughly a
    /// kilobyte of file per memo this is already tens of thousands of transactions — a run whose
    /// cost stops it long before its size does.
    /// </summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The last entry of "Paid by" (spec 6.1).</summary>
    public const string NobodyLabel = "Nobody — publish with tickets";

    /// <summary>The muted note under "Nobody" (spec 6.1).</summary>
    public const string NobodyNote = "Slower, and holds up to 50 XRP of reserve while publishing.";

    /// <summary>Under "Paid by" when the network's ledger does not enable Batch (spec 6.1).</summary>
    public const string BatchUnavailableNote = "This network does not support Batch, so publishing uses tickets.";

    /// <summary>The writer the current payer was chosen for.</summary>
    private string? _payerChosenFor;

    /// <summary>Whether the person chose the current payer; a default is worked out again on every refresh.</summary>
    private bool _payerChosenByPerson;

    /// <summary>The chosen payer's reference, or null for Nobody.</summary>
    public string? SelectedPayerReference { get; private set; }

    /// <summary>The cached amendment read for the writer's network (spec B14): null when it could not be read.</summary>
    public bool? BatchEnabled { get; private set; }

    /// <summary>Fixed on Nobody: the network does not enable Batch.</summary>
    public bool PayerLocked => BatchEnabled == false;

    /// <summary>
    /// Every other key on the writer's network, compared in canonical form, never the writer's own
    /// account under another reference (spec 6.1, B3). Oldest key first: the vault lists by random
    /// reference, so "the first other key" needs an order of its own to mean the same thing twice.
    /// </summary>
    public IReadOnlyList<VaultEntryInfo> PayerChoices =>
        SelectedKey is { } writer && XrplFileLink.TryNormalizeNetwork(writer.Network, out string? network)
            ? [.. Keys
                .Where(key =>
                    !string.Equals(key.Reference, writer.Reference, StringComparison.Ordinal)
                    && !string.Equals(key.Address, writer.Address, StringComparison.Ordinal)
                    && XrplFileLink.TryNormalizeNetwork(key.Network, out string? other)
                    && string.Equals(other, network, StringComparison.Ordinal))
                .OrderBy(key => key.CreatedAt)
                .ThenBy(key => key.Label, StringComparer.Ordinal)
                .ThenBy(key => key.Reference, StringComparer.Ordinal)]
            : [];

    /// <summary>The payer the run will use, or null for Nobody (a locked drop-down is always Nobody).</summary>
    public VaultEntryInfo? SelectedPayer =>
        PayerLocked || SelectedPayerReference is null
            ? null
            : PayerChoices.FirstOrDefault(key => string.Equals(key.Reference, SelectedPayerReference, StringComparison.Ordinal));

    /// <summary>The muted line under "Paid by", or null.</summary>
    public string? PayerNote => PayerLocked ? BatchUnavailableNote : SelectedPayer is null ? NobodyNote : null;

    /// <summary>
    /// Reads the amendment for the writer's network (cached, spec B14) and picks the default payer
    /// for the selected writer: the payer used last on that network, else the first other key
    /// there, else Nobody (spec 6.1). A payer the person chose for this writer stands.
    /// </summary>
    public async Task RefreshPayerAsync(CancellationToken cancellationToken)
    {
        if (SelectedKey is not { } writer || !XrplFileLink.TryNormalizeNetwork(writer.Network, out string? network))
        {
            SelectedPayerReference = null;
            BatchEnabled = null;
            _payerChosenFor = null;
            _payerChosenByPerson = false;
            return;
        }

        BatchEnabled = batch is null ? null : await batch.IsEnabledAsync(network, cancellationToken).ConfigureAwait(false);

        if (_payerChosenByPerson
            && string.Equals(_payerChosenFor, writer.Reference, StringComparison.Ordinal)
            && (SelectedPayerReference is null || PayerChoices.Any(key => string.Equals(key.Reference, SelectedPayerReference, StringComparison.Ordinal))))
        {
            return;
        }

        IReadOnlyList<VaultEntryInfo> choices = PayerChoices;
        string? last = payers?.LastPayer(network);

        SelectedPayerReference =
            last is not null && choices.Any(key => string.Equals(key.Reference, last, StringComparison.Ordinal)) ? last
            : choices.Count > 0 ? choices[0].Reference
            : null;
        _payerChosenFor = writer.Reference;
        _payerChosenByPerson = false;
    }

    /// <summary>The person's choice in "Paid by"; null or empty is Nobody.</summary>
    public void SelectPayer(string? reference)
    {
        SelectedPayerReference = string.IsNullOrEmpty(reference) ? null : reference;
        _payerChosenFor = SelectedReference;
        _payerChosenByPerson = true;
    }

    /// <summary>
    /// A run with this request has started: its payer becomes the network's default (spec 6.1), and
    /// its file is put away, so the picker a finished run is dismissed back to starts the next
    /// publication empty rather than showing the file just published. The key and the payer stay.
    /// </summary>
    public void RecordStarted(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PayerReference is { } payer) payers?.Remember(request.Network, payer);

        FilePath = null;
        FileName = null;
        FileSize = 0;
        Message = null;
        Warning = null;
    }

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".bin"] = "application/octet-stream",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".gif"] = "image/gif",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".md"] = "text/markdown",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".wasm"] = "application/wasm",
        [".webp"] = "image/webp",
        [".xml"] = "application/xml",
        [".zip"] = "application/zip",
    };

    public IReadOnlyList<VaultEntryInfo> Keys { get; private set; } = [];

    public string? FilePath { get; private set; }

    public string? FileName { get; private set; }

    public long FileSize { get; private set; }

    public string? SelectedReference { get; private set; }

    /// <summary>The chosen key's entry, or <see langword="null"/> when none is chosen.</summary>
    public VaultEntryInfo? SelectedKey =>
        SelectedReference is null
            ? null
            : Keys.FirstOrDefault(key => string.Equals(key.Reference, SelectedReference, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="key"/> is the one currently chosen — the same
    /// <see cref="StringComparison.Ordinal"/> comparison <see cref="SelectedKey"/> itself uses, kept
    /// here rather than left for the key list's own markup to redo with whatever comparison (or none
    /// stated at all) happened to be typed there.
    /// </summary>
    public bool IsSelected(VaultEntryInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return SelectedReference is not null
            && string.Equals(key.Reference, SelectedReference, StringComparison.Ordinal);
    }

    /// <summary>
    /// The node this publication would go to, or <see langword="null"/> when there is none. Resolved
    /// from the network's canonical form, not the entry's raw text: <see cref="Endpoints.For"/> and a
    /// person's own overrides in <see cref="INodeSettingsStore"/> are both keyed by the canonical
    /// name, so a key recorded as e.g. <c>"TESTNET"</c> must resolve the same node as one recorded
    /// as <c>"testnet"</c> rather than being told, wrongly, that no node URL is known for it.
    /// </summary>
    public string? NodeUrl =>
        SelectedKey is not null && XrplFileLink.TryNormalizeNetwork(SelectedKey.Network, out string? canonical)
            ? NodeEndpoint.Resolve(nodes, canonical)
            : null;

    /// <summary>Why the screen cannot go on, or <see langword="null"/> when it can.</summary>
    public string? Message { get; private set; }

    /// <summary>Something true and worth knowing that does not stop the publication.</summary>
    public string? Warning { get; private set; }

    public bool CanEstimate =>
        FilePath is not null && FileSize > 0 && FileSize <= MaxFileBytes && SelectedKey is not null
        && XrplFileLink.TryNormalizeNetwork(SelectedKey.Network, out _) && NodeUrl is not null;

    public async Task RefreshKeysAsync(CancellationToken cancellationToken)
    {
        Keys = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

        // A key that was selected and is no longer listed -- deleted from another screen, or gone
        // with a vault that was locked and reopened elsewhere -- must not stay selected: SelectedKey
        // would answer null while SelectedReference still named it.
        if (SelectedKey is null) SelectedReference = null;

        // One key and nothing chosen: a list of one unselected row read, in a running window, as a
        // field already showing its value, while the screen asked the person to choose. There is
        // nothing to choose between, and nothing is spent here -- the estimate names the paying
        // account and still has to be confirmed with its own button.
        //
        // With two or more keys the choice stays the person's, and that includes a choice this model
        // made while there was only one: left in place, a second key appearing would leave the first
        // highlighted as though somebody had picked it from two. A choice the person made survives.
        if (_chosenByModel && Keys.Count != 1)
        {
            SelectedReference = null;
            _chosenByModel = false;
        }

        if (SelectedReference is null && Keys.Count == 1)
        {
            SelectedReference = Keys[0].Reference;
            _chosenByModel = true;
        }

        Describe();
    }

    /// <summary>
    /// What went wrong the last time the file dialog failed to open, or <see langword="null"/>.
    /// The person is shown <see cref="Message"/>; this is kept so the platform layer can write the
    /// cause somewhere, because once this model catches the exception nothing else will see it.
    /// </summary>
    public Exception? LastPickerFailure { get; private set; }

    /// <summary>Whether <see cref="SelectedReference"/> was set by this model rather than by the person.</summary>
    private bool _chosenByModel;

    public async Task PickFileAsync(CancellationToken cancellationToken)
    {
        string? path;

        try
        {
            path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
            LastPickerFailure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog itself failing, as opposed to the person cancelling it (a null path). Seen
            // in a running window: Windows refuses to show this dialog to a process running as
            // administrator, and the exception went straight to Blazor's anonymous error bar.
            LastPickerFailure = ex;
            Message = "The file dialog could not be opened.";
            return;
        }

        if (path is null) return;

        try
        {
            FileInfo info = new(path);
            FilePath = path;
            FileName = info.Name;
            FileSize = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FilePath = null;
            FileName = null;
            FileSize = 0;
            Message = "That file could not be read.";
            return;
        }

        Describe();
    }

    public void SelectKey(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        SelectedReference = reference;
        _chosenByModel = false;
        Describe();
    }

    /// <summary>
    /// Records a file's name and size without touching the disk. Exists so the two size rules can be
    /// tested without writing tens of megabytes on a CI runner.
    /// </summary>
    internal void DescribeFileForTests(string name, long size)
    {
        FilePath = Path.Combine(Path.GetTempPath(), name);
        FileName = name;
        FileSize = size;
        Describe();
    }

    /// <summary>
    /// Measures the file and settles everything a run needs, without reading its bytes: the estimate
    /// opens and holds the file (spec S24). Returns <see langword="null"/> when it cannot, having set
    /// <see cref="Message"/> to why — a locked vault and a file that has gone away between choosing it
    /// and pressing the button are both ordinary here.
    /// </summary>
    public async Task<PublishRequest?> BuildRequestAsync(CancellationToken cancellationToken)
    {
        Describe();
        if (!CanEstimate || FilePath is null || SelectedKey is null) return null;

        long size;

        try
        {
            // Measured again rather than remembered: the file may have changed since it was picked. Its
            // bytes are not read here: the estimate opens and holds it (spec S24).
            size = new FileInfo(FilePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return null;
        }

        if (size == 0)
        {
            Message = "That file is empty, and an empty file has nothing to publish.";
            return null;
        }

        // Asked rather than remembered: the vault can be locked by the idle timer or by another
        // screen between this screen opening and this button being pressed, and a request built over
        // a locked vault fails at its first signature instead of here.
        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Any(key => string.Equals(key.Reference, SelectedKey.Reference, StringComparison.Ordinal)))
            {
                Message = "That key is no longer in the vault.";
                return null;
            }

            if (SelectedPayer is { } chosen && !listed.Any(key => string.Equals(key.Reference, chosen.Reference, StringComparison.Ordinal)))
            {
                Message = "The key chosen to pay is no longer in the vault.";
                return null;
            }
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            return null;
        }

        // CanEstimate already established that the key's network normalizes; the canonical form is
        // what travels in the request, so a key recorded as e.g. "TESTNET" publishes as "testnet"
        // rather than being refused deep inside the run for a mismatch of its own making.
        XrplFileLink.TryNormalizeNetwork(SelectedKey.Network, out string? canonicalNetwork);

        VaultEntryInfo? payer = SelectedPayer;

        return new PublishRequest(
            size,
            TypeOf(FileName!),
            FileName!,
            SelectedKey.Reference,
            SelectedKey.Address,
            canonicalNetwork!,
            payer?.Reference,
            payer?.Address,
            FilePath)
        {
            WriterLabel = SelectedKey.Label,
            PayerLabel = payer?.Label,
        };
    }

    private static string TypeOf(string fileName) =>
        TypesByExtension.TryGetValue(Path.GetExtension(fileName), out string? type)
            ? type
            : "application/octet-stream";

    private void Describe()
    {
        Warning = null;

        if (FilePath is null)
        {
            Message = "Choose a file to publish.";
            return;
        }

        if (FileSize == 0)
        {
            Message = "That file is empty, and an empty file has nothing to publish.";
            return;
        }

        if (FileSize > MaxFileBytes)
        {
            Message = $"That file is {FileSize:N0} bytes. This application publishes files up to {MaxFileBytes:N0} bytes.";
            return;
        }

        if (SelectedKey is null)
        {
            Message = "Choose the key that will publish it.";
            return;
        }

        if (!XrplFileLink.TryNormalizeNetwork(SelectedKey.Network, out string? canonicalNetwork))
        {
            Message =
                $"The key's network '{SelectedKey.Network}' is not one an xrpl-file link can name. "
                + "xrpl-file links use 'mainnet', 'testnet', 'devnet', or a NetworkID written as digits.";
            return;
        }

        if (FileSize > ReaderDefaultMaxBytes)
        {
            Warning =
                $"This file is larger than the {ReaderDefaultMaxBytes:N0} bytes a reader accepts by default, "
                + "so the link will only open for a reader whose limit has been raised. Publishing it still works.";
        }

        // Named by its canonical form: that is the network the publication will actually use, and
        // the one NodeUrl itself resolved against, so it is also the one worth reading about here.
        Message = NodeUrl is null
            ? $"No node URL is known for the network '{canonicalNetwork}'. Add one in Settings."
            : null;
    }
}
