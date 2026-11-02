// The embed snippets shown in "For developers". Both pin one element version with its integrity value
// and load a classic script (never type="module": the element finds mp4box.min.js beside
// document.currentScript). The first is the jsDelivr npm URL, the second the same file on this site.
import { ELEMENT_PACKAGE, HERO_LINK, SITE_ORIGIN } from './src/js/config.mjs';

export const ELEMENT_FILE = 'xrpl-file-element.js';

export function npmUrl(version) {
  return `https://cdn.jsdelivr.net/npm/${ELEMENT_PACKAGE}@${version}/${ELEMENT_FILE}`;
}

export function siteUrl(version) {
  return `${SITE_ORIGIN}/element/${version}/${ELEMENT_FILE}`;
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

/** The primary snippet, escaped for the inside of a <code> element. */
export function snippetHtml({ version, integrity }) {
  return escapeHtml(snippetOf(npmUrl(version), integrity));
}

/** The alternative snippet (this site's own copy of the same file). */
export function alternativeSnippetHtml({ version, integrity }) {
  return escapeHtml(snippetOf(siteUrl(version), integrity));
}
