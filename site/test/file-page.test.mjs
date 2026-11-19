import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import './support/register-reader.mjs';

const { initFilePage, initNotFoundPage, HOLD_MAX_MS } = await import('../src/js/file-page.mjs');
const { t, formatSize, networkName } = await import('../src/js/strings.mjs');
const { ACTIVE_LANGS: LANGS, homePath, htmlLang, localeOf } = await import('../src/js/languages.mjs');
const strings = await import('../src/js/strings.mjs');
const { startElementRead } = await import('../src/js/element-read.mjs');
const { fileCloudStrings } = await import('../src/js/home-strings.mjs');

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
    this.parentElement = null;
    this.classes = new Set();
    this.classList = {
      add: (name) => this.classes.add(name),
      remove: (name) => this.classes.delete(name),
      contains: (name) => this.classes.has(name),
    };
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
  removeAttribute(name) { this.attributes.delete(name); }
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
    'check-link', 'check-link-input', 'check-link-error', 'file-cloud', 'file-cloud-caption', 'file-cloud-note',
  ];
  const nodes = Object.fromEntries(ids.map((id) => [id, new FakeNode(id)]));
  // The canvas sits in the stage, which is where the page puts the element.
  const stage = new FakeNode();
  stage.append(nodes['file-cloud']);
  nodes['file-cloud'].parentElement = stage;
  nodes['file-view'].append(stage);
  nodes['file-cloud-note'].hidden = true;
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
    stage,
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

/** Stands in for mountCloud, which needs a real canvas: records how it was called and what became of it. */
function fakeCloud(doc) {
  const cloud = {
    mounts: [],
    destroyed: 0,
    strings: [],
    // What settleDelay() reports; a test sets it before `load`.
    settle: 0,
    // The line the real cloud writes into the caption on `error`; its listener is registered at mount,
    // before the page's own, so it runs first.
    failureLine: null,
    mount(canvas, options, env) {
      env.element.addEventListener('error', () => {
        if (cloud.failureLine !== null) options.caption.textContent = cloud.failureLine;
      });
      cloud.mounts.push({
        canvas,
        options,
        env,
        elementAppended: doc.stage.children.includes(env.element),
        loader: env.element.getAttribute('loader'),
      });
      return {
        done: Promise.resolve(),
        phase: 'reading',
        destroy: () => { cloud.destroyed += 1; },
        settleDelay: () => cloud.settle,
        setStrings: (strings) => cloud.strings.push(strings),
      };
    },
  };
  return cloud;
}

function open(pathname, { lang = 'en', search = '', hash = '', doc = fakeDocument(), reducedMotion = true } = {}) {
  const history = fakeHistory();
  const cloud = fakeCloud(doc);
  const page = initFilePage(doc, { pathname, search, hash }, { lang, history, mountCloud: cloud.mount, reducedMotion });
  return { doc, history, page, cloud };
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
  assert.throws(() => t('download', 'xx'), /xx/);
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
  assert.deepEqual(doc.stage.children, [doc.nodes['file-cloud'], doc.file], 'the element stands above the canvas');
  assert.equal(doc.nodes['file-page'].hidden, false);
  assert.equal(doc.nodes['invalid-page'].hidden, true);
  assert.equal(doc.nodes['file-link'].textContent, LINK);
});

test('a picture loads without the box, and the box stays before load and for every other type', () => {
  const { doc } = open(CANONICAL);
  assert.equal(doc.file.getAttribute('background'), null, 'the loader and any message keep the box');
  doc.file.load(DETAIL);
  assert.equal(doc.file.getAttribute('background'), 'transparent');
  for (const type of ['image/png', 'image/svg+xml', 'image/gif']) {
    const page = open(CANONICAL);
    page.doc.file.load({ ...DETAIL, type });
    assert.equal(page.doc.file.getAttribute('background'), 'transparent', type);
  }
  for (const type of ['video/mp4', 'text/plain', 'application/pdf', 'imagery/x', undefined]) {
    const page = open(CANONICAL);
    page.doc.file.load({ ...DETAIL, type });
    assert.equal(page.doc.file.getAttribute('background'), null, String(type));
  }
});

test('a file that fails to load keeps the box', () => {
  const { doc } = open(CANONICAL);
  doc.file.fail();
  assert.equal(doc.file.getAttribute('background'), null);
});

