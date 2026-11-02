import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { mountCloud } = await import('../src/js/cloud.mjs');
const { XrplFileError } = await import('../src/js/xrpl.mjs');

const HASH = '2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4';
const LINK = `xrpl-file://testnet/${HASH}`;
// 123 chunks over 8 ledgers, like the hero file.
const MANIFEST = { size: 122_958, chunk: 1000, type: 'image/png', firstLedger: 100, lastLedger: 107 };

function fakeElement(tag) {
  const element = {
    tag,
    children: [],
    attrs: {},
    dataset: {},
    style: {},
    className: '',
    textContent: '',
    hidden: false,
    offsetWidth: 90,
    offsetHeight: 40,
    classList: {
      toggled: [],
      toggle(name, force) { this.toggled.push([name, force]); },
    },
    setAttribute(name, value) { this.attrs[name] = String(value); },
    getAttribute(name) { return this.attrs[name] ?? null; },
    append(...nodes) { for (const node of nodes) this.children.push(typeof node === 'string' ? { tag: '#text', textContent: node } : node); },
    replaceChildren(...nodes) { this.children = []; this.append(...nodes); },
    querySelector(selector) {
      for (const child of this.children) {
        if (child.tag === selector) return child;
        const found = child.querySelector?.(selector);
        if (found) return found;
      }
      return null;
    },
  };
  return element;
}

const textOf = (node) => (node.children?.length ? node.children.map(textOf).join('') : node.textContent ?? '');

function fakeCanvas({ computedStyle = () => '' } = {}) {
  const log = { putImageData: 0, styleReads: 0, strokes: 0 };
  const context = {
    createImageData: (w, h) => ({ width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }),
    putImageData(image) { log.putImageData += 1; log.last = image; },
    beginPath() {}, moveTo() {}, lineTo() {}, arc() {}, fill() {},
    stroke() { log.strokes += 1; },
  };
  const document = {
    hidden: false,
    documentElement: Object.assign(fakeElement('html'), { dataset: { theme: 'dark' } }),
    head: fakeElement('head'),
    createElement: fakeElement,
    createElementNS: (_, tag) => fakeElement(tag),
    defaultView: {
      devicePixelRatio: 2,
      getComputedStyle: () => ({ getPropertyValue: (name) => { log.styleReads += 1; return computedStyle(name); } }),
      matchMedia: () => ({ matches: false }),
    },
  };
  const canvas = Object.assign(fakeElement('canvas'), {
    ownerDocument: document,
    clientWidth: 400,
    clientHeight: 300,
    offsetLeft: 0,
    offsetTop: 0,
    width: 0,
    height: 0,
    getContext: () => context,
  });
  return { canvas, document, log };
}

const strings = {
  label: (phase, { chunks }) => `label:${phase}:${chunks}`,
  caption: ({ chunks, firstLedger, lastLedger }) => `caption:${chunks}:${firstLedger}-${lastLedger}`,
  failed: (code) => `failed:${code}`,
  fileLinkLabel: 'file page',
  verified: 'verified',
  note: 'drawn note',
  fileCallout: (chunks) => ({ caption: 'file', value: String(chunks), unit: 'chunks' }),
  linkCallout: (bytes) => ({ caption: 'link', value: String(bytes), unit: 'bytes' }),
};

function fakeEnv(overrides = {}) {
  let time = 1000;
  const polls = [];
  const timers = [];
  const frames = [];
  const stored = new Map();
  const loaded = [];
  const env = {
    now: () => time,
    setPoll: (callback) => polls.push(callback) && polls.length,
    clearPoll: (id) => { polls[id - 1] = null; },
    setTimer: (callback) => timers.push(callback) && timers.length,
    clearTimer: (id) => { timers[id - 1] = null; },
    requestFrame: (callback) => frames.push(callback),
    cancelFrame: () => {},
    storage: { getItem: (key) => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value) },
    loadElement: (doc) => loaded.push(doc),
    ...overrides,
  };
  return {
    env,
    loaded,
    advance(ms) { time += ms; },
    pollOnce() { for (const poll of polls) poll?.(); },
    firePending() { for (const timer of timers.splice(0)) timer?.(); },
    flushFrames() { const batch = frames.splice(0); for (const frame of batch) frame(); return batch.length; },
    pendingFrames: () => frames.length,
    activePolls: () => polls.filter(Boolean).length,
  };
}

const tick = () => new Promise((resolve) => setImmediate(resolve));
const chunk = (index) => ({ index, bytes: Uint8Array.from({ length: 1000 }, (_, i) => (i * 7 + index * 13) % 256) });

