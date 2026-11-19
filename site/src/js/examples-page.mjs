// The examples page: the chrome (theme switch, language menu) is brought by chrome.mjs through the head
// marker; what is left is the elements, which follow the site theme the way the hero card does, and each
// card's "</>" button, which opens a dialog with the card's embed code and a copy button.
import { initCopyButton } from './copy-button.mjs';
import { COPY_STRINGS } from './home-strings.mjs';
import { activeLang, pageLang } from './languages.mjs';
import { applyTheme, getTheme } from './prefs.mjs';

// chrome.mjs also applies the theme, but a module's order is not a contract; this one call is what
// makes the cards' theme attribute right whichever script runs first.
applyTheme(document, getTheme());

const strings = COPY_STRINGS[activeLang(pageLang(document.documentElement.lang))];

for (const opener of document.querySelectorAll('.example__code-button')) {
  const dialog = document.getElementById(opener.getAttribute('aria-controls'));
  if (!(dialog instanceof HTMLDialogElement)) continue;
  opener.addEventListener('click', () => dialog.showModal());
  dialog.querySelector('.code-dialog__close')?.addEventListener('click', () => dialog.close());
  // A click on the backdrop lands on the dialog itself, outside its padding box.
  dialog.addEventListener('click', (event) => {
    if (event.target !== dialog) return;
    const box = dialog.getBoundingClientRect();
    const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
    if (!inside) dialog.close();
  });
  const button = dialog.querySelector('.example__copy');
  const source = dialog.querySelector('code');
  const status = dialog.querySelector('.snippet__status');
  if (button && source && status) initCopyButton({ button, source, status, strings });
}
