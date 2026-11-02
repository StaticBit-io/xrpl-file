// Computes WCAG 2.x contrast for every text/background and UI/background pair the site's tokens
// form, in both themes, plus the <xrpl-file> element's own palettes on the site's paper, and writes
// the table to docs/superpowers/reports/2026-11-02-site/contrast.md.
//
//   node site/tools/contrast.mjs            rewrite the report
//   node site/tools/contrast.mjs --check    fail when a required pair misses its threshold
//
// site/test/contrast.test.mjs runs the same computation, so a token edit that breaks AA fails the suite.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { contrast, over, parseColor, toHex } from './color.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const REPORT_PATH = join(here, '..', '..', 'docs', 'superpowers', 'reports', '2026-11-02-site', 'contrast.md');
const TOKENS_PATH = join(here, '..', 'src', 'css', 'tokens.css');
const BASE_PATH = join(here, '..', 'src', 'css', 'base.css');
const README_PATH = join(here, '..', '..', 'README.md');

const TEXT = 4.5;
const UI = 3;

/** Token values of one theme block, with var() references resolved. */
export function readThemeTokens(css, selector) {
  const start = css.indexOf(selector);
  if (start < 0) throw new Error(`no ${selector} block in tokens.css`);
  const open = css.indexOf('{', start);
  const body = css.slice(open + 1, css.indexOf('}', open));
  const raw = new Map([...body.matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
  const resolve = (name, depth = 0) => {
    const value = raw.get(name);
    if (value === undefined) throw new Error(`token --${name} is not defined in ${selector}`);
    const ref = /^var\(--([a-z0-9-]+)\)$/.exec(value);
    if (!ref) return value;
    if (depth > 5) throw new Error(`token --${name} loops`);
    return resolve(ref[1], depth + 1);
  };
  return Object.fromEntries([...raw.keys()].map((name) => [name, resolve(name)]));
}

const SITE_PAIRS = [
  ['text', 'Body text', 'ink', 'paper', TEXT],
  ['text', 'Body text on raised surface and hover', 'ink', 'paper-2', TEXT],
  ['text', 'Muted text (captions, footer labels, hints)', 'ink-muted', 'paper', TEXT],
  ['text', 'Muted text on raised surface', 'ink-muted', 'paper-2', TEXT],
  ['text', 'Secondary text (body, lead, inactive language)', 'ink-2', 'paper', TEXT],
  ['text', 'Secondary text on raised surface', 'ink-2', 'paper-2', TEXT],
  ['text', 'Secondary text on the hovered control', 'ink-2', 'paper-3', TEXT],
  ['text', 'Text on the hovered control', 'ink', 'paper-3', TEXT],
  ['text', 'Accent as text (reserved for found and verified labels)', 'accent', 'paper', TEXT],
  ['text', 'Accent as text on raised surface', 'accent', 'paper-2', TEXT],
  ['text', 'Text on an accent fill (primary button)', 'accent-ink', 'accent', TEXT],
  ['text', 'Text on the hovered accent fill', 'accent-ink', 'accent-hi', TEXT],
  ['text', 'Text on an ink fill (skip link)', 'paper', 'ink', TEXT],
  ['text', 'Error message', 'error', 'paper', TEXT],
  ['text', 'Error message on raised surface', 'error', 'paper-2', TEXT],
  ['ui', 'Focus ring', 'focus', 'paper', UI],
  ['ui', 'Focus ring on raised surface', 'focus', 'paper-2', UI],
  ['ui', 'Focus ring on the hovered or pressed control', 'focus', 'paper-3', UI],
  ['ui', 'Control border (theme switch, buttons, link field)', 'rule', 'paper', UI],
  ['ui', 'Control border on raised surface (link field)', 'rule', 'paper-2', UI],
  ['ui', 'Verified check mark (hero card, on the cloud ground)', 'accent', 'paper', UI],
  ['ui', 'Unread cell outline', 'cell', 'paper-2', UI],
  ['ui', 'Lit cell', 'cell-lit', 'paper-2', UI],
  ['ui', 'Verified cell', 'cell-verified', 'paper-2', UI],
  ['info', 'Decorative hairline (exempt: carries no information)', 'rule-soft', 'paper', null],
  ['info', 'Corner ticks and section rules (exempt: decorative)', 'rule-2', 'paper', null],
  ['info', 'Lit cell against verified cell (state also shown in text)', 'cell-lit', 'cell-verified', null],
];

function row(group, kind, label, foreground, background, minimum) {
  const ratio = contrast(foreground.color, background.color);
  return {
    group,
    kind,
    label,
    foreground: foreground.name,
    background: background.name,
    foregroundHex: toHex(foreground.color),
    backgroundHex: toHex(background.color),
    ratio,
    minimum,
    pass: minimum === null ? null : ratio >= minimum,
  };
}

export function siteRows(css) {
  const rows = [];
  for (const [theme, selector] of [['Light', ':root[data-theme="light"]'], ['Dark', ':root[data-theme="dark"]']]) {
    const tokens = readThemeTokens(css, selector);
    const colour = (name) => ({ name: `--${name}`, color: parseColor(tokens[name]) });
    for (const [kind, label, foreground, background, minimum] of SITE_PAIRS) {
      rows.push(row(`${theme} theme`, kind, label, colour(foreground), colour(background), minimum));
    }
  }
  return rows;
}

/** The element's documented palettes: README "Embed a file", columns Property | Dark | Light. */
export function readElementPalettes(readme) {
  const palettes = { Dark: {}, Light: {} };
  for (const match of readme.matchAll(/^\|\s*`--xrpl-file-([a-z-]+)`\s*\|\s*`([^`]+)`\s*\|\s*`([^`]+)`\s*\|/gm)) {
    palettes.Dark[match[1]] = match[2];
    palettes.Light[match[1]] = match[3];
  }
  for (const name of ['accent', 'on-accent', 'background', 'foreground', 'muted', 'danger', 'text-background', 'text-foreground']) {
    if (!palettes.Dark[name] || !palettes.Light[name]) throw new Error(`README has no --xrpl-file-${name} row`);
  }
  return palettes;
}

// The element's documented overlay (README, "Embed a file"): rgba(0,0,0,.72) with #ddd text, in both themes.
const OVERLAY_ALPHA = 0.72;
const OVERLAY_FOREGROUND = '#dddddd';

const ELEMENT_PAIRS = [
  ['text', 'Names, percentages, badge', 'foreground', 'background', TEXT],
  ['text', 'Sizes, progress details, notes', 'muted', 'background', TEXT],
  ['text', 'Error message', 'danger', 'background', TEXT],
  ['text', 'Text on the accent (buttons)', 'on-accent', 'accent', TEXT],
  ['text', 'Text file', 'text-foreground', 'text-background', TEXT],
  ['ui', 'Buttons, progress, spinner, focus ring against the box', 'accent', 'background', UI],
];

/** What base.css sets on `xrpl-file`: --xrpl-file-<name> -> the site token it points at. */
export function readElementOverrides(baseCss) {
  const css = baseCss.replace(/\/\*[\s\S]*?\*\//g, '');
  const overrides = {};
  for (const rule of css.matchAll(/(?<=^|\})\s*xrpl-file\s*\{([^{}]*)\}/g)) {
    for (const m of rule[1].matchAll(/--xrpl-file-([a-z-]+):\s*var\(--([a-z0-9-]+)\)\s*;/g)) overrides[m[1]] = m[2];
  }
  return overrides;
}

export function elementRows(css, readme, baseCss = readFileSync(BASE_PATH, 'utf8')) {
  const palettes = readElementPalettes(readme);
  const overrides = readElementOverrides(baseCss);
  const rows = [];
  for (const [theme, selector] of [['Light', ':root[data-theme="light"]'], ['Dark', ':root[data-theme="dark"]']]) {
    const site = readThemeTokens(css, selector);
    // The file page puts the element in a --paper-2 frame. base.css replaces the element's accent, the
    // text on it and its muted text with the page's, in both themes; every other colour is the documented palette.
    const paper = { name: `site --paper-2 (${theme.toLowerCase()})`, color: parseColor(site['paper-2']) };
    const palette = { ...palettes[theme] };
    const fromSite = new Set();
    for (const name of ['accent', 'on-accent', 'muted']) {
      if (overrides[name]) { palette[name] = site[overrides[name]]; fromSite.add(name); }
    }
    const colour = (name) => ({ name: `--xrpl-file-${name}${fromSite.has(name) ? ' (site)' : ''}`, color: parseColor(palette[name]) });
    const group = `Element, theme="${theme.toLowerCase()}"`;
    for (const [kind, label, foreground, background, minimum] of ELEMENT_PAIRS) {
      rows.push(row(group, kind, label, colour(foreground), colour(background), minimum));
    }
    rows.push(row(group, 'ui', 'Accent against the frame the element sits in (the element is embedded with the page theme)', colour('accent'), paper, UI));
    rows.push(row(group, 'ui', 'Accent against a text file box', colour('accent'), colour('text-background'), UI));
    // The progress bar's fill sits on its track: the track composited over the box.
    const trackOver = theme === 'Light'
      ? { name: 'track rgba(0,0,0,.16) over --xrpl-file-background', color: over([0, 0, 0], 0.16, parseColor(palette.background)) }
      : { name: 'track rgba(255,255,255,.15) over --xrpl-file-background', color: over([1, 1, 1], 0.15, parseColor(palette.background)) };
    rows.push(row(group, 'ui', 'Progress fill against its track', colour('accent'), trackOver, UI));
    rows.push(row(group, 'info', 'Element box against its frame (a picture frame: not a control, exempt)', colour('background'), paper, null));

    // background="transparent" (the hero card, a picture on the file page) drops the box, so whatever the
    // element draws before the picture arrives (loader, notes, an error) sits on the site's own ground:
    // the file page's --paper-2, and the hero card's --paper (the card has no backing of its own).
    const pageGrounds = [paper, { name: `site --paper (${theme.toLowerCase()}, hero card)`, color: parseColor(site.paper) }];
    for (const ground of pageGrounds) {
      rows.push(row(group, 'text', `Transparent box: names, percentages, badge on the ${ground.name}`, colour('foreground'), ground, TEXT));
      rows.push(row(group, 'text', `Transparent box: sizes, progress details, notes on the ${ground.name}`, colour('muted'), ground, TEXT));
      rows.push(row(group, 'text', `Transparent box: error message on the ${ground.name}`, colour('danger'), ground, TEXT));
      rows.push(row(group, 'ui', `Transparent box: buttons, progress, focus ring on the ${ground.name}`, colour('accent'), ground, UI));
    }

    // The overlay (caption, progress over a cover, idle cover) is dark in both themes. It draws
    // --xrpl-file-overlay-accent, else --xrpl-file-accent when the host sets it, else the element's #4da3ff.
    const overlayName = (name) => {
      for (const candidate of [`overlay-${name}`, name]) if (overrides[candidate]) return { name: `--xrpl-file-${candidate} (site)`, hex: site[overrides[candidate]] };
      return { name: `--xrpl-file-${name} (element default)`, hex: palettes.Dark[name] };
    };
    const overlayAccentSource = overlayName('accent');
    const overlayOnAccentSource = overlayName('on-accent');
    const overlayAccent = { name: overlayAccentSource.name, color: parseColor(overlayAccentSource.hex) };
    const overlayOnAccent = { name: overlayOnAccentSource.name, color: parseColor(overlayOnAccentSource.hex) };
    const overlayText = { name: '--xrpl-file-overlay-foreground', color: parseColor(OVERLAY_FOREGROUND) };
    const grounds = [
      { name: 'overlay over black', color: parseColor('#000000') },
      { name: 'overlay over a white cover', color: over([0, 0, 0], OVERLAY_ALPHA, parseColor('#ffffff')) },
    ];
    for (const ground of grounds) {
      rows.push(row(group, 'ui', `Progress ring and bar, focus ring on the ${ground.name}`, overlayAccent, ground, UI));
      rows.push(row(group, 'text', `Overlay text on the ${ground.name}`, overlayText, ground, TEXT));
    }
    rows.push(row(group, 'text', 'Text on the accent button over a cover', overlayOnAccent, overlayAccent, TEXT));
  }
  return rows;
}

export function allRows(css = readFileSync(TOKENS_PATH, 'utf8'), readme = readFileSync(README_PATH, 'utf8')) {
  return [...siteRows(css), ...elementRows(css, readme)];
}

export function failures(rows) {
  return rows.filter((r) => r.pass === false);
}

function formatRatio(value) {
  return `${(Math.floor(value * 100) / 100).toFixed(2)}:1`;
}

export function renderReport(rows) {
  const groups = [...new Set(rows.map((r) => r.group))];
  const failed = failures(rows).length;
  const lines = [
    '# Site contrast',
    '',
    'WCAG 2.2 AA, computed from `site/src/css/tokens.css` and the `<xrpl-file>` palettes in the README',
    '("Embed a file") by `node site/tools/contrast.mjs`; `site/test/contrast.test.mjs` fails when a required pair',
    'misses its threshold or when this file is out of date. Ratios are the WCAG 2.x relative-luminance',
    'contrast, truncated (never rounded up) to two decimals.',
    '',
    '- Text: at least 4.5:1. Non-text UI and graphics that carry information: at least 3:1.',
    '- Rows marked "exempt" are shown for the record and have no threshold.',
    '- Every token the stylesheets use for a colour is in `tokens.css`; `--paper` is OKLCH L 0.945 in the light',
    '  theme and 0.135 in the dark one, never `#fff` or `#000`.',
    '',
    `Result: ${failed === 0 ? 'every required pair passes.' : `${failed} required pair(s) FAIL.`}`,
    '',
  ];
  for (const group of groups) {
    lines.push(`## ${group}`, '', '| Pair | Foreground | Background | Ratio | Needs | Result |', '|---|---|---|---|---|---|');
    for (const r of rows.filter((x) => x.group === group)) {
      const needs = r.minimum === null ? 'exempt' : `${r.minimum}:1`;
      const result = r.pass === null ? 'n/a' : r.pass ? 'pass' : 'FAIL';
      lines.push(`| ${r.label} | \`${r.foreground}\` ${r.foregroundHex} | \`${r.background}\` ${r.backgroundHex} | ${formatRatio(r.ratio)} | ${needs} | ${result} |`);
    }
    lines.push('');
  }
  lines.push(
    '## Notes',
    '',
    '- The element is always embedded with the page theme (`chrome.mjs` sets its `theme` attribute with the',
    '  switch), so a light element on a dark paper, or the reverse, does not occur.',
    "- `base.css` replaces the element's `--xrpl-file-accent` and `--xrpl-file-on-accent` with the page's",
    "  `--accent` and `--accent-ink` in both themes (moss in light), so the element's buttons and spinner carry the",
    '  same signal colour as the page. The other element colours are the documented palette of the README.',
    "- The element draws its overlay accent (`--xrpl-file-overlay-accent`, the page's `--overlay-accent`) on the",
    '  overlay below. It is a lime in both themes, because moss on that overlay falls below 3:1.',
    '- Over a cover and on its caption the element draws on a dark overlay (rgba(0,0,0,.72)) in both themes.',
    '  The "overlay over black" and "overlay over a white cover" rows are its two extremes (the second is the',
    '  overlay composited over pure white, the brightest cover possible).',
    '- The unread cell is drawn as an outline at 3:1, so an empty mosaic reads as graph paper; the lit and',
    '  verified cells are fills. The mosaic also states its progress in text, so no state depends on colour alone.',
    '',
  );
  return lines.join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const rows = allRows();
  if (process.argv.includes('--check')) {
    for (const r of failures(rows)) console.error(`FAIL ${r.group}: ${r.label} ${formatRatio(r.ratio)} < ${r.minimum}:1`);
    process.exitCode = failures(rows).length > 0 ? 1 : 0;
  } else {
    writeFileSync(REPORT_PATH, renderReport(rows));
    console.log(`wrote ${REPORT_PATH} (${rows.length} rows, ${failures(rows).length} failing)`);
  }
}
