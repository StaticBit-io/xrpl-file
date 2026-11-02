// Behaviour of the shared header and footer: the theme switch, the language menu and links and, on a page
// whose language is the visitor's stored choice, switching the chrome's own text in place.
import { LANGUAGE_NAMES, homePath, htmlLang, isActive, pageLang } from './languages.mjs';
import { applyTheme, equivalentPath, getLang, getTheme, safeLocalStorage, setLang, setTheme } from './prefs.mjs';
import { STRINGS } from './chrome-strings.mjs';

function translate(document, lang) {
  const strings = STRINGS[lang];
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = strings[element.dataset.i18n];
  for (const element of document.querySelectorAll('[data-i18n-label]')) {
    element.setAttribute('aria-label', strings[element.dataset.i18nLabel]);
  }
  for (const element of document.querySelectorAll('[data-lang-current]')) element.textContent = LANGUAGE_NAMES[lang];
}

function markCurrentLanguage(document, lang) {
  for (const link of document.querySelectorAll('[data-lang-link]')) {
    if (link.dataset.langLink === lang) link.setAttribute('aria-current', 'page');
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

/**
 * The disclosure's own toggling is native (details/summary, Enter and Space). What it lacks is what a
 * menu is expected to do: Escape closes it and returns focus to its button, and so does a click or a
 * focus move to anywhere outside it.
 */
function initLanguageMenus(document) {
  const menus = document.querySelectorAll('[data-lang-menu]');
  if (menus.length === 0) return;
  for (const menu of menus) {
    menu.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape' || !menu.open) return;
      event.preventDefault();
      menu.open = false;
      menu.querySelector('summary')?.focus();
    });
  }
  const closeOutside = (event) => {
    for (const menu of menus) {
      if (menu.open && !menu.contains(event.target)) menu.open = false;
    }
  };
  document.addEventListener('click', closeOutside);
  document.addEventListener('focusin', closeOutside);
}

function initLanguage(document, location, storage, browserLanguages) {
  const root = document.documentElement;
  const fromStorage = root.getAttribute('data-lang-source') === 'storage';
  let lang = fromStorage ? getLang(storage, browserLanguages) : pageLang(root.lang);

  const refresh = () => {
    markCurrentLanguage(document, lang);
    for (const link of document.querySelectorAll('[data-lang-link]')) {
      // A stored-language page has one URL for every language (the file page, its address-error view, the 404).
      const href = fromStorage ? location.pathname : equivalentPath(location.pathname, link.dataset.langLink);
      link.setAttribute('href', href);
    }
    // The wordmark's home link: a stored-language page is built once, so its build-time "/" is the English home.
    if (fromStorage) {
      for (const link of document.querySelectorAll('[data-home]')) link.setAttribute('href', homePath(lang));
    }
  };

  if (fromStorage) {
    root.lang = htmlLang(lang);
    translate(document, lang);
  }
  refresh();

  for (const link of document.querySelectorAll('[data-lang-link]')) {
    link.addEventListener('click', (event) => {
      const chosen = isActive(link.dataset.langLink) ? link.dataset.langLink : 'en';
      setLang(chosen, storage);
      // A fixed-language page is left by the link itself; a stored-language page has one URL and
      // is rewritten in place, with an event so the page's own text follows.
      if (!fromStorage) return;
      event.preventDefault();
      // The chosen link is about to be hidden with its menu; focus goes to the menu's button so it
      // does not fall to the start of the page.
      const menu = link.closest('[data-lang-menu]');
      if (menu) {
        menu.open = false;
        menu.querySelector('summary')?.focus();
      }
      if (chosen === lang) return;
      lang = chosen;
      root.lang = htmlLang(lang);
      translate(document, lang);
      refresh();
      document.dispatchEvent(new CustomEvent('xf-langchange', { detail: { lang } }));
    });
  }
}

/**
 * @param {{ document: Document, location: Pick<Location, 'pathname'>, storage?: Storage | null, navigatorLanguages?: string | string[] }} env
 *   navigatorLanguages defaults to the browser's own list (see getLang)
 */
export function initChrome({ document, location, storage = safeLocalStorage(), navigatorLanguages }) {
  initTheme(document, storage);
  initLanguageMenus(document);
  initLanguage(document, location, storage, navigatorLanguages);
}

if (typeof document !== 'undefined') initChrome({ document, location });
