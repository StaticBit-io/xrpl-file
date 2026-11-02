// Build-time markup for the shared head, header and footer. build.mjs replaces a marker comment in a
// page with the matching part, so no page repeats this markup and none can drift from another.
import { equivalentPath, THEME_KEY } from './src/js/prefs.mjs';
import { LANGUAGE_NAMES, STRINGS } from './src/js/chrome-strings.mjs';

export const REPOSITORY_URL = 'https://github.com/StaticBit-io/xrpl-file';
export const SPECIFICATION_URL = 'https://github.com/StaticBit-io/xrpl-file/blob/main/docs/spec/xrpl-file-v1.md';

const LANGS = ['en', 'ru'];

/**
 * Runs in <head> before first paint so the page never flashes the wrong theme. The same rule as
 * getTheme in prefs.mjs (only a stored "light" gives light, everything else is dark), written out
 * because a classic inline script cannot import; the storage key comes from prefs.mjs.
 */
export function prePaintScript() {
  return (
    `(function(){var t="dark";try{if(localStorage.getItem(${JSON.stringify(THEME_KEY)})==="light")t="light"}catch(e){}` +
    'document.documentElement.dataset.theme=t})()'
  );
}

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * @param {'head'|'header'|'footer'} part
 * @param {{ lang: 'en'|'ru', pagePath: string, langFromStorage: boolean }} page
 *   pagePath is the page's own URL path; langFromStorage marks a page with one URL for both
 *   languages, whose chrome text chrome.mjs switches in place.
 */
export function renderChrome(part, page) {
  if (part === 'head') return renderHead(page);
  if (part === 'header') return renderHeader(page);
  if (part === 'footer') return renderFooter(page);
  throw new Error(`unknown chrome part: ${part}`);
}

function renderHead({ lang, langFromStorage }) {
  const preloads = ['geologica-latin'];
  if (lang === 'ru' && !langFromStorage) preloads.push('geologica-cyrillic');
  return [
    `<script>${prePaintScript()}</script>`,
    ...preloads.map((name) => `<link rel="preload" href="/fonts/${name}.woff2" as="font" type="font/woff2" crossorigin>`),
    '<link rel="stylesheet" href="/css/site.css">',
    '<script type="module" src="/js/chrome.mjs"></script>',
  ].join('\n');
}

function labels(page) {
  const strings = STRINGS[page.lang];
  const text = (key) => escapeHtml(strings[key]);
  const i18n = (key) => (page.langFromStorage ? ` data-i18n="${key}"` : '');
  const i18nLabel = (key) => (page.langFromStorage ? ` data-i18n-label="${key}"` : '');
  return { text, i18n, i18nLabel };
}

function langLinks(page) {
  return LANGS.map((lang) => {
    // No script: a stored-language page falls back to the two homes; with script chrome.mjs
    // replaces both hrefs with the page's own URL.
    const href = page.langFromStorage ? (lang === 'ru' ? '/ru/' : '/') : equivalentPath(page.pagePath, lang);
    const current = !page.langFromStorage && lang === page.lang ? ' aria-current="true"' : '';
    return `<li><a data-lang-link="${lang}" href="${href}" lang="${lang}" hreflang="${lang}"${current}>${LANGUAGE_NAMES[lang]}</a></li>`;
  }).join('\n');
}

function themeToggle(page) {
  const { text, i18n } = labels(page);
  return (
    '<button type="button" class="theme-toggle" data-theme-toggle aria-pressed="false">' +
    '<svg class="theme-toggle__icon" width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">' +
    '<rect x="1" y="1" width="14" height="14" rx="2"/><path d="M8 1h5a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H8z"/></svg>' +
    `<span${i18n('lightTheme')}>${text('lightTheme')}</span></button>`
  );
}

// A 3x3 patch of the cell motif: three cells read, one verified.
const MARK_CELLS = [
  [0, 0, 'is-lit'], [1, 0, 'is-lit'], [2, 0, ''],
  [0, 1, ''], [1, 1, 'is-lit'], [2, 1, 'is-lit'],
  [0, 2, ''], [1, 2, ''], [2, 2, 'is-verified'],
];

function wordmarkMark() {
  const rects = MARK_CELLS.map(([column, row, state]) => {
    const cls = state ? ` class="${state}"` : '';
    return `<rect${cls} x="${0.5 + column * 6.5}" y="${0.5 + row * 6.5}" width="5" height="5" rx="0.5"/>`;
  }).join('');
  return `<svg class="wordmark__mark" width="22" height="22" viewBox="0 0 19 19" aria-hidden="true">${rects}</svg>`;
}

function renderHeader(page) {
  const { text, i18n, i18nLabel } = labels(page);
  const home = page.lang === 'ru' ? '/ru/' : '/';
  return `<a class="skip-link" href="#main"${i18n('skip')}>${text('skip')}</a>
<header class="site-header">
<div class="container site-header__bar">
<a class="wordmark" href="${home}" aria-label="${text('homeLabel')}"${i18nLabel('homeLabel')}>${wordmarkMark()}<span aria-hidden="true">xrpl-file</span></a>
<div class="site-header__tools">
<nav aria-label="${text('languageLabel')}"${i18nLabel('languageLabel')}>
<ul class="lang-switch">
${langLinks(page)}
</ul>
</nav>
${themeToggle(page)}
</div>
</div>
</header>`;
}

function renderFooter(page) {
  const { text, i18n } = labels(page);
  return `<footer class="site-footer">
<div class="container site-footer__grid">
<div>
<p class="site-footer__label"${i18n('projectLabel')}>${text('projectLabel')}</p>
<ul class="site-footer__list">
<li><a href="${REPOSITORY_URL}"${i18n('repository')}>${text('repository')}</a></li>
<li><a href="${SPECIFICATION_URL}"${i18n('specification')}>${text('specification')}</a></li>
</ul>
</div>
<div role="group" aria-labelledby="footer-language">
<p class="site-footer__label" id="footer-language"${i18n('languageLabel')}>${text('languageLabel')}</p>
<ul class="lang-switch">
${langLinks(page)}
</ul>
</div>
<div role="group" aria-labelledby="footer-theme">
<p class="site-footer__label" id="footer-theme"${i18n('themeLabel')}>${text('themeLabel')}</p>
${themeToggle(page)}
</div>
</div>
</footer>`;
}
