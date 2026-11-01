import { test } from 'node:test';
import assert from 'node:assert/strict';
import { IMAGE_MAX_BYTES, normalizeType, viewForType, magicAgrees } from '../src/element/view.mjs';

const m = (type, size) => ({ type, size });
const ascii = (s) => Uint8Array.from(s, (c) => c.charCodeAt(0));
const bytes = (...parts) => Uint8Array.from(parts.flatMap((p) => (typeof p === 'string' ? [...ascii(p)] : p)));
const u32 = (n) => [(n >>> 24) & 0xff, (n >>> 16) & 0xff, (n >>> 8) & 0xff, n & 0xff];

const PNG = bytes([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], [0, 0, 0, 13]);
const RIFF_WEBP = bytes('RIFF', [0x24, 0, 0, 0], 'WEBP', 'VP8 ');
const RIFF_AVI = bytes('RIFF', [0x24, 0, 0, 0], 'AVI ', 'LIST');
const FTYP_AVIF = bytes(u32(24), 'ftyp', 'avif', u32(0), 'avif', 'mif1');
const FTYP_ISOM = bytes(u32(24), 'ftyp', 'isom', u32(512), 'isom', 'iso2');

test('the type loses its case and parameters', () => {
  assert.equal(normalizeType('TEXT/PLAIN; charset=utf-8'), 'text/plain');
  assert.equal(normalizeType(' image/PNG '), 'image/png');
  assert.equal(normalizeType(null), '');
});

test('raster image types get the image view', () => {
  for (const t of ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/avif'])
    assert.equal(viewForType(m(t, 1000)).view, 'image', t);
});

test('an image above the decode ceiling is offered as a download, not decoded', () => {
  assert.deepEqual(viewForType(m('image/png', 1_600_000_000)), { view: 'download', reason: 'too-large' });
  assert.equal(viewForType(m('image/png', 20_000_000)).view, 'image');
  assert.equal(IMAGE_MAX_BYTES, 20_000_000);
});

test('svg is never rendered', () => {
  assert.deepEqual(viewForType(m('image/svg+xml', 1000)), { view: 'download', reason: 'unsupported' });
});

test('mp4, webm and quicktime get the video view', () => {
  for (const t of ['video/mp4', 'video/webm', 'video/quicktime']) assert.equal(viewForType(m(t, 1000)).view, 'video', t);
});

test('other video, audio and documents are downloads', () => {
  for (const t of ['video/x-msvideo', 'audio/mpeg', 'application/pdf', 'application/zip'])
    assert.deepEqual(viewForType(m(t, 1000)), { view: 'download', reason: 'unsupported' }, t);
});

test('text types get the text view', () => {
  for (const t of ['text/plain', 'TEXT/PLAIN; charset=utf-8', 'text/csv', 'text/markdown', 'application/json', 'text/html', 'text/xml', 'text/x-log'])
    assert.deepEqual(viewForType(m(t, 1000)), { view: 'text', reason: null }, t);
});

test('png magic', () => {
  assert.equal(magicAgrees('image/png', PNG), true);
  assert.equal(magicAgrees('image/jpeg', PNG), false);
});

test('jpeg magic', () => {
  assert.equal(magicAgrees('image/jpeg', Uint8Array.of(0xff, 0xd8, 0xff, 0xe0)), true);
  assert.equal(magicAgrees('image/jpeg', Uint8Array.of(0xff, 0xd8, 0x00)), false);
});

test('webp needs RIFF and WEBP, not just RIFF', () => {
  assert.equal(magicAgrees('image/webp', RIFF_WEBP), true);
  assert.equal(magicAgrees('image/webp', RIFF_AVI), false);
});

test('gif magic accepts both versions', () => {
  assert.equal(magicAgrees('image/gif', ascii('GIF87a')), true);
  assert.equal(magicAgrees('image/gif', ascii('GIF89a')), true);
  assert.equal(magicAgrees('image/gif', ascii('GIF90a')), false);
});

test('avif needs an avif or avis brand, major or compatible', () => {
  assert.equal(magicAgrees('image/avif', FTYP_AVIF), true);
  assert.equal(magicAgrees('image/avif', FTYP_ISOM), false);
  assert.equal(magicAgrees('image/avif', bytes(u32(20), 'ftyp', 'mif1', u32(0), 'avis')), true);
  assert.equal(magicAgrees('image/avif', bytes(u32(16), 'ftyp', 'mif1', u32(0))), false);
});

test('a brand past the ftyp box is not a compatible brand', () => {
  assert.equal(magicAgrees('image/avif', bytes(u32(16), 'ftyp', 'isom', u32(0), 'avif')), false);
});

test('text must not start like a binary file', () => {
  assert.equal(magicAgrees('text/plain', PNG), false);
  assert.equal(magicAgrees('text/plain', ascii('hello')), true);
  assert.equal(magicAgrees('application/json', ascii('%PDF-1.7')), false);
  assert.equal(magicAgrees('text/csv', bytes('PK', [3, 4])), false);
  assert.equal(magicAgrees('text/plain', Uint8Array.of(0xff, 0xd8, 0xff)), false);
  assert.equal(magicAgrees('text/plain', ascii('GIF89a')), false);
  assert.equal(magicAgrees('text/plain', RIFF_WEBP), false);
  assert.equal(magicAgrees('text/plain', FTYP_ISOM), false);
  assert.equal(magicAgrees('text/plain', Uint8Array.of(0x1a, 0x45, 0xdf, 0xa3)), false);
  assert.equal(magicAgrees('text/plain', new Uint8Array()), true);
});

test('an empty head agrees with no image type', () => {
  assert.equal(magicAgrees('image/png', new Uint8Array()), false);
});

test('video containers are checked elsewhere, so they agree here', () => {
  assert.equal(magicAgrees('video/mp4', PNG), true);
});
