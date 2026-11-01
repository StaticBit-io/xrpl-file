// The page-level layer under the <xrpl-file> element (design spec section 2.7): one LedgerClient
// per node set behind a reference count, transactions shared between the elements that ask for
// them, a priority queue of block reads, and per-link shared reads.
//
// None of it is a change to the reader: XrplFileReader#block(record, entry, onChunk) already
// reads one block by its block-list entry, and this layer decides which reader instance does it.
import { LedgerClient } from '../ledger.mjs';
import { XrplFileReader } from '../reader.mjs';
import { formatLink } from '../link.mjs';
import { BLOCK_CHUNKS, chunkCount } from '../tree.mjs';

export const PRIORITY = Object.freeze({ NOW: 0, AHEAD: 1, BACKGROUND: 2 });

/**
 * A page-layer reader's history cache: one block's worth, about 4 MB, against the reader's own
 * default of four blocks. A page may hold several files, and each keeps one such cache.
 */
export const PAGE_HISTORY_RECORDS = 1024;

/** Verified blocks a link keeps for its handles: a seek back into recent blocks needs no read. */
const VERIFIED_BLOCKS = 4;

const NEVER_ABORTED = new AbortController().signal;

function abortError(message = 'The operation was aborted.') {
  return new DOMException(message, 'AbortError');
}

/**
 * The promise, rejecting as soon as the signal aborts. The promise itself is left running: it is
 * shared with other callers, and one caller's abort is not theirs.
 */
function raceAbort(promise, signal) {
  if (!signal) return promise;
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(abortError()); return; }
    const onAbort = () => reject(abortError());
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then(
      (value) => { signal.removeEventListener('abort', onAbort); resolve(value); },
      (error) => { signal.removeEventListener('abort', onAbort); reject(error); });
  });
}

/**
 * Runs tasks, at most `limit` at a time, the lowest priority value first and in submission order
 * among equals. One slot beyond the limit is kept for NOW tasks: a seek never waits behind
 * read-ahead that is already running.
 */
export class ReadQueue {
  #limit;
  #active = 0;
  #waiting = [];
  #sequence = 0;

  constructor(limit = 3) {
    if (!Number.isInteger(limit) || limit < 1) throw new RangeError('a read queue runs at least one task at a time');
    this.#limit = limit;
  }

