import { test } from 'node:test';
import assert from 'node:assert/strict';
import { XrplFileReader } from '../src/reader.mjs';
import { parseLink } from '../src/link.mjs';
import { vectorFile, vectorLedger, vectorFileBytes, FakeSource, LINK, WRITER, labelHash, MANIFEST_HASH } from './vectors.mjs';
import {
  chunkIndexOf, listIndexOf, isManifest, listMarkerHex, chunkMarkerHex, split, blockHash,
  MANIFEST_MARKER_HEX, JSON_FORMAT_HEX,
} from '../src/tree.mjs';
import { encodeManifest } from '../src/manifest.mjs';
import { hex, utf8Hex, hexUtf8 } from '../src/hex.mjs';
import { MAINNET_FIRST_LEDGER, LedgerClient } from '../src/ledger.mjs';

const link = parseLink(LINK);
const sourceFor = async (size) => new FakeSource(await vectorLedger(await vectorFile(size)));
const rejectsWith = (promise, code, block) =>
  assert.rejects(promise, (e) => e.code === code && (block === undefined || e.block === block));

for (const size of [1, 1000, 1001, 1024001, 2500500]) {
  test(`a ${size}-byte vector reads back byte for byte`, async () => {
    const vector = await vectorFile(size);
    const result = await new XrplFileReader(await sourceFor(size)).read(link);
    assert.deepEqual(result.bytes, await vectorFileBytes(vector.seed, vector.size));
    assert.equal(result.manifest.root, vector.root);
    assert.equal(result.writer, WRITER);
    assert.equal(result.blocks.length, vector.blocks);
  });
}

test('a single block file needs no list read', async () => {
  const source = await sourceFor(1001);
  const reader = new XrplFileReader(source);
  const record = await reader.manifest(link);
  const blocks = await reader.blockList(record);
  assert.deepEqual(blocks, [{ i: 0, h: record.manifest.root, l: record.manifest.firstLedger }]);
  assert.equal(source.accountTransactionsCalls, 0);
});

test('a duplicate index keeps the earlier copy', async () => {
  // 1024001 bytes: block 0 spans ledgers 1000..1010 and needs all 1024 of its chunks before the
  // scan can stop, so a duplicate placed at ledger 1005 -- strictly inside that range -- is one
  // the reader must actually pass through and skip, not one it never reaches because the block
  // already completed on the file's first couple of chunks (as the 1001-byte vector would let it).
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 0);
  const duplicate = { ...real, hash: labelHash('retry'), ledgerIndex: 1005, memoDataHex: 'F'.repeat(real.memoDataHex.length) };
  const source = new FakeSource([...ledger, duplicate]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorFileBytes(vector.seed, vector.size));
});

test('a wrong copy earlier in canonical order than the real one wins and breaks the hash', async () => {
  // Proves the rule really is "first wins", not "the right one wins": if the wrong copy sits
  // earlier in canonical order than the real one, the reader must keep the wrong bytes and fail
  // the block hash -- not silently prefer the correct copy because it happens to exist.
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 500);
  // The real copy of chunk 500 is at ledger 1005; the wrong copy is at ledger 1002, earlier in
  // canonical order, still strictly inside block 0's range (1000..1010).
  const wrongButEarlier = { ...real, hash: labelHash('wrong-but-earlier'), ledgerIndex: 1002, memoDataHex: 'F'.repeat(real.memoDataHex.length) };
  const source = new FakeSource([...ledger, wrongButEarlier]);
  await rejectsWith(new XrplFileReader(source).read(link), 'BlockMismatch', 0);
});

test('a corrupt chunk is reported with its block', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (chunkIndexOf(tx.memoTypeHex) === 1500 ? { ...tx, memoDataHex: 'F'.repeat(2000) } : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'BlockMismatch', 1);
});

test('a missing chunk is reported with its block', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.filter((tx) => chunkIndexOf(tx.memoTypeHex) !== 2400);
  await rejectsWith(new XrplFileReader(source).read(link), 'ChunkMissing', 2);
});

test('an oversized chunk is skipped so a later real copy still wins', async () => {
  // manifest.chunk is 1000 for this vector; a memo decoding to 2000 bytes is well over that and
  // must be treated like a non-hex memo -- skipped, not accepted into the slot.
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 0);
  const withOversizedFirstCopy = ledger.map((tx) => (tx === real ? { ...tx, memoDataHex: 'A'.repeat(4000) } : tx));
  // The good copy, later in canonical order, still inside block 0's range (1000..1010).
  const source = new FakeSource([...withOversizedFirstCopy, { ...real, hash: labelHash('good-later'), ledgerIndex: 1005 }]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorFileBytes(vector.seed, vector.size));
});

