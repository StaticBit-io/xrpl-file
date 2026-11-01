/** Limits and checks of a cover, rule for rule from section 4.1 of the format. */

export const COVER_MAX_BYTES = 65536;
export const COVER_TYPES = ['image/webp', 'image/jpeg'];

/**
 * Why a cover manifest may not stand for the file it decorates, or null when it may. Both
 * arguments are records as XrplFileReader#manifest returns them.
 */
export function coverProblem(coverRecord, decoratedRecord) {
  const cover = coverRecord.manifest;
  if (coverRecord.writer !== decoratedRecord.writer) return 'the cover is written by another account';
  // Strictly below, so the cover's chunks never fall inside the decorated file's chunk range.
  if (coverRecord.ledgerIndex >= decoratedRecord.manifest.firstLedger) return 'the cover is not published below the file';
  if (!COVER_TYPES.includes(cover.type)) return `the cover type ${cover.type} is not one a cover may have`;
  if (cover.size > COVER_MAX_BYTES) return `the cover is ${cover.size} bytes, past ${COVER_MAX_BYTES}`;
  if (cover.blocks !== 1 || cover.lists !== 0) return 'the cover is not a single block';
  return null;
}

/** Whether the bytes begin the way the declared type says: RIFF....WEBP, or FF D8. */
export function coverMagicAgrees(type, bytes) {
  if (type === 'image/jpeg') return bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xd8;
  if (type === 'image/webp') {
    return bytes.length >= 12
      && bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46
      && bytes[8] === 0x57 && bytes[9] === 0x45 && bytes[10] === 0x42 && bytes[11] === 0x50;
  }
  return false;
}
