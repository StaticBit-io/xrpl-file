import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { parseRecord, earliestAvailableLedger, LedgerClient, MAINNET_FIRST_LEDGER } from '../src/ledger.mjs';

// parseRecord and earliestAvailableLedger decide what a chunk is and whether a node's history
// reaches back far enough -- the two answers the reader trusts a node for. The .NET side holds
// XrplClientLedgerSource's parser to these same shapes; this file is that suite's mirror.

test('an api_version 2 record is parsed from tx_json and the record-level fields', () => {
  const tx = parseRecord({
    hash: 'abc1',
    ledger_index: 106497159,
    validated: true,
    meta: { TransactionResult: 'tesSUCCESS' },
    tx_json: {
      Account: 'rWriter',
      TransactionType: 'AccountSet',
      Memos: [{ Memo: { MemoType: '78662F632F30', MemoData: 'CAFE', MemoFormat: '6170706C69636174696F6E2F6A736F6E' } }],
    },
  });

  assert.deepEqual(tx, {
    hash: 'ABC1',
    ledgerIndex: 106497159,
    account: 'rWriter',
    succeeded: true,
    validated: true,
    memoTypeHex: '78662F632F30',
    memoDataHex: 'CAFE',
    memoFormatHex: '6170706C69636174696F6E2F6A736F6E',
  });
});

test('an api_version 1 record is parsed from tx', () => {
  const tx = parseRecord({
    validated: true,
    meta: { TransactionResult: 'tecUNFUNDED' },
    tx: { hash: 'abc2', ledger_index: 5, Account: 'rWriter', Memos: [{ Memo: { MemoData: '00' } }] },
  });

  assert.equal(tx.hash, 'ABC2');
  assert.equal(tx.ledgerIndex, 5);
  assert.equal(tx.succeeded, false);
  assert.equal(tx.memoTypeHex, null);
  assert.equal(tx.memoDataHex, '00');
});

test('a record without memos or meta still parses', () => {
  const tx = parseRecord({ hash: 'x', ledger_index: 1, tx_json: { Account: 'r' } });

  assert.equal(tx.succeeded, false);
  assert.equal(tx.validated, false);
  assert.equal(tx.memoDataHex, null);
});

test('a Memos that is not an array of memos yields no memo, not a crash', () => {
  for (const Memos of [null, 'CAFE', [], [{}], [{ Memo: null }]]) {
    const tx = parseRecord({ hash: 'x', ledger_index: 1, tx_json: { Account: 'r', Memos } });
    assert.equal(tx.memoTypeHex, null);
    assert.equal(tx.memoDataHex, null);
  }
});

test('a non-integer ledger_index is not a ledger', () => {
  assert.equal(parseRecord({ hash: 'x', ledger_index: 'current', tx_json: { Account: 'r' } }).ledgerIndex, 0);
  assert.equal(parseRecord({ tx_json: { Account: 'r' } }).hash, '');
});

test('the earliest ledger is the low bound of the first range', () => {
  const info = (complete_ledgers) => ({ info: { complete_ledgers } });

  assert.equal(earliestAvailableLedger(info('32570-106738922')), MAINNET_FIRST_LEDGER);
  assert.equal(earliestAvailableLedger(info('32570-100,5000-106738922')), 32570);
  assert.equal(earliestAvailableLedger(info('100000000-106738922')), 100000000);
  // "empty" and a node that does not say are both null -- zero would read as "has everything".
  assert.equal(earliestAvailableLedger(info('empty')), null);
  assert.equal(earliestAvailableLedger(info('')), null);
  assert.equal(earliestAvailableLedger({}), null);
  assert.equal(earliestAvailableLedger(undefined), null);
  assert.equal(earliestAvailableLedger({ info: { complete_ledgers: 42 } }), null);
});

// The LedgerClient itself, over a stub socket: what it sends, and every way a request can fail
// without the node ever answering. All of these must be XrplFileError('NodeUnreachable').

/** A socket that opens on the next turn and answers only what a test hands it. */
class StubSocket {
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.sent = [];
    StubSocket.created.push(this);
    queueMicrotask(() => this.onopen?.());
  }

  send(frame) {
    this.sent.push(frame);
  }

  close() {
    this.readyState = 3;
  }
}
StubSocket.OPEN = 1;

const turn = () => new Promise((resolve) => setTimeout(resolve, 0));

async function withStub(run) {
  const original = globalThis.WebSocket;
  StubSocket.created = [];
  globalThis.WebSocket = StubSocket;
  try {
    return await run();
  } finally {
    globalThis.WebSocket = original;
  }
}

/** The last frame the client sent, and a way to answer it. */
function conversation() {
  const socket = StubSocket.created.at(-1);
  const sent = () => JSON.parse(socket.sent.at(-1));
  return {
    socket,
    sent,
    answer: (result) => socket.onmessage({ data: JSON.stringify({ id: sent().id, status: 'success', result }) }),
    fail: (error) => socket.onmessage({ data: JSON.stringify({ id: sent().id, status: 'error', error }) }),
  };
}

test('a frame that is not JSON is ignored, not thrown out of the socket handler', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const pending = client.request('server_info', {});
    await turn();
    const node = conversation();

    // Without the guard this throws inside onmessage, where nothing catches it, and the request
    // hangs until its own 20-second timeout instead of being answered by the frame below.
    assert.doesNotThrow(() => node.socket.onmessage({ data: 'not json at all' }));
    node.answer({ info: { complete_ledgers: '32570-9' } });

    assert.deepEqual(await pending, { info: { complete_ledgers: '32570-9' } });
    assert.equal(client.connections[0].pending.size, 0);
    client.close();
  });
});

