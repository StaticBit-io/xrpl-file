using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// A backup container re-encoded for a QR code (design section 8): the text a scanner hands back,
/// how many container bytes went into it, and whether that text fits in a single code.
/// </summary>
/// <param name="Text">
/// <paramref name="ContainerBytes"/> worth of container, Base45-encoded (RFC 9285) rather than
/// raw or base64. Both mobile scanners hand a scanned code back as a string, and a byte with the
/// high bit set does not survive that round trip: the authentication tag then fails to verify and
/// the user is told their password is wrong when it was right.
/// </param>
/// <param name="ContainerBytes">The container's length before encoding — what <see cref="FitsOneCode"/> is judged against.</param>
/// <param name="FitsOneCode">
/// Whether <see cref="Text"/> fits inside one QR code at version 40, correction level H — 1852
/// alphanumeric characters, measured by the Wallet against a real encoder, which is 1234 container
/// bytes (design section 8, "What was measured"). One key always fits; a container holding several
/// may not, and this is how a caller learns that before a code is drawn that will not scan, rather
/// than after. The error correction level is never lowered and multi-frame QR is not implemented,
/// so <see langword="false"/> here is not a hint to work around — it is the answer.
/// </param>
public sealed record QrPayload(string Text, int ContainerBytes, bool FitsOneCode);

/// <summary>
/// The model behind exporting one vault entry to a portable backup container and turning that
/// container into a QR code (design section 8).
/// </summary>
public sealed class ExportModel(IKeyVault vault)
{
    /// <summary>
    /// The largest alphanumeric payload one QR code is assumed to carry: version 40, correction
    /// level H, measured against a real encoder rather than assumed (design section 8, "What was
    /// measured"). <see cref="Base45.GetEncodedLength"/> is pinned at the corresponding 1234-byte
    /// boundary by a test in the Keys suite; this is the character-count side of that same fact.
    /// </summary>
    private const int MaxAlphanumericCharacters = 1852;

    /// <summary>
    /// What went wrong with the most recent <see cref="ToContainerAsync"/> call, in words a screen
    /// can show as-is — or <see langword="null"/> after a call that produced a container. Never
    /// built from the backup password or from anything derived from it.
    /// </summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Reveals <paramref name="reference"/>'s secret and seals it into a container under
    /// <paramref name="containerPassword"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A backup password shorter than <see cref="PasswordPolicy.MinimumLength"/> is refused
    /// here, and this is the only layer that refuses it.</b> A component holds no decisions, and
    /// neither <see cref="IKeyVault.ExportAsync"/> nor <see cref="BackupContainer"/> can make this
    /// one: the container format admits any password, including none at all, because it also has
    /// to be readable by another product whose own rules are not ours to impose. Whether a password
    /// is strong enough for the file to be worth calling a backup is a judgement about this
    /// application's promise to its user — there is no recovery phrase, so this file is the only
    /// way back — and that judgement belongs in a model. See <see cref="PasswordPolicy"/> for the
    /// number and why it is that number.
    /// </para>
    /// <para>
    /// Whatever <see cref="IKeyVault.ExportAsync"/> reports as a <see cref="VaultException"/> — a
    /// locked vault, an unknown reference, a damaged entry — becomes <see cref="Problem"/> here,
    /// through <see cref="VaultErrorMessages"/>, rather than being left for a component to catch
    /// and map: this model then answers a caller the same way for every failure it can have.
    /// Everything else, cancellation included, propagates unchanged.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The sealed container, or <see langword="null"/> with <see cref="Problem"/> set when nothing
    /// was sealed. A refused password never reaches the vault at all: nothing is revealed, and
    /// nothing about the entry changes. Nothing about the entry changes on the success path
    /// either — see <see cref="RecordSavedAsync"/>.
    /// </returns>
    public async Task<byte[]?> ToContainerAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken)
    {
        if (!PasswordPolicy.IsLongEnough(containerPassword.Span))
        {
            Problem =
                $"Choose a backup password of at least {PasswordPolicy.MinimumLength} characters. " +
                "This file is the only way back to this key if the master password is ever " +
                "forgotten, and anyone who finds it can guess at it for as long as they like.";
            return null;
        }

        try
        {
            byte[] container = await vault
                .ExportAsync(reference, containerPassword, cancellationToken)
                .ConfigureAwait(false);
            Problem = null;
            return container;
        }
        catch (VaultException exception)
        {
            Problem = VaultErrorMessages.For(exception.Error);
            return null;
        }
    }

    /// <summary>
    /// Records that a container for <paramref name="reference"/> reached a file the person chose,
    /// by marking the entry <c>Exported</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called only after a save has actually succeeded, and nowhere else.</b> The flag exists so
    /// the key list can say which keys exist nowhere but this vault (design section 6, "The key
    /// list"), and the warning before deleting one rests on the same bit. Sealing a container does
    /// not make that bit true: <see cref="IKeyVault.ExportAsync"/> deliberately records nothing,
    /// because between it returning and the bytes landing there is a save dialog a person can
    /// cancel and a disk that can refuse them.
    /// </para>
    /// <para>
    /// <b>A QR code on screen does not count either.</b> It is a way to move a key to a device
    /// standing in front of this one, not a copy this application can claim exists afterwards —
    /// nobody can be sure a code that was displayed was ever scanned, and this flag's only job is
    /// to be sure.
    /// </para>
    /// <para>
    /// A failure to record it is reported through <see cref="Problem"/> rather than thrown. The
    /// bytes are already on disk at that point, so the export itself succeeded; what failed is
    /// remembering it, and the consequence is a key that keeps being flagged as unexported — the
    /// safe direction to be wrong in, and one the person can correct by exporting again.
    /// </para>
    /// </remarks>
    public async Task RecordSavedAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            await vault
                .UpdateAsync(reference, new VaultEntryEdit(Exported: true), cancellationToken)
                .ConfigureAwait(false);
            Problem = null;
        }
        catch (VaultException exception)
        {
            Problem =
                "The backup was saved, but the vault could not record that it was: " +
                VaultErrorMessages.For(exception.Error);
        }
    }

    /// <summary>
    /// Records that the platform refused to write the container where the person asked for it, so
    /// the screen has words for it and the entry stays unexported.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="RecordSavedAsync"/> for a save that threw rather than one that
    /// came back <see langword="false"/>. A caller that cancels the dialog is not a failure and must
    /// not call this: nothing went wrong, the person simply chose not to save yet. The sentence is
    /// composed here rather than in the component for the same reason every other sentence in this
    /// model is, and it names nothing about where the file was going -- a path on screen is exactly
    /// what <see cref="CreateMessages"/> exists to prevent elsewhere.
    /// </remarks>
    public void ReportSaveFailed() =>
        Problem =
            "The backup could not be written where you chose. Nothing was saved, and this key " +
            "still exists only in this vault. Try somewhere with room to write.";

    /// <summary>
    /// Encodes <paramref name="container"/> as the text a QR code would carry, and says whether
    /// that text fits into a single code.
    /// </summary>
    public QrPayload ToQr(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);

        string text = Base45.Encode(container);
        return new QrPayload(text, container.Length, text.Length <= MaxAlphanumericCharacters);
    }
}
