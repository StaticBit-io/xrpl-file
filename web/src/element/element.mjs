// The <xrpl-file> custom element: it shows a file stored on the XRP Ledger, reading it through the
// page's shared connection and showing nothing that did not verify. Decisions that need no DOM live
// in host.mjs, load.mjs, view.mjs and attributes.mjs, where node --test pins them; this file is the
// DOM glue and is checked in a browser.
import { TEMPLATE, STYLES } from './template.mjs';
import { hostStyle, linkReadiness, captionParts, styleSupport } from './host.mjs';
import { prepare, loadWhole, progressDetail, loadDetail } from './load.mjs';
import { pool, PRIORITY } from './page.mjs';
import { saveFile } from './download.mjs';
import { copyText, execCopyIn } from './clipboard.mjs';
import { parseLength, parseLoading, parseLoader, parseTheme, backgroundTransparent, captionHidden, linkText, endpointsFor, languageOf } from './attributes.mjs';
import { viewForType, magicAgrees, normalizeType } from './view.mjs';
import { textPreview, TEXT_LIMIT } from './text.mjs';
import { VideoView } from './video.mjs';
import { formatBytes, downloadName, bytesOf, progressLines, typeBadge, downloadLabel } from './format.mjs';
import { elementError, messageFor, shortTitleFor, saveMessageFor, detailFor, canVerify, INSECURE, NO_LINK } from './messages.mjs';
import { stringsFor } from './strings.mjs';
import { parseLink, formatLink, XrplFileError } from '../link.mjs';

const DEFAULT_RATIO = '16 / 9';
// Ratio stages, each applied at most once and only ever forward: the reserved box, the cover, the content.
const RATIO_COVER = 1;
const RATIO_CONTENT = 2;

const VIEWS = ['idle', 'loading', 'image', 'video', 'text', 'download', 'error'];
const COPIED_MS = 1500;
// An <a download> click is handed to the browser, which may still refuse it (a sandboxed iframe
// without allow-downloads) without telling the page, so only the picker path may claim "Saved.".
const SAVE_RESULT_KEYS = { saved: 'saved', started: 'downloadStarted' };

// The word for each reason a file is offered as a download. 'player' is mp4box.js, fetched from the
// script's own origin: a host that serves the script without CORS headers breaks only that fetch.
const NOTE_KEYS = {
  'too-large': 'noteTooLarge', mismatch: 'noteMismatch', undecodable: 'noteUndecodable', unplayable: 'noteUnplayable', player: 'notePlayer',
};

// The loading view's text is kept as a function of the language, so that a change of language words it again.
const READING = (lang) => ({ bytes: stringsFor(lang).reading, blocks: '' });
const saying = (key) => (lang) => stringsFor(lang)[key];

const isAbort = (error) => error?.name === 'AbortError';
const abortError = () => new DOMException('The save was cancelled.', 'AbortError');

function isLink(text) {
  try {
    parseLink(text);
    return true;
  } catch {
    return false;
  }
}

let sharedSheet = null;

/** The stylesheet every element adopts, built once from the template's style text. */
function sharedStyles() {
  if (sharedSheet === null) {
    sharedSheet = new CSSStyleSheet();
    sharedSheet.replaceSync(STYLES);
  }
  return sharedSheet;
}

export class XrplFileElement extends HTMLElement {
  static observedAttributes = ['src', 'width', 'height', 'loading', 'loader', 'node', 'theme', 'background', 'caption', 'lang'];

  #parts = {};
  #connected = false;
  #restartScheduled = false;

  // One generation per start: everything that outlives an await checks its signal first.
  #controller = new AbortController();
  #waiting = null;
  #observers = [];
  #visible = false;
  #frameRequest = 0;
  #pendingProgress = null;
  #lastBytes = 0;

  #link = null;
  #endpoints = null;
  #lease = null;
  #handle = null;
  #record = null;
  #video = null;