test('the theme is dark when the page has not set one, and light only when the page says so', () => {
  const { doc } = open(CANONICAL, { doc: fakeDocument({ theme: '' }) });
  assert.equal(doc.file.getAttribute('theme'), 'dark');
  const light = open(CANONICAL, { doc: fakeDocument({ theme: 'light' }) });
  assert.equal(light.doc.file.getAttribute('theme'), 'light');
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
  assert.equal(status.textContent, 'Saving…', 'the disabled button is explained while a save runs');
  assert.equal(status.dataset.state, 'ok');
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
  assert.deepEqual(doc.stage.children, [doc.nodes['file-cloud']], 'no element is created');
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

test('the element is given the language of the page when it is created and on every switch', () => {
  for (const lang of LANGS) {
    const { doc } = open(CANONICAL, { lang });
    assert.equal(doc.file.getAttribute('lang'), htmlLang(lang), `created in ${lang}`);
    for (const next of LANGS) {
      doc.switchLanguage(next);
      assert.equal(doc.file.getAttribute('lang'), htmlLang(next), `${lang} -> ${next}`);
    }
  }
});

test('the element gets the same tag the page puts on <html>, and Chinese says its script', () => {
  const { doc } = open(CANONICAL, { lang: 'zh' });
  assert.equal(doc.file.getAttribute('lang'), 'zh-Hans');
  doc.switchLanguage('pt');
  assert.equal(doc.file.getAttribute('lang'), 'pt');
});

test('a language the site does not offer gives the element English', () => {
  const { doc } = open(CANONICAL, { lang: 'fr' });
  assert.equal(doc.file.getAttribute('lang'), 'en');
  doc.switchLanguage('ru');
  doc.switchLanguage('xx');
  assert.equal(doc.file.getAttribute('lang'), 'en');
});

test('switching language keeps the element, the loaded details and the Download button', () => {
  const { doc } = open(CANONICAL, { lang: 'ru' });
  doc.file.load(DETAIL);
  doc.switchLanguage('de');
  assert.deepEqual(doc.stage.children, [doc.nodes['file-cloud'], doc.file], 'one element, not re-created');
  assert.equal(doc.nodes['file-download'].disabled, false);
  assert.equal(doc.file.getAttribute('src'), LINK);
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

test('the element gets loader="false"', () => {
  const { doc } = open(CANONICAL);
  assert.equal(doc.file.getAttribute('loader'), 'false', 'the cloud is the loader, the element shows only its bare box');
});

test('the cloud is mounted before the element reads', () => {
  const { doc, cloud } = open(CANONICAL);
  assert.equal(cloud.mounts.length, 1);
  const [mount] = cloud.mounts;
  assert.equal(mount.elementAppended, false, 'no event can be missed: the element is not in the page yet');
  assert.equal(mount.loader, 'false');
  assert.equal(mount.canvas, doc.nodes['file-cloud']);
  assert.equal(mount.env.element, doc.file);
  assert.equal(mount.env.startRead, startElementRead);
  assert.equal(mount.options.caption, doc.nodes['file-cloud-caption']);
  assert.equal(mount.options.note, doc.nodes['file-cloud-note']);
  assert.equal(mount.options.turn, false);
  assert.equal(mount.options.swayFor, 5000);
  assert.equal(mount.options.fast, false, 'the normal timings: the cubes light up and the sweep passes');
  assert.equal(mount.options.callouts, undefined, 'no callouts and no card on this page');
  assert.equal(mount.options.card, undefined);
  const expected = fileCloudStrings('en', 'testnet');
  assert.deepEqual(Object.keys(mount.options.strings).sort(), Object.keys(expected).sort());
  assert.equal(mount.options.strings.failed('x'), expected.failed('x'));
});

test('the cloud is worded for the network of the link', () => {
  const { cloud } = open(`/21338/${HASH}`);
  const detail = { firstLedger: 1, lastLedger: 2, chunks: 3, span: 2 };
  assert.equal(cloud.mounts[0].options.strings.note(detail), fileCloudStrings('en', '21338').note(detail));
});

test('load hides the canvas, empties the caption and destroys the cloud', () => {
  const { doc, cloud } = open(CANONICAL);
  const canvas = doc.nodes['file-cloud'];
  const caption = doc.nodes['file-cloud-caption'];
  const note = doc.nodes['file-cloud-note'];
  caption.textContent = 'read · 3 chunks';
  note.textContent = 'Each cube stands on its ledger.';
  note.hidden = false;
  assert.equal(canvas.hidden, false);
  doc.file.load(DETAIL);
  assert.equal(canvas.classList.contains('is-gone'), true);
  assert.equal(canvas.hidden, true, 'a transparent picture would show the page through a covered canvas');
  assert.equal(caption.textContent, '');
  assert.equal(caption.hidden, true);
  assert.equal(note.textContent, '');
  assert.equal(note.hidden, true);
  assert.equal(cloud.destroyed, 1);
});

test('without reduced motion the canvas fades first and goes after 250 ms', async () => {
  const { doc, cloud } = open(CANONICAL, { reducedMotion: false });
  const canvas = doc.nodes['file-cloud'];
  doc.file.load(DETAIL);
  assert.equal(canvas.classList.contains('is-gone'), true, 'the fade starts at once');
  assert.equal(canvas.hidden, false, 'still there while it fades');
  assert.equal(cloud.destroyed, 0);
  await new Promise((resolve) => setTimeout(resolve, 400));
  assert.equal(canvas.hidden, true);
  assert.equal(cloud.destroyed, 1);
});

test('load empties the caption and the note at once, before the fade ends', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { doc, cloud } = open(CANONICAL, { reducedMotion: false });
  const caption = doc.nodes['file-cloud-caption'];
  const note = doc.nodes['file-cloud-note'];
  caption.textContent = 'read · 3 chunks';
  note.textContent = 'Each cube stands on its ledger.';
  note.hidden = false;
  doc.file.load(DETAIL);
  assert.equal(doc.nodes['file-cloud'].hidden, false, 'the canvas is still fading');
  assert.equal(caption.textContent, '');
  assert.equal(caption.hidden, true);
  assert.equal(note.textContent, '');
  assert.equal(note.hidden, true);
  assert.equal(cloud.destroyed, 0);
});

test('load after the hold empties the caption when the hold ends, not after the fade', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { doc, cloud } = open(CANONICAL, { reducedMotion: false });
  cloud.settle = 2000;
  const caption = doc.nodes['file-cloud-caption'];
  caption.textContent = 'read · 3 chunks';
  doc.file.load(DETAIL);
  assert.equal(caption.textContent, 'read · 3 chunks', 'the cubes are still settling');
  t.mock.timers.tick(2000);
  assert.equal(doc.nodes['file-cloud'].hidden, false, 'the fade has only begun');
  assert.equal(caption.textContent, '');
  assert.equal(caption.hidden, true);
});

test('error empties the caption at once, before the fade ends', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { doc, cloud } = open(CANONICAL, { reducedMotion: false });
  const caption = doc.nodes['file-cloud-caption'];
  const note = doc.nodes['file-cloud-note'];
  cloud.failureLine = 'could not read';
  caption.textContent = 'read · 3 chunks';
  note.textContent = 'Each cube stands on its ledger.';
  note.hidden = false;
  doc.file.fail();
  assert.equal(doc.nodes['file-cloud'].hidden, false, 'the canvas is still fading');
  assert.equal(doc.nodes['file-cloud'].classList.contains('is-gone'), true);
  assert.equal(caption.textContent, '');
  assert.equal(caption.hidden, true);
  assert.equal(note.textContent, '');
  assert.equal(note.hidden, true);
  assert.equal(cloud.destroyed, 0);
  t.mock.timers.tick(250);
  assert.equal(doc.nodes['file-cloud'].hidden, true);
  assert.equal(cloud.destroyed, 1);
});

test('load holds the file while the cloud settles, capped at 3 s', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  assert.equal(HOLD_MAX_MS, 3000);

  const short = open(CANONICAL, { reducedMotion: false });
  short.cloud.settle = 2000;
  short.doc.file.load(DETAIL);
  assert.equal(short.doc.stage.classList.contains('is-settling'), true, 'the file is hidden while the cubes settle');
  assert.equal(short.doc.nodes['file-title'].textContent, 'photo.webp', 'the name and details do not wait');
  assert.equal(short.doc.nodes['file-download'].disabled, false);
  t.mock.timers.tick(1999);
  assert.equal(short.doc.stage.classList.contains('is-settling'), true);
  assert.equal(short.doc.nodes['file-cloud'].classList.contains('is-gone'), false);
  t.mock.timers.tick(1);
  assert.equal(short.doc.stage.classList.contains('is-settling'), false, 'released when the cubes have settled');
  assert.equal(short.doc.nodes['file-cloud'].classList.contains('is-gone'), true, 'then the cloud fades');
  assert.equal(short.doc.nodes['file-cloud'].hidden, false);
  t.mock.timers.tick(250);
  assert.equal(short.doc.nodes['file-cloud'].hidden, true);
  assert.equal(short.cloud.destroyed, 1);

  const long = open(CANONICAL, { reducedMotion: false });
  long.cloud.settle = 10_000;
  long.doc.file.load(DETAIL);
  t.mock.timers.tick(2999);
  assert.equal(long.doc.stage.classList.contains('is-settling'), true);
  t.mock.timers.tick(1);
  assert.equal(long.doc.stage.classList.contains('is-settling'), false, 'a slow sweep is cut at 3 s');
  assert.equal(long.doc.nodes['file-cloud'].classList.contains('is-gone'), true);
});

