// The "Check a link" field: a pasted xrpl-file:// link opens its file page, anything else gets an
// inline error. The home page and the 404 page share this behaviour; each passes its own strings.
import { pathFromLink } from './route.mjs';

/**
 * @param {object} parts
 * @param {HTMLFormElement} parts.form
 * @param {HTMLInputElement} parts.input
 * @param {HTMLElement} parts.message the element that holds the error text; its id is what the input points at
 * @param {{ empty: string, invalid: string }} parts.strings
 * @param {(path: string) => void} [parts.navigate]
 */
export function initCheckLink({ form, input, message, strings, navigate = (path) => location.assign(path) }) {
  const clear = () => {
    input.removeAttribute('aria-invalid');
    input.removeAttribute('aria-describedby');
    message.textContent = '';
    message.hidden = true;
  };

  const fail = (text) => {
    message.textContent = text;
    message.hidden = false;
    input.setAttribute('aria-invalid', 'true');
    input.setAttribute('aria-describedby', message.id);
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