  #phase = 'idle';
  #verified = false;
  #saving = null;
  // The error the element is showing, so that save() can say why there is nothing to save.
  #failure = null;
  #loaderOn = true;
  // The language of everything drawn, from the `lang` attribute, an ancestor or the document (languageOf).
  #lang = 'en';
  #loading = { lines: READING, percent: null };
  #idleVideo = false;
  // The reason the download view is showing (null when it is not), and whether the text view was cut short:
  // what a change of language has to word again.
  #downloadView = null;
  #textTruncated = false;
  // The save status line as a function of the language, or null while it is empty.
  #saveStatus = null;
  #ratio = DEFAULT_RATIO;
  #ratioStage = 0;
  #urls = [];
  #copiedTimer = 0;
  // 'copied' or 'failed' while the feedback shows, else null.
  #copyFeedback = null;
  // The element's own `:host` rule: a constructable sheet, or a <style> element where those do not exist.
  #hostSheet = null;
  #hostStyleElement = null;

  constructor() {
    super();
    const root = this.attachShadow({ mode: 'open' });
    root.innerHTML = TEMPLATE;
    this.#adoptStyles(root);
    for (const element of root.querySelectorAll('[id]')) this.#parts[element.id] = element;
    this.#wire();
    this.#lang = languageOf(this);
    this.#applyLabels();
    this.#applySizing();
    this.#applyTheme();
    this.#applyBackground();
  }

  connectedCallback() {
    this.#connected = true;
    this.#restartScheduled = false;
    // The ancestors are known only now, and a page may have changed `lang` while the element was away.
    this.#updateLanguage();
    this.#start();
  }

  disconnectedCallback() {
    this.#connected = false;
    this.#restartScheduled = false;
    this.#reset();
  }

