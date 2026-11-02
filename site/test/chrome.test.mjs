import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { initChrome } from '../src/js/chrome.mjs';
import { ACTIVE_LANGS, LANGS, equivalentPath, getLang } from '../src/js/prefs.mjs';
import { LANGUAGE_NAMES, homePath, htmlLang } from '../src/js/languages.mjs';
import { renderChrome, prePaintScript, languageAlternates, REPOSITORY_URL, SPECIFICATION_URL } from '../chrome-markup.mjs';
import { STRINGS } from '../src/js/chrome-strings.mjs';
import { SITE_ORIGIN } from '../src/js/config.mjs';

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';

function memoryStorage(initial = {}) {
  const data = new Map(Object.entries(initial));
  return {
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => { data.set(key, String(value)); },
    data,
  };
}

const throwingStorage = {
  getItem() { throw new Error('blocked'); },
  setItem() { throw new Error('blocked'); },
};

class FakeElement {
  constructor(selectors, attributes = {}) {
    this.selectors = selectors;
    this.attributes = new Map(Object.entries(attributes));
    this.dataset = {};
    this.listeners = new Map();
    this.hidden = false;
    this.open = false;
    this.textContent = '';
    this.parent = null;
    this.children = [];
    this.focused = false;
  }
  contains(node) {
    for (let current = node; current; current = current.parent) if (current === this) return true;
    return false;
  }
  closest(selector) {
    for (let current = this; current; current = current.parent) if (current.selectors.includes(selector)) return current;
    return null;
  }
  querySelector(selector) { return this.children.find((child) => child.selectors.includes(selector)) ?? null; }
  focus() { this.focused = true; this.ownerDocument.activeElement = this; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  hasAttribute(name) { return this.attributes.has(name); }
  removeAttribute(name) { this.attributes.delete(name); }
  addEventListener(type, listener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
  click() {
    const event = { type: 'click', defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
    for (const listener of this.listeners.get('click') ?? []) listener(event);
    return event;
  }
}

function fakeDocument({ lang = 'en', langSource, theme } = {}) {
  const html = new FakeElement(['html'], { lang });
  if (langSource) html.setAttribute('data-lang-source', langSource);
  if (theme) html.dataset.theme = theme;
  const elements = [];
  const events = [];
  Object.defineProperty(html, 'lang', {
    get() { return this.getAttribute('lang'); },
    set(value) { this.setAttribute('lang', value); },
  });
  const doc = {
    documentElement: html,
    elements,
    events,
    activeElement: null,
    listeners: new Map(),
    addEventListener(type, listener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); },
    fire(type, event) { for (const listener of this.listeners.get(type) ?? []) listener(event); },
    querySelectorAll: (selector) => elements.filter((el) => el.selectors.includes(selector)),
    dispatchEvent: (event) => { events.push(event); return true; },
    add(selectors, attributes, dataset) {
      const el = new FakeElement(selectors, attributes);
      el.ownerDocument = doc;
      Object.assign(el.dataset, dataset);
      elements.push(el);
      return el;
    },
  };
  return doc;
}

function themeButton(doc) {
  const el = doc.add(['[data-theme-toggle]'], { 'aria-pressed': 'false' });
  el.hidden = true;
  return el;
}

function langLink(doc, lang) {
  return doc.add(['[data-lang-link]'], { href: '#' }, { langLink: lang });
}

test('theme switch toggles data-theme and every xrpl-file theme attribute', () => {
  const doc = fakeDocument({ theme: 'dark' });
  const header = themeButton(doc);
  const footer = themeButton(doc);
  const first = doc.add(['xrpl-file'], { theme: 'dark' });
  const second = doc.add(['xrpl-file']);
  const storage = memoryStorage();
  initChrome({ document: doc, location: { pathname: '/' }, storage });

  assert.equal(doc.documentElement.dataset.theme, 'dark');
  assert.equal(first.getAttribute('theme'), 'dark');
  assert.equal(header.getAttribute('aria-pressed'), 'false', 'the switch is pressed while the light theme is on');

  header.click();
  assert.equal(doc.documentElement.dataset.theme, 'light');
  assert.equal(first.getAttribute('theme'), 'light');
  assert.equal(second.getAttribute('theme'), 'light');
  assert.equal(storage.data.get('xf-theme'), 'light');
  assert.equal(header.getAttribute('aria-pressed'), 'true');
  assert.equal(footer.getAttribute('aria-pressed'), 'true', 'the footer switch follows the header one');

  footer.click();
  assert.equal(doc.documentElement.dataset.theme, 'dark');
  assert.equal(first.getAttribute('theme'), 'dark');
  assert.equal(storage.data.get('xf-theme'), 'dark');
  assert.equal(header.getAttribute('aria-pressed'), 'false');
});

test('the first visit is dark, and a remembered light theme is applied on load', () => {
  const fresh = fakeDocument({ theme: 'light' });
  themeButton(fresh);
  initChrome({ document: fresh, location: { pathname: '/' }, storage: memoryStorage() });
  assert.equal(fresh.documentElement.dataset.theme, 'dark');

  const doc = fakeDocument({ theme: 'dark' });
  const button = themeButton(doc);
  const element = doc.add(['xrpl-file']);
  initChrome({ document: doc, location: { pathname: '/' }, storage: memoryStorage({ 'xf-theme': 'light' }) });
  assert.equal(doc.documentElement.dataset.theme, 'light');
  assert.equal(element.getAttribute('theme'), 'light');
  assert.equal(button.getAttribute('aria-pressed'), 'true');
  assert.equal(button.getAttribute('data-ready'), '');
});

test('with storage that throws the page is dark and the switch still works for this page', () => {
  const doc = fakeDocument();
  const button = themeButton(doc);
  assert.doesNotThrow(() => initChrome({ document: doc, location: { pathname: '/' }, storage: throwingStorage }));
  assert.equal(doc.documentElement.dataset.theme, 'dark');
  assert.doesNotThrow(() => button.click());
  assert.equal(doc.documentElement.dataset.theme, 'light');
  assert.equal(button.getAttribute('aria-pressed'), 'true');
});

function langLinks(doc) {
  return Object.fromEntries(ACTIVE_LANGS.map((lang) => [lang, langLink(doc, lang)]));
}

test('language links point at equivalentPath, in every active language', () => {
  for (const lang of ACTIVE_LANGS) {
    const prefix = lang === 'en' ? '' : `/${lang}`;
    for (const pathname of [`${prefix}/`, `${prefix}/x`]) {
      const doc = fakeDocument({ lang: htmlLang(lang) });
      const links = langLinks(doc);
      initChrome({ document: doc, location: { pathname }, storage: memoryStorage() });
      for (const target of ACTIVE_LANGS) {
        assert.equal(links[target].getAttribute('href'), equivalentPath(pathname, target), `${target} link on ${pathname}`);
      }
      assert.equal(doc.documentElement.lang, htmlLang(lang));
    }
  }
  const doc = fakeDocument({ langSource: 'storage' });
  const links = langLinks(doc);
  const path = `/testnet/${HASH}`;
  initChrome({ document: doc, location: { pathname: path }, storage: memoryStorage() });
  for (const lang of ACTIVE_LANGS) assert.equal(links[lang].getAttribute('href'), path, 'a file page has one URL for every language');

  // The address-error view and the 404 are stored-language pages at any path; no link may become /ru/<path>.
  for (const other of ['/testnet/xyz', '/nope', '/file']) {
    const page = fakeDocument({ langSource: 'storage' });
    const pageLinks = langLinks(page);
    initChrome({ document: page, location: { pathname: other }, storage: memoryStorage() });
    for (const lang of ACTIVE_LANGS) assert.equal(pageLinks[lang].getAttribute('href'), other, `${lang} link on ${other}`);
  }
});

test('the current language link is marked and the other is not', () => {
  const doc = fakeDocument({ lang: 'ru' });
  const en = langLink(doc, 'en');
  const ru = langLink(doc, 'ru');
  initChrome({ document: doc, location: { pathname: '/ru/' }, storage: memoryStorage() });
  assert.equal(ru.getAttribute('aria-current'), 'page');
  assert.equal(en.hasAttribute('aria-current'), false);
});

test('a language link on a fixed-language page stores the choice and lets the browser navigate', () => {
  const doc = fakeDocument({ lang: 'en' });
  langLink(doc, 'en');
  const ru = langLink(doc, 'ru');
  const storage = memoryStorage();
  initChrome({ document: doc, location: { pathname: '/' }, storage });
  const event = ru.click();
  assert.equal(storage.data.get('xf-lang'), 'ru');
  assert.equal(event.defaultPrevented, false);
  assert.equal(doc.documentElement.lang, 'en', 'the page itself is replaced by navigation, not rewritten');
});

test('on the file page the switch changes the stored language in place and announces it', () => {
  const doc = fakeDocument({ lang: 'en', langSource: 'storage' });
  const en = langLink(doc, 'en');
  const ru = langLink(doc, 'ru');
  const label = doc.add(['[data-i18n]'], {}, { i18n: 'repository' });
  const aria = doc.add(['[data-i18n-label]'], {}, { i18nLabel: 'homeLabel' });
  const current = doc.add(['[data-lang-current]']);
  const storage = memoryStorage();
  const path = `/testnet/${HASH}`;
  initChrome({ document: doc, location: { pathname: path }, storage, navigatorLanguages: ['en-US'] });
  assert.equal(doc.documentElement.lang, 'en');
  assert.equal(label.textContent, STRINGS.en.repository);
  assert.equal(current.textContent, LANGUAGE_NAMES.en);

  const event = ru.click();
  assert.equal(event.defaultPrevented, true);
  assert.equal(storage.data.get('xf-lang'), 'ru');
  assert.equal(doc.documentElement.lang, 'ru');
  assert.equal(label.textContent, STRINGS.ru.repository);
  assert.equal(aria.getAttribute('aria-label'), STRINGS.ru.homeLabel);
  assert.equal(current.textContent, LANGUAGE_NAMES.ru, 'the menu button shows the language now in use');
  assert.equal(ru.getAttribute('aria-current'), 'page');
  assert.equal(en.hasAttribute('aria-current'), false);
  assert.equal(ru.getAttribute('href'), path);
  const announced = doc.events.at(-1);
  assert.equal(announced.type, 'xf-langchange');
  assert.equal(announced.detail.lang, 'ru');
});

test('on the file page the language comes from storage, then from the first browser language the site offers', () => {
  const path = `/testnet/${HASH}`;
  const stored = fakeDocument({ langSource: 'storage' });
  initChrome({ document: stored, location: { pathname: path }, storage: memoryStorage({ 'xf-lang': 'ru' }), navigatorLanguages: ['en-US'] });
  assert.equal(stored.documentElement.lang, 'ru');
  const browser = fakeDocument({ langSource: 'storage' });
  initChrome({ document: browser, location: { pathname: path }, storage: memoryStorage(), navigatorLanguages: ['fr-FR', 'ru-RU', 'en'] });
  assert.equal(browser.documentElement.lang, 'ru');
  const blocked = fakeDocument({ langSource: 'storage' });
  assert.doesNotThrow(() => initChrome({ document: blocked, location: { pathname: path }, storage: throwingStorage, navigatorLanguages: ['fr'] }));
  assert.equal(blocked.documentElement.lang, 'en');
});

test('on a stored-language page the wordmark leads to the home of the language in use and follows a switch', () => {
  const path = `/testnet/${HASH}`;
  for (const start of ACTIVE_LANGS) {
    const doc = fakeDocument({ lang: 'en', langSource: 'storage' });
    const links = langLinks(doc);
    const wordmark = doc.add(['[data-home]'], { href: '/' });
    initChrome({ document: doc, location: { pathname: path }, storage: memoryStorage({ 'xf-lang': start }), navigatorLanguages: ['en-US'] });
    assert.equal(wordmark.getAttribute('href'), homePath(start), `stored ${start}`);
    for (const chosen of ACTIVE_LANGS) {
      links[chosen].click();
      assert.equal(wordmark.getAttribute('href'), homePath(chosen), `${start} then ${chosen}`);
    }
  }

  const blocked = fakeDocument({ langSource: 'storage' });
  const home = blocked.add(['[data-home]'], { href: '/' });
  initChrome({ document: blocked, location: { pathname: path }, storage: throwingStorage, navigatorLanguages: ['de-DE'] });
  assert.equal(home.getAttribute('href'), homePath('de'), 'the browser language is used when storage is blocked');

  const fixed = fakeDocument({ lang: 'ru' });
  const own = fixed.add(['[data-home]'], { href: '/ru/' });
  initChrome({ document: fixed, location: { pathname: '/ru/' }, storage: memoryStorage({ 'xf-lang': 'de' }) });
  assert.equal(own.getAttribute('href'), '/ru/', 'a fixed-language page keeps its own home');
});

test('only a stored-language page marks its wordmark for the script to retarget', () => {
  const stored = renderChrome('header', { lang: 'en', pagePath: '/nope', langFromStorage: true });
  assert.match(stored, /<a class="wordmark" href="\/" data-home /);
  for (const lang of ACTIVE_LANGS) {
    assert.doesNotMatch(renderChrome('header', fixedPage(lang, homePath(lang))), /data-home/, lang);
  }
});

function menuDocument(extraHtmlAttributes = {}) {
  const doc = fakeDocument({ lang: 'en', ...extraHtmlAttributes });
  const menu = doc.add(['[data-lang-menu]']);
  const summary = doc.add(['summary']);
  summary.parent = menu;
  menu.children.push(summary);
  const links = {};
  for (const lang of ACTIVE_LANGS) {
    const link = langLink(doc, lang);
    link.parent = menu;
    links[lang] = link;
  }
  const outside = doc.add(['a']);
  return { doc, menu, summary, links, outside };
}

const pressKey = (element, key) => {
  const event = { key, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  for (const listener of element.listeners.get('keydown') ?? []) listener(event);
  return event;
};

test('the language menu closes on Escape and returns focus to its button', () => {
  const { doc, menu, summary, links } = menuDocument();
  initChrome({ document: doc, location: { pathname: '/' }, storage: memoryStorage() });
  menu.open = true;
  links.ru.focus();
  const event = pressKey(menu, 'Escape');
  assert.equal(menu.open, false);
  assert.equal(doc.activeElement, summary);
  assert.equal(event.defaultPrevented, true);

  doc.activeElement = null;
  pressKey(menu, 'Escape');
  assert.equal(doc.activeElement, null, 'Escape on a closed menu does nothing');
});

test('other keys leave the language menu open', () => {
  const { doc, menu } = menuDocument();
  initChrome({ document: doc, location: { pathname: '/' }, storage: memoryStorage() });
  menu.open = true;
  for (const key of ['ArrowDown', 'Tab', 'Enter']) pressKey(menu, key);
  assert.equal(menu.open, true);
});

test('the language menu closes when the visitor clicks or moves focus outside it, not inside', () => {
  const { doc, menu, summary, links, outside } = menuDocument();
  initChrome({ document: doc, location: { pathname: '/' }, storage: memoryStorage() });
  menu.open = true;
  doc.fire('click', { target: links.ru });
  doc.fire('click', { target: summary });
  doc.fire('focusin', { target: links.ru });
  assert.equal(menu.open, true, 'inside the menu nothing closes it');
  doc.fire('click', { target: outside });
  assert.equal(menu.open, false);
  menu.open = true;
  doc.fire('focusin', { target: outside });
  assert.equal(menu.open, false, 'tabbing out closes it');
});

test('choosing a language in place closes the menu and puts focus on its button', () => {
  const { doc, menu, summary, links } = menuDocument({ langSource: 'storage' });
  initChrome({ document: doc, location: { pathname: '/nope' }, storage: memoryStorage(), navigatorLanguages: ['en'] });
  menu.open = true;
  const event = links.ru.click();
  assert.equal(event.defaultPrevented, true);
  assert.equal(menu.open, false);
  assert.equal(doc.activeElement, summary, 'the link that had focus is hidden now; focus must not fall to the page start');
});

test('choosing a language on a fixed page leaves the navigation to the link', () => {
  const { doc, menu, links } = menuDocument();
  initChrome({ document: doc, location: { pathname: '/' }, storage: memoryStorage() });
  menu.open = true;
  const event = links.ru.click();
  assert.equal(event.defaultPrevented, false);
});

test('the pre-paint script applies the stored theme, and dark when storage is missing, blocked or odd', () => {
  const script = prePaintScript();
  const run = (localStorage) => {
    const documentElement = { dataset: {} };
    const sandbox = { document: { documentElement }, localStorage };
    vm.runInNewContext(script, sandbox);
    return documentElement.dataset.theme;
  };
  assert.equal(run({ getItem: () => 'light' }), 'light');
  assert.equal(run({ getItem: () => 'dark' }), 'dark');
  assert.equal(run({ getItem: () => 'sepia' }), 'dark');
  assert.equal(run({ getItem: () => null }), 'dark');
  assert.equal(run({ getItem() { throw new Error('blocked'); } }), 'dark');
  // The property read itself throws when site data is blocked.
  const documentElement = { dataset: {} };
  const denied = { document: { documentElement } };
  Object.defineProperty(denied, 'localStorage', { get() { throw new Error('denied'); } });
  vm.runInNewContext(script, denied);
  assert.equal(documentElement.dataset.theme, 'dark');
});

test('the pre-paint script reads the same key as prefs.mjs', async () => {
  const { setTheme } = await import('../src/js/prefs.mjs');
  const storage = memoryStorage();
  setTheme('light', storage);
  const documentElement = { dataset: {} };
  vm.runInNewContext(prePaintScript(), { document: { documentElement }, localStorage: storage });
  assert.equal(documentElement.dataset.theme, 'light');
});

test('every active language carries every chrome string', () => {
  for (const lang of ACTIVE_LANGS) {
    assert.deepEqual(Object.keys(STRINGS[lang]).sort(), Object.keys(STRINGS.en).sort(), lang);
    for (const [key, value] of Object.entries(STRINGS[lang])) assert.ok(value.length > 0, `${lang}.${key}`);
  }
});

const fixedPage = (lang, pagePath) => ({ lang, pagePath, langFromStorage: false });
const prefixOf = (lang) => (lang === 'en' ? '' : `/${lang}`);

test('the header links home in its language and holds the language menu and the theme switch', () => {
  const en = renderChrome('header', fixedPage('en', '/'));
  assert.match(en, /<a class="wordmark" href="\/"/);
  assert.match(en, /href="#main"/);
  assert.match(en, /<button type="button" class="theme-toggle" data-theme-toggle aria-pressed="false"/);
  const ru = renderChrome('header', fixedPage('ru', '/ru/'));
  assert.match(ru, /<a class="wordmark" href="\/ru\/"/);
  assert.ok(ru.includes(STRINGS.ru.lightTheme));
});

test('the header language control is a disclosure of plain links, one per active language, each in its own name', () => {
  for (const lang of ACTIVE_LANGS) {
    const header = renderChrome('header', fixedPage(lang, `${prefixOf(lang)}/`));
    assert.match(header, /<details class="lang-menu" data-lang-menu>\s*<summary class="lang-menu__button"/);
    const summary = /<summary[\s\S]*?<\/summary>/.exec(header)[0];
    assert.ok(summary.includes(`>${STRINGS[lang].languageLabel}</span>`), 'the button is named "Language" for assistive technology');
    assert.ok(summary.includes(`<span data-lang-current>${LANGUAGE_NAMES[lang]}</span>`), 'and shows the language in use');
    const links = [...header.matchAll(/<a data-lang-link="(\w+)" href="([^"]*)" lang="([^"]*)" hreflang="([^"]*)"( aria-current="page")?>([^<]*)<\/a>/g)];
    assert.deepEqual(links.map((match) => match[1]), ACTIVE_LANGS, 'the menu lists every active language, in order');
    for (const [, code, href, tag, hreflang, current, name] of links) {
      assert.equal(href, equivalentPath(`${prefixOf(lang)}/`, code));
      assert.equal(tag, htmlLang(code));
      assert.equal(hreflang, htmlLang(code));
      assert.equal(name, LANGUAGE_NAMES[code]);
      assert.equal(current !== undefined, code === lang, `aria-current on ${code} in ${lang}`);
    }
  }
});

test('the language menu lists only active languages', () => {
  const header = renderChrome('header', fixedPage('en', '/'));
  for (const lang of LANGS) {
    assert.equal(header.includes(`data-lang-link="${lang}"`), ACTIVE_LANGS.includes(lang), lang);
  }
});

test('the footer names the repository, the specification, the language and the theme', () => {
  assert.equal(REPOSITORY_URL, 'https://github.com/StaticBit-io/xrpl-file');
  assert.equal(SPECIFICATION_URL, 'https://github.com/StaticBit-io/xrpl-file/blob/main/docs/spec/xrpl-file-v1.md');
  const footer = renderChrome('footer', fixedPage('en', '/'));
  assert.ok(footer.includes(`href="${REPOSITORY_URL}"`));
  assert.ok(footer.includes(`href="${SPECIFICATION_URL}"`));
  for (const lang of ACTIVE_LANGS) assert.match(footer, new RegExp(`data-lang-link="${lang}"`));
  assert.match(footer, /data-theme-toggle/);
});

test('a page whose language is stored keeps one URL and falls back to the home pages without script', () => {
  const header = renderChrome('header', { lang: 'en', pagePath: '/file', langFromStorage: true });
  for (const lang of ACTIVE_LANGS) {
    assert.match(header, new RegExp(`data-lang-link="${lang}" href="${lang === 'en' ? '/' : `/${lang}/`}"`));
  }
  assert.match(header, /data-i18n="lightTheme"/);
  assert.match(header, /data-i18n="languageLabel"/, 'the menu button is named in the language in use');
  assert.match(header, /<span data-lang-current>/);
  assert.ok(!/aria-current/.test(header), 'chrome.mjs marks the current language once it knows the stored one');
});

test('the head part carries the pre-paint script before the stylesheet', () => {
  const head = renderChrome('head', fixedPage('ru', '/ru/'));
  assert.ok(head.indexOf('<script>') < head.indexOf('rel="stylesheet"'));
  assert.ok(head.includes('/fonts/geologica-latin.woff2'));
  assert.ok(head.includes('/fonts/geologica-cyrillic.woff2'), 'a Russian page preloads the Cyrillic face');
  assert.ok(!renderChrome('head', fixedPage('en', '/')).includes('geologica-cyrillic'));
});

test('a fixed-language page declares its canonical URL and every active language as an alternate, plus x-default', () => {
  for (const lang of ACTIVE_LANGS) {
    const head = renderChrome('head', fixedPage(lang, `${prefixOf(lang)}/`));
    assert.ok(head.includes(`<link rel="canonical" href="${SITE_ORIGIN}${prefixOf(lang)}/">`), `canonical on ${lang}`);
    for (const target of ACTIVE_LANGS) {
      assert.ok(
        head.includes(`<link rel="alternate" hreflang="${htmlLang(target)}" href="${SITE_ORIGIN}${prefixOf(target)}/">`),
        `${lang} page lists ${target}`,
      );
    }
    assert.ok(head.includes(`<link rel="alternate" hreflang="x-default" href="${SITE_ORIGIN}/">`));
    assert.equal(head.match(/hreflang=/g).length, ACTIVE_LANGS.length + 1);
  }
});

test('hreflang alternates follow the page, not just the home', () => {
  const head = renderChrome('head', fixedPage('en', '/about'));
  assert.ok(head.includes(`<link rel="canonical" href="${SITE_ORIGIN}/about">`));
  assert.ok(head.includes(`<link rel="alternate" hreflang="ru" href="${SITE_ORIGIN}/ru/about">`));
  assert.ok(head.includes(`<link rel="alternate" hreflang="x-default" href="${SITE_ORIGIN}/about">`));
});

test('a page with one URL for every language has neither a canonical nor hreflang alternates', () => {
  const head = renderChrome('head', { lang: 'en', pagePath: '/file', langFromStorage: true });
  assert.ok(!head.includes('hreflang') && !head.includes('rel="canonical"'));
});

test('languageAlternates handles all six languages, with Chinese tagged zh-Hans and living at /zh/', () => {
  const links = languageAlternates('/', LANGS);
  for (const [tag, path] of [['zh-Hans', '/zh/'], ['de', '/de/'], ['pt', '/pt/'], ['es', '/es/'], ['en', '/'], ['ru', '/ru/'], ['x-default', '/']]) {
    assert.ok(links.includes(`<link rel="alternate" hreflang="${tag}" href="${SITE_ORIGIN}${path}">`), tag);
  }
  assert.equal(links.match(/hreflang=/g).length, 7);
  const fromChinese = languageAlternates('/zh/x', LANGS);
  assert.ok(fromChinese.includes(`<link rel="alternate" hreflang="en" href="${SITE_ORIGIN}/x">`));
  assert.ok(fromChinese.includes(`<link rel="alternate" hreflang="x-default" href="${SITE_ORIGIN}/x">`));
});

const SITE_HOST = new URL(SITE_ORIGIN).origin;

/** Runs the inline script the way a browser would on `pathname` and returns where it sent the visitor, or null. */
function visit({ pathname = '/', search = '', hash = '', referrer = '', stored, languages, language, storage, langs } = {}) {
  const location = {
    pathname, search, hash, origin: SITE_HOST,
    replaced: null,
    replace(url) { this.replaced = url; },
  };
  const sandbox = {
    URL,
    location,
    document: { referrer, documentElement: { dataset: {} } },
    navigator: { languages, language },
    localStorage: storage ?? memoryStorage(stored === undefined ? {} : { 'xf-lang': stored }),
  };
  vm.runInNewContext(prePaintScript(langs), sandbox);
  return location.replaced;
}

const BROWSERS = [undefined, [], ['en-US'], ['ru-RU'], ['fr', 'ru'], ['ru', 'en'], ['de-AT'], ['zh-TW'], ['pt-BR'], ['es-419'], ['ja', 'en']];
const STORED = [undefined, 'en', 'ru', 'de', 'zh', 'garbage'];

test('on the root the inline script sends the visitor where getLang would, and only away from English', () => {
  for (const stored of STORED) {
    for (const languages of BROWSERS) {
      const lang = getLang(memoryStorage(stored === undefined ? {} : { 'xf-lang': stored }), languages);
      const expected = lang === 'en' ? null : homePath(lang);
      assert.equal(visit({ stored, languages }), expected, `stored ${stored}, browser ${JSON.stringify(languages)}`);
    }
  }
});

test('first visit: a Russian browser lands on /ru/, an English or unsupported one stays', () => {
  assert.equal(visit({ languages: ['ru-RU', 'en'] }), '/ru/');
  assert.equal(visit({ languages: ['fr', 'ru'] }), '/ru/');
  assert.equal(visit({ languages: ['en-US', 'ru'] }), null);
  assert.equal(visit({ languages: ['fr'] }), null);
  assert.equal(visit({ language: 'ru' }), '/ru/', 'navigator.language when there is no list');
  assert.equal(visit({ languages: [], language: 'ru' }), '/ru/');
  assert.equal(visit({}), null);
});

test('a remembered Russian choice returns to /ru/, a remembered English one stays on /', () => {
  assert.equal(visit({ stored: 'ru', languages: ['en-US'] }), '/ru/');
  assert.equal(visit({ stored: 'en', languages: ['ru-RU'] }), null, 'the visitor chose English: the browser does not override it');
});

test('the inline script survives storage that throws or is unreachable', () => {
  const throwing = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };
  assert.equal(visit({ storage: throwing, languages: ['ru-RU'] }), '/ru/');
  assert.equal(visit({ storage: throwing, languages: ['en'] }), null);
  const location = { pathname: '/', search: '', hash: '', origin: SITE_HOST, replaced: null, replace(url) { this.replaced = url; } };
  const denied = { URL, location, document: { referrer: '', documentElement: { dataset: {} } }, navigator: { languages: ['ru'] } };
  Object.defineProperty(denied, 'localStorage', { get() { throw new Error('denied'); } });
  vm.runInNewContext(prePaintScript(), denied);
  assert.equal(location.replaced, '/ru/');
});

test('the inline script never moves a visitor who came from this site, or who is not on the root', () => {
  assert.equal(visit({ referrer: `${SITE_HOST}/ru/`, languages: ['ru-RU'] }), null, 'they just chose English in the menu');
  assert.equal(visit({ referrer: `${SITE_HOST}/`, stored: 'ru' }), null);
  assert.equal(visit({ referrer: 'https://example.com/page', languages: ['ru-RU'] }), '/ru/', 'a visitor from elsewhere is redirected');
  assert.equal(visit({ referrer: 'not a url', languages: ['ru-RU'] }), '/ru/');
  for (const pathname of ['/ru/', '/index.html', '/x', `/testnet/${HASH}`, '/nope', '/file']) {
    assert.equal(visit({ pathname, stored: 'ru', languages: ['ru-RU'] }), null, pathname);
  }
});

test('the redirect keeps the query and the fragment', () => {
  assert.equal(visit({ search: '?a=1', hash: '#price-title', languages: ['ru'] }), '/ru/?a=1#price-title');
});

test('with all six languages active the script reads the primary subtag like getLang', () => {
  const six = (languages, stored) => visit({ languages, stored, langs: LANGS });
  assert.equal(six(['de-AT']), '/de/');
  assert.equal(six(['zh-TW']), '/zh/');
  assert.equal(six(['zh-Hans-CN']), '/zh/');
  assert.equal(six(['pt-BR']), '/pt/');
  assert.equal(six(['es-419']), '/es/');
  assert.equal(six(['fr', 'es']), '/es/');
  assert.equal(six(['en', 'de']), null);
  assert.equal(six(['en'], 'zh'), '/zh/');
  assert.equal(six(['de'], 'en'), null);
});

test('with German not active a German browser stays on the English page', () => {
  assert.equal(visit({ languages: ['de-AT'], langs: ['en', 'ru'] }), null);
});

test('the head of the English home carries the redirect, and it is in the same script as the theme', () => {
  const head = renderChrome('head', fixedPage('en', '/'));
  assert.ok(head.includes('location.replace('));
  assert.equal(head.match(/<script>/g).length, 1);
});

test('choosing English in the menu is remembered before the page is left', () => {
  const doc = fakeDocument({ lang: 'ru' });
  const en = langLink(doc, 'en');
  langLink(doc, 'ru');
  const storage = memoryStorage({ 'xf-lang': 'ru' });
  initChrome({ document: doc, location: { pathname: '/ru/' }, storage });
  const event = en.click();
  assert.equal(storage.data.get('xf-lang'), 'en');
  assert.equal(event.defaultPrevented, false);
});
