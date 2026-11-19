// A minimal XRPL WebSocket client: tx, account_tx and server_info, nothing else. No SDK, no
// dependencies, so anyone can see exactly what the page asks the network for.
import { XrplFileError } from './link.mjs';

export const MAINNET_FIRST_LEDGER = 32570;

export const ENDPOINTS = {
  mainnet: ['wss://xrplcluster.com', 'wss://s2.ripple.com'],
  testnet: ['wss://s.altnet.rippletest.net:51233'],
  devnet: ['wss://s.devnet.rippletest.net:51233'],
};

// Inactivity, not a per-request budget: one page of account_tx is ~1.2 MB, so on a slow link
// several concurrent pages legitimately take longer than this each. See Connection.rearm.
const REQUEST_TIMEOUT_MS = 20000;

// Public nodes meter requests by units per window and answer tooBusy or slowDown once it is spent.
// Another endpoint is usually free, so a busy answer moves the request on instead of failing it.
const BUSY_ERRORS = new Set(['tooBusy', 'slowDown']);
const MIN_BUSY_WAIT_MS = 1000;
// Above the longest hint seen from a public node (~66-81 s), so the second pass lands after its window.
const MAX_BUSY_WAIT_MS = 120000;
// s2.ripple.com says slowDown with no hint; 5 s proved too short for it to recover.
const DEFAULT_BUSY_WAIT_MS = 20000;

/**
 * Milliseconds a node said to wait ("retry in ~66-81 s", "retry in ~19353ms"), the high end of a
 * range so the retry lands after the window; null when it said none.
 */
function busyHintMs(answer) {
  const match = /retry in ~?\s*(\d+)(?:\s*-\s*(\d+))?\s*(ms|s)\b/i.exec(JSON.stringify(answer));
  if (!match) return null;
  return Number(match[2] ?? match[1]) * (match[3].toLowerCase() === 'ms' ? 1 : 1000);
}

const text = (o, k) => (o && typeof o === 'object' && typeof o[k] === 'string' ? o[k] : null);
const number = (o, k) => (o && typeof o === 'object' && Number.isInteger(o[k]) ? o[k] : null);

/** Restarts of one walk in a row that yielded nothing new, before it gives up. */
const MAX_IDLE_RESTARTS = 8;

/** One transaction as a node describes it — an account_tx record or a tx answer; api_version 1 and 2 shapes. */
export function parseRecord(record) {
  const tx = record.tx_json ?? record.tx ?? record;
  const memo = Array.isArray(tx.Memos) && tx.Memos.length > 0 ? tx.Memos[0].Memo ?? {} : {};
  return {
    hash: (text(record, 'hash') ?? text(tx, 'hash') ?? '').toUpperCase(),
    ledgerIndex: number(record, 'ledger_index') ?? number(tx, 'ledger_index') ?? 0,
    account: text(tx, 'Account') ?? '',
    succeeded: text(record.meta, 'TransactionResult') === 'tesSUCCESS',
    validated: record.validated === true,
    memoTypeHex: text(memo, 'MemoType'),
    memoDataHex: text(memo, 'MemoData'),
    memoFormatHex: text(memo, 'MemoFormat'),
  };
}

/** The low bound of the first range in complete_ledgers, or null. "empty" parses to null — zero would read as "has everything". */
export function earliestAvailableLedger(result) {
  const ranges = text(result?.info, 'complete_ledgers');
  if (!ranges) return null;
  const first = ranges.split(',')[0];
  const dash = first.indexOf('-');
  const lowest = Number.parseInt(dash > 0 ? first.slice(0, dash) : first, 10);
  return Number.isInteger(lowest) ? lowest : null;
}

/** One socket to one node: its shared handshake, its requests in flight and their inactivity clock. */
class Connection {
  constructor(url, requestTimeoutMs) {
    this.url = url;
    this.requestTimeoutMs = requestTimeoutMs;
    this.socket = null;
    this.pendingSocket = null;
    this.pendingReject = null;
    this.connecting = null;
    // Bumped by every teardown: a request that saw an older socket fail must not close a newer one.
    this.generation = 0;
    this.nextId = 1;
    this.pending = new Map();
    this.closed = false;
  }

  close() {
    this.closed = true;
    this.disconnect();
  }

