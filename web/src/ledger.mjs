// A minimal XRPL WebSocket client: tx, account_tx and server_info, nothing else. No SDK, no
// dependencies, so anyone can see exactly what the page asks the network for.
import { XrplFileError } from './link.mjs';

export const MAINNET_FIRST_LEDGER = 32570;

export const ENDPOINTS = {
  mainnet: ['wss://xrplcluster.com', 'wss://s2.ripple.com'],
  testnet: ['wss://s.altnet.rippletest.net:51233'],
  devnet: ['wss://s.devnet.rippletest.net:51233'],
};

const REQUEST_TIMEOUT_MS = 20000;

const text = (o, k) => (o && typeof o === 'object' && typeof o[k] === 'string' ? o[k] : null);
const number = (o, k) => (o && typeof o === 'object' && Number.isInteger(o[k]) ? o[k] : null);

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

export class LedgerClient {
  /** @param {string[]} endpoints tried in order; each operation moves on when a node fails */
  constructor(endpoints, { pageSize = 400 } = {}) {
    if (!endpoints?.length) throw new Error('at least one endpoint is required');
    this.endpoints = endpoints;
    this.pageSize = pageSize;
    this.index = 0;
    this.socket = null;
    this.pendingSocket = null;
    this.pendingReject = null;
    this.connecting = null;
    this.failures = 0;
    this.nextId = 1;
    this.pending = new Map();
    this.closed = false;
  }

  get endpoint() { return this.endpoints[this.index]; }

  close() {
    this.closed = true;
    this.disconnect();
  }

  disconnect() {
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
        : `connection to ${this.endpoint} closed while it was still connecting`));
    }

    for (const { reject, timer } of this.pending.values()) {
      clearTimeout(timer);
      reject(new XrplFileError('NodeUnreachable', `connection to ${this.endpoint} closed while a request was in flight`));
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
      const socket = new WebSocket(this.endpoint);
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
      socket.onerror = () => settle(reject, new XrplFileError('NodeUnreachable', `cannot reach ${this.endpoint}`));
      socket.onclose = () => {
        if (this.socket === socket) this.disconnect();
        settle(reject, new XrplFileError('NodeUnreachable', `${this.endpoint} closed the connection`));
      };
      socket.onmessage = (event) => {
        // A frame that does not parse is ignored, not thrown out of the socket handler where
        // nothing catches it and the request it belonged to hangs until its own timeout.
        let message;
        try { message = JSON.parse(event.data); } catch { return; }
        this.receive(message);
      };
    });
  }

  receive(message) {
    const waiting = this.pending.get(message.id);
    if (!waiting) return;
    this.pending.delete(message.id);
    clearTimeout(waiting.timer);
    if (message.status === 'error') waiting.resolve({ error: message.error, ...message });
    else waiting.resolve(message.result);
  }

  /** One request, tried on each endpoint in turn; the answer is the node's result object. */
  async request(command, params) {
    let lastError = null;
    for (let attempt = 0; attempt < this.endpoints.length; attempt++) {
      const failures = this.failures;
      try {
        await this.connect();
        return await this.send(command, params);
      } catch (e) {
        if (this.closed || e.code !== 'NodeUnreachable') throw e;
        lastError = e;
        // Requests sharing one connection all see its failure; only the first may tear down and
        // move to the next endpoint, or the rest would skip nodes and kill the retry's handshake.
        if (this.failures === failures) {
          this.failures++;
          this.disconnect();
          this.index = (this.index + 1) % this.endpoints.length;
        }
      }
    }
    throw lastError;
  }

  send(command, params) {
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new XrplFileError('NodeUnreachable', `${this.endpoint} did not answer ${command} in ${REQUEST_TIMEOUT_MS} ms`));
      }, REQUEST_TIMEOUT_MS);
      const fail = (message) => {
        this.pending.delete(id);
        clearTimeout(timer);
        reject(new XrplFileError('NodeUnreachable', message));
      };

      this.pending.set(id, { resolve, reject, timer });

      // connect() resolved, but the socket can be gone or closing by the time this runs -- a node
      // that hung up in between, or a close() from the page. Rejecting here is what lets
      // request() move on to the next endpoint instead of raising whatever send() throws.
      if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
        fail(`the connection to ${this.endpoint} was gone before ${command} could be sent`);
        return;
      }

      try {
        this.socket.send(JSON.stringify({ id, command, api_version: 2, ...params }));
      } catch (e) {
        fail(`${this.endpoint} refused ${command}: ${e.message}`);
      }
    });
  }

  async transaction(hash) {
    const answer = await this.request('tx', { transaction: hash });
    if (answer.error === 'txnNotFound') return null;
    if (answer.error) throw new XrplFileError('NodeUnreachable', `${this.endpoint} answered ${answer.error} to tx`);
    return parseRecord(answer);
  }

  async *accountTransactions(account, fromLedger, toLedger, forward) {
    let marker;
    for (;;) {
      const answer = await this.request('account_tx', {
        account, ledger_index_min: fromLedger, ledger_index_max: toLedger, forward, limit: this.pageSize, ...(marker ? { marker } : {}),
      });
      // An account the node has never heard of has no transactions -- that is an answer, not a
      // failure to read, exactly as txnNotFound is for tx above. The .NET source agrees, and the
      // two have to: a mirror that turns absence into an unreachable node reports a file as
      // missing on one side and the network as down on the other.
      if (answer.error === 'actNotFound') return;
      if (answer.error) throw new XrplFileError('NodeUnreachable', `${this.endpoint} answered ${answer.error} to account_tx`);
      for (const record of answer.transactions ?? []) yield parseRecord(record);
      marker = answer.marker;
      if (!marker) return;
    }
  }

  async earliestLedger() {
    const answer = await this.request('server_info', {});
    // A node that answered an error said nothing about its history; reading that as "does not
    // say" would turn an unreachable node into a history verdict. The .NET source agrees.
    if (answer.error) throw new XrplFileError('NodeUnreachable', `${this.endpoint} answered ${answer.error} to server_info`);
    return earliestAvailableLedger(answer);
  }
}
