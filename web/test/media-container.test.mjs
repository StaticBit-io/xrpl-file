import assert from 'node:assert/strict';
import { test } from 'node:test';
import { containerAgrees, detectContainer } from '../src/element/media/container.mjs';
import { ascii, read } from './media-helpers.mjs';

for (const f of ['plain-start.mp4', 'plain-end.mp4', 'fragmented.mp4', 'fragmented-sidx.mp4', 'hevc.mp4']) {
  test(`${f} is an ISO file`, () => {
    assert.equal(detectContainer(read(f)), 'iso', f);
  });
}

test('a WebM file is recognized, with or without Cues', () => {
  assert.equal(detectContainer(read('clip.webm')), 'webm');
  assert.equal(detectContainer(read('clip-live.webm')), 'webm');
});

test('the first block is enough to decide', () => {
  assert.equal(detectContainer(read('plain-end.mp4').subarray(0, 64)), 'iso');
  assert.equal(detectContainer(read('clip.webm').subarray(0, 64)), 'webm');
});

test('text and empty input are unknown', () => {
  assert.equal(detectContainer(new TextEncoder().encode('hello world')), 'unknown');
  assert.equal(detectContainer(new Uint8Array(0)), 'unknown');
  assert.equal(detectContainer(new Uint8Array(3)), 'unknown');
});

test('an EBML file that is not WebM is unknown', () => {
  const bytes = read('clip.webm').slice();
  const at = bytes.findIndex((_, i) => ascii(bytes.subarray(i, i + 4)) === 'webm');
  bytes.set(ascii('mkv_'), at);
  assert.equal(detectContainer(bytes), 'unknown');
});

test('a known first box with a size smaller than its header is unknown', () => {
  const bytes = new Uint8Array(16);
  new DataView(bytes.buffer).setUint32(0, 4);
  bytes.set(ascii('ftyp'), 4);
  assert.equal(detectContainer(bytes), 'unknown');
});

test('a MIME type agrees with the container it names', () => {
  assert.equal(containerAgrees('video/mp4', 'webm'), false);
  assert.equal(containerAgrees('video/quicktime', 'iso'), true);
  assert.equal(containerAgrees('video/mp4', 'iso'), true);
  assert.equal(containerAgrees('video/webm', 'webm'), true);
  assert.equal(containerAgrees('video/webm', 'iso'), false);
  assert.equal(containerAgrees('video/mp4; codecs="avc1.64001F"', 'iso'), true);
  assert.equal(containerAgrees('Video/MP4', 'iso'), true);
  assert.equal(containerAgrees('video/mp4', 'unknown'), false);
  assert.equal(containerAgrees('image/png', 'iso'), false);
});
