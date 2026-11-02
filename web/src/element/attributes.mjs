import { ENDPOINTS } from '../ledger.mjs';
import { BAD_NODE, NO_NODE, elementError } from './messages.mjs';

const NUMBER = '(?:\\d+(?:\\.\\d+)?|\\.\\d+)';
const BARE = new RegExp(`^${NUMBER}$`);
// A closed list of units and a bare number: the value is written into a style rule, so nothing else may pass.
const LENGTH = new RegExp(`^${NUMBER}(?:px|%|vh|vw|vmin|vmax|em|rem|ch|cm|mm|in|pt|pc)$`, 'i');
const LOADING = new Set(['lazy', 'eager', 'click']);
const THEMES = new Set(['dark', 'light', 'auto']);
const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1']);

/** A `width` or `height` attribute as a CSS length: a bare number is pixels, as on <img>; null when it is not a plain length. */
export function parseLength(value) {
  if (typeof value !== 'string') return null;
  const text = value.trim();
  if (BARE.test(text)) return `${text}px`;
  if (text.toLowerCase() === 'auto') return 'auto';
  return LENGTH.test(text) ? text : null;
}

export function parseLoading(value) {
  const text = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return LOADING.has(text) ? text : 'lazy';
}

/** The `theme` attribute: `dark`, `light` or `auto`; anything else is the default, `dark`, so an embedded block never changes look by accident. */
export function parseTheme(value) {
  const text = typeof value === 'string' ? value.trim().toLowerCase() : '';
  return THEMES.has(text) ? text : 'dark';
}

/** The `background` attribute: `transparent` drops the box behind the file; any other value, or none, keeps the theme's. */
export function backgroundTransparent(value) {
  return typeof value === 'string' && value.trim().toLowerCase() === 'transparent';
}

/** The `caption` attribute: `off` hides the hover caption; any other value, or none, keeps it. */
export function captionHidden(value) {
  return typeof value === 'string' && value.trim().toLowerCase() === 'off';
}

export function parseLoader(value) {
  return !(typeof value === 'string' && value.trim().toLowerCase() === 'false');
}

/** The link text: `src` when it has any, else the element's own text, so `<xrpl-file>xrpl-file://…</xrpl-file>` works. */
export function linkText({ src, text }) {
  const fromSrc = typeof src === 'string' ? src.trim() : '';
  if (fromSrc) return fromSrc;
  const fromText = typeof text === 'string' ? text.trim() : '';
  return fromText || null;
}

function nodeUrl(node) {
  let url;
  try {
    url = new URL(node);
  } catch {
    throw elementError('NodeUnreachable', BAD_NODE);
  }
  // Plain ws:// is for local testing only; anywhere else it would send the reads in the clear.
  const allowed = url.protocol === 'wss:' || (url.protocol === 'ws:' && LOCAL_HOSTS.has(url.hostname));
  if (!allowed) throw elementError('NodeUnreachable', BAD_NODE);
  return node;
}

/** The nodes to read from: the `node` attribute when given, else the built-in endpoints of the link's network. */
export function endpointsFor(link, node) {
  const given = typeof node === 'string' ? node.trim() : '';
  if (given) return [nodeUrl(given)];
  const builtIn = Object.hasOwn(ENDPOINTS, link.network) ? ENDPOINTS[link.network] : null;
  if (!builtIn) throw elementError('NodeUnreachable', NO_NODE);
  return [...builtIn];
}
