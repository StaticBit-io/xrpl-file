import { XrplFileError } from '../link.mjs';
import { STRINGS, stringsFor } from './strings.mjs';

// The English sentences are the element's documented text: the `error` event and XrplFileError carry
// them whatever the page's language, and scripts match on them. A language only changes what is drawn.
const EN = STRINGS.en;

export const NO_NODE = EN.noNode;
export const BAD_NODE = EN.badNode;
export const INSECURE = EN.insecure;
export const NO_LINK = EN.noLink;
export const TOO_LARGE = EN.tooLarge;
export const DECODE_FAILED = EN.decodeFailed;

/**
 * An error the element raises itself, before or beside the reader. Its sentence is the English one, which
 * scripts see; what is drawn is the same sentence in the page's language (see {@link messageFor}).
 * `code` may be null when the failure has no name in the shared error set.
 */
export function elementError(code, sentence) {
  const error = new XrplFileError(code, sentence);
  error.sentence = sentence;
  return error;
}

// The element's own sentences by their English text, so that a language can word them again.
const KEY_BY_SENTENCE = new Map([
  [NO_NODE, 'noNode'],
  [BAD_NODE, 'badNode'],
  [INSECURE, 'insecure'],
  [NO_LINK, 'noLink'],
  [TOO_LARGE, 'tooLarge'],
  [DECODE_FAILED, 'decodeFailed'],
]);

function sentenceIn(sentence, strings) {
  const key = KEY_BY_SENTENCE.get(sentence);
  return key === undefined ? sentence : strings[key];
}

/**
 * The one short sentence the visitor sees, in `lang` (English by default; the element passes its own).
 * The reader's own messages are for developers and never reach the page; `playing` is set when video was
 * already on screen, where "Nothing was shown" would be false.
 */
export function messageFor(error, { playing = false, lang = 'en' } = {}) {
  const s = stringsFor(lang);
  if (typeof error?.sentence === 'string') return sentenceIn(error.sentence, s);
  if (!(error instanceof XrplFileError)) return s.generic;
  switch (error.code) {
    case 'InvalidLink': return s.invalidLink;
    case 'ManifestNotFound': return s.manifestNotFound;
    case 'NodeLacksHistory': return s.nodeLacksHistory;
    case 'CorruptManifest': return s.corruptManifest;
    case 'ListMismatch': return s.listMismatch;
    case 'BlockMismatch': return playing ? s.blockMismatchPlaying(error.block) : s.blockMismatch(error.block);
    case 'ChunkMissing': return playing ? s.chunkMissingPlaying(error.block) : s.chunkMissing(error.block);
    case 'TooLarge': return s.tooLarge;
    case 'NodeUnreachable': return s.nodeUnreachable;
    default: return s.generic;
  }
}

const SHORT_KEY_BY_SENTENCE = new Map([
  [NO_NODE, 'shortNoNode'],
  [BAD_NODE, 'shortBadNode'],
  [INSECURE, 'shortNotSecure'],
  [NO_LINK, 'shortNoLink'],
]);

/**
 * A title of a few characters for a box too small for the sentence: the full sentence stays in the
 * accessible text and in the tooltip. The element's own sentences are told apart by the sentence,
 * since they share codes with reader errors (NO_NODE is a NodeUnreachable).
 */
export function shortTitleFor(error, lang = 'en') {
  const s = stringsFor(lang);
  const known = typeof error?.sentence === 'string' ? SHORT_KEY_BY_SENTENCE.get(error.sentence) : undefined;
  if (known) return s[known];
  if (!(error instanceof XrplFileError)) return s.shortError;
  switch (error.code) {
    case 'InvalidLink': return s.shortBadLink;
    case 'ManifestNotFound': return s.shortNotFound;
    case 'NodeLacksHistory': return s.shortNoHistory;
    case 'CorruptManifest': return s.shortDamaged;
    case 'ListMismatch':
    case 'BlockMismatch': return s.shortNotVerified;
    case 'ChunkMissing': return s.shortIncomplete;
    case 'TooLarge': return s.shortTooLarge;
    case 'NodeUnreachable': return s.shortNoAnswer;
    default: return s.shortError;
  }
}

/**
 * The status-line sentence for a save that did not finish. The file is still on screen, so the
 * view-level wording ("Nothing was shown") would be false; a block that failed verification is still
 * named, since that is a statement about the file and not about the save.
 */
export function saveMessageFor(error, lang = 'en') {
  const s = stringsFor(lang);
  if (typeof error?.sentence === 'string') return sentenceIn(error.sentence, s);
  if (error instanceof XrplFileError) {
    switch (error.code) {
      case 'BlockMismatch': return s.saveBlockMismatch(error.block);
      case 'ChunkMissing': return s.saveChunkMissing(error.block);
      case 'NodeUnreachable': return messageFor(error, { lang });
      default: break;
    }
  }
  return s.saveFailed;
}

/**
 * The `detail` of the `error` event: the error name from the shared set (null when it has none) and the
 * sentence shown, which for a video that was already playing is the `playing` one. Always English: the
 * event is API, and a page that matches on the text must not depend on the language it is shown in.
 */
export function detailFor(error, { playing = false } = {}) {
  const name = error instanceof XrplFileError ? (error.code ?? null) : null;
  return { name, message: messageFor(error, { playing }) };
}

/** Hash checks need SubtleCrypto, which browsers expose to secure contexts only. */
export function canVerify(scope = globalThis) {
  return scope.isSecureContext === true && typeof scope.crypto?.subtle?.digest === 'function';
}
