import assert from 'node:assert/strict';
import { test } from 'node:test';
import { exifHasGps, scanMetadata } from './scan-metadata.mjs';

const exifPrefix = Buffer.from([0x45, 0x78, 0x69, 0x66, 0, 0]);
// Little-endian TIFF, one IFD0 entry: the given tag, type LONG, count 1, value 0.
const tiffWithTag = (tag) => Buffer.from([0x49, 0x49, 0x2a, 0, 8, 0, 0, 0, 1, 0, tag & 0xff, tag >> 8, 4, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
const gps = tiffWithTag(0x8825);
const plain = tiffWithTag(0x0112);

function jpegWith(...segments) {
  return Buffer.concat([Buffer.from([0xff, 0xd8]), ...segments, Buffer.from([0xff, 0xda, 0, 2, 0xff, 0xd9])]);
}
function app1(body) {
  return Buffer.concat([Buffer.from([0xff, 0xe1, (body.length + 2) >> 8, (body.length + 2) & 0xff]), body]);
}
function pngWith(...chunks) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const chunk = (type, body) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(body.length, 0);
    head.write(type, 4, 'latin1');
    return Buffer.concat([head, body, Buffer.alloc(4)]);
  };
  return Buffer.concat([signature, ...chunks.map(([t, b]) => chunk(t, b)), chunk('IEND', Buffer.alloc(0))]);
}
function webpWith(...chunks) {
  const body = Buffer.concat(chunks.map(([type, data]) => {
    const head = Buffer.alloc(8);
    head.write(type, 0, 'latin1');
    head.writeUInt32LE(data.length, 4);
    return Buffer.concat([head, data, Buffer.alloc(data.length % 2)]);
  }));
  const header = Buffer.alloc(12);
  header.write('RIFF', 0, 'latin1');
  header.writeUInt32LE(body.length + 4, 4);
  header.write('WEBP', 8, 'latin1');
  return Buffer.concat([header, body]);
}

test('GPS is told apart from other Exif', () => {
  assert.equal(exifHasGps(Buffer.concat([exifPrefix, gps])), true);
  assert.equal(exifHasGps(gps), true);
  assert.equal(exifHasGps(Buffer.concat([exifPrefix, plain])), false);
  assert.equal(exifHasGps(Buffer.from('nope')), false);
});

test('JPEG: Exif, GPS and XMP are named, a clean file is not', () => {
  assert.deepEqual(scanMetadata(jpegWith(app1(Buffer.concat([exifPrefix, plain])))), ['EXIF']);
  assert.deepEqual(scanMetadata(jpegWith(app1(Buffer.concat([exifPrefix, gps])))), ['EXIF with GPS location']);
  assert.deepEqual(scanMetadata(jpegWith(app1(Buffer.from('http://ns.adobe.com/xap/1.0/\0<x/>', 'latin1')))), ['XMP']);
  assert.deepEqual(scanMetadata(jpegWith()), []);
});

test('PNG: text chunks are listed by keyword, eXIf is named', () => {
  const png = pngWith(['tEXt', Buffer.from('Author\0Someone', 'latin1')], ['iTXt', Buffer.from('XML:com.adobe.xmp\0', 'latin1')], ['eXIf', gps]);
  assert.deepEqual(scanMetadata(png), ['EXIF with GPS location', 'text chunks (Author, XML:com.adobe.xmp)']);
  assert.deepEqual(scanMetadata(pngWith()), []);
});

test('WebP: EXIF and XMP chunks are named', () => {
  assert.deepEqual(scanMetadata(webpWith(['EXIF', Buffer.concat([exifPrefix, gps])], ['XMP ', Buffer.from('<x/>')])), ['EXIF with GPS location', 'XMP']);
  assert.deepEqual(scanMetadata(webpWith(['VP8 ', Buffer.alloc(10)])), []);
});

test('something else is not scanned', () => {
  assert.deepEqual(scanMetadata(Buffer.from('plain text')), []);
});
