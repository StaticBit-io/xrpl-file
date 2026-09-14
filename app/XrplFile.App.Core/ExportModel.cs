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
    /// Reveals <paramref name="reference"/>'s secret and seals it into a container under
    /// <paramref name="containerPassword"/>, marking the entry exported.
    /// </summary>
    /// <remarks>
    /// A thin pass-through to <see cref="IKeyVault.ExportAsync"/>: the guarantee that an entry is
    /// never marked exported before the container that proves it actually holds the secret exists
    /// — reveal first, mark only after, roll back on a failed write — belongs to the vault, not to
    /// this model. Duplicating any of that logic here would only give the two a chance to drift
    /// apart; whatever <see cref="IKeyVault.ExportAsync"/> throws propagates unchanged.
    /// </remarks>
    public async Task<byte[]> ToContainerAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
        await vault.ExportAsync(reference, containerPassword, cancellationToken).ConfigureAwait(false);

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