test('a socket gone between connect and send rejects as an unreachable node', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const connection = client.connections[0];
    await connection.connect();
    // The node hung up in the gap between connect() resolving and the frame going out.
    connection.socket.readyState = 3;

    await assert.rejects(
      connection.send('server_info', {}),
      (e) => e.code === 'NodeUnreachable' && /was gone before server_info/.test(e.message),
    );
    assert.equal(connection.pending.size, 0);
    client.close();
  });
});

test('a send that throws rejects as an unreachable node rather than escaping', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const connection = client.connections[0];
    await connection.connect();
    connection.socket.send = () => { throw new Error('INVALID_STATE_ERR'); };

    await assert.rejects(
      connection.send('tx', {}),
      (e) => e.code === 'NodeUnreachable' && /INVALID_STATE_ERR/.test(e.message),
    );
    assert.equal(connection.pending.size, 0);
    client.close();
  });
});

test('an error answer to server_info is an unreachable node, not a history verdict', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const pending = client.earliestLedger();
    await turn();
    conversation().fail('noNetwork');

    // Read as "the node does not say", this would reach the reader as NodeLacksHistory: a verdict
    // about the node's history drawn from an answer that carried none.
    await assert.rejects(pending, (e) => e.code === 'NodeUnreachable' && /noNetwork/.test(e.message));
    client.close();
  });
});

test('a transaction the node does not have is null, and any other error is unreachable', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);

    const missing = client.transaction('A'.repeat(64));
    await turn();
    conversation().fail('txnNotFound');
    assert.equal(await missing, null);

    const refused = client.transaction('B'.repeat(64));
    await turn();
    conversation().fail('invalidParams');
    await assert.rejects(refused, (e) => e.code === 'NodeUnreachable' && /invalidParams/.test(e.message));
    client.close();
  });
});

test('account_tx goes out with the ledger range and pages on the marker the node returned', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid'], { pageSize: 7 });
    const read = [];
    const walk = (async () => {
      for await (const tx of client.accountTransactions('rWriter', 1000, 1026, true)) read.push(tx);
    })();

    await turn();
    const first = conversation().sent();
    conversation().answer({
      transactions: [{ hash: 'a', ledger_index: 1000, validated: true, meta: { TransactionResult: 'tesSUCCESS' }, tx_json: { Account: 'rWriter' } }],
      marker: { seq: 3 },
    });
    await turn();
    const second = conversation().sent();
    conversation().answer({ transactions: [] });
    await walk;

    assert.deepEqual(read.map((tx) => tx.hash), ['A']);
    assert.equal(first.command, 'account_tx');
    assert.equal(first.api_version, 2);
    assert.equal(first.ledger_index_min, 1000);
    assert.equal(first.ledger_index_max, 1026);
    assert.equal(first.forward, true);
    assert.equal(first.limit, 7);
    assert.equal(first.marker, undefined);
    assert.deepEqual(second.marker, { seq: 3 });
    client.close();
  });
});

test('an account the node has never heard of has no transactions, not an unreachable node', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const read = [];
    const walk = (async () => {
      for await (const tx of client.accountTransactions('rNobody', 1, 2, true)) read.push(tx);
    })();

    await turn();
    conversation().fail('actNotFound');

    await walk;
    assert.deepEqual(read, []);
    client.close();
  });
});

test('an error answer to account_tx is an unreachable node', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const walk = (async () => {
      for await (const _ of client.accountTransactions('rWriter', 1, 2, true)) break;
    })();

    await turn();
    conversation().fail('invalidParams');

    await assert.rejects(walk, (e) => e.code === 'NodeUnreachable' && /account_tx/.test(e.message));
    client.close();
  });
});

// Connection sharing: a page of blocks starts many requests at once, before the first socket has
// opened. They must share that one handshake instead of each building a socket of their own.

/** A socket that stays CONNECTING until a test opens, fails or closes it. */
class ManualSocket {
  constructor(url) {
    this.url = url;
    this.readyState = 0;
    this.sent = [];
    this.closeCalls = 0;
    ManualSocket.created.push(this);
  }

  open() {
    this.readyState = 1;
    this.onopen?.();
  }

  fail() {
    this.readyState = 3;
    this.onerror?.();
    this.onclose?.();
  }

  hangUp() {
    this.readyState = 3;
    this.onclose?.();
  }

  send(frame) {
    this.sent.push(frame);
    const { id } = JSON.parse(frame);
    queueMicrotask(() => this.onmessage?.({ data: JSON.stringify({ id, status: 'success', result: { ok: true } }) }));
  }

  close() {
    this.closeCalls++;
    this.readyState = 3;
  }
}
ManualSocket.OPEN = 1;

async function withManual(run) {
  const original = globalThis.WebSocket;
  ManualSocket.created = [];
  globalThis.WebSocket = ManualSocket;
  try {
    return await run();
  } finally {
    globalThis.WebSocket = original;
  }
}

test('requests started before the socket opens share one WebSocket', async () => {
  await withManual(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const requests = Array.from({ length: 10 }, () => client.request('server_info', {}));
    await turn();

    assert.equal(ManualSocket.created.length, 1);
    ManualSocket.created[0].open();
    const answers = await Promise.all(requests);

    assert.equal(ManualSocket.created.length, 1);
    assert.equal(ManualSocket.created[0].sent.length, 10);
    assert.ok(answers.every((answer) => answer.ok === true));
    client.close();
  });
});

