// Copying the link. The async Clipboard API is refused in cross-origin iframes unless the embedding
// page delegates `clipboard-write` (Chromium: "blocked because of a permissions policy"), which most
// embeds never do; the older execCommand('copy') still works there while the click's activation lasts.

/**
 * Copies `text`, trying the Clipboard API first and `execCopy` when it is missing or refuses.
 * The only await before the fallback is the API's own answer, so the click's transient activation
 * (a few seconds in Chromium) still covers execCommand.
 * @returns true when either way reported success
 */
export async function copyText(text, { clipboard = globalThis.navigator?.clipboard, execCopy }) {
  if (typeof clipboard?.writeText === 'function') {
    try {
      await clipboard.writeText(text);
      return true;
    } catch { /* refused by a permissions policy or the user agent: try the older way */ }
  }
  try {
    return execCopy(text) === true;
  } catch {
    return false;
  }
}

/**
 * The execCommand('copy') way, through a temporary read-only textarea inside `root` (the element's
 * shadow root), so the selection never touches the host page's own content.
 */
export function execCopyIn(root, doc = document) {
  return (text) => {
    const area = doc.createElement('textarea');
    area.value = text;
    area.readOnly = true;
    area.setAttribute('aria-hidden', 'true');
    area.style.cssText = 'position: fixed; top: 0; left: 0; opacity: 0; pointer-events: none;';
    root.append(area);
    try {
      area.select();
      return doc.execCommand('copy');
    } finally {
      area.remove();
    }
  };
}
