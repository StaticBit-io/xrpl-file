using System.Text;
using Xrpl.Client;
using Xrpl.Client.Exceptions;
using Xrpl.Models.Common;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using LedgerIndexType = Xrpl.Models.Ledger.LedgerIndexType;

namespace XrplFile;

/// <summary>An owner key ready to sign for <see cref="Account"/>, either its master key or a regular key.</summary>
internal sealed record OwnKey(XrplWallet Wallet, string Account, bool IsRegularKey);

/// <summary>
/// Seed handling for publishing from the owner's own accounts. A seed is a live secret on mainnet, so it is read
/// without echo, never placed in an exception message and never taken from an argument or a pipe there.
/// </summary>
internal static class OwnAccounts
{
    /// <summary>
    /// Derives the signing wallet for <paramref name="account"/>. The algorithm comes from the seed itself. A seed
    /// that derives another address is taken to be a regular key and yields a wallet that signs for the account.
    /// </summary>
    public static OwnKey FromSeed(string seed, string account)
    {
        string trimmed = seed.Trim();
        XrplWallet derived;

        try
        {
            derived = XrplWallet.FromSeed(trimmed);
        }
        catch (Exception)
        {
            // The inner exception is dropped on purpose: decoder messages can quote the input.
            throw new InvalidOperationException($"the seed for {account} is not a valid XRPL seed");
        }

        if (string.Equals(derived.ClassicAddress, account, StringComparison.Ordinal))
        {
            return new OwnKey(derived, account, false);
        }

        return new OwnKey(XrplWallet.FromSeed(trimmed, account), account, true);
    }

    /// <summary>
    /// Reads one seed. A redirected input is refused on mainnet, where a seed in a pipe or a file ends up in shell
    /// history or on disk; on the testnet it is read as a line so the rehearsal can run unattended. On a console the
    /// keys are read with <c>intercept</c> and nothing is echoed.
    /// </summary>
    public static string ReadSeed(string label, FixtureNetwork network, TextReader input, bool inputRedirected)
    {
        if (inputRedirected)
        {
            if (network == FixtureNetwork.Mainnet)
            {
                throw new InvalidOperationException("mainnet seeds are only accepted at an interactive prompt, not from redirected input");
            }

            return input.ReadLine()?.Trim() ?? throw new InvalidOperationException("no seed on the input");
        }

        Console.Error.Write(label);
        StringBuilder typed = new();

        try
        {
            while (true)
            {
                ConsoleKeyInfo pressed = Console.ReadKey(intercept: true);

                if (pressed.Key == ConsoleKey.Enter) break;

                if (pressed.Key == ConsoleKey.Backspace)
                {
                    if (typed.Length > 0) typed.Length--;
                }
                else if (!char.IsControl(pressed.KeyChar))
                {
                    typed.Append(pressed.KeyChar);
                }
            }

            Console.Error.WriteLine();
            return typed.ToString().Trim();
        }
        finally
        {
            // A StringBuilder cannot be zeroed, but emptying it drops its chunks' reference to the seed text sooner.
            typed.Clear();
        }
    }

    /// <summary>
    /// A regular key signs only while the account still names it, so a stale or wrong seed is caught before any
    /// transaction is built. A master key needs no lookup: it always signs unless disabled, and the node says so.
    /// </summary>
    public static async Task RequireSigningRightsAsync(XrplClient client, OwnKey key, string network, CancellationToken cancellationToken)
    {
        if (!key.IsRegularKey) return;

        AccountInfo info;

        try
        {
            info = await client.AccountInfo(
                new AccountInfoRequest(key.Account)
                {
                    LedgerIndex = new LedgerIndex(LedgerIndexType.Validated),
                },
                cancellationToken).Typed().ConfigureAwait(false);
        }
        catch (RippledException e) when (string.Equals(e.Classify().RawError, XrplErrorCodes.ActNotFound, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"account {key.Account} does not exist on {network}");
        }

        string derived = Xrpl.Keypairs.XrplKeypairs.DeriveAddress(key.Wallet.PublicKey);

        if (!string.Equals(info.AccountData.RegularKey, derived, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"the seed given for {key.Account} is neither its master key nor its regular key (the account's regular key is "
                + (info.AccountData.RegularKey ?? "not set") + ")");
        }
    }
}