test('close() during a shared handshake rejects every waiter and closes that one socket', async () => {
  await withManual(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const requests = Array.from({ length: 4 }, () => client.request('server_info', {}));
    await turn();
    const socket = ManualSocket.created[0];

    client.close();
    // A handshake that was already queued to open must not revive the closed client.
    socket.onopen?.();

    for (const request of requests) {
      await assert.rejects(request, (e) => e.code === 'NodeUnreachable' && /has been closed/.test(e.message));
    }
    assert.equal(ManualSocket.created.length, 1);
    assert.equal(socket.closeCalls, 1);
    assert.equal(client.connections[0].socket, null);
    assert.equal(client.connections[0].connecting, null);
  });
});

test('a failed shared handshake rejects every waiter, and the next request builds a new socket', async () => {
  await withManual(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const requests = Array.from({ length: 3 }, () => client.request('server_info', {}));
    await turn();
    assert.equal(ManualSocket.created.length, 1);

    ManualSocket.created[0].fail();
    for (const request of requests) {
      await assert.rejects(request, (e) => e.code === 'NodeUnreachable');
    }
    assert.equal(ManualSocket.created.length, 1);

    const retry = client.request('server_info', {});
    await turn();
    assert.equal(ManualSocket.created.length, 2);
    ManualSocket.created[1].open();
    assert.deepEqual(await retry, { ok: true });
    client.close();
  });
});

test('waiters of a failed shared handshake move on together, each node tried once', async () => {
  await withManual(async () => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid']);
    const requests = Array.from({ length: 3 }, () => client.request('server_info', {}));
    await turn();
    ManualSocket.created[0].fail();
    await turn();

    // One shared retry on the second node, not one per waiter and not a skip back to the first.
    assert.deepEqual(ManualSocket.created.map((s) => s.url), ['wss://a.invalid', 'wss://b.invalid']);
    ManualSocket.created[1].open();
    const answers = await Promise.all(requests);

    assert.ok(answers.every((answer) => answer.ok === true));
    assert.equal(ManualSocket.created.length, 2);
    client.close();
  });
});

test('after the server closes an open socket the next request reconnects once', async () => {
  await withManual(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    const first = client.request('server_info', {});
    await turn();
    ManualSocket.created[0].open();
    await first;

    ManualSocket.created[0].hangUp();
    assert.equal(client.connections[0].socket, null);

    const again = Array.from({ length: 3 }, () => client.request('server_info', {}));
    await turn();
    assert.equal(ManualSocket.created.length, 2);
    ManualSocket.created[1].open();
    await Promise.all(again);

    assert.equal(ManualSocket.created.length, 2);
    client.close();
  });
});

// Inactivity timeout: the deadline is "no frame at all for the whole window", not "no answer to
// this request", so concurrent slow pages on one shared socket do not kill each other.

/** An open socket that never answers on its own; the test delivers frames. */
class QuietSocket extends ManualSocket {
  send(frame) {
    this.sent.push(frame);
  }

  deliver(message) {
    this.onmessage?.({ data: JSON.stringify(message) });
  }
}
QuietSocket.OPEN = 1;

const flush = () => new Promise((resolve) => setImmediate(resolve));

async function withQuiet(run) {
  const original = globalThis.WebSocket;
  ManualSocket.created = [];
  globalThis.WebSocket = QuietSocket;
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    return await run();
  } finally {
    mock.timers.reset();
    globalThis.WebSocket = original;
  }
}

test('a slow request on a socket that keeps delivering other frames does not time out', async () => {
  await withQuiet(async () => {
    const client = new LedgerClient(['wss://example.invalid'], { requestTimeoutMs: 20000 });
    const slow = client.request('account_tx', {});
    const fast = client.request('account_tx', {});
    await flush();
    const socket = ManualSocket.created[0];
    socket.open();
    await flush();

    const [slowId, fastId] = socket.sent.map((frame) => JSON.parse(frame).id);
    let settled = false;
    slow.then(() => { settled = true; }, () => { settled = true; });

    // 30 s in total is past one window, but a frame lands every 15 s.
    mock.timers.tick(15000);
    socket.deliver({ id: fastId, status: 'success', result: { page: 1 } });
    await flush();
    mock.timers.tick(15000);
    await flush();

    assert.equal(settled, false);
    assert.deepEqual(await fast, { page: 1 });

    socket.deliver({ id: slowId, status: 'success', result: { page: 2 } });
    assert.deepEqual(await slow, { page: 2 });
    client.close();
  });
});

test('a frame that answers nobody still counts as socket activity', async () => {
  await withQuiet(async () => {
    const client = new LedgerClient(['wss://example.invalid'], { requestTimeoutMs: 20000 });
    const request = client.request('server_info', {});
    await flush();
    const socket = ManualSocket.created[0];
    socket.open();
    await flush();
    let settled = false;
    request.then(() => { settled = true; }, () => { settled = true; });

    mock.timers.tick(19000);
    socket.deliver({ type: 'ledgerClosed' });
    mock.timers.tick(19000);
    await flush();
    assert.equal(settled, false);

    socket.deliver({ id: JSON.parse(socket.sent[0]).id, status: 'success', result: { ok: true } });
    assert.deepEqual(await request, { ok: true });
    client.close();
  });
});

