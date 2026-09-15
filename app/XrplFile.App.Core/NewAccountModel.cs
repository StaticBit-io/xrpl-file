using Xrpl.Wallet;

using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Generating a throwaway key (design 4.1). It generates a <em>key</em>, not an account: on the XRP
/// Ledger an account exists once it holds its reserve, so nothing is on the ledger until somebody
/// funds the address this produces. The screen says so, and this model's name is the only place the
/// word "account" appears without that qualification.
/// </summary>
public sealed class NewAccountModel(IKeyVault vault)
{
    public string Label { get; set; } = string.Empty;

    public string Network { get; set; } = "mainnet";

    public string? Message { get; private set; }

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(Label) && XrplFileLink.TryNormalizeNetwork(Network, out _);

    /// <summary>
    /// Generates a key and stores it. Returns the vault's reference for it, or
    /// <see langword="null"/> when nothing was stored, having set <see cref="Message"/> to why.
    /// </summary>
    public async Task<string?> CreateAsync(CancellationToken cancellationToken)
    {
        Message = null;

        if (string.IsNullOrWhiteSpace(Label))
        {
            Message = "Give the account a name, so you can tell it from the others later.";
            return null;
        }

        if (!XrplFileLink.TryNormalizeNetwork(Network, out string? network))
        {
            Message =
                $"'{Network}' is not a network an xrpl-file link can name. Use mainnet, testnet, devnet, "
                + "or a network ID in digits.";
            return null;
        }

        XrplWallet wallet = XrplWallet.Generate();

        try
        {
            return await vault.StoreAsync(
                new NewVaultEntry(VaultEntryKind.Generated, Label.Trim(), network, wallet.ClassicAddress, wallet.Seed),
                cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            return null;
        }
    }
}
