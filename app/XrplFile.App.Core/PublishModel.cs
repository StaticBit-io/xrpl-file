using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The first publish screen (design 4.1): which file, which key, and every reason those two are not
/// yet enough to ask a node anything.
/// </summary>
public sealed class PublishModel(IKeyVault vault, IFilePickerService picker, INodeSettingsStore nodes)
{
    /// <summary>
    /// <see cref="ReaderOptions.MaxBytes"/>'s own default. A larger file publishes correctly and
    /// then produces a link the library's reader refuses unless its caller raises the ceiling, so
    /// this is a warning rather than a refusal (design 4.1) — the limit is the reader's default, not
    /// the format's, and the writer's read-back lifts it for itself.
    /// </summary>
    public const long ReaderDefaultMaxBytes = 20_000_000;

    /// <summary>
    /// The largest file this application will load. The whole file is held in memory as
    /// <see cref="ReadOnlyMemory{T}"/> and encoded into memos on top of that, and at roughly a
    /// kilobyte of file per memo this is already tens of thousands of transactions — a run whose
    /// cost stops it long before its size does.
    /// </summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

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
        && XrplFileLink.TryNormalizeNetwork(SelectedKey.Network, out _);

    public async Task RefreshKeysAsync(CancellationToken cancellationToken)
    {
        Keys = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

        // A key that was selected and is no longer listed -- deleted from another screen, or gone
        // with a vault that was locked and reopened elsewhere -- must not stay selected: SelectedKey
        // would answer null while SelectedReference still named it.
        if (SelectedKey is null) SelectedReference = null;

        Describe();
    }

    public async Task PickFileAsync(CancellationToken cancellationToken)
    {
        string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
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
    /// Reads the file and settles everything a run needs. Returns <see langword="null"/> when it
    /// cannot, having set <see cref="Message"/> to why — a locked vault and a file that has gone
    /// away between choosing it and pressing the button are both ordinary here.
    /// </summary>
    public async Task<PublishRequest?> BuildRequestAsync(CancellationToken cancellationToken)
    {
        Describe();
        if (!CanEstimate || FilePath is null || SelectedKey is null) return null;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return null;
        }

        if (bytes.Length == 0)
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

        return new PublishRequest(
            bytes,
            TypeOf(FileName!),
            FileName!,
            SelectedKey.Reference,
            SelectedKey.Address,
            canonicalNetwork!);
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