test('a cloud with nothing left to settle does not hold the file', () => {
  const { doc } = open(CANONICAL, { reducedMotion: false });
  doc.file.load(DETAIL);
  assert.equal(doc.stage.classList.contains('is-settling'), false);
  assert.equal(doc.nodes['file-cloud'].classList.contains('is-gone'), true);
});

test('reduced motion shows the file at once', () => {
  const { doc, cloud } = open(CANONICAL, { reducedMotion: true });
  cloud.settle = 2000;
  doc.file.load(DETAIL);
  assert.equal(doc.stage.classList.contains('is-settling'), false);
  assert.equal(doc.nodes['file-cloud'].hidden, true);
  assert.equal(cloud.destroyed, 1);
});

test('error during the hold ends at once', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { doc, cloud } = open(CANONICAL, { reducedMotion: false });
  cloud.settle = 2000;
  doc.file.load(DETAIL);
  assert.equal(doc.stage.classList.contains('is-settling'), true);
  doc.file.fail();
  assert.equal(doc.stage.classList.contains('is-settling'), false);
  assert.equal(doc.nodes['file-cloud'].classList.contains('is-gone'), true);
  t.mock.timers.tick(250);
  assert.equal(doc.nodes['file-cloud'].hidden, true);
  assert.equal(cloud.destroyed, 1);
  t.mock.timers.tick(5000);
  assert.equal(cloud.destroyed, 1, 'the abandoned hold timer does nothing');
});