test('a silent socket times out once and tears the connection down for every request', async () => {
  await withQuiet(async () => {
    const client = new LedgerClient(['wss://example.invalid'], { requestTimeoutMs: 20000 });
    const first = client.request('account_tx', {});
    const second = client.request('account_tx', {});
    const outcomes = [first, second].map((request) => request.then(() => null, (e) => e));
    await flush();
    const socket = ManualSocket.created[0];
    socket.open();
    await flush();

    mock.timers.tick(19999);
    await flush();
    assert.equal(client.connections[0].pending.size, 2);
    mock.timers.tick(1);

    const [a, b] = await Promise.all(outcomes);
    assert.equal(a.code, 'NodeUnreachable');
    assert.equal(b.code, 'NodeUnreachable');
    assert.equal(socket.closeCalls, 1);
    assert.equal(client.connections[0].pending.size, 0);
    assert.equal(client.connections[0].socket, null);
    client.close();
  });
});

// Rate limiting: a public node that has used up its quota answers tooBusy or slowDown. That is an
// answer, not silence, so the client has to treat it as "this endpoint is busy" and ask another.

/**
 * A socket that answers every frame from a per-endpoint script: a function of the command, the
 * frame and the fake clock. A reply may be null (never answered), { hold } (answered when the test
 * releases ScriptedSocket.held), late (after the burst), or a function evaluated when it is delivered.
 */
class ScriptedSocket {
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.sent = [];
    this.closeCalls = 0;
    ScriptedSocket.created.push(this);
    queueMicrotask(() => {
      if (!ScriptedSocket.unreachable.has(url)) {
        this.onopen?.();
        return;
      }
      this.readyState = 3;
      this.onerror?.();
      this.onclose?.();
    });
  }

  send(frame) {
    const parsed = JSON.parse(frame);
    const { id, command } = parsed;
    this.sent.push(command);
    ScriptedSocket.requests.push(this.url);
    ScriptedSocket.log.push({ url: this.url, tag: parsed.tag });
    const reply = ScriptedSocket.script[this.url](command, parsed, ScriptedSocket.now());
    if (reply === null) return;
    const deliver = (body) => this.onmessage?.({ data: JSON.stringify({ id, ...body }) });
    if (typeof reply === 'function') {
      setImmediate(() => deliver(reply(ScriptedSocket.now())));
      return;
    }
    if (reply.hold) {
      ScriptedSocket.held.push(() => deliver(reply.hold));
      return;
    }
    const { late, ...body } = reply;
    // A late answer lands after the burst, so whatever happens in between catches it in flight.
    if (late) setImmediate(() => deliver(body));
    else queueMicrotask(() => deliver(body));
  }

  hangUp() {
    this.readyState = 3;
    this.onclose?.();
  }

  close() {
    this.closeCalls++;
    this.readyState = 3;
  }
}
ScriptedSocket.OPEN = 1;

const ok = (result) => ({ status: 'success', result });
const busy = (error, message = 'units quota (10000 per 60s) exhausted, retry in ~66-81 s') => ({ status: 'error', error, error_message: message });

/**
 * Runs against ScriptedSocket with a fake clock and a timer that records waits instead of
 * sleeping: a wait ends on the next turn and moves the clock to its end.
 */
async function withScript(script, run, { unreachable = [] } = {}) {
  const original = globalThis.WebSocket;
  let clock = 0;
  ScriptedSocket.created = [];
  ScriptedSocket.requests = [];
  ScriptedSocket.log = [];
  ScriptedSocket.held = [];
  ScriptedSocket.unreachable = new Set(unreachable);
  ScriptedSocket.script = script;
  ScriptedSocket.now = () => clock;
  globalThis.WebSocket = ScriptedSocket;
  const waits = [];
  const timers = {
    now: () => clock,
    setTimer: (fn, ms) => {
      waits.push(ms);
      const at = clock + ms;
      return setImmediate(() => {
        clock = Math.max(clock, at);
        fn();
      });
    },
    clearTimer: (handle) => clearImmediate(handle),
  };
  try {
    return await run({ waits, timers });
  } finally {
    globalThis.WebSocket = original;
  }
}

/** Gives the client turns until the condition holds. */
async function until(condition) {
  for (let i = 0; i < 200 && !condition(); i++) await turn();
  assert.ok(condition(), 'the condition never held');
}

/** Sends per tag per endpoint, from ScriptedSocket.log. */
function sendsByTag() {
  const counts = new Map();
  for (const { url, tag } of ScriptedSocket.log) {
    const key = `${tag} ${url}`;
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return counts;
}

test('a tooBusy answer moves the same request to the next endpoint and returns its result', async () => {
  const script = {
    'wss://a.invalid': () => busy('tooBusy'),
    'wss://b.invalid': () => ok({ info: { complete_ledgers: '32570-9' } }),
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    assert.deepEqual(await client.request('server_info', {}), { info: { complete_ledgers: '32570-9' } });
    assert.deepEqual(ScriptedSocket.requests, ['wss://a.invalid', 'wss://b.invalid']);
    assert.deepEqual(waits, []);
    client.close();
  });
});

test('a slowDown answer is treated like tooBusy', async () => {
  const script = {
    'wss://a.invalid': () => busy('slowDown', 'rate limit: units quota (2000 per 10s) exhausted, retry in ~19353ms'),
    'wss://b.invalid': () => ok({ fine: true }),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    assert.deepEqual(await client.request('account_tx', {}), { fine: true });
    client.close();
  });
});

test('the client stays on the endpoint it moved to for the next request', async () => {
  const script = {
    'wss://a.invalid': () => busy('tooBusy'),
    'wss://b.invalid': () => ok({ fine: true }),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);
    await client.request('account_tx', {});
    ScriptedSocket.requests.length = 0;

    await client.request('account_tx', {});

    assert.deepEqual(ScriptedSocket.requests, ['wss://b.invalid']);
    assert.equal(client.endpoint, 'wss://b.invalid');
    client.close();
  });
});

