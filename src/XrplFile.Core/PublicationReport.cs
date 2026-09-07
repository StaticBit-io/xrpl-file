namespace XrplFile;

/// <summary>
/// What a finished run produced, in the shape the war-and-peace uploader wrote: enough to show a
/// person what happened, and enough to check it against the ledger afterwards.
/// </summary>
public sealed record PublicationReport(
    XrplFileLink Link,
    Manifest Manifest,
    string Writer,
    long Transactions,
    long FeeDropsSpent,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<string> ListHashes,
    string ManifestHash)
{
    public TimeSpan Took => FinishedAt - StartedAt;
}
