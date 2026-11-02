import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { formatDate, formatSize, formatXrp, keptDrops, priceRows, reserveSummary } from '../src/js/price.mjs';

const json = JSON.parse(await readFile(new URL('../data/price.json', import.meta.url), 'utf8'));

test('formatXrp turns drops into XRP with locale separators and no padding zeros', () => {
  assert.equal(formatXrp(1_234_500, 'en'), '1.2345');
  assert.equal(formatXrp(1_234_500, 'ru'), '1,2345');
  assert.equal(formatXrp(1_000_000, 'en'), '1');
  assert.equal(formatXrp(270, 'en'), '0.00027');
  assert.equal(formatXrp(1, 'en'), '0.000001');
  assert.equal(formatXrp(0, 'ru'), '0');
});

test('priceRows keeps the JSON order and takes fee, held and returned from it', () => {
  const rows = priceRows(json, 'en');
  assert.equal(rows.length, json.rows.length);
  json.rows.forEach((row, index) => {
    assert.equal(rows[index].fee, formatXrp(row.feeDrops, 'en'));
    assert.equal(rows[index].held, formatXrp(row.heldDrops, 'en'));
    assert.equal(rows[index].returned, formatXrp(row.returnedDrops, 'en'));
    assert.equal(rows[index].size, formatSize(row.bytes, 'en'));
  });
  assert.deepEqual(rows.map((row) => row.size), ['10 KB', '50 KB', '180 KB', '1 MB']);
});

test('priceRows follows the language of the digits and the size units', () => {
  const [first] = priceRows(json, 'ru');
  assert.equal(first.fee, '0,00027');
  assert.equal(first.size, '10 КБ');
});

test('formatSize uses binary kilobytes and megabytes, as the estimate does', () => {
  assert.equal(formatSize(184_320, 'en'), '180 KB');
  assert.equal(formatSize(1_048_576, 'ru'), '1 МБ');
  assert.equal(formatSize(512, 'en'), '512 B');
});

test('formatDate names the UTC calendar day unambiguously', () => {
  assert.equal(formatDate('2026-11-02T01:01:27.1337123+00:00', 'en'), '2 November 2026 UTC');
  assert.equal(formatDate('2026-11-02T01:01:27.1337123+00:00', 'ru'), '2 ноября 2026 г. UTC');
  // 23:30 at UTC-5 is already the next day in UTC; the shown day must be the UTC one.
  assert.equal(formatDate('2026-11-02T23:30:00-05:00', 'en'), '3 November 2026 UTC');
});

test('keptDrops is what the network keeps when every row agrees, otherwise null', () => {
  assert.equal(keptDrops(json), 200_000);
  assert.equal(keptDrops({ rows: [{ heldDrops: 10, returnedDrops: 8 }, { heldDrops: 10, returnedDrops: 7 }] }), null);
  assert.equal(keptDrops({ rows: [] }), null);
});

test('reserveSummary reports one held, returned and kept figure only when the rows agree', () => {
  assert.deepEqual(reserveSummary(json), { heldDrops: 1_000_000, returnedDrops: 800_000, keptDrops: 200_000 });
  assert.equal(reserveSummary({ rows: [{ heldDrops: 10, returnedDrops: 8 }, { heldDrops: 12, returnedDrops: 10 }] }), null);
  assert.equal(reserveSummary({ rows: [{ heldDrops: 10, returnedDrops: 8 }, { heldDrops: 10, returnedDrops: 7 }] }), null);
});
