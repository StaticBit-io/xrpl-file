using XrplFile.App.Core.Localization;
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Turns a <see cref="VaultError"/> into the sentence a screen shows, for the two models that
/// report failure by throwing <see cref="VaultException"/> rather than setting a <c>Problem</c>
/// string of their own — <see cref="RevealModel.ShowAsync"/>, <see cref="DeleteKeyModel.PrepareAsync"/>
/// and <see cref="DeleteKeyModel.DeleteAsync"/>. <see cref="AddKeyModel"/> and
/// <see cref="PasswordChangeModel"/> already compose their own words for the errors they can see
/// (<c>AddKeyModel.StoreProblem</c>, <see cref="PasswordChangeModel.Describe"/>); this exists so a
/// component wiring the other two models is not left to compose that sentence itself — mapping an
/// error code to what it means is exactly the kind of decision this codebase keeps out of markup
/// (see <see cref="UnlockMessages"/> and <see cref="CreateMessages"/> for the same rule applied to
/// <see cref="VaultUnlockResult"/> and <see cref="VaultCreateResult"/>).
/// </summary>
/// <remarks>
/// Never built from a path: <c>VaultException.Message</c> is not used here, because
/// <c>KeyVault</c>'s own exception text is written for a developer reading a stack trace, not a
/// screen — see <see cref="CreateMessages"/>'s own tests on why an exception's message must never
/// reach a screen verbatim.
/// </remarks>
public static class VaultErrorMessages
{
    /// <summary>The sentence for one <see cref="VaultError"/>.</summary>
    /// <remarks>
    /// A <c>switch</c> expression with no default arm, the same discipline
    /// <see cref="UnlockMessages.For"/> follows: a value added to <see cref="VaultError"/> without a
    /// matching arm here is CS8509 under this repository's <c>TreatWarningsAsErrors</c>, so the
    /// build fails instead of a screen silently showing nothing for a case nobody wrote words for.
    /// </remarks>
    // See UnlockMessages.For for why CS8524 is suppressed here and CS8509 is left armed.
#pragma warning disable CS8524
    public static UiText For(VaultError error) => error switch
    {
        VaultError.Corrupt =>
            VaultText.ErrorCorrupt,

        VaultError.Unsupported =>
            VaultText.ErrorUnsupported,

        VaultError.Locked =>
            VaultText.ErrorLocked,

        VaultError.NoSuchEntry =>
            VaultText.ErrorNoSuchEntry,

        VaultError.DuplicateAddress =>
            VaultText.ErrorDuplicateAddress,

        VaultError.EntryDamaged =>
            VaultText.ErrorEntryDamaged,

        VaultError.StorageFailed =>
            VaultText.ErrorStorageFailed,

        VaultError.Degraded =>
            VaultText.ErrorDegraded,

        VaultError.AddressMismatch =>
            VaultText.ErrorAddressMismatch,
    };
#pragma warning restore CS8524
}
