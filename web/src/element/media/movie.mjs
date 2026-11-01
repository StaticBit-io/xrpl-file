// The parts of a `moov` box the player needs: duration, tracks with their codec strings, and for an
// ordinary (non-fragmented) file the sample tables, turned into a seek point with the rule mp4box.js
// 0.5.4 applies in `ISOFile.prototype.seek(t, true)`.
//
// Adapted from E:\GIT\XRPL\xrpl-video-platform src/Platform.Web/wwwroot/js/mp4.mjs: the duration
// sources (`mvhd`, else `mehd`; the platform's third source, counting fragments, needs `moof`
// boxes this module never sees) and the codec strings (`readVideoCodec` avcC profile/compat/level in
// upper-case hex, `readAudioCodec` walking the esds descriptor chain to the audio object type).
// Changed: boxes are walked rather than searched for by marker, the video entry's own type is the
// codec prefix, and hvcC and the sample tables are new.

import { children, find, readBox } from './boxes.mjs';

// Samples over all tracks together. Two million is hours of video plus its audio; a moov claiming more is
// built to exhaust memory, since a few hundred bytes of boxes can declare millions of samples (a constant-size
// stsz has no table to run out of) and each sample costs 29 bytes of arrays.
const MAX_SAMPLES = 2_000_000;

// Bytes of a sample entry that precede its child boxes.
const VISUAL_ENTRY_FIELDS = 78;
const AUDIO_ENTRY_FIELDS = 28;

// Version/flags (4) and the entry count (4) that open most table boxes.
const TABLE_HEADER = 8;

/**
 * Sample table of one track of an ordinary MP4, one element per sample in decode order.
 *
 * @typedef {object} SampleTable
 * @property {number} count
 * @property {Float64Array} cts  composition time, track timescale units (`stts` plus `ctts`)
 * @property {Float64Array} offsets  file offset of each sample (`stsc`, `stco`/`co64`, `stsz`)
 * @property {Uint32Array} sizes
 * @property {Uint8Array | null} sync  1 for a sync sample; null when the track has no `stss`, which makes every sample one
 * @property {number} endTicks  composition time of the last sample plus its duration, where mp4box ends the track
 */

/**
 * @typedef {object} Track
 * @property {number} id
 * @property {'video' | 'audio' | 'other'} kind
 * @property {number} timescale
 * @property {string} codec  e.g. `avc1.64001F`, `mp4a.40.2`, `hvc1.1.6.L120.B0`; the sample entry's four-character code when no string is known for it
 * @property {number} width  pixels; 0 for a track that is not video
 * @property {number} height
 * @property {SampleTable | null} table  null for a fragmented file
 */

/**
 * @typedef {object} Movie
 * @property {number} timescale  movie timescale, units per second
 * @property {number} duration  seconds; 0 when neither `mvhd` nor `mehd` states one
 * @property {boolean} fragmented  `moov` has `mvex`: the samples are in `moof` boxes
 * @property {Track[]} tracks
 */

function hex(value, digits = 2) {
  return value.toString(16).toUpperCase().padStart(digits, '0');
}

function u64(view, at) {
  return view.getUint32(at) * 2 ** 32 + view.getUint32(at + 4);
}

function checkTable(box, entries, entryBytes, name) {
  if (box.end - box.start - box.headerSize - TABLE_HEADER < entries * entryBytes) {
    throw new RangeError(`the ${name} box claims ${entries} entries but is too short for them`);
  }
}

/** Timescale and duration of an `mvhd` or `mdhd` box, which share a layout. */
function readHeaderTimes(view, box) {
  const body = box.start + box.headerSize;

  return view.getUint8(body) === 1
    ? { timescale: view.getUint32(body + 20), duration: u64(view, body + 24) }
    : { timescale: view.getUint32(body + 12), duration: view.getUint32(body + 16) };
}

/** `mehd`'s fragment duration in movie timescale units, 0 when absent. */
function readFragmentDuration(view, bytes, moov) {
  const mehd = find(bytes, moov, 'mvex', 'mehd');
  if (mehd === null) return 0;

  const body = mehd.start + mehd.headerSize;
  return view.getUint8(body) === 1 ? u64(view, body + 4) : view.getUint32(body + 4);
}

function readDuration(view, bytes, moov, mvhd) {
  const { timescale, duration } = readHeaderTimes(view, mvhd);
  if (!(timescale > 0)) return { timescale, seconds: 0 };

  // A version 0 duration of all ones is the format's "not known yet".
  const known = duration > 0 && duration !== 0xffffffff ? duration : readFragmentDuration(view, bytes, moov);
  return { timescale, seconds: known / timescale };
}

