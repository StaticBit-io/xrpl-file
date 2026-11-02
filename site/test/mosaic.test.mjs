import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { mountMosaic, hasSeenMosaic, markMosaicSeen, SEEN_KEY } = await import('../src/js/mosaic.mjs');
const { XrplFileError } = await import('../src/js/xrpl.mjs');

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const LINK = `xrpl-file://testnet/${HASH}`;

function fakeElement(tag) {
  return {
    tag,
    children: [],
    attrs: {},
    dataset: {},
    style: {},
    className: '',
    textContent: '',
    setAttribute(name, value) { this.attrs[name] = String(value); },
    append(...nodes) { this.children.push(...nodes); },
    replaceChildren(...nodes) { this.children = nodes; },
  };
}

function fakeCanvas() {
  const calls = [];
  const context = new Proxy({}, {
    get: (target, name) => (name in target ? target[name] : (...args) => calls.push([name, ...args])),
    set: (target, name, value) => { target[name] = value; return true; },
  });
  const document = {
    documentElement: fakeElement('html'),
    createElement: fakeElement,
    createElementNS: (_, tag) => fakeElement(tag),
    defaultView: {
      devicePixelRatio: 2,
      getComputedStyle: () => ({ getPropertyValue: () => '' }),
      matchMedia: () => ({ matches: false }),
    },
  };
  const canvas = Object.assign(fakeElement('canvas'), {
    ownerDocument: document,
    clientWidth: 130,
    clientHeight: 140,
    width: 0,
    height: 0,
    getContext: () => context,
  });
  return { canvas, calls };
}

const strings = {
  label: (phase, { chunks }) => `label:${phase}:${chunks}`,
  caption: ({ chunks, firstLedger, lastLedger }) => `caption:${chunks}:${firstLedger}-${lastLedger}`,
  failed: (code) => `failed:${code}`,
  fileLinkLabel: 'file page',
};

/** A clock, timers and frame queue the test steps by hand. */
function fakeEnv(overrides = {}) {
  let time = 1000;
  const polls = [];
  const timers = [];
  const frames = [];
  const stored = new Map();
  const env = {
    now: () => time,
    setPoll: (callback) => polls.push(callback) && polls.length,
    clearPoll: (id) => { polls[id - 1] = null; },
    setTimer: (callback) => timers.push(callback) && timers.length,
    clearTimer: (id) => { timers[id - 1] = null; },
    requestFrame: (callback) => frames.push(callback),
    cancelFrame: () => {},
    storage: { getItem: (key) => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value) },
    ...overrides,
  };
  return {
    env,
    stored,
    advance(ms) { time += ms; },
    pollOnce() { for (const poll of polls) poll?.(); },
    firePending() { for (const timer of timers.splice(0)) timer?.(); },
    flushFrames() { for (const frame of frames.splice(0)) frame(); },
    activePolls: () => polls.filter(Boolean).length,
  };
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

function readerThat(read) {
  const reader = { closed: 0, read, close() { reader.closed += 1; } };
  return reader;
}

const MANIFEST = { size: 2500, chunk: 1000, type: 'image/webp', firstLedger: 10, lastLedger: 20 };

test('a node that never answers still ends in the failure state within 20 s of mounting', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const reader = readerThat(() => new Promise(() => {}));
  const mosaic = mountMosaic(canvas, { link: LINK, aspect: 1, caption, strings, reducedMotion: true },
    { ...clock.env, createReader: () => reader });

  clock.advance(19_999);
  clock.pollOnce();
  assert.equal(mosaic.phase, 'reading');
  assert.equal(caption.children.length, 0, 'nothing is said while reading');

  clock.advance(1);
  clock.pollOnce();
  await mosaic.done;
  assert.equal(mosaic.phase, 'failed');
  assert.equal(caption.dataset.phase, 'failed');
  assert.equal(canvas.attrs['aria-label'], 'label:failed:null');
  assert.equal(caption.children[0].children[1].textContent, 'failed:Stalled');
  assert.equal(reader.closed, 1, 'the connection is abandoned');
  assert.equal(clock.activePolls(), 0, 'the stall clock is stopped');
});

test('a read that rejects keeps the cells it got and shows the quiet message for its code', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    hooks.onChunk({ index: 0, bytes: new Uint8Array(1000).fill(7) });
    hooks.onChunk({ index: 1, bytes: new Uint8Array(1000).fill(9) });
    throw new XrplFileError('NodeLacksHistory', 'the node does not have this ledger');
  });
  const mosaic = mountMosaic(canvas, { link: LINK, aspect: 1, caption, strings, reducedMotion: true },
    { ...clock.env, createReader: () => reader });

  await mosaic.done;
  assert.equal(mosaic.phase, 'failed');
  assert.equal(caption.children[0].children[1].textContent, 'failed:NodeLacksHistory');
  assert.equal(canvas.attrs['aria-label'], 'label:failed:3');
  assert.equal(reader.closed, 1);
  assert.equal(caption.attrs['aria-live'], 'polite');
  assert.equal(caption.attrs.role, 'status');
  assert.equal(clock.stored.has(SEEN_KEY), false, 'a failed run is not remembered as seen');
  // The grid is redrawn with the cells that did arrive.
  clock.flushFrames();
});

test('a network with no public node fails at once with its own code', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const mosaic = mountMosaic(canvas, { link: `xrpl-file://21338/${HASH}`, aspect: 1, caption, strings }, clock.env);
  await mosaic.done;
  assert.equal(caption.children[0].children[1].textContent, 'failed:UnsupportedNetwork');
});

