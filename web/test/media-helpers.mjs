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

const WEBM_TOP_LEVEL = new Set([0x18538067, 0x114d9b74, 0x1549a966, 0x1654ae6b, 0x1f43b675, 0x1c53bb6b, 0x1043a770, 0x1254c367, 0x1941a469]);

/** An EBML ID (marker kept) or size (marker stripped; null when all ones) at `at`, written independently of src/. */
function vint(bytes, at, keepMarker) {
  const first = bytes[at];
  let length = 1;
  while (length <= 8 && !(first & (0x80 >> (length - 1)))) length++;
  let value = keepMarker ? first : first & (0xff >> length);
  let ones = (first & (0xff >> length)) === 0xff >> length;
  for (let i = 1; i < length; i++) {
    value = value * 256 + bytes[at + i];
    if (bytes[at + i] !== 0xff) ones = false;
  }
  return { value: !keepMarker && ones ? null : value, length };
}

/** The top-level elements of a WebM file from its first Cluster: `{ id, start, end }`; an unknown-size Cluster ends at the next top-level ID. */
export function webmTopLevel(bytes) {
  let at = 0;
  const out = [];
  while (at < bytes.length) {
    const id = vint(bytes, at, true);
    const size = vint(bytes, at + id.length, false);
    const dataStart = at + id.length + size.length;
    if (id.value === 0x18538067) {
      at = dataStart;
      continue;
    }

    let end = size.value === null ? bytes.length : dataStart + size.value;
    if (size.value === null) {
      for (let c = dataStart; c < bytes.length; ) {
        const childId = vint(bytes, c, true);
        if (WEBM_TOP_LEVEL.has(childId.value)) { end = c; break; }
        const childSize = vint(bytes, c + childId.length, false);
        c += childId.length + childSize.length + childSize.value;
      }
    }
    out.push({ id: id.value, start: at, dataStart, end });
    at = end;
  }
  return out;
}

/** The Clusters of a whole WebM file, as byte slices in file order. */
export function webmClusters(bytes) {
  return webmTopLevel(bytes).filter((element) => element.id === 0x1f43b675).map((element) => bytes.subarray(element.start, element.end));
}

/** `{ track, timecode }` of every SimpleBlock and BlockGroup block of one Cluster, in file order, read independently of src/. */
export function clusterBlocks(cluster) {
  const id = vint(cluster, 0, true);
  const size = vint(cluster, id.length, false);
  const out = [];
  const timecodeOf = (dataStart) => {
    const track = vint(cluster, dataStart, false);
    const at = dataStart + track.length;
    return { track: track.value, timecode: ((cluster[at] << 8) | cluster[at + 1]) << 16 >> 16 };
  };

  for (let c = id.length + size.length; c < cluster.length; ) {
    const childId = vint(cluster, c, true);
    const childSize = vint(cluster, c + childId.length, false);
    const dataStart = c + childId.length + childSize.length;
    if (childId.value === 0xa3) out.push(timecodeOf(dataStart));
    if (childId.value === 0xa0) {
      for (let g = dataStart; g < dataStart + childSize.value; ) {
        const gId = vint(cluster, g, true);
        const gSize = vint(cluster, g + gId.length, false);
        if (gId.value === 0xa1) out.push(timecodeOf(g + gId.length + gSize.length));
        g += gId.length + gSize.length + gSize.value;
      }
    }
    c = dataStart + childSize.value;
  }
  return out;
}

/** True when no block of the Cluster is earlier than the one before it, whatever the track. */
export function blocksInOrder(cluster) {
  const timecodes = clusterBlocks(cluster).map((block) => block.timecode);
  return timecodes.every((value, i) => i === 0 || value >= timecodes[i - 1]);
}
