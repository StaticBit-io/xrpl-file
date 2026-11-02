import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { allRows, failures, renderReport, REPORT_PATH } from '../tools/contrast.mjs';
import { contrast, parseColor } from '../tools/color.mjs';

test('the contrast arithmetic matches known WCAG values', () => {
  assert.equal(contrast(parseColor('#000000'), parseColor('#ffffff')).toFixed(2), '21.00');
  assert.equal(contrast(parseColor('#777777'), parseColor('#ffffff')).toFixed(2), '4.48');
  assert.equal(contrast(parseColor('oklch(1 0 0)'), parseColor('#ffffff')).toFixed(2), '1.00');
});

test('every required text and UI pair meets AA in both themes and for both element palettes', () => {
  const rows = allRows();
  assert.ok(rows.length > 40, 'the table is not empty');
  const bad = failures(rows).map((r) => `${r.group}: ${r.label} ${r.ratio.toFixed(2)} < ${r.minimum}`);
  assert.deepEqual(bad, []);
});

test('the paper is a soft off-white and a soft near-black, never #fff or #000', () => {
  const css = readFileSync(new URL('../src/css/tokens.css', import.meta.url), 'utf8');
  const lightness = (selector) => {
    const block = css.slice(css.indexOf(selector));
    return Number(/--paper:\s*oklch\(([\d.]+)/.exec(block)[1]);
  };
  const light = lightness(':root[data-theme="light"]');
  const dark = lightness(':root[data-theme="dark"]');
  assert.ok(light >= 0.93 && light <= 0.97, `light paper L ${light}`);
  assert.ok(dark > 0.1 && dark < 0.2, `dark paper L ${dark}`);
});

test('contrast.md is the report for the current tokens', () => {
  assert.equal(readFileSync(REPORT_PATH, 'utf8'), renderReport(allRows()), 'run: node site/tools/contrast.mjs');
});
