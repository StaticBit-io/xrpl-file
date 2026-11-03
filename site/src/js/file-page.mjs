// The file page (/<network>/<HASH>, served by one static file.html) and the 404 page. The file itself
// is the <xrpl-file> element; this module adds what sits around it: Download, name, size, network,
// the link with a copy button, and the note on how it works. The page's name, size and Download
// come from the element's `load` event and `save()`, never from a second read of the ledger.
import { initCheckLink } from './check-link.mjs';
import { mountCloud as mountPointCloud } from './cloud.mjs';
import { initCopyButton } from './copy-button.mjs';
import { startElementRead } from './element-read.mjs';
import { fileCloudStrings } from './home-strings.mjs';
import { activeLang, homePath, htmlLang } from './languages.mjs';
import { getLang } from './prefs.mjs';
import { linkFromPath } from './route.mjs';
import { formatSize, networkName, t } from './strings.mjs';

// Errors of save() by what the visitor can do about them. The element draws its own texts in the
// page's language, but a rejected save() carries an English message for scripts, so the page words the
// result on its own status line.
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

// How long the canvas fades out; the same figure as the transition in site.css.
const CLOUD_FADE_MS = 250;

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
 * @param {{ lang?: string, history?: Pick<History, 'replaceState'>, mountCloud?: typeof mountPointCloud,
 *   reducedMotion?: boolean }} [options] `mountCloud` and `reducedMotion` are seams for tests; the
 *   defaults are the point cloud and the browser's own setting.
 */
export function initFilePage(doc, location, {
  lang: initialLang,
  history = globalThis.history,
  mountCloud = mountPointCloud,
  reducedMotion = doc.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false,
} = {}) {
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

  const canvas = doc.getElementById('file-cloud');
  const cloudCaption = doc.getElementById('file-cloud-caption');
  const cloudNote = doc.getElementById('file-cloud-note');
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
  // The element reads the page's language when it connects and follows its own `lang` after that. This page
  // switches language in place, so <html lang> changes under an element that is already there: it is told.
  element.setAttribute('lang', htmlLang(lang));
  // The cloud is the loader: the element keeps its box, which the canvas fills, and draws nothing in it.
  element.setAttribute('loader', 'false');

  // The cloud listens to the element, so it is mounted before the element is in the page and cannot
  // miss an event; it uses the network of the link, which is known before the file is read.
  const network = route.link.split('/')[2];
  const cloud = mountCloud(
    canvas,
    {
      caption: cloudCaption,
      note: cloudNote,
      turn: false,
      swayFor: 5000,
      fast: true,
      strings: fileCloudStrings(lang, network),
    },
    { startRead: startElementRead, element },
  );
  let cloudEnded = false;
  const endCloud = () => {
    if (cloudEnded) return;
    cloudEnded = true;
    canvas.classList.add('is-gone');
    const remove = () => {
      // Hidden, not just covered: a transparent picture shows whatever is under the element.
      canvas.hidden = true;
      cloudCaption.textContent = '';
      cloudCaption.hidden = true;
      cloudNote.textContent = '';
      cloudNote.hidden = true;
      cloud.destroy();
    };
    if (reducedMotion) remove();
    else setTimeout(remove, CLOUD_FADE_MS);
  };
  canvas.parentElement.append(element);

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
    endCloud();
    detail = event.detail ?? {};
    // A picture with transparent parts shows on the page itself. Until it loads, and for any other
    // type, the box stays: the loader, a message, a text file or a video need a ground of their own.
    if (typeof detail.type === 'string' && detail.type.startsWith('image/')) element.setAttribute('background', 'transparent');
    else element.removeAttribute('background');
    render();
    syncButton();
  });
  // `error` only ends the cloud: the element also reports a video its browser cannot play, while the
  // verified file can still be saved, so it neither disables Download nor words a message here. A save
  // the element refuses is worded below.
  element.addEventListener('error', endCloud);

  button.addEventListener('click', async () => {
    if (button.disabled) return;
    saving = true;
    // Saving a large file takes a while and the button is disabled meanwhile, so the line says why.
    result = { key: 'downloadSaving' };
    syncButton();
    renderStatus();
    try {
      const outcome = await element.save();
      result = null;
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
    element.setAttribute('lang', htmlLang(lang));
    if (!cloudEnded) cloud.setStrings(fileCloudStrings(lang, network));
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
