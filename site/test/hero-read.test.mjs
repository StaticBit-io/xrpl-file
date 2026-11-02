import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { startHeroRead, hasSeenMosaic, markMosaicSeen, SEEN_KEY } = await import('../src/js/hero-read.mjs');
const { XrplFileError } = await import('../src/js/xrpl.mjs');

const HASH = '2AEB485BB0FAECA2DF4B60F196D5199042288FEB124ED8D95F4313CF06200DB4';
const LINK = `xrpl-file://testnet/${HASH}`;
const MANIFEST = { size: 2500, chunk: 1000, type: 'image/png', firstLedger: 10, lastLedger: 20 };

/** A clock and timers the test steps by hand. */
function fakeEnv(overrides = {}) {
  let time = 1000;
  const polls = [];
  const timers = [];
  const stored = new Map();
  const env = {
    now: () => time,
    setPoll: (callback) => polls.push(callback) && polls.length,
    clearPoll: (id) => { polls[id - 1] = null; },
    setTimer: (callback) => timers.push(callback) && timers.length,
    clearTimer: (id) => { timers[id - 1] = null; },
    storage: { getItem: (key) => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value) },
    ...overrides,
  };
  return {
    env,
    stored,
    advance(ms) { time += ms; },
    pollOnce() { for (const poll of polls) poll?.(); },
    firePending() { for (const timer of timers.splice(0)) timer?.(); },
    activePolls: () => polls.filter(Boolean).length,
  };
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

function readerThat(read) {
  const reader = { closed: 0, read, close() { reader.closed += 1; } };
  return reader;
}

function recorder() {
  const events = [];
  const listener = {};
  for (const name of ['onManifest', 'onChunk', 'onBlockVerified', 'onDeveloping', 'onDone', 'onFailed', 'onPhase']) {
    listener[name] = (detail) => events.push([name, detail]);
  }
  return { listener, events, names: () => events.map(([name]) => name) };
}

test('a node that never answers still ends in the failure state within 20 s of starting', async () => {
  const clock = fakeEnv();
  const { listener, events } = recorder();
  const reader = readerThat(() => new Promise(() => {}));
  const read = startHeroRead({ link: LINK, listener }, { ...clock.env, createReader: () => reader });

  clock.advance(19_999);
  clock.pollOnce();
  assert.equal(read.phase, 'reading');
  assert.deepEqual(events, []);

  clock.advance(1);
  clock.pollOnce();
  await read.done;
  assert.equal(read.phase, 'failed');
  assert.deepEqual(events.map(([name]) => name), ['onPhase', 'onFailed']);
  assert.equal(events[1][1].error.code, 'Stalled');
  assert.equal(reader.closed, 1, 'the connection is abandoned');
  assert.equal(clock.activePolls(), 0, 'the stall clock is stopped');
});

test('a read that rejects keeps what it got and reports the error code once', async () => {
  const clock = fakeEnv();
  const { listener, names, events } = recorder();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    hooks.onChunk({ index: 0, bytes: new Uint8Array(1000).fill(7) });
    hooks.onChunk({ index: 1, bytes: new Uint8Array(1000).fill(9) });
    throw new XrplFileError('NodeLacksHistory', 'the node does not have this ledger');
  });
  const read = startHeroRead({ link: LINK, listener }, { ...clock.env, createReader: () => reader });

  await read.done;
  assert.equal(read.phase, 'failed');
  assert.deepEqual(names(), ['onManifest', 'onChunk', 'onChunk', 'onPhase', 'onFailed']);
  assert.equal(events.at(-1)[1].error.code, 'NodeLacksHistory');
  assert.equal(read.state.cells[0], 'lit', 'the chunks read so far stay');
  assert.equal(reader.closed, 1);
  assert.equal(clock.stored.has(SEEN_KEY), false, 'a failed run is not remembered as seen');
});

test('a network with no public node fails at once with its own code', async () => {
  const clock = fakeEnv();
  const { listener, events } = recorder();
  const read = startHeroRead({ link: `xrpl-file://21338/${HASH}`, listener }, clock.env);
  await read.done;
  assert.equal(events.at(-1)[1].error.code, 'UnsupportedNetwork');
});

test('text that is not a link fails instead of throwing', async () => {
  const clock = fakeEnv();
  const { listener, events } = recorder();
  const read = startHeroRead({ link: 'https://example.com', listener }, clock.env);
  await read.done;
  assert.equal(events.at(-1)[1].error.code, 'InvalidLink');
});