function readCodecs(bytes, view, entry, handler) {
  const fields = handler === 'vide' ? VISUAL_ENTRY_FIELDS : handler === 'soun' ? AUDIO_ENTRY_FIELDS : -1;
  const inside = fields < 0 ? [] : children(bytes, { ...entry, headerSize: entry.headerSize + fields });
  const config = (type) => inside.find((box) => box.type === type) ?? null;

  switch (entry.type) {
    case 'avc1':
    case 'avc3': {
      const avcC = config('avcC');
      if (avcC === null) return entry.type;
      const body = avcC.start + avcC.headerSize;
      return `${entry.type}.${hex(bytes[body + 1])}${hex(bytes[body + 2])}${hex(bytes[body + 3])}`;
    }
    case 'hvc1':
    case 'hev1': {
      const hvcC = config('hvcC');
      return hvcC === null ? entry.type : hevcString(bytes, view, entry.type, hvcC);
    }
    case 'mp4a': {
      const esds = config('esds');
      return esds === null ? 'mp4a.40.2' : aacString(bytes, esds);
    }
    default:
      return entry.type.trim();
  }
}

/** ISO/IEC 14496-15 Annex E: `hvc1.<space><profile>.<reversed compat flags>.<tier><level>[.<constraint bytes>]`. */
function hevcString(bytes, view, type, hvcC) {
  const body = hvcC.start + hvcC.headerSize;
  const profileByte = bytes[body + 1];
  const space = ['', 'A', 'B', 'C'][profileByte >> 6];
  const tier = profileByte & 0x20 ? 'H' : 'L';

  // The flags are stored with profile 0 as the most significant bit; the string wants it as the least.
  const flags = view.getUint32(body + 2);
  let reversed = 0;
  for (let bit = 0; bit < 32; bit++) {
    if (flags & (1 << bit)) reversed |= 1 << (31 - bit);
  }

  const constraints = [...bytes.subarray(body + 6, body + 12)];
  while (constraints.length > 0 && constraints.at(-1) === 0) constraints.pop();

  const tail = constraints.map((byte) => `.${hex(byte)}`).join('');
  return `${type}.${space}${profileByte & 0x1f}.${(reversed >>> 0).toString(16).toUpperCase()}.${tier}${bytes[body + 12]}${tail}`;
}

/**
 * The AAC codec string out of an `esds` box.
 *
 * The descriptors are walked, not searched for a tag byte: hunting for 0x05 finds the first byte that
 * happens to equal five, which on the platform's own demo clip gave mp4a.40.16, a codec the file does
 * not contain. Descriptor lengths are seven bits per byte, the top bit meaning "another byte follows".
 */
function aacString(bytes, esds) {
  let at = esds.start + esds.headerSize + 4; // past version and flags
  const end = Math.min(esds.end, bytes.length);
  let objectType = -1;

  while (at < end - 2) {
    const tag = bytes[at++];

    let size = 0;
    for (let i = 0; i < 4 && at < end; i++) {
      const byte = bytes[at++];
      size = (size << 7) | (byte & 0x7f);
      if ((byte & 0x80) === 0) break;
    }

    if (tag === 0x05) {
      if (objectType !== 0x40) break;

      // The audio object type is the top five bits of the AudioSpecificConfig; 31 escapes to six more bits.
      let audioType = bytes[at] >> 3;
      if (audioType === 31) audioType = 32 + (((bytes[at] & 7) << 3) | (bytes[at + 1] >> 5));
      return audioType > 0 ? `mp4a.40.${audioType}` : 'mp4a.40.2';
    }

    if (tag === 0x03) {
      // ES_Descriptor: an id and flags, then optional fields before its child descriptors.
      const flags = bytes[at + 2];
      at += 3;
      if (flags & 0x80) at += 2;
      if (flags & 0x40) at += 1 + bytes[at];
      if (flags & 0x20) at += 2;
    } else if (tag === 0x04) {
      // DecoderConfigDescriptor: object type, stream type, buffer size and two bitrates, then its children.
      objectType = bytes[at];
      at += 13;
    } else {
      at += size;
    }
  }

  // An object type other than MPEG-4 audio (0x40) is its own codec string; MP3 is mp4a.6B.
  return objectType > 0 && objectType !== 0x40 ? `mp4a.${hex(objectType)}` : 'mp4a.40.2';
}

