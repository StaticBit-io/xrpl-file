import { XrplFileError } from './link.mjs';
import { MAINNET_FIRST_LEDGER } from './ledger.mjs';
import { BLOCK_CHUNKS, isManifest, chunkIndexOf, listIndexOf, decodeEntries, rootHex, blockHash, chunkCount } from './tree.mjs';
import { decodeManifest, inconsistency } from './manifest.mjs';
import { hex, unhex, hexUtf8 } from './hex.mjs';
import { ForwardHistory, DEFAULT_MAX_RECORDS } from './history.mjs';

/**
 * Reads a file out of the ledger and verifies every byte against the manifest's root — the
 * .NET XrplFileReader's algorithm, rule for rule.
 *
 * One instance is meant for one read: its block walks share what they took from the node (see
 * ForwardHistory), so reading the blocks in order takes each record once.
 */
export class XrplFileReader {
  /** @param source anything with transaction(hash), accountTransactions(account, from, to, forward), earliestLedger() */
  constructor(source, { maxBytes = 20_000_000, historyCacheRecords = DEFAULT_MAX_RECORDS } = {}) {
    this.source = source;
    this.maxBytes = maxBytes;
    // Shared by the blocks of one read: a block starts in the ledger the block before it ended
    // in, and walking each block afresh read that ledger twice.
    this.history = new ForwardHistory(source, historyCacheRecords);
  }

  async read(link, onProgress = () => {}) {
    const record = await this.manifest(link);
    const manifest = record.manifest;

    if (manifest.size > this.maxBytes) {
      throw new XrplFileError('TooLarge', `the file is ${manifest.size} bytes and this reader stops at ${this.maxBytes}`);
    }

    await this.requireHistoryFrom(manifest.firstLedger);

    const blocks = await this.blockList(record);
    const bytes = new Uint8Array(manifest.size);
    const chunksTotal = chunkCount(manifest.size, manifest.chunk);
    let at = 0;
    let chunksDone = 0;

    for (const block of blocks) {
      const content = await this.block(record, block, () => onProgress({ chunksDone: ++chunksDone, chunksTotal }));
      bytes.set(content, at);
      at += content.length;
    }

    // block() holds every block to the length the manifest implies for it, so the sum can only
    // come out short if the block list was, and that is checked before any block is read.
    if (at !== manifest.size) {
      throw new XrplFileError('BlockMismatch',
        `the blocks assemble to ${at} bytes and the manifest says ${manifest.size}`, { block: blocks.at(-1).i });
    }

    return { manifest, writer: record.writer, blocks, bytes };
  }

  async manifest(link) {
    const tx = await this.source.transaction(link.txHash);

    if (tx === null) {
      const earliest = await this.source.earliestLedger();
      if (earliest === null || earliest > MAINNET_FIRST_LEDGER) {
        throw new XrplFileError('NodeLacksHistory',
          `the node does not know transaction ${link.txHash} and its history starts at ledger ${earliest ?? 'an unknown point'}; a node with full history may still have it`);
      }
      throw new XrplFileError('ManifestNotFound', `no transaction ${link.txHash} on this network`);
    }

    if (!tx.validated || !tx.succeeded) throw new XrplFileError('ManifestNotFound', `transaction ${link.txHash} is not a validated, successful transaction`);
    if (!isManifest(tx.memoTypeHex) || tx.memoDataHex === null) throw new XrplFileError('ManifestNotFound', `transaction ${link.txHash} does not carry an xf/m memo`);

    let manifest;
    try {
      manifest = decodeManifest(hexUtf8(tx.memoDataHex));
    } catch (e) {
      throw new XrplFileError('CorruptManifest', `the manifest in ${link.txHash} is unreadable: ${e.message}`, { cause: e });
    }

    const problem = inconsistency(manifest);
    if (problem) throw new XrplFileError('CorruptManifest', `the manifest in ${link.txHash} is inconsistent: ${problem}`);
    if (manifest.lastLedger >= tx.ledgerIndex) throw new XrplFileError('CorruptManifest', `the manifest in ${link.txHash} names a ledger range that ends after the manifest itself`);

    return { manifest, writer: tx.account, ledgerIndex: tx.ledgerIndex, txHash: tx.hash };
  }

