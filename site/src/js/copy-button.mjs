// A copy button with a spoken result. The clipboard API needs a secure context and a user gesture and
// can be refused, so a failure says so in the status line and leaves the text selected for a manual copy.

function selectText(element) {
  const selection = globalThis.getSelection?.();
  if (!selection || !element.ownerDocument) return;
  const range = element.ownerDocument.createRange();
  range.selectNodeContents(element);
  selection.removeAllRanges();
  selection.addRange(range);
}

/**
 * @param {object} parts
 * @param {HTMLElement} parts.button
 * @param {HTMLElement} parts.source the element whose text is copied
 * @param {HTMLElement} parts.status a polite live region next to the button
 * @param {{ copied: string, failed: string }} parts.strings
 * @param {{ writeText(text: string): Promise<void> } | null} [parts.clipboard]
 */
export function initCopyButton({ button, source, status, strings, clipboard = globalThis.navigator?.clipboard ?? null }) {
  let timer = null;
  const say = (text) => {
    status.textContent = text;
    clearTimeout(timer);
    timer = setTimeout(() => { status.textContent = ''; }, 4000);
  };

  button.addEventListener('click', async () => {
    try {
      if (!clipboard) throw new Error('no clipboard');
      await clipboard.writeText(source.textContent);
      say(strings.copied);
    } catch {
      selectText(source);
      say(strings.failed);
    }
  });
}