test('every endpoint busy: one wait for the node free soonest, then only the nodes free by then are asked', async () => {
  const script = {
    'wss://a.invalid': () => busy('tooBusy', 'retry in ~30-40 s'),
    'wss://b.invalid': (command, frame, now) => (now < 12000 ? busy('slowDown', 'rate limit, retry in ~12000ms') : ok({ again: true })),
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    assert.deepEqual(await client.request('account_tx', {}), { again: true });
    assert.deepEqual(waits, [12000]);
    // a said 30 s; asking it again after 12 s would only spend more of its quota.
    assert.deepEqual(ScriptedSocket.requests, ['wss://a.invalid', 'wss://b.invalid', 'wss://b.invalid']);
    client.close();
  });
});

test('the wait is clamped to 1-120 s and is 20 s without a hint', async () => {
  const cases = [
    ['retry in ~5 ms', 1000],
    ['retry in ~500 s', 120000],
    ['retry in ~70 s', 70000],
    // A range waits for its high end, so the retry lands after the node's window.
    ['units quota (10000 per 60s) exhausted, retry in ~66-81 s', 81000],
    ['retry in ~19353ms', 19353],
    ['retry in ~90-150 s', 120000],
    ['quota exhausted', 20000],
  ];
  for (const [message, expected] of cases) {
    let calls = 0;
    const script = { 'wss://a.invalid': () => (++calls === 1 ? busy('tooBusy', message) : ok({ fine: true })) };
    await withScript(script, async ({ waits, timers }) => {
      const client = new LedgerClient(['wss://a.invalid'], timers);

      await client.request('server_info', {});
      assert.deepEqual(waits, [expected], message);
      client.close();
    });
  }
});

test('busy on every endpoint in both passes is an unreachable node that names the rate limit', async () => {
  const script = {
    'wss://a.invalid': () => busy('tooBusy'),
    'wss://b.invalid': () => busy('tooBusy'),
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    await assert.rejects(client.request('account_tx', {}), (e) => e.code === 'NodeUnreachable' && /rate limit/i.test(e.message));
    assert.equal(waits.length, 1);
    client.close();
  });
});

test('txnNotFound, actNotFound and other errors do not move the client or wait', async () => {
  for (const error of ['txnNotFound', 'actNotFound', 'invalidParams']) {
    const script = { 'wss://a.invalid': () => busy(error, ''), 'wss://b.invalid': () => ok({}) };
    await withScript(script, async ({ waits, timers }) => {
      const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

      const answer = await client.request('tx', {});

      assert.equal(answer.error, error);
      assert.deepEqual(ScriptedSocket.requests, ['wss://a.invalid']);
      assert.deepEqual(waits, []);
      client.close();
    });
  }
});

test('close() during the rate-limit wait cancels it promptly', async () => {
  const script = { 'wss://a.invalid': () => busy('tooBusy') };
  await withScript(script, async () => {
    const cleared = [];
    const timers = {
      setTimer: (fn, ms) => setTimeout(fn, ms),
      clearTimer: (handle) => { cleared.push(handle); clearTimeout(handle); },
    };
    const client = new LedgerClient(['wss://a.invalid'], timers);
    const request = client.request('account_tx', {}).then(() => null, (e) => e);
    await turn();
    await turn();
    assert.equal(client.waits.size, 1);

    client.close();

    const error = await request;
    assert.equal(error.code, 'NodeUnreachable');
    assert.match(error.message, /closed/);
    assert.equal(cleared.length, 1);
    assert.equal(client.waits.size, 0);
  });
});

test('a wait started after close() is refused instead of armed', async () => {
  const client = new LedgerClient(['wss://a.invalid'], { setTimer: () => assert.fail('armed a timer') });
  client.close();

  await assert.rejects(client.waitMs(1000), (e) => e.code === 'NodeUnreachable' && /closed/.test(e.message));
});

test('one endpoint: a busy answer waits and resends on the same socket without failing its neighbours', async () => {
  let calls = 0;
  const script = { 'wss://a.invalid': () => (++calls === 1 ? busy('slowDown', 'retry in ~2 s') : { ...ok({ n: calls }), late: true }) };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid'], timers);

    const answers = await Promise.all(Array.from({ length: 4 }, () => client.request('account_tx', {})));

    assert.equal(answers.length, 4);
    assert.ok(answers.every((answer) => answer.n > 1));
    assert.equal(ScriptedSocket.created.length, 1);
    assert.deepEqual(waits, [2000]);
    client.close();
  });
});

test('two endpoints: once a node is busy, requests started later go to the other one', async () => {
  let aCalls = 0;
  const script = {
    'wss://a.invalid': () => (++aCalls === 1 ? busy('tooBusy') : { ...ok({ from: 'a' }), late: true }),
    'wss://b.invalid': () => ok({ from: 'b' }),
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);
    await client.request('account_tx', {});

    const answers = await Promise.all(Array.from({ length: 4 }, () => client.request('account_tx', {})));

    assert.deepEqual(answers.map((answer) => answer.from), ['b', 'b', 'b', 'b']);
    assert.equal(aCalls, 1);
    assert.deepEqual(waits, []);
    assert.equal(client.endpoint, 'wss://b.invalid');
    client.close();
  });
});

test('every endpoint busy for parallel requests: each fails naming the rate limit, none as a closed connection', async () => {
  let calls = 0;
  const script = {
    'wss://a.invalid': () => (++calls === 1 ? busy('tooBusy') : { ...busy('tooBusy'), late: true }),
    'wss://b.invalid': () => ({ ...busy('slowDown'), late: true }),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    const outcomes = await Promise.all(Array.from({ length: 3 }, () => client.request('account_tx', {}).then(() => null, (e) => e)));

    for (const error of outcomes) {
      assert.equal(error?.code, 'NodeUnreachable');
      assert.match(error.message, /rate limit/i);
      assert.doesNotMatch(error.message, /in flight/);
    }
    client.close();
  });
});