  async blockList(record) {
    const manifest = record.manifest;
    if (manifest.blocks === 1) return [{ i: 0, h: manifest.root, l: manifest.firstLedger }];

    // Backwards from the end of the range, stopping as soon as every index has a value: walking a
    // large file's chunks in reverse just to reach the lists would be far too expensive. That early
    // stop means the rule for block-list memos is "the last copy in canonical (forward) order wins"
    // -- the opposite of chunks, where the first copy wins. It is not only safe but required: a
    // resumed publication lands chunks in new ledgers, so it rewrites the list entries that name
    // them, and the last copy is the one describing the file as it now stands. A stray or malformed
    // copy is still caught below either way: decodeEntries or the root check fails.
    const payloads = new Map();
    for await (const tx of this.source.accountTransactions(record.writer, manifest.firstLedger, manifest.lastLedger, false)) {
      if (!isMine(tx, record.writer, manifest)) continue;
      const index = listIndexOf(tx.memoTypeHex);
      if (index === null || index >= manifest.lists || tx.memoDataHex === null) continue;
      // The first copy this backward walk meets for an index is the last one in canonical order,
      // and that is the one kept -- ignore any earlier (canonical) copy seen after.
      if (!payloads.has(index)) payloads.set(index, tx.memoDataHex);
      if (payloads.size === manifest.lists) break;
    }

    // The collection's completeness is verified here, by index, before decodeEntries is ever
    // called -- payloads.get(i) below must never come back undefined. Relying on decodeEntries
    // (or hexUtf8) to fail on a missing entry would route a plain "index X is missing" outcome
    // through the generic catch below, indistinguishable from an actual corrupt payload.
    //
    // Both walks below stop at what the node actually answered rather than at what the manifest
    // claims: `lists` is a number a stranger wrote, and materialising an index array for it would
    // let a manifest saying `"lists":1000000000` allocate gigabytes in the viewer's tab -- or
    // overflow Array() outright past 2^32 -- before the mismatch below was ever reported.
    if (payloads.size !== manifest.lists) {
      const missing = [];
      let more = false;
      for (let i = 0; i < manifest.lists; i++) {
        if (payloads.has(i)) continue;
        if (missing.length === 8) { more = true; break; }
        missing.push(i);
      }
      throw new XrplFileError('ListMismatch',
        `the manifest promises ${manifest.lists} block-list memos and ${payloads.size} were found -- missing index ${missing.join(', ')}${more ? ', ...' : ''}`);
    }

    let entries;
    try {
      // Past the guard above there are exactly `lists` payloads in hand, so this array is bounded
      // by what the node answered rather than by the manifest's number. hexUtf8 stays inside the
      // try: a memo whose data is not hex is an unreadable list, not a raw error.
      const ordered = [];
      for (let i = 0; i < manifest.lists; i++) ordered.push(hexUtf8(payloads.get(i)));
      entries = decodeEntries(ordered);
    } catch (e) {
      throw new XrplFileError('ListMismatch', `the block list is unreadable: ${e.message}`, { cause: e });
    }

    if (entries.length !== manifest.blocks) throw new XrplFileError('ListMismatch', `the manifest says ${manifest.blocks} blocks and the list has ${entries.length}`);
    const root = await rootHex(entries);
    if (root !== manifest.root) throw new XrplFileError('ListMismatch', `the block list hashes to ${root}, the manifest says ${manifest.root}`);
    return entries;
  }

  async block(record, entry, onChunk = () => {}) {
    const manifest = record.manifest;
    const firstChunk = entry.i * BLOCK_CHUNKS;
    const lastChunk = Math.min(firstChunk + BLOCK_CHUNKS, chunkCount(manifest.size, manifest.chunk)) - 1;
    const count = lastChunk - firstChunk + 1;
    const chunks = new Array(count).fill(null);
    let found = 0;

    for await (const tx of this.history.walk(record.writer, entry.l, manifest.lastLedger)) {
      if (!isMine(tx, record.writer, manifest)) continue;
      const index = chunkIndexOf(tx.memoTypeHex);
      if (index === null || index < firstChunk || index > lastChunk || tx.memoDataHex === null) continue;
      const slot = index - firstChunk;
      if (chunks[slot] !== null) continue;

      let decoded;
      try { decoded = unhex(tx.memoDataHex); } catch { continue; }

      // A memo decoding to more bytes than the manifest's chunk size is not a chunk, the same as
      // one that fails to decode -- accepting it here would let the sum below overflow before the
      // block-hash check ever gets a chance to catch the corruption.
      if (decoded.length > manifest.chunk) continue;

      chunks[slot] = decoded;
      onChunk();
      if (++found === count) break;
    }

    if (found !== count) {
      const missing = chunks.indexOf(null);
      throw new XrplFileError('ChunkMissing', `chunk ${firstChunk + missing} of block ${entry.i} is not in ledgers ${entry.l}..${manifest.lastLedger}`, { block: entry.i });
    }

    // The chunk rule accepts a MemoData shorter than the manifest's chunk size, because the file's
    // last chunk is short. That makes a block's length the manifest's business, not the memos':
    // every chunk but the file's last is exactly `chunk` bytes. A writer that cut its file into
    // short chunks and hashed them honestly produces a block whose hash is right and whose bytes
    // are fewer than promised -- and the zeros a caller's buffer would keep in their place are
    // covered by no hash at all. Checked before the hash, because it is the check that case fails.
    const total = chunks.reduce((sum, c) => sum + c.length, 0);
    const expected = Math.min(manifest.size - firstChunk * manifest.chunk, count * manifest.chunk);
    if (total !== expected) {
      throw new XrplFileError('BlockMismatch',
        `block ${entry.i} assembles to ${total} bytes and the manifest implies ${expected}`, { block: entry.i });
    }

    const h = hex(await blockHash(chunks));
    if (h !== entry.h) throw new XrplFileError('BlockMismatch', `block ${entry.i} hashes to ${h}, the list says ${entry.h}`, { block: entry.i });

    const bytes = new Uint8Array(total);
    let at = 0;
    for (const chunk of chunks) { bytes.set(chunk, at); at += chunk.length; }
    return bytes;
  }

  async requireHistoryFrom(ledger) {
    const earliest = await this.source.earliestLedger();
    if (earliest !== null && earliest > ledger) {
      throw new XrplFileError('NodeLacksHistory', `the file starts in ledger ${ledger} and the node's history starts at ${earliest}; use a node with full history`);
    }
  }
}

function isMine(tx, writer, manifest) {
  return tx.succeeded && tx.validated && tx.account === writer && tx.ledgerIndex >= manifest.firstLedger && tx.ledgerIndex <= manifest.lastLedger;
}
