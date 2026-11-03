using XrplFile.App.Core.Localization;
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// What a screen says about one outcome of trying to open a vault, and whether that outcome may
/// count against any attempt limit the screen keeps (design 7).
/// </summary>
/// <param name="Headline">The short line a screen leads with. Distinct for every <see cref="VaultUnlockResult"/>.</param>
/// <param name="Detail">The sentence that follows it, explaining what the headline means and, where relevant, what to do about it.</param>
/// <param name="CountsAsAFailedAttempt">
/// Whether a screen keeping an attempt limit may advance it because of this outcome. Only
/// <see cref="VaultUnlockResult.WrongPassword"/> ever sets this: a disk that refused to answer or
/// a record that was already damaged is not evidence the person mistyped anything.
/// </param>
public sealed record UnlockMessage(UiText Headline, UiText Detail, bool CountsAsAFailedAttempt);

/// <summary>
/// Turns a <see cref="VaultUnlockResult"/> into the words a person is told (design 7).
/// </summary>
/// <remarks>
/// <see cref="VaultUnlockResult.WrongPassword"/>, <see cref="VaultUnlockResult.VaultCorrupt"/> and
/// <see cref="VaultUnlockResult.VaultUnavailable"/> must never be conflated. Only the first may
/// ever advance a caller's failed-attempt counter. The second cannot be told apart from a wrong
/// password entered over a record whose integrity code already did not match, and so is never
/// phrased as a claim that the password is fine — nobody can make that claim. The third is a
/// storage failure from which nothing about the password, or anything else, can be concluded.
/// A screen that collapses these back into one sentence is how a failing disk gets reported as a
/// user's mistake, which is how healthy data gets destroyed by an attempt counter that should
/// never have moved.
/// </remarks>
public static class UnlockMessages
{
    /// <summary>The message for one outcome of an attempt to open a vault.</summary>
    /// <remarks>
    /// A <c>switch</c> expression with no default arm on purpose: a value added to
    /// <see cref="VaultUnlockResult"/> without a matching arm here is CS8509 under this
    /// repository's <c>TreatWarningsAsErrors</c>, so the build fails instead of silently handing
    /// out the wrong sentence for a case nobody wrote words for.
    /// </remarks>
    // CS8524 fires because a plain enum switch can never be exhaustive over an arbitrary integral
    // cast (e.g. `(VaultUnlockResult)99`) no matter how many named members are covered; suppressed
    // here on purpose, and only here, because that is not the failure this switch exists to catch.
    // CS8509 — a genuinely missing named member — is left fully armed: that is the one that must
    // still fail the build.
#pragma warning disable CS8524
    public static UnlockMessage For(VaultUnlockResult result) => result switch
    {
        VaultUnlockResult.Success => new UnlockMessage(
            VaultText.UnlockSuccessHeadline,
            VaultText.UnlockSuccessDetail,
            CountsAsAFailedAttempt: false),

        VaultUnlockResult.WrongPassword => new UnlockMessage(
            VaultText.UnlockWrongPasswordHeadline,
            VaultText.UnlockWrongPasswordDetail,
            CountsAsAFailedAttempt: true),

        VaultUnlockResult.VaultMissing => new UnlockMessage(
            VaultText.UnlockMissingHeadline,
            VaultText.UnlockMissingDetail,
            CountsAsAFailedAttempt: false),

        VaultUnlockResult.VaultCorrupt => new UnlockMessage(
            VaultText.UnlockCorruptHeadline,
            VaultText.UnlockCorruptDetail,
            CountsAsAFailedAttempt: false),

        VaultUnlockResult.VaultDegraded => new UnlockMessage(
            VaultText.UnlockDegradedHeadline,
            VaultText.UnlockDegradedDetail,
            CountsAsAFailedAttempt: false),

        VaultUnlockResult.VaultUnsupported => new UnlockMessage(
            VaultText.UnlockUnsupportedHeadline,
            VaultText.UnlockUnsupportedDetail,
            CountsAsAFailedAttempt: false),

        VaultUnlockResult.VaultUnavailable => new UnlockMessage(
            VaultText.UnlockUnavailableHeadline,
            VaultText.UnlockUnavailableDetail,
            CountsAsAFailedAttempt: false),
    };
#pragma warning restore CS8524
}