test('the rate-limit message names every endpoint that answered busy and what it said', async () => {
  const script = { 'wss://a.invalid': () => busy('tooBusy'), 'wss://b.invalid': () => busy('slowDown') };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    await assert.rejects(
      client.request('tx', {}),
      (e) => e.message === 'wss://a.invalid answered tooBusy, wss://b.invalid answered slowDown to tx: the node is rate limiting this client',
    );
    client.close();
  });
});

// One connection per endpoint: a busy answer is about the node and this client, not about the
// other requests in flight, so it must never close a connection or fail its neighbours.

test('eight parallel requests with both endpoints busy and staggered answers each wait once at most and succeed', async () => {
  const script = {
    'wss://a.invalid': (command, frame) => {
      const reply = (now) => (now < 66000 ? busy('tooBusy') : ok({ from: 'a', tag: frame.tag }));
      return frame.tag % 2 === 1 ? reply : reply(ScriptedSocket.now());
    },
    'wss://b.invalid': (command, frame) => {
      const reply = (now) => (now < 19353 ? busy('slowDown', 'rate limit: units quota (2000 per 10s) exhausted, retry in ~19353ms') : ok({ from: 'b', tag: frame.tag }));
      return frame.tag % 2 === 1 ? reply : reply(ScriptedSocket.now());
    },
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    const outcomes = await Promise.all(Array.from({ length: 8 }, (_, tag) => client.request('account_tx', { tag }).then((answer) => answer, (e) => e)));

    for (const [tag, outcome] of outcomes.entries()) {
      assert.equal(outcome.tag, tag, `request ${tag}: ${outcome.message ?? ''}`);
    }
    assert.ok(waits.length <= 8, `${waits.length} waits`);
    for (const [key, count] of sendsByTag()) assert.ok(count <= 2, `${key}: ${count} sends`);
    assert.ok(ScriptedSocket.created.length <= 2, `${ScriptedSocket.created.length} sockets`);
    client.close();
  });
});

test('a busy answer for one request leaves the requests in flight beside it on that endpoint', async () => {
  const script = {
    'wss://a.invalid': (command, frame) => (frame.tag === 0 ? busy('tooBusy') : { ...ok({ from: 'a' }), late: true }),
    'wss://b.invalid': () => ok({ from: 'b' }),
  };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    const answers = await Promise.all(Array.from({ length: 4 }, (_, tag) => client.request('account_tx', { tag })));

    assert.deepEqual(answers.map((answer) => answer.from), ['b', 'a', 'a', 'a']);
    assert.deepEqual(waits, []);
    assert.equal(ScriptedSocket.created.length, 2);
    assert.equal(ScriptedSocket.created[0].closeCalls, 0);
    client.close();
  });
});

test('a node that hangs up fails only its own requests, and they finish on the next endpoint', async () => {
  const script = {
    'wss://a.invalid': (command, frame) => (frame.tag === 0 ? busy('tooBusy', 'retry in ~5 ms') : null),
    'wss://b.invalid': (command, frame) => (frame.tag === 0 ? { hold: ok({ from: 'b', tag: 0 }) } : ok({ from: 'b', tag: frame.tag })),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);
    const requests = Array.from({ length: 4 }, (_, tag) => client.request('account_tx', { tag }));
    await until(() => ScriptedSocket.created.length === 2 && ScriptedSocket.created[1].sent.length === 1);
    const [a, b] = ScriptedSocket.created;
    assert.equal(a.sent.length, 4);
    assert.equal(a.closeCalls, 0);

    a.hangUp();
    await until(() => b.sent.length === 4);
    ScriptedSocket.held.forEach((release) => release());
    const answers = await Promise.all(requests);

    assert.deepEqual(answers.map((answer) => answer.tag), [0, 1, 2, 3]);
    assert.ok(answers.every((answer) => answer.from === 'b'));
    assert.equal(b.closeCalls, 0);
    assert.equal(ScriptedSocket.created.length, 2);
    client.close();
  });
});

test('one endpoint busy twice: one wait, two sends, and the error names the rate limit', async () => {
  const script = { 'wss://a.invalid': () => busy('slowDown', 'retry in ~3 s') };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid'], timers);

    await assert.rejects(client.request('account_tx', {}), (e) => e.code === 'NodeUnreachable' && /rate limit/i.test(e.message));
    assert.deepEqual(waits, [3000]);
    assert.equal(ScriptedSocket.requests.length, 2);
    assert.equal(ScriptedSocket.created.length, 1);
    client.close();
  });
});

test('a request that starts while every endpoint is known to be busy waits without sending', async () => {
  const script = { 'wss://a.invalid': () => busy('tooBusy'), 'wss://b.invalid': () => busy('slowDown') };
  await withScript(script, async () => {
    const held = [];
    const timers = { now: () => 0, setTimer: (fn, ms) => { held.push(ms); return held.length; }, clearTimer: () => {} };
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);
    const first = client.request('account_tx', {}).catch((e) => e);
    await until(() => held.length === 1);
    const sent = ScriptedSocket.requests.length;

    const second = client.request('account_tx', {}).catch((e) => e);
    await until(() => held.length === 2);

    assert.equal(ScriptedSocket.requests.length, sent);
    client.close();
    assert.equal((await first).code, 'NodeUnreachable');
    assert.equal((await second).code, 'NodeUnreachable');
  });
});

