import test from 'node:test';
import assert from 'node:assert/strict';
import {
  getTheme, setTheme, getLang, setLang, equivalentPath, applyTheme, safeLocalStorage, LANGS, ACTIVE_LANGS,
} from '../src/js/prefs.mjs';

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

test('getTheme defaults to dark and survives a storage that throws', () => {
  assert.equal(getTheme(memoryStorage()), 'dark');
  assert.equal(getTheme(throwingStorage), 'dark');
  assert.equal(getTheme(null), 'dark');
});

test('setTheme with a storage that throws does not throw', () => {
  assert.doesNotThrow(() => setTheme('light', throwingStorage));
  assert.doesNotThrow(() => setTheme('light', null));
});

test('the theme round-trips through storage under xf-theme', () => {
  const storage = memoryStorage();
  setTheme('light', storage);
  assert.equal(storage.data.get('xf-theme'), 'light');
  assert.equal(getTheme(storage), 'light');
  setTheme('dark', storage);
  assert.equal(storage.data.get('xf-theme'), 'dark');
  assert.equal(getTheme(storage), 'dark');
  assert.equal(getTheme(memoryStorage({ 'xf-theme': 'garbage' })), 'dark');
});

test('the first visit is dark even when the OS prefers light', () => {
  const original = globalThis.matchMedia;
  globalThis.matchMedia = (query) => ({ matches: query.includes('light'), media: query });
  try {
    assert.equal(getTheme(memoryStorage()), 'dark');
  } finally {
    if (original === undefined) delete globalThis.matchMedia;
    else globalThis.matchMedia = original;
  }
});

test('safeLocalStorage does not throw when localStorage access throws', () => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    get() { throw new DOMException('denied', 'SecurityError'); },
  });
  try {
    assert.equal(safeLocalStorage(), null);
    assert.equal(getTheme(), 'dark');
    assert.doesNotThrow(() => setTheme('light'));
    assert.equal(getLang(undefined, 'ru-RU'), 'ru');
  } finally {
    if (descriptor) Object.defineProperty(globalThis, 'localStorage', descriptor);
    else delete globalThis.localStorage;
  }
});

test('six languages are known, and the active list is a subset that starts with English', () => {
  assert.deepEqual(LANGS, ['en', 'ru', 'de', 'es', 'pt', 'zh']);
  assert.ok(ACTIVE_LANGS.every((lang) => LANGS.includes(lang)));
  assert.equal(ACTIVE_LANGS[0], 'en');
  assert.equal(new Set(ACTIVE_LANGS).size, ACTIVE_LANGS.length);
});

test('getLang prefers the stored choice, then the first browser language that is active', () => {
  assert.equal(getLang(null, 'ru-RU'), 'ru');
  assert.equal(getLang(null, 'RU'), 'ru');
  assert.equal(getLang(null, 'en-US'), 'en');
  assert.equal(getLang(null, 'fr'), 'en');
  assert.equal(getLang(null, undefined), 'en');
  assert.equal(getLang(null, []), 'en');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'en' }), 'ru-RU'), 'en');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'ru' }), 'en-US'), 'ru');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'fr' }), 'ru-RU'), 'ru');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'xx-YY' }), 'fr'), 'en');
  assert.equal(getLang(throwingStorage, 'ru-RU'), 'ru');
});

test('getLang reads the whole browser list in order, and skips what the site does not offer', () => {
  assert.equal(getLang(null, ['fr-FR', 'ru-RU', 'en']), 'ru');
  assert.equal(getLang(null, ['en-US', 'ru']), 'en');
  assert.equal(getLang(null, ['ja', 'ko', 'ru']), 'ru');
  assert.equal(getLang(null, ['fr', 'it']), 'en');
  assert.equal(getLang(null, ['ru_RU']), 'ru', 'an underscore tag is read like a hyphenated one');
});

test('getLang falls back from navigator.languages to navigator.language', () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  const set = (value) => Object.defineProperty(globalThis, 'navigator', { configurable: true, value });
  try {
    set({ languages: ['fr', 'ru-RU'], language: 'en-US' });
    assert.equal(getLang(null), 'ru');
    set({ languages: [], language: 'ru-RU' });
    assert.equal(getLang(null), 'ru');
    set({ language: 'ru-RU' });
    assert.equal(getLang(null), 'ru');
    set({});
    assert.equal(getLang(null), 'en');
  } finally {
    if (original) Object.defineProperty(globalThis, 'navigator', original);
    else delete globalThis.navigator;
  }
});

