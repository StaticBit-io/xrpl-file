import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileError } from '../src/link.mjs';
import { elementError, messageFor, saveMessageFor, detailFor, shortTitleFor, canVerify, NO_NODE, BAD_NODE, INSECURE, NO_LINK } from '../src/element/messages.mjs';

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
  assert.equal(BAD_NODE, 'The node attribute must hold wss:// addresses, separated by spaces.');
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

test('the error event of a video that was playing carries the playing sentence', () => {
  const error = new XrplFileError('BlockMismatch', 'x', { block: 4 });
  assert.deepEqual(detailFor(error, { playing: true }),
    { name: 'BlockMismatch', message: 'Playback stopped at block 4: it did not pass verification.' });
  assert.deepEqual(detailFor(error, { playing: true }).message, messageFor(error, { playing: true }));
  assert.equal(detailFor(error).message, messageFor(error));
  assert.notEqual(detailFor(error, { playing: true }).message, detailFor(error).message);
});

test('a failed save is worded for a file that is still on screen', () => {
  assert.equal(saveMessageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 })), 'Saving stopped at block 4: it did not pass verification.');
  assert.equal(saveMessageFor(new XrplFileError('ChunkMissing', 'x', { block: 2 })), 'Saving stopped at block 2: part of it is missing from the ledger.');
  assert.equal(saveMessageFor(new XrplFileError('NodeUnreachable', 'x')), 'The node is not answering. Try again later.');
  assert.equal(saveMessageFor(elementError('TooLarge', 'This file is too large to save in this browser.')), 'This file is too large to save in this browser.');
  for (const message of [saveMessageFor(new Error('disk full')), saveMessageFor(new DOMException('no', 'NotFoundError')), saveMessageFor(null)]) {
    assert.equal(message, 'The file could not be saved.');
    assert.ok(!/Nothing was shown/.test(message));
  }
});

test('every reading error has a short title of at most twelve characters', () => {
  const short = (code) => shortTitleFor(new XrplFileError(code, 'x', { block: 3 }));
  assert.equal(short('InvalidLink'), 'Bad link');
  assert.equal(short('ManifestNotFound'), 'Not found');
  assert.equal(short('NodeLacksHistory'), 'No history');
  assert.equal(short('CorruptManifest'), 'Damaged');
  assert.equal(short('ListMismatch'), 'Not verified');
  assert.equal(short('BlockMismatch'), 'Not verified');
  assert.equal(short('ChunkMissing'), 'Incomplete');
  assert.equal(short('TooLarge'), 'Too large');
  assert.equal(short('NodeUnreachable'), 'No answer');
  for (const code of ['InvalidLink', 'ManifestNotFound', 'NodeLacksHistory', 'CorruptManifest', 'ListMismatch', 'BlockMismatch', 'ChunkMissing', 'TooLarge', 'NodeUnreachable'])
    assert.ok(short(code).length <= 12, code);
});

test('the element sentences have their own short titles', () => {
  assert.equal(shortTitleFor(elementError('NodeUnreachable', NO_NODE)), 'No node');
  assert.equal(shortTitleFor(elementError('NodeUnreachable', BAD_NODE)), 'Bad node');
  assert.equal(shortTitleFor(elementError(null, INSECURE)), 'Not secure');
  assert.equal(shortTitleFor(elementError('InvalidLink', NO_LINK)), 'No link');
});

test('an error without a name has the short title Error', () => {
  assert.equal(shortTitleFor(new TypeError('boom')), 'Error');
  assert.equal(shortTitleFor(null), 'Error');
  assert.equal(shortTitleFor(elementError(null, 'This video could not be decoded.')), 'Error');
});
