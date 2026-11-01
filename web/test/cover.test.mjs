import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileReader } from '../src/reader.mjs';
import { parseLink, XrplFileError } from '../src/link.mjs';
import { decodeManifest, encodeManifest } from '../src/manifest.mjs';
import { utf8Hex } from '../src/hex.mjs';
import { vectorFile, vectorLedger, FakeSource, LINK, MANIFEST_HASH, labelHash, smallFileLedger } from './vectors.mjs';
import { COVER_MAX_BYTES, COVER_TYPES, coverMagicAgrees } from '../src/cover.mjs';

const link = parseLink(LINK);

function image(head, size) {
  const bytes = new Uint8Array(size).fill(0x5a);
  bytes.set(head);
  return bytes;
}
const ascii = (text) => [...text].map((c) => c.charCodeAt(0));
const WEBP = image([...ascii('RIFF'), 0x10, 0, 0, 0, ...ascii('WEBP'), ...ascii('VP8 ')], 1500);
const JPEG = image([0xff, 0xd8, 0xff, 0xe0], 1500);

/** The 1001-byte vector (first ledger 1000) with `cover` in its manifest and `extra` added. */
async function decoratedReader({ cover, extra = [], mutate = (source) => source }) {
  const vector = await vectorFile(1001);
  const ledger = await vectorLedger(vector);
  const manifestTx = ledger.find((tx) => tx.hash === MANIFEST_HASH);
  const manifest = { ...decodeManifest(vector.manifest), cover };
  manifestTx.memoDataHex = utf8Hex(encodeManifest(manifest));
  const source = mutate(new FakeSource([...ledger, ...extra]));
  return { source, reader: new XrplFileReader(source) };
}

/** Reads the cover `file` names from the decorated file's manifest. */
async function coverOf({ file, mutate }) {
  const { source, reader } = await decoratedReader({ cover: file.hash, extra: file.transactions, mutate });
  const record = await reader.manifest(link);
  return { source, reader, record, result: await reader.readCover(record) };
}

test('a WebP cover by the writer, below the file, is read and verified', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const { result } = await coverOf({ file: cover });
  assert.deepEqual(result, { bytes: WEBP, type: 'image/webp', txHash: cover.hash });
});

test('a JPEG cover is read as well', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: JPEG, type: 'image/jpeg', firstLedger: 990 });
  const { result } = await coverOf({ file: cover });
  assert.deepEqual(result, { bytes: JPEG, type: 'image/jpeg', txHash: cover.hash });
});

test('the limits are the ones of the format', () => {
  assert.equal(COVER_MAX_BYTES, 65536);
  assert.deepEqual(COVER_TYPES, ['image/webp', 'image/jpeg']);
});

test('a cover by another account is ignored', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990, writer: 'rSomeoneElse' });
  assert.equal((await coverOf({ file: cover })).result, null);
});

test('a cover whose manifest is in the first ledger of the file is ignored, one ledger lower it is read', async () => {
  const at = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 999 });
  assert.equal((await coverOf({ file: at })).result, null);
  const below = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 998 });
  assert.notEqual((await coverOf({ file: below })).result, null);
});

test('a cover of a type or size the format does not allow is ignored', async () => {
  const png = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/png', firstLedger: 990 });
  assert.equal((await coverOf({ file: png })).result, null);

  const big = await smallFileLedger({ label: 'cover', bytes: image(WEBP.subarray(0, 12), COVER_MAX_BYTES + 1), type: 'image/webp', firstLedger: 900 });
  assert.equal((await coverOf({ file: big })).result, null);

  const largest = await smallFileLedger({ label: 'cover', bytes: image(WEBP.subarray(0, 12), COVER_MAX_BYTES), type: 'image/webp', firstLedger: 900 });
  assert.notEqual((await coverOf({ file: largest })).result, null);
});

test('a cover that declares one type and holds the bytes of another is ignored', async () => {
  const lie = await smallFileLedger({ label: 'cover', bytes: JPEG, type: 'image/webp', firstLedger: 990 });
  assert.equal((await coverOf({ file: lie })).result, null);
  const other = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/jpeg', firstLedger: 990 });
  assert.equal((await coverOf({ file: other })).result, null);
});

test('a cover the node does not have is ignored', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const { reader } = await decoratedReader({ cover: labelHash('unknown'), extra: cover.transactions });
  assert.equal(await reader.readCover(await reader.manifest(link)), null);
});

test('a cover that is not an xf/m memo, or whose manifest is corrupt, is ignored', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });

  const notManifest = structuredClone(cover);
  notManifest.transactions.at(-1).memoTypeHex = utf8Hex('xf/x');
  assert.equal((await coverOf({ file: notManifest })).result, null);

  const corrupt = structuredClone(cover);
  corrupt.transactions.at(-1).memoDataHex = utf8Hex('{not json');
  assert.equal((await coverOf({ file: corrupt })).result, null);
});

test('a node that cannot be reached for the cover does not fail the file', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const mutate = (source) => {
    const transaction = source.transaction.bind(source);
    source.transaction = async (hash) => {
      if (hash === cover.hash) throw new XrplFileError('NodeUnreachable', 'fake: the node does not answer');
      return transaction(hash);
    };
    return source;
  };
  assert.equal((await coverOf({ file: cover, mutate })).result, null);
});

test('a chunk of the cover missing from the ledger is ignored', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  cover.transactions.splice(0, 1);
  assert.equal((await coverOf({ file: cover })).result, null);
});

test("a cover's own cover is not followed", async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990, cover: labelHash('third') });
  const { source, reader } = await decoratedReader({ cover: cover.hash, extra: cover.transactions });
  const record = await reader.manifest(link);
  const before = source.transactionCalls;
  assert.notEqual(await reader.readCover(record), null);
  assert.equal(source.transactionCalls - before, 1);
});

test('no cover means no request', async () => {
  const { source, reader } = await decoratedReader({ cover: null });
  const record = await reader.manifest(link);
  assert.equal(record.manifest.cover, null);
  const transactionsBefore = source.transactionCalls;
  const walksBefore = source.accountTransactionsCalls;
  assert.equal(await reader.readCover(record), null);
  assert.equal(source.transactionCalls, transactionsBefore);
  assert.equal(source.accountTransactionsCalls, walksBefore);
});

test("reading a cover leaves the reader's forward history alone", async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const { reader, record, result } = await coverOf({ file: cover });
  assert.notEqual(result, null);
  assert.equal(record.manifest.cover, cover.hash);
  assert.equal(reader.history.account, null);
  assert.equal(reader.history.size, 0);
});

test('the magic of a WebP is RIFF, any four bytes, WEBP; of a JPEG FF D8', () => {
  assert.equal(coverMagicAgrees('image/webp', WEBP), true);
  assert.equal(coverMagicAgrees('image/webp', image([...ascii('RIFF'), 1, 2, 3, 4, ...ascii('WEBX')], 20)), false);
  assert.equal(coverMagicAgrees('image/webp', new Uint8Array(11)), false);
  assert.equal(coverMagicAgrees('image/jpeg', JPEG), true);
  assert.equal(coverMagicAgrees('image/jpeg', image([0xff, 0xd9], 10)), false);
  assert.equal(coverMagicAgrees('image/jpeg', new Uint8Array(1)), false);
  assert.equal(coverMagicAgrees('image/png', WEBP), false);
});
