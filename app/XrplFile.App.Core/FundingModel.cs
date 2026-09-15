using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The funding screen (design 4.1): the address to send to, a QR of it, and what the account holds
/// now. It computes no target amount — what a publication needs depends on the file, and C2's
/// estimate screen already answers that exactly, against the real bytes (design G10).
/// </summary>
public sealed class FundingModel(IKeyVault vault, IConnectionFactory connections, IClipboardService clipboard)
{
    public string? Address { get; private set; }

    /// <summary>
    /// What the QR carries: the address, and nothing else. No scheme, no amount, no destination tag
    /// — a wallet scanning it must be free to send whatever the person decided, and an amount baked
    /// in here would be this application guessing a number it does not have.
    /// </summary>
    public string? QrPayload => Address;

    public string? Network { get; private set; }

    public long BalanceDrops { get; private set; }

    /// <summary>Whether the ledger carries this account at all. An address nobody funded is not on it.</summary>
    public bool AccountExists { get; private set; }

    public string? Message { get; private set; }

    /// <summary>
    /// Resolves <paramref name="reference"/> before touching anything shown, so a failed or empty
    /// lookup leaves the previous selection on screen rather than blanking it. The address may be
    /// the one thing a person is halfway through copying when, say, the vault locks itself between
    /// two visits to this screen — a lookup that cannot answer must not be the reason it disappears.
    /// </summary>
    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;

        VaultEntryInfo? entry;
        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            entry = listed.FirstOrDefault(info => string.Equals(info.Reference, reference, StringComparison.Ordinal));
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            return;
        }

        if (entry is null)
        {
            Message = "That key is no longer in the vault.";
            return;
        }

        Address = entry.Address;
        Network = entry.Network;
        BalanceDrops = 0;
        AccountExists = false;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        if (Address is null || Network is null) return;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(Network, cancellationToken).ConfigureAwait(false);

            AccountState state = await connection.Source
                .AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

            BalanceDrops = state.BalanceDrops;
            AccountExists = state.BalanceDrops > 0;
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
    }

    public Task CopyAddressAsync(CancellationToken cancellationToken) =>
        Address is null ? Task.CompletedTask : clipboard.CopyAsync(Address, cancellationToken);
}
