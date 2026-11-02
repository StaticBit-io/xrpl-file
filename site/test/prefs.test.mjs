import test from 'node:test';
import assert from 'node:assert/strict';
import {
  getTheme, setTheme, getLang, setLang, equivalentPath, applyTheme, safeLocalStorage,
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

test('getTheme defaults to light and survives a storage that throws', () => {
  assert.equal(getTheme(memoryStorage()), 'light');
  assert.equal(getTheme(throwingStorage), 'light');
  assert.equal(getTheme(null), 'light');
});

test('setTheme with a storage that throws does not throw', () => {
  assert.doesNotThrow(() => setTheme('dark', throwingStorage));
  assert.doesNotThrow(() => setTheme('dark', null));
});

test('the theme round-trips through storage under xf-theme', () => {
  const storage = memoryStorage();
  setTheme('dark', storage);
  assert.equal(storage.data.get('xf-theme'), 'dark');
  assert.equal(getTheme(storage), 'dark');
  assert.equal(getTheme(memoryStorage({ 'xf-theme': 'garbage' })), 'light');
});

test('the first visit is light even when the OS prefers dark', () => {
  const original = globalThis.matchMedia;
  globalThis.matchMedia = (query) => ({ matches: query.includes('dark'), media: query });
  try {
    assert.equal(getTheme(memoryStorage()), 'light');
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
    assert.equal(getTheme(), 'light');
    assert.doesNotThrow(() => setTheme('dark'));
    assert.equal(getLang(undefined, 'ru-RU'), 'ru');
  } finally {
    if (descriptor) Object.defineProperty(globalThis, 'localStorage', descriptor);
    else delete globalThis.localStorage;
  }
});

test('getLang prefers the stored choice, then the browser language', () => {
  assert.equal(getLang(null, 'ru-RU'), 'ru');
  assert.equal(getLang(null, 'RU'), 'ru');
  assert.equal(getLang(null, 'en-US'), 'en');
  assert.equal(getLang(null, 'de'), 'en');
  assert.equal(getLang(null, undefined), 'en');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'en' }), 'ru-RU'), 'en');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'ru' }), 'en-US'), 'ru');
  assert.equal(getLang(memoryStorage({ 'xf-lang': 'fr' }), 'ru-RU'), 'ru');
  assert.equal(getLang(throwingStorage, 'ru-RU'), 'ru');
});

test('setLang stores under xf-lang and tolerates a storage that throws', () => {
  const storage = memoryStorage();
  setLang('ru', storage);
  assert.equal(storage.data.get('xf-lang'), 'ru');
  assert.doesNotThrow(() => setLang('en', throwingStorage));
});

test('equivalentPath maps between / and /ru/ and leaves a file page alone', () => {
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

test('applyTheme sets the page theme and the theme of every xrpl-file element', () => {
  const attributes = [];
  const doc = {
    documentElement: { dataset: {} },
    querySelectorAll(selector) {
      assert.equal(selector, 'xrpl-file');
      return [1, 2].map(() => ({ setAttribute: (name, value) => attributes.push([name, value]) }));
    },
  };
  applyTheme(doc, 'dark');
  assert.equal(doc.documentElement.dataset.theme, 'dark');
  assert.deepEqual(attributes, [['theme', 'dark'], ['theme', 'dark']]);
});
