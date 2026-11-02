import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import { TEMPLATE } from '../src/element/template.mjs';

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

test('the caption attribute is observed', () => {
  assert.ok(XrplFileElement.observedAttributes.includes('caption'));
});

test('the stubbed template really holds the caption and the download parts', () => {
  for (const id of ['caption', 'download', 'dlStatus']) assert.match(TEMPLATE, new RegExp(`id="${id}"`));
});
