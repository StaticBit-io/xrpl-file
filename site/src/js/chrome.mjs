// Behaviour of the shared header and footer: the theme switch, the language links and, on a page
// whose language is the visitor's stored choice, switching the chrome's own text in place.
import { applyTheme, equivalentPath, getLang, getTheme, safeLocalStorage, setLang, setTheme } from './prefs.mjs';
import { STRINGS } from './chrome-strings.mjs';

function translate(document, lang) {
  const strings = STRINGS[lang];
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = strings[element.dataset.i18n];
  for (const element of document.querySelectorAll('[data-i18n-label]')) {
    element.setAttribute('aria-label', strings[element.dataset.i18nLabel]);
  }
}

function markCurrentLanguage(document, lang) {
  for (const link of document.querySelectorAll('[data-lang-link]')) {
    if (link.dataset.langLink === lang) link.setAttribute('aria-current', 'true');
    else link.removeAttribute('aria-current');
  }
}

function initTheme(document, storage) {
  const buttons = document.querySelectorAll('[data-theme-toggle]');
  // The switch is named for the theme it turns on ("Light theme") and is pressed while that theme is on.
  const show = (theme) => {
    applyTheme(document, theme);
    for (const button of buttons) button.setAttribute('aria-pressed', String(theme === 'light'));
  };
  show(getTheme(storage));
  for (const button of buttons) {
    button.addEventListener('click', () => {
      // Read the page, not storage: with blocked storage the switch must still work for this page.
      const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
      show(next);
      setTheme(next, storage);
    });
    button.setAttribute('data-ready', '');
  }
}

function initLanguage(document, location, storage, navigatorLanguage) {
  const root = document.documentElement;
  const fromStorage = root.getAttribute('data-lang-source') === 'storage';
  let lang = fromStorage ? getLang(storage, navigatorLanguage) : root.lang === 'ru' ? 'ru' : 'en';

  const refresh = () => {
    markCurrentLanguage(document, lang);
    for (const link of document.querySelectorAll('[data-lang-link]')) {
      // A stored-language page has one URL for both languages (the file page, its address-error view, the 404).
      const href = fromStorage ? location.pathname : equivalentPath(location.pathname, link.dataset.langLink);
      link.setAttribute('href', href);
    }
  };

  if (fromStorage) {
    root.lang = lang;
    translate(document, lang);
  }
  refresh();

  for (const link of document.querySelectorAll('[data-lang-link]')) {
    link.addEventListener('click', (event) => {
      const chosen = link.dataset.langLink === 'ru' ? 'ru' : 'en';
      setLang(chosen, storage);
      // A fixed-language page is left by the link itself; a stored-language page has one URL and
      // is rewritten in place, with an event so the page's own text follows.
      if (!fromStorage) return;
      event.preventDefault();
      if (chosen === lang) return;
      lang = chosen;
      root.lang = lang;
      translate(document, lang);
      refresh();
      document.dispatchEvent(new CustomEvent('xf-langchange', { detail: { lang } }));
    });
  }
}

/**
 * @param {{ document: Document, location: Pick<Location, 'pathname'>, storage?: Storage | null, navigatorLanguage?: string }} env
 */
export function initChrome({ document, location, storage = safeLocalStorage(), navigatorLanguage = globalThis.navigator?.language }) {
  initTheme(document, storage);
  initLanguage(document, location, storage, navigatorLanguage);
}

if (typeof document !== 'undefined') initChrome({ document, location });
