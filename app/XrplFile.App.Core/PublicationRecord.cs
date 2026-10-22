using System.Text.Json.Serialization;

namespace XrplFile.App.Core;

/// <summary>
/// One publication that succeeded (design 12). It holds no secret: the account is an address and
/// nothing here is a seed, which is the rule part A's own state file follows.
/// </summary>
/// <param name="FeeDropsEstimated">The estimated fee in drops, as the report states it. For a Batch publication it is the writer's part only (its solo transactions at the approved unit, Spec question 9): what the payer spent is a measurement the library reports apart (<c>PayerFeeDrops</c>), is not recorded here, and is not added to this figure, so a Batch record understates the publication's total cost.</param>
/// <param name="Recovered">True for a record written at start-up from a finished run's state, which has no report: its transaction and fee figures are 0, not measured. Omitted otherwise; an older application ignores it.</param>
/// <param name="Payer">The account that paid for a Batch publication, or null. Omitted from the file when null; an older application ignores it.</param>
public sealed record PublicationRecord(
    string Link,
    string Name,
    long Size,
    string Type,
    string Network,
    string Account,
    long Transactions,
    long FeeDropsEstimated,
    DateTimeOffset FinishedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Recovered = null);