/** The sample count a track's `stsz` or `stz2` declares, read before anything is allocated for it. */
function declaredSamples(bytes, view, trak) {
  const stsz = find(bytes, trak, 'mdia', 'minf', 'stbl', 'stsz') ?? find(bytes, trak, 'mdia', 'minf', 'stbl', 'stz2');
  return stsz === null ? 0 : view.getUint32(stsz.start + stsz.headerSize + 8);
}

function readStsz(view, stsz, fileSize) {
  const body = stsz.start + stsz.headerSize;

  if (stsz.type === 'stsz') {
    const constant = view.getUint32(body + 4);
    const count = view.getUint32(body + 8);
    if (count > MAX_SAMPLES) throw new RangeError(`a track of ${count} samples is not read`);

    // A constant size needs no table bytes to back the count, so the file itself is the bound.
    if (constant !== 0 && count * constant > fileSize) {
      throw new RangeError(`${count} samples of ${constant} bytes do not fit in a file of ${fileSize} bytes`);
    }

    const sizes = new Uint32Array(count);
    if (constant !== 0) {
      sizes.fill(constant);
    } else {
      if (stsz.end - body - 12 < count * 4) throw new RangeError('the stsz box is too short for its sample count');
      for (let i = 0; i < count; i++) sizes[i] = view.getUint32(body + 12 + i * 4);
    }
    return sizes;
  }

  // stz2: the same list at 4, 8 or 16 bits a sample.
  const fieldBits = view.getUint8(body + 7);
  const count = view.getUint32(body + 8);
  if (count > MAX_SAMPLES) throw new RangeError(`a track of ${count} samples is not read`);
  if (![4, 8, 16].includes(fieldBits)) throw new RangeError(`the stz2 box has a ${fieldBits}-bit field size`);
  if (stsz.end - body - 12 < Math.ceil((count * fieldBits) / 8)) throw new RangeError('the stz2 box is too short for its sample count');

  const sizes = new Uint32Array(count);
  for (let i = 0; i < count; i++) {
    if (fieldBits === 16) sizes[i] = view.getUint16(body + 12 + i * 2);
    else if (fieldBits === 8) sizes[i] = view.getUint8(body + 12 + i);
    else {
      const byte = view.getUint8(body + 12 + (i >> 1));
      sizes[i] = i & 1 ? byte & 0x0f : byte >> 4;
    }
  }
  return sizes;
}

/** Composition time of every sample: `stts` decode times plus `ctts` offsets. Also returns the last sample's decode time. */
function readSampleTimes(bytes, view, stbl, count) {
  const stts = find(bytes, stbl, 'stts');
  if (stts === null) throw new Error('the track has no stts box');

  const body = stts.start + stts.headerSize;
  const entries = view.getUint32(body + 4);
  checkTable(stts, entries, 8, 'stts');

  const cts = new Float64Array(count);
  let sample = 0;
  let dts = 0;
  let lastDts = 0;

  for (let entry = 0; entry < entries && sample < count; entry++) {
    const runLength = view.getUint32(body + TABLE_HEADER + entry * 8);
    const delta = view.getUint32(body + TABLE_HEADER + entry * 8 + 4);

    for (let k = 0; k < runLength && sample < count; k++) {
      cts[sample] = dts;
      lastDts = dts;
      dts += delta;
      sample++;
    }
  }

  if (sample < count) throw new Error('the stts box covers fewer samples than the track has');

  const ctts = find(bytes, stbl, 'ctts');
  if (ctts !== null) {
    const cbody = ctts.start + ctts.headerSize;
    const signed = view.getUint8(cbody) === 1; // version 0 offsets are unsigned, as mp4box reads them
    const centries = view.getUint32(cbody + 4);
    checkTable(ctts, centries, 8, 'ctts');

    sample = 0;
    for (let entry = 0; entry < centries && sample < count; entry++) {
      const runLength = view.getUint32(cbody + TABLE_HEADER + entry * 8);
      const at = cbody + TABLE_HEADER + entry * 8 + 4;
      const offset = signed ? view.getInt32(at) : view.getUint32(at);

      for (let k = 0; k < runLength && sample < count; k++) cts[sample++] += offset;
    }
  }

  return { cts, lastDts };
}