  get active() { return this.#active; }

  get waiting() { return this.#waiting.length; }

  /**
   * @param task called with the signal (a never-aborted one when none was given) when it starts
   * @returns the task's result; rejects with an AbortError when the signal aborts while queued
   */
  run(task, { priority = PRIORITY.BACKGROUND, signal } = {}) {
    if (signal?.aborted) return Promise.reject(abortError());

    return new Promise((resolve, reject) => {
      const job = { task, priority, order: this.#sequence++, signal, resolve, reject, onAbort: null };

      if (signal) {
        job.onAbort = () => {
          const at = this.#waiting.indexOf(job);
          if (at >= 0) this.#waiting.splice(at, 1);
          reject(abortError());
        };
        signal.addEventListener('abort', job.onAbort, { once: true });
      }

      // After the last job of the same or a lower priority value: first in, first out among equals.
      let at = this.#waiting.length;
      while (at > 0 && this.#waiting[at - 1].priority > priority) at--;
      this.#waiting.splice(at, 0, job);
      this.#pump();
    });
  }

  #pump() {
    while (this.#waiting.length > 0) {
      const next = this.#waiting[0];
      const capacity = next.priority === PRIORITY.NOW ? this.#limit + 1 : this.#limit;
      if (this.#active >= capacity) return;
      this.#waiting.shift();
      this.#start(next);
    }
  }

  #start(job) {
    if (job.signal) job.signal.removeEventListener('abort', job.onAbort);
    this.#active++;

    let result;
    try {
      result = Promise.resolve(job.task(job.signal ?? NEVER_ABORTED));
    } catch (e) {
      result = Promise.reject(e);
    }

    // The slot is given back before the caller hears the result, so whoever continues from it
    // sees the queue as it stands once this task is done.
    result.then(
      (value) => { this.#active--; this.#pump(); job.resolve(value); },
      (error) => { this.#active--; this.#pump(); job.reject(error); });
  }
}

/**
 * A source whose account_tx walk ends between pages once the signal aborts: the walk checks it
 * before handing on each record, closes the underlying iterator and throws an AbortError, so an
 * aborted read stops with at most the page in flight.
 */
export class AbortableSource {
  constructor(source, signal) {
    this.source = source;
    this.signal = signal;
  }

  transaction(hash) { return this.source.transaction(hash); }

  earliestLedger() { return this.source.earliestLedger(); }

  async *accountTransactions(account, fromLedger, toLedger, forward) {
    if (this.signal.aborted) throw abortError();

    const inner = this.source.accountTransactions(account, fromLedger, toLedger, forward)[Symbol.asyncIterator]();
    let finished = false;
    try {
      for (;;) {
        let step;
        try {
          step = await inner.next();
        } catch (e) {
          // An iterator that threw is finished; it is not closed again.
          finished = true;
          throw e;
        }
        if (step.done) { finished = true; return; }
        if (this.signal.aborted) throw abortError();
        yield step.value;
      }
    } finally {
      if (!finished) await inner.return?.();
    }
  }
}

/**
 * A client behind the page: one request per transaction hash in flight, and a validated answer
 * kept for the page's life (a validated transaction does not change). A transaction the node
 * does not know, or knows unvalidated, is asked for again next time.
 */
export class SharedSource {
  #client;
  #inflight = new Map();
  #validated = new Map();
  #earliestRequest = null;

  constructor(client) {
    this.#client = client;
  }

  transaction(hash) {
    const key = String(hash).toUpperCase();
    const known = this.#validated.get(key);
    if (known !== undefined) return Promise.resolve(known);

    const inflight = this.#inflight.get(key);
    if (inflight !== undefined) return inflight;

    // The executor runs now, so a client that throws at once still becomes a rejection; the
    // handlers run later, by which time `request` is assigned.
    const request = new Promise((resolve) => resolve(this.#client.transaction(hash))).then(
      (tx) => {
        this.#settled(key, request);
        if (tx?.validated === true) this.#validated.set(key, tx);
        return tx;
      },
      (error) => {
        this.#settled(key, request);
        throw error;
      });
    this.#inflight.set(key, request);
    return request;
  }

  #settled(key, request) {
    if (this.#inflight.get(key) === request) this.#inflight.delete(key);
  }

  earliestLedger() {
    if (this.#earliestRequest !== null) return this.#earliestRequest;

    // History grows as a node syncs, so only a request in flight is shared, never an answer.
    const request = new Promise((resolve) => resolve(this.#client.earliestLedger())).then(
      (ledger) => { if (this.#earliestRequest === request) this.#earliestRequest = null; return ledger; },
      (error) => { if (this.#earliestRequest === request) this.#earliestRequest = null; throw error; });
    this.#earliestRequest = request;
    return request;
  }

  accountTransactions(account, fromLedger, toLedger, forward) {
    return this.#client.accountTransactions(account, fromLedger, toLedger, forward);
  }
}

/** One in-flight read of one block, shared by every consumer that asked for it. */
class SharedRead {
  constructor(priority, sequential) {
    this.priority = priority;
    this.controller = new AbortController();
    this.listeners = new Set();
    // Every chunk the read has taken so far, replayed to a consumer that joins late.
    this.chunks = 0;
    // Consumers that have not aborted; the read is aborted when this reaches zero.
    this.live = 0;
    this.started = false;
    // Only a read every consumer wants in order runs through the shared reader. A queue may start
    // the read the moment it is enqueued, so the first consumer's wish is settled here, and a
    // later one can only narrow it while the read still waits.
    this.sequential = sequential;
    // One queue entry per priority the read was asked for at; the first to start cancels the rest.
    this.tickets = new Set();
    this.promise = new Promise((resolve, reject) => {
      this.resolve = resolve;
      this.reject = reject;
    });
  }

  emitChunk = () => {
    this.chunks++;
    for (const listener of this.listeners) callProgress(listener);
  };
}

/** A consumer's progress callback must not fail a read that other consumers share. */
function callProgress(listener) {
  try { listener(); } catch { /* the callback's own problem */ }
}

/** What every handle of one link shares: the manifest, the block list, the reads and the blocks verified. */
class LinkState {
  constructor(registry, key, link) {
    this.registry = registry;
    this.key = key;
    this.link = link;
    this.refs = 0;
    this.recordPromise = null;
    this.loadedPromise = null;
    this.coverPromise = null;
    this.reads = new Map();
    this.verified = new Map();
    this.historyKey = null;
    this.disposed = false;
  }

  /** Runs `create` once; a failure is not kept, so the next caller asks the node again. */
  #once(field, create) {
    if (this[field] === null) {
      const promise = create().catch((e) => {
        if (this[field] === promise) this[field] = null;
        throw e;
      });
      this[field] = promise;
    }
    return this[field];
  }

  record() {
    return this.#once('recordPromise', () => this.registry.reader.manifest(this.link));
  }

  /** The manifest record and the block list, after the history check of format section 6 step 4. */
  loaded() {
    return this.#once('loadedPromise', async () => {
      const record = await this.record();
      await this.registry.reader.requireHistoryFrom(record.manifest.firstLedger);
      const list = await this.registry.reader.blockList(record);
      return { record, list };
    });
  }

  cover() {
    return this.#once('coverPromise', async () => this.registry.reader.readCover(await this.record()));
  }

  async block(index, { priority = PRIORITY.BACKGROUND, signal, onChunk, sequential = false }) {
    if (signal?.aborted) throw abortError();
    const { record, list } = await raceAbort(this.loaded(), signal);
    // The last handle went while the manifest was still coming: nobody is left to read for.
    if (this.disposed) throw abortError('this file was released');

    const entry = list[index];
    if (entry === undefined) throw new RangeError(`block ${index} is not in a file of ${list.length} blocks`);

    // From here to the end of the synchronous part nothing awaits: a verified block, a read in
    // flight and a new read are told apart without another caller slipping in between.
    const verified = this.verified.get(index);
    if (verified !== undefined) {
      // Most recent last, so the four kept are the four used last.
      this.verified.delete(index);
      this.verified.set(index, verified);
      if (onChunk) for (let i = 0; i < blockChunks(record, index); i++) callProgress(onChunk);
      return verified.slice();
    }

    let read = this.reads.get(index);
    if (read === undefined) {
      read = new SharedRead(priority, sequential);
      this.reads.set(index, read);
      this.#enqueue(read, index, record, entry, priority);
    } else if (!read.started && priority < read.priority) {
      read.priority = priority;
      this.#enqueue(read, index, record, entry, priority);
    }

    return this.#attach(read, { signal, onChunk, sequential });
  }

  #attach(read, { signal, onChunk, sequential }) {
    read.sequential &&= sequential;
    read.live++;

    // Its own function, so one callback passed by two consumers is two listeners.
    const listener = onChunk ? () => onChunk() : null;
    if (listener) {
      for (let i = 0; i < read.chunks; i++) callProgress(listener);
      read.listeners.add(listener);
    }

    return new Promise((resolve, reject) => {
      const leave = () => {
        signal?.removeEventListener('abort', onAbort);
        if (listener) read.listeners.delete(listener);
      };
      const onAbort = () => {
        leave();
        reject(abortError());
        if (--read.live === 0) this.#abort(read);
      };
      signal?.addEventListener('abort', onAbort, { once: true });

      read.promise.then(
        (bytes) => { leave(); resolve(bytes.slice()); },
        (error) => { leave(); reject(error); });
    });
  }

  #enqueue(read, index, record, entry, priority) {
    const ticket = new AbortController();
    read.tickets.add(ticket);

    this.registry.queue.run(async () => {
      read.started = true;
      for (const other of read.tickets) if (other !== ticket) other.abort();
      read.tickets.clear();
      await this.#execute(read, index, record, entry);
    }, { priority, signal: ticket.signal }).catch(() => { /* removed from the queue, or reported by the read itself */ });
  }

  async #execute(read, index, record, entry) {
    try {
      const bytes = await this.#readerFor(read, record).block(record, entry, read.emitChunk);
      // An aborted read is never kept: whoever wanted it has gone, and a block that finished
      // after that is no business of the next reader's.
      if (!read.controller.signal.aborted) this.#remember(index, bytes);
      this.#finish(read, index);
      read.resolve(bytes);
    } catch (e) {
      this.#finish(read, index);
      read.reject(e);
    }
  }

  #readerFor(read, record) {
    if (read.sequential) return this.registry.pageReader(this, record);
    // A seek must stop the walk, which the shared reader's cache cannot do for one read, so a
    // read that may be cancelled has its own reader over a source that honours the signal.
    return new XrplFileReader(new AbortableSource(this.registry.source, read.controller.signal),
      { historyCacheRecords: PAGE_HISTORY_RECORDS });
  }

  #remember(index, bytes) {
    this.verified.delete(index);
    this.verified.set(index, bytes);
    while (this.verified.size > VERIFIED_BLOCKS) this.verified.delete(this.verified.keys().next().value);
  }

  #finish(read, index) {
    if (this.reads.get(index) === read) this.reads.delete(index);
  }

  #abort(read) {
    if (read.controller.signal.aborted) return;
    read.controller.abort();
    for (const ticket of read.tickets) ticket.abort();
    read.tickets.clear();
    for (const [index, other] of this.reads) if (other === read) this.reads.delete(index);
    read.reject(abortError());
  }

  /** The last handle is gone: whatever is still being read has nobody to read it for. */
  dispose() {
    this.disposed = true;
    for (const read of [...this.reads.values()]) this.#abort(read);
    this.verified.clear();
    this.registry.forgetReader(this);
  }
}

function blockChunks(record, index) {
  const { size, chunk } = record.manifest;
  return Math.min(BLOCK_CHUNKS, chunkCount(size, chunk) - index * BLOCK_CHUNKS);
}

/**
 * One reader's view of a file. Handles of one link share the manifest request, the block list,
 * the read in flight for each block and the four blocks verified last; a handle is released when
 * its element goes, which does not touch what the others are reading.
 */
export class FileHandle {
  #state;
  #released = false;

  /** @param link the link this handle was opened for */
  constructor(state) {
    this.#state = state;
    this.link = state.link;
  }

  #live(operation) {
    if (this.#released) return Promise.reject(abortError('this file handle was released'));
    return operation();
  }

  /** The manifest record, as XrplFileReader#manifest returns it. */
  record() { return this.#live(() => this.#state.record()); }

  /** The block list. Awaits the history check once per link, so a node without the history says so first. */
  async entries() {
    return this.#live(async () => (await this.#state.loaded()).list);
  }

  /** The bytes of every block but the file's last. */
  async blockBytes() {
    return this.#live(async () => BLOCK_CHUNKS * (await this.#state.record()).manifest.chunk);
  }

  /**
   * One verified block. `onChunk` is called once per chunk of the block, whether the read is
   * shared, was joined late or the block was already verified. A shared read is aborted only when
   * every consumer's signal has aborted. `sequential` reads through the page's shared reader (its
   * history cache serves blocks read in order); otherwise the walk stops when the read is aborted.
   * @returns a copy of the block that the caller may keep or change
   */
  block(index, { priority = PRIORITY.BACKGROUND, signal, onChunk, sequential = false } = {}) {
    return this.#live(() => this.#state.block(index, { priority, signal, onChunk, sequential }));
  }

  /** The cover the manifest names, once per link, or null. */
  cover() { return this.#live(() => this.#state.cover()); }

  release() {
    if (this.#released) return;
    this.#released = true;
    this.#state.registry.release(this.#state);
  }
}

/** The files of one node set: one state per link, dropped when its last handle is released. */
export class FileRegistry {
  #states = new Map();
  #pageReaders = new Map();

  constructor(source, queue) {
    this.source = source;
    this.queue = queue;
    // For the manifest, the block list and covers, none of which use a reader's forward history.
    this.reader = new XrplFileReader(source);
  }

  open(link) {
    const key = formatLink(link);
    let state = this.#states.get(key);
    if (state === undefined) {
      state = new LinkState(this, key, { network: link.network, txHash: link.txHash });
      this.#states.set(key, state);
    }
    state.refs++;
    return new FileHandle(state);
  }

  release(state) {
    if (--state.refs > 0) return;
    // A release followed by an open in the same tick (an element moved in the DOM) keeps the state.
    queueMicrotask(() => {
      if (state.refs !== 0 || this.#states.get(state.key) !== state) return;
      this.#states.delete(state.key);
      state.dispose();
    });
  }

  /**
   * The reader the sequential reads of a file go through, one per `${writer} ${firstLedger}
   * ${lastLedger}`. Its ForwardHistory is the page's history cache (spec 2.7): it answers from a
   * range only when it knows the range is complete for that account.
   */
  pageReader(state, record) {
    const key = `${record.writer} ${record.manifest.firstLedger} ${record.manifest.lastLedger}`;
    let entry = this.#pageReaders.get(key);
    if (entry === undefined) {
      entry = { reader: new XrplFileReader(this.source, { historyCacheRecords: PAGE_HISTORY_RECORDS }), states: new Set() };
      this.#pageReaders.set(key, entry);
    }
    entry.states.add(state);
    state.historyKey = key;
    return entry.reader;
  }

  forgetReader(state) {
    const entry = this.#pageReaders.get(state.historyKey);
    if (entry === undefined) return;
    entry.states.delete(state);
    if (entry.states.size === 0) this.#pageReaders.delete(state.historyKey);
  }

  /** The connection is going: nothing of this registry is read for again. */
  dispose() {
    for (const state of [...this.#states.values()]) state.dispose();
    this.#states.clear();
    this.#pageReaders.clear();
  }
}

/** What one element holds of a node set. Releasing it twice releases it once. */
export class Lease {
  #entry;
  #release;
  #released = false;

  constructor(entry, release) {
    this.#entry = entry;
    this.#release = release;
  }

  get source() { return this.#entry.source; }

  get queue() { return this.#entry.queue; }

  get files() { return this.#entry.files; }

  release() {
    if (this.#released) return;
    this.#released = true;
    this.#release();
  }
}

/**
 * One LedgerClient per node set, shared by every element that names it and closed when the last
 * of them lets go. LedgerClient#close is final, so elements never call it: a node set that was
 * released and is acquired again later gets a new client.
 */
export class NodePool {
  #createClient;
  #entries = new Map();

  constructor(createClient = (endpoints) => new LedgerClient(endpoints)) {
    this.#createClient = createClient;
  }

  get size() { return this.#entries.size; }

  acquire(endpoints) {
    const key = endpoints.join(' ');
    let entry = this.#entries.get(key);
    if (entry === undefined) {
      const client = this.#createClient([...endpoints]);
      const source = new SharedSource(client);
      const queue = new ReadQueue();
      entry = { key, client, source, queue, files: new FileRegistry(source, queue), refs: 0 };
      this.#entries.set(key, entry);
    }

    entry.refs++;
    return new Lease(entry, () => this.#release(entry));
  }

  #release(entry) {
    if (--entry.refs > 0) return;
    // An acquire in the same tick (an element moved in the DOM) keeps the connection open;
    // closing is final, and reopening a socket for it would drop what is in flight.
    queueMicrotask(() => {
      if (entry.refs !== 0 || this.#entries.get(entry.key) !== entry) return;
      this.#entries.delete(entry.key);
      entry.files.dispose();
      entry.client.close();
    });
  }
}

/** The page's pool: every element of the page shares it. */
export const pool = new NodePool();
