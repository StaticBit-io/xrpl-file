namespace XrplFile.App.Core;

/// <summary>The payer used last on each network: the default of "Paid by" (spec 6.1).</summary>
public interface IPayerSettingsStore
{
    string? LastPayer(string network);

    void Remember(string network, string payerReference);
}
