// Draws a QR module matrix into a <canvas>. Deliberately never an <img src="data:...">: a long
// press on an <img> inside a WKWebView offers "Add to Photos", and an unencrypted backup code would
// go to the photo gallery and iCloud around every restriction XrplFile.App.Core applies to it. The
// canvas the caller passes in must sit inside a container styled "-webkit-touch-callout: none" for
// the same reason on the markup side; this script only owns the pixels.
//
// matrix is a row-major bool[][] built in C# (see XrplFile.App/QrMatrix.cs) from text already
// Base45-encoded and capped at error correction level H — this script does not decide the payload,
// the level, or whether a code should be drawn at all; it only paints the grid it is handed.
function drawQrMatrix(canvas, matrix) {
    if (!canvas || !matrix || matrix.length === 0) {
        return;
    }

    const moduleCount = matrix.length;
    const quietZoneModules = 4;
    const targetSize = 320;
    const moduleSize = Math.max(2, Math.floor(targetSize / (moduleCount + quietZoneModules * 2)));
    const dimension = (moduleCount + quietZoneModules * 2) * moduleSize;

    canvas.width = dimension;
    canvas.height = dimension;

    const context = canvas.getContext('2d');
    context.fillStyle = '#ffffff';
    context.fillRect(0, 0, dimension, dimension);

    context.fillStyle = '#000000';
    for (let row = 0; row < moduleCount; row++) {
        const line = matrix[row];
        for (let column = 0; column < moduleCount; column++) {
            if (line[column]) {
                const x = (column + quietZoneModules) * moduleSize;
                const y = (row + quietZoneModules) * moduleSize;
                context.fillRect(x, y, moduleSize, moduleSize);
            }
        }
    }
}

// Clears a canvas previously drawn by drawQrMatrix, so a screen that stops fitting one code (or
// navigates away) does not leave a stale code rendered behind newer, unrelated markup.
function clearQrCanvas(canvas) {
    if (!canvas) {
        return;
    }

    const context = canvas.getContext('2d');
    context.clearRect(0, 0, canvas.width, canvas.height);
}
