// Loads mp4box.js on demand, from the origin the element's own script came from.
//
// The library is only needed for a plain MP4 (one whose samples are not already in `moof` boxes), so
// it is a separate file the page fetches when such a file is opened, not part of the element bundle.
// Fetching it from the element's origin, with a Subresource Integrity hash the build writes in, means
// the page trusts nothing new: whoever can swap the library can already swap the element.

/**
 * The build replaces this literal, and every other copy of it in the bundle, with the `sha384-...` hash
 * of the mp4box file it publishes. It must stay the only copy: a second one would become the hash too.
 */
export const MP4BOX_INTEGRITY = '__MP4BOX_INTEGRITY__';

// What an integrity value looks like once the build has written it in; anything else is the placeholder.
const SRI_VALUE = /^sha(256|384|512)-/;

// The unbundled demo page imports this module straight from `src/`; the library sits in `vendor/`
// there, under its versioned name, not next to the module.
const DEV_MODULE_SUFFIX = '/src/element/media/mp4box-loader.mjs';
const DEV_LIBRARY_PATH = '/vendor/mp4box-0.5.4.min.js';
const BUNDLED_LIBRARY_NAME = 'mp4box.min.js';

/**
 * Where the library is served from.
 *
 * @param {string} scriptUrl the absolute URL of the element's script (the bundle, or this module on the dev page)
 * @returns {string}
 * @throws {TypeError} when `scriptUrl` is not an absolute http(s) URL
 */
export function mp4boxUrl(scriptUrl) {
  const url = new URL(scriptUrl);
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new TypeError(`the mp4box script cannot be placed next to ${url.protocol} script URLs`);
  }

  url.search = '';
  url.hash = '';

  if (url.pathname.endsWith(DEV_MODULE_SUFFIX)) {
    url.pathname = url.pathname.slice(0, -DEV_MODULE_SUFFIX.length) + DEV_LIBRARY_PATH;
    return url.href;
  }

  return new URL(BUNDLED_LIBRARY_NAME, url).href;
}

/**
 * Keeps mp4box's parser complaints out of the visitor's console.
 *
 * mp4box logs through a global `Log` object that the script defines next to `MP4Box`, and no level
 * of its `setLogLevel` is quieter than `error`, which is where it reports a box it could not read to
 * the end (QuickTime's `wave` audio entries produce one per file) and then carries on. Nothing a
 * visitor can act on, and a failure that matters reaches `onError` of the file anyway, so the method
 * is replaced instead.
 */
function silence() {
  const log = globalThis.Log;
  if (log && typeof log.getDurationString === 'function' && typeof log.error === 'function') log.error = () => {};
}

// One script per page, however many elements ask. Keyed by URL because the page could in principle
// hold two copies of the element at different versions, each with its own library.
const loads = new Map();

function insertScript(src, integrity) {
  const doc = globalThis.document;
  if (!doc) return Promise.reject(new Error('mp4box can only be loaded into a page'));

  return new Promise((resolve, reject) => {
    const script = doc.createElement('script');
    script.src = src;
    script.async = true;
    // Needed for the integrity check to apply to a script from another origin (a CDN).
    script.crossOrigin = 'anonymous';
    if (SRI_VALUE.test(integrity ?? '')) script.integrity = integrity;

    const hadOwn = Object.prototype.hasOwnProperty.call(globalThis, 'MP4Box');
    const previous = globalThis.MP4Box;

    // The bundle assigns `MP4Box` on the global object. A host page may carry its own copy, possibly
    // another version, that its own code relies on: take ours and give theirs back.
    function restore() {
      if (hadOwn) globalThis.MP4Box = previous;
      else delete globalThis.MP4Box;
    }

    script.onload = () => {
      const loaded = globalThis.MP4Box;
      restore();
      script.onload = script.onerror = null;

      if (loaded && typeof loaded.createFile === 'function') {
        silence();
        resolve(loaded);
      } else {
        script.remove?.();
        reject(new Error('the mp4box script loaded but did not define MP4Box'));
      }
    };

    script.onerror = () => {
      restore();
      script.onload = script.onerror = null;
      script.remove?.();
      reject(new Error(`the mp4box script could not be loaded from ${src}`));
    };

    (doc.head ?? doc.documentElement).append(script);
  });
}

/**
 * The `MP4Box` namespace, loaded once per page.
 *
 * A failed load is forgotten, so the next call tries again instead of replaying the failure.
 *
 * @param {object} options
 * @param {string} options.scriptUrl the element script's own URL; the library is looked up next to it, see {@link mp4boxUrl}
 * @param {string} [options.integrity] a Subresource Integrity value; unset while it is still the build placeholder
 * @returns {Promise<any>}
 */
export function loadMp4Box({ scriptUrl, integrity = MP4BOX_INTEGRITY }) {
  const src = mp4boxUrl(scriptUrl);

  let pending = loads.get(src);
  if (!pending) {
    pending = insertScript(src, integrity);
    loads.set(src, pending);
    pending.catch(() => loads.delete(src));
  }

  return pending;
}
