import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import { TEMPLATE } from '../src/element/template.mjs';
import { STRINGS } from '../src/element/strings.mjs';

// Just enough of a page for the real element class to run: every id in the template becomes a stub
// node, and the pool is replaced by a lease that hands out a fake file handle. The element's reads,
// its views and its save path are the shipped code; only the browser and the ledger are fakes.
class StubNode extends EventTarget {
  hidden = false;
  disabled = false;
  textContent = '';
  title = '';
  dataset = {};
  style = {};
  #attributes = new Map();
  classes = new Set();
  classList = {
    add: (...names) => names.forEach((name) => this.classes.add(name)),
    remove: (...names) => names.forEach((name) => this.classes.delete(name)),
    toggle: (name, force) => {
      const on = force ?? !this.classes.has(name);
      if (on) this.classes.add(name); else this.classes.delete(name);
      return on;
    },
    contains: (name) => this.classes.has(name),
  };

  setAttribute(name, value) { this.#attributes.set(name, String(value)); }
  getAttribute(name) { return this.#attributes.get(name) ?? null; }
  removeAttribute(name) { this.#attributes.delete(name); }
  prepend() {}
  append() {}
  closest() { return null; }
  click() {}
  remove() {}
}

class StubRoot {
  #nodes = new Map();
  set innerHTML(html) {
    for (const [, id] of html.matchAll(/\sid="([^"]+)"/g)) this.#nodes.set(id, new StubNode());
  }
  querySelectorAll() { return [...this.#nodes.entries()].map(([id, node]) => Object.assign(node, { id })); }
  node(id) { return this.#nodes.get(id); }
  prepend() {}
}

class StubHTMLElement extends EventTarget {
  #attributes = new Map();
  textContent = '';
  parentElement = null;
  // The one selector the element asks for: like the DOM's, it matches the element itself first.
  closest(selector) {
    assert.equal(selector, '[lang]');
    return this.getAttribute('lang') !== null ? this : (this.parentElement?.closest(selector) ?? null);
  }
  attachShadow() {
    this.shadowRoot = new StubRoot();
    return this.shadowRoot;
  }
  getAttribute(name) { return this.#attributes.get(name) ?? null; }
  setAttribute(name, value) {
    const previous = this.getAttribute(name);
    this.#attributes.set(name, String(value));
    this.attributeChangedCallback?.(name, previous, String(value));
  }
  removeAttribute(name) {
    const previous = this.getAttribute(name);
    this.#attributes.delete(name);
    this.attributeChangedCallback?.(name, previous, null);
  }
}

const realSetTimeout = globalThis.setTimeout;
// The Blob path revokes its object URL a minute later; an unref'd timer lets the test process end.
globalThis.setTimeout = (callback, delay, ...rest) => {
  const timer = realSetTimeout(callback, delay, ...rest);
  if (delay >= 60_000) timer.unref?.();
  return timer;
};
// Progress is painted on animation frames; the read events of these tests do not depend on them.
globalThis.requestAnimationFrame = (callback) => realSetTimeout(() => callback(0), 0);
globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
globalThis.HTMLElement = StubHTMLElement;
globalThis.isSecureContext = true;
globalThis.document = {
  readyState: 'complete',
  createElement: () => new StubNode(),
  body: new StubNode(),
  addEventListener() {},
  removeEventListener() {},
};

const { XrplFileElement } = await import('../src/element/element.mjs');
const { pool } = await import('../src/element/page.mjs');

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const LINK = `xrpl-file://testnet/${HASH}`;
const SVG = new TextEncoder().encode(`<svg xmlns="http://www.w3.org/2000/svg"/>${' '.repeat(268 - 41)}`);
const MANIFEST = { v: 1, type: 'image/svg+xml', size: 268, blocks: 1, chunk: 1000, name: 'drawing.svg' };

function fakeHandle({ manifest = MANIFEST, recordError = null, hang = false } = {}) {
  return {
    released: false,
    async record() {
      if (hang) return new Promise(() => {});
      if (recordError) throw recordError;
      return { manifest };
    },
    async cover() { return null; },
    async blockBytes() { return 1_000_000; },
    async block() { return SVG.slice(); },
    release() { this.released = true; },
  };
}

const realAcquire = pool.acquire;
after(() => { pool.acquire = realAcquire; });

function mount(handle, attributes = { src: LINK, loading: 'eager' }) {
  pool.acquire = () => ({ files: { open: () => handle }, release() {} });
  const element = new XrplFileElement();
  for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
  return element;
}

const settled = (element) => new Promise((resolve) => {
  element.addEventListener('load', (event) => resolve({ type: 'load', event }), { once: true });
  element.addEventListener('error', (event) => resolve({ type: 'error', event }), { once: true });
});

async function loaded(element) {
  const outcome = settled(element);
  element.connectedCallback();
  const result = await outcome;
  assert.equal(result.type, 'load');
  return result.event;
}

function installPicker() {
  const writable = {
    chunks: [],
    async write(bytes) { writable.chunks.push(bytes); },
    async close() { writable.closed = true; },
    async abort() {},
  };
  globalThis.showSaveFilePicker = async () => ({ createWritable: async () => writable });
  return writable;
}

function installBlobSink() {
  delete globalThis.showSaveFilePicker;
  const saved = [];
  const anchor = new StubNode();
  anchor.click = () => saved.push(anchor.download);
  globalThis.document.createElement = () => anchor;
  return saved;
}

test('load detail carries name, type, size, network and link', async () => {
  const element = mount(fakeHandle());
  const event = await loaded(element);
  assert.deepEqual(event.detail, {
    name: 'drawing.svg', type: 'image/svg+xml', size: 268, network: 'testnet', link: LINK,
  });
});

test('load detail names no file when the manifest has no name, and gives the type without parameters', async () => {
  const element = mount(fakeHandle({ manifest: { ...MANIFEST, name: undefined, type: 'Image/SVG+XML; charset=utf-8' } }));
  const { detail } = await loaded(element);
  assert.equal(detail.name, null);
  assert.equal(detail.type, 'image/svg+xml');
});

test('load detail link is canonical whatever the src looked like', async () => {
  const element = mount(fakeHandle(), { src: `XRPL-FILE://TestNet/${HASH.toLowerCase()}`, loading: 'eager' });
  const { detail } = await loaded(element);
  assert.equal(detail.link, LINK);
  assert.equal(detail.network, 'testnet');
});

test('save() before the file is known rejects NotLoaded', async () => {
  const unconnected = mount(fakeHandle());
  await assert.rejects(unconnected.save(), (error) => error instanceof XrplFileError && error.code === 'NotLoaded');

  const reading = mount(fakeHandle({ hang: true }));
  reading.connectedCallback();
  await assert.rejects(reading.save(), (error) => error instanceof XrplFileError && error.code === 'NotLoaded');
});

test("save() after an error rejects with that error's code", async () => {
  const element = mount(fakeHandle({ recordError: new XrplFileError('ManifestNotFound', 'no manifest') }));
  const outcome = settled(element);
  element.connectedCallback();
  assert.equal((await outcome).type, 'error');
  await assert.rejects(element.save(), (error) => error instanceof XrplFileError && error.code === 'ManifestNotFound');
});

test('save() resolves started on the Blob path', async () => {
  const saved = installBlobSink();
  const element = mount(fakeHandle());
  await loaded(element);
  assert.equal(await element.save(), 'started');
  assert.deepEqual(saved, ['drawing.svg']);
});

test('save() resolves saved on the picker path', async () => {
  const writable = installPicker();
  const element = mount(fakeHandle());
  await loaded(element);
  assert.equal(await element.save(), 'saved');
  assert.equal(writable.closed, true);
  assert.deepEqual(writable.chunks[0], SVG);
  delete globalThis.showSaveFilePicker;
});

test('save() resolves cancelled when the visitor closes the dialog', async () => {
  globalThis.showSaveFilePicker = async () => { throw new DOMException('closed', 'AbortError'); };
  const element = mount(fakeHandle());
  await loaded(element);
  assert.equal(await element.save(), 'cancelled');
  delete globalThis.showSaveFilePicker;
});

test('a save that fails rejects, and the verified file stays on screen', async () => {
  installBlobSink();
  const handle = fakeHandle();
  handle.block = async () => { throw new XrplFileError('BlockMismatch', 'bad block', { block: 0 }); };
  const element = mount(handle);
  await loaded(element);
  await assert.rejects(element.save(), (error) => error.code === 'BlockMismatch');
  assert.equal(element.shadowRoot.node('download').hidden, false);
  assert.match(element.shadowRoot.node('dlStatus').textContent, /did not pass verification/);
});

test('a second save() while one is running shares its result', async () => {
  installBlobSink();
  const handle = fakeHandle();
  const element = mount(handle);
  await loaded(element);
  const first = element.save();
  const second = element.save();
  assert.deepEqual(await Promise.all([first, second]), ['started', 'started']);
});

test('caption="off" keeps the caption hidden after load, and removing the attribute shows it again on hover', async () => {
  const element = mount(fakeHandle(), { src: LINK, loading: 'eager', caption: 'off' });
  await loaded(element);
  const caption = element.shadowRoot.node('caption');
  assert.equal(caption.hidden, true);

  element.removeAttribute('caption');
  assert.equal(caption.hidden, false, 'the caption is in the DOM again; the stylesheet reveals it on hover');

  element.setAttribute('caption', 'off');
  assert.equal(caption.hidden, true);
  element.setAttribute('caption', 'on');
  assert.equal(caption.hidden, false, 'any value but off keeps today\'s behaviour');
});

test('background="transparent" marks the frame, and the mark follows the attribute live', () => {
  const element = mount(fakeHandle(), { src: LINK, loading: 'eager' });
  const frame = element.shadowRoot.node('frame');
  assert.equal(frame.dataset.background, undefined, 'no attribute keeps the theme box');

  element.setAttribute('background', 'transparent');
  assert.equal(frame.dataset.background, 'transparent');
  element.setAttribute('background', 'theme');
  assert.equal(frame.dataset.background, undefined, 'any other value keeps the box');
  element.setAttribute('background', 'Transparent');
  assert.equal(frame.dataset.background, 'transparent');
  element.removeAttribute('background');
  assert.equal(frame.dataset.background, undefined);
});

test('background set before the element is built applies from the start', () => {
  const element = mount(fakeHandle(), { background: 'transparent', src: LINK, loading: 'eager' });
  assert.equal(element.shadowRoot.node('frame').dataset.background, 'transparent');
});

test('the background attribute is observed', () => {
  assert.ok(XrplFileElement.observedAttributes.includes('background'));
});

test('the caption attribute is observed', () => {
  assert.ok(XrplFileElement.observedAttributes.includes('caption'));
});

test('the stubbed template really holds the caption and the download parts', () => {
  for (const id of ['caption', 'download', 'dlStatus']) assert.match(TEMPLATE, new RegExp(`id="${id}"`));
});

// Language

const part = (element, id) => element.shadowRoot.node(id);
const RU = STRINGS.ru;
const DE = STRINGS.de;

/** Lets the document name a language for the length of one call. */
function withPageLanguage(documentLanguage, run) {
  const saved = globalThis.document.documentElement;
  globalThis.document.documentElement = documentLanguage === undefined ? undefined : { lang: documentLanguage };
  try { return run(); } finally { globalThis.document.documentElement = saved; }
}

const later = (ms = 5) => new Promise((resolve) => realSetTimeout(resolve, ms));

test('the lang attribute is observed', () => {
  assert.ok(XrplFileElement.observedAttributes.includes('lang'));
});

test('an element with its own lang draws its fixed words in that language from the start', () => {
  const element = mount(fakeHandle({ hang: true }), { lang: 'ru', src: LINK, loading: 'eager' });
  assert.equal(part(element, 'textDownload').textContent, RU.download);
  assert.equal(part(element, 'dlFull').textContent, RU.download);
  assert.equal(part(element, 'dlShort').textContent, RU.save);
  assert.equal(part(element, 'loading').getAttribute('aria-label'), RU.loadingLabel);
  assert.equal(part(element, 'copyText').textContent, RU.copyLink);
  assert.equal(part(element, 'copy').getAttribute('aria-label'), RU.copyLink);
});

test('without a lang the element is English', () => {
  const element = withPageLanguage(undefined, () => mount(fakeHandle({ hang: true })));
  assert.equal(part(element, 'dlFull').textContent, 'Download');
  assert.equal(part(element, 'loading').getAttribute('aria-label'), 'Loading the file');
});

test('a lang the element has no words for is English', () => {
  const element = mount(fakeHandle({ hang: true }), { lang: 'fr', src: LINK, loading: 'eager' });
  assert.equal(part(element, 'dlFull').textContent, 'Download');
});

test('the nearest ancestor with a lang decides when the element has none, once it is connected', () => {
  const element = withPageLanguage('es', () => mount(fakeHandle({ hang: true })));
  element.closest = () => ({ getAttribute: () => 'de-AT', parentElement: null });
  element.connectedCallback();
  assert.equal(part(element, 'dlFull').textContent, DE.download);
  assert.equal(part(element, 'progressBytes').textContent, DE.reading);
  element.disconnectedCallback();
});

test('the document lang decides when no ancestor has one', () => {
  const element = withPageLanguage('pt-BR', () => {
    const mounted = mount(fakeHandle({ hang: true }));
    mounted.connectedCallback();
    return mounted;
  });
  assert.equal(part(element, 'dlFull').textContent, STRINGS.pt.download);
  assert.equal(part(element, 'progressBytes').textContent, STRINGS.pt.reading);
  element.disconnectedCallback();
});

test('changing lang words the loading view again, and removing it goes back to the language of the page', () => {
  withPageLanguage(undefined, () => {
    const element = mount(fakeHandle({ hang: true }));
    element.connectedCallback();
    assert.equal(part(element, 'progressBytes').textContent, 'Reading the file description…');
    assert.equal(part(element, 'dlFull').textContent, 'Download');

    element.setAttribute('lang', 'ru');
    assert.equal(part(element, 'progressBytes').textContent, RU.reading);
    assert.equal(part(element, 'loading').getAttribute('aria-label'), RU.loadingLabel);
    assert.equal(part(element, 'dlFull').textContent, RU.download);
    assert.equal(part(element, 'dlShort').textContent, RU.save);
    assert.equal(part(element, 'textDownload').textContent, RU.download);
    assert.equal(part(element, 'copyText').textContent, RU.copyLink);

    element.setAttribute('lang', 'zh-Hans');
    assert.equal(part(element, 'progressBytes').textContent, STRINGS.zh.reading);

    element.removeAttribute('lang');
    assert.equal(part(element, 'progressBytes').textContent, 'Reading the file description…');
    assert.equal(part(element, 'dlFull').textContent, 'Download');
    element.disconnectedCallback();
  });
});

test('changing lang does not start the load over', async () => {
  const element = mount(fakeHandle());
  let loads = 0;
  element.addEventListener('load', () => { loads++; });
  await loaded(element);
  element.setAttribute('lang', 'ru');
  element.setAttribute('lang', 'de');
  await later();
  assert.equal(loads, 1);
  assert.equal(part(element, 'download').hidden, false, 'the file is still on screen');
});

// A plain-text file is what the stub can show as verified; it cannot decode a picture.
const TEXT = { ...MANIFEST, type: 'text/plain', name: 'notes.txt' };

test('the progress is worded in the language, bytes and blocks, and again on a change', async () => {
  const element = mount(fakeHandle({ manifest: TEXT }), { lang: 'ru', src: LINK, loading: 'eager' });
  await loaded(element);
  // The last report of a one-block file is all of its bytes with its block verified.
  assert.equal(part(element, 'progressBytes').textContent, '268 из 268 Б');
  assert.equal(part(element, 'progressBlocks').textContent, ' · проверено блоков: 1 из 1');
  element.setAttribute('lang', 'en');
  assert.equal(part(element, 'progressBytes').textContent, '268 of 268 B');
  assert.equal(part(element, 'progressBlocks').textContent, ' · 1 of 1 blocks verified');
});

test('an error is worded again, with its short title and tooltips, when lang changes', async () => {
  const element = mount(fakeHandle({ recordError: new XrplFileError('ManifestNotFound', 'no manifest') }));
  const outcome = settled(element);
  element.connectedCallback();
  assert.equal((await outcome).type, 'error');
  assert.equal(part(element, 'msgFull').textContent, 'This file is not on the ledger.');
  assert.equal(part(element, 'msgShort').textContent, 'Not found');

  element.setAttribute('lang', 'ru');
  assert.equal(part(element, 'msgFull').textContent, RU.manifestNotFound);
  assert.equal(part(element, 'msgShort').textContent, RU.shortNotFound);
  assert.equal(part(element, 'msgShort').title, RU.manifestNotFound);
  assert.equal(part(element, 'msgIcon').title, RU.manifestNotFound);

  element.setAttribute('lang', 'de');
  assert.equal(part(element, 'msgFull').textContent, DE.manifestNotFound);
  assert.equal(part(element, 'msgShort').textContent, DE.shortNotFound);
});

test('the error event keeps its English message under lang="ru", while the box says it in Russian', async () => {
  const element = mount(fakeHandle({ recordError: new XrplFileError('ManifestNotFound', 'no manifest') }), { lang: 'ru', src: LINK, loading: 'eager' });
  const outcome = settled(element);
  element.connectedCallback();
  const { type, event } = await outcome;
  assert.equal(type, 'error');
  assert.deepEqual(event.detail, { name: 'ManifestNotFound', message: 'This file is not on the ledger.' });
  assert.equal(part(element, 'msgFull').textContent, RU.manifestNotFound);
});

test('an error the element raises itself is English in the event and worded in the box', async () => {
  const element = mount(fakeHandle(), { lang: 'de', src: LINK, loading: 'eager', node: 'http://example.com' });
  const outcome = settled(element);
  element.connectedCallback();
  const { type, event } = await outcome;
  assert.equal(type, 'error');
  assert.deepEqual(event.detail, { name: 'NodeUnreachable', message: 'The node attribute must hold wss:// addresses, separated by spaces or commas.' });
  assert.equal(part(element, 'msgFull').textContent, DE.badNode);
  assert.equal(part(element, 'msgShort').textContent, DE.shortBadNode);
});

test('save() rejects with the English error whatever the language of the box', async () => {
  const element = mount(fakeHandle({ recordError: new XrplFileError('ManifestNotFound', 'no manifest') }), { lang: 'ru', src: LINK, loading: 'eager' });
  const outcome = settled(element);
  element.connectedCallback();
  await outcome;
  await assert.rejects(element.save(), (error) => error.code === 'ManifestNotFound');
});

test('the download view, its note and its size are worded again when lang changes', async () => {
  const manifest = { v: 1, type: 'image/png', size: 25_000_000, blocks: 25, chunk: 1000, name: 'report.png' };
  const element = mount(fakeHandle({ manifest }));
  await loaded(element);
  assert.equal(part(element, 'dlNote').textContent, 'This picture is too large to show here.');
  assert.equal(part(element, 'dlSize').textContent, '25.0 MB');
  assert.equal(part(element, 'dlButton').title, 'Download report.png');

  element.setAttribute('lang', 'ru');
  assert.equal(part(element, 'dlNote').textContent, RU.noteTooLarge);
  assert.equal(part(element, 'dlNote').hidden, false);
  assert.equal(part(element, 'dlSize').textContent, '25,0 МБ');
  assert.equal(part(element, 'dlButton').title, 'Скачать report.png');
  assert.equal(part(element, 'dlName').textContent, 'report.png', 'the file name is the file own, not ours');
});

test('a file with no type says unknown type in the language and has no note', async () => {
  const manifest = { v: 1, type: '', size: 268, blocks: 1, chunk: 1000, name: 'blob' };
  const element = mount(fakeHandle({ manifest }), { lang: 'de', src: LINK, loading: 'eager' });
  await loaded(element);
  assert.equal(part(element, 'dlType').textContent, `${DE.unknownType} · `);
  assert.equal(part(element, 'dlNote').hidden, true);
});

test('the save status line is worded again when lang changes', async () => {
  installBlobSink();
  const element = mount(fakeHandle());
  await loaded(element);
  assert.equal(await element.save(), 'started');
  assert.equal(part(element, 'dlStatus').textContent, 'Download started.');
  assert.equal(part(element, 'textStatus').textContent, 'Download started.');

  element.setAttribute('lang', 'ru');
  assert.equal(part(element, 'dlStatus').textContent, RU.downloadStarted);
  assert.equal(part(element, 'textStatus').textContent, RU.downloadStarted);
  assert.equal(part(element, 'textStatus').title, RU.downloadStarted);
});

test('a failed save names the block in the language and is worded again on a change', async () => {
  installBlobSink();
  const handle = fakeHandle();
  const readable = handle.block;
  let failing = false;
  handle.block = async (...args) => {
    if (failing) throw new XrplFileError('BlockMismatch', 'bad block', { block: 0 });
    return readable(...args);
  };
  const element = mount(handle, { lang: 'de', src: LINK, loading: 'eager' });
  await loaded(element);
  failing = true;
  await assert.rejects(element.save(), (error) => error.code === 'BlockMismatch');
  assert.equal(part(element, 'dlStatus').textContent, DE.saveBlockMismatch(0));
  element.setAttribute('lang', 'en');
  assert.equal(part(element, 'dlStatus').textContent, 'Saving stopped at block 0: it did not pass verification.');
});

test('the caption says verified and the size in the language', async () => {
  const element = mount(fakeHandle({ manifest: TEXT }), { lang: 'ru', src: LINK, loading: 'eager' });
  await loaded(element);
  assert.equal(part(element, 'capWord').textContent, 'проверено · ');
  assert.equal(part(element, 'capName').textContent, 'notes.txt · 268 Б');
  element.setAttribute('lang', 'de');
  assert.equal(part(element, 'capWord').textContent, 'geprüft · ');
  assert.equal(part(element, 'capName').textContent, 'notes.txt · 268 B');
});

test('the note on a text cut at 1 MB follows the language, and so does the size in it', async () => {
  const manifest = { v: 1, type: 'text/plain', size: 3_000_000, blocks: 5, chunk: 1000, name: 'log.txt' };
  const handle = fakeHandle({ manifest });
  handle.blockBytes = async () => 600_000;
  handle.block = async () => new Uint8Array(600_000).fill(0x61);
  const element = mount(handle);
  await loaded(element);
  assert.equal(part(element, 'textNote').textContent, 'Showing the first 1 MB of 3.0 MB.');

  element.setAttribute('lang', 'ru');
  assert.equal(part(element, 'textNote').textContent, 'Показан первый 1 МБ из 3,0 МБ.');
  assert.equal(part(element, 'textNote').title, 'Показан первый 1 МБ из 3,0 МБ.');
});

test('the Open and Play buttons of a click-to-load element follow the language', async () => {
  const picture = mount(fakeHandle(), { src: LINK, loading: 'click' });
  picture.connectedCallback();
  await later();
  assert.equal(part(picture, 'open').textContent, 'Open');
  picture.setAttribute('lang', 'ru');
  assert.equal(part(picture, 'open').textContent, RU.open);
  assert.equal(part(picture, 'open').getAttribute('aria-label'), RU.open);
  picture.disconnectedCallback();

  const video = mount(fakeHandle({ manifest: { ...MANIFEST, type: 'video/mp4', name: 'clip.mp4' } }), { src: LINK, loading: 'click' });
  video.connectedCallback();
  await later();
  assert.equal(part(video, 'open').textContent, '▶');
  assert.equal(part(video, 'open').getAttribute('aria-label'), 'Play');
  video.setAttribute('lang', 'de');
  assert.equal(part(video, 'open').textContent, '▶');
  assert.equal(part(video, 'open').getAttribute('aria-label'), DE.play);
  assert.equal(part(video, 'idleMeta').textContent, '268 B');
  video.setAttribute('lang', 'ru');
  assert.equal(part(video, 'idleMeta').textContent, '268 Б');
  video.disconnectedCallback();
});

test('the copy button and its feedback follow the language', async () => {
  const element = mount(fakeHandle());
  await loaded(element);
  assert.equal(part(element, 'copyText').textContent, 'Copy link');
  element.setAttribute('lang', 'ru');
  assert.equal(part(element, 'copyText').textContent, RU.copyLink);
  assert.equal(part(element, 'copy').getAttribute('aria-label'), RU.copyLink);

  // The stub has no clipboard and no way to select text, so the copy fails: that is the feedback to follow.
  part(element, 'copy').dispatchEvent(new Event('click'));
  await later();
  assert.equal(part(element, 'copyText').textContent, RU.copyFailed);
  assert.equal(part(element, 'copyStatus').textContent, RU.copyFailed);
  assert.equal(part(element, 'copy').dataset.state, 'failed');
  element.setAttribute('lang', 'de');
  assert.equal(part(element, 'copyText').textContent, DE.copyFailed);
  assert.equal(part(element, 'copy').getAttribute('aria-label'), DE.copyFailed);
  assert.equal(part(element, 'copy').dataset.state, 'failed');
  element.disconnectedCallback();
});

const VIEW_IDS = ['idle', 'loading', 'image', 'video', 'text', 'download', 'error'];
const shownViews = (element) => VIEW_IDS.filter((id) => !part(element, id).hidden);

test('loader toggled live does not show the idle view of an element that has nothing to open yet', async () => {
  // A lazy element waits for the page to scroll near it; an observer that never reports keeps it there.
  const realObserver = globalThis.IntersectionObserver;
  globalThis.IntersectionObserver = class { observe() {} disconnect() {} };
  try {
    const element = mount(fakeHandle(), { src: LINK });
    element.connectedCallback();
    assert.deepEqual(shownViews(element), [], 'waiting: no view yet');

    element.setAttribute('loader', 'false');
    assert.deepEqual(shownViews(element), [], 'the empty idle view stays hidden, its Open button would do nothing');
    assert.equal(part(element, 'frame').classList.contains('bare'), false);

    element.setAttribute('loader', 'true');
    assert.deepEqual(shownViews(element), []);
    element.disconnectedCallback();
  } finally {
    if (realObserver === undefined) delete globalThis.IntersectionObserver;
    else globalThis.IntersectionObserver = realObserver;
  }
});

test('loader toggled live shows or hides the loading view and bares the box while a file is loading', () => {
  const element = mount(fakeHandle({ hang: true }));
  element.connectedCallback();
  assert.deepEqual(shownViews(element), ['loading']);
  assert.equal(part(element, 'frame').classList.contains('bare'), false);

  element.setAttribute('loader', 'false');
  assert.deepEqual(shownViews(element), [], 'the box stays empty for the site\'s own placeholder');
  assert.equal(part(element, 'frame').classList.contains('bare'), true);

  element.setAttribute('loader', 'true');
  assert.deepEqual(shownViews(element), ['loading']);
  assert.equal(part(element, 'frame').classList.contains('bare'), false);
  element.disconnectedCallback();
});

test('loader toggled live leaves the idle view of a click-to-load element in place', async () => {
  const element = mount(fakeHandle(), { src: LINK, loading: 'click' });
  element.connectedCallback();
  await later();
  assert.deepEqual(shownViews(element), ['idle']);

  element.setAttribute('loader', 'false');
  assert.deepEqual(shownViews(element), ['idle']);
  element.setAttribute('loader', 'true');
  assert.deepEqual(shownViews(element), ['idle']);
  element.disconnectedCallback();
});

// A file of three 100-byte chunks. Like the real handle, `block` reports every chunk of the block through
// onChunk before it resolves, whether or not the block is cached.
const READ_TYPES = ['xrpl-file-manifest', 'xrpl-file-chunk', 'xrpl-file-block'];
const PNG_MAGIC = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

function chunkedHandle({ type = 'image/png', chunksPerBlock = 2, gate = null } = {}) {
  const blockBytes = chunksPerBlock * 100;
  const bytes = new Uint8Array(268).fill(0x61);
  if (type === 'image/png') bytes.set(PNG_MAGIC);
  const manifest = {
    ...MANIFEST, type, chunk: 100, blocks: Math.ceil(268 / blockBytes), firstLedger: 500, lastLedger: 520,
  };
  const handle = fakeHandle({ manifest });
  handle.blockBytes = async () => blockBytes;
  handle.block = async (index, { onChunk } = {}) => {
    const first = index * chunksPerBlock;
    const last = Math.min(3, first + chunksPerBlock);
    for (let i = first; i < last; i++) {
      onChunk?.({ index: i, ledger: 500 + i });
      if (gate !== null && i === 0) await gate.promise;
    }
    // The stubbed image never decodes by itself: say so once the last block is back.
    if (last === 3 && type === 'image/png') setImmediate(() => handle.element?.shadowRoot.node('img').dispatchEvent(new Event('load')));
    return bytes.slice(index * blockBytes, Math.min(268, (index + 1) * blockBytes));
  };
  return handle;
}

function mountChunked(options) {
  const handle = chunkedHandle(options);
  const element = mount(handle);
  handle.element = element;
  return element;
}

function recordReads(element) {
  const events = [];
  for (const type of [...READ_TYPES, 'load']) {
    element.addEventListener(type, (event) => events.push({ type, detail: event.detail, bubbles: event.bubbles }));
  }
  return events;
}

const named = (events) => events.map(({ type, detail }) => (detail && 'index' in detail ? `${type}:${detail.index}` : type));

test('an image load emits manifest, then each chunk once, then each block once, before load', async () => {
  const element = mountChunked({});
  const events = recordReads(element);
  await loaded(element);

  // Block 0 is read twice on the image path (the type check, then the whole file).
  assert.deepEqual(named(events), [
    'xrpl-file-manifest',
    'xrpl-file-chunk:0', 'xrpl-file-chunk:1', 'xrpl-file-block:0',
    'xrpl-file-chunk:2', 'xrpl-file-block:1',
    'load',
  ]);
  assert.deepEqual(events[0].detail, { size: 268, chunk: 100, chunks: 3, blocks: 2, firstLedger: 500, lastLedger: 520 });
  assert.deepEqual(events[1].detail, { index: 0, ledger: 500 });
  assert.deepEqual(events[3].detail, { index: 0, firstChunk: 0, lastChunk: 1 });
  assert.deepEqual(events[5].detail, { index: 1, firstChunk: 2, lastChunk: 2 });
});

test('a cached block reports its chunks then the block', async () => {
  // A text file takes the loadWhole path alone; one block of ten chunks' room holds all three.
  const element = mountChunked({ type: 'text/plain', chunksPerBlock: 10 });
  const events = recordReads(element);
  await loaded(element);

  assert.deepEqual(named(events), [
    'xrpl-file-manifest',
    'xrpl-file-chunk:0', 'xrpl-file-chunk:1', 'xrpl-file-chunk:2',
    'xrpl-file-block:0',
    'load',
  ]);
  assert.deepEqual(events.at(-2).detail, { index: 0, firstChunk: 0, lastChunk: 2 });
});

test('a src change mid-read emits nothing more for the old file', async () => {
  let release;
  const gate = { promise: new Promise((resolve) => { release = resolve; }) };
  const element = mountChunked({ gate, type: 'text/plain' });
  const events = recordReads(element);
  const firstChunk = new Promise((resolve) => element.addEventListener('xrpl-file-chunk', resolve, { once: true }));
  element.connectedCallback();
  await firstChunk;

  element.removeAttribute('src');
  const seen = events.length;
  release();
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(named(events), ['xrpl-file-manifest', 'xrpl-file-chunk:0']);
  assert.equal(events.length, seen);
});

test('save() emits no read events', async () => {
  installBlobSink();
  const element = mountChunked({});
  await loaded(element);
  const events = recordReads(element);

  assert.equal(await element.save(), 'started');
  assert.deepEqual(events, []);
});

test('events do not bubble', async () => {
  const element = mountChunked({});
  const events = recordReads(element);
  await loaded(element);

  const reads = events.filter(({ type }) => READ_TYPES.includes(type));
  assert.ok(reads.length > 0);
  assert.ok(reads.every(({ bubbles }) => bubbles === false));
});
