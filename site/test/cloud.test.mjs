import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { mountCloud, TURN_MS, TURN_FADE_MS, turnOffset } = await import('../src/js/cloud.mjs');
const { XrplFileError } = await import('../src/js/xrpl.mjs');
const { startHeroRead } = await import('../src/js/hero-read.mjs');

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
  note: ({ ledgers, ledgersPerPad, capped }) => `note:${ledgers}:${ledgersPerPad}:${capped}`,
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
// 16 chunks on each of the 8 ledgers, the last one a few short.
const ledgerOf = (index) => MANIFEST.firstLedger + Math.min(7, Math.floor(index / 16));
const chunk = (index, ledger = ledgerOf(index)) => ({ index, ledger, bytes: Uint8Array.from({ length: 1000 }, (_, i) => (i * 7 + index * 13) % 256) });

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
    { ...clock.env, startRead: startHeroRead, createReader: () => reader },
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
  assert.equal(parts.note.textContent, '', 'no cube exists yet, so nothing is said about cubes');
  assert.equal(parts.note.hidden, true, 'an empty note takes no room');
  cloud.destroy();
});

test('the canvas follows devicePixelRatio and draws the ground before any answer', () => {
  const parts = page();
  const clock = fakeEnv();
  mount(parts, clock, readerThat(() => new Promise(() => {})));
  assert.equal(parts.canvas.width, 600, 'the buffer is capped at 1.5 device pixels per CSS pixel on a ratio-2 screen');
  assert.equal(parts.canvas.height, 450);
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
  assert.equal(element.attrs.background, 'transparent', 'the transparent logo sits on the card');
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
  assert.equal(parts.note.textContent, 'note:8:1:false', 'the cubes read so far stay, and so does what the note says about them');
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
  assert.equal(parts.note.textContent, '', 'a read that failed before any cube has no note about cubes');
  assert.equal(parts.note.hidden, true);

  const second = page();
  const other = mountCloud(second.canvas, { link: `xrpl-file://21338/${HASH}`, caption: second.caption, strings, reducedMotion: true }, { ...fakeEnv().env, startRead: startHeroRead });
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

function lastPixels(parts) { return Uint8ClampedArray.from(parts.log.last.data); }

/** Mean x of the pixels that differ from the same scene with no chunk read. */
async function changedCentre(ledger) {
  const draw = async (withChunk) => {
    const parts = page();
    const clock = fakeEnv();
    mount(parts, clock, readerThat(async (_link, _progress, hooks) => {
      hooks.onManifest(MANIFEST);
      if (withChunk) hooks.onChunk(chunk(0, ledger));
      return new Promise(() => {});
    }));
    await tick();
    clock.flushFrames();
    return { data: lastPixels(parts), width: parts.canvas.width };
  };
  const base = await draw(false);
  const drawn = await draw(true);
  let sum = 0;
  let count = 0;
  for (let i = 0; i < drawn.data.length; i += 4) {
    if (drawn.data[i] !== base.data[i] || drawn.data[i + 1] !== base.data[i + 1] || drawn.data[i + 2] !== base.data[i + 2]) {
      sum += (i / 4) % drawn.width;
      count += 1;
    }
  }
  return { x: sum / count, count };
}

test('a cube is drawn on the ledger its chunk reports: the same chunk on a later ledger stands further right', async () => {
  const first = await changedCentre(MANIFEST.firstLedger);
  const last = await changedCentre(MANIFEST.lastLedger);
  assert.ok(first.count > 0 && last.count > 0, 'the chunk is drawn');
  assert.ok(last.x - first.x > 40, `first ledger at x ${first.x.toFixed(0)}, last ledger at x ${last.x.toFixed(0)}`);
});

test('a chunk with no ledger is not drawn', async () => {
  const none = await changedCentre(null);
  assert.equal(none.count, 0);
});

test('the note is written again when the manifest names the ledger range, and says when pads group ledgers', async () => {
  const exact = page();
  mount(exact, fakeEnv(), verifiedRead({ hold: new Promise(() => {}) }));
  await tick();
  assert.equal(exact.note.textContent, 'note:8:1:false');
  assert.equal(exact.note.hidden, false);

  const wide = page();
  mount(wide, fakeEnv(), readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest({ ...MANIFEST, firstLedger: 1000, lastLedger: 1339 });
    return new Promise(() => {});
  }));
  await tick();
  assert.equal(wide.note.textContent, 'note:340:34:true');
});