test('an oversized chunk with no other copy is reported as missing', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (chunkIndexOf(tx.memoTypeHex) === 0 ? { ...tx, memoDataHex: 'A'.repeat(4000) } : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'ChunkMissing', 0);
});

test('a failed, foreign or unmarked transaction is not a chunk', async () => {
  // Same 1024001-byte vector and ledger-1005 placement as above, and for the same reason: block 0
  // needs all its chunks before the scan can stop, so these injected transactions are met mid-block.
  const vector = await vectorFile(1024001);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 500);
  const source = new FakeSource([
    ...ledger,
    { ...real, hash: labelHash('failed'), ledgerIndex: 1005, succeeded: false, memoDataHex: '00' },
    { ...real, hash: labelHash('stranger'), ledgerIndex: 1005, account: 'rSomebodyElse', memoDataHex: '00' },
    { ...real, hash: labelHash('unmarked'), ledgerIndex: 1005, memoTypeHex: null, memoDataHex: '00' },
  ]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorFileBytes(vector.seed, vector.size));
});

test('a list that does not match the root is refused before any chunk', async () => {
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (listIndexOf(tx.memoTypeHex) === 0
    ? { ...tx, memoDataHex: utf8Hex(hexUtf8(tx.memoDataHex).replaceAll('A', 'B')) }
    : tx));
  await rejectsWith(new XrplFileReader(source).read(link), 'ListMismatch');
});

test('a stray out-of-range list memo is a list mismatch naming the missing index, not a crash', async () => {
  // The manifest promises a fixed number of list memos indexed 0..lists-1. Re-marking the single
  // list memo xf/b/7 leaves the account with a stray out-of-range index and none of the indices
  // the manifest actually promises. The message is checked, not just the code: without the
  // `index >= manifest.lists` range filter in blockList(), this same scenario reaches
  // decodeEntries with `payloads.get(0)` undefined, and the untyped catch there turns that crash
  // into a ListMismatch too -- with a generic "unreadable" message, not one naming index 0. Only
  // a message assertion tells the two apart; see the fix-round report for the before/after runs.
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (listIndexOf(tx.memoTypeHex) === 0 ? { ...tx, memoTypeHex: listMarkerHex(7) } : tx));
  await assert.rejects(
    new XrplFileReader(source).read(link),
    (e) => e.code === 'ListMismatch' && /\bmissing index 0\b/.test(e.message),
  );
});

test('a duplicate list index keeps the later copy in canonical order', async () => {
  // The 2500500-byte vector's single list memo (xf/b/0) sits at ledger 1026, after every chunk.
  // A wrong-but-valid copy planted earlier in canonical order must lose to it: the block-list
  // rule keeps the LAST copy, the opposite of the chunk rule tested above.
  const vector = await vectorFile(2500500);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => listIndexOf(tx.memoTypeHex) === 0);
  // Ledger 1010 is inside block 1's range and strictly earlier than the real copy's 1026, so this
  // loses under "last wins" without the backward walk ever needing to reach it.
  const wrongButEarlier = {
    ...real,
    hash: labelHash('list-wrong-but-earlier'),
    ledgerIndex: 1010,
    memoDataHex: utf8Hex(hexUtf8(real.memoDataHex).replaceAll('A', 'B')),
  };
  const source = new FakeSource([...ledger, wrongButEarlier]);
  assert.deepEqual((await new XrplFileReader(source).read(link)).bytes, await vectorFileBytes(vector.seed, vector.size));
});

test('a wrong copy later in canonical order than the real list memo wins and breaks the root', async () => {
  // Proves the rule really is "last wins", not "the right one wins": a wrong-but-valid copy
  // planted after the real one, at the same ledger, must be the one used -- producing
  // ListMismatch, not silently preferring the correct copy because it happens to exist.
  const vector = await vectorFile(2500500);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => listIndexOf(tx.memoTypeHex) === 0);
  const wrongButLater = {
    ...real,
    hash: labelHash('list-wrong-but-later'),
    memoDataHex: utf8Hex(hexUtf8(real.memoDataHex).replaceAll('A', 'B')),
  };
  const source = new FakeSource([...ledger, wrongButLater]);
  await rejectsWith(new XrplFileReader(source).read(link), 'ListMismatch');
});

