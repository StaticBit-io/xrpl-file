import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { topLevelBoxes } from '../src/element/media/boxes.mjs';

const directory = join(dirname(fileURLToPath(import.meta.url)), 'media');

/** A committed test medium, as bytes. */
export function read(name) {
  return new Uint8Array(readFileSync(join(directory, name)));
}

export const expected = JSON.parse(readFileSync(join(directory, 'expected.json'), 'utf8'));

/** Text to bytes, or bytes to text: four-character codes and the like. */
export function ascii(value) {
  if (typeof value === 'string') return new Uint8Array([...value].map((c) => c.charCodeAt(0)));
  return String.fromCharCode(...value);
}

/** A block reader over bytes, the shape locateMoov takes. */
export function reader(bytes, blockBytes) {
  return async (index) => bytes.slice(index * blockBytes, (index + 1) * blockBytes);
}

/** The whole moov box of a medium, header included. */
export function moovOf(name) {
  const bytes = read(name);
  const moov = topLevelBoxes(bytes).find((box) => box.type === 'moov');
  return bytes.subarray(moov.start, moov.end);
}

/** The sync samples of a track from its parsed table, composition times in seconds. */
export function syncSamples(track) {
  const { table } = track;
  const samples = [];
  for (let i = 0; i < table.count; i++) {
    if (table.sync === null || table.sync[i] === 1) {
      samples.push({ cts: table.cts[i] / track.timescale, offset: table.offsets[i] });
    }
  }
  return samples;
}

/** True when a file offset is where some sample of some track begins. */
export function isSampleStart(movie, offset) {
  return movie.tracks.some((track) => track.table?.offsets.includes(offset));
}