function page() {
  const parts = {
    ...fakeCanvas(),
    caption: fakeElement('figcaption'),
    note: fakeElement('p'),
    callouts: { file: fakeElement('div'), link: fakeElement('div') },
    card: Object.assign(fakeElement('div'), { hidden: true }),
  };
  return parts;
}

function mount(parts, clock, reader, extra = {}) {
  const { canvas, caption, note, callouts, card } = parts;
  return mountCloud(
    canvas,
    { link: LINK, aspect: 1422 / 1231, caption, note, callouts, card, strings, reducedMotion: true, ...extra },
    { ...clock.env, createReader: () => reader },
  );
}

function readerThat(read) {
  const reader = { closed: 0, read, close() { reader.closed += 1; } };
  return reader;
}

function verifiedRead({ manifest = MANIFEST, hold = null } = {}) {
  const count = Math.ceil(manifest.size / manifest.chunk);
  return readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(manifest);
    for (let index = 0; index < count; index += 1) hooks.onChunk(chunk(index));
    if (hold) await hold;
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: count - 1 });
    return { manifest, writer: 'rWriter', blocks: [], bytes: new Uint8Array(manifest.size) };
  });
}

const isLime = (data) => {
  for (let i = 0; i < data.length; i += 4) if (data[i] - data[i + 2] > 60) return true;
  return false;
};

test('nothing is claimed while the read has not started to answer: no card, no caption, no callouts', () => {
  const parts = page();
  const clock = fakeEnv();
  const cloud = mount(parts, clock, readerThat(() => new Promise(() => {})));
  assert.equal(parts.card.hidden, true);
  assert.equal(parts.caption.children.length, 0);
  assert.equal(parts.callouts.file.children.length, 0);
  assert.equal(parts.callouts.link.children.length, 0);
  assert.equal(parts.canvas.attrs.role, 'img');
  assert.equal(parts.canvas.attrs['aria-label'], 'label:reading:null');
  assert.equal(parts.caption.attrs['aria-live'], 'polite');
  assert.equal(parts.caption.attrs.role, 'status');
  assert.equal(parts.note.textContent, 'drawn note');
  cloud.destroy();
});

test('the canvas follows devicePixelRatio and draws the ground before any answer', () => {
  const parts = page();
  const clock = fakeEnv();
  mount(parts, clock, readerThat(() => new Promise(() => {})));
  assert.equal(parts.canvas.width, 800);
  assert.equal(parts.canvas.height, 600);
  clock.flushFrames();
  assert.equal(parts.log.putImageData, 1);
});

test('the manifest fills both callouts from the manifest and from the link itself', async () => {
  const parts = page();
  const clock = fakeEnv();
  const hold = new Promise(() => {});
  const reader = verifiedRead({ hold });
  mount(parts, clock, reader);
  await tick();
  const file = parts.callouts.file;
  assert.equal(textOf(file), 'file123 chunks');
  assert.equal(textOf(parts.callouts.link), `link${LINK.length} bytes`);
  assert.equal(LINK.length, 84);
  assert.equal(parts.canvas.attrs['aria-label'], 'label:reading:123');
  assert.equal(parts.card.hidden, true, 'chunks are not a verified file');
  assert.equal(parts.caption.children.length, 0);
});

test('no cube is lime until a block is verified, and the card waits for the whole read', async () => {
  const parts = page();
  const clock = fakeEnv();
  let release;
  const hold = new Promise((resolve) => { release = resolve; });
  mount(parts, clock, verifiedRead({ hold }));
  await tick();
  clock.flushFrames();
  assert.ok(parts.log.putImageData >= 1);
  assert.equal(isLime(parts.log.last.data), false, 'chunks read but unverified are not lime');

  release();
  await tick();
  clock.flushFrames();
  assert.equal(isLime(parts.log.last.data), true, 'the verified block is lime');
  assert.equal(parts.card.hidden, true, 'still waiting for the settling time');
});

