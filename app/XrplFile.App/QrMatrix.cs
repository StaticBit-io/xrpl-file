using QRCoder;

namespace XrplFile.App;

/// <summary>
/// Turns a <see cref="XrplFile.App.Core.QrPayload"/>'s already-Base45-encoded text into the module
/// grid a canvas can be painted from (design section 8, "The rendering trap").
/// </summary>
/// <remarks>
/// <para>
/// This is presentation, not a model: it decides nothing about whether a QR code should be drawn —
/// <see cref="XrplFile.App.Core.QrPayload.FitsOneCode"/> already answered that in
/// <c>XrplFile.App.Core</c>, with no encoder in the loop, and a component reads it before ever
/// calling here. What is left is purely mechanical — text in, black-and-white grid out — which is
/// why it lives in this project rather than <c>XrplFile.App.Core</c>: <c>QRCoder</c> is a rendering
/// dependency with nothing to unit-test that the container-byte-cap tests in the Keys suite and
/// <c>ExportModelTests</c> do not already cover from the encoding side.
/// </para>
/// <para>
/// Error correction level H, always. Design section 8 is explicit that it is never quietly lowered,
/// so there is no parameter for it here to lower.
/// </para>
/// </remarks>
public static class QrMatrix
{
    /// <summary>
    /// Encodes <paramref name="text"/> at error correction level H and returns the module grid,
    /// <see langword="true"/> for a dark module, row-major, with no quiet zone added — a caller that
    /// draws one adds its own border.
    /// </summary>
    public static bool[][] Build(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        QRCodeGenerator generator = new();
        QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.H);

        int size = data.ModuleMatrix.Count;
        bool[][] matrix = new bool[size][];
        for (int row = 0; row < size; row++)
        {
            System.Collections.BitArray line = data.ModuleMatrix[row];
            bool[] destination = new bool[size];
            for (int column = 0; column < size; column++)
            {
                destination[column] = line[column];
            }

            matrix[row] = destination;
        }

        return matrix;
    }
}
