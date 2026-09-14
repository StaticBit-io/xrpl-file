using System.Buffers.Binary;

using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// The container's own format, tested without a vault anywhere near it. What a vault does with one
/// is <see cref="ExportContainerTests"/>; that the Wallet's own writer produces bytes this reader
/// opens is <see cref="WalletCompatibilityTests"/>, and that is the only one of the three that can
/// prove compatibility rather than assert it.
/// </summary>
public class BackupContainerTests
{
    private static BackupPayload OneEntry() => new()
    {
        Wallets =
        [
            new BackupWalletEntry(
                KeyValue: "sEdTM1uX8pu2do5XvTnutH6HsouMaM2",
                MnemonicPassphrase: null,
                ClassicAddress: "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
                KeyType: 0,
                Algorithm: "ed25519",
                DerivationPath: null,
                MasterAddress: null,
                Description: "a key",
                Network: "testnet",
                Kind: "Generated"),
        ],
    };

    [Fact]
    public void A_container_round_trips()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "container password", iterations: 1000);

        Assert.True(BackupContainer.TryUnpack(container, "container password", fromFile: true,
            out BackupPayload? opened, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.None, error);
        Assert.Equal("sEdTM1uX8pu2do5XvTnutH6HsouMaM2", opened!.Wallets[0].KeyValue);
        Assert.Equal("testnet", opened.Wallets[0].Network);
    }

    [Fact]
    public void Every_field_of_an_entry_survives_the_round_trip()
    {
        // The four trailing fields are ours, added the way Folder and DestinationTag already were.
        // Nothing but this says they are actually written and read back rather than quietly lost to
        // the naming policy or to WhenWritingNull.
        BackupWalletEntry entry = new(
            KeyValue: "sEdTM1uX8pu2do5XvTnutH6HsouMaM2",
            MnemonicPassphrase: "a passphrase",
            ClassicAddress: "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
            KeyType: 1,
            Algorithm: "secp256k1",
            DerivationPath: "m/44'/144'/0'/0/0",
            MasterAddress: "rMaster",
            Description: "a key",
            Folder: "a folder",
            DestinationTag: "12345",
            Network: "testnet",
            Kind: "Imported",
            LastKnownSequence: 42u,
            DeletableFromLedger: 1_000L);

        byte[] container = BackupContainer.Pack(
            new BackupPayload { Wallets = [entry] }, "container password", iterations: 1000);

        Assert.True(BackupContainer.TryUnpack(container, "container password", fromFile: true,
            out BackupPayload? opened, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.None, error);
        Assert.Equal(entry, opened!.Wallets[0]);
    }

    [Fact]
    public void The_header_is_the_wallets_layout()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "p", iterations: 1000);

        Assert.Equal("SBW1"u8.ToArray(), container[..4]);
        Assert.Equal(BackupContainer.CurrentVersion, container[4]);
        Assert.Equal(1, container[5]);                                             // kdfId = PBKDF2
        Assert.Equal(1000, BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(6, 4)));
    }

    [Fact]
    public void A_wrong_password_is_told_apart_from_a_foreign_container()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);

        Assert.False(BackupContainer.TryUnpack(container, "wrong", fromFile: true, out _, out BackupUnpackError wrong));
        Assert.Equal(BackupUnpackError.WrongPassword, wrong);

        Assert.False(BackupContainer.TryUnpack("not a container at all"u8.ToArray(), "right", fromFile: true,
            out _, out BackupUnpackError foreign));
        Assert.Equal(BackupUnpackError.NotOurContainer, foreign);
    }

    [Fact]
    public void A_tampered_header_reads_as_a_wrong_password_on_purpose()
    {
        // The header is the associated data, so any edit fails the tag exactly as a wrong password
        // does. The two are deliberately not told apart: the only thing a user can reason about is
        // the password.
        //
        // The byte edited is the kdfId, which is the Wallet's own worked example of the rule, and it
        // is edited to another password-carrying value so that nothing but the associated data
        // changes: the salt and the iteration count are untouched, so the key derived here is the
        // very key that sealed the container and only the tag can tell that anything happened. The
        // version byte is the one header field this does not hold for, because it is read before
        // anything is decrypted -- see An_unknown_format_version_is_not_our_container.
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        container[5] = 2;

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.WrongPassword, error);
    }

    [Fact]
    public void An_unknown_format_version_is_not_our_container()
    {
        // The one header byte decided before the tag is, and deliberately so: a container from a
        // build that moved the version is not one this build opens at any password, and reporting it
        // as a wrong password would send the user retyping a correct one for ever.
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        container[4] ^= 0xFF;

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.NotOurContainer, error);
    }

    [Fact]
    public void An_iteration_count_outside_the_shared_range_is_refused_before_the_derivation()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(6, 4), 50_000_000);

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.IterationsOutOfRange, error);
    }

    [Fact]
    public void A_zero_iteration_count_on_a_password_container_is_refused_the_same_way()
    {
        // The other end of the range, and the one a truncated or zeroed field lands on. It is never
        // a value Pack produces, so it is damage rather than a cost this build merely disagrees with.
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(6, 4), 0);

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.IterationsOutOfRange, error);
    }

    [Fact]
    public void Packing_above_the_ceiling_is_refused_rather_than_written()
    {
        // A container written above the ceiling would report success and then be refused by this
        // build's own reader as IterationsOutOfRange -- a backup the user only discovers is
        // unopenable at the moment they need it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackupContainer.Pack(OneEntry(), "right", Pbkdf2Kdf.IterationsCeiling + 1));
    }

    [Fact]
    public void A_password_less_container_is_refused_when_it_arrives_as_a_file()
    {
        // Nothing about a password-less container is signed, so anyone can write one. Accepting a
        // file would let a stranger's bytes arrive on the import screen as a list of keys to pick.
        byte[] container = BackupContainer.Pack(OneEntry(), password: null, iterations: 0);

        Assert.True(BackupContainer.DeclaresNoPassword(container));
        Assert.False(BackupContainer.TryUnpack(container, null, fromFile: true, out _, out BackupUnpackError refused));
        Assert.Equal(BackupUnpackError.PasswordlessContainerNotAllowed, refused);

        Assert.True(BackupContainer.TryUnpack(container, null, fromFile: false, out _, out BackupUnpackError allowed));
        Assert.Equal(BackupUnpackError.None, allowed);
    }

    [Fact]
    public void Anything_that_is_not_one_of_our_containers_declares_nothing()
    {
        // The kdfId byte means something only behind our own header. A caller that skipped that
        // check would describe an arbitrary file whose sixth byte happens to be zero as an
        // unprotected backup.
        Assert.False(BackupContainer.DeclaresNoPassword("not a container at all"u8));
        Assert.False(BackupContainer.DeclaresNoPassword([]));
    }

    [Fact]
    public void A_container_that_needs_a_password_and_is_handed_none_reads_as_a_wrong_password()
    {
        // There is no password to try, and nothing else can be concluded about the container without
        // a derivation that could never succeed. Distinct from the case above, which is a container
        // declaring that it carries no password at all.
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);

        Assert.False(BackupContainer.TryUnpack(container, null, fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.WrongPassword, error);
    }

    [Fact]
    public void A_payload_that_expands_past_the_cap_is_not_a_wrong_password()
    {
        // The password was right; the container is not something this build will open.
        byte[] container = PackDeliberatelyOversizedPayload("right");

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.NotOurContainer, error);
    }

    /// <summary>
    /// A payload whose inflated JSON runs past <see cref="BackupContainer.MaxInflatedSize"/> while
    /// the container itself stays small: thousands of identical descriptions are what deflate is
    /// best at, which is precisely the shape the inflation bound exists to refuse.
    /// </summary>
    private static byte[] PackDeliberatelyOversizedPayload(string password)
    {
        string description = new('d', 4 * 1024);
        int count = ((BackupContainer.MaxInflatedSize / description.Length) + 1) * 2;

        List<BackupWalletEntry> wallets = new(count);
        for (int index = 0; index < count; index++)
        {
            wallets.Add(new BackupWalletEntry(
                KeyValue: "sEdTM1uX8pu2do5XvTnutH6HsouMaM2",
                MnemonicPassphrase: null,
                ClassicAddress: "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
                KeyType: 0,
                Algorithm: "ed25519",
                DerivationPath: null,
                MasterAddress: null,
                Description: description));
        }

        return BackupContainer.Pack(new BackupPayload { Wallets = wallets }, password, iterations: 1000);
    }

    [Fact]
    public void An_oversized_container_is_refused_before_anything_proportional_to_it_is_allocated()
    {
        // A header this build recognises, padded one byte past MaxContainerSize. Without the size
        // check in TryUnpack, this reaches TryUnpackWithPassword, which copies the tail into a
        // ciphertext buffer and a plaintext buffer each sized off data.Length and runs a full
        // PBKDF2 derivation, before the GCM tag ever gets a say -- the cost a phishing "backup"
        // would impose on whoever opens it. GC.GetAllocatedBytesForCurrentThread proves the
        // refusal happens before any of that: this array is the only large allocation in the
        // test, and it happens before the measurement below starts.
        byte[] data = new byte[BackupContainer.MaxContainerSize + 1];
        "SBW1"u8.CopyTo(data);
        data[4] = (byte)BackupContainer.CurrentVersion;
        data[5] = 1; // kdfId = Pbkdf2Kdf.KdfId
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(6, 4), 600_000); // a valid iteration count

        // Warms up the JIT and any lazy static state first, so the measurement below reflects
        // only what this specific call allocates.
        byte[] warmup = BackupContainer.Pack(OneEntry(), "warm-up", iterations: 1000);
        BackupContainer.TryUnpack(warmup, "warm-up", fromFile: true, out _, out _);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool opened = BackupContainer.TryUnpack(
            data, "irrelevant", fromFile: true, out BackupPayload? payload, out BackupUnpackError error);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(opened);
        Assert.Null(payload);
        Assert.Equal(BackupUnpackError.NotOurContainer, error);
        Assert.True(allocated < 4096,
            $"expected the container to be refused before allocating anything proportional to its " +
            $"length, but the call allocated {allocated} bytes");
    }

    /// <summary>
    /// Fix round 1, item 2 (<c>BackupContainer.cs</c>, <c>TryUnpackWithPassword</c>): the review
    /// found <c>passwordBytes</c> allocated and handed to <see cref="Pbkdf2Kdf.Derive"/> outside
    /// the <c>try</c> that zeroes it, so a throw out of <c>Derive</c> would leak it. The fix moves
    /// both statements inside the <c>try</c>, mirroring how <see cref="BackupContainer.Pack"/>
    /// already protects its own password buffer around <c>PackWithPassword</c>.
    /// </summary>
    /// <remarks>
    /// There is no byte array this test can hand <see cref="BackupContainer.TryUnpack"/> that
    /// reaches <c>Derive</c> with an input <c>Derive</c> refuses: the iteration count is read
    /// unsigned and rejected as <see cref="BackupUnpackError.IterationsOutOfRange"/> for anything
    /// outside <c>[1, Pbkdf2Kdf.IterationsCeiling]</c> before <c>Derive</c> is ever called, and
    /// that range is a subset of what <c>Derive</c> itself requires (<c>iterations &gt;= 1</c>,
    /// with no ceiling of its own); the output length is the fixed 32-byte key size, never
    /// attacker-supplied. So this cannot be a test that fails against the unfixed method and
    /// passes against the fixed one -- the window the fix closes is not reachable through
    /// <c>TryUnpack</c> today, exactly as the review says. What is left to pin is the contract the
    /// fix defends against: that <c>Derive</c> does throw for the one input that guard exists to
    /// keep away from it.
    /// </remarks>
    [Fact]
    public void Derive_refuses_the_one_input_the_iteration_range_check_keeps_away_from_it()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Pbkdf2Kdf.Derive("password"u8, "0123456789abcdef"u8, iterations: 0, outputLength: 32));
    }
}
