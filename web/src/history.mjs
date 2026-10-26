import { BLOCK_CHUNKS } from './tree.mjs';

/**
 * Four blocks' worth: two testnet Batch ledgers of chunks. A record carries up to 2,000
 * characters of memo hex, so this holds about 17 MB at the most.
 */
export const DEFAULT_MAX_RECORDS = 4 * BLOCK_CHUNKS;

/**
 * One read's forward walks over a writer's history, each taking from the node only what the walk
 * before it did not -- the .NET ForwardHistory, rule for rule.
 *
 * A block is read from its own first ledger, and a block usually starts in the ledger the block
 * before it ended in: on testnet a Batch ledger holds about 2,048 chunks, two blocks. Walking each
 * block afresh read that ledger twice. Here a walk that stops keeps its place, and the next walk
 * replays what this holds from its own first ledger and then carries the stopped walk on, so a
 * file read block by block in order takes each record from the node once.
 *
 * What a walk yields is exactly what a fresh forward accountTransactions from its first ledger
 * would yield, in the same canonical order, so the chunk rule applied to it is unchanged. That
 * rests on validated history not changing between two walks of one read.
 *
 * Memory is bounded by maxRecords: a walk starting further on drops everything before its first
 * ledger, and once more than that many records are held the oldest ledgers are let go whole, and
 * a ledger that alone is larger than the cap is streamed through without being held (spec S26). A
 * walk from a ledger that was let go or not held, from another writer or to another last ledger
 * starts over with a fresh walk. A walk that fails forgets everything.
 *
 * One walk at a time takes part. A walk started while another is in progress goes to the node
 * directly, so callers that read blocks concurrently still get correct, if unshared, walks.
 */
export class ForwardHistory {
  constructor(source, maxRecords = DEFAULT_MAX_RECORDS) {
    this.source = source;
    this.maxRecords = maxRecords;
    // Every record a walk from `from` would yield, in canonical order, up to where the walk has
    // got: to its end when cursor is null and account is set, otherwise to the cursor's place.
    this.held = [];
    this.account = null;
    this.toLedger = 0;
    this.from = 0;
    // A ledger that alone held more than the cap: its records are streamed through and not held.
    this.uncached = -Infinity;
    // The most records held after any record was taken in.
    this.peakHeld = 0;
    // The ledger of the last record the cursor gave; the rest of that ledger may still be ahead.
    this.position = 0;
    this.cursor = null;
    this.walking = false;
  }

  /** How many records it holds now. */
  get size() {
    return this.held.length;
  }

  /** The account's transactions in [fromLedger, toLedger], ascending in canonical order. */
  async *walk(account, fromLedger, toLedger) {
    // A range that ends before it starts is the node's to refuse, as it always was.
    if (fromLedger > toLedger || this.walking) {
      yield* this.source.accountTransactions(account, fromLedger, toLedger, true);
      return;
    }

    this.walking = true;
    try {
      if (!this.covers(account, fromLedger, toLedger)) await this.startOver(account, fromLedger, toLedger);

      const before = this.held.findIndex((tx) => tx.ledgerIndex >= fromLedger);
      this.held.splice(0, before < 0 ? this.held.length : before);
      this.from = fromLedger;

      // Replayed by index: nothing is added to or dropped from held until the replay is done.
      for (let i = 0; i < this.held.length; i++) yield this.held[i];

      while (this.cursor !== null) {
        const tx = await this.next();
        if (tx === null) break;

        this.position = tx.ledgerIndex;

        // Only when this walk starts one past where the last one stopped: the rest of the stopped
        // walk's last ledger is behind this one's first.
        if (tx.ledgerIndex < fromLedger) continue;

        this.hold(tx);
        yield tx;
      }
    } finally {
      this.walking = false;
    }
  }

  covers(account, fromLedger, toLedger) {
    if (this.account !== account || this.toLedger !== toLedger || fromLedger < this.from) return false;
    return this.cursor === null || fromLedger <= this.position + 1;
  }

  async startOver(account, fromLedger, toLedger) {
    const stale = this.cursor;
    this.cursor = null;
    this.account = null;
    this.uncached = -Infinity;
    this.held = [];
    if (stale !== null) await stale.return?.();

    // A source may refuse the walk as it is asked for rather than on the first record; until the
    // cursor exists nothing here may claim to hold anything.
    const cursor = this.source.accountTransactions(account, fromLedger, toLedger, true)[Symbol.asyncIterator]();

    this.cursor = cursor;
    this.account = account;
    this.toLedger = toLedger;
    this.from = fromLedger;
    this.position = fromLedger - 1;
  }

  /** The cursor's next record, or null at the end of its range, when it is let go. */
  async next() {
    let step;
    try {
      step = await this.cursor.next();
    } catch (e) {
      // Where the failed walk stopped inside its last ledger is not known, so nothing it read is
      // kept. An iterator that threw is finished; it is not closed again.
      this.cursor = null;
      this.account = null;
      this.held = [];
      throw e;
    }

    if (!step.done) return step.value;
    this.cursor = null;
    return null;
  }

  hold(tx) {
    if (tx.ledgerIndex !== this.uncached) {
      this.held.push(tx);
      this.letGo();
    }
    this.peakHeld = Math.max(this.peakHeld, this.held.length);
  }

  /**
   * Keeps what is held within the cap (spec S26), as the .NET ForwardHistory does: the oldest ledgers
   * go first, whole; a ledger that alone is larger than the cap is not held at all, and `from` moves
   * past it, so a later walk from it asks the node again instead of getting a ledger with records missing.
   */
  letGo() {
    while (this.held.length > this.maxRecords && this.held[0].ledgerIndex < this.position) {
      const oldest = this.held[0].ledgerIndex;
      const end = this.held.findIndex((tx) => tx.ledgerIndex !== oldest);
      this.held.splice(0, end);
      this.from = oldest + 1;
    }

    if (this.held.length > this.maxRecords) {
      this.uncached = this.position;
      this.held = [];
      this.from = this.position + 1;
    }
  }
}