test('the sway ends with the read: after done no frame is requested, and a failure stops it too', async () => {
  const parts = page();
  const clock = fakeEnv();
  const cloud = mount(parts, clock, verifiedRead(), { reducedMotion: false, fast: true });
  await tick();
  clock.advance(50);
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 1, 'swaying while the read runs');
  clock.firePending();
  await cloud.done;
  assert.equal(cloud.phase, 'done');
  clock.advance(5000);
  clock.flushFrames();
  clock.advance(100);
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 0, 'the animation has stopped');

  const failing = page();
  const failClock = fakeEnv();
  const failed = mount(failing, failClock, readerThat(() => new Promise(() => {})), { reducedMotion: false, fast: false });
  failClock.advance(100);
  failClock.flushFrames();
  failClock.advance(20_000);
  failClock.pollOnce();
  await failed.done;
  failClock.advance(100);
  failClock.flushFrames();
  failClock.advance(100);
  failClock.flushFrames();
  assert.equal(failClock.pendingFrames(), 0);
});

test('the visibility observer follows its last entry', async () => {
  const observers = [];
  const saved = globalThis.IntersectionObserver;
  globalThis.IntersectionObserver = class {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() {}
    disconnect() {}
  };
  try {
    const parts = page();
    const clock = fakeEnv();
    mount(parts, clock, readerThat(() => new Promise(() => {})), { reducedMotion: false, fast: false });
    clock.advance(100);
    clock.flushFrames();
    observers[0].callback([{ isIntersecting: true }, { isIntersecting: false }]);
    clock.advance(100);
    clock.flushFrames();
    assert.equal(clock.pendingFrames(), 0, 'it left the screen: the last entry says so');
    observers[0].callback([{ isIntersecting: false }, { isIntersecting: true }]);
    assert.equal(clock.pendingFrames(), 1, 'it came back: drawing resumes');
  } finally {
    globalThis.IntersectionObserver = saved;
  }
});

// A verified read with motion, run to done; `offScreenAtDone` takes the cloud off screen just before.
async function turnRun({ offScreenAtDone = false, reducedMotion = false, extra = {} } = {}) {
  const observers = [];
  const saved = globalThis.IntersectionObserver;
  globalThis.IntersectionObserver = class {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() {}
    disconnect() {}
  };
  try {
    const parts = page();
    const listeners = {};
    parts.canvas.parentElement = { addEventListener: (type, listener) => { listeners[type] = listener; }, removeEventListener() {} };
    const clock = fakeEnv();
    const cloud = mount(parts, clock, verifiedRead(), { reducedMotion, fast: true, ...extra });
    await tick();
    clock.advance(50);
    clock.flushFrames();
    if (offScreenAtDone) observers[0].callback([{ isIntersecting: false }]);
    clock.firePending();
    await cloud.done;
    assert.equal(cloud.phase, 'done');
    return { parts, clock, listeners, observer: observers[0] };
  } finally {
    globalThis.IntersectionObserver = saved;
  }
}

/** Steps the clock in frame-sized steps, flushing frames, for `ms`. */
function play(clock, ms) {
  for (let elapsed = 0; elapsed < ms; elapsed += 50) {
    clock.advance(50);
    clock.flushFrames();
  }
}

const lastImage = (parts) => Buffer.from(parts.log.last.data);

test('the turn is one eased round: it adds nothing at its start and a whole turn at its end, within 5 s', () => {
  assert.ok(TURN_MS <= 5000, 'WCAG 2.2.2: longer motion that starts on its own needs a pause control');
  assert.equal(turnOffset(0), 0);
  assert.equal(turnOffset(TURN_MS), 2 * Math.PI);
  assert.equal(turnOffset(TURN_MS * 2), 2 * Math.PI);
  assert.ok(turnOffset(10) < 1e-4, 'it starts from rest, so the sway does not jump');
  assert.ok(2 * Math.PI - turnOffset(TURN_MS - 10) < 1e-4, 'it comes to rest at the end');
  for (let t = 0; t < TURN_MS; t += 100) assert.ok(turnOffset(t + 100) >= turnOffset(t));
});

