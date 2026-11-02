// Remembered theme and language. localStorage throws in private windows and with blocked site data
// (even the property read can), so every access is guarded and a failure only means "not remembered".
export const THEME_KEY = 'xf-theme';
export const LANG_KEY = 'xf-lang';
const FILE_PAGE = /^\/[^/]+\/[0-9A-Fa-f]{64}\/?$/;

export function safeLocalStorage() {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}

function readItem(storage, key) {
  try {
    return storage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

function writeItem(storage, key, value) {
  try {
    storage?.setItem(key, value);
  } catch {
    // Not remembered; the choice still applies to the current page.
  }
}

/** The first visit is dark whatever the OS prefers; only an explicit stored choice gives light. */
export function getTheme(storage = safeLocalStorage()) {
  return readItem(storage, THEME_KEY) === 'light' ? 'light' : 'dark';
}

export function setTheme(theme, storage = safeLocalStorage()) {
  writeItem(storage, THEME_KEY, theme === 'light' ? 'light' : 'dark');
}

export function getLang(storage = safeLocalStorage(), navigatorLanguage = globalThis.navigator?.language) {
  const stored = readItem(storage, LANG_KEY);
  if (stored === 'en' || stored === 'ru') return stored;
  return String(navigatorLanguage ?? '').toLowerCase().startsWith('ru') ? 'ru' : 'en';
}

export function setLang(lang, storage = safeLocalStorage()) {
  writeItem(storage, LANG_KEY, lang === 'ru' ? 'ru' : 'en');
}

/** The same page in the other language: / <-> /ru/ and /x.html <-> /ru/x.html. A file page has one URL. */
export function equivalentPath(pathname, lang) {
  if (FILE_PAGE.test(pathname)) return pathname;
  const inRu = pathname === '/ru' || pathname.startsWith('/ru/');
  if (lang === 'ru') return inRu ? pathname : `/ru${pathname}`;
  if (!inRu) return pathname;
  return pathname.slice('/ru'.length) || '/';
}

export function applyTheme(doc, theme) {
  const value = theme === 'light' ? 'light' : 'dark';
  doc.documentElement.dataset.theme = value;
  for (const element of doc.querySelectorAll('xrpl-file')) element.setAttribute('theme', value);
}
