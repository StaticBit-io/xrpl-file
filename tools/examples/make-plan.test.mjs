import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildPlan, keyOf } from './make-plan.mjs';

const prepared = [
  { file: 'Sunset Over_Sea.webp', type: 'image/webp', role: 'drawing' },
  { file: 'my clip.mp4', type: 'video/mp4', role: 'video' },
  { file: 'my clip-cover.webp', type: 'image/webp', role: 'cover' },
  { file: 'Second.PNG', type: 'image/png', role: 'drawing' },
];

test('keys are kebab-case from the base name', () => {
  assert.equal(keyOf('Sunset Over_Sea.webp'), 'sunset-over-sea');
  assert.equal(keyOf('Second.PNG'), 'second');
  assert.equal(keyOf('a..b.txt'), 'a-b');
});

test('order is cover, video, drawings, text, svg', () => {
  const plan = buildPlan(prepared);
  assert.deepEqual(plan.map((p) => p.file), [
    'my clip-cover.webp', 'my clip.mp4', 'Sunset Over_Sea.webp', 'Second.PNG', 'about.txt', 'chunks.svg',
  ]);
  assert.deepEqual(plan.map((p) => p.type), [
    'image/webp', 'video/mp4', 'image/webp', 'image/png', 'text/plain', 'image/svg+xml',
  ]);
});

test('names are file names and every writer is a', () => {
  for (const p of buildPlan(prepared)) {
    assert.equal(p.name, p.file);
    assert.equal(p.writer, 'a');
  }
});

test('the video names the cover key, nothing else has a cover', () => {
  const plan = buildPlan(prepared);
  const video = plan.find((p) => p.type === 'video/mp4');
  assert.equal(video.cover, 'my-clip-cover');
  assert.equal(plan.findIndex((p) => p.key === video.cover) < plan.indexOf(video), true);
  assert.equal(plan.filter((p) => p.cover !== null).length, 1);
});

test('a missing cover or a duplicate key is refused', () => {
  assert.throws(() => buildPlan(prepared.filter((p) => p.role !== 'cover')), /cover/);
  assert.throws(() => buildPlan([...prepared, { file: 'second.jpg', type: 'image/jpeg', role: 'drawing' }]), /share the key/);
});
