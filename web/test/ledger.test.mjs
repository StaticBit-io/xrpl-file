import { test } from 'node:test';
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
    assert.equal(client.pending.size, 0);
    client.close();
  });
});

test('a socket gone between connect and send rejects as an unreachable node', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    await client.connect();
    // The node hung up in the gap between connect() resolving and the frame going out.
    client.socket.readyState = 3;

    await assert.rejects(
      client.send('server_info', {}),
      (e) => e.code === 'NodeUnreachable' && /was gone before server_info/.test(e.message),
    );
    assert.equal(client.pending.size, 0);
    client.close();
  });
});

test('a send that throws rejects as an unreachable node rather than escaping', async () => {
  await withStub(async () => {
    const client = new LedgerClient(['wss://example.invalid']);
    await client.connect();
    client.socket.send = () => { throw new Error('INVALID_STATE_ERR'); };

    await assert.rejects(
      client.send('tx', {}),
      (e) => e.code === 'NodeUnreachable' && /INVALID_STATE_ERR/.test(e.message),
    );
    assert.equal(client.pending.size, 0);
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
    assert.equal(client.socket, null);
    assert.equal(client.connecting, null);
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
    assert.equal(client.socket, null);

    const again = Array.from({ length: 3 }, () => client.request('server_info', {}));
    await turn();
    assert.equal(ManualSocket.created.length, 2);
    ManualSocket.created[1].open();
    await Promise.all(again);

    assert.equal(ManualSocket.created.length, 2);
    client.close();
  });
});
