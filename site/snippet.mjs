// The embed snippet shown in "For developers". It pins one element version with its integrity value
// and loads a classic script (never type="module": the element finds mp4box.min.js beside
// document.currentScript) from the jsDelivr npm URL. The site's own copy under /element/ serves its
// pages; the snippet does not offer it, since a CDN outlives this site.
import { ELEMENT_PACKAGE, HERO_LINK } from './src/js/config.mjs';

export const ELEMENT_FILE = 'xrpl-file-element.js';

export function npmUrl(version) {
  return `https://cdn.jsdelivr.net/npm/${ELEMENT_PACKAGE}@${version}/${ELEMENT_FILE}`;
}

// Element content only: quotes stay as they are, so the text a reader copies is the snippet itself.
function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function snippetOf(url, integrity) {
  return [
    `<script src="${url}"`,
    `        integrity="${integrity}"`,
    '        crossorigin="anonymous" defer></script>',
    `<xrpl-file src="${HERO_LINK}"></xrpl-file>`,
  ].join('\n');
}

/** The snippet, escaped for the inside of a <code> element. */
export function snippetHtml({ version, integrity }) {
  return escapeHtml(snippetOf(npmUrl(version), integrity));
}