test('one node busy and the other unreachable: the busy node is asked at most twice', async () => {
  const script = { 'wss://a.invalid': () => busy('tooBusy', 'retry in ~2 s'), 'wss://b.invalid': () => ok({}) };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    await assert.rejects(client.request('account_tx', {}), (e) => e.code === 'NodeUnreachable' && /rate limit/i.test(e.message));
    assert.deepEqual(ScriptedSocket.requests, ['wss://a.invalid', 'wss://a.invalid']);
    assert.deepEqual(waits, [2000]);
    assert.ok(ScriptedSocket.created.length <= 3, `${ScriptedSocket.created.length} sockets`);
    client.close();
  }, { unreachable: ['wss://b.invalid'] });
});

test('close() during the waits of several requests cancels every one of them promptly', async () => {
  const script = { 'wss://a.invalid': () => busy('tooBusy'), 'wss://b.invalid': () => busy('slowDown') };
  await withScript(script, async () => {
    const cleared = [];
    const timers = {
      setTimer: (fn, ms) => setTimeout(fn, ms),
      clearTimer: (handle) => { cleared.push(handle); clearTimeout(handle); },
    };
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);
    const requests = Array.from({ length: 3 }, (_, tag) => client.request('account_tx', { tag }).then(() => null, (e) => e));
    await until(() => client.waits.size === 3);

    client.close();

    for (const error of await Promise.all(requests)) {
      assert.equal(error.code, 'NodeUnreachable');
      assert.match(error.message, /closed/);
    }
    assert.equal(cleared.length, 3);
    assert.equal(client.waits.size, 0);
  });
});

test('eight parallel requests with every endpoint busy throughout each fail once, naming the rate limit', async () => {
  const late = (reply) => (command, frame) => (frame.tag % 3 === 0 ? reply : { ...reply, late: true });
  const script = { 'wss://a.invalid': late(busy('tooBusy')), 'wss://b.invalid': late(busy('slowDown', 'retry in ~19353ms')) };
  await withScript(script, async ({ waits, timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], timers);

    const outcomes = await Promise.all(Array.from({ length: 8 }, (_, tag) => client.request('account_tx', { tag }).then(() => null, (e) => e)));

    for (const error of outcomes) {
      assert.equal(error?.code, 'NodeUnreachable');
      assert.match(error.message, /rate limit/i);
    }
    assert.ok(waits.length <= 8, `${waits.length} waits`);
    for (const [key, count] of sendsByTag()) assert.ok(count <= 2, `${key}: ${count} sends`);
    assert.ok(ScriptedSocket.log.length <= 32, `${ScriptedSocket.log.length} sends`);
    assert.equal(ScriptedSocket.created.length, 2);
    client.close();
  });
});

test('a node marked busy again during a wait is still asked by the request that waited for it', async () => {
  const calls = new Map();
  const script = {
    'wss://a.invalid': () => busy('tooBusy'),
    'wss://b.invalid': (command, frame) => {
      const call = (calls.get(frame.tag) ?? 0) + 1;
      calls.set(frame.tag, call);
      if (call > 1) return ok({ tag: frame.tag });
      if (frame.tag === 0) return busy('slowDown', 'retry in ~5 s');
      return { hold: busy('slowDown', 'retry in ~6 s') };
    },
  };
  await withScript(script, async () => {
    let clock = 0;
    const timers = [];
    const options = { now: () => clock, setTimer: (fn, ms) => timers.push({ fn, at: clock + ms }), clearTimer: () => {} };
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], options);
    const first = client.request('account_tx', { tag: 0 });
    const second = client.request('account_tx', { tag: 1 });
    await until(() => timers.length === 1 && ScriptedSocket.held.length === 1);

    // tag 1's answer lands while tag 0 waits and moves b's mark past the end of that wait.
    ScriptedSocket.held[0]();
    await until(() => timers.length === 2);
    clock = timers[0].at;
    timers[0].fn();

    assert.deepEqual(await first, { tag: 0 });
    clock = timers[1].at;
    timers[1].fn();
    assert.deepEqual(await second, { tag: 1 });
    client.close();
  });
});

// A marker means something only to the node that issued it. Measured on mainnet (2026-11-19): a
// marker from xrplcluster.com names the next transaction to return, one from s2.ripple.com names the
// last one returned, so xrplcluster's marker sent to s2 skips a transaction -- a missing chunk.

/** One writer's history across ledgers, in canonical forward order. */
const HISTORY = [
  ['H1', 100], ['H2', 100], ['H3', 100], ['H4', 101], ['H5', 101], ['H6', 102], ['H7', 102],
].map(([hash, ledger]) => ({ hash, ledger_index: ledger, validated: true, meta: { TransactionResult: 'tesSUCCESS' }, tx_json: { Account: 'rWriter' } }));

/**
 * account_tx over HISTORY, paged the way one node does it. 'next' markers name the first record of
 * the next page (xrplcluster); 'last' markers name the last record of this one (s2).
 */
function pagedNode(style, { busyOn = () => false } = {}) {
  let calls = 0;
  return (command, frame) => {
    calls++;
    if (busyOn(calls)) return busy('tooBusy');
    const forward = frame.forward;
    const order = HISTORY.map((record, index) => ({ record, index }))
      .filter(({ record }) => record.ledger_index >= frame.ledger_index_min && record.ledger_index <= frame.ledger_index_max);
    if (!forward) order.reverse();
    let start = 0;
    if (frame.marker) {
      const at = order.findIndex(({ index }) => index === frame.marker.seq);
      start = style === 'next' ? at : at + 1;
    }
    const page = order.slice(start, start + frame.limit);
    const rest = order.slice(start + frame.limit);
    const marker = rest.length === 0 ? undefined : { seq: style === 'next' ? rest[0].index : page.at(-1).index };
    return ok({ transactions: page.map(({ record }) => record), ...(marker ? { marker } : {}) });
  };
}

