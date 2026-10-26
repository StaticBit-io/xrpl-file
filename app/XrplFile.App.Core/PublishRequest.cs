namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. It carries no bytes (spec 5.1): a
/// fresh run gets the file held open since its estimate in <see cref="Source"/>, a resume opens the
/// recorded <see cref="SourcePath"/> itself.
/// </summary>
/// <param name="Size">The file's length in bytes, as the source that is published has it.</param>
/// <param name="PayerReference">The vault key that pays for a Batch run, or null for a one-account run (spec 6.1).</param>
/// <param name="PayerAccount">That key's classic address.</param>
/// <param name="SourcePath">Where the file is, recorded so a resume need not ask for it (spec B17).</param>
public sealed record PublishRequest(
    long Size,
    string Type,
    string Name,
    string KeyReference,
    string Account,
    string Network,
    string? PayerReference = null,
    string? PayerAccount = null,
    string? SourcePath = null)
{
    /// <summary>The writer key's label, for the estimate card; null when not known.</summary>
    public string? WriterLabel { get; init; }

    /// <summary>The payer key's label, for the estimate card and the method line; null when not known.</summary>
    public string? PayerLabel { get; init; }

    /// <summary>
    /// The file held open from the estimate on (spec S24), so what was approved is what is published.
    /// A run that <see cref="PublicationRun.Start"/> accepts owns it from then on.
    /// </summary>
    public IPublicationSource? Source { get; init; }

    /// <summary>What the person approved on the Estimate screen (spec S25); a fresh run needs one.</summary>
    public SpendingApproval? Approval { get; init; }

    /// <summary>Whether <see cref="SourcePath"/> is the path the interrupted run recorded, rather than one just picked.</summary>
    public bool SourceRecorded { get; init; }
}
