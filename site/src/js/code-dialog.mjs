// The embed code dialog of the examples page and the file page: a "</>" button opens a modal <dialog>
// holding the code, a copy button and a close button. The native dialog brings the focus trap, Escape
// and the return of focus to the button; this adds the close button and a click on the backdrop.
import { initCopyButton } from './copy-button.mjs';

/**
 * @param {object} parts
 * @param {HTMLElement} parts.opener the button that opens the dialog
 * @param {HTMLDialogElement} parts.dialog
 * @param {{ copied: string, failed: string }} parts.strings the copy button's spoken results
 */
export function initCodeDialog({ opener, dialog, strings }) {
  opener.addEventListener('click', () => dialog.showModal());
  dialog.querySelector('.code-dialog__close')?.addEventListener('click', () => dialog.close());
  // A click on the backdrop lands on the dialog element itself, outside its box.
  dialog.addEventListener('click', (event) => {
    if (event.target !== dialog) return;
    const box = dialog.getBoundingClientRect();
    const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
    if (!inside) dialog.close();
  });
  const button = dialog.querySelector('.code-dialog__copy');
  const source = dialog.querySelector('code');
  const status = dialog.querySelector('.snippet__status');
  if (button && source && status) initCopyButton({ button, source, status, strings });
}
