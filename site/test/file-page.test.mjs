import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import './support/register-reader.mjs';

const { initFilePage, initNotFoundPage } = await import('../src/js/file-page.mjs');
const { t, formatSize, networkName, LANGS } = await import('../src/js/strings.mjs');
const strings = await import('../src/js/strings.mjs');

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const LINK = `xrpl-file://testnet/${HASH}`;
const CANONICAL = `/testnet/${HASH}`;

class FakeNode {
  constructor(id = null) {
    this.id = id;
    this.attributes = new Map();
    this.dataset = {};
    this.listeners = new Map();
    this.hidden = false;
    this.disabled = false;
    this.textContent = '';
    this.value = '';
    this.children = [];
    this.focused = false;
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  removeAttribute(name) { this.attributes.delete(name); }
  addEventListener(type, listener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
  append(...nodes) { this.children.push(...nodes); }
  focus() { this.focused = true; }
  click() { for (const listener of this.listeners.get('click') ?? []) listener({ type: 'click' }); }
}

class FakeFile extends EventTarget {
  constructor() {
    super();
    this.attributes = new Map();
    this.saves = 0;
    this.outcome = () => Promise.resolve('saved');
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  save() {
    this.saves += 1;
    return this.outcome();
  }
  load(detail) { this.dispatchEvent(new CustomEvent('load', { detail })); }
  fail() { this.dispatchEvent(new CustomEvent('error', { detail: { name: 'BlockMismatch', message: 'x' } })); }
}

const DETAIL = { name: 'photo.webp', type: 'image/webp', size: 178448, network: 'testnet', link: LINK };

/** The ids the file page and the 404 page share, plus a few `data-s` nodes, behind the DOM calls the modules make. */
function fakeDocument({ theme = 'dark' } = {}) {
  const ids = [
    'file-page', 'invalid-page', 'file-view', 'file-download', 'file-status', 'file-title', 'file-details',
    'file-size', 'file-size-row', 'file-network', 'file-link', 'file-copy', 'file-copy-status',
    'check-link', 'check-link-input', 'check-link-error',
  ];
  const nodes = Object.fromEntries(ids.map((id) => [id, new FakeNode(id)]));
  nodes['check-link-error'].id = 'check-link-error';
  const heading = Object.assign(new FakeNode(), { dataset: { s: 'howTitle' } });
  const labelled = Object.assign(new FakeNode(), { dataset: { sLabel: 'copyLabel' } });
  const home = Object.assign(new FakeNode(), { dataset: { home: '' } });
  const file = new FakeFile();
  const root = new FakeNode();
  root.dataset.theme = theme;
  root.lang = 'en';
  const listeners = new Map();
  const doc = {
    nodes,
    heading,
    labelled,
    home,
    file,
    title: '',
    documentElement: root,
    getElementById: (id) => nodes[id] ?? null,
    querySelectorAll: (selector) => ({
      '[data-s]': [heading],
      '[data-s-label]': [labelled],
      '[data-home]': [home],
    })[selector] ?? [],
    createElement: (tag) => {
      assert.equal(tag, 'xrpl-file');
      return file;
    },
    addEventListener: (type, listener) => listeners.set(type, [...(listeners.get(type) ?? []), listener]),
    switchLanguage: (lang) => {
      for (const listener of listeners.get('xf-langchange') ?? []) listener({ detail: { lang } });
    },
  };
  return doc;
}

function fakeHistory() {
  const calls = [];
  return { calls, replaceState: (...args) => calls.push(args) };
}

function open(pathname, { lang = 'en', search = '', hash = '', doc = fakeDocument() } = {}) {
  const history = fakeHistory();
  const page = initFilePage(doc, { pathname, search, hash }, { lang, history });
  return { doc, history, page };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 80));

test('every key exists in both languages', () => {
  const tables = LANGS.map((lang) => strings.TABLES[lang]);
  const [first, ...rest] = tables.map((table) => Object.keys(table).sort());
  for (const keys of rest) assert.deepEqual(keys, first);
  for (const lang of LANGS) {
    for (const key of first) {
      const value = strings.TABLES[lang][key];
      assert.ok(typeof value === 'string' && value.length > 0, `${lang}.${key} is empty`);
      assert.ok(!value.includes('—'), `${lang}.${key} has a long dash`);
    }
  }
  const placeholders = (text) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();
  for (const key of first) {
    assert.deepEqual(placeholders(strings.TABLES.ru[key]), placeholders(strings.TABLES.en[key]), `${key} placeholders differ`);
  }
});

test('a missing key or language throws', () => {
  assert.throws(() => t('noSuchKey', 'en'), /noSuchKey/);
  assert.throws(() => t('download', 'de'), /de/);
});

test('t fills {placeholders} and rejects a missing value', () => {
  assert.equal(t('sizeKB', 'en', { value: '178' }), '178 KB');
  assert.equal(t('sizeKB', 'ru', { value: '178' }), '178 КБ');
  assert.throws(() => t('sizeKB', 'en'), /value/);
});

test('sizes are human, decimal, and the same facts in both languages', () => {
  assert.equal(formatSize(178448, 'en'), '178 KB');
  assert.equal(formatSize(178448, 'ru'), '178 КБ');
  assert.equal(formatSize(512, 'en'), '512 B');
  assert.equal(formatSize(4200, 'en'), '4.2 KB');
  assert.equal(formatSize(4200, 'ru'), '4,2 КБ');
  assert.equal(formatSize(12_300_000, 'en'), '12 MB');
  assert.equal(formatSize(999_999, 'en'), '1.0 MB', 'rounding never shows 1000 KB');
  assert.equal(formatSize(-1, 'en'), null);
  assert.equal(formatSize(Number.NaN, 'en'), null);
  assert.equal(formatSize('big', 'en'), null);
});

test('the network is localised and an unknown one is shown as it is', () => {
  assert.equal(networkName('testnet', 'en'), 'testnet');
  assert.equal(networkName('testnet', 'ru'), 'тестовая сеть');
  assert.equal(networkName('mainnet', 'en'), 'mainnet');
  assert.equal(networkName('mainnet', 'ru'), 'основная сеть');
  assert.equal(networkName('21338', 'ru'), '21338');
});

test('the Download button is disabled until load', () => {
  const { doc } = open(CANONICAL);
  const button = doc.nodes['file-download'];
  assert.equal(button.disabled, true);
  doc.file.load(DETAIL);
  assert.equal(button.disabled, false);
});

test('the page renders the element with the link, no caption and the page theme', () => {
  const { doc } = open(CANONICAL);
  assert.equal(doc.file.getAttribute('src'), LINK);
  assert.equal(doc.file.getAttribute('caption'), 'off');
  assert.equal(doc.file.getAttribute('theme'), 'dark');
  assert.deepEqual(doc.nodes['file-view'].children, [doc.file]);
  assert.equal(doc.nodes['file-page'].hidden, false);
  assert.equal(doc.nodes['invalid-page'].hidden, true);
  assert.equal(doc.nodes['file-link'].textContent, LINK);
});

test('the theme is light when the page has not set one', () => {
  const { doc } = open(CANONICAL, { doc: fakeDocument({ theme: '' }) });
  assert.equal(doc.file.getAttribute('theme'), 'light');
});

test('name and size come from the load detail', () => {
  const { doc } = open(CANONICAL);
  assert.equal(doc.nodes['file-details'].hidden, true, 'nothing is claimed before the file has loaded');
  doc.file.load(DETAIL);
  assert.equal(doc.nodes['file-title'].textContent, 'photo.webp');
  assert.equal(doc.nodes['file-size'].textContent, '178 KB');
  assert.equal(doc.nodes['file-network'].textContent, 'testnet');
  assert.equal(doc.nodes['file-details'].hidden, false);
  assert.equal(doc.title, 'photo.webp · xrpl-file');
});

test('a file without a name gets a plain title', () => {
  const { doc } = open(CANONICAL);
  doc.file.load({ ...DETAIL, name: null });
  assert.equal(doc.nodes['file-title'].textContent, 'File');
  assert.equal(doc.title, 'File · xrpl-file');
});

test('Download saves through the element and reports how it went', async () => {
  const { doc } = open(CANONICAL);
  const button = doc.nodes['file-download'];
  const status = doc.nodes['file-status'];
  doc.file.load(DETAIL);

  button.click();
  assert.equal(button.disabled, true, 'busy while saving');
  await settle();
  assert.equal(doc.file.saves, 1);
  assert.equal(status.textContent, 'Saved.');
  assert.equal(button.disabled, false);

  doc.file.outcome = () => Promise.resolve('started');
  button.click();
  await settle();
  assert.equal(status.textContent, 'Download started.');

  doc.file.outcome = () => Promise.resolve('cancelled');
  button.click();
  await settle();
  assert.equal(status.textContent, '', 'a closed dialog is not a result');
});

test('a second click while a save runs does not start another', async () => {
  const { doc } = open(CANONICAL);
  doc.file.load(DETAIL);
  let finish;
  doc.file.outcome = () => new Promise((resolve) => { finish = resolve; });
  doc.nodes['file-download'].click();
  doc.nodes['file-download'].click();
  assert.equal(doc.file.saves, 1);
  finish('saved');
  await settle();
  assert.equal(doc.nodes['file-status'].textContent, 'Saved.');
});

test('a rejected save shows the error and does not throw', async () => {
  const unhandled = [];
  const record = (reason) => unhandled.push(reason);
  process.on('unhandledRejection', record);
  try {
    const { doc } = open(CANONICAL);
    const status = doc.nodes['file-status'];
    doc.file.load(DETAIL);
    const reject = (error) => { doc.file.outcome = () => Promise.reject(error); };

    reject(Object.assign(new Error('block 3 does not match'), { code: 'BlockMismatch' }));
    doc.nodes['file-download'].click();
    await settle();
    assert.equal(status.textContent, t('saveNotVerified', 'en'));
    assert.equal(status.dataset.state, 'error');
    assert.notEqual(status.textContent, 'Saved.');
    assert.equal(doc.nodes['file-download'].disabled, false, 'the visitor can try again');

    reject(Object.assign(new Error('The file has not been read yet'), { code: 'NotLoaded' }));
    doc.nodes['file-download'].click();
    await settle();
    assert.equal(status.textContent, t('saveNotLoaded', 'en'));

    reject(Object.assign(new Error('node'), { code: 'NodeUnreachable' }));
    doc.nodes['file-download'].click();
    await settle();
    assert.equal(status.textContent, t('saveNodeDown', 'en'));

    reject(Object.assign(new Error('weird'), { code: 'SomethingNew' }));
    doc.nodes['file-download'].click();
    await settle();
    assert.equal(status.textContent, t('saveFailed', 'en'));

    reject('not even an Error');
    doc.nodes['file-download'].click();
    await settle();
    assert.equal(status.textContent, t('saveFailed', 'en'));

    assert.deepEqual(unhandled, []);
  } finally {
    process.off('unhandledRejection', record);
  }
});

test('a save rejected with a DOMException is handled too', async () => {
  const { doc } = open(CANONICAL);
  const status = doc.nodes['file-status'];
  doc.file.load(DETAIL);

  doc.file.outcome = () => Promise.reject(new DOMException('The request is not allowed', 'NotAllowedError'));
  doc.nodes['file-download'].click();
  await settle();
  assert.equal(status.textContent, t('saveRefused', 'en'));

  doc.file.outcome = () => Promise.reject(new DOMException('The save was cancelled.', 'AbortError'));
  doc.nodes['file-download'].click();
  await settle();
  assert.equal(status.textContent, t('saveInterrupted', 'en'));
  assert.notEqual(status.textContent, 'Saved.');
});

test('a save that throws at once is handled', async () => {
  const { doc } = open(CANONICAL);
  doc.file.load(DETAIL);
  doc.file.outcome = () => { throw new TypeError('save is not a function'); };
  doc.nodes['file-download'].click();
  await settle();
  assert.equal(doc.nodes['file-status'].textContent, t('saveFailed', 'en'));
  assert.equal(doc.nodes['file-download'].disabled, false);
});

test('a playback error after load leaves Download enabled and save() is still called', async () => {
  const { doc } = open(CANONICAL);
  const button = doc.nodes['file-download'];
  doc.file.load(DETAIL);
  doc.file.fail();
  assert.equal(button.disabled, false, 'the verified file can still be saved');
  button.click();
  await settle();
  assert.equal(doc.file.saves, 1);
  assert.equal(doc.nodes['file-status'].textContent, 'Saved.');
});

test('an error before any load keeps Download disabled', () => {
  const { doc } = open(CANONICAL);
  doc.file.fail();
  assert.equal(doc.nodes['file-download'].disabled, true);
});

test('a lower-case path is replaced by the canonical one', () => {
  const { history, doc } = open(`/TestNet/${HASH.toLowerCase()}`, { search: '?utm=1', hash: '#top' });
  assert.deepEqual(history.calls, [[null, '', `${CANONICAL}?utm=1#top`]]);
  assert.equal(doc.file.getAttribute('src'), LINK);
});

test('a trailing slash is dropped from the address', () => {
  const { history } = open(`${CANONICAL}/`);
  assert.deepEqual(history.calls, [[null, '', CANONICAL]]);
});

test('a canonical path is left alone', () => {
  const { history } = open(CANONICAL);
  assert.deepEqual(history.calls, []);
});

test('a decimal NetworkID keeps its path', () => {
  const { history, doc } = open(`/21338/${HASH}`);
  assert.deepEqual(history.calls, []);
  assert.equal(doc.file.getAttribute('src'), `xrpl-file://21338/${HASH}`);
});

test('an invalid path shows "Check a link" with an error and no file', async () => {
  const { doc, history } = open('/testnet/xyz');
  assert.deepEqual(history.calls, []);
  assert.equal(doc.nodes['file-page'].hidden, true);
  assert.equal(doc.nodes['invalid-page'].hidden, false);
  assert.deepEqual(doc.nodes['file-view'].children, [], 'no element is created');
  assert.equal(doc.nodes['check-link-input'].value, 'xrpl-file://testnet/xyz');
  await settle();
  assert.equal(doc.nodes['check-link-error'].textContent, t('checkInvalid', 'en'));
  assert.equal(doc.nodes['check-link-input'].getAttribute('aria-invalid'), 'true');
  assert.equal(doc.title, 'Check a link · xrpl-file');
});

test('the page texts follow the language switch', () => {
  const { doc } = open(CANONICAL);
  assert.equal(doc.heading.textContent, 'How this works');
  assert.equal(doc.labelled.getAttribute('aria-label'), 'Copy the link');
  assert.equal(doc.home.getAttribute('href'), '/');
  doc.file.load(DETAIL);

  doc.switchLanguage('ru');
  assert.equal(doc.heading.textContent, 'Как это работает');
  assert.equal(doc.labelled.getAttribute('aria-label'), 'Скопировать ссылку');
  assert.equal(doc.home.getAttribute('href'), '/ru/');
  assert.equal(doc.nodes['file-size'].textContent, '178 КБ');
  assert.equal(doc.nodes['file-network'].textContent, 'тестовая сеть');

  doc.switchLanguage('en');
  assert.equal(doc.nodes['file-size'].textContent, '178 KB');
  assert.equal(doc.home.getAttribute('href'), '/');
});

test('a shown save result is re-worded in the new language, an error stays an error', async () => {
  const { doc } = open(CANONICAL, { lang: 'ru' });
  doc.file.load(DETAIL);
  doc.nodes['file-download'].click();
  await settle();
  assert.equal(doc.nodes['file-status'].textContent, 'Сохранено.');
  doc.switchLanguage('en');
  assert.equal(doc.nodes['file-status'].textContent, 'Saved.');

  doc.file.outcome = () => Promise.reject(Object.assign(new Error('x'), { code: 'BlockMismatch' }));
  doc.nodes['file-download'].click();
  await settle();
  doc.switchLanguage('ru');
  assert.equal(doc.nodes['file-status'].textContent, t('saveNotVerified', 'ru'));
  assert.equal(doc.nodes['file-status'].dataset.state, 'error');
});

test('the 404 page is localised and its field works', () => {
  const doc = fakeDocument();
  initNotFoundPage(doc, { lang: 'ru' });
  assert.equal(doc.heading.textContent, 'Как это работает', 'static texts come from the table');
  assert.equal(doc.home.getAttribute('href'), '/ru/');
  assert.equal(doc.title, 'Страница не найдена · xrpl-file');
  doc.switchLanguage('en');
  assert.equal(doc.home.getAttribute('href'), '/');
  assert.equal(doc.title, 'Page not found · xrpl-file');
});

async function page(name) {
  return readFile(new URL(`../src/${name}`, import.meta.url), 'utf8');
}

test('the file and 404 pages are chrome pages with one URL for both languages', async () => {
  for (const name of ['file.html', '404.html']) {
    const html = await page(name);
    assert.match(html, /<html lang="en" data-lang-source="storage">/, name);
    assert.match(html, /<meta name="description" content="[^"]{40,}">/, name);
    assert.match(html, /<main id="main">/, name);
    for (const marker of ['head', 'header', 'footer']) assert.ok(html.includes(`<!--@chrome:${marker}-->`), `${name} ${marker}`);
    assert.ok(!html.includes('—'), name);
    assert.ok(!/<script(?![^>]*\bsrc=)[^>]*>/.test(html), `${name} has an inline script`);
  }
});

test('the file page loads the element from its own build, before its own script', async () => {
  const html = await page('file.html');
  const element = html.indexOf('<script src="/element/1.1.0/xrpl-file-element.js" defer></script>');
  const own = html.indexOf('<script type="module" src="/js/file-page.mjs"></script>');
  assert.ok(element > 0 && own > element);
  assert.match(html, /<button id="file-download"[^>]*\bdisabled\b/);
  assert.match(html, /<p id="file-status"[^>]*role="status"/);
  assert.match(html, /<p id="check-link-error" class="check__error" role="alert"><\/p>/);
});

test('the text shipped in the HTML is the English string of its key', async () => {
  for (const name of ['file.html', '404.html']) {
    const html = await page(name);
    const found = [...html.matchAll(/<(\w+)[^>]*\bdata-s="(\w+)"[^>]*>([^<]*)</g)];
    assert.ok(found.length >= 3, name);
    for (const [, , key, text] of found) assert.equal(text, t(key, 'en'), `${name}: ${key}`);
    for (const [tag, key] of html.matchAll(/<[^>]*\bdata-s-label="(\w+)"[^>]*>/g)) {
      assert.equal(/\baria-label="([^"]*)"/.exec(tag)?.[1], t(key, 'en'), `${name}: ${key}`);
    }
  }
});

test('the 404 page carries the check field and a way home', async () => {
  const html = await page('404.html');
  assert.match(html, /<form id="check-link"/);
  assert.match(html, /<input id="check-link-input"/);
  assert.match(html, /<a [^>]*data-home[^>]*href="\/"|<a [^>]*href="\/"[^>]*data-home/);
  assert.match(html, /<body data-page="not-found">/);
  assert.match(html, /<script type="module" src="\/js\/file-page\.mjs"><\/script>/);
});
