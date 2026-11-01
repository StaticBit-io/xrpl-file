import { test } from 'node:test';
import assert from 'node:assert/strict';
import { copyText } from '../src/element/clipboard.mjs';

const LINK = 'xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';

function recorder(result) {
  const calls = [];
  const execCopy = (text) => {
    calls.push(text);
    if (result instanceof Error) throw result;
    return result;
  };
  return { calls, execCopy };
}

test('the Clipboard API is used first and execCommand is not touched when it succeeds', async () => {
  const written = [];
  const exec = recorder(true);
  const clipboard = { writeText: async (text) => { written.push(text); } };
  assert.equal(await copyText(LINK, { clipboard, execCopy: exec.execCopy }), true);
  assert.deepEqual(written, [LINK]);
  assert.deepEqual(exec.calls, []);
});

test('a refused Clipboard API falls back to execCommand, as in a cross-origin iframe', async () => {
  const exec = recorder(true);
  const clipboard = { writeText: async () => { throw new DOMException('blocked by a permissions policy', 'NotAllowedError'); } };
  assert.equal(await copyText(LINK, { clipboard, execCopy: exec.execCopy }), true);
  assert.deepEqual(exec.calls, [LINK]);
});

test('without the Clipboard API execCommand is used directly', async () => {
  const exec = recorder(true);
  assert.equal(await copyText(LINK, { clipboard: undefined, execCopy: exec.execCopy }), true);
  assert.equal(await copyText(LINK, { clipboard: {}, execCopy: exec.execCopy }), true);
  assert.deepEqual(exec.calls, [LINK, LINK]);
});

test('when both ways fail the copy is reported as failed', async () => {
  const clipboard = { writeText: async () => { throw new DOMException('denied', 'NotAllowedError'); } };
  assert.equal(await copyText(LINK, { clipboard, execCopy: recorder(false).execCopy }), false);
  assert.equal(await copyText(LINK, { clipboard, execCopy: recorder(new Error('no document')).execCopy }), false);
});

test('only a literal true from execCommand counts as copied', async () => {
  assert.equal(await copyText(LINK, { clipboard: undefined, execCopy: recorder(undefined).execCopy }), false);
  assert.equal(await copyText(LINK, { clipboard: undefined, execCopy: recorder('true').execCopy }), false);
});
