// The file page (/<network>/<HASH>, served by one static file.html) and the 404 page. The file itself
// is the <xrpl-file> element; this module adds what sits around it: Download, name, size, network,
// the link with a copy button, and the note on how it works. The page's name, size and Download
// come from the element's `load` event and `save()`, never from a second read of the ledger.
import { initCheckLink } from './check-link.mjs';
import { initCopyButton } from './copy-button.mjs';
import { activeLang, homePath } from './languages.mjs';
import { getLang } from './prefs.mjs';
import { linkFromPath } from './route.mjs';
import { formatSize, networkName, t } from './strings.mjs';

// Errors of save() by what the visitor can do about them. The element words its own errors in
// English only; the page is bilingual, so it words the save result itself.
const SAVE_ERROR_KEYS = {
  NotLoaded: 'saveNotLoaded',
  BlockMismatch: 'saveNotVerified',
  ListMismatch: 'saveNotVerified',
  ChunkMissing: 'saveUnavailable',
  ManifestNotFound: 'saveUnavailable',
  NodeLacksHistory: 'saveUnavailable',
  CorruptManifest: 'saveUnavailable',
  NodeUnreachable: 'saveNodeDown',
  TooLarge: 'saveTooLarge',
};

/** The string key for a rejected save(): an XrplFileError by code, or a DOMException by name. */
function saveErrorKey(error) {
  // AbortError: the element was reset mid-save. NotAllowedError: the browser wanted a user gesture.
  if (error?.name === 'AbortError') return 'saveInterrupted';
  if (error?.name === 'NotAllowedError' || error?.name === 'SecurityError') return 'saveRefused';
  return SAVE_ERROR_KEYS[error?.code] ?? 'saveFailed';
}

function normalise(lang) {
  return activeLang(lang);
}

function localizeStatic(doc, lang) {
  for (const node of doc.querySelectorAll('[data-s]')) node.textContent = t(node.dataset.s, lang);
  for (const node of doc.querySelectorAll('[data-s-label]')) node.setAttribute('aria-label', t(node.dataset.sLabel, lang));
  for (const node of doc.querySelectorAll('[data-home]')) node.setAttribute('href', homePath(lang));
}

/** The "Check a link" field, in the language the page has now. */
function initCheckForm(doc, getCurrentLang) {
  const message = doc.getElementById('check-link-error');
  const input = doc.getElementById('check-link-input');
  // initCheckLink reads these when the visitor submits, so a switch of language reaches the errors.
  const strings = {
    get empty() { return t('checkEmpty', getCurrentLang()); },
    get invalid() { return t('checkInvalid', getCurrentLang()); },
  };
  const field = initCheckLink({ form: doc.getElementById('check-link'), input, message, strings });
  return {
    input,
    showInvalid: () => field.showError(strings.invalid),
    // An error already on screen is re-worded; focus stays where the visitor's switch left it.
    relocalize(from, to) {
      for (const key of ['checkEmpty', 'checkInvalid']) {
        if (message.textContent === t(key, from)) message.textContent = t(key, to);
      }
    },
  };
}

/** The link an address of the /<a>/<b> shape stands for, so the visitor can correct it; anything else gives an empty field. */
function typedLink(pathname) {
  if (!/^\/[^/]+\/[^/]+\/?$/.test(pathname)) return '';
  try {
    return `xrpl-file:/${decodeURIComponent(pathname)}`;
  } catch {
    return `xrpl-file:/${pathname}`;
  }
}

function setTitle(doc, text) {
  doc.title = `${text} · xrpl-file`;
}

/**
 * Wires the file page.
 * @param {Document} doc
 * @param {{ pathname: string, search?: string, hash?: string }} location
 * @param {{ lang?: string, history?: Pick<History, 'replaceState'> }} [options]
 */
