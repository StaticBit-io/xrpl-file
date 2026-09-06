export class XrplFileError extends Error {
  /** @param {string} code one of the XrplFileError names shared with the .NET library */
  constructor(code, message, { block = null, cause = null } = {}) {
    super(message, cause ? { cause } : undefined);
    this.name = 'XrplFileError';
    this.code = code;
    this.block = block;
  }
}

export const SCHEME = 'xrpl-file';
const PREFIX = `${SCHEME}://`;
const KNOWN = new Set(['mainnet', 'testnet', 'devnet']);

function normalizeNetwork(network) {
  const lower = String(network ?? '').toLowerCase();
  if (KNOWN.has(lower)) return lower;
  // Any other network is named by its NetworkID: digits only, no sign, no leading zero.
  return /^(0|[1-9][0-9]*)$/.test(lower) ? lower : null;
}

function normalizeHash(hash) {
  return /^[0-9a-fA-F]{64}$/.test(hash ?? '') ? hash.toUpperCase() : null;
}

export function parseLink(text) {
  if (typeof text !== 'string' || !text.toLowerCase().startsWith(PREFIX)) {
    throw new XrplFileError('InvalidLink', `an xrpl-file link starts with '${PREFIX}'`);
  }
  const rest = text.slice(PREFIX.length);
  const slash = rest.indexOf('/');
  if (slash <= 0 || slash === rest.length - 1) {
    throw new XrplFileError('InvalidLink', 'an xrpl-file link is the network, a slash and the manifest transaction hash');
  }
  const network = normalizeNetwork(rest.slice(0, slash));
  if (!network) throw new XrplFileError('InvalidLink', `'${rest.slice(0, slash)}' is neither a network name nor a NetworkID`);
  const txHash = normalizeHash(rest.slice(slash + 1));
  if (!txHash) throw new XrplFileError('InvalidLink', 'the transaction hash is not 64 hexadecimal characters');
  return { network, txHash };
}

export function formatLink({ network, txHash }) {
  return `${PREFIX}${network}/${txHash}`;
}