  attributeChangedCallback(name, previous, value) {
    if (previous === value) return;
    switch (name) {
      case 'width':
      case 'height':
        this.#applySizing();
        break;
      case 'theme':
        this.#applyTheme();
        break;
      case 'background':
        this.#applyBackground();
        break;
      case 'caption':
        this.#renderCaption();
        break;
      case 'lang':
        this.#updateLanguage();
        break;
      case 'loader':
        this.#loaderOn = parseLoader(value);
        this.#showPhase(this.#phase);
        break;
      case 'loading':
        // Only a start that has not happened yet can still follow the new mode.
        if (this.#phase === 'idle' && this.#handle === null) this.#scheduleRestart();
        break;
      default:
        this.#scheduleRestart();
    }
  }

  // Wiring

  #wire() {
    const { cover, img, open, copy, dlButton, textDownload, frame } = this.#parts;
    cover.addEventListener('load', () => this.#setRatio(RATIO_COVER, cover.naturalWidth, cover.naturalHeight));
    img.addEventListener('load', () => this.#setRatio(RATIO_CONTENT, img.naturalWidth, img.naturalHeight));
    open.addEventListener('click', () => this.#openContent());
    copy.addEventListener('click', () => this.#copyLink());
    dlButton.addEventListener('click', () => this.#saveFromButton());
    textDownload.addEventListener('click', () => this.#saveFromButton());
    frame.addEventListener('pointerup', (event) => {
      // A tap toggles the caption where there is no hover; a tap on a button is that button's own.
      if (event.pointerType !== 'touch' || event.target.closest?.('button')) return;
      frame.classList.toggle('tapped');
    });
  }

  #scheduleRestart() {
    if (!this.#connected || this.#restartScheduled) return;
    this.#restartScheduled = true;
    // Several attributes changed in one go (a template stamping them) start over once.
    queueMicrotask(() => {
      if (!this.#restartScheduled) return;
      this.#restartScheduled = false;
      if (this.#connected) this.#start();
    });
  }

  // Sizing

  #adoptStyles(root) {
    if (styleSupport() === 'adopted') {
      this.#hostSheet = new CSSStyleSheet();
      root.adoptedStyleSheets = [sharedStyles(), this.#hostSheet];
      return;
    }
    const shared = document.createElement('style');
    shared.textContent = STYLES;
    this.#hostStyleElement = document.createElement('style');
    root.prepend(shared, this.#hostStyleElement);
  }

  #applySizing() {
    const rule = hostStyle({
      width: parseLength(this.getAttribute('width')),
      height: parseLength(this.getAttribute('height')),
      ratio: this.#ratio,
    });
    if (this.#hostSheet !== null) this.#hostSheet.replaceSync(rule);
    else this.#hostStyleElement.textContent = rule;
  }

  #applyTheme() {
    this.#parts.frame.dataset.theme = parseTheme(this.getAttribute('theme'));
  }

  #applyBackground() {
    const { dataset } = this.#parts.frame;
    if (backgroundTransparent(this.getAttribute('background'))) dataset.background = 'transparent';
    else delete dataset.background;
  }

  #setRatio(stage, width, height) {
    if (stage <= this.#ratioStage || !(width > 0) || !(height > 0)) return;
    this.#ratioStage = stage;
    this.#ratio = `${width} / ${height}`;
    this.#applySizing();
  }

  // Language

  #updateLanguage() {
    const lang = languageOf(this);
    if (lang === this.#lang) return;
    this.#lang = lang;
    this.#renderLanguage();
  }

  /** Words everything on screen again, from the state the element keeps for it. */
  #renderLanguage() {
    this.#applyLabels();
    this.#paintLoading();
    this.#paintRecord();
    if (this.#failure !== null) this.#paintError();
    this.#paintSaveStatus();
    this.#renderCaption();
    this.#video?.relocalize();
  }

  /** The words that do not come from a file: buttons and the loader's accessible name. */
  #applyLabels() {
    const p = this.#parts;
    const s = stringsFor(this.#lang);
    p.loading.setAttribute('aria-label', s.loadingLabel);
    p.textDownload.textContent = s.download;
    p.dlFull.textContent = s.download;
    p.dlShort.textContent = s.save;
    this.#paintOpen();
    this.#renderCopy();
  }

  // Starting over

  /** Ends the current generation: aborts every read it started, then lets go of the page's shared state. */
  #reset() {
    this.#controller.abort();
    this.#controller = new AbortController();
    this.#waiting?.stop();
    this.#waiting = null;
    for (const observer of this.#observers) observer.disconnect();
    this.#observers = [];
    this.#visible = false;
    if (this.#frameRequest) cancelAnimationFrame(this.#frameRequest);
    this.#frameRequest = 0;
    this.#pendingProgress = null;
    this.#lastBytes = 0;
    clearTimeout(this.#copiedTimer);

    // The view's reads go first: FileHandle#release does not cancel a handle's own readers, the abort above
    // did for the element's and this does for the player's.
    this.#video?.close();
    this.#video = null;
    this.#handle?.release();
    this.#lease?.release();
    this.#handle = null;
    this.#lease = null;
    this.#record = null;
    this.#link = null;
    this.#endpoints = null;

    this.#phase = 'idle';
    this.#verified = false;
    this.#saving = null;
    this.#saveStatus = null;
    this.#failure = null;
    this.#idleVideo = false;
    this.#downloadView = null;
    this.#textTruncated = false;
    this.#ratio = DEFAULT_RATIO;
    this.#ratioStage = 0;
    this.#clearViews();
    this.#applySizing();
  }

  #clearViews() {
    const p = this.#parts;
    for (const image of [p.cover, p.img]) image.removeAttribute('src');
    for (const url of this.#urls) URL.revokeObjectURL(url);
    this.#urls = [];
    p.cover.hidden = true;
    p.pre.textContent = '';
    p.textBar.hidden = true;
    p.dlNote.hidden = true;
    this.#paintSaveStatus();
    p.frame.classList.remove('video', 'tapped');
    p.idle.classList.remove('covered');
    p.overlay.hidden = true;
    p.overlayText.textContent = '';
    this.#copyFeedback = null;
    this.#renderCopy();
    p.caption.hidden = true;
    for (const view of VIEWS) p[view].hidden = true;
  }

  #start() {
    this.#reset();
    this.#loaderOn = parseLoader(this.getAttribute('loader'));
    // Nothing can be checked on a page the browser does not trust with SubtleCrypto, and nothing is requested.
    if (!canVerify()) { this.#fail(elementError(null, INSECURE)); return; }
    this.#resolveLink();
  }

  // The link

  #linkStatus() {
    const src = this.getAttribute('src');
    const text = this.textContent;
    const parsing = document.readyState === 'loading';
    const status = linkReadiness({ src, text, parsing });
    // The parser may hand over the element's text in pieces; half a link is not yet a link.
    const hasSrc = typeof src === 'string' && src.trim() !== '';
    if (status === 'ready' && parsing && !hasSrc && !isLink(linkText({ src, text }))) return 'wait';
    return status;
  }

  #resolveLink() {
    const status = this.#linkStatus();
    if (status === 'wait') { this.#waitForLink(); return; }
    if (status === 'missing') { this.#fail(elementError('InvalidLink', NO_LINK)); return; }
    this.#configure();
  }

  /** The original form `<xrpl-file>xrpl-file://…</xrpl-file>` can run before the parser has made its children. */
  #waitForLink() {
    const signal = this.#controller.signal;
    const recheck = () => {
      if (signal.aborted) return;
      const status = this.#linkStatus();
      if (status === 'wait') return;
      this.#waiting?.stop();
      this.#waiting = null;
      if (status === 'missing') this.#fail(elementError('InvalidLink', NO_LINK));
      else this.#configure();
    };

    const observer = new MutationObserver(recheck);
    observer.observe(this, { childList: true, characterData: true, subtree: true });
    document.addEventListener('DOMContentLoaded', recheck, { once: true });
    this.#waiting = {
      stop: () => {
        observer.disconnect();
        document.removeEventListener('DOMContentLoaded', recheck);
      },
    };
  }

  #configure() {
    try {
      const text = linkText({ src: this.getAttribute('src'), text: this.textContent });
      const parsed = parseLink(text);
      this.#endpoints = endpointsFor(parsed, this.getAttribute('node'));
      this.#link = parsed;
    } catch (error) {
      this.#fail(error);
      return;
    }

    this.#renderCaption();
    this.#observeVisibility();
    switch (parseLoading(this.getAttribute('loading'))) {
      case 'eager': this.#load(); break;
      case 'click': this.#prepareOnly(); break;
      default: this.#whenNear();
    }
  }

  // When loading starts

  #observeVisibility() {
    if (typeof IntersectionObserver !== 'function') { this.#visible = true; return; }
    const observer = new IntersectionObserver((entries) => {
      this.#visible = entries.at(-1).isIntersecting;
    });
    observer.observe(this);
    this.#observers.push(observer);
  }

  #whenNear() {
    if (typeof IntersectionObserver !== 'function') { this.#load(); return; }
    // One viewport height ahead, so a file is mostly there by the time it scrolls into view.
    const observer = new IntersectionObserver((entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return;
      observer.disconnect();
      this.#observers = this.#observers.filter((o) => o !== observer);
      this.#load();
    }, { rootMargin: '100% 0px' });
    observer.observe(this);
    this.#observers.push(observer);
  }

  #priority = () => (this.#visible ? PRIORITY.NOW : PRIORITY.BACKGROUND);

  // Reading

  #connect() {
    if (this.#handle !== null) return;
    this.#lease = pool.acquire(this.#endpoints);
    this.#handle = this.#lease.files.open(this.#link);
  }

  /** `loading="click"`: the manifest and the cover now, the content when the visitor asks. */
  async #prepareOnly() {
    const signal = this.#controller.signal;
    this.#showLoading(READING, null);
    try {
      this.#connect();
      const { record, cover } = await prepare(this.#handle);
      if (signal.aborted) return;
      this.#record = record;
      this.#renderIdle(record, cover);
    } catch (error) {
      if (!signal.aborted) this.#fail(error);
    }
  }

  #openContent() {
    if (this.#record === null || this.#phase !== 'idle') return;
    this.#load();
  }

  async #load() {
    const signal = this.#controller.signal;
    this.#showLoading(READING, null);
    try {
      this.#connect();
      const record = this.#record ?? await this.#handle.record();
      if (signal.aborted) return;
      this.#record = record;
      this.#renderCaption();

      const { view, reason } = viewForType(record.manifest);
      this.#parts.frame.classList.toggle('video', view === 'video');
      if (view === 'image') await this.#loadImage(record, signal);
      else if (view === 'text') await this.#loadText(record, signal);
      else if (view === 'video') await this.#loadVideo(record, signal);
      else this.#showDownload(record, reason);
    } catch (error) {
      if (!signal.aborted) this.#fail(error);
    }
  }

  async #loadImage(record, signal) {
    const { manifest } = record;
    // The first block decides before the rest is read: a file that is not what its type says never
    // costs more than one block. Block 0 then comes back from the handle's recent blocks.
    const head = await this.#readHead(record, signal);
    if (signal.aborted) return;
    if (!magicAgrees(manifest.type, head)) { this.#showDownload(record, 'mismatch'); return; }

    const bytes = await loadWhole(this.#handle, record, {
      priority: this.#priority, signal, onProgress: (detail, kind) => this.#report(detail, kind),
    });
    if (signal.aborted) return;
    this.#verified = true;

    const url = URL.createObjectURL(new Blob([bytes], { type: normalizeType(manifest.type) }));
    this.#urls.push(url);
    const decoded = this.#decode(this.#parts.img, url, signal);
    if (!(await decoded)) {
      if (!signal.aborted) this.#showDownload(record, 'undecodable');
      return;
    }
    this.#parts.img.alt = downloadName(manifest);
    this.#showPhase('image');
    this.#emitLoad(record);
  }

  async #loadVideo(record, signal) {
    const view = new VideoView({
      root: this.#parts.video,
      handle: this.#handle,
      record,
      priority: this.#priority,
      onProgress: (detail, kind) => this.#report(detail, kind),
      onSize: (kind, width, height) => this.#setRatio(kind === 'cover' ? RATIO_COVER : RATIO_CONTENT, width, height),
      onStatus: (text) => this.#renderLoading(text === null ? READING : () => ({ bytes: text, blocks: '' }), null),
      lang: () => this.#lang,
      onError: (error, state) => this.#videoFailed(error, state),
      onDownload: (reason) => this.#videoUnplayable(record, reason),
      onLoad: () => {
        // The first frame came out of verified blocks only.
        this.#verified = true;
        this.#renderCaption();
        this.#emitLoad(record);
      },
    });
    this.#video = view;

    const outcome = await view.start();
    if (signal.aborted || this.#video !== view) return;
    if (outcome.view === 'download') { this.#videoUnplayable(record, outcome.reason); return; }
    this.#showPhase('video');
  }

  /** The file is offered as a download: the browser cannot play it here. */
  #videoUnplayable(record, reason) {
    this.#video?.close();
    this.#video = null;
    this.#parts.frame.classList.remove('video');
    this.#showDownload(record, reason);
  }

  /** A video that was already on screen keeps showing what it has, with the error at the gap; before that it is the error view. */
  #videoFailed(error, { playing }) {
    if (!playing) { this.#fail(error); return; }
    this.#emit('error', detailFor(error, { playing: true }));
  }

  async #readHead(record, signal) {
    const { manifest } = record;
    const blockBytes = await this.#handle.blockBytes();
    const blockLength = Math.min(blockBytes, manifest.size);
    let chunks = 0;
    return this.#handle.block(0, {
      priority: this.#priority(),
      signal,
      sequential: true,
      onChunk: () => {
        chunks++;
        this.#report(progressDetail({
          bytesDone: Math.min(chunks * manifest.chunk, blockLength),
          bytesTotal: manifest.size,
          blocksVerified: 0,
          blocksTotal: manifest.blocks,
        }), { block: false });
      },
    });
  }

  /** Resolves true once `url` has decoded into `image`, false when the browser cannot show it or this generation ended. */
  #decode(image, url, signal) {
    return new Promise((resolve) => {
      const finish = (ok) => {
        image.removeEventListener('load', onLoad);
        image.removeEventListener('error', onError);
        signal.removeEventListener('abort', onAbort);
        resolve(ok);
      };
      const onLoad = () => finish(true);
      const onError = () => finish(false);
      const onAbort = () => finish(false);
      image.addEventListener('load', onLoad);
      image.addEventListener('error', onError);
      signal.addEventListener('abort', onAbort);
      image.src = url;
    });
  }

  async #loadText(record, signal) {
    const { manifest } = record;
    const bytes = await loadWhole(this.#handle, record, {
      limitBytes: TEXT_LIMIT,
      priority: this.#priority,
      signal,
      onProgress: (detail, kind) => this.#report(detail, kind),
    });
    if (signal.aborted) return;
    if (!magicAgrees(manifest.type, bytes.subarray(0, 64))) { this.#showDownload(record, 'mismatch'); return; }
    this.#verified = true;

    const { text, truncated } = textPreview(bytes);
    const p = this.#parts;
    p.pre.textContent = text;
    p.pre.scrollTop = 0;
    p.textBar.hidden = !truncated;
    this.#textTruncated = truncated;
    if (truncated) this.#paintTextNote();
    this.#showPhase('text');
    this.#emitLoad(record);
  }

  // Views

  #showPhase(phase) {
    this.#phase = phase;
    const p = this.#parts;
    for (const view of VIEWS) p[view].hidden = view !== phase || (view === 'loading' && !this.#loaderOn);
    // With the loader off the box stays empty, so the site's own placeholder behind it shows through.
    p.frame.classList.toggle('bare', phase === 'loading' && !this.#loaderOn);
    this.#renderCaption();
  }

  #showLoading(lines, percent) {
    this.#lastBytes = 0;
    this.#renderLoading(lines, percent);
    this.#showPhase('loading');
  }

  /** `lines(lang)` gives `{ bytes, blocks }`; `blocks` is the second half of the line, which a narrow box leaves out. */
  #renderLoading(lines, percent) {
    this.#loading = { lines, percent };
    this.#paintLoading();
  }

  #paintLoading() {
    const p = this.#parts;
    const { lines, percent } = this.#loading;
    const text = lines(this.#lang);
    p.percent.textContent = percent === null ? '' : `${percent}%`;
    p.progressBytes.textContent = text.bytes;
    p.progressBlocks.textContent = text.blocks;
    p.barFill.style.width = `${percent ?? 0}%`;
    if (percent === null) p.loading.removeAttribute('aria-valuenow');
    else p.loading.setAttribute('aria-valuenow', String(percent));
  }

  #renderIdle(record, cover) {
    const p = this.#parts;
    const { manifest } = record;
    const isVideo = viewForType(manifest).view === 'video';
    p.idleName.textContent = downloadName(manifest);
    p.idleMeta.textContent = formatBytes(manifest.size, this.#lang);
    this.#idleVideo = isVideo;
    this.#paintOpen();
    p.frame.classList.toggle('video', isVideo);

    if (cover) {
      const url = URL.createObjectURL(new Blob([cover.bytes], { type: cover.type }));
      this.#urls.push(url);
      p.cover.src = url;
      p.cover.hidden = false;
    }
    // Over a cover the idle view is a media surface: dark in every theme, so its scrim and text read the same.
    p.idle.classList.toggle('covered', Boolean(cover));
    this.#showPhase('idle');
  }

  #showDownload(record, reason) {
    this.#downloadView = { reason };
    this.#paintDownload();
    this.#showPhase('download');
    this.#emitLoad(record);
  }

  #paintDownload() {
    const p = this.#parts;
    const s = stringsFor(this.#lang);
    const { manifest } = this.#record;
    const name = downloadName(manifest);
    p.dlName.textContent = name;
    // The meta line is three pieces so that a narrow box can keep the size alone.
    p.dlType.textContent = `${normalizeType(manifest.type) || s.unknownType} · `;
    p.dlSize.textContent = formatBytes(manifest.size, this.#lang);
    p.dlNet.textContent = ` · ${this.#link.network}`;
    p.dlBadge.textContent = typeBadge(manifest.type);
    p.dlBadge.title = name;
    // The button's name is its visible word ("Download", "Save" or, in the icon tier, the clipped
    // "Download") followed by this clipped file name, so the name always starts with what is shown.
    p.dlLabel.textContent = name;
    p.dlButton.title = downloadLabel(manifest, this.#lang);
    const noteKey = Object.hasOwn(NOTE_KEYS, this.#downloadView.reason) ? NOTE_KEYS[this.#downloadView.reason] : null;
    p.dlNote.hidden = noteKey === null;
    p.dlNote.textContent = noteKey === null ? '' : s[noteKey];
  }

  /** The parts of the views that show the file's size, worded in the current language. */
  #paintRecord() {
    if (this.#record === null) return;
    this.#parts.idleMeta.textContent = formatBytes(this.#record.manifest.size, this.#lang);
    if (this.#downloadView !== null) this.#paintDownload();
    if (this.#textTruncated) this.#paintTextNote();
  }

  #paintTextNote() {
    const p = this.#parts;
    p.textNote.textContent = stringsFor(this.#lang).textNote(formatBytes(this.#record.manifest.size, this.#lang));
    // A short box cuts the note with an ellipsis; the tooltip keeps all of it.
    p.textNote.title = p.textNote.textContent;
  }

  #paintOpen() {
    const p = this.#parts;
    const s = stringsFor(this.#lang);
    p.open.classList.toggle('play', this.#idleVideo);
    p.open.textContent = this.#idleVideo ? '▶' : s.open;
    p.open.setAttribute('aria-label', this.#idleVideo ? s.play : s.open);
  }

  #fail(error) {
    this.#video?.close();
    this.#video = null;
    // Nothing is read after a failure: the connection is the page's to close, not ours to hold.
    this.#handle?.release();
    this.#lease?.release();
    this.#handle = null;
    this.#lease = null;
    this.#failure = error;
    this.#paintError();
    this.#showPhase('error');
    this.#emit('error', detailFor(error));
  }

  #paintError() {
    const p = this.#parts;
    const sentence = messageFor(this.#failure, { lang: this.#lang });
    // The full sentence stays in the accessible text in every size; the short title and the icon are what a narrow box shows, with the sentence as their tooltip.
    p.msgFull.textContent = sentence;
    p.msgShort.textContent = shortTitleFor(this.#failure, this.#lang);
    p.msgShort.title = sentence;
    p.msgIcon.title = sentence;
  }

  // Progress

  /** Chunks arrive faster than frames: they are folded into one update per animation frame; a verified block is never held back. */
  #report(detail, { block }) {
    if (detail.bytesDone < this.#lastBytes) return;
    this.#lastBytes = detail.bytesDone;
    if (block) {
      if (this.#frameRequest) cancelAnimationFrame(this.#frameRequest);
      this.#frameRequest = 0;
      this.#pendingProgress = null;
      this.#publish(detail);
      return;
    }
    this.#pendingProgress = detail;
    if (this.#frameRequest) return;
    this.#frameRequest = requestAnimationFrame(() => {
      this.#frameRequest = 0;
      const pending = this.#pendingProgress;
      this.#pendingProgress = null;
      if (pending) this.#publish(pending);
    });
  }

  #publish(detail) {
    const percent = detail.bytesTotal > 0 ? Math.min(100, Math.floor((detail.bytesDone / detail.bytesTotal) * 100)) : 0;
    this.#renderLoading((lang) => progressLines(detail, lang), percent);
    this.#emit('xrpl-file-progress', progressDetail(detail));
  }

  // Caption

  #renderCaption() {
    const p = this.#parts;
    // caption="off" is for a page that draws its own copy and download controls; hidden also takes the caption's copy button out of the tab order.
    if (this.#link === null || captionHidden(this.getAttribute('caption'))) { p.caption.hidden = true; return; }

    const link = formatLink(this.#link);
    const { manifest } = this.#record ?? {};
    p.capName.textContent = manifest ? `${downloadName(manifest)} · ${formatBytes(manifest.size, this.#lang)}` : '';
    // The mark follows what was actually read and verified, not the view: a download view reached
    // from the manifest alone has verified nothing.
    const state = captionParts({ phase: this.#verified ? this.#phase : 'idle', network: this.#link.network, lang: this.#lang });
    p.capMark.textContent = state.mark;
    p.capWord.textContent = state.word;
    p.capNetwork.textContent = state.network;
    p.capNet.classList.toggle('ok', state.verified);
    // The smallest box shows the verified mark alone, so the caption says which kind it is.
    p.caption.classList.toggle('verified', state.verified);
    p.caption.title = link;
    p.caption.hidden = false;
    this.#renderCopy();
  }

  /**
   * The copy button. A narrow box has no room for "Copied" next to it, so the feedback goes where a
   * screen reader and a tooltip can reach it: the name, the title and a status region.
   */
  #renderCopy() {
    const p = this.#parts;
    const s = stringsFor(this.#lang);
    const state = this.#copyFeedback;
    const feedback = state === null ? null : (state === 'copied' ? s.copied : s.copyFailed);
    p.copyText.textContent = feedback ?? s.copyLink;
    p.copy.setAttribute('aria-label', feedback ?? s.copyLink);
    p.copyStatus.textContent = feedback ?? '';
    if (feedback !== null) p.copy.title = feedback;
    else if (this.#link !== null) p.copy.title = formatLink(this.#link);
    else p.copy.removeAttribute('title');
    if (state === null) p.copy.removeAttribute('data-state');
    else p.copy.dataset.state = state;
  }

  async #copyLink() {
    if (this.#link === null) return;
    const done = await copyText(formatLink(this.#link), { execCopy: execCopyIn(this.shadowRoot) });
    this.#copyFeedback = done ? 'copied' : 'failed';
    this.#renderCopy();
    clearTimeout(this.#copiedTimer);
    this.#copiedTimer = setTimeout(() => {
      this.#copyFeedback = null;
      this.#renderCopy();
    }, COPIED_MS);
  }

  // Download

  /** The element's own buttons: the outcome is on the status line, so there is nothing to report here. */
  #saveFromButton() {
    if (this.#handle === null || this.#record === null) return;
    this.#download().catch(() => {});
  }

  /**
   * Saves the verified file, the same way the element's own Download button does, and resolves with
   * how it went: `saved` (written through the save dialog), `started` (handed to the browser as a
   * download, whose outcome a page cannot see) or `cancelled` (the visitor closed the dialog).
   * Rejects with the element's error while it shows one, with `NotLoaded` before the file's
   * description is known, and with the reason when the save itself fails: a block that does not
   * verify is never written.
   * @returns {Promise<'saved'|'started'|'cancelled'>}
   */
  save() {
    if (this.#failure !== null) {
      const failure = this.#failure instanceof XrplFileError ? this.#failure : new XrplFileError('Unknown', messageFor(this.#failure), { cause: this.#failure });
      return Promise.reject(failure);
    }
    if (this.#handle === null || this.#record === null) {
      return Promise.reject(new XrplFileError('NotLoaded', 'The file has not been read yet: wait for the load event.'));
    }
    return this.#download();
  }

  /** One save at a time: a second call while one runs gets that one's result. */
  #download() {
    if (this.#saving !== null) return this.#saving;
    // A reset clears #saving while the old run is still ending; only the run that is current clears it.
    const run = this.#runSave().finally(() => { if (this.#saving === run) this.#saving = null; });
    this.#saving = run;
    return run;
  }

  async #runSave() {
    const signal = this.#controller.signal;
    const p = this.#parts;
    for (const button of [p.dlButton, p.textDownload]) button.disabled = true;
    try {
      const result = await saveFile({
        handle: this.#handle,
        record: this.#record,
        signal,
        onProgress: (detail) => this.#showSaving((lang) => stringsFor(lang).savingProgress(bytesOf(detail.bytesDone, detail.bytesTotal, lang))),
      });
      if (signal.aborted) throw abortError();
      this.#showSaving(Object.hasOwn(SAVE_RESULT_KEYS, result) ? saying(SAVE_RESULT_KEYS[result]) : null);
      if (result !== 'cancelled') {
        // Every block verified before it was written or put in the Blob.
        this.#verified = true;
        this.#renderCaption();
      }
      return result;
    } catch (error) {
      // A failed save is the save's problem: the verified file stays on screen and the reason goes
      // to the status line. No `error` event either; that one means the file could not be shown.
      if (!signal.aborted && !isAbort(error)) this.#showSaving((lang) => saveMessageFor(error, lang));
      throw error;
    } finally {
      for (const button of [p.dlButton, p.textDownload]) button.disabled = false;
    }
  }

  /** `say(lang)` gives the status line, or null empties it. */
  #showSaving(say) {
    this.#saveStatus = say;
    this.#paintSaveStatus();
  }

  #paintSaveStatus() {
    const text = this.#saveStatus === null ? '' : this.#saveStatus(this.#lang);
    this.#parts.dlStatus.textContent = text;
    this.#parts.textStatus.textContent = text;
    this.#parts.textStatus.title = text;
  }

  // Events

  #emitLoad(record) {
    this.#emit('load', loadDetail(record.manifest, this.#link));
  }

  #emit(type, detail) {
    this.dispatchEvent(detail === undefined ? new Event(type) : new CustomEvent(type, { detail }));
  }
}
