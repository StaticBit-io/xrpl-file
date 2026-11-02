// The "Check a link" field: a pasted xrpl-file:// link opens its file page, anything else gets an
// inline error. The home page and the 404 page share this behaviour; each passes its own strings.
import { pathFromLink } from './route.mjs';

// Delay between emptying the live region and writing the message again. An identical message is
// announced a second time only if the region was seen empty in between.
const REANNOUNCE_DELAY_MS = 50;

/**
 * @param {object} parts
 * @param {HTMLFormElement} parts.form
 * @param {HTMLInputElement} parts.input
 * @param {HTMLElement} parts.message a live region (role="alert") that stays in the page and is empty
 *   while the field is fine; its id is what the input points at while it is invalid
 * @param {{ empty: string, invalid: string }} parts.strings
 * @param {(path: string) => void} [parts.navigate]
 * @param {(callback: () => void) => void} [parts.defer] runs `callback` after the re-announce delay
 */
export function initCheckLink({
  form,
  input,
  message,
  strings,
  navigate = (path) => location.assign(path),
  defer = (callback) => setTimeout(callback, REANNOUNCE_DELAY_MS),
}) {
  let pending = 0;

  const clear = () => {
    pending += 1;
    input.removeAttribute('aria-invalid');
    input.removeAttribute('aria-describedby');
    message.textContent = '';
  };

  // Pressing Enter leaves focus where it is, so focus() announces nothing: the message is the
  // announcement, and the region is not toggled with `hidden`, which would drop it from the
  // accessibility tree while the text is inserted.
  const fail = (text) => {
    clear();
    const turn = pending;
    input.setAttribute('aria-invalid', 'true');
    input.setAttribute('aria-describedby', message.id);
    defer(() => {
      if (turn === pending) message.textContent = text;
    });
    input.focus();
  };

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (input.value.trim() === '') return fail(strings.empty);
    const path = pathFromLink(input.value);
    if (path === null) return fail(strings.invalid);
    clear();
    navigate(path);
    return undefined;
  });

  input.addEventListener('input', clear);
}
