import { PRIORITY } from './page.mjs';
import { downloadName, extensionFor } from './format.mjs';
import { elementError } from './messages.mjs';

/** The most a browser without a save-file picker is asked to hold in memory: 2 GiB. */
export const BLOB_LIMIT = 2_147_483_648;

const TOO_LARGE = 'This file is too large to save in this browser.';
// Revoking in the same task as the click can cancel the download in Safari and older Firefox.
const REVOKE_DELAY_MS = 60_000;

/** Browser only: hands a Blob to the user through a temporary `<a download>`. */
export function defaultSaveBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name;
  anchor.rel = 'noopener';
  anchor.style.display = 'none';
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
}

function isAbort(error) {
  return error?.name === 'AbortError';
}

/**
 * A cross-origin iframe (Google Sites, Wix and Notion embeds) has `showSaveFilePicker` but the
 * browser refuses it there with SecurityError; NotAllowedError is the same refusal without a user
 * gesture. Neither says anything about the file, so the save moves to the in-memory path.
 */
function isPickerBlocked(error) {
  return error?.name === 'SecurityError' || error?.name === 'NotAllowedError';
}

const PICKER_BLOCKED = Symbol('picker blocked');

// Without startIn Chromium opens the picker in Documents, which OneDrive folder backup redirects to
// the cloud; the Windows dialog then hangs enumerating it while Ctrl+S, which starts in Downloads,
// works. Starting where the browser's own downloads go avoids that and is where visitors look.
const START_IN = 'downloads';

/**
 * Asks for a place to save. A TypeError means the platform refused the suggested name, which comes
 * from the manifest and so cannot be vetted in advance; it is asked once more under a name that is
 * ours. Returns null when the visitor closed the dialog, PICKER_BLOCKED when the browser does not
 * let this page use the picker at all.
 */
async function pickFile(picker, manifest) {
  const name = downloadName(manifest);
  const fallback = `xrpl-file.${extensionFor(manifest.type)}`;
  try {
    return await picker({ suggestedName: name, startIn: START_IN });
  } catch (error) {
    if (isAbort(error)) return null;
    if (isPickerBlocked(error)) return PICKER_BLOCKED;
    if (!(error instanceof TypeError) || name === fallback) throw error;
  }
  try {
    return await picker({ suggestedName: fallback, startIn: START_IN });
  } catch (error) {
    if (isAbort(error)) return null;
    if (isPickerBlocked(error)) return PICKER_BLOCKED;
    throw error;
  }
}

/** Reads every block, in order, and gives each verified block to `sink` before asking for the next. */
async function eachBlock({ handle, manifest, signal, onProgress, sink }) {
  let bytesDone = 0;
  for (let index = 0; index < manifest.blocks; index++) {
    signal?.throwIfAborted();
    const bytes = await handle.block(index, { priority: PRIORITY.NOW, signal, sequential: true });
    await sink(bytes);
    bytesDone += bytes.length;
    onProgress({ bytesDone, bytesTotal: manifest.size, blocksVerified: index + 1, blocksTotal: manifest.blocks });
  }
}

async function writeToPicked(file, options) {
  const writable = await file.createWritable();
  try {
    await eachBlock({ ...options, sink: (bytes) => writable.write(bytes) });
    await writable.close();
  } catch (error) {
    // The partial file is dropped where the API allows; the failure that got us here is the one to report.
    try { await writable.abort(); } catch { /* the original error matters more */ }
    throw error;
  }
}

// The type is deliberately not the manifest's: it is attacker-chosen, and a same-origin blob: URL of
// text/html (or svg) that gets navigated to would run script in the host page's origin.
async function collectBlob(options) {
  const parts = [];
  await eachBlock({ ...options, sink: (bytes) => { parts.push(bytes); } });
  return new Blob(parts, { type: 'application/octet-stream' });
}

/**
 * Saves the verified bytes of a file. Where `showSaveFilePicker` exists the file is written block by
 * block as each verifies, so its size is not bounded by memory; elsewhere it is assembled as a Blob,
 * up to {@link BLOB_LIMIT}; the same path takes over when the browser blocks the picker for this page
 * (a cross-origin iframe). A block that fails verification is never written, and the file under way
 * is aborted.
 * @returns 'saved', or 'cancelled' when the visitor closed the save dialog before anything was read
 */
export async function saveFile({
  handle,
  record,
  picker = globalThis.showSaveFilePicker,
  saveBlob = defaultSaveBlob,
  onProgress = () => {},
  signal,
}) {
  const { manifest } = record;
  const options = { handle, manifest, signal, onProgress };

  if (typeof picker === 'function') {
    const file = await pickFile(picker, manifest);
    if (file === null) return 'cancelled';
    if (file !== PICKER_BLOCKED) {
      await writeToPicked(file, options);
      return 'saved';
    }
  }

  if (manifest.size > BLOB_LIMIT) throw elementError('TooLarge', TOO_LARGE);
  const blob = await collectBlob(options);
  saveBlob(blob, downloadName(manifest));
  return 'saved';
}
