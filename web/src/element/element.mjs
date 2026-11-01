// The <xrpl-file> custom element: it shows a file stored on the XRP Ledger, reading it through the
// page's shared connection and showing nothing that did not verify. Decisions that need no DOM live
// in host.mjs, load.mjs, view.mjs and attributes.mjs, where node --test pins them; this file is the
// DOM glue and is checked in a browser.
import { TEMPLATE, STYLES } from './template.mjs';
import { hostStyle, linkReadiness, captionState, styleSupport } from './host.mjs';
import { prepare, loadWhole, progressDetail } from './load.mjs';
import { pool, PRIORITY } from './page.mjs';
import { saveFile } from './download.mjs';
import { parseLength, parseLoading, parseLoader, linkText, endpointsFor } from './attributes.mjs';
import { viewForType, magicAgrees, normalizeType } from './view.mjs';
import { textPreview, TEXT_LIMIT } from './text.mjs';
import { VideoView } from './video.mjs';
import { formatBytes, downloadName } from './format.mjs';
import { elementError, messageFor, detailFor, canVerify, INSECURE, NO_LINK } from './messages.mjs';
import { parseLink, formatLink } from '../link.mjs';

const DEFAULT_RATIO = '16 / 9';
// Ratio stages, each applied at most once and only ever forward: the reserved box, the cover, the content.
const RATIO_COVER = 1;
const RATIO_CONTENT = 2;

const VIEWS = ['idle', 'loading', 'image', 'video', 'text', 'download', 'error'];
const COPIED_MS = 1500;

const NOTE_TOO_LARGE = 'This picture is too large to show here.';
const NOTE_MISMATCH = "This file's contents do not match its type, so it is offered as a download.";
const NOTE_UNDECODABLE = 'This browser cannot show this picture.';
const NOTE_UNPLAYABLE = 'This browser cannot play this video here.';
const NOTES = { 'too-large': NOTE_TOO_LARGE, mismatch: NOTE_MISMATCH, undecodable: NOTE_UNDECODABLE, unplayable: NOTE_UNPLAYABLE };

const isAbort = (error) => error?.name === 'AbortError';

function isLink(text) {
  try {
    parseLink(text);
    return true;
  } catch {
    return false;
  }
}