async function walkAll(client, forward = true) {
  const read = [];
  for await (const tx of client.accountTransactions('rWriter', 100, 102, forward)) read.push(tx.hash);
  return read;
}

test('a page answered by another node than the one that issued the marker loses no transaction', async () => {
  const script = {
    'wss://cluster.invalid': pagedNode('next', { busyOn: (call) => call >= 2 }),
    'wss://s2.invalid': pagedNode('last'),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://cluster.invalid', 'wss://s2.invalid'], { ...timers, pageSize: 2 });

    assert.deepEqual(await walkAll(client), ['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'H7']);
    client.close();
  });
});

test('a hand-off the other way yields no transaction twice', async () => {
  const script = {
    'wss://s2.invalid': pagedNode('last', { busyOn: (call) => call >= 2 }),
    'wss://cluster.invalid': pagedNode('next'),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://s2.invalid', 'wss://cluster.invalid'], { ...timers, pageSize: 2 });

    assert.deepEqual(await walkAll(client), ['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'H7']);
    client.close();
  });
});

test('a backward walk handed off mid-ledger loses and repeats nothing', async () => {
  const script = {
    'wss://cluster.invalid': pagedNode('next', { busyOn: (call) => call >= 2 }),
    'wss://s2.invalid': pagedNode('last'),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://cluster.invalid', 'wss://s2.invalid'], { ...timers, pageSize: 3 });

    assert.deepEqual(await walkAll(client, false), ['H7', 'H6', 'H5', 'H4', 'H3', 'H2', 'H1']);
    client.close();
  });
});

test('a hand-off before anything was read restarts from the walk\'s own range', async () => {
  const empty = (command, frame) => (frame.marker ? pagedNode('next')(command, frame) : ok({ transactions: [], marker: { seq: 0 } }));
  let clusterCalls = 0;
  const script = {
    'wss://cluster.invalid': (command, frame) => (++clusterCalls >= 2 ? busy('tooBusy') : empty(command, frame)),
    'wss://s2.invalid': pagedNode('last'),
  };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://cluster.invalid', 'wss://s2.invalid'], { ...timers, pageSize: 2 });

    assert.deepEqual(await walkAll(client), ['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'H7']);
    client.close();
  });
});

test('pages that stay on one node follow its markers without restarting', async () => {
  const script = { 'wss://s2.invalid': pagedNode('last'), 'wss://cluster.invalid': pagedNode('next') };
  await withScript(script, async ({ timers }) => {
    const client = new LedgerClient(['wss://s2.invalid', 'wss://cluster.invalid'], { ...timers, pageSize: 2 });

    assert.deepEqual(await walkAll(client), ['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'H7']);
    assert.equal(ScriptedSocket.requests.filter((url) => url === 'wss://s2.invalid').length, 4);
    assert.equal(ScriptedSocket.requests.filter((url) => url === 'wss://cluster.invalid').length, 0);
    client.close();
  });
});

test('nodes that keep taking the walk from each other end it as unreachable instead of looping', async () => {
  // Each node is busy for its own markers only, so every continuation lands on the other node and
  // every restart reads back the same full page of one ledger: nothing new is ever yielded.
  const crowded = Array.from({ length: 4 }, (_, i) => ({ hash: `C${i}`, ledger_index: 100, validated: true, meta: { TransactionResult: 'tesSUCCESS' }, tx_json: { Account: 'rWriter' } }));
  const node = (name) => (command, frame) => {
    if (frame.marker?.node === name) return busy('tooBusy', 'retry in ~1000ms');
    return ok({ transactions: crowded.slice(0, frame.limit), marker: { node: name, seq: frame.limit } });
  };
  await withScript({ 'wss://a.invalid': node('a'), 'wss://b.invalid': node('b') }, async ({ timers }) => {
    const client = new LedgerClient(['wss://a.invalid', 'wss://b.invalid'], { ...timers, pageSize: 2 });
    const read = [];

    await assert.rejects(
      (async () => { for await (const tx of client.accountTransactions('rWriter', 100, 100, true)) read.push(tx.hash); })(),
      (e) => e.code === 'NodeUnreachable' && /kept moving between nodes/.test(e.message),
    );
    assert.deepEqual(read, ['C0', 'C1']);
    client.close();
  });
});

test('an error answer to a page sent with another node\'s marker restarts the walk instead of ending it', async () => {
  for (const refusal of ['actNotFound', 'invalidParams']) {
    const own = pagedNode('last');
    const issued = new Set();
    // This node refuses a marker it did not issue, and answers a walk without one.
    const refusesForeign = (command, frame) => {
      if (frame.marker && !issued.has(JSON.stringify(frame.marker))) return { status: 'error', error: refusal };
      const reply = own(command, frame);
      if (reply.result?.marker) issued.add(JSON.stringify(reply.result.marker));
      return reply;
    };
    const script = { 'wss://cluster.invalid': pagedNode('next', { busyOn: (call) => call >= 2 }), 'wss://s2.invalid': refusesForeign };
    await withScript(script, async ({ timers }) => {
      const client = new LedgerClient(['wss://cluster.invalid', 'wss://s2.invalid'], { ...timers, pageSize: 2 });

      assert.deepEqual(await walkAll(client), ['H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'H7'], refusal);
      client.close();
    });
  }
});
