// The examples page: the chrome (theme switch, language menu) is brought by chrome.mjs through the head
// marker; what is left is the elements, which follow the site theme the way the hero card does, and each
// card's "</>" button, which opens a dialog with the card's embed code and a copy button.
import { initCodeDialog } from './code-dialog.mjs';
import { COPY_STRINGS } from './home-strings.mjs';
import { activeLang, pageLang } from './languages.mjs';
import { applyTheme, getTheme } from './prefs.mjs';

// chrome.mjs also applies the theme, but a module's order is not a contract; this one call is what
// makes the cards' theme attribute right whichever script runs first.
applyTheme(document, getTheme());

const strings = COPY_STRINGS[activeLang(pageLang(document.documentElement.lang))];

for (const opener of document.querySelectorAll('.example .code-button')) {
  const dialog = document.getElementById(opener.getAttribute('aria-controls'));
  if (dialog instanceof HTMLDialogElement) initCodeDialog({ opener, dialog, strings });
}