test('getLang maps regional and script tags to the site language; only active languages are chosen', () => {
  const cases = [
    ['zh-CN', 'zh'], ['zh-Hans', 'zh'], ['zh-TW', 'zh'], ['zh', 'zh'], ['pt-BR', 'pt'], ['pt-PT', 'pt'],
    ['de-AT', 'de'], ['de-CH', 'de'], ['es-419', 'es'], ['es-MX', 'es'], ['ru-UA', 'ru'], ['en-GB', 'en'],
  ];
  for (const [tag, lang] of cases) {
    const expected = ACTIVE_LANGS.includes(lang) ? lang : 'en';
    assert.equal(getLang(null, tag), expected, tag);
    assert.equal(getLang(memoryStorage({ 'xf-lang': lang }), 'fr'), expected, `stored ${lang}`);
  }
});

test('setLang stores an active language under xf-lang and tolerates a storage that throws', () => {
  const storage = memoryStorage();
  setLang('ru', storage);
  assert.equal(storage.data.get('xf-lang'), 'ru');
  setLang('fr', storage);
  assert.equal(storage.data.get('xf-lang'), 'en', 'anything the site does not offer is English');
  for (const lang of ACTIVE_LANGS) {
    setLang(lang, storage);
    assert.equal(getLang(storage, 'fr'), lang);
  }
  assert.doesNotThrow(() => setLang('en', throwingStorage));
});

test('equivalentPath maps between the home pages of every language and leaves a file page alone', () => {
  assert.equal(equivalentPath('/', 'ru'), '/ru/');
  assert.equal(equivalentPath('/ru/', 'en'), '/');
  assert.equal(equivalentPath('/ru/', 'ru'), '/ru/');
  assert.equal(equivalentPath('/', 'en'), '/');
  assert.equal(equivalentPath('/x.html', 'ru'), '/ru/x.html');
  assert.equal(equivalentPath('/ru/x.html', 'en'), '/x.html');
  assert.equal(equivalentPath(`/testnet/${HASH}`, 'ru'), `/testnet/${HASH}`);
  assert.equal(equivalentPath(`/testnet/${HASH}`, 'en'), `/testnet/${HASH}`);
  assert.equal(equivalentPath(`/21338/${HASH}/`, 'ru'), `/21338/${HASH}/`);
});

test('equivalentPath works between any two of the six prefixes, for pages and the home alike', () => {
  for (const from of LANGS) {
    for (const to of LANGS) {
      const prefix = (lang) => (lang === 'en' ? '' : `/${lang}`);
      assert.equal(equivalentPath(`${prefix(from)}/`, to), `${prefix(to)}/`, `${from} home to ${to}`);
      assert.equal(equivalentPath(`${prefix(from)}/x.html`, to), `${prefix(to)}/x.html`, `${from} page to ${to}`);
      assert.equal(equivalentPath(`${prefix(from)}/x`, to), `${prefix(to)}/x`);
    }
  }
  assert.equal(equivalentPath('/zh', 'de'), '/de/');
  assert.equal(equivalentPath('/', 'zh'), '/zh/');
});

test('a path that only starts like a language prefix is not one', () => {
  assert.equal(equivalentPath('/deep/x', 'ru'), '/ru/deep/x');
  assert.equal(equivalentPath('/russia', 'en'), '/russia');
  assert.equal(equivalentPath('/ptolemy', 'zh'), '/zh/ptolemy');
  assert.equal(equivalentPath('/', 'fr'), '/', 'an unknown language is English');
});

test('a file page keeps its URL in every language, whatever its network is called', () => {
  for (const lang of LANGS) {
    for (const network of ['testnet', 'mainnet', '21338', 'de']) {
      const path = `/${network}/${HASH}`;
      assert.equal(equivalentPath(path, lang), path, `${lang} ${network}`);
    }
  }
});

test('applyTheme sets the page theme and the theme of every xrpl-file element', () => {
  const attributes = [];
  const doc = {
    documentElement: { dataset: {} },
    querySelectorAll(selector) {
      assert.equal(selector, 'xrpl-file');
      return [1, 2].map(() => ({ setAttribute: (name, value) => attributes.push([name, value]) }));
    },
  };
  applyTheme(doc, 'light');
  assert.equal(doc.documentElement.dataset.theme, 'light');
  assert.deepEqual(attributes, [['theme', 'light'], ['theme', 'light']]);
  applyTheme(doc, 'sepia');
  assert.equal(doc.documentElement.dataset.theme, 'dark', 'anything but light is dark');
});
