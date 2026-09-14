using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// The one test in this suite that can prove compatibility rather than assert it: everything else
/// checks this library's reader against this library's writer, which would pass just as happily on
/// a format nobody else speaks.
/// </summary>
/// <remarks>
/// <para>
/// <c>fixtures/wallet-container.bin</c> was produced once, by the Wallet project's own
/// <c>SB.WalletApp.Security.Backup.BackupContainer.Pack</c> and its own <c>ManagedPbkdf2Kdf</c>
/// (<c>kdfId</c> 1), from a throwaway program in that repository that was deleted afterwards. The
/// payload and password it was given are restated below, and the task report records how it was
/// generated. Nothing in this repository can regenerate it, which is the point: if a change here
/// stops it opening, the change broke the format rather than the fixture.
/// </para>
/// <para>
/// 227 bytes, SHA-256 <c>43FC70BF73EF6853CAB3F84955184A9BB2E626006032D87A3981DC813C84D208</c>.
/// </para>
/// </remarks>
public class WalletCompatibilityTests
{
    /// <summary>The password the throwaway program packed the fixture under.</summary>
    private const string FixturePassword = "wallet backup password";

    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine("fixtures", "wallet-container.bin"));

    [Fact]
    public void A_container_the_wallet_wrote_opens_here()
    {
        byte[] container = Fixture();

        Assert.True(BackupContainer.TryUnpack(container, FixturePassword, fromFile: true,
            out BackupPayload? payload, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.None, error);

        BackupWalletEntry entry = payload!.Wallets.Single();
        Assert.Equal("sEdTM1uX8pu2do5XvTnutH6HsouMaM2", entry.KeyValue);
        Assert.Equal("rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD", entry.ClassicAddress);
        Assert.Equal(1, entry.KeyType);
        Assert.Equal("ed25519", entry.Algorithm);
        Assert.Equal("a key from the wallet", entry.Description);

        // Their optional field, written by their build and read by ours. It is the evidence that the
        // record's shape survived the lift rather than only its first eight parameters.
        Assert.Equal("Main", entry.Folder);

        Assert.Null(entry.MnemonicPassphrase);
        Assert.Null(entry.DerivationPath);
        Assert.Null(entry.MasterAddress);
        Assert.Null(entry.DestinationTag);

        // A wallet backup carries no network, and the four fields this application added are absent
        // from a container written before they existed -- which is exactly the compatibility claim
        // the payload's own documentation makes. The import screen asks rather than guessing: a
        // testnet key treated as a mainnet one is an address the user does not own.
        Assert.Null(entry.Network);
        Assert.Null(entry.Kind);
        Assert.Null(entry.LastKnownSequence);
        Assert.Null(entry.DeletableFromLedger);
    }

    [Fact]
    public void The_wallets_container_carries_the_header_this_build_reads()
    {
        byte[] container = Fixture();

        Assert.Equal("SBW1"u8.ToArray(), container[..4]);
        Assert.Equal(BackupContainer.CurrentVersion, container[4]);
        Assert.Equal(Pbkdf2Kdf.KdfId, container[5]);
        Assert.False(BackupContainer.DeclaresNoPassword(container));

        // Their production floor, and inside the range both products agree on. The count travels in
        // the header precisely so each side can write its own measured value and the other honours it.
        Assert.Equal(
            Pbkdf2Kdf.IterationsFloor,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(6, 4)));
    }

    [Fact]
    public void A_wrong_password_against_the_wallets_container_is_still_a_wrong_password()
    {
        // The interesting half of the previous test's success: the tag really is being checked
        // against their bytes, so opening them is a derivation that agreed and not a reader that
        // accepts anything.
        Assert.False(BackupContainer.TryUnpack(Fixture(), "not the password", fromFile: true,
            out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.WrongPassword, error);
    }
}
