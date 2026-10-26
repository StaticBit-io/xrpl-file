namespace XrplFile.App.Core.Tests;

public static class Requests
{
    /// <summary>A request as the Estimate screen hands it to a run: the bytes as a held source, and an approval that bounds nothing.</summary>
    public static PublishRequest Fresh(
        byte[] bytes, string type, string name, string keyReference, string account, string network,
        string? payerReference = null, string? payerAccount = null, string? sourcePath = null) =>
        new(bytes.Length, type, name, keyReference, account, network, payerReference, payerAccount, sourcePath)
        {
            Source = new MemoryPublicationSource(bytes),
            Approval = new SpendingApproval(account, payerAccount, UnboundedFeeDrops, long.MaxValue, payerAccount is null ? 0 : long.MaxValue),
        };

    /// <summary>
    /// A fee-unit ceiling no test node reaches. Not <see cref="long.MaxValue"/>: the session prices the
    /// worst cases at the approved ceiling (Task 11), and that product must stay a number of drops.
    /// </summary>
    public const long UnboundedFeeDrops = 1_000;
}
