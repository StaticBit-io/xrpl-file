// Remembered theme and language. localStorage throws in private windows and with blocked site data
// (even the property read can), so every access is guarded and a failure only means "not remembered".
export const THEME_KEY = 'xf-theme';
import { ACTIVE_LANGS, LANGS, isActive, langFromTag, pathPrefix } from './languages.mjs';

export { ACTIVE_LANGS, LANGS };

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

function browserLanguages() {
  const navigator = globalThis.navigator;
  return navigator?.languages?.length ? navigator.languages : navigator?.language;
}

/**
 * The stored choice if it is an active language, else the first browser language the site offers
 * (matched by primary subtag: zh-TW is Chinese, pt-BR Portuguese), else English. `browserLanguages` is
 * navigator.languages or a single tag.
 */
export function getLang(storage = safeLocalStorage(), browserLangs = browserLanguages()) {
  const stored = readItem(storage, LANG_KEY);
  if (isActive(stored)) return stored;
  const tags = Array.isArray(browserLangs) ? browserLangs : [browserLangs];
  for (const tag of tags) {
    const lang = langFromTag(tag);
    if (lang !== null && isActive(lang)) return lang;
  }
  return 'en';
}

export function setLang(lang, storage = safeLocalStorage()) {
  writeItem(storage, LANG_KEY, isActive(lang) ? lang : 'en');
}

/**
 * The same page in another language: / <-> /ru/ <-> /de/ and /x.html <-> /ru/x.html. A file page has
 * one URL. Every known prefix is stripped, active or not, so a stray /de/ path still maps back.
 */
export function equivalentPath(pathname, lang) {
  if (FILE_PAGE.test(pathname)) return pathname;
  let rest = pathname;
  for (const known of LANGS) {
    const prefix = pathPrefix(known);
    if (prefix !== '' && (rest === prefix || rest.startsWith(`${prefix}/`))) {
      rest = rest.slice(prefix.length) || '/';
      break;
    }
  }
  return `${pathPrefix(lang)}${rest}`;
}

export function applyTheme(doc, theme) {
  const value = theme === 'light' ? 'light' : 'dark';
  doc.documentElement.dataset.theme = value;
  for (const element of doc.querySelectorAll('xrpl-file')) element.setAttribute('theme', value);
}
