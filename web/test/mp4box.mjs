/**
 * mp4box.js outside a browser.
 *
 * Adapted from StaticBit's XRPL video platform (a private repository), tests/Platform.Web.Js/helpers/mp4box.mjs: the same `vm`
 * sandbox, pointed at this repository's vendored `web/vendor/mp4box-0.5.4.min.js`. The library is a
 * UMD bundle that expects `window`; node has none, so it is evaluated in a context with a stand-in.
 * Loading the vendored file rather than an npm copy is the point: a test exercises the bytes the
 * element serves. Changed: the loader is async and counts its calls (`loadMp4Box.calls`), because the
 * element must not load the library for a file that does not need it, and the platform's
 * `remuxSample` is left out.
 */
import fs from 'node:fs';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

let cached = null;

function load() {
  if (cached) return cached;

  const source = fs.readFileSync(join(here, '..', 'vendor', 'mp4box-0.5.4.min.js'), 'utf8');
  const sandbox = { window: {}, console, setTimeout, clearTimeout, TextDecoder, TextEncoder };
  sandbox.self = sandbox;
  sandbox.globalThis = sandbox;

  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);

  cached = sandbox.MP4Box ?? sandbox.window.MP4Box;
  if (!cached) throw new Error('the mp4box bundle did not define MP4Box');

  // Same as the element's loader: QuickTime audio entries make the parser log an error per file
  // and carry on; it would only drown the test output.
  sandbox.Log.error = () => {};

  return cached;
}

/** The MP4Box namespace, evaluated once; `calls` counts how often a feed asked for it. */
export async function loadMp4Box() {
  loadMp4Box.calls++;
  return load();
}
loadMp4Box.calls = 0;

/** The MP4Box namespace without counting the call: for a test's own cross-checks. */
export function mp4box() {
  return load();
}