test('error before the manifest hides the canvas without throwing', () => {
  const { doc, cloud } = open(CANONICAL);
  assert.doesNotThrow(() => doc.file.fail());
  assert.equal(doc.nodes['file-cloud'].hidden, true);
  assert.equal(doc.nodes['file-cloud-caption'].hidden, true);
  assert.equal(cloud.destroyed, 1);
  assert.equal(doc.nodes['file-download'].disabled, true, 'a file that did not load cannot be saved');
});

test('a later event does not end the cloud twice', () => {
  const { doc, cloud } = open(CANONICAL);
  doc.file.load(DETAIL);
  doc.file.fail();
  doc.file.load(DETAIL);
  assert.equal(cloud.destroyed, 1);
});

test('a language switch re-words the cloud', () => {
  const { doc, cloud } = open(CANONICAL);
  doc.switchLanguage('ru');
  assert.equal(cloud.strings.length, 1);
  assert.equal(cloud.strings[0].failed('x'), fileCloudStrings('ru', 'testnet').failed('x'));
  assert.notEqual(cloud.strings[0].failed('x'), cloud.mounts[0].options.strings.failed('x'));
});

test('a language switch after the cloud has ended leaves it alone', () => {
  const { doc, cloud } = open(CANONICAL);
  doc.file.load(DETAIL);
  doc.switchLanguage('ru');
  assert.deepEqual(cloud.strings, []);
});

test('an invalid address mounts no cloud', () => {
  const { doc, cloud } = open('/testnet/xyz');
  assert.deepEqual(cloud.mounts, []);
  assert.equal(doc.nodes['file-cloud'].hidden, false, 'nothing touches the canvas on the invalid view');
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
  const element = html.indexOf('<script src="/element/1.2.1/xrpl-file-element.js" defer></script>');
  const own = html.indexOf('<script type="module" src="/js/file-page.mjs"></script>');
  assert.ok(element > 0 && own > element);
  assert.match(html, /<button id="file-download"[^>]*\bdisabled\b/);
  assert.match(html, /<p id="file-status"[^>]*role="status"/);
  assert.match(html, /<p id="check-link-error" class="check__error" role="alert"><\/p>/);
});

test('the file page has the cloud stage: canvas under the element, caption and note after it', async () => {
  const html = await page('file.html');
  assert.match(html, /<div id="file-view"[^>]*>\s*<div class="file__stage">\s*<canvas id="file-cloud" class="cloud file__cloud"[^>]*><\/canvas>\s*<\/div>/);
  assert.match(html, /<p id="file-cloud-caption" class="cloud-caption"><\/p>/);
  assert.match(html, /<p id="file-cloud-note" class="cloud-note" hidden><\/p>/);
  assert.ok(html.indexOf('id="file-cloud-caption"') > html.indexOf('class="file__stage"'));
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

test('the way home on a page with one URL leads to the home of the language in use, for every active language', () => {
  const { doc } = open(CANONICAL);
  for (const lang of LANGS) {
    doc.switchLanguage(lang);
    assert.equal(doc.home.getAttribute('href'), homePath(lang), lang);
  }
});

test('a language the site does not offer falls back to English instead of throwing', () => {
  const { doc } = open(CANONICAL, { lang: 'xx' });
  assert.equal(doc.heading.textContent, 'How this works');
  doc.switchLanguage('fr');
  assert.equal(doc.heading.textContent, 'How this works');
});

test('sizes take their separators from the language locale', () => {
  for (const lang of LANGS) {
    const decimal = new Intl.NumberFormat(localeOf(lang), { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(4.2);
    assert.equal(formatSize(4200, lang), t('sizeKB', lang, { value: decimal }), lang);
  }
});