/** File offset of every sample from `stsc` (samples per chunk), `stco`/`co64` (chunk offsets) and the sizes. */
function readOffsets(bytes, view, stbl, sizes, fileSize) {
  const stsc = find(bytes, stbl, 'stsc');
  const chunks = find(bytes, stbl, 'stco') ?? find(bytes, stbl, 'co64');
  if (stsc === null || chunks === null) throw new Error('the track has no stsc or stco box');

  const sbody = stsc.start + stsc.headerSize;
  const runs = view.getUint32(sbody + 4);
  checkTable(stsc, runs, 12, 'stsc');

  const cbody = chunks.start + chunks.headerSize;
  const chunkCount = view.getUint32(cbody + 4);
  const wide = chunks.type === 'co64';
  checkTable(chunks, chunkCount, wide ? 8 : 4, chunks.type);

  if (runs === 0 && sizes.length > 0) throw new Error('the stsc box is empty');

  const offsets = new Float64Array(sizes.length);
  let sample = 0;
  let run = 0;

  for (let chunk = 1; chunk <= chunkCount && sample < sizes.length; chunk++) {
    while (run + 1 < runs && view.getUint32(sbody + TABLE_HEADER + (run + 1) * 12) <= chunk) run++;

    const perChunk = view.getUint32(sbody + TABLE_HEADER + run * 12 + 4);
    let at = wide ? u64(view, cbody + TABLE_HEADER + (chunk - 1) * 8) : view.getUint32(cbody + TABLE_HEADER + (chunk - 1) * 4);

    for (let k = 0; k < perChunk && sample < sizes.length; k++) {
      if (at + sizes[sample] > fileSize) throw new RangeError(`sample ${sample} lies past the end of the file`);
      offsets[sample] = at;
      at += sizes[sample];
      sample++;
    }
  }

  if (sample < sizes.length) throw new Error('stsc and stco place fewer samples than stsz lists');
  return offsets;
}

/** 1 for each sync sample, read the way mp4box reads `stss`: the numbers are consumed in order. */
function readSync(bytes, view, stbl, count) {
  const stss = find(bytes, stbl, 'stss');
  if (stss === null) return null;

  const body = stss.start + stss.headerSize;
  const entries = view.getUint32(body + 4);
  checkTable(stss, entries, 4, 'stss');

  const sync = new Uint8Array(count);
  let next = 0;
  for (let i = 0; i < count; i++) {
    if (next < entries && i === view.getUint32(body + TABLE_HEADER + next * 4) - 1) {
      sync[i] = 1;
      next++;
    }
  }
  return sync;
}

/** @returns {SampleTable} */
function readTable(bytes, view, stbl, mediaDuration, fileSize) {
  const stsz = find(bytes, stbl, 'stsz') ?? find(bytes, stbl, 'stz2');
  if (stsz === null) throw new Error('the track has no stsz box');

  const sizes = readStsz(view, stsz, fileSize);
  const count = sizes.length;
  const { cts, lastDts } = readSampleTimes(bytes, view, stbl, count);

  // mp4box gives the last sample whatever the media duration leaves after its decode time, not the last stts delta.
  const lastDuration = count > 0 ? Math.max(mediaDuration - lastDts, 0) : 0;

  return {
    count,
    cts,
    offsets: readOffsets(bytes, view, stbl, sizes, fileSize),
    sizes,
    sync: readSync(bytes, view, stbl, count),
    endTicks: count > 0 ? cts[count - 1] + lastDuration : 0,
  };
}

function readTrack(bytes, view, trak, fragmented, fileSize) {
  const tkhd = find(bytes, trak, 'tkhd');
  const mdhd = find(bytes, trak, 'mdia', 'mdhd');
  const hdlr = find(bytes, trak, 'mdia', 'hdlr');
  const stbl = find(bytes, trak, 'mdia', 'minf', 'stbl');
  if (tkhd === null || mdhd === null || hdlr === null || stbl === null) return null;

  const tbody = tkhd.start + tkhd.headerSize;
  const wide = view.getUint8(tbody) === 1;
  const id = view.getUint32(tbody + (wide ? 20 : 12));
  const size = tbody + (wide ? 88 : 76); // width and height, 16.16 fixed point, after the matrix

  const handler = String.fromCharCode(...bytes.subarray(hdlr.start + hdlr.headerSize + 8, hdlr.start + hdlr.headerSize + 12));
  const kind = handler === 'vide' ? 'video' : handler === 'soun' ? 'audio' : 'other';
  const { timescale, duration } = readHeaderTimes(view, mdhd);

  const stsd = find(bytes, stbl, 'stsd');
  const entry = stsd === null ? null : readBox(bytes, stsd.start + stsd.headerSize + TABLE_HEADER, stsd.end);

  return {
    id,
    kind,
    timescale,
    codec: entry === null ? '' : readCodecs(bytes, view, entry, handler),
    width: kind === 'video' ? view.getUint32(size) / 65536 : 0,
    height: kind === 'video' ? view.getUint32(size + 4) / 65536 : 0,
    table: fragmented ? null : readTable(bytes, view, stbl, duration, fileSize),
  };
}