test('an unknown manifest is not found on a full-history node and a history problem on a partial one', async () => {
  await rejectsWith(new XrplFileReader(new FakeSource([], MAINNET_FIRST_LEDGER)).manifest(link), 'ManifestNotFound');
  await rejectsWith(new XrplFileReader(new FakeSource([], 100_000_000)).manifest(link), 'NodeLacksHistory');
});

test('a node whose history starts after the first chunk cannot read the file', async () => {
  const source = await sourceFor(1001);
  source.earliest = 1001;
  await rejectsWith(new XrplFileReader(source).read(link), 'NodeLacksHistory');
});

test('a manifest whose counts disagree is corrupt, and a non-manifest is not found', async () => {
  const corrupt = await sourceFor(1001);
  corrupt.transactions = corrupt.transactions.map((tx) => (isManifest(tx.memoTypeHex) ? { ...tx, memoDataHex: utf8Hex('not json') } : tx));
  await rejectsWith(new XrplFileReader(corrupt).manifest(link), 'CorruptManifest');

  const other = await sourceFor(1001);
  other.transactions = other.transactions.map((tx) => (isManifest(tx.memoTypeHex) ? { ...tx, memoTypeHex: utf8Hex('xf/c/5') } : tx));
  await rejectsWith(new XrplFileReader(other).manifest(link), 'ManifestNotFound');
});

test('chunks shorter than the manifest implies are a block mismatch, not zero padding', async () => {
  // The chunk rule accepts a MemoData shorter than `chunk`, so a writer can cut a 2500-byte file
  // into five 500-byte chunks, hash them honestly and claim size 5000: the block hash matches, the
  // root matches, every count agrees. Reading that used to return 5000 bytes of which 2500 were
  // zeros no hash covered -- the only path on which unverified bytes reached a caller. The .NET
  // reader is held to the same rule.
  const real = await vectorFileBytes('short', 2500);
  const chunks = split(real, 500);
  const manifest = {
    type: 'application/octet-stream', size: 5000, chunk: 1000, root: hex(await blockHash(chunks)),
    firstLedger: 1000, lastLedger: 1000, blocks: 1, lists: 0, name: null,
  };

  const ledger = chunks.map((chunk, i) => ({
    hash: labelHash(`short:${i}`), ledgerIndex: 1000, account: WRITER, succeeded: true, validated: true,
    memoTypeHex: chunkMarkerHex(i), memoDataHex: hex(chunk), memoFormatHex: null,
  }));
  ledger.push({
    hash: MANIFEST_HASH, ledgerIndex: 1001, account: WRITER, succeeded: true, validated: true,
    memoTypeHex: MANIFEST_MARKER_HEX, memoDataHex: utf8Hex(encodeManifest(manifest)), memoFormatHex: JSON_FORMAT_HEX,
  });

  await assert.rejects(
    new XrplFileReader(new FakeSource(ledger)).read(link),
    (e) => e.code === 'BlockMismatch' && e.block === 0 && /2500 bytes and the manifest implies 5000/.test(e.message),
  );
});

test('a manifest promising a billion block lists is a mismatch, not a billion-element array', async () => {
  // The manifest's counts are numbers a stranger wrote. With `lists` unbounded, blockList()
  // materialised [...Array(manifest.lists).keys()] to name the missing indices -- eight gigabytes
  // in the viewer's tab for this manifest -- before it could report the mismatch. The walk now
  // stops at the first few missing indices, so this test finishes in milliseconds; without the
  // fix it exhausts memory instead of failing an assertion.
  const source = await sourceFor(2500500);
  source.transactions = source.transactions.map((tx) => (isManifest(tx.memoTypeHex)
    ? { ...tx, memoDataHex: utf8Hex(hexUtf8(tx.memoDataHex).replace('"lists":1', '"lists":1000000000')) }
    : tx));

  const started = Date.now();
  await assert.rejects(
    new XrplFileReader(source).read(link),
    (e) => e.code === 'ListMismatch' && /missing index 1, 2, 3, 4, 5, 6, 7, 8, \.\.\./.test(e.message),
  );
  assert.ok(Date.now() - started < 5000, 'the refusal must not walk a billion indices');
});

