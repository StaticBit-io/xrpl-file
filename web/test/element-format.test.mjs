import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatBytes, extensionFor, downloadName, typeBadge, downloadLabel, bytesOf, progressLines } from '../src/element/format.mjs';

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

test('a right-to-left override cannot disguise an extension', () => {
  assert.equal(downloadName({ name: 'invoice‮fdp.exe', type: 'application/pdf' }), 'invoicefdp.exe');
});

for (const [label, code] of [
  ['LRM', 0x200e], ['RLM', 0x200f], ['ALM', 0x061c],
  ['LRE', 0x202a], ['RLE', 0x202b], ['PDF', 0x202c], ['LRO', 0x202d], ['RLO', 0x202e],
  ['LRI', 0x2066], ['RLI', 0x2067], ['FSI', 0x2068], ['PDI', 0x2069],
  ['ZWSP', 0x200b], ['BOM', 0xfeff], ['soft hyphen', 0x00ad], ['LS', 0x2028], ['PS', 0x2029],
  ['C1 start', 0x0080], ['C1 NEL', 0x0085], ['C1 end', 0x009f],
])
  test(`U+${code.toString(16).toUpperCase().padStart(4, '0')} (${label}) is stripped from the name`, () => {
    assert.equal(downloadName({ name: `a${String.fromCodePoint(code)}b.txt`, type: 'text/plain' }), 'ab.txt');
  });

test('a name made only of invisible characters falls back', () => {
  assert.equal(downloadName({ name: '‮‏', type: 'text/plain' }), 'xrpl-file.txt');
});

test('a device name hidden behind a format character is still defused', () => {
  assert.equal(downloadName({ name: 'c​on.txt', type: 'text/plain' }), '_con.txt');
});

test('markup in a name stays inert text', () => {
  assert.equal(downloadName({ name: '<img src=x onerror=alert(1)>', type: 'text/plain' }), '_img src=x onerror=alert(1)_');
});

test('"con:" loses its colon and is no longer a device name', () => {
  assert.equal(downloadName({ name: 'con:', type: 'text/plain' }), 'con_');
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

test('the type badge is the media subtype in capitals, at most five characters', () => {
  assert.equal(typeBadge('image/svg+xml'), 'SVG');
  assert.equal(typeBadge('application/pdf'), 'PDF');
  assert.equal(typeBadge('text/plain;charset=utf-8'), 'PLAIN');
  assert.equal(typeBadge('IMAGE/PNG'), 'PNG');
  assert.equal(typeBadge('application/octet-stream'), 'OCTET');
  assert.equal(typeBadge('application/x-tar'), 'TAR');
});

test('a vendor subtype loses its prefix and is capped at five characters', () => {
  const badge = typeBadge('application/vnd.ms-excel');
  assert.equal(badge, 'MSEXC');
  assert.ok(badge.length <= 5);
  assert.ok(typeBadge('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet').length <= 5);
});

test('a missing or odd type gives FILE', () => {
  for (const type of [undefined, null, '', 42, 'weird', 'application/', '/png', 'a/+xml', 'x/ ..'])
    assert.equal(typeBadge(type), 'FILE', String(type));
});

test('the download label names the file by its cleaned display name', () => {
  assert.equal(downloadLabel({ name: 'photo.svg', type: 'image/svg+xml' }), 'Download photo.svg');
  assert.equal(downloadLabel({ name: 'invoice‮fdp.exe', type: 'application/pdf' }), 'Download invoicefdp.exe');
  assert.equal(downloadLabel({ name: '../../evil.exe', type: 'application/octet-stream' }), 'Download _.._evil.exe');
  assert.equal(downloadLabel({ name: '', type: 'image/png' }), 'Download xrpl-file.png');
});

test('bytes are written once with their unit when both numbers share it', () => {
  assert.equal(bytesOf(12_400_000, 36_500_000), '12.4 of 36.5 MB');
  assert.equal(bytesOf(900, 36_500_000), '900 B of 36.5 MB');
});

test('the progress is two lines: the bytes, then the verified blocks', () => {
  assert.deepEqual(progressLines({ bytesDone: 12_400_000, bytesTotal: 36_500_000, blocksVerified: 3, blocksTotal: 8 }),
    { bytes: '12.4 of 36.5 MB', blocks: ' · 3 of 8 blocks verified' });
});
