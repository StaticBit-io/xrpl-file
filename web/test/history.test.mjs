import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ForwardHistory } from '../src/history.mjs';
import { chunkIndexOf } from '../src/tree.mjs';
import { vectorFile, vectorLedger, FakeSource, WRITER } from './vectors.mjs';

const vector = await vectorFile(2500500);
const sourceFor = async () => new FakeSource(await vectorLedger(vector));

async function walk(history, from, perRecord = () => {}) {
  const seen = [];
  for await (const tx of history.walk(WRITER, from, vector.ledger.last)) {
    seen.push(tx);
    perRecord();
  }
  return seen;
}

test('what it holds stays under its cap, and a walk from what it let go asks the node again', async () => {
  const source = await sourceFor();
  const history = new ForwardHistory(source, 150);
  let most = 0;

  const whole = await walk(history, vector.ledger.first, () => { most = Math.max(most, history.size); });
  const again = await walk(history, vector.ledger.first);

  assert.ok(most >= 1 && most <= 150, `held ${most}`);
  assert.deepEqual(again, whole);
  assert.equal(source.calls.length, 2);
});

test('a walk inside a history read to its end asks the node nothing', async () => {
  const source = await sourceFor();
  const history = new ForwardHistory(source, 150);
  const whole = await walk(history, vector.ledger.first);
  const late = whole.at(-1).ledgerIndex;

  const tail = await walk(history, late);

  assert.deepEqual(tail, whole.filter((tx) => tx.ledgerIndex >= late));
  assert.equal(source.calls.length, 1);
});

test('a walk starting one past where the last stopped carries it on past the rest of its ledger', async () => {
  const ledger = await vectorLedger(vector);
  const source = new FakeSource(ledger);
  const history = new ForwardHistory(source);
  let stopped = 0;

  for await (const tx of history.walk(WRITER, vector.ledger.first, vector.ledger.last)) {
    // The middle of the vector's second ledger: half of it is still ahead of the walk.
    if (chunkIndexOf(tx.memoTypeHex) === 150) {
      stopped = tx.ledgerIndex;
      break;
    }
  }

  const next = await walk(history, stopped + 1);

  assert.deepEqual(next, ledger.filter((tx) => tx.ledgerIndex >= stopped + 1 && tx.ledgerIndex <= vector.ledger.last));
  assert.equal(source.calls.length, 1);
});

test('another writer or range starts over', async () => {
  const source = await sourceFor();
  const history = new ForwardHistory(source);
  await walk(history, vector.ledger.first);

  for await (const _ of history.walk('rSomebodyElse', vector.ledger.first, vector.ledger.last)) { /* drained */ }
  const shorter = [];
  for await (const tx of history.walk(WRITER, vector.ledger.first, vector.ledger.first + 1)) shorter.push(tx);

  assert.equal(shorter.length, 200);
  assert.equal(source.calls.length, 3);
});

test('a source that refuses the walk as it is asked for leaves nothing behind', async () => {
  const ledger = await vectorLedger(vector);
  const inner = new FakeSource(ledger);
  let refuse = false;
  const source = {
    accountTransactions(...args) {
      if (refuse) { refuse = false; throw new Error('refused'); }
      return inner.accountTransactions(...args);
    },
  };
  const history = new ForwardHistory(source);
  for await (const tx of history.walk(WRITER, vector.ledger.first, vector.ledger.last)) if (chunkIndexOf(tx.memoTypeHex) === 150) break;

  refuse = true;
  await assert.rejects(walk(history, vector.ledger.first + 5), /refused/);
  const after = await walk(history, vector.ledger.first + 5);

  assert.deepEqual(after, ledger.filter((tx) => tx.ledgerIndex >= vector.ledger.first + 5 && tx.ledgerIndex <= vector.ledger.last));
});