test('a list memo whose entries are not entries is a list mismatch', async () => {
  // A null entry used to raise a TypeError inside decodeEntries, and an entry with no index or no
  // ledger used to be accepted with whatever the .NET decoder defaulted them to. Both are
  // ListMismatch on both sides now.
  for (const payload of ['[null]', `[{"i":0,"h":"${'0'.repeat(64)}"}]`, `[{"h":"${'0'.repeat(64)}","l":1000}]`]) {
    const source = await sourceFor(2500500);
    source.transactions = source.transactions.map((tx) => (listIndexOf(tx.memoTypeHex) === 0 ? { ...tx, memoDataHex: utf8Hex(payload) } : tx));
    await rejectsWith(new XrplFileReader(source).read(link), 'ListMismatch');
  }
});

test('a file above the ceiling is refused before any chunk is read', async () => {
  const source = await sourceFor(2500500);
  await rejectsWith(new XrplFileReader(source, { maxBytes: 2_000_000 }).read(link), 'TooLarge');
  assert.equal(source.accountTransactionsCalls, 0);
});

test('progress counts chunks as they land', async () => {
  const seen = [];
  await new XrplFileReader(await sourceFor(2500500)).read(link, (p) => seen.push(p));
  assert.equal(seen.at(-1).chunksDone, 2501);
  assert.equal(seen.at(-1).chunksTotal, 2501);
});

test("a block is read from its own ledger, not the file's first", async () => {
  const vector = await vectorFile(2500500);
  const source = await sourceFor(2500500);
  const reader = new XrplFileReader(source);
  const record = await reader.manifest(link);
  const blocks = await reader.blockList(record);

  const block = await reader.block(record, blocks[2]);

  assert.deepEqual(block, (await vectorFileBytes(vector.seed, vector.size)).subarray(2048 * 1000));
  // The discriminating check: block() must page from the block's own first ledger, not the
  // file's -- breaking chunks by index alone would prove nothing, since block() filters chunks
  // by index regardless of where the scan starts.
  const forwardCalls = source.calls.filter((call) => call.forward);
  assert.equal(forwardCalls.length, 1);
  assert.equal(forwardCalls[0].fromLedger, blocks[2].l);
});

test('close() during a pending handshake leaves no socket behind and rejects the request', async () => {
  // A minimal stub WebSocket whose handshake never completes on its own: the point is to prove
  // that close() -- not a server answer -- is what settles a connection stuck mid-handshake.
  const created = [];
  class NeverOpens {
    constructor(url) {
      this.url = url;
      this.readyState = 0; // CONNECTING
      this.closeCalls = 0;
      created.push(this);
    }

    close() {
      this.closeCalls++;
      this.readyState = 3; // CLOSED
    }

    send() {}
  }
  NeverOpens.OPEN = 1;

  const original = globalThis.WebSocket;
  globalThis.WebSocket = NeverOpens;
  try {
    const client = new LedgerClient(['wss://example.invalid']);
    const pending = client.request('server_info', {});

    // The socket is created synchronously inside connect()'s Promise executor, so by the time
    // client.request() returns control here, the handshake is already in flight and closing now
    // exercises the exact race the fix targets.
    client.close();

    await assert.rejects(pending, (e) => e.code === 'NodeUnreachable');
    assert.equal(client.socket, null);
    assert.equal(client.pendingSocket, null);
    assert.equal(client.pending.size, 0);
    assert.equal(created.length, 1);
    assert.equal(created[0].closeCalls, 1);
  } finally {
    globalThis.WebSocket = original;
  }
});

// The 2,500,500-byte vector puts 100 chunks in a ledger, so block 1 starts in the ledger that
// holds chunks 1000..1099, which block 0 ends in, and block 2 in the one holding 2000..2099. A
// reader that walks each block from its own first ledger reads those two ledgers twice.
async function open(reader) {
  const record = await reader.manifest(link);
  return { record, blocks: await reader.blockList(record) };
}

async function blockBytes(vector, block) {
  const all = await vectorFileBytes(vector.seed, vector.size);
  return all.subarray(block * 1024 * vector.chunk, (block + 1) * 1024 * vector.chunk);
}

test('blocks sharing a ledger take each record from the node once', async () => {
  const vector = await vectorFile(2500500);
  const source = await sourceFor(2500500);
  const reader = new XrplFileReader(source);
  const { record, blocks } = await open(reader);
  assert.equal(blocks[1].l, blocks[0].l + 10);
  const listCalls = source.calls.length;
  const listRecords = source.yielded;

  for (const [i, block] of blocks.entries()) assert.deepEqual(await reader.block(record, block), await blockBytes(vector, i));

  assert.equal(source.yielded - listRecords, vector.chunks);
  const walks = source.calls.slice(listCalls);
  assert.equal(walks.length, 1);
  assert.equal(walks[0].fromLedger, blocks[0].l);
});

