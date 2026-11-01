using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xrpl.AddressCodec;

namespace XrplFile;

/// <summary>
/// What a publication needs to survive the process dying.
///
/// It holds no seed. The key it names is a reference the caller understands — a row in a key
/// store, a file name, whatever the application uses — and resolving it back to a signer is
/// somebody else's job. That keeps the whole secret surface in one place.
/// <para>
/// Files written before streaming publication carry a <c>ChunkHashes</c> map; it is read and
/// ignored (spec S8). Nothing in the library or the application read it, and at "done" it held a
/// hash per chunk.
/// </para>
/// </summary>
/// <param name="StartedInLedger">
/// Where reconciliation begins on a resume. Without it a resumed run would sweep the account's
/// entire history rather than the few hundred ledgers it wrote in.
/// </param>
/// <param name="Phase">
/// The last stage the run was seen in: one of "chunks", "lists", "read-back", "manifest", "done".
/// It is written when the stage changes, so a run interrupted half-way names the stage it died in
/// rather than the one it started in.
/// </param>
/// <param name="Strategy">
/// How the run submits (spec B16): "sequence" or "tickets" in version 2, absent in a version-2 file
/// written before this was recorded; "batch" in version 3. A run resumes on the strategy it recorded.
/// </param>
/// <param name="SourcePath">The absolute path the file was read from, so a resume need not ask for it (spec B17).</param>
/// <param name="ManifestHash">The manifest's hash once the run is done (spec B27).</param>
/// <param name="Payer">Version 3: the classic address that signs and pays for the outers.</param>
/// <param name="PayerKeyReference">Version 3: the payer's key, as the caller references it.</param>
/// <param name="Fence">
/// Version 3: the highest deadline any transmission of the run registered, written before that
/// transmission is first sent (spec B23); 0 before the first one.
/// </param>
/// <param name="StartSequence">
/// Version 3: the writer's next sequence in <see cref="StartedInLedger"/>, read from the same
/// snapshot. A resume's reconciliation counts the history it reads as complete only when it covers
/// every sequence from here up to the writer's sequence after the fence (spec 5.2 step 3, B30).
/// Absent in a state written before it was recorded.
/// </param>
/// <param name="ManifestStatus">
/// <see cref="ManifestLandedRejected"/> when the manifest landed and a reader rejected it (spec S22);
/// <see cref="ManifestHash"/> then names it. Absent otherwise.
/// </param>
/// <param name="Failure">
/// Why the run cannot go on, when that outlives a resume: <see cref="WriterWindowConflictFailure"/>
/// (spec S17). A resume reports it again without sending anything. Absent otherwise.
/// </param>
/// <param name="WorkedSeconds">
/// How long the sessions before this one worked, in whole seconds, as the application measured it.
/// The library carries it and never reads it. Absent until a session that sent something has ended.
/// </param>
/// <param name="Cover">
/// The hash of the manifest transaction of the file's cover picture (format 4.1), upper-case hex, or
/// absent for none. The run writes it into its manifest, and a resume looks for a manifest carrying
/// exactly it (spec B27). An application built before this field existed ignores it when it resumes
/// such a run: it looks for a manifest without a cover and sends a second one. That limit is accepted
/// rather than closed with a state version, because only the fixture tool sets a cover so far.
/// </param>
public sealed record PublicationState(
    int Version,
    string Network,
    string Writer,
    string KeyReference,
    string Type,
    string? Name,
    long Size,
    int Chunk,
    string? Root,
    long StartedInLedger,
    string Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Strategy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourcePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManifestHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PayerKeyReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Fence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? StartSequence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManifestStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Failure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WorkedSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cover = null)
{
    /// <summary>
    /// Sequence and ticket runs. 2 since <see cref="Root"/> became something a run records at its start rather than at its
    /// end. A version 1 file names a started run with no root in it, and a resume that read one
    /// would have nothing to check the bytes it was handed against — so those files are refused
    /// by <see cref="Load"/> and listed as unreadable by <see cref="PublicationSession.Unfinished"/>,
    /// which is where a person can still see the account and the ledger they name.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Batch runs (spec B16). An application that predates them refuses this version, which is the
    /// point: it cannot resume a Batch run as a one-account run and erase its payer on save.
    /// </summary>
    public const int BatchVersion = 3;

    public const string SequenceStrategyName = "sequence";

    public const string TicketStrategyName = "tickets";

    public const string BatchStrategyName = "batch";

    /// <summary>The value of <see cref="Failure"/> for spec S17's conflict: the error's own name.</summary>
    public const string WriterWindowConflictFailure = nameof(XrplFileError.WriterWindowConflict);

    /// <summary>The value of <see cref="ManifestStatus"/> for a manifest that landed and that a reader does not accept (spec S22).</summary>
    public const string ManifestLandedRejected = "landed, rejected";

    /// <summary>The one this file ends with, on every platform, so two hosts write the same bytes.</summary>
    private const string LineEnding = "\n";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Written beside and moved into place: a crash during the write must not leave a state
        // file that parses as something it is not. The flush is what makes that true of a machine
        // losing power rather than only of a process dying: without it the move can reach the disk
        // ahead of the bytes it moves, and the run's record comes back empty or truncated.
        string temporary = path + ".writing";

        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Options) + LineEnding);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static PublicationState Load(string path)
    {
        PublicationState? state = JsonSerializer.Deserialize<PublicationState>(File.ReadAllText(path), Options);

        if (state is null) throw new FormatException($"{path} did not deserialize as a publication state");

        if (state.Version == CurrentVersion)
        {
            RequireRunnableSize(state);

            if (state.Strategy is not (null or SequenceStrategyName or TicketStrategyName))
            {
                throw new FormatException($"a version {CurrentVersion} state records the strategy '{state.Strategy}', which is not one it can carry");
            }

            return state;
        }

        if (state.Version == BatchVersion)
        {
            RequireRunnableSize(state);

            if (!string.Equals(state.Strategy, BatchStrategyName, StringComparison.Ordinal))
            {
                throw new FormatException($"a version {BatchVersion} state must record the strategy '{BatchStrategyName}'");
            }

            if (state.Payer is null || !XrplCodec.IsValidClassicAddress(state.Payer))
            {
                throw new FormatException($"a version {BatchVersion} state must name its payer by classic address");
            }

            if (string.IsNullOrWhiteSpace(state.PayerKeyReference))
            {
                throw new FormatException($"a version {BatchVersion} state must name its payer's key");
            }

            if (state.Fence is null)
            {
                throw new FormatException($"a version {BatchVersion} state must record its fence");
            }

            return state;
        }

        throw new FormatException($"state version {state.Version} is not one this library reads");
    }

    /// <summary>
    /// No run starts on an empty file or on one above the cap, so a recorded size outside the range
    /// is a damaged file that would otherwise reach the chunk arithmetic of a resume. It is checked
    /// per known version: a newer version may lay its fields out differently and must be reported
    /// as a version this library does not read, not as a bad size.
    /// </summary>
    private static void RequireRunnableSize(PublicationState state)
    {
        if (state.Size < 1 || state.Size > PublicationSession.MaxPublicationBytes)
        {
            throw new FormatException($"a state records a size of {state.Size} bytes, which no run can have");
        }
    }
}
