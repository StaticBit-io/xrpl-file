// Build-time markup for the shared head, header and footer. build.mjs replaces a marker comment in a
// page with the matching part, so no page repeats this markup and none can drift from another.
import { SITE_ORIGIN } from './src/js/config.mjs';
import { ACTIVE_LANGS, DEFAULT_LANG, LANGUAGE_NAMES, htmlLang, homePath } from './src/js/languages.mjs';
import { equivalentPath, LANG_KEY, THEME_KEY } from './src/js/prefs.mjs';
import { STRINGS } from './src/js/chrome-strings.mjs';

export const REPOSITORY_URL = 'https://github.com/StaticBit-io/xrpl-file';
export const SPECIFICATION_URL = 'https://github.com/StaticBit-io/xrpl-file/blob/main/docs/spec/xrpl-file-v1.md';

/**
 * The first-visit language rule, written out for the inline script: only on the English home, send the
 * visitor to the language getLang would choose (the stored choice if it is active, else the first
 * browser language whose primary subtag is active), unless that is English. A visitor who arrives from
 * this site is never moved: with storage blocked, the one who just chose English in the menu would
 * bounce straight back. Every read is guarded, like the rest of prefs.mjs.
 */
function languageRedirect(langs) {
  const homes = JSON.stringify(Object.fromEntries(langs.map((lang) => [lang, homePath(lang)])));
  return (
    'try{if(location.pathname==="/"){' +
    `var H=${homes},R=document.referrer,S=false;` +
    'try{S=!!R&&new URL(R).origin===location.origin}catch(e){}' +
    'if(!S){var K=null,G=null;' +
    `try{K=localStorage.getItem(${JSON.stringify(LANG_KEY)})}catch(e){}` +
    'if(K&&H.hasOwnProperty(K))G=K;else{' +
    'var L=navigator.languages&&navigator.languages.length?navigator.languages:[navigator.language];' +
    'for(var i=0;i<L.length;i++){var C=String(L[i]||"").toLowerCase().split(/[-_]/)[0];if(H.hasOwnProperty(C)){G=C;break}}}' +
    `if(G&&G!==${JSON.stringify(DEFAULT_LANG)})location.replace(H[G]+location.search+location.hash)}}}catch(e){}`
  );
}

/**
 * Runs in <head> before first paint so the page never flashes the wrong theme (the same rule as
 * getTheme in prefs.mjs: only a stored "light" gives light, everything else is dark) and so a first
 * visit to the English home can move to the visitor's language before anything is drawn. A classic
 * inline script cannot import, so both rules are written out here; the storage keys and the language
 * list come from prefs.mjs and languages.mjs, and tests pin the language rule against getLang.
 * @param {string[]} [langs] the active languages
 */
export function prePaintScript(langs = ACTIVE_LANGS) {
  return (
    `(function(){${languageRedirect(langs)}var t="dark";try{if(localStorage.getItem(${JSON.stringify(THEME_KEY)})==="light")t="light"}catch(e){}` +
    'document.documentElement.dataset.theme=t})()'
  );
}

export function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * @param {'head'|'header'|'footer'} part
 * @param {{ lang: string, pagePath: string, langFromStorage: boolean }} page
 *   lang is a site language code (en, ru, ...); pagePath is the page's own URL path; langFromStorage
 *   marks a page with one URL for every language, whose chrome text chrome.mjs switches in place.
 */
export function renderChrome(part, page) {
  if (part === 'head') return renderHead(page);
  if (part === 'header') return renderHeader(page);
  if (part === 'footer') return renderFooter(page);
  throw new Error(`unknown chrome part: ${part}`);
}

/**
 * The canonical link and the hreflang alternates of a page that exists in several languages, one per
 * language plus x-default (the English page, which is where a visitor with no matching language
 * belongs). Written at build time from the language list, so no page spells them by hand.
 */
export function languageAlternates(pagePath, langs = ACTIVE_LANGS) {
  const url = (lang) => `${SITE_ORIGIN}${equivalentPath(pagePath, lang)}`;
  return [
    `<link rel="canonical" href="${SITE_ORIGIN}${pagePath}">`,
    ...langs.map((lang) => `<link rel="alternate" hreflang="${htmlLang(lang)}" href="${url(lang)}">`),
    `<link rel="alternate" hreflang="x-default" href="${url('en')}">`,
  ].join('\n');
}

/** The icon links every page carries, and the root files they name. */
export const ICON_LINKS = [
  '<link rel="icon" href="/favicon.ico" sizes="32x32">',
  '<link rel="icon" href="/favicon.svg" type="image/svg+xml">',
  '<link rel="apple-touch-icon" href="/apple-touch-icon.png">',
];

/** The card banner every page names; site/tools/og-banner.mjs draws it at this size. */
export const CARD_IMAGE = { path: '/og-banner.png', width: 1200, height: 630 };