  disconnect() {
    this.generation++;
    if (this.socket) {
      this.socket.onclose = null;
      this.socket.onmessage = null;
      try { this.socket.close(); } catch { /* already closed */ }
      this.socket = null;
    }

    // A socket still shaking hands is not in this.socket yet, so closing only that one would
    // leave it to finish opening and revive a client that was told to close (the original bug:
    // onopen unconditionally assigned this.socket and resolved, even after close()). onopen is
    // nulled here so a late handshake cannot fire it at all, and it also checks this.closed
    // itself as a second line of defense against a handshake that was already queued to open.
    if (this.pendingSocket) {
      const pending = this.pendingSocket;
      this.pendingSocket = null;
      pending.onopen = null;
      pending.onerror = null;
      pending.onclose = null;
      pending.onmessage = null;
      try { pending.close(); } catch { /* it may not be open yet */ }
    }

    // connect()'s promise would otherwise hang forever: its socket's handlers were just nulled
    // above, so nothing else will ever settle it.
    if (this.pendingReject) {
      const reject = this.pendingReject;
      this.pendingReject = null;
      reject(new XrplFileError('NodeUnreachable', this.closed
        ? 'this client has been closed'
        : `connection to ${this.url} closed while it was still connecting`));
    }

    for (const { reject, timer } of this.pending.values()) {
      clearTimeout(timer);
      reject(new XrplFileError('NodeUnreachable', `connection to ${this.url} closed while a request was in flight`));
    }
    this.pending.clear();
  }

  async connect() {
    if (this.closed) throw new XrplFileError('NodeUnreachable', 'this client has been closed');
    if (this.socket?.readyState === WebSocket.OPEN) return;
    // Concurrent first requests (a page of blocks all starting at once) must share one handshake:
    // each would otherwise build its own socket, and every onopen overwrites this.socket, leaving
    // the earlier ones open and unreferenced. A new attempt starts only once this one has settled.
    if (!this.connecting) {
      const attempt = this.handshake();
      const done = () => { if (this.connecting === attempt) this.connecting = null; };
      attempt.then(done, done);
      this.connecting = attempt;
    }
    await this.connecting;
  }

  handshake() {
    return new Promise((resolve, reject) => {
      const socket = new WebSocket(this.url);
      this.pendingSocket = socket;
      this.pendingReject = reject;
      const settle = (fn, value) => {
        if (this.pendingSocket === socket) this.pendingSocket = null;
        if (this.pendingReject === reject) this.pendingReject = null;
        fn(value);
      };
      socket.onopen = () => {
        // The client may have been closed while this socket was still shaking hands; a late
        // handshake must not revive it by assigning this.socket and resolving.
        if (this.closed) {
          try { socket.close(); } catch { /* already closing */ }
          settle(reject, new XrplFileError('NodeUnreachable', 'this client has been closed'));
          return;
        }
        this.socket = socket;
        settle(resolve);
      };
      socket.onerror = () => settle(reject, new XrplFileError('NodeUnreachable', `cannot reach ${this.url}`));
      socket.onclose = () => {
        if (this.socket === socket) this.disconnect();
        settle(reject, new XrplFileError('NodeUnreachable', `${this.url} closed the connection`));
      };
      socket.onmessage = (event) => {
        // A frame that does not parse is ignored, not thrown out of the socket handler where
        // nothing catches it and the request it belonged to hangs until its own timeout.
        // Any frame proves the socket is alive, so it pushes every pending deadline back, even
        // when it answers a different request or does not parse.
        this.rearm();
        let message;
        try { message = JSON.parse(event.data); } catch { return; }
        this.receive(message);
      };
    });
  }

  /** Restarts the inactivity clock of every pending request; one stalled request must not outlive a socket that keeps delivering. */
  rearm() {
    for (const waiting of this.pending.values()) {
      clearTimeout(waiting.timer);
      waiting.timer = this.startTimer(waiting.id, waiting.command);
    }
  }

  startTimer(id, command) {
    return setTimeout(() => {
      const waiting = this.pending.get(id);
      if (!waiting) return;
      this.pending.delete(id);
      waiting.reject(new XrplFileError('NodeUnreachable', `${this.url} sent nothing for ${this.requestTimeoutMs} ms while ${command} was pending`));
    }, this.requestTimeoutMs);
  }

  receive(message) {
    const waiting = this.pending.get(message.id);
    if (!waiting) return;
    this.pending.delete(message.id);
    clearTimeout(waiting.timer);
    if (message.status === 'error') waiting.resolve({ error: message.error, ...message });
    else waiting.resolve(message.result);
  }

