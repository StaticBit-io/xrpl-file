// The examples page: the chrome (theme switch, language menu) is brought by chrome.mjs through the head
// marker; what is left is the elements, which follow the site theme the way the hero card does.
import { applyTheme, getTheme } from './prefs.mjs';

// chrome.mjs also applies the theme, but a module's order is not a contract; this one call is what
// makes the cards' theme attribute right whichever script runs first.
applyTheme(document, getTheme());