test('text that is not a link fails instead of throwing', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const mosaic = mountMosaic(canvas, { link: 'https://example.com', aspect: 1, caption, strings }, clock.env);
  await mosaic.done;
  assert.equal(caption.children[0].children[1].textContent, 'failed:InvalidLink');
});

test('a verified read develops: the picture is decoded from the verified bytes and the caption links to the file page', async () => {
  const { canvas, calls } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const decoded = [];
  const bitmap = { width: 30, height: 30 };
  const bytes = new Uint8Array(2500).fill(3);
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    for (let index = 0; index < 3; index += 1) hooks.onChunk({ index, bytes: bytes.subarray(index * 1000, index * 1000 + 1000) });
    assert.equal(caption.children.length, 0, 'nothing is claimed before verification');
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: 2 });
    return { manifest: MANIFEST, writer: 'rWriter', blocks: [], bytes };
  });
  const mosaic = mountMosaic(canvas, { link: ` ${LINK.toLowerCase().replace('xrpl-file://testnet/', 'XRPL-FILE://TestNet/')}\n`, aspect: 1, caption, strings, reducedMotion: true },
    {
      ...clock.env,
      createReader: () => reader,
      decode: async (data, type) => { decoded.push({ data, type }); return bitmap; },
    });

  await tick();
  assert.equal(mosaic.phase, 'developing');
  assert.equal(canvas.attrs['aria-label'], 'label:developing:3');
  assert.equal(caption.children.length, 0, 'the caption waits for the picture');
  assert.deepEqual(decoded.map((d) => d.type), ['image/webp']);
  assert.equal(decoded[0].data, bytes);

  clock.firePending();
  await mosaic.done;
  assert.equal(mosaic.phase, 'done');
  assert.equal(reader.closed >= 1, true);
  assert.equal(canvas.attrs['aria-label'], 'label:done:3');
  assert.equal(clock.stored.get(SEEN_KEY), '1');

  const [status, link] = caption.children;
  assert.equal(status.children[0].attrs.class, 'mosaic-caption__check');
  assert.equal(status.children[1].textContent, 'caption:3:10-20');
  assert.equal(link.attrs.href, `/testnet/${HASH}`);
  assert.equal(link.textContent, 'file page');

  clock.flushFrames();
  assert.ok(calls.some(([name, image]) => name === 'drawImage' && image === bitmap), 'the whole picture is drawn at the end');
  assert.equal(canvas.width, 260, 'the backing store follows devicePixelRatio');
  assert.equal(canvas.height, 280);
  assert.equal(canvas.style.aspectRatio, '1');
});

test('a picture that cannot be decoded ends in the failure state, not in a half-turned mosaic', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    for (let index = 0; index < 3; index += 1) hooks.onChunk({ index, bytes: new Uint8Array(10) });
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: 2 });
    return { manifest: MANIFEST, writer: 'rWriter', blocks: [], bytes: new Uint8Array(2500) };
  });
  const mosaic = mountMosaic(canvas, { link: LINK, aspect: 1, caption, strings, reducedMotion: true },
    { ...clock.env, createReader: () => reader, decode: async () => { throw new Error('unsupported image'); } });
  await mosaic.done;
  assert.equal(mosaic.phase, 'failed');
  assert.equal(caption.children[0].children[1].textContent, 'failed:DecodeFailed');
});

test('a read that resolves with unverified blocks is not shown as verified', async () => {
  const { canvas } = fakeCanvas();
  const caption = fakeElement('p');
  const clock = fakeEnv();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    hooks.onChunk({ index: 0, bytes: new Uint8Array(10) });
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: 0 });
    return { manifest: MANIFEST, writer: 'rWriter', blocks: [], bytes: new Uint8Array(2500) };
  });
  const mosaic = mountMosaic(canvas, { link: LINK, aspect: 1, caption, strings, reducedMotion: true }, { ...clock.env, createReader: () => reader });
  await mosaic.done;
  assert.equal(mosaic.phase, 'failed');
  assert.equal(caption.children[0].children[1].textContent, 'failed:Incomplete');
});

test('destroy abandons the read and settles done', async () => {
  const { canvas } = fakeCanvas();
  const clock = fakeEnv();
  const reader = readerThat(() => new Promise(() => {}));
  const mosaic = mountMosaic(canvas, { link: LINK, aspect: 1, strings }, { ...clock.env, createReader: () => reader });
  mosaic.destroy();
  await mosaic.done;
  assert.equal(reader.closed, 1);
  assert.equal(clock.activePolls(), 0);
});

test('the seen flag survives storage that throws and blocked storage', () => {
  const throwing = {
    getItem() { throw new Error('blocked'); },
    setItem() { throw new Error('blocked'); },
  };
  assert.equal(hasSeenMosaic(throwing), false);
  assert.doesNotThrow(() => markMosaicSeen(throwing));
  assert.equal(hasSeenMosaic(null), false);
  assert.doesNotThrow(() => markMosaicSeen(null));

  const map = new Map();
  const storage = { getItem: (key) => map.get(key) ?? null, setItem: (key, value) => map.set(key, value) };
  assert.equal(hasSeenMosaic(storage), false);
  markMosaicSeen(storage);
  assert.equal(map.get('xf-mosaic-seen'), '1');
  assert.equal(hasSeenMosaic(storage), true);
});
