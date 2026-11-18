// The cards of the examples page, written by the build in place of <!--@examples-->. Static HTML: the
// <xrpl-file> element inside loads the file in the browser, the rest is readable without script.
import { escapeHtml } from './chrome-markup.mjs';
import { EXAMPLES } from './src/js/examples.mjs';
import { EXAMPLE_FILE_LINK, EXAMPLE_STRINGS } from './src/js/examples-strings.mjs';

export const EXAMPLES_MARKER = '<!--@examples-->';

const LINK = /^xrpl-file:\/\/([a-z0-9]+)\/([0-9A-Fa-f]{64})$/;
// An SVG can carry script and the element offers it as a download; a text file has no picture.
// Neither has a size to reserve.
const BOXED = new Set(['drawing', 'video', 'logo']);

function card(example, strings, linkText) {
  const match = LINK.exec(example.link);
  if (!match) throw new Error(`example "${example.key}" has a malformed link: ${example.link}`);
  const words = strings[example.key];
  if (!words) throw new Error(`no strings for example "${example.key}"`);
  const element = `<xrpl-file src="${escapeHtml(example.link)}" loading="lazy"></xrpl-file>`;
  const media =
    BOXED.has(example.kind) && example.width && example.height
      ? `<div class="example__box" style="aspect-ratio: ${example.width} / ${example.height}">${element}</div>`
      : element;
  const href = `/${match[1]}/${match[2].toUpperCase()}`;
  return [
    `<figure class="example example--${escapeHtml(example.kind)}">`,
    `  ${media}`,
    '  <figcaption>',
    `    <h2>${escapeHtml(words.title)}</h2>`,
    `    <p>${escapeHtml(words.line)}</p>`,
    `    <a href="${escapeHtml(href)}">${escapeHtml(linkText)}</a>`,
    '  </figcaption>',
    '</figure>',
  ].join('\n');
}

/**
 * @param {string} lang
 * @param {{ examples?: typeof EXAMPLES, strings?: Record<string, {title: string, line: string}>, linkText?: string }} [overrides]
 *   a test's own list and words
 */
export function renderExamples(lang, overrides = {}) {
  const strings = overrides.strings ?? EXAMPLE_STRINGS[lang];
  const linkText = overrides.linkText ?? EXAMPLE_FILE_LINK[lang];
  if (!strings || linkText === undefined) throw new Error(`no example strings for "${lang}"`);
  return (overrides.examples ?? EXAMPLES).map((example) => card(example, strings, linkText)).join('\n');
}
