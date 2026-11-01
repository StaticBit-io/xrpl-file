// Rebuilds an ordinary MP4 as the fragmented MP4 the element streams.
// Usage: node tools/fixtures/fragment.mjs <path to the platform's remux.mjs> <in.mp4> <out.mp4>
//
// Adapted from E:\GIT\XRPL\xrpl-video-platform: the vm loader is that repository's
// tests/Platform.Web.Js/helpers/mp4box.mjs, and the segmentation is the one `remuxSample` there and
// `toFragmented` in src/Platform.Web/wwwroot/js/remux.mjs do (one segment per track, whole track).
// `assembleFragmented` is imported from the platform's own remux.mjs, by file URL, rather than
// copied: the fixture then has exactly the layout the video platform publishes (merged moov, sidx,
// fragments interleaved by time), which is what the element has to play.
import fs from 'node:fs';
import vm from 'node:vm';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const mp4boxPath = join(here, '..', '..', 'web', 'vendor', 'mp4box-0.5.4.min.js');

/** The MP4Box namespace, from the vendored bundle: the same bytes the page will run. */
function loadMp4Box() {
  const source = fs.readFileSync(mp4boxPath, 'utf8');
  const sandbox = { window: {}, console, setTimeout, clearTimeout, TextDecoder, TextEncoder };
  sandbox.self = sandbox;
  sandbox.globalThis = sandbox;

  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);

  const library = sandbox.MP4Box ?? sandbox.window.MP4Box;
  if (!library) throw new Error('the mp4box bundle did not define MP4Box');

  return library;
}

function segment(MP4Box, bytes) {
  const file = MP4Box.createFile();
  const inits = [];
  const segments = [];
  const tracks = [];

  return new Promise((resolvePromise, reject) => {
    file.onError = (error) => reject(new Error(`the input could not be read: ${error}`));

    file.onReady = (info) => {
      if (!info.tracks?.length) {
        reject(new Error('the input has no tracks'));
        return;
      }

      for (const track of info.tracks) {
        tracks.push({ id: track.id, timescale: track.timescale, type: track.type });
        file.setSegmentOptions(track.id, null, { nbSamples: Number.MAX_SAFE_INTEGER });
      }

      for (const init of file.initializeSegmentation()) {
        inits.push(new Uint8Array(init.buffer));
      }

      file.start();
    };

    file.onSegment = (id, user, buffer) => {
      segments.push({ id, bytes: new Uint8Array(buffer) });
      if (segments.length >= inits.length) resolvePromise({ inits, segments, tracks });
    };

    const copy = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    copy.fileStart = 0;
    file.appendBuffer(copy);
    file.flush();
  });
}

if (process.argv.length !== 5) {
  console.error('usage: node fragment.mjs <remux.mjs> <in.mp4> <out.mp4>');
  process.exit(2);
}

const [, , remuxPath, inputPath, outputPath] = process.argv;
const { assembleFragmented } = await import(pathToFileURL(resolve(remuxPath)).href);

const parts = await segment(loadMp4Box(), new Uint8Array(fs.readFileSync(inputPath)));
fs.writeFileSync(outputPath, assembleFragmented(parts));
console.log(`${outputPath}: ${fs.statSync(outputPath).size} bytes, ${parts.tracks.length} tracks`);