test('a verified read turns the scene once and ends on the frame it would have stopped on without the turn', async () => {
  const turned = await turnRun();
  const skipped = await turnRun({ offScreenAtDone: true });

  play(turned.clock, TURN_MS / 2);
  play(skipped.clock, TURN_MS / 2);
  skipped.observer.callback([{ isIntersecting: true }]);
  skipped.clock.flushFrames();
  assert.ok(!lastImage(turned.parts).equals(lastImage(skipped.parts)), 'half way the scene is turned away');
  assert.equal(turned.parts.callouts.file.style.opacity, '0', 'the figures are faded out while it turns');

  play(turned.clock, TURN_MS / 2 + 500);
  play(skipped.clock, TURN_MS / 2 + 500);
  skipped.observer.callback([{ isIntersecting: true }]);
  skipped.clock.flushFrames();
  assert.ok(lastImage(turned.parts).equals(lastImage(skipped.parts)), 'the last frame is the resting angle');
  assert.equal(turned.parts.callouts.file.style.opacity, '', 'the figures are back');
  assert.equal(turned.clock.pendingFrames(), 0, 'no frame is asked for after the turn');
});

test('the figures stay with their cubes while they fade: half faded at the middle of the fade', async () => {
  const run = await turnRun();
  play(run.clock, TURN_FADE_MS / 2);
  const opacity = Number(run.parts.callouts.file.style.opacity);
  assert.ok(opacity > 0.3 && opacity < 0.7, String(opacity));
  assert.ok(run.parts.log.strokes > 0, 'the leaders are still drawn');
});

test('a failed read does not turn: the sway stops where it is', async () => {
  const parts = page();
  const clock = fakeEnv();
  const failed = mount(parts, clock, readerThat(() => new Promise(() => {})), { reducedMotion: false, fast: true });
  clock.advance(100);
  clock.flushFrames();
  clock.advance(20_000);
  clock.pollOnce();
  await failed.done;
  play(clock, 200);
  assert.equal(clock.pendingFrames(), 0, 'nothing turns after a failure');
});

test('with reduced motion there is no turn', async () => {
  const run = await turnRun({ reducedMotion: true });
  run.clock.flushFrames();
  run.clock.advance(100);
  run.clock.flushFrames();
  assert.equal(run.clock.pendingFrames(), 0);
});

test('off screen when the read ends, the scene goes straight to its last frame', async () => {
  const run = await turnRun({ offScreenAtDone: true });
  play(run.clock, 1000);
  run.observer.callback([{ isIntersecting: true }]);
  run.clock.flushFrames();
  run.clock.advance(50);
  run.clock.flushFrames();
  assert.equal(run.clock.pendingFrames(), 0, 'back on screen a second later, nothing is turning');
});

test('leaving the screen, a pointer or a key on the cloud ends the turn on its last frame', async () => {
  for (const interrupt of ['offscreen', 'pointerdown', 'keydown']) {
    const run = await turnRun();
    play(run.clock, 1000);
    assert.equal(run.clock.pendingFrames(), 1, `${interrupt}: turning`);
    if (interrupt === 'offscreen') {
      run.observer.callback([{ isIntersecting: false }]);
      run.observer.callback([{ isIntersecting: true }]);
    } else {
      run.listeners[interrupt]();
    }
    play(run.clock, 100);
    assert.equal(run.clock.pendingFrames(), 0, `${interrupt}: the turn is over`);
    assert.equal(run.parts.callouts.file.style.opacity, '', `${interrupt}: the figures are back`);
  }
});

test('the cloud imports neither the hero reader nor the reader door, so a page can bring its own reader', async () => {
  const { readFileSync } = await import('node:fs');
  const source = readFileSync(new URL('../src/js/cloud.mjs', import.meta.url), 'utf8');
  const specifiers = [...source.matchAll(/^import\s[^;]*?from\s+'([^']+)'/gms)].map((match) => match[1]);
  assert.ok(specifiers.length > 0, 'the pattern finds the imports');
  for (const specifier of specifiers) assert.ok(!/hero-read\.mjs|xrpl\.mjs/.test(specifier), specifier);
});

test('a read is required: the cloud does not pick one for itself', () => {
  const parts = page();
  assert.throws(() => mountCloud(parts.canvas, { link: LINK, strings }, fakeEnv().env), /startRead/);
});

test('turn: false never turns after done: the figures stay and no frame is asked for', async () => {
  const run = await turnRun({ extra: { turn: false } });
  for (let elapsed = 0; elapsed < TURN_MS; elapsed += 50) {
    run.clock.advance(50);
    run.clock.flushFrames();
    assert.equal(run.parts.callouts.file.style.opacity ?? '', '', 'the figures never fade for a turn');
  }
  assert.equal(run.clock.pendingFrames(), 0);
});