test('a verified read shows the card with the file, "verified" and the link to its page, once', async () => {
  const parts = page();
  const clock = fakeEnv();
  const cloud = mount(parts, clock, verifiedRead());
  await tick();
  assert.equal(cloud.phase, 'developing');
  assert.equal(parts.card.hidden, true);
  assert.equal(clock.loaded.length, 0, 'the element script is not fetched before it is needed');

  clock.firePending();
  await cloud.done;
  assert.equal(cloud.phase, 'done');
  assert.equal(parts.card.hidden, false);
  const [element, line] = parts.card.children;
  assert.equal(element.tag, 'xrpl-file');
  assert.equal(element.attrs.src, LINK);
  assert.equal(element.attrs.caption, 'off');
  assert.equal(element.attrs.theme, 'dark', 'the element follows the site theme');
  assert.equal(element.style.aspectRatio, String(1422 / 1231));
  assert.equal(textOf(line.children[0]), 'verified');
  assert.equal(line.children[0].children[0].attrs.class, 'hero-card__check', 'the check mark is a drawing, not a glyph');
  assert.equal(line.children[1].attrs.href, `/testnet/${HASH}`);
  assert.equal(line.children[1].textContent, 'file page');
  assert.equal(clock.loaded.length, 1);
  assert.equal(textOf(parts.caption), 'caption:123:100-107');
  assert.equal(parts.caption.dataset.phase, 'done');
  assert.equal(parts.canvas.attrs['aria-label'], 'label:done:123');
  assert.equal(parts.callouts.file.attrs['data-state'], 'verified');
});

test('the card takes the light theme when the site is light', async () => {
  const parts = page();
  parts.document.documentElement.dataset.theme = 'light';
  const clock = fakeEnv();
  const cloud = mount(parts, clock, verifiedRead());
  await tick();
  clock.firePending();
  await cloud.done;
  assert.equal(parts.card.children[0].attrs.theme, 'light');
});

test('a stalled read settles into the quiet failure state within 20 s and keeps the cubes read so far', async () => {
  const parts = page();
  const clock = fakeEnv();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    hooks.onChunk(chunk(0));
    hooks.onChunk(chunk(1));
    return new Promise(() => {});
  });
  const cloud = mount(parts, clock, reader);
  await tick();
  clock.advance(19_999);
  clock.pollOnce();
  assert.equal(cloud.phase, 'reading');
  clock.advance(1);
  clock.pollOnce();
  await cloud.done;
  assert.equal(cloud.phase, 'failed');
  assert.equal(textOf(parts.caption), 'failed:Stalled');
  assert.equal(parts.caption.dataset.phase, 'failed');
  assert.equal(parts.canvas.attrs['aria-label'], 'label:failed:123');
  assert.equal(parts.card.hidden, true, 'no verified file to show');
  assert.equal(reader.closed, 1);
  assert.equal(clock.activePolls(), 0);
  assert.doesNotThrow(() => clock.flushFrames(), 'the frame after a failure still draws');
});

test('a rejected read shows the message for its code, and a network without a node fails at once', async () => {
  const parts = page();
  const clock = fakeEnv();
  const cloud = mount(parts, clock, readerThat(async () => { throw new XrplFileError('NodeLacksHistory', 'no history'); }));
  await cloud.done;
  assert.equal(textOf(parts.caption), 'failed:NodeLacksHistory');

  const second = page();
  const other = mountCloud(second.canvas, { link: `xrpl-file://21338/${HASH}`, caption: second.caption, strings, reducedMotion: true }, fakeEnv().env);
  await other.done;
  assert.equal(textOf(second.caption), 'failed:UnsupportedNetwork');
});

test('reduced motion draws one still frame per change and then stops asking for frames', async () => {
  const parts = page();
  const clock = fakeEnv();
  const cloud = mount(parts, clock, verifiedRead());
  await tick();
  clock.flushFrames();
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 0, 'no animation loop');
  clock.firePending();
  await cloud.done;
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 0);
  assert.equal(parts.card.hidden, false, 'the card appears when verified');
});

test('with motion the scene keeps swaying, and destroy stops it and releases the read', async () => {
  const parts = page();
  const clock = fakeEnv();
  const reader = readerThat(() => new Promise(() => {}));
  const cloud = mount(parts, clock, reader, { reducedMotion: false, fast: false });
  clock.advance(100);
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 1, 'the sway asks for the next frame');
  cloud.destroy();
  await cloud.done;
  assert.equal(reader.closed, 1);
  assert.equal(clock.activePolls(), 0);
});

test('a theme switch re-reads the colours', () => {
  const observers = [];
  const saved = globalThis.MutationObserver;
  globalThis.MutationObserver = class {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() {}
    disconnect() {}
  };
  try {
    const parts = page();
    const clock = fakeEnv();
    mount(parts, clock, readerThat(() => new Promise(() => {})));
    const before = parts.log.styleReads;
    assert.ok(before > 0);
    observers[0].callback();
    assert.ok(parts.log.styleReads > before);
  } finally {
    globalThis.MutationObserver = saved;
  }
});