  send(command, params) {
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const waiting = { id, command, resolve, reject, timer: this.startTimer(id, command) };
      const fail = (message) => {
        this.pending.delete(id);
        clearTimeout(waiting.timer);
        reject(new XrplFileError('NodeUnreachable', message));
      };

      this.pending.set(id, waiting);

      // connect() resolved, but the socket can be gone or closing by the time this runs -- a node
      // that hung up in between, or a close() from the page. Rejecting here is what lets
      // request() move on to the next endpoint instead of raising whatever send() throws.
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
        fail(`the connection to ${this.url} was gone before ${command} could be sent`);
        return;
      }

      try {
        this.socket.send(JSON.stringify({ id, command, api_version: 2, ...params }));
      } catch (e) {
        fail(`${this.url} refused ${command}: ${e.message}`);
      }
    });
  }
}

export class LedgerClient {
  /**
   * @param {string[]} endpoints tried in order; a request moves on when a node fails or is busy
   * @param {object} [options] now, setTimer and clearTimer let tests run the waits on a fake clock
   */
  constructor(endpoints, {
    pageSize = 400,
    requestTimeoutMs = REQUEST_TIMEOUT_MS,
    setTimer = (fn, ms) => setTimeout(fn, ms),
    clearTimer = (handle) => clearTimeout(handle),
    now = () => Date.now(),
  } = {}) {
    if (!endpoints?.length) throw new Error('at least one endpoint is required');
    this.endpoints = endpoints;
    this.pageSize = pageSize;
    this.setTimer = setTimer;
    this.clearTimer = clearTimer;
    this.now = now;
    // One connection per node, opened on first use: a busy answer or a dead socket on one node
    // must not cost the requests in flight on another.
    this.connections = endpoints.map((url) => new Connection(url, requestTimeoutMs));
    // Nodes meter the client, not one request, so a busy answer holds the node off for everyone.
    this.busy = endpoints.map(() => ({ until: 0, error: null }));
    this.preferred = 0;
    this.waits = new Set();
    this.closed = false;
  }

  /** The node the client asks first: the one that last served a request. */
  get endpoint() { return this.endpoints[this.preferred]; }

  close() {
    this.closed = true;
    for (const connection of this.connections) connection.close();
    for (const wait of [...this.waits]) wait.cancel();
  }

  /** One request; the answer is the node's result object, or its error answer when not busy. */
  async request(command, params) {
    return (await this.ask(command, params)).answer;
  }

  /**
   * Each node is asked at most twice: every node busy or failed for this request means one wait
   * until the earliest busy node is free again, then a second round over the nodes free by then.
   */
  async ask(command, params) {
    const count = this.endpoints.length;
    const failed = new Set();
    const refused = new Set();
    const said = new Map();
    let due = null;
    let lastError = null;
    for (;;) {
      const index = this.pick(failed, refused, said, due);
      if (index === null) {
        if (refused.size === 0) throw lastError;
        if (due) throw this.rateLimited(command, said);
        const until = Math.min(...[...refused].map((i) => this.busy[i].until));
        // The nodes waited for are asked even if another answer marked them again meanwhile:
        // that answer may have left the node before the wait began.
        due = new Set([...refused].filter((i) => this.busy[i].until <= until));
        await this.waitMs(Math.max(0, until - this.now()));
        refused.clear();
        continue;
      }

      const connection = this.connections[index];
      const generation = connection.generation;
      try {
        await connection.connect();
        const answer = await connection.send(command, params);
        if (!BUSY_ERRORS.has(answer.error)) {
          this.preferred = index;
          return { answer, endpoint: connection.url };
        }
        this.markBusy(index, answer);
        refused.add(index);
        said.set(index, answer.error);
      } catch (e) {
        if (this.closed || e.code !== 'NodeUnreachable') throw e;
        // Every request on the socket sees its failure; only the first closes it, or a later one
        // would close the reconnect already made.
        if (connection.generation === generation) connection.disconnect();
        if (this.preferred === index) this.preferred = (index + 1) % count;
        failed.add(index);
        lastError = e;
      }
    }
  }

  /** The first node from the preferred one that this request may ask now, or null. */
  pick(failed, refused, said, due) {
    const now = this.now();
    for (let step = 0; step < this.endpoints.length; step++) {
      const index = (this.preferred + step) % this.endpoints.length;
      if (failed.has(index) || refused.has(index)) continue;
      if (due?.has(index) || this.busy[index].until <= now) return index;
      refused.add(index);
      said.set(index, this.busy[index].error);
    }
    return null;
  }