test('swayFor stops the sway at mount + swayFor, before the read ends', () => {
  const parts = page();
  const clock = fakeEnv();
  mount(parts, clock, readerThat(() => new Promise(() => {})), { reducedMotion: false, fast: false, swayFor: 5000 });
  clock.advance(4000);
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 1, 'still swaying inside the window');
  clock.advance(1100);
  clock.flushFrames();
  assert.equal(clock.pendingFrames(), 0, 'past mount + 5000 ms the read is idle and no frame is asked for');
});

// A read the test drives by hand, to feed the cloud what a reader adapter other than the hero's would.
function drivenRead() {
  const driven = { listener: null, destroyed: 0 };
  driven.startRead = (options) => {
    driven.listener = options.listener;
    return { done: new Promise(() => {}), destroy() { driven.destroyed += 1; }, get phase() { return 'reading'; } };
  };
  return driven;
}

function mountDriven(extraStrings = {}, extra = {}) {
  const parts = page();
  const clock = fakeEnv();
  const driven = drivenRead();
  const cloud = mountCloud(
    parts.canvas,
    { link: LINK, caption: parts.caption, note: parts.note, callouts: parts.callouts, card: parts.card, strings: { ...strings, ...extraStrings }, reducedMotion: true, ...extra },
    { ...clock.env, startRead: driven.startRead },
  );
  const manifest = (detail = {}) => driven.listener.onManifest({ manifest: MANIFEST, chunks: 30, time: clock.env.now(), ...detail });
  return { parts, clock, driven, cloud, manifest };
}

test('reading() fills the caption at the manifest and gives way when the phase changes', () => {
  const reading = ({ chunks, perCube, firstLedger, lastLedger }) => `reading:${chunks}:${perCube}:${firstLedger}-${lastLedger}`;
  const { parts, driven, manifest } = mountDriven({ reading });
  assert.equal(parts.caption.children.length, 0, 'nothing before the manifest');
  manifest({ fileChunks: 123, perCube: 4 });
  assert.equal(textOf(parts.caption), 'reading:123:4:100-107');
  driven.listener.onPhase('developing');
  assert.equal(textOf(parts.caption), '', 'the reading line is gone once the phase has changed');
});

test('without reading() the caption stays empty while reading, as the hero does', () => {
  const { parts, manifest } = mountDriven();
  manifest();
  assert.equal(parts.caption.children.length, 0);
});

test('perCube and fileChunks reach the note, the label and the file figure', () => {
  const { parts, manifest } = mountDriven({
    label: (phase, { chunks, perCube }) => `label:${phase}:${chunks}:${perCube}`,
    note: ({ ledgers, ledgersPerPad, capped, perCube }) => `note:${ledgers}:${ledgersPerPad}:${capped}:${perCube}`,
  });
  assert.equal(parts.canvas.attrs['aria-label'], 'label:reading:null:1');
  manifest({ fileChunks: 123, perCube: 4 });
  assert.equal(parts.canvas.attrs['aria-label'], 'label:reading:123:4');
  assert.equal(parts.note.textContent, 'note:8:1:false:4');
  assert.equal(textOf(parts.callouts.file), 'file123 chunks');
});

test('fileChunks and perCube default to chunks and 1: the hero reports neither', () => {
  const { parts, manifest } = mountDriven({
    label: (phase, { chunks, perCube }) => `label:${phase}:${chunks}:${perCube}`,
    note: ({ perCube }) => `note:${perCube}`,
  });
  manifest();
  assert.equal(parts.canvas.attrs['aria-label'], 'label:reading:30:1');
  assert.equal(parts.note.textContent, 'note:1');
});

test('setStrings rewrites label, caption and note at once', () => {
  const { parts, driven, manifest, cloud } = mountDriven();
  manifest({ fileChunks: 123, perCube: 2 });
  driven.listener.onPhase('done');
  driven.listener.onDone({ time: 2000 });
  assert.equal(textOf(parts.caption), 'caption:123:100-107');
  assert.equal(parts.canvas.attrs['aria-label'], 'label:done:123');
  assert.equal(parts.note.textContent, 'note:8:1:false');

  cloud.setStrings({
    ...strings,
    label: (phase, { chunks, perCube }) => `etiquette:${phase}:${chunks}:${perCube}`,
    caption: ({ chunks }) => `legende:${chunks}`,
    note: ({ perCube }) => `remarque:${perCube}`,
  });
  assert.equal(parts.canvas.attrs['aria-label'], 'etiquette:done:123:2');
  assert.equal(textOf(parts.caption), 'legende:123');
  assert.equal(parts.note.textContent, 'remarque:2');
  assert.equal(parts.note.hidden, false);
  assert.equal(parts.caption.dataset.phase, 'done');
});

