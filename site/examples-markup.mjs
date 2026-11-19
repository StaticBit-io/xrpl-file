// The cards of the examples page, written by the build in place of <!--@examples-->. Static HTML: the
// <xrpl-file> element inside loads the file in the browser, the rest is readable without script.
import { escapeHtml } from './chrome-markup.mjs';
import { snippetHtml } from './snippet.mjs';
import { EXAMPLES } from './src/js/examples.mjs';
import { EXAMPLE_CODE, EXAMPLE_FILE_LINK, EXAMPLE_STRINGS } from './src/js/examples-strings.mjs';

export const EXAMPLES_MARKER = '<!--@examples-->';

const LINK = /^xrpl-file:\/\/([a-z0-9]+)\/([0-9A-Fa-f]{64})$/;
// An SVG can carry script and the element offers it as a download; a text file has no picture.
// Neither has a size to reserve.
const BOXED = new Set(['drawing', 'video', 'logo']);

// Cloudflare's email obfuscation reads "xrpl-file-element@<version>/xrpl-file-element.js" as an
// address; it leaves the text between these comments alone (see inlineSnippet in build.mjs).
const EMAIL_OFF = '<!--email_off-->';
const EMAIL_ON = '<!--/email_off-->';

const CODE_ICON =
  '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 7l-5 5 5 5M16 7l5 5-5 5M14 4l-4 16"/></svg>';
const CLOSE_ICON =
  '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 6l12 12M18 6L6 18"/></svg>';

/** The "</>" button laid over the preview; examples-page.mjs opens the card's dialog from it. */
function codeButton(id, title, code) {
  const name = escapeHtml(code.show(title));
  return `<button class="code-button" type="button" aria-haspopup="dialog" aria-controls="${id}" aria-label="${name}" title="${name}">${CODE_ICON}</button>`;
}

/** The card's embed code in a modal dialog: the pinned script and this example's element, ready to copy. */
function codeDialog(example, id, title, code, snippet) {
  return [
    `  <dialog class="code-dialog" id="${id}" aria-labelledby="${id}-title">`,
    '    <div class="code-dialog__head">',
    `      <h2 class="code-dialog__title" id="${id}-title">${escapeHtml(code.code(title))}</h2>`,
    `      <button class="code-dialog__close" type="button" aria-label="${escapeHtml(code.close)}">${CLOSE_ICON}</button>`,
    '    </div>',
    `    <pre tabindex="0"><code>${EMAIL_OFF}${snippetHtml({ ...snippet, link: example.link })}${EMAIL_ON}</code></pre>`,
    '    <div class="snippet__bar">',
    `      <button class="button code-dialog__copy" type="button" aria-label="${escapeHtml(code.copyLabel(title))}">${escapeHtml(code.copy)}</button>`,
    '      <span class="snippet__status" role="status"></span>',
    '    </div>',
    '  </dialog>',
  ].join('\n');
}

function card(example, strings, linkText, code, snippet) {
  const match = LINK.exec(example.link);
  if (!match) throw new Error(`example "${example.key}" has a malformed link: ${example.link}`);
  const words = strings[example.key];
  if (!words) throw new Error(`no strings for example "${example.key}"`);
  const element = `<xrpl-file src="${escapeHtml(example.link)}" loading="lazy"></xrpl-file>`;
  const preview =
    BOXED.has(example.kind) && example.width && example.height
      ? `<div class="example__box" style="aspect-ratio: ${example.width} / ${example.height}">${element}</div>`
      : element;
  const id = `code-${escapeHtml(example.key)}`;
  const media = snippet ? `<div class="example__media">${preview}${codeButton(id, words.title, code)}</div>` : preview;
  const href = `/${match[1]}/${match[2].toUpperCase()}`;
  return [
    `<figure class="example example--${escapeHtml(example.kind)}">`,
    `  ${media}`,
    '  <figcaption>',
    `    <h2>${escapeHtml(words.title)}</h2>`,
    `    <p>${escapeHtml(words.line)}</p>`,
    `    <a href="${escapeHtml(href)}">${escapeHtml(linkText)}</a>`,
    '  </figcaption>',
    ...(snippet ? [codeDialog(example, id, words.title, code, snippet)] : []),
    '</figure>',
  ].join('\n');
}

/**
 * @param {string} lang
 * @param {{ examples?: typeof EXAMPLES, strings?: Record<string, {title: string, line: string}>, linkText?: string,
 *   snippet?: { version: string, integrity: string } }} [overrides]
 *   a test's own list and words; `snippet` (the element version and its integrity) adds each card's embed code
 */
export function renderExamples(lang, overrides = {}) {
  const strings = overrides.strings ?? EXAMPLE_STRINGS[lang];
  const linkText = overrides.linkText ?? EXAMPLE_FILE_LINK[lang];
  if (!strings || linkText === undefined) throw new Error(`no example strings for "${lang}"`);
  const code = EXAMPLE_CODE[lang];
  if (overrides.snippet && !code) throw new Error(`no embed code words for "${lang}"`);
  return (overrides.examples ?? EXAMPLES).map((example) => card(example, strings, linkText, code, overrides.snippet)).join('\n');
}