  markBusy(index, answer) {
    const hint = busyHintMs(answer) ?? DEFAULT_BUSY_WAIT_MS;
    this.busy[index] = {
      until: this.now() + Math.min(MAX_BUSY_WAIT_MS, Math.max(MIN_BUSY_WAIT_MS, hint)),
      error: answer.error,
    };
  }

  rateLimited(command, said) {
    const answers = [...said].map(([index, error]) => `${this.endpoints[index]} answered ${error}`);
    return new XrplFileError('NodeUnreachable', `${answers.join(', ')} to ${command}: the node is rate limiting this client`);
  }

  /** A pause that close() ends early. */
  waitMs(ms) {
    return new Promise((resolve, reject) => {
      if (this.closed) {
        reject(new XrplFileError('NodeUnreachable', 'this client has been closed'));
        return;
      }
      const wait = {
        handle: null,
        cancel: () => {
          this.clearTimer(wait.handle);
          this.waits.delete(wait);
          reject(new XrplFileError('NodeUnreachable', 'this client has been closed'));
        },
      };
      wait.handle = this.setTimer(() => {
        this.waits.delete(wait);
        resolve();
      }, ms);
      this.waits.add(wait);
    });
  }

  async transaction(hash) {
    const { answer, endpoint } = await this.ask('tx', { transaction: hash });
    if (answer.error === 'txnNotFound') return null;
    if (answer.error) throw new XrplFileError('NodeUnreachable', `${endpoint} answered ${answer.error} to tx`);
    return parseRecord(answer);
  }

  async *accountTransactions(account, fromLedger, toLedger, forward) {
    // A marker means something only to the node that issued it: xrplcluster.com's names the next
    // transaction to return, s2.ripple.com's the last one returned, so one sent to the other node
    // skips or repeats a transaction. A page answered by another node is dropped and the walk
    // restarts without a marker at the ledger it had reached, skipping what it already yielded there.
    let marker;
    let markerFrom = null;
    let low = fromLedger;
    let high = toLedger;
    let edgeLedger = null;
    let yieldedAtEdge = new Set();
    let idleRestarts = 0;
    for (;;) {
      const { answer, endpoint } = await this.ask('account_tx', {
        account, ledger_index_min: low, ledger_index_max: high, forward, limit: this.pageSize, ...(marker ? { marker } : {}),
      });
      // An account the node has never heard of has no transactions -- that is an answer, not a
      // failure to read, exactly as txnNotFound is for tx above. The .NET source agrees, and the
      // two have to: a mirror that turns absence into an unreachable node reports a file as
      // missing on one side and the network as down on the other.
      if (answer.error === 'actNotFound') return;
      if (answer.error) throw new XrplFileError('NodeUnreachable', `${endpoint} answered ${answer.error} to account_tx`);
      if (marker && endpoint !== markerFrom) {
        // Nodes taking turns while a single ledger holds more than a page would restart forever.
        if (++idleRestarts > MAX_IDLE_RESTARTS) {
          throw new XrplFileError('NodeUnreachable', `account_tx pages kept moving between nodes before reaching past ledger ${edgeLedger ?? low}`);
        }
        marker = undefined;
        markerFrom = null;
        if (edgeLedger !== null) {
          if (forward) low = edgeLedger;
          else high = edgeLedger;
        }
        continue;
      }
      for (const record of answer.transactions ?? []) {
        const tx = parseRecord(record);
        if (tx.hash && tx.ledgerIndex === edgeLedger && yieldedAtEdge.has(tx.hash)) continue;
        if (tx.ledgerIndex !== edgeLedger) {
          edgeLedger = tx.ledgerIndex;
          yieldedAtEdge = new Set();
        }
        if (tx.hash) yieldedAtEdge.add(tx.hash);
        idleRestarts = 0;
        yield tx;
      }
      marker = answer.marker;
      markerFrom = endpoint;
      if (!marker) return;
    }
  }

  async earliestLedger() {
    const { answer, endpoint } = await this.ask('server_info', {});
    // A node that answered an error said nothing about its history; reading that as "does not
    // say" would turn an unreachable node into a history verdict. The .NET source agrees.
    if (answer.error) throw new XrplFileError('NodeUnreachable', `${endpoint} answered ${answer.error} to server_info`);
    return earliestAvailableLedger(answer);
  }
}