test('setStrings re-renders the reading line and the failure message in the new language', () => {
  const { parts, driven, manifest, cloud } = mountDriven({ reading: ({ chunks }) => `reading:${chunks}` });
  manifest({ fileChunks: 7 });
  cloud.setStrings({ ...strings, reading: ({ chunks }) => `lecture:${chunks}` });
  assert.equal(textOf(parts.caption), 'lecture:7');

  driven.listener.onPhase('failed');
  driven.listener.onFailed({ error: new XrplFileError('Stalled', 'stalled') });
  assert.equal(textOf(parts.caption), 'failed:Stalled');
  cloud.setStrings({ ...strings, failed: (code) => `echec:${code}` });
  assert.equal(textOf(parts.caption), 'echec:Stalled');
});

test('setStrings before the manifest writes the label and leaves the note and caption empty', () => {
  const { parts, cloud } = mountDriven();
  cloud.setStrings({ ...strings, label: (phase) => `etiquette:${phase}` });
  assert.equal(parts.canvas.attrs['aria-label'], 'etiquette:reading');
  assert.equal(parts.note.textContent, '');
  assert.equal(parts.note.hidden, true);
  assert.equal(parts.caption.children.length, 0);
});

// The element-driven read: a plain EventTarget stands in for <xrpl-file>.
const { startElementRead } = await import('../src/js/element-read.mjs');

function mountOnElement() {
  const parts = page();
  const clock = fakeEnv();
  const element = new EventTarget();
  const cloud = mountCloud(
    parts.canvas,
    { link: LINK, caption: parts.caption, note: parts.note, callouts: parts.callouts, card: parts.card, strings, reducedMotion: true },
    { ...clock.env, startRead: startElementRead, element },
  );
  const send = (type, detail) => element.dispatchEvent(
    detail === undefined ? new Event(type) : new CustomEvent(type, { detail }),
  );
  return { parts, clock, cloud, send };
}

test('an empty file (a manifest with no chunks, then load) ends in done without throwing and leaves the caption empty', async () => {
  const { parts, clock, cloud, send } = mountOnElement();
  assert.doesNotThrow(() => {
    send('xrpl-file-manifest', { size: 0, chunk: 1000, chunks: 0, blocks: 0, firstLedger: 100, lastLedger: 107 });
    send('load');
  });
  await cloud.done;
  assert.equal(cloud.phase, 'done');
  assert.equal(parts.caption.children.length, 0, 'no manifest was reported, so there is nothing to caption');
  assert.doesNotThrow(() => clock.flushFrames());
});

test('an element error before the manifest still writes the failure line', async () => {
  const { parts, cloud, send } = mountOnElement();
  send('error', { name: 'NotFound', message: 'x' });
  await cloud.done;
  assert.equal(cloud.phase, 'failed');
  assert.equal(textOf(parts.caption), 'failed:NotFound');
});

test('settleDelay is 0 before the manifest, counts down the sweep, the colour change and the pause, and is 0 after destroy', () => {
  const { clock, driven, manifest, cloud } = mountDriven({}, { reducedMotion: false, fast: false });
  assert.equal(cloud.settleDelay(), 0, 'nothing has been drawn yet');
  manifest();
  assert.equal(cloud.settleDelay(), 0, 'cubes that are not verified have nothing to settle');
  for (let index = 0; index < 30; index += 1) driven.listener.onChunk({ ...chunk(index), time: clock.env.now() });
  driven.listener.onBlockVerified({ firstChunk: 0, lastChunk: 29, time: clock.env.now() });
  // Normal timings: a 1600 ms sweep, 500 ms to turn lime and a 500 ms pause.
  assert.equal(cloud.settleDelay(), 2600);
  clock.advance(1000);
  assert.equal(cloud.settleDelay(), 1600);
  clock.advance(5000);
  assert.equal(cloud.settleDelay(), 0, 'never negative');
  clock.advance(-6000);
  cloud.destroy();
  assert.equal(cloud.settleDelay(), 0, 'a destroyed cloud settles nothing');
});
