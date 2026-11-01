import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import { elementError, messageFor, detailFor, canVerify, NO_NODE, BAD_NODE, INSECURE, NO_LINK } from '../src/element/messages.mjs';

test('every reading error has a sentence', () => {
  for (const code of ['InvalidLink', 'ManifestNotFound', 'NodeLacksHistory', 'CorruptManifest', 'ListMismatch', 'BlockMismatch', 'ChunkMissing', 'TooLarge', 'NodeUnreachable'])
    assert.ok(messageFor(new XrplFileError(code, 'x', { block: 3 })).length > 0, code);
});

test('the sentences the spec quotes are exact', () => {
  assert.equal(messageFor(new XrplFileError('BlockMismatch', 'x', { block: 12 })),
    "This file did not pass verification. Block 12 does not match the file's fingerprint. Nothing was shown.");
  assert.equal(messageFor(new XrplFileError('NodeLacksHistory', 'x')), 'The node does not have the history this file needs.');
});

test('the remaining sentences', () => {
  const say = (code) => messageFor(new XrplFileError(code, 'x', { block: 7 }));
  assert.equal(say('InvalidLink'), 'This is not an xrpl-file link.');
  assert.equal(say('ManifestNotFound'), 'This file is not on the ledger.');
  assert.equal(say('CorruptManifest'), "This file's description on the ledger is damaged.");
  assert.equal(say('ListMismatch'), "This file did not pass verification. Its block list does not match the file's fingerprint. Nothing was shown.");
  assert.equal(say('ChunkMissing'), 'Part of this file is missing from the ledger: block 7 is incomplete. Nothing was shown.');
  assert.equal(say('TooLarge'), 'This file is too large to save in this browser.');
  assert.equal(say('NodeUnreachable'), 'The node is not answering. Try again later.');
});

test('a video that is already playing does not claim nothing was shown', () => {
  const mismatch = (opts) => messageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 }), opts);
  const missing = (opts) => messageFor(new XrplFileError('ChunkMissing', 'x', { block: 4 }), opts);
  assert.notEqual(mismatch({ playing: true }), mismatch());
  assert.equal(mismatch({ playing: true }), 'Playback stopped at block 4: it did not pass verification.');
  assert.equal(missing({ playing: true }), 'Playback stopped at block 4: part of it is missing from the ledger.');
  assert.equal(messageFor(new XrplFileError('NodeLacksHistory', 'x'), { playing: true }), 'The node does not have the history this file needs.');
});

test('an element error carries its own sentence', () => {
  assert.equal(messageFor(elementError('NodeUnreachable', NO_NODE)), NO_NODE);
  assert.equal(elementError('NodeUnreachable', NO_NODE).sentence, NO_NODE);
  assert.equal(elementError('NodeUnreachable', NO_NODE).code, 'NodeUnreachable');
});

test('the element sentences are fixed', () => {
  assert.equal(NO_NODE, 'This network has no built-in node. Set the node attribute.');
  assert.equal(BAD_NODE, 'The node attribute must be a wss:// address.');
  assert.equal(INSECURE, 'This page is not served over HTTPS, so the browser cannot check this file.');
  assert.equal(NO_LINK, 'There is no xrpl-file link in this element.');
});

test('an unexpected error never leaks its own text', () => {
  assert.deepEqual(detailFor(new TypeError('boom')), { name: null, message: 'Something went wrong while showing this file.' });
  assert.equal(messageFor(new TypeError('boom')), 'Something went wrong while showing this file.');
  assert.equal(messageFor(null), 'Something went wrong while showing this file.');
});

test('the error event names the reader error and carries the shown sentence', () => {
  assert.deepEqual(detailFor(new XrplFileError('NodeLacksHistory', 'x')),
    { name: 'NodeLacksHistory', message: 'The node does not have the history this file needs.' });
  assert.deepEqual(detailFor(elementError(null, INSECURE)), { name: null, message: INSECURE });
});

test('verification needs a secure context and SubtleCrypto', () => {
  assert.equal(canVerify({ isSecureContext: true, crypto: { subtle: { digest() {} } } }), true);
  assert.equal(canVerify({ isSecureContext: false, crypto: {} }), false);
  assert.equal(canVerify({ isSecureContext: true, crypto: {} }), false);
  assert.equal(canVerify({ isSecureContext: true }), false);
});
