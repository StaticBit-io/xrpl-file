import { XrplFileError } from '../link.mjs';

export const NO_NODE = 'This network has no built-in node. Set the node attribute.';
export const BAD_NODE = 'The node attribute must be a wss:// address.';
export const INSECURE = 'This page is not served over HTTPS, so the browser cannot check this file.';
export const NO_LINK = 'There is no xrpl-file link in this element.';

const GENERIC = 'Something went wrong while showing this file.';

/**
 * An error the element raises itself, before or beside the reader. Its sentence is shown as written;
 * `code` may be null when the failure has no name in the shared error set.
 */
export function elementError(code, sentence) {
  const error = new XrplFileError(code, sentence);
  error.sentence = sentence;
  return error;
}

function blockMismatch(block, playing) {
  if (playing) return `Playback stopped at block ${block}: it did not pass verification.`;
  return `This file did not pass verification. Block ${block} does not match the file's fingerprint. Nothing was shown.`;
}

function chunkMissing(block, playing) {
  if (playing) return `Playback stopped at block ${block}: part of it is missing from the ledger.`;
  return `Part of this file is missing from the ledger: block ${block} is incomplete. Nothing was shown.`;
}

/**
 * The one short sentence the visitor sees. The reader's own messages are for developers and never
 * reach the page; `playing` is set when video was already on screen, where "Nothing was shown" would be false.
 */
export function messageFor(error, { playing = false } = {}) {
  if (typeof error?.sentence === 'string') return error.sentence;
  if (!(error instanceof XrplFileError)) return GENERIC;
  switch (error.code) {
    case 'InvalidLink': return 'This is not an xrpl-file link.';
    case 'ManifestNotFound': return 'This file is not on the ledger.';
    case 'NodeLacksHistory': return 'The node does not have the history this file needs.';
    case 'CorruptManifest': return "This file's description on the ledger is damaged.";
    case 'ListMismatch': return "This file did not pass verification. Its block list does not match the file's fingerprint. Nothing was shown.";
    case 'BlockMismatch': return blockMismatch(error.block, playing);
    case 'ChunkMissing': return chunkMissing(error.block, playing);
    case 'TooLarge': return 'This file is too large to save in this browser.';
    case 'NodeUnreachable': return 'The node is not answering. Try again later.';
    default: return GENERIC;
  }
}

/** The `detail` of the `error` event: the error name from the shared set (null when it has none) and the sentence shown. */
export function detailFor(error) {
  const name = error instanceof XrplFileError ? (error.code ?? null) : null;
  return { name, message: messageFor(error) };
}

/** Hash checks need SubtleCrypto, which browsers expose to secure contexts only. */
export function canVerify(scope = globalThis) {
  return scope.isSecureContext === true && typeof scope.crypto?.subtle?.digest === 'function';
}
