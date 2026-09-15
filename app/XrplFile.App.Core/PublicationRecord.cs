namespace XrplFile.App.Core;

/// <summary>
/// One publication that succeeded (design 12). It holds no secret: the account is an address and
/// nothing here is a seed, which is the rule part A's own state file follows.
/// </summary>
public sealed record PublicationRecord(
    string Link,
    string Name,
    long Size,
    string Type,
    string Network,
    string Account,
    long Transactions,
    long FeeDropsEstimated,
    DateTimeOffset FinishedAt);