/** The banner as a card image at `origin`, with the size it is drawn at. */
export function bannerImage(origin = SITE_ORIGIN) {
  return { url: `${origin}${CARD_IMAGE.path}`, width: CARD_IMAGE.width, height: CARD_IMAGE.height, alt: 'xrpl-file' };
}

/**
 * Open Graph and Twitter card tags, from the page's own <title> and description so no page states its
 * card twice. `title` is the <title> text as written (entities kept), `description` the attribute value
 * as written. Twitter falls back to og:title, og:description and og:image, so only twitter:card is its own.
 * A file card (site/cards/html.mjs) passes its own `url`, `image` and `twitterCard`; an image's width
 * and height are written only when it has them, and its alt text is already escaped.
 */
export function cardTags({
  title, description, pagePath, langFromStorage,
  // The file page is served at every file link and the 404 at every missing one: neither has a URL of its own.
  url = langFromStorage ? null : `${SITE_ORIGIN}${pagePath}`,
  image = bannerImage(),
  twitterCard = 'summary_large_image',
}) {
  return [
    '<meta property="og:type" content="website">',
    '<meta property="og:site_name" content="xrpl-file">',
    `<meta property="og:title" content="${title.replace(/"/g, '&quot;')}">`,
    `<meta property="og:description" content="${description}">`,
    ...(url === null ? [] : [`<meta property="og:url" content="${url}">`]),
    `<meta property="og:image" content="${image.url}">`,
    ...(image.width === undefined ? [] : [`<meta property="og:image:width" content="${image.width}">`]),
    ...(image.height === undefined ? [] : [`<meta property="og:image:height" content="${image.height}">`]),
    `<meta property="og:image:alt" content="${image.alt}">`,
    `<meta name="twitter:card" content="${twitterCard}">`,
  ].join('\n');
}

function renderHead({ lang, pagePath, langFromStorage, title = null, description = null }) {
  const preloads = ['geologica-latin'];
  if (lang === 'ru' && !langFromStorage) preloads.push('geologica-cyrillic');
  return [
    // A page with one URL for every language (the file page, the 404) belongs to no single language,
    // so it declares neither a canonical nor alternates.
    ...(langFromStorage ? [] : [languageAlternates(pagePath)]),
    ...(title !== null && description !== null ? [cardTags({ title, description, pagePath, langFromStorage })] : []),
    `<script>${prePaintScript()}</script>`,
    ...preloads.map((name) => `<link rel="preload" href="/fonts/${name}.woff2" as="font" type="font/woff2" crossorigin>`),
    // The .ico first for browsers that take the first icon they can read; the svg wins where it is
    // understood. Both and the touch icon are written by site/tools/favicon.mjs.
    ...ICON_LINKS,
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
  return ACTIVE_LANGS.map((lang) => {
    // No script: a stored-language page falls back to the home pages; with script chrome.mjs
    // replaces every href with the page's own URL.
    const href = page.langFromStorage ? homePath(lang) : equivalentPath(page.pagePath, lang);
    const current = !page.langFromStorage && lang === page.lang ? ' aria-current="page"' : '';
    return `<li><a data-lang-link="${lang}" href="${href}" lang="${htmlLang(lang)}" hreflang="${htmlLang(lang)}"${current}>${LANGUAGE_NAMES[lang]}</a></li>`;
  }).join('\n');
}

// A disclosure (<details>) of plain links rather than a <select>: the links work without script and
// can be followed by a crawler, the summary is a native button that opens with Enter or Space, and the
// current language is shown on the button. chrome.mjs only adds closing on Escape and on leaving.
function langMenu(page) {
  const { text, i18n } = labels(page);
  return `<details class="lang-menu" data-lang-menu>
<summary class="lang-menu__button"><span class="visually-hidden"${i18n('languageLabel')}>${text('languageLabel')}</span> <span data-lang-current>${LANGUAGE_NAMES[page.lang]}</span><svg class="lang-menu__chevron" width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M1.5 3.5 5 7l3.5-3.5"/></svg></summary>
<ul class="lang-menu__list">
${langLinks(page)}
</ul>
</details>`;
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
export const MARK_CELLS = [
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
  const home = homePath(page.lang);
  return `<a class="skip-link" href="#main"${i18n('skip')}>${text('skip')}</a>
<header class="site-header">
<div class="container site-header__bar">
<a class="wordmark" href="${home}"${page.langFromStorage ? ' data-home' : ''} aria-label="${text('homeLabel')}"${i18nLabel('homeLabel')}>${wordmarkMark()}<span aria-hidden="true">xrpl-file</span></a>
<div class="site-header__tools">
${langMenu(page)}
${themeToggle(page)}
</div>
</div>
</header>`;
}

// A page with one URL for every language is built once, so its link names the build language's page
// and chrome.mjs repoints it when the visitor's language is another.
function examplesHref(page) {
  return equivalentPath('/examples', page.lang);
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
<li><a href="${examplesHref(page)}"${page.langFromStorage ? ' data-examples-link' : ''}${i18n('examples')}>${text('examples')}</a></li>
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
