import { normalizeType } from './view.mjs';
import { stringsFor } from './strings.mjs';

const EXTENSIONS = {
  'image/png': 'png',
  'image/jpeg': 'jpg',
  'image/gif': 'gif',
  'image/webp': 'webp',
  'image/avif': 'avif',
  'video/mp4': 'mp4',
  'video/webm': 'webm',
  'video/quicktime': 'mov',
  'text/plain': 'txt',
  'text/csv': 'csv',
  'text/markdown': 'md',
  'application/json': 'json',
  'text/html': 'html',
  'text/xml': 'xml',
  'image/svg+xml': 'svg',
  'application/pdf': 'pdf',
};

const UNIT_KEYS = ['unitB', 'unitKB', 'unitMB', 'unitGB', 'unitTB'];
const MAX_NAME = 200;
// Past this the "extension" is just a long tail of the name, not something worth protecting from the cut.
const MAX_EXTENSION = 32;
// C0 and C1 controls, line/paragraph separators and every format character (\p{Cf}: bidi overrides
// and isolates, zero-width characters, tags). A right-to-left override turns "invoice<RLO>fdp.exe"
// into a name that reads "invoiceexe.pdf", so none of these reach a caption or a save dialog.
const CONTROL = /[\u0000-\u001f\u007f-\u009f\p{Zl}\p{Zp}\p{Cf}]/gu;
const FORBIDDEN = /[/\\:*?"<>|]/g;
const EDGE = /^[ .]+|[ .]+$/g;
const DEVICE = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;

export function extensionFor(type) {
  return Object.hasOwn(EXTENSIONS, normalizeType(type)) ? EXTENSIONS[normalizeType(type)] : 'bin';
}

/**
 * Decimal units with one decimal, as file managers and browsers show them: 999 B, 12.4 KB, 36.5 MB.
 * The unit and the decimal mark follow `lang` the way the site writes sizes (12,4 МБ in Russian, 12,4 MB
 * in German, 12.4 MB in English and Chinese). Done by hand and not with Intl, so the text does not depend
 * on the ICU data of the browser.
 */
export function formatBytes(n, lang = 'en') {
  const s = stringsFor(lang);
  if (n < 1000) return `${n} ${s.unitB}`;
  let value = n / 1000;
  let unit = 1;
  while (Number(value.toFixed(1)) >= 1000 && unit < UNIT_KEYS.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${value.toFixed(1).replace('.', s.decimalMark)} ${s[UNIT_KEYS[unit]]}`;
}

function cut(name) {
  if (name.length <= MAX_NAME) return name;
  const dot = name.lastIndexOf('.');
  const extension = dot > 0 && name.length - dot <= MAX_EXTENSION ? name.slice(dot) : '';
  let stem = name.slice(0, MAX_NAME - extension.length);
  // Do not leave half of a surrogate pair at the cut.
  if (/[\uD800-\uDBFF]$/.test(stem)) stem = stem.slice(0, -1);
  return extension ? stem + extension : stem.replace(EDGE, '');
}

/**
 * The file name to save under. The manifest name is attacker-chosen, so it is reduced to one safe path
 * segment: no separators or reserved characters, no leading dots, no Windows device names, no invisible
 * or direction-changing characters. The element shows this same string, so the visible name cannot be
 * spoofed either.
 */
export function downloadName(manifest) {
  let name = typeof manifest.name === 'string' ? manifest.name : '';
  name = name.replace(CONTROL, '').replace(FORBIDDEN, '_').replace(EDGE, '');
  // Windows treats "con.txt" and "con .txt" as the device, whatever the extension.
  if (DEVICE.test(name.split('.')[0].trimEnd())) name = `_${name}`;
  name = cut(name);
  return name || `xrpl-file.${extensionFor(manifest.type)}`;
}

/** "12.4 of 36.5 MB": the unit is written once when both numbers share it. */
export function bytesOf(done, total, lang = 'en') {
  const [doneValue, doneUnit] = formatBytes(done, lang).split(' ');
  const totalText = formatBytes(total, lang);
  const doneText = totalText.endsWith(` ${doneUnit}`) ? doneValue : formatBytes(done, lang);
  return stringsFor(lang).bytesOf(doneText, totalText);
}

/**
 * The loader's two lines. They are separate so that a narrow box can keep the bytes and drop the
 * blocks; the blocks text carries its own separator so the two read as one line when both show.
 */
export function progressLines({ bytesDone, bytesTotal, blocksVerified, blocksTotal }, lang = 'en') {
  return {
    bytes: bytesOf(bytesDone, bytesTotal, lang),
    blocks: stringsFor(lang).blocksVerified(blocksVerified, blocksTotal),
  };
}

const BADGE_MAX = 5;
const VENDOR_PREFIX = /^(?:vnd\.|prs\.|x-|x\.)/;

/**
 * A short tag for the file type in a box too small for the name: the media subtype in capitals, such
 * as SVG for image/svg+xml, at most five characters. The type comes from the file's manifest, so only
 * letters and digits survive; anything that leaves nothing is FILE.
 */
export function typeBadge(type) {
  const normalized = normalizeType(type);
  const slash = normalized.indexOf('/');
  const subtype = slash < 1 ? '' : normalized.slice(slash + 1);
  const core = subtype.replace(VENDOR_PREFIX, '').split('+')[0].replace(/[^a-z0-9]/g, '');
  return core ? core.slice(0, BADGE_MAX).toUpperCase() : 'FILE';
}

/** The accessible name of the download button, which in the smallest box shows only an icon. */
export function downloadLabel(manifest, lang = 'en') {
  return stringsFor(lang).downloadLabel(downloadName(manifest));
}
