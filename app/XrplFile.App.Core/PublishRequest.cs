namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. The bytes travel with it because
/// <see cref="PublicationSession"/> takes the whole file as a <see cref="ReadOnlyMemory{T}"/> — there
/// is no streaming path into it, which is also why <see cref="PublishModel.MaxFileBytes"/> exists.
/// </summary>
/// <param name="PayerReference">The vault key that pays for a Batch run, or null for a one-account run (spec 6.1).</param>
/// <param name="PayerAccount">That key's classic address.</param>
/// <param name="SourcePath">Where the bytes were read from, recorded so a resume need not ask for them (spec B17).</param>
public sealed record PublishRequest(
    ReadOnlyMemory<byte> File,
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
}
