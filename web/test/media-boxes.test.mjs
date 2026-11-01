import assert from 'node:assert/strict';
import { test } from 'node:test';
import { children, find, locateMoov, readBox, topLevelBoxes } from '../src/element/media/boxes.mjs';
import { ascii, moovOf, read, reader } from './media-helpers.mjs';

test('the top-level boxes of a faststart file are ftyp, moov, mdat', () => {
  assert.deepEqual(
    topLevelBoxes(read('plain-start.mp4')).map((b) => b.type).filter((t) => t !== 'free'),
    ['ftyp', 'moov', 'mdat'],
  );
});

test('the top-level boxes of a file with moov at the end are ftyp, mdat, moov', () => {
  assert.deepEqual(
    topLevelBoxes(read('plain-end.mp4')).map((b) => b.type).filter((t) => t !== 'free'),
    ['ftyp', 'mdat', 'moov'],
  );
});

test('moov at the end is found without reading the blocks between', async () => {
  const bytes = read('plain-end.mp4');
  const blockBytes = 4096;
  const { offset, bytes: moov, blocks } = await locateMoov(reader(bytes, blockBytes), blockBytes, bytes.length);
  assert.equal(ascii(bytes.subarray(offset + 4, offset + 8)), 'moov');
  assert.deepEqual(moov, bytes.subarray(offset, offset + moov.length));
  const total = Math.ceil(bytes.length / blockBytes);
  assert.ok(blocks.length < total / 2);
  assert.equal(blocks[0], 0);
});

test('moov at the start is found from the first block alone', async () => {
  const bytes = read('plain-start.mp4');
  const blockBytes = 16384;
  const { offset, bytes: moov, blocks } = await locateMoov(reader(bytes, blockBytes), blockBytes, bytes.length);
  assert.equal(ascii(moov.subarray(4, 8)), 'moov');
  assert.deepEqual(moov, bytes.subarray(offset, offset + moov.length));
  assert.deepEqual(blocks, [0]);
});

test('a header that straddles two blocks is still read', async () => {
  const bytes = read('plain-end.mp4');
  // 36 bytes per block puts the header after the 32-byte ftyp across a block edge.
  const blockBytes = 36;
  const { offset, bytes: moov } = await locateMoov(reader(bytes, blockBytes), blockBytes, bytes.length);
  assert.equal(ascii(bytes.subarray(offset + 4, offset + 8)), 'moov');
  assert.deepEqual(moov, bytes.subarray(offset, offset + moov.length));
});

test('a file without a moov is refused', async () => {
  const bytes = read('plain-end.mp4').subarray(0, 5000);
  await assert.rejects(locateMoov(reader(bytes, 4096), 4096, bytes.length));
});

test('a 64-bit box size is read', () => {
  const bytes = new Uint8Array(16);
  new DataView(bytes.buffer).setUint32(0, 1);
  bytes.set(ascii('mdat'), 4);
  new DataView(bytes.buffer).setBigUint64(8, 2n ** 32n + 16n);
  assert.deepEqual(readBox(bytes, 0), { type: 'mdat', start: 0, headerSize: 16, size: 2 ** 32 + 16, end: 2 ** 32 + 16 });
});

test('a box of size 0 runs to the end of the bytes', () => {
  const bytes = new Uint8Array(40);
  bytes.set(ascii('mdat'), 4);
  assert.deepEqual(readBox(bytes, 0), { type: 'mdat', start: 0, headerSize: 8, size: 40, end: 40 });
});

test('a box that claims fewer bytes than its own header is refused', () => {
  const bytes = new Uint8Array(16);
  new DataView(bytes.buffer).setUint32(0, 4);
  bytes.set(ascii('free'), 4);
  assert.throws(() => readBox(bytes, 0), RangeError);
});

test('a header cut by the end of the bytes is reported as partial', () => {
  const bytes = read('plain-start.mp4').subarray(0, 36); // ftyp (32) and half of the next header
  assert.equal(readBox(bytes, 32), null);
  assert.equal(topLevelBoxes(bytes).at(-1).partial, true);
});

test('a 64-bit header cut after its first eight bytes is reported as partial', () => {
  const bytes = new Uint8Array(12);
  new DataView(bytes.buffer).setUint32(0, 1);
  bytes.set(ascii('mdat'), 4);
  assert.equal(readBox(bytes, 0), null);
});

test('children and find walk down to a box by path', () => {
  const bytes = read('plain-start.mp4');
  const moov = topLevelBoxes(bytes).find((b) => b.type === 'moov');
  const kinds = children(bytes, moov).map((b) => b.type);
  assert.equal(kinds[0], 'mvhd');
  assert.equal(kinds.filter((t) => t === 'trak').length, 2);

  const stbl = find(bytes, moov, 'trak', 'mdia', 'minf', 'stbl');
  assert.equal(stbl.type, 'stbl');
  assert.equal(find(bytes, moov, 'trak', 'nope'), null);
  assert.equal(find(bytes, moov, 'mvex'), null);
});

test('moovOf returns a box that starts with its own header', () => {
  const moov = moovOf('plain-end.mp4');
  assert.equal(ascii(moov.subarray(4, 8)), 'moov');
  assert.equal(new DataView(moov.buffer, moov.byteOffset).getUint32(0), moov.length);
});