test('a verified read reports each step once, in order, and is done only after the settling time', async () => {
  const clock = fakeEnv();
  const { listener, names, events } = recorder();
  const bytes = new Uint8Array(2500).fill(3);
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    for (let index = 0; index < 3; index += 1) hooks.onChunk({ index, bytes: bytes.subarray(index * 1000, index * 1000 + 1000) });
    assert.ok(!names().includes('onBlockVerified'), 'a chunk is not a verified block');
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: 2 });
    return { manifest: MANIFEST, writer: 'rWriter', blocks: [], bytes };
  });
  const read = startHeroRead(
    { link: ` ${LINK.replace('xrpl-file://testnet/', 'XRPL-FILE://TestNet/')}\n`, listener, developDelay: () => 1500 },
    { ...clock.env, createReader: () => reader },
  );

  await tick();
  assert.equal(read.phase, 'developing');
  assert.deepEqual(names(), ['onManifest', 'onChunk', 'onChunk', 'onChunk', 'onBlockVerified', 'onPhase', 'onDeveloping']);
  assert.equal(events[0][1].chunks, 3);
  assert.equal(events[4][1].lastChunk, 2);
  assert.equal(clock.stored.has(SEEN_KEY), false, 'not marked seen before the end');

  clock.firePending();
  await read.done;
  assert.equal(read.phase, 'done');
  assert.deepEqual(names().slice(-2), ['onPhase', 'onDone']);
  assert.equal(clock.stored.get(SEEN_KEY), '1');
  assert.equal(reader.closed >= 1, true);
});

test('chunks before the manifest, and anything after a failure, are ignored', async () => {
  const clock = fakeEnv();
  const { listener, names } = recorder();
  let hooks;
  const reader = readerThat((_link, _progress, given) => {
    hooks = given;
    return new Promise(() => {});
  });
  const read = startHeroRead({ link: LINK, listener }, { ...clock.env, createReader: () => reader });
  hooks.onChunk({ index: 0, bytes: new Uint8Array(10) });
  hooks.onBlockVerified({ firstChunk: 0, lastChunk: 0 });
  assert.deepEqual(names(), [], 'nothing is reported ahead of the manifest');
  hooks.onManifest(MANIFEST);
  clock.advance(20_000);
  clock.pollOnce();
  await read.done;
  hooks.onChunk({ index: 1, bytes: new Uint8Array(10) });
  assert.deepEqual(names(), ['onManifest', 'onPhase', 'onFailed']);
});

test('a read that resolves with unverified blocks is not shown as verified', async () => {
  const clock = fakeEnv();
  const { listener, events } = recorder();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest(MANIFEST);
    hooks.onChunk({ index: 0, bytes: new Uint8Array(10) });
    hooks.onBlockVerified({ block: 0, firstChunk: 0, lastChunk: 0 });
    return { manifest: MANIFEST, writer: 'rWriter', blocks: [], bytes: new Uint8Array(2500) };
  });
  const read = startHeroRead({ link: LINK, listener }, { ...clock.env, createReader: () => reader });
  await read.done;
  assert.equal(read.phase, 'failed');
  assert.equal(events.at(-1)[1].error.code, 'Incomplete');
  assert.ok(!events.some(([name]) => name === 'onDeveloping'));
});

test('destroy abandons the read, releases everything and settles done', async () => {
  const clock = fakeEnv();
  const { listener, names } = recorder();
  const reader = readerThat(() => new Promise(() => {}));
  const read = startHeroRead({ link: LINK, listener }, { ...clock.env, createReader: () => reader });
  read.destroy();
  await read.done;
  assert.equal(reader.closed, 1);
  assert.equal(clock.activePolls(), 0);
  assert.deepEqual(names(), []);
});

test('destroy during the settling time cancels the timer', async () => {
  const clock = fakeEnv();
  const { listener, names } = recorder();
  const reader = readerThat(async (_link, _progress, hooks) => {
    hooks.onManifest({ ...MANIFEST, size: 1000 });
    hooks.onChunk({ index: 0, bytes: new Uint8Array(1000) });
    hooks.onBlockVerified({ firstChunk: 0, lastChunk: 0 });
    return {};
  });
  const read = startHeroRead({ link: LINK, listener, developDelay: () => 500 }, { ...clock.env, createReader: () => reader });
  await tick();
  assert.equal(read.phase, 'developing');
  read.destroy();
  clock.firePending();
  assert.ok(!names().includes('onDone'));
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