/**
 * Reads a `moov` box.
 *
 * An ordinary file's sample tables are bounded before they are allocated: at most 2,000,000 samples over all
 * tracks, a constant-size `stsz` must fit in the file, and every sample must lie inside it.
 *
 * @param {Uint8Array} moovBytes the whole box, header included (what `locateMoov` returns)
 * @param {{ fileSize?: number }} [options] size of the whole file; callers that know it pass it, so a table cannot place samples past the end. Without it only the sample caps apply
 * @returns {Movie}
 * @throws {Error} when the bytes are not a complete `moov`, or an ordinary file's sample tables are missing, inconsistent or larger than the caps allow
 */
export function parseMovie(moovBytes, { fileSize = Infinity } = {}) {
  const moov = readBox(moovBytes, 0);
  if (moov === null || moov.type !== 'moov') throw new Error('these bytes are not a moov box');
  if (moov.end > moovBytes.length) throw new Error('the moov box is cut short');

  const view = new DataView(moovBytes.buffer, moovBytes.byteOffset, moovBytes.byteLength);
  const mvhd = find(moovBytes, moov, 'mvhd');
  if (mvhd === null) throw new Error('the moov box has no mvhd');

  const fragmented = find(moovBytes, moov, 'mvex') !== null;
  const { timescale, seconds } = readDuration(view, moovBytes, moov, mvhd);

  const traks = children(moovBytes, moov).filter((box) => box.type === 'trak');

  if (!fragmented) {
    const declared = traks.reduce((sum, trak) => sum + declaredSamples(moovBytes, view, trak), 0);
    if (declared > MAX_SAMPLES) throw new RangeError(`${declared} samples over all tracks are not read`);
  }

  const tracks = traks
    .map((trak) => readTrack(moovBytes, view, trak, fragmented, fileSize))
    .filter((track) => track !== null);

  return { timescale, duration: seconds, fragmented, tracks };
}

/**
 * Where to start reading an ordinary file to play from `seconds`: mp4box.js 0.5.4's
 * `ISOFile.prototype.seek(seconds, true)` over the parsed tables.
 *
 * Per track whose end (last composition time plus last duration) is not before `seconds`: the samples
 * are scanned in decode order up to the first whose composition time is past `seconds` (the first
 * sample is never tested), and the last sync sample seen is the one chosen; a track without `stss`
 * counts every sample as sync. The result is the least offset and the least composition time over
 * those tracks. Edit lists are ignored, as mp4box ignores them. A chosen sample of size 0 is stepped
 * over to the next one for its offset, as mp4box does.
 *
 * @param {Movie} movie
 * @param {number} seconds
 * @returns {{ time: number, offset: number } | null} null when no track has a table that reaches `seconds` (a fragmented file has none; use its `sidx`)
 */
export function seekPoint(movie, seconds) {
  let offset = Infinity;
  let time = Infinity;

  for (const track of movie.tracks) {
    const table = track.table;
    if (table === null || table.count === 0 || !(track.timescale > 0)) continue;
    if (seconds > table.endTicks / track.timescale) continue;

    const limit = seconds * track.timescale;
    let chosen = 0;
    for (let i = 0; i < table.count; i++) {
      if (i > 0 && table.cts[i] > limit) break;
      if (table.sync === null || table.sync[i] === 1) chosen = i;
    }

    const at = table.cts[chosen] / track.timescale;
    let sample = chosen;
    while (table.sizes[sample] === 0 && sample + 1 < table.count) sample++;

    offset = Math.min(offset, table.offsets[sample]);
    time = Math.min(time, at);
  }

  return offset === Infinity ? null : { time, offset };
}

/**
 * The MIME type to open a `SourceBuffer` with: the first video and the first audio codec.
 *
 * @param {Movie} movie
 * @returns {string} e.g. `video/mp4; codecs="avc1.64001F,mp4a.40.2"`
 */
export function mimeFor(movie) {
  const codecs = ['video', 'audio']
    .map((kind) => movie.tracks.find((track) => track.kind === kind)?.codec)
    .filter(Boolean);

  return `video/mp4; codecs="${codecs.join(',')}"`;
}
