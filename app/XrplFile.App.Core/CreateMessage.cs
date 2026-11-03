using XrplFile.App.Core.Localization;
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>How an attempt to create a new vault ended (design E2: a component holds no decision).</summary>
/// <remarks>
/// <see cref="IKeyVault.CreateAsync"/> has no typed result of its own — it either succeeds or
/// throws — so this is <see cref="UnlockModel.CreateAsync"/>'s own translation of the two failures
/// that call can actually produce: <see cref="KeyVault.CreateAsync"/>'s <see cref="InvalidOperationException"/>
/// for a path that already holds a vault, and the <see cref="VaultException"/> with
/// <see cref="VaultError.StorageFailed"/> that <c>DurableFile.WriteAsync</c> wraps a refused
/// write in. Every other exception — including <see cref="OperationCanceledException"/> — has no
/// value here and is left to propagate: this type exists to give words to failures a screen must
/// show, not to catch everything a bug could throw.
/// </remarks>
public enum VaultCreateResult
{
    /// <summary>A new, empty vault was written and is now open.</summary>
    Success,

    /// <summary>
    /// A vault file is already at this path. Creating over it would destroy every key inside it, so
    /// nothing was written.
    /// </summary>
    VaultAlreadyExists,

    /// <summary>Storage refused the write. Transient; nothing can be concluded about the password.</summary>
    StorageUnavailable,

    /// <summary>
    /// The chosen master password is shorter than <see cref="PasswordPolicy.MinimumLength"/>.
    /// Nothing was written and <see cref="IKeyVault.CreateAsync"/> was never called: this is
    /// <see cref="UnlockModel.CreateAsync"/>'s own refusal, not something the vault reported — see
    /// <see cref="PasswordPolicy"/> for why a vault with no recovery phrase cannot be sealed under
    /// a password of any length at all.
    /// </summary>
    PasswordTooShort,
}

/// <summary>What a screen says about one outcome of trying to create a vault.</summary>
/// <param name="Headline">The short line a screen leads with. Distinct for every <see cref="VaultCreateResult"/>.</param>
/// <param name="Detail">The sentence that follows it, explaining what the headline means and, where relevant, what to do about it.</param>
public sealed record CreateMessage(UiText Headline, UiText Detail);

/// <summary>
/// Turns a <see cref="VaultCreateResult"/> into the words a person is told (design E2).
/// </summary>
/// <remarks>
/// No message here may carry a file path, an internal exception detail, or anything derived from a
/// password. That is the whole point of this type existing: <c>DurableFile.WriteAsync</c>'s
/// own exception message for a refused write names the full local vault path
/// ("Writing the vault file at '{path}' failed."), and a screen that relayed that verbatim — as
/// <c>CreateVault.razor</c> once did — put the Windows profile path on screen. This class is the
/// only place a create failure is turned into words a person sees, and it is handed a
/// <see cref="VaultCreateResult"/>, never the exception that produced it.
/// </remarks>
public static class CreateMessages
{
    /// <summary>The message for one outcome of an attempt to create a vault.</summary>
    /// <remarks>
    /// A <c>switch</c> expression with no default arm on purpose, exactly as
    /// <see cref="UnlockMessages.For"/> is: a value added to <see cref="VaultCreateResult"/> without
    /// a matching arm here is CS8509 under this repository's <c>TreatWarningsAsErrors</c>, so the
    /// build fails instead of silently handing out no words for a case nobody wrote any for.
    /// </remarks>
    // CS8524 suppressed for the same reason as UnlockMessages.For: a plain enum switch can never be
    // exhaustive over an arbitrary integral cast, and that is not the failure this switch exists to
    // catch. CS8509 — a genuinely missing named member — is left fully armed.
#pragma warning disable CS8524
    public static CreateMessage For(VaultCreateResult result) => result switch
    {
        VaultCreateResult.Success => new CreateMessage(
            VaultText.CreateSuccessHeadline,
            VaultText.CreateSuccessDetail),

        VaultCreateResult.VaultAlreadyExists => new CreateMessage(
            VaultText.CreateExistsHeadline,
            VaultText.CreateExistsDetail),

        VaultCreateResult.StorageUnavailable => new CreateMessage(
            VaultText.CreateStorageHeadline,
            VaultText.CreateStorageDetail),

        VaultCreateResult.PasswordTooShort => new CreateMessage(
            VaultText.CreateTooShortHeadline,
            UiText.Of(VaultText.CreateTooShortDetail, ("minimum", PasswordPolicy.MinimumLength))),
    };
#pragma warning restore CS8524
}