test('a shared ledger whose next block comes first is replayed, not read again', async () => {
  // Canonical order inside a ledger is not index order: here block 1's chunks come before block
  // 0's last ones, so block 0 reads to the shared ledger's end and block 1's chunks in it are
  // already behind the walk when block 1 starts.
  const vector = await vectorFile(2500500);
  const ledger = await vectorLedger(vector);
  const shared = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 1000).ledgerIndex;
  const sharedChunks = ledger.filter((tx) => tx.ledgerIndex === shared);
  const source = new FakeSource([
    ...ledger.filter((tx) => tx.ledgerIndex !== shared),
    ...sharedChunks.filter((tx) => chunkIndexOf(tx.memoTypeHex) >= 1024),
    ...sharedChunks.filter((tx) => chunkIndexOf(tx.memoTypeHex) < 1024),
  ]);
  const reader = new XrplFileReader(source);
  const { record, blocks } = await open(reader);
  const listCalls = source.calls.length;
  const listRecords = source.yielded;

  assert.deepEqual(await reader.block(record, blocks[0]), await blockBytes(vector, 0));
  assert.deepEqual(await reader.block(record, blocks[1]), await blockBytes(vector, 1));

  assert.equal(source.calls.length - listCalls, 1);
  // Block 0 reads 1,100 records, the whole shared ledger included; block 1 takes the 76 of its
  // own that ledger holds from what block 0 read and the other 948 from the node.
  assert.equal(source.yielded - listRecords, 1100 + 948);
});

test('a wrong copy in a shared ledger still wins over the real one after it', async () => {
  // The first copy in canonical order is the chunk, whether the walk meets it from the node or
  // among what the block before it read. Here the wrong copy of chunk 1024 is the shared ledger's
  // first record, read by block 0's walk, which has no use for it.
  const ledger = await vectorLedger(await vectorFile(2500500));
  const at = ledger.findIndex((tx) => chunkIndexOf(tx.memoTypeHex) === 1000);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 1024);
  ledger.splice(at, 0, { ...real, hash: labelHash('wrong:1024'), memoDataHex: 'F'.repeat(real.memoDataHex.length) });

  await rejectsWith(new XrplFileReader(new FakeSource(ledger)).read(link), 'BlockMismatch', 1);
});

test("a copy before a block's first ledger is not its chunk, even when an earlier block read it", async () => {
  // Block 0's walk passes this copy of chunk 1024; block 1's range starts after it, so a fresh
  // walk for block 1 never meets it, and neither may what block 1 takes from block 0.
  const vector = await vectorFile(2500500);
  const ledger = await vectorLedger(vector);
  const real = ledger.find((tx) => chunkIndexOf(tx.memoTypeHex) === 1024);
  const at = ledger.findIndex((tx) => chunkIndexOf(tx.memoTypeHex) === 500);
  ledger.splice(at, 0, { ...real, hash: labelHash('early:1024'), ledgerIndex: ledger[at].ledgerIndex, memoDataHex: 'F'.repeat(real.memoDataHex.length) });

  assert.deepEqual((await new XrplFileReader(new FakeSource(ledger)).read(link)).bytes, await vectorFileBytes(vector.seed, vector.size));
});

test("a walk the node stopped answering is taken again from the block's own ledger", async () => {
  const vector = await vectorFile(2500500);
  const source = await sourceFor(2500500);
  const reader = new XrplFileReader(source);
  const { record, blocks } = await open(reader);
  const listCalls = source.calls.length;

  // Block 0 takes 1,024 records of the walk; carried on into block 1, it fails 500 later.
  source.failAfter = 1524;
  await reader.block(record, blocks[0]);
  await rejectsWith(reader.block(record, blocks[1]), 'NodeUnreachable');
  const retried = await reader.block(record, blocks[1]);

  assert.deepEqual(retried, await blockBytes(vector, 1));
  assert.deepEqual(source.calls.slice(listCalls).map((call) => call.fromLedger), [blocks[0].l, blocks[1].l]);
});

test('blocks read at the same time each come out whole', async () => {
  const vector = await vectorFile(2500500);
  const reader = new XrplFileReader(await sourceFor(2500500));
  const { record, blocks } = await open(reader);

  const read = await Promise.all(blocks.map((block) => reader.block(record, block)));

  for (const [i, bytes] of read.entries()) assert.deepEqual(bytes, await blockBytes(vector, i));
});