/** "12.4 of 36.5 MB": the unit is written once when both numbers share it. */
function bytesOf(done, total) {
  const [doneValue, doneUnit] = formatBytes(done).split(' ');
  const totalText = formatBytes(total);
  return totalText.endsWith(` ${doneUnit}`) ? `${doneValue} of ${totalText}` : `${formatBytes(done)} of ${totalText}`;
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
  static observedAttributes = ['src', 'width', 'height', 'loading', 'loader', 'node'];

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
  #saving = false;
  #loaderOn = true;
  #ratio = DEFAULT_RATIO;
  #ratioStage = 0;
  #urls = [];
  #copiedTimer = 0;
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
    this.#applySizing();
  }

  connectedCallback() {
    this.#connected = true;
    this.#restartScheduled = false;
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
    dlButton.addEventListener('click', () => this.#save());
    textDownload.addEventListener('click', () => this.#save());
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

  #setRatio(stage, width, height) {
    if (stage <= this.#ratioStage || !(width > 0) || !(height > 0)) return;
    this.#ratioStage = stage;
    this.#ratio = `${width} / ${height}`;
    this.#applySizing();
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
    this.#saving = false;
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
    for (const status of [p.dlStatus, p.textStatus]) status.textContent = '';
    p.frame.classList.remove('video', 'tapped');
    p.overlay.hidden = true;
    p.overlayText.textContent = '';
    p.copy.textContent = 'Copy link';
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
    this.#showLoading('Reading the file description…', null);
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
    this.#showLoading('Reading the file description…', null);
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
    this.#emit('load');
  }

  async #loadVideo(record, signal) {
    const view = new VideoView({
      root: this.#parts.video,
      handle: this.#handle,
      record,
      priority: this.#priority,
      onProgress: (detail, kind) => this.#report(detail, kind),
      onSize: (kind, width, height) => this.#setRatio(kind === 'cover' ? RATIO_COVER : RATIO_CONTENT, width, height),
      onStatus: (text) => this.#renderLoading(text ?? 'Reading the file description…', null),
      onError: (error, state) => this.#videoFailed(error, state),
      onDownload: (reason) => this.#videoUnplayable(record, reason),
      onLoad: () => {
        // The first frame came out of verified blocks only.
        this.#verified = true;
        this.#renderCaption();
        this.#emit('load');
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
    if (truncated) p.textNote.textContent = `Showing the first 1 MB of ${formatBytes(manifest.size)}.`;
    this.#showPhase('text');
    this.#emit('load');
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

  #showLoading(detail, percent) {
    this.#lastBytes = 0;
    this.#renderLoading(detail, percent);
    this.#showPhase('loading');
  }

  #renderLoading(detail, percent) {
    const p = this.#parts;
    p.percent.textContent = percent === null ? '' : `${percent}%`;
    p.progressDetail.textContent = detail;
    p.barFill.style.width = `${percent ?? 0}%`;
    if (percent === null) p.loading.removeAttribute('aria-valuenow');
    else p.loading.setAttribute('aria-valuenow', String(percent));
  }

  #renderIdle(record, cover) {
    const p = this.#parts;
    const { manifest } = record;
    const isVideo = viewForType(manifest).view === 'video';
    p.idleName.textContent = downloadName(manifest);
    p.idleMeta.textContent = formatBytes(manifest.size);
    p.open.classList.toggle('play', isVideo);
    p.open.textContent = isVideo ? '▶' : 'Open';
    p.open.setAttribute('aria-label', isVideo ? 'Play' : 'Open');
    p.frame.classList.toggle('video', isVideo);

    if (cover) {
      const url = URL.createObjectURL(new Blob([cover.bytes], { type: cover.type }));
      this.#urls.push(url);
      p.cover.src = url;
      p.cover.hidden = false;
    }
    this.#showPhase('idle');
  }

  #showDownload(record, reason) {
    const p = this.#parts;
    const { manifest } = record;
    p.dlName.textContent = downloadName(manifest);
    p.dlMeta.textContent = `${normalizeType(manifest.type) || 'unknown type'} · ${formatBytes(manifest.size)} · ${this.#link.network}`;
    const note = NOTES[reason] ?? null;
    p.dlNote.hidden = note === null;
    p.dlNote.textContent = note ?? '';
    this.#showPhase('download');
    this.#emit('load');
  }

  #fail(error) {
    this.#video?.close();
    this.#video = null;
    this.#parts.message.textContent = messageFor(error);
    // Nothing is read after a failure: the connection is the page's to close, not ours to hold.
    this.#handle?.release();
    this.#lease?.release();
    this.#handle = null;
    this.#lease = null;
    this.#showPhase('error');
    this.#emit('error', detailFor(error));
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
    this.#renderLoading(
      `${bytesOf(detail.bytesDone, detail.bytesTotal)} · ${detail.blocksVerified} of ${detail.blocksTotal} blocks verified`,
      percent);
    this.#emit('xrpl-file-progress', progressDetail(detail));
  }

  // Caption

  #renderCaption() {
    const p = this.#parts;
    if (this.#link === null) { p.caption.hidden = true; return; }

    const link = formatLink(this.#link);
    const { manifest } = this.#record ?? {};
    p.capName.textContent = manifest ? `${downloadName(manifest)} · ${formatBytes(manifest.size)}` : '';
    // The mark follows what was actually read and verified, not the view: a download view reached
    // from the manifest alone has verified nothing.
    const state = captionState({ phase: this.#verified ? this.#phase : 'idle', network: this.#link.network });
    p.capNet.textContent = state.text;
    p.capNet.classList.toggle('ok', state.verified);
    p.caption.title = link;
    p.caption.hidden = false;
  }

  async #copyLink() {
    if (this.#link === null) return;
    const copy = this.#parts.copy;
    let done = false;
    try {
      await navigator.clipboard.writeText(formatLink(this.#link));
      done = true;
    } catch { /* clipboard access can be refused; the label says so */ }
    copy.textContent = done ? 'Copied' : 'Copy failed';
    clearTimeout(this.#copiedTimer);
    this.#copiedTimer = setTimeout(() => { copy.textContent = 'Copy link'; }, COPIED_MS);
  }

  // Download

  async #save() {
    if (this.#saving || this.#handle === null || this.#record === null) return;
    const signal = this.#controller.signal;
    const p = this.#parts;
    this.#saving = true;
    for (const button of [p.dlButton, p.textDownload]) button.disabled = true;
    try {
      const result = await saveFile({
        handle: this.#handle,
        record: this.#record,
        signal,
        onProgress: (detail) => this.#showSaving(`Saving… ${bytesOf(detail.bytesDone, detail.bytesTotal)}`),
      });
      if (signal.aborted) return;
      this.#showSaving(result === 'saved' ? 'Saved.' : '');
      if (result === 'saved') {
        // Every block verified on the way to the disk.
        this.#verified = true;
        this.#renderCaption();
      }
    } catch (error) {
      if (!signal.aborted && !isAbort(error)) this.#fail(error);
    } finally {
      this.#saving = false;
      for (const button of [p.dlButton, p.textDownload]) button.disabled = false;
    }
  }

  #showSaving(text) {
    this.#parts.dlStatus.textContent = text;
    this.#parts.textStatus.textContent = text;
  }

  // Events

  #emit(type, detail) {
    this.dispatchEvent(detail === undefined ? new Event(type) : new CustomEvent(type, { detail }));
  }
}
