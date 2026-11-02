// Link <-> path mapping for the file page: xrpl-file://<network>/<HASH> <-> /<network>/<HASH>.
// Parsing and formatting are the reader's own (one definition of a valid link); this module only
// moves between the two spellings.
import { parseLink, formatLink, XrplFileError } from './xrpl.mjs';

const FILE_PATH = /^\/([^/]+)\/([^/]+)\/?$/;

function parseOrNull(text) {
  try {
    return parseLink(text);
  } catch (error) {
    if (error instanceof XrplFileError) return null;
    throw error;
  }
}

/** The canonical file-page path for pasted link text, or null when the text is not a link. */
export function pathFromLink(text) {
  if (typeof text !== 'string') return null;
  // Chat apps and clipboards add whitespace and newlines around a pasted link.
  const parsed = parseOrNull(text.trim());
  return parsed ? `/${parsed.network}/${parsed.txHash}` : null;
}

/**
 * The link and canonical path for a request path, or null when the path is not a file page.
 * The network and the hash are accepted in any case; the canonical form is lower and upper case.
 */
export function linkFromPath(pathname) {
  const match = FILE_PATH.exec(pathname);
  if (!match) return null;
  const parsed = parseOrNull(`xrpl-file://${match[1]}/${match[2]}`);
  if (!parsed) return null;
  return { link: formatLink(parsed), canonicalPath: `/${parsed.network}/${parsed.txHash}` };
}