export function initFilePage(doc, location, { lang: initialLang, history = globalThis.history } = {}) {
  let lang = normalise(initialLang);
  const route = linkFromPath(location.pathname);
  const filePage = doc.getElementById('file-page');
  const invalidPage = doc.getElementById('invalid-page');

  if (route === null) {
    // An address that is not a link: the home page's field, already in error, with what was typed.
    filePage.hidden = true;
    invalidPage.hidden = false;
    const form = initCheckForm(doc, () => lang);
    form.input.value = typedLink(location.pathname);
    const render = () => {
      localizeStatic(doc, lang);
      setTitle(doc, t('checkTitle', lang));
    };
    render();
    form.showInvalid();
    doc.addEventListener('xf-langchange', (event) => {
      const from = lang;
      lang = normalise(event.detail?.lang);
      render();
      form.relocalize(from, lang);
    });
    return { view: 'invalid', element: null };
  }

  // Replace, never push: the visitor did not navigate, the address was only spelled differently.
  if (location.pathname !== route.canonicalPath) {
    history.replaceState(null, '', `${route.canonicalPath}${location.search ?? ''}${location.hash ?? ''}`);
  }

  filePage.hidden = false;
  invalidPage.hidden = true;

  const view = doc.getElementById('file-view');
  const button = doc.getElementById('file-download');
  const status = doc.getElementById('file-status');
  const linkText = doc.getElementById('file-link');
  const details = doc.getElementById('file-details');

  // What the page knows about the file: nothing until the element's `load`.
  let detail = null;
  let saving = false;
  let result = null;

  const element = doc.createElement('xrpl-file');
  element.setAttribute('src', route.link);
  // The page draws its own copy and download controls under the file.
  element.setAttribute('caption', 'off');
  element.setAttribute('theme', doc.documentElement.dataset.theme === 'light' ? 'light' : 'dark');
  view.append(element);

  linkText.textContent = route.link;
  initCopyButton({
    button: doc.getElementById('file-copy'),
    source: linkText,
    status: doc.getElementById('file-copy-status'),
    strings: {
      get copied() { return t('copied', lang); },
      get failed() { return t('copyFailed', lang); },
    },
  });

  const renderStatus = () => {
    const key = result?.key ?? null;
    status.textContent = key === null ? '' : t(key, lang);
    status.dataset.state = result?.error ? 'error' : 'ok';
  };

  const render = () => {
    localizeStatic(doc, lang);
    const name = typeof detail?.name === 'string' && detail.name !== '' ? detail.name : t('fileTitle', lang);
    doc.getElementById('file-title').textContent = name;
    setTitle(doc, name);
    const size = detail === null ? null : formatSize(detail.size, lang);
    doc.getElementById('file-size').textContent = size ?? '';
    doc.getElementById('file-size-row').hidden = size === null;
    doc.getElementById('file-network').textContent = networkName(detail?.network ?? route.link.split('/')[2], lang);
    details.hidden = detail === null;
    renderStatus();
  };

  const syncButton = () => {
    button.disabled = detail === null || saving;
  };

  element.addEventListener('load', (event) => {
    detail = event.detail ?? {};
    render();
    syncButton();
  });
  // There is no `error` listener on purpose: the element also reports a video its browser cannot
  // play, while the verified file can still be saved. A save the element refuses is worded below.

  button.addEventListener('click', async () => {
    if (button.disabled) return;
    saving = true;
    result = null;
    syncButton();
    renderStatus();
    try {
      const outcome = await element.save();
      // 'cancelled' (the dialog was closed) and anything unexpected leave the line empty: only
      // an outcome the element reported as done may say so.
      if (outcome === 'saved') result = { key: 'downloadSaved' };
      else if (outcome === 'started') result = { key: 'downloadStarted' };
    } catch (error) {
      result = { key: saveErrorKey(error), error: true };
    } finally {
      saving = false;
      syncButton();
      renderStatus();
    }
  });

  render();
  syncButton();
  doc.addEventListener('xf-langchange', (event) => {
    lang = normalise(event.detail?.lang);
    render();
  });
  return { view: 'file', element };
}

/**
 * Wires the 404 page: the words, the way home and the "Check a link" field.
 * @param {Document} doc
 * @param {{ lang?: string }} [options]
 */
export function initNotFoundPage(doc, { lang: initialLang } = {}) {
  let lang = normalise(initialLang);
  const form = initCheckForm(doc, () => lang);
  const render = () => {
    localizeStatic(doc, lang);
    setTitle(doc, t('notFoundTitle', lang));
  };
  render();
  doc.addEventListener('xf-langchange', (event) => {
    const from = lang;
    lang = normalise(event.detail?.lang);
    render();
    form.relocalize(from, lang);
  });
}

if (typeof document !== 'undefined') {
  const lang = getLang();
  if (document.body.dataset.page === 'not-found') initNotFoundPage(document, { lang });
  else initFilePage(document, location, { lang });
}
