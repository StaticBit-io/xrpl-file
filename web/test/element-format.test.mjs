import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatBytes, extensionFor, downloadName } from '../src/element/format.mjs';

test('a path cannot climb out of the download folder', () => {
  assert.equal(downloadName({ name: '../../evil.exe', type: 'application/octet-stream' }), '_.._evil.exe');
});

test('characters Windows and POSIX reject become underscores', () => {
  assert.equal(downloadName({ name: 'a<b>:c|d?.txt', type: 'text/plain' }), 'a_b__c_d_.txt');
  assert.equal(downloadName({ name: 'a"b*c\\d.txt', type: 'text/plain' }), 'a_b_c_d.txt');
});

test('control characters are dropped and a device name is defused', () => {
  assert.equal(downloadName({ name: 'con\u0007\u0000.txt', type: 'text/plain' }), '_con.txt');
  assert.equal(downloadName({ name: 'a\u007fb.txt', type: 'text/plain' }), 'ab.txt');
});

for (const [name, out] of [
  ['NUL', '_NUL'],
  ['com1.log', '_com1.log'],
  ['LPT9', '_LPT9'],
  ['aux.tar.gz', '_aux.tar.gz'],
  ['console.txt', 'console.txt'],
  ['com0.txt', 'com0.txt'],
  ['name. . ', 'name'],
])
  test(`downloadName(${JSON.stringify(name)})`, () => {
    assert.equal(downloadName({ name, type: 'text/plain' }), out);
  });

test('a name with nothing left falls back to xrpl-file and the type extension', () => {
  assert.equal(downloadName({ name: '   ', type: 'image/webp' }), 'xrpl-file.webp');
  assert.equal(downloadName({ name: null, type: 'video/mp4' }), 'xrpl-file.mp4');
  assert.equal(downloadName({ name: '...', type: 'text/plain' }), 'xrpl-file.txt');
  assert.equal(downloadName({ name: null, type: 'application/x-unknown' }), 'xrpl-file.bin');
});

test('a long name is cut to 200 characters and keeps its extension', () => {
  const cut = downloadName({ name: 'n'.repeat(500), type: 'text/plain' });
  assert.ok(cut.length <= 200);
  const withExt = downloadName({ name: `${'n'.repeat(500)}.mp4`, type: 'video/mp4' });
  assert.equal(withExt.length, 200);
  assert.ok(withExt.endsWith('.mp4'));
});

test('a cut never leaves half of a surrogate pair', () => {
  const cut = downloadName({ name: '😀'.repeat(150), type: 'text/plain' });
  assert.ok(cut.length <= 200);
  assert.ok(!/[\uD800-\uDBFF]$/.test(cut));
});

test('extensions by type', () => {
  const table = {
    'image/png': 'png', 'image/jpeg': 'jpg', 'image/gif': 'gif', 'image/webp': 'webp', 'image/avif': 'avif',
    'video/mp4': 'mp4', 'video/webm': 'webm', 'video/quicktime': 'mov', 'text/plain': 'txt', 'text/csv': 'csv',
    'text/markdown': 'md', 'application/json': 'json', 'text/html': 'html', 'text/xml': 'xml',
    'image/svg+xml': 'svg', 'application/pdf': 'pdf',
  };
  for (const [type, ext] of Object.entries(table)) assert.equal(extensionFor(type), ext, type);
  assert.equal(extensionFor('IMAGE/PNG; q=1'), 'png');
  assert.equal(extensionFor('application/zip'), 'bin');
});

test('sizes use decimal units with one decimal', () => {
  assert.equal(formatBytes(36_500_000), '36.5 MB');
  assert.equal(formatBytes(999), '999 B');
  assert.equal(formatBytes(0), '0 B');
  assert.equal(formatBytes(12_400), '12.4 KB');
  assert.equal(formatBytes(1_600_000_000), '1.6 GB');
  assert.equal(formatBytes(1000), '1.0 KB');
});
