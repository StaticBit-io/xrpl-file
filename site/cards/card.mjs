// What a file's card says and shows, read through the element's own reader so every byte a card
// shows is verified the way the element verifies it. Nothing here throws: a card that cannot be
// read is a card with a status, and the caller answers with the generic one.
import { XrplFileReader } from '../../web/src/reader.mjs';
import { formatBytes } from '../../web/src/element/format.mjs';
import { FILE_PICTURE_MAX_BYTES, isFilePictureCandidate, pictureMagicAgrees } from './rules.mjs';

/**
 * @typedef {{ status: 'file', title: string, description: string, picture: 'file' | 'cover' | 'banner' }
 *   | { status: 'missing' } | { status: 'unavailable' }} Card
 */

// The same characters the element strips from a name before it shows one: controls, separators and
// format characters, so a right-to-left override cannot make a card read differently from the file.
const INVISIBLE = /[\u0000-\u001f\u007f-\u009f\p{Zl}\p{Zp}\p{Cf}]/gu;

/** The card's title: the manifest's name made visible-safe, or the site name and the hash's start. */
export function cardTitle(manifest, hash) {
  const name = typeof manifest.name === 'string' ? manifest.name.replace(INVISIBLE, '').trim() : '';
  return name || `xrpl-file ${hash.slice(0, 8)}`;
}

// A subtype written into the description must look like one: the manifest type is a stranger's text.
const SUBTYPE = /^[a-z0-9][a-z0-9.+-]{0,15}$/;
const SUBTYPE_NAMES = { 'svg+xml': 'SVG' };
const KINDS = { image: 'image', video: 'video', audio: 'audio' };

function typeLabel(type) {
  const [kind, subtype = ''] = String(type).split(';')[0].trim().toLowerCase().split('/');
  if (kind === 'application' && subtype === 'pdf') return 'PDF document';
  if (kind === 'text') return 'Text file';
  if (!Object.hasOwn(KINDS, kind)) return 'File';
  if (!SUBTYPE.test(subtype)) return `${kind[0].toUpperCase()}${kind.slice(1)}`;
  return `${SUBTYPE_NAMES[subtype] ?? subtype.toUpperCase()} ${KINDS[kind]}`;
}

/** "PNG image, 1.5 KB, stored in the XRP Ledger." */
export function describeFile({ type, size }) {
  return `${typeLabel(type)}, ${formatBytes(size)}, stored in the XRP Ledger.`;
}

/**
 * The card of the file whose manifest is `hash` on `network`, and its cover when the cover is the
 * card picture: the cover is read here so the card names a picture the robot will actually get.
 * @returns {Promise<{ card: Card, cover: { bytes: Uint8Array, type: string } | null }>}
 */
export async function readCard(source, { hash }) {
  // One reader per read: its forward history belongs to a single read (see XrplFileReader).
  const reader = new XrplFileReader(source);
  let record;
  try {
    record = await reader.manifest({ txHash: hash });
  } catch (error) {
    // Only a manifest that is definitely not on the network is "missing"; a node that lacks the
    // history, does not answer or returns garbage may do better next time.
    return { card: { status: error?.code === 'ManifestNotFound' ? 'missing' : 'unavailable' }, cover: null };
  }
  const manifest = record.manifest;
  const words = { status: 'file', title: cardTitle(manifest, hash), description: describeFile(manifest) };
  if (isFilePictureCandidate(manifest)) return { card: { ...words, picture: 'file' }, cover: null };
  const cover = await reader.readCover(record);
  if (cover === null) return { card: { ...words, picture: 'banner' }, cover: null };
  return { card: { ...words, picture: 'cover' }, cover: { bytes: cover.bytes, type: cover.type } };
}

/**
 * The card picture: the file itself when it is a picture of a card type under the ceiling and its
 * bytes begin as its type says, else its cover, else null.
 * @returns {Promise<{ bytes: Uint8Array, type: string } | null>}
 */
export async function readPicture(source, { hash }) {
  try {
    const reader = new XrplFileReader(source, { maxBytes: FILE_PICTURE_MAX_BYTES });
    const record = await reader.manifest({ txHash: hash });
    const manifest = record.manifest;
    if (isFilePictureCandidate(manifest)) {
      try {
        const { bytes } = await reader.read({ txHash: hash });
        if (pictureMagicAgrees(manifest.type, bytes)) return { bytes, type: manifest.type };
      } catch {
        // A file that fails to read or verify still has its cover to offer.
      }
    }
    const cover = await reader.readCover(record);
    return cover === null ? null : { bytes: cover.bytes, type: cover.type };
  } catch {
    return null;
  }
}
