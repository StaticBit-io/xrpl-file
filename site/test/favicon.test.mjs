import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { inflateSync } from 'node:zlib';
import { ICO_SIZES, TOUCH, TOUCH_SIZE, iconShapes, iconSvg, rasterize } from '../tools/favicon.mjs';
import { MARK_CELLS } from '../chrome-markup.mjs';

const src = (name) => readFileSync(new URL(`../src/${name}`, import.meta.url));

/** Width, height and RGBA pixels of a PNG written by encodePng (8-bit RGBA, unfiltered rows). */
function decodePng(bytes) {
  assert.deepEqual([...bytes.subarray(0, 8)], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  let offset = 8;
  let width = 0;
  const data = [];
  while (offset < bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const body = bytes.subarray(offset + 8, offset + 8 + length);
    if (type === 'IHDR') width = body.readUInt32BE(0);
    if (type === 'IDAT') data.push(body);
    offset += 12 + length;
  }
  const rows = inflateSync(Buffer.concat(data));
  const pixels = Buffer.alloc(width * width * 4);
  for (let y = 0; y < width; y++) {
    assert.equal(rows[y * (width * 4 + 1)], 0, 'unfiltered rows');
    rows.copy(pixels, y * width * 4, y * (width * 4 + 1) + 1, (y + 1) * (width * 4 + 1));
  }
  return { width, pixels };
}

function icoImages(bytes) {
  assert.equal(bytes.readUInt16LE(2), 1, 'an icon, not a cursor');
  const count = bytes.readUInt16LE(4);
  return Array.from({ length: count }, (_, i) => {
    const entry = 6 + i * 16;
    const size = bytes[entry] || 256;
    const png = bytes.subarray(bytes.readUInt32LE(entry + 12), bytes.readUInt32LE(entry + 12) + bytes.readUInt32LE(entry + 8));
    return { size, ...decodePng(png) };
  });
}

test('favicon.svg is what the generator writes: run node site/tools/favicon.mjs after a change', () => {
  assert.equal(src('favicon.svg').toString('utf8'), iconSvg());
});

test('favicon.ico holds 16, 32 and 48 px images drawn by the generator', () => {
  const images = icoImages(src('favicon.ico'));
  assert.deepEqual(images.map((image) => image.size), ICO_SIZES);
  for (const image of images) {
    assert.equal(image.width, image.size);
    assert.ok(image.pixels.equals(rasterize(image.size)), `${image.size} px is out of date`);
  }
});

test('apple-touch-icon.png is 180 px, opaque to the edge, with the outlined mark', () => {
  const { width, pixels } = decodePng(src('apple-touch-icon.png'));
  assert.equal(width, TOUCH_SIZE);
  assert.ok(pixels.equals(rasterize(TOUCH_SIZE, TOUCH)), 'out of date');
  for (let i = 3; i < pixels.length; i += 4) assert.equal(pixels[i], 255, 'iOS rounds the corners itself; a transparent corner would show black');
});

test('every cell of the mark is told apart from its neighbours at 16 px', () => {
  const size = 16;
  const pixels = rasterize(size);
  const cells = iconShapes().slice(1);
  const grey = (shape) => {
    const x = Math.floor(((shape.x + shape.w / 2) / 32) * size);
    const y = Math.floor(((shape.y + shape.h / 2) / 32) * size);
    const i = (y * size + x) * 4;
    return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
  };
  const centres = cells.map(grey);
  const lit = centres.filter((_, i) => MARK_CELLS[i][2] === 'is-lit');
  const empty = centres.filter((_, i) => MARK_CELLS[i][2] === '');
  assert.ok(Math.min(...lit) - Math.max(...empty) > 80, `lit ${lit} against empty ${empty}`);
});
