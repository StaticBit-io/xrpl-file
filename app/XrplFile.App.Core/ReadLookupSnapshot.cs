using XrplFile.App.Core.Localization;

namespace XrplFile.App.Core;

/// <summary>
/// One consistent view of a link lookup (design 4.1). Follows the same pattern
/// <see cref="ReadSnapshot"/> already established for <see cref="ReadRun"/>, applied to the other
/// half of reading: <see cref="ReadModel.LookUpAsync"/> writes its four result fields together, from
/// a thread-pool continuation, under <see cref="ReadModel"/>'s own lock — but until this type existed
/// a caller read them back one property at a time with no lock at all, which could see, for instance,
/// a <see cref="Manifest"/> from one lookup sitting next to a <see cref="NodeUrl"/> the model had
/// already cleared for the next one. <see cref="ReadModel.Snapshot"/> is this type's only source.
/// </summary>
/// <param name="Parsed">The link, once it has parsed. <see langword="null"/> before a lookup, or when the link did not parse.</param>
/// <param name="Manifest">What the node said the file is. <see langword="null"/> until a lookup both parses the link and reaches a node that answers.</param>
/// <param name="NodeUrl">
/// The node the manifest came from. Not necessarily the node a following read's blocks will come
/// from: <see cref="ReadRun.Start"/> takes only the link and the manifest, and
/// <see cref="ReadRun.RunAsync"/> re-resolves the node from the link's network against the settings
/// file as they stand *then* — so editing the node URL in Settings between this lookup and pressing
/// Read means this field names one node while the blocks arrive from another. Harmless (H9: the node
/// always comes from the link's own network either way, and every block is still checked against the
/// manifest's own root), but this field is a record of where the manifest was found, not a promise
/// about where anything else will come from.
/// </param>
/// <param name="Message">Why <see cref="CanRead"/> is false, when it is false for a reason worth showing.</param>
public sealed record ReadLookupSnapshot(
    XrplFileLink? Parsed,
    ManifestRecord? Manifest,
    string? NodeUrl,
    UiText? Message)
{
    /// <summary>Whether there is a manifest in hand and nothing wrong with it.</summary>
    public bool CanRead => Manifest is not null && Message is null;

    /// <summary>
    /// How long the read takes at the reader's measured pace (spec S15); null without a manifest. Taken
    /// from the same view as <see cref="Manifest"/>, so the screen shows a duration of the file it shows.
    /// </summary>
    public TimeSpan? ReadDuration => Manifest is { } record ? PublicationDuration.ForRead(record.Manifest) : null;
}
