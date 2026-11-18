// What makes a request a card request and a file a card picture. No I/O and no platform API, so the
// same rules run under the Pages Function and under Node.
import { coverMagicAgrees } from '../../web/src/cover.mjs';

// Substrings of the user agents of link-preview robots, lower case. A person's browser matches none:
// they all name a robot product, never an engine.
const ROBOTS = ['telegrambot', 'twitterbot', 'facebookexternalhit', 'discordbot', 'slackbot', 'linkedinbot'];
// WhatsApp's in-app browser carries "WhatsApp/<version>" at the end of a browser's user agent; only the
// link fetcher's user agent begins with it.
const WHATSAPP_FETCHER = 'whatsapp/';

/** Whether the user agent is a link-preview robot that reads og/twitter tags and runs no script. */
export function isLinkPreviewRobot(userAgent) {
  if (!userAgent) return false;
  const lower = userAgent.toLowerCase();
  return lower.startsWith(WHATSAPP_FETCHER) || ROBOTS.some((robot) => lower.includes(robot));
}

// Only the named networks: a NetworkID has no built-in node to read from.
const PAGE_PATH = /^\/(mainnet|testnet|devnet)\/([0-9A-Fa-f]{64})\/?$/;
const PICTURE_PATH = /^\/og\/(mainnet|testnet|devnet)\/([0-9A-Fa-f]{64})$/;

/**
 * The card a path asks for: `page` for the file page /<network>/<hash>[/], `picture` for
 * /og/<network>/<hash>; null for any other path. The hash comes back upper case, as links carry it.
 */
export function parseCardPath(pathname) {
  const page = PAGE_PATH.exec(pathname);
  if (page) return { kind: 'page', network: page[1], hash: page[2].toUpperCase() };
  const picture = PICTURE_PATH.exec(pathname);
  if (picture) return { kind: 'picture', network: picture[1], hash: picture[2].toUpperCase() };
  return null;
}

// SVG is not here: neither Telegram nor X renders it as a card picture.
export const FILE_PICTURE_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif'];
// About 0.3 s of CPU to read and verify (spec measurements), and far more than a preview needs.
export const FILE_PICTURE_MAX_BYTES = 1_000_000;

/** Whether the file itself may be its card picture, judged from the manifest before any byte is read. */
export function isFilePictureCandidate(manifest) {
  return FILE_PICTURE_TYPES.includes(manifest.type) && manifest.size <= FILE_PICTURE_MAX_BYTES;
}

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const startsWith = (bytes, head) => bytes.length >= head.length && head.every((b, i) => bytes[i] === b);
const ascii = (text) => [...text].map((c) => c.charCodeAt(0));

/**
 * Whether the bytes begin with the signature of the declared type. The hash proves the bytes are the
 * published ones, not that they are a picture, and /og/ serves them under the manifest's type.
 */
export function pictureMagicAgrees(type, bytes) {
  if (type === 'image/png') return startsWith(bytes, PNG_SIGNATURE);
  if (type === 'image/gif') return startsWith(bytes, ascii('GIF87a')) || startsWith(bytes, ascii('GIF89a'));
  if (type === 'image/jpeg' || type === 'image/webp') return coverMagicAgrees(type, bytes);
  return false;
}
