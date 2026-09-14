namespace XrplFile.Keys;

/// <summary>
/// One key's exportable material, as it appears inside a backup container's plaintext.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from the Wallet project (<c>SB.WalletApp.Security</c>), file <c>Backup/BackupPayload.cs</c>.
/// Unchanged except for the namespace, this header, and the four optional trailing parameters this
/// application needs -- see the second remarks block below. The eight leading parameters are theirs,
/// in their order, because a key that moves between the two products is a key both readers
/// deserialise from the same JSON.
/// </para>
/// </remarks>
/// <param name="KeyValue">The seed, secret key or mnemonic the wallet is derived from.</param>
/// <param name="MnemonicPassphrase">
/// The optional passphrase applied on top of a mnemonic; <see langword="null"/> for wallets that
/// have none. Never written by this application, which holds seeds and nothing else.
/// </param>
/// <param name="ClassicAddress">
/// The wallet's classic address. The importer matches on this field to detect a wallet already
/// present on the device.
/// </param>
/// <param name="KeyType">
/// The wallet's key type as a raw number, not a domain enum. This application writes the value the
/// Wallet's own seed-derived wallets carry, because that is what every entry here is.
/// </param>
/// <param name="Algorithm">The signing algorithm the wallet uses (e.g. "ed25519", "secp256k1").</param>
/// <param name="DerivationPath">The derivation path used to obtain the wallet from its seed, when applicable.</param>
/// <param name="MasterAddress">The master (regular-key) address, when the wallet has one.</param>
/// <param name="Description">The user-facing label for the wallet.</param>
/// <param name="Folder">
/// The name of the folder the wallet was filed under, or <see langword="null"/> when it was in none.
/// </param>
/// <param name="DestinationTag">
/// The destination tag saved against this wallet, or <see langword="null"/> when it has none.
/// </param>
/// <param name="Network">
/// The network the key belongs to, or <see langword="null"/> on a container the Wallet wrote -- it
/// does not carry one. The import screen asks rather than guessing, because a testnet key treated
/// as a mainnet one is an address the user does not own.
/// </param>
/// <param name="Kind">
/// Where the key came from, as the name of a <see cref="VaultEntryKind"/>. A string rather than the
/// enum, so that a value a later build adds deserialises here instead of throwing.
/// </param>
/// <param name="LastKnownSequence">The account sequence this application last observed, when it has observed one.</param>
/// <param name="DeletableFromLedger">The ledger index from which the account may be deleted, when it is known.</param>
/// <remarks>
/// <para>
/// <paramref name="Folder"/> carries a name and not an identifier, because identifiers are local to
/// the device that issued them: folder 3 on the phone the backup came from is a different folder, or
/// none at all, on the one it is restored to. The importer matches on the name and creates what it
/// does not find.
/// </para>
/// <para>
/// It is optional, and reading it is where the compatibility lives: a container written before this
/// field existed simply has no <c>folder</c> property, deserialises with <see langword="null"/>, and
/// restores exactly as it did before. Nothing about the container header changes, so no version byte
/// moves.
/// </para>
/// <para>
/// <paramref name="Network"/>, <paramref name="Kind"/>, <paramref name="LastKnownSequence"/> and
/// <paramref name="DeletableFromLedger"/> are this application's, added in exactly that way and for
/// exactly that reason. Verified before adopting it: nothing in the Wallet sets
/// <c>JsonUnmappedMemberHandling.Disallow</c>, and <c>System.Text.Json</c> ignores unknown members by
/// default, so these four are invisible to its reader and a container written here still restores
/// there.
/// </para>
/// <para>
/// Names repeat across the wallets of one backup, and repetition is what deflate is for: the payload
/// is compressed before it is encrypted, so a network shared by twenty keys costs its name once and a
/// few bytes apiece. That matters here more than anywhere else in this format -- the QR form is
/// bounded at 1234 container bytes, and every field on this record is spent out of the same budget.
/// <c>DefaultIgnoreCondition = WhenWritingNull</c> is what keeps an unused one costing nothing at all.
/// </para>
/// </remarks>
public sealed record BackupWalletEntry(
    string KeyValue,
    string? MnemonicPassphrase,
    string ClassicAddress,
    int KeyType,
    string Algorithm,
    string? DerivationPath,
    string? MasterAddress,
    string? Description,
    string? Folder = null,
    string? DestinationTag = null,
    string? Network = null,
    string? Kind = null,
    uint? LastKnownSequence = null,
    long? DeletableFromLedger = null);

/// <summary>
/// The plaintext contents of a backup: every key the user chose to export, before the compression
/// and encryption <see cref="BackupContainer"/> applies on top.
/// </summary>
/// <remarks>
/// Lifted from the Wallet project (<c>SB.WalletApp.Security</c>), file <c>Backup/BackupPayload.cs</c>.
/// Unchanged except for the namespace and this header.
/// </remarks>
public sealed class BackupPayload
{
    public required IReadOnlyList<BackupWalletEntry> Wallets { get; init; }
}
