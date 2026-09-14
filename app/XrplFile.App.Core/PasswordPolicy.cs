namespace XrplFile.App.Core;

/// <summary>
/// The one rule about how short a password a person chooses may be, and the one place it is
/// written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why any rule at all, in a design that otherwise refuses to nag.</b> This vault has no
/// recovery phrase and no second factor (design E5): a master password is the only thing that
/// opens it, and a per-key export container is the only way back from a forgotten one (design
/// section 8, D10). Both of those were, until this review, accepted empty. An export sealed under
/// PBKDF2 of the empty string is not a backup, and the entry it came from was marked
/// <c>Exported</c> anyway — so the list stopped warning that the key exists only in the vault, for
/// a key whose only copy outside it anybody can open. A password the person chooses is the whole
/// of the security here; it cannot be optional.
/// </para>
/// <para>
/// <b>Why eight characters, and not some other number.</b> NIST SP 800-63B requires memorized
/// secrets chosen by the subscriber to be at least 8 characters, and — in the same breath —
/// forbids composition rules about which character classes must appear. Eight is therefore a floor
/// an actual standard names rather than a number invented here, and it is the only requirement
/// that standard would let this application impose. The container's own work factor is what
/// carries the rest: PBKDF2-HMAC-SHA256 at 1,000,000 iterations, measured at 122–126 ms on the
/// machine in design section 9, puts an offline guesser at roughly eight attempts a second per
/// core, so eight characters is where the attacker starts paying for the key-derivation function
/// rather than for the length. Anything longer would be a rule this project has no evidence for,
/// and the create screen already states the consequence of forgetting in plain words.
/// </para>
/// <para>
/// <b>Where it applies, and where it deliberately does not.</b> Every point at which a person
/// <i>chooses</i> a password: creating a vault (<see cref="UnlockModel.CreateAsync"/>), changing
/// the master password (<see cref="PasswordChangeModel.ChangeAsync"/>), and sealing an export
/// container (<see cref="ExportModel.ToContainerAsync"/>). Never at a point where a person
/// <i>enters</i> one they already have: <see cref="UnlockModel.UnlockAsync"/> and the "current
/// password" half of a change pass whatever was typed straight through, because a vault created
/// before this rule existed must still open, and refusing a short password at the door would lock
/// its owner out of their own keys to enforce a rule retroactively.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>The fewest characters a chosen password may have. See the type's remarks for why it is this number.</summary>
    public const int MinimumLength = 8;

    /// <summary>
    /// Whether <paramref name="utf8Password"/> is at least <see cref="MinimumLength"/> characters
    /// long.
    /// </summary>
    /// <remarks>
    /// Counts characters, not bytes. A password arrives here as the UTF-8 bytes of what was typed
    /// — that is how every caller in this application already holds one, so that it can be erased
    /// — and a length in bytes would make the rule depend on the alphabet a person writes in: an
    /// eight-character Cyrillic or Greek password is sixteen bytes and a four-character one is
    /// eight, so a byte count would accept and reject the wrong ones in opposite directions.
    /// Counting the bytes that are not UTF-8 continuation bytes counts code points, which is what
    /// "characters" means for this purpose.
    /// </remarks>
    /// <param name="utf8Password">The UTF-8 bytes of the password as typed. Never logged, never echoed back.</param>
    public static bool IsLongEnough(ReadOnlySpan<byte> utf8Password)
    {
        int characters = 0;
        foreach (byte b in utf8Password)
        {
            // A continuation byte is 10xxxxxx; every other byte starts a code point.
            if ((b & 0xC0) != 0x80)
            {
                characters++;
            }
        }

        return characters >= MinimumLength;
    }
}
