import { normalizeType } from './view.mjs';

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

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
const MAX_NAME = 200;
// Past this the "extension" is just a long tail of the name, not something worth protecting from the cut.
const MAX_EXTENSION = 32;
const CONTROL = /[\u0000-\u001f\u007f]/g;
const FORBIDDEN = /[/\\:*?"<>|]/g;
const EDGE = /^[ .]+|[ .]+$/g;
const DEVICE = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;

export function extensionFor(type) {
  return Object.hasOwn(EXTENSIONS, normalizeType(type)) ? EXTENSIONS[normalizeType(type)] : 'bin';
}

/** Decimal units with one decimal, as file managers and browsers show them: 999 B, 12.4 KB, 36.5 MB. */
export function formatBytes(n) {
  if (n < 1000) return `${n} B`;
  let value = n / 1000;
  let unit = 1;
  while (Number(value.toFixed(1)) >= 1000 && unit < UNITS.length - 1) {
    value /= 1000;
    unit++;
  }
  return `${value.toFixed(1)} ${UNITS[unit]}`;
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
 * segment: no separators or reserved characters, no leading dots, no Windows device names.
 */
export function downloadName(manifest) {
  let name = typeof manifest.name === 'string' ? manifest.name : '';
  name = name.replace(CONTROL, '').replace(FORBIDDEN, '_').replace(EDGE, '');
  // Windows treats "con.txt" and "con .txt" as the device, whatever the extension.
  if (DEVICE.test(name.split('.')[0].trimEnd())) name = `_${name}`;
  name = cut(name);
  return name || `xrpl-file.${extensionFor(manifest.type)}`;
}
