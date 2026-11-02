import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { initChrome } from '../src/js/chrome.mjs';
import { equivalentPath } from '../src/js/prefs.mjs';
import { renderChrome, prePaintScript, REPOSITORY_URL, SPECIFICATION_URL } from '../chrome-markup.mjs';
import { STRINGS } from '../src/js/chrome-strings.mjs';

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
    this.textContent = '';
  }
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
    querySelectorAll: (selector) => elements.filter((el) => el.selectors.includes(selector)),
    dispatchEvent: (event) => { events.push(event); return true; },
    add(selectors, attributes, dataset) {
      const el = new FakeElement(selectors, attributes);
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

test('language links point at equivalentPath', () => {
  for (const [pathname, lang] of [['/', 'en'], ['/ru/', 'ru'], ['/ru/x', 'ru'], ['/x', 'en']]) {
    const doc = fakeDocument({ lang });
    const en = langLink(doc, 'en');
    const ru = langLink(doc, 'ru');
    initChrome({ document: doc, location: { pathname }, storage: memoryStorage() });
    assert.equal(en.getAttribute('href'), equivalentPath(pathname, 'en'), `en link on ${pathname}`);
    assert.equal(ru.getAttribute('href'), equivalentPath(pathname, 'ru'), `ru link on ${pathname}`);
    assert.equal(doc.documentElement.lang, lang);
  }
  const doc = fakeDocument({ langSource: 'storage' });
  const en = langLink(doc, 'en');
  const ru = langLink(doc, 'ru');
  const path = `/testnet/${HASH}`;
  initChrome({ document: doc, location: { pathname: path }, storage: memoryStorage() });
  assert.equal(en.getAttribute('href'), path);
  assert.equal(ru.getAttribute('href'), path, 'a file page has one URL for both languages');

  // The address-error view and the 404 are stored-language pages at any path; no link may become /ru/<path>.
  for (const other of ['/testnet/xyz', '/nope', '/file']) {
    const page = fakeDocument({ langSource: 'storage' });
    const pageEn = langLink(page, 'en');
    const pageRu = langLink(page, 'ru');
    initChrome({ document: page, location: { pathname: other }, storage: memoryStorage() });
    assert.equal(pageEn.getAttribute('href'), other, `en link on ${other}`);
    assert.equal(pageRu.getAttribute('href'), other, `ru link on ${other}`);
  }
});

test('the current language link is marked and the other is not', () => {
  const doc = fakeDocument({ lang: 'ru' });
  const en = langLink(doc, 'en');
  const ru = langLink(doc, 'ru');
  initChrome({ document: doc, location: { pathname: '/ru/' }, storage: memoryStorage() });
  assert.equal(ru.getAttribute('aria-current'), 'true');
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
  const storage = memoryStorage();
  const path = `/testnet/${HASH}`;
  initChrome({ document: doc, location: { pathname: path }, storage, navigatorLanguage: 'en-US' });
  assert.equal(doc.documentElement.lang, 'en');
  assert.equal(label.textContent, STRINGS.en.repository);

  const event = ru.click();
  assert.equal(event.defaultPrevented, true);
  assert.equal(storage.data.get('xf-lang'), 'ru');
  assert.equal(doc.documentElement.lang, 'ru');
  assert.equal(label.textContent, STRINGS.ru.repository);
  assert.equal(aria.getAttribute('aria-label'), STRINGS.ru.homeLabel);
  assert.equal(ru.getAttribute('aria-current'), 'true');
  assert.equal(en.hasAttribute('aria-current'), false);
  assert.equal(ru.getAttribute('href'), path);
  const announced = doc.events.at(-1);
  assert.equal(announced.type, 'xf-langchange');
  assert.equal(announced.detail.lang, 'ru');
});

test('on the file page the language comes from storage, then from the browser', () => {
  const stored = fakeDocument({ langSource: 'storage' });
  initChrome({ document: stored, location: { pathname: `/testnet/${HASH}` }, storage: memoryStorage({ 'xf-lang': 'ru' }), navigatorLanguage: 'en-US' });
  assert.equal(stored.documentElement.lang, 'ru');
  const browser = fakeDocument({ langSource: 'storage' });
  initChrome({ document: browser, location: { pathname: `/testnet/${HASH}` }, storage: memoryStorage(), navigatorLanguage: 'ru-RU' });
  assert.equal(browser.documentElement.lang, 'ru');
  const blocked = fakeDocument({ langSource: 'storage' });
  assert.doesNotThrow(() => initChrome({ document: blocked, location: { pathname: `/testnet/${HASH}` }, storage: throwingStorage, navigatorLanguage: 'de' }));
  assert.equal(blocked.documentElement.lang, 'en');
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

test('both languages carry every string', () => {
  assert.deepEqual(Object.keys(STRINGS.ru).sort(), Object.keys(STRINGS.en).sort());
  for (const lang of ['en', 'ru']) for (const [key, value] of Object.entries(STRINGS[lang])) assert.ok(value.length > 0, `${lang}.${key}`);
});

test('the header links home in its language and holds the language and theme switches', () => {
  const en = renderChrome('header', { lang: 'en', pagePath: '/', langFromStorage: false });
  assert.match(en, /<a class="wordmark" href="\/"/);
  assert.match(en, /data-lang-link="en" href="\/" lang="en" hreflang="en" aria-current="true"/);
  assert.match(en, /data-lang-link="ru" href="\/ru\/" lang="ru" hreflang="ru"(?! aria-current)/);
  assert.match(en, /<button type="button" class="theme-toggle" data-theme-toggle aria-pressed="false"/);
  assert.match(en, /href="#main"/);
  const ru = renderChrome('header', { lang: 'ru', pagePath: '/ru/', langFromStorage: false });
  assert.match(ru, /<a class="wordmark" href="\/ru\/"/);
  assert.match(ru, /data-lang-link="en" href="\/" lang="en" hreflang="en"(?! aria-current)/);
  assert.match(ru, /data-lang-link="ru" href="\/ru\/" lang="ru" hreflang="ru" aria-current="true"/);
  assert.ok(ru.includes(STRINGS.ru.lightTheme));
});

test('the footer names the repository, the specification, the language and the theme', () => {
  assert.equal(REPOSITORY_URL, 'https://github.com/StaticBit-io/xrpl-file');
  assert.equal(SPECIFICATION_URL, 'https://github.com/StaticBit-io/xrpl-file/blob/main/docs/spec/xrpl-file-v1.md');
  const footer = renderChrome('footer', { lang: 'en', pagePath: '/', langFromStorage: false });
  assert.ok(footer.includes(`href="${REPOSITORY_URL}"`));
  assert.ok(footer.includes(`href="${SPECIFICATION_URL}"`));
  assert.match(footer, /data-lang-link="ru"/);
  assert.match(footer, /data-theme-toggle/);
});

test('a page whose language is stored keeps one URL and falls back to the two homes without script', () => {
  const header = renderChrome('header', { lang: 'en', pagePath: '/file', langFromStorage: true });
  assert.match(header, /data-lang-link="en" href="\/"/);
  assert.match(header, /data-lang-link="ru" href="\/ru\/"/);
  assert.match(header, /data-i18n="lightTheme"/);
});

test('the head part carries the pre-paint script before the stylesheet', () => {
  const head = renderChrome('head', { lang: 'ru', pagePath: '/ru/', langFromStorage: false });
  assert.ok(head.indexOf('<script>') < head.indexOf('rel="stylesheet"'));
  assert.ok(head.includes('/fonts/geologica-latin.woff2'));
  assert.ok(head.includes('/fonts/geologica-cyrillic.woff2'), 'a Russian page preloads the Cyrillic face');
  assert.ok(!renderChrome('head', { lang: 'en', pagePath: '/', langFromStorage: false }).includes('geologica-cyrillic'));
});
