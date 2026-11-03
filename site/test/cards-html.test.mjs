import test from 'node:test';
import assert from 'node:assert/strict';
import { fileCardHtml } from '../cards/html.mjs';
import { SITE_ORIGIN } from '../src/js/config.mjs';
import { builtSite } from './support/built-site.mjs';

const H = 'AB'.repeat(32);
const ORIGIN = 'https://xrpl-file.staticbit.io';
const PNG_CARD = { status: 'file', title: 'logo.png', description: 'PNG image, 1.5 KB, stored in the XRP Ledger.', picture: 'file' };
const at = { origin: ORIGIN, network: 'mainnet', hash: H };
const count = (html, pattern) => (html.match(pattern) ?? []).length;

test('a file card replaces the title, the description and every og/twitter tag of the static page', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, PNG_CARD, at);
  assert.match(html, /<title>logo\.png<\/title>/);
  assert.ok(html.includes('<meta name="description" content="PNG image, 1.5 KB, stored in the XRP Ledger.">'));
  assert.ok(html.includes('<meta property="og:title" content="logo.png">'));
  assert.ok(html.includes('<meta property="og:description" content="PNG image, 1.5 KB, stored in the XRP Ledger.">'));
  assert.ok(html.includes(`<meta property="og:url" content="${SITE_ORIGIN}/mainnet/${H}">`));
  assert.ok(html.includes(`<meta property="og:image" content="${ORIGIN}/og/mainnet/${H}">`));
  assert.ok(html.includes('<meta property="og:image:alt" content="logo.png">'));
  assert.ok(!html.includes('og:image:width'), 'the picture is not decoded, so its size is not claimed');
  assert.ok(html.includes('<meta name="twitter:card" content="summary_large_image">'));
  for (const tag of ['og:type', 'og:site_name', 'og:title', 'og:description', 'og:url', 'og:image']) {
    assert.equal(count(html, new RegExp(`property="${tag}"`, 'g')), 1, tag);
  }
  assert.equal(count(html, /name="twitter:card"/g), 1);
  assert.equal(count(html, /<title>/g), 1);
  assert.equal(count(html, /name="description"/g), 1);
  assert.ok(!html.includes('og-banner.png'));
});

test('the page outside the head is the static file page, so a robot that follows it gets a working page', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, PNG_CARD, at);
  const body = (page) => page.slice(page.indexOf('</head>'));
  assert.equal(body(html), body(filePage));
});

test('a card picture from another origin is named at that origin, the page URL stays canonical', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, PNG_CARD, { ...at, origin: 'http://127.0.0.1:8788' });
  assert.ok(html.includes(`<meta property="og:image" content="http://127.0.0.1:8788/og/mainnet/${H}">`));
  assert.ok(html.includes(`<meta property="og:url" content="${SITE_ORIGIN}/mainnet/${H}">`));
});

test('a cover card points at the card picture as well', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, { ...PNG_CARD, title: 'clip.mp4', picture: 'cover' }, at);
  assert.ok(html.includes(`<meta property="og:image" content="${ORIGIN}/og/mainnet/${H}">`));
  assert.ok(html.includes('<meta name="twitter:card" content="summary_large_image">'));
});

test('a banner card points og:image at /og-banner.png with its size and uses summary', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, { ...PNG_CARD, title: 'paper.pdf', picture: 'banner' }, at);
  assert.ok(html.includes(`<meta property="og:image" content="${ORIGIN}/og-banner.png">`));
  assert.ok(html.includes('<meta property="og:image:width" content="1200">'));
  assert.ok(html.includes('<meta property="og:image:height" content="630">'));
  assert.ok(html.includes('<meta property="og:image:alt" content="xrpl-file">'));
  assert.ok(html.includes('<meta name="twitter:card" content="summary">'));
  assert.ok(!html.includes('/og/mainnet/'));
});

test('missing and unavailable cards return the static page unchanged', async () => {
  const { filePage } = await builtSite();
  assert.equal(fileCardHtml(filePage, { status: 'missing' }, at), filePage);
  assert.equal(fileCardHtml(filePage, { status: 'unavailable' }, at), filePage);
});

test('a hostile name is escaped and cut to 100 characters everywhere', async () => {
  const { filePage } = await builtSite();
  const name = `"><script>alert(1)</script>&${'x'.repeat(300)}`;
  const html = fileCardHtml(filePage, { ...PNG_CARD, title: name }, at);
  assert.ok(!html.includes('<script>alert'), 'no tag survives');
  const shown = '&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;&amp;';
  const cut = `${shown}${'x'.repeat(100 - 1 - '"><script>alert(1)</script>&'.length)}…`;
  assert.ok(html.includes(`<title>${cut}</title>`));
  assert.ok(html.includes(`<meta property="og:title" content="${cut}">`));
  assert.ok(html.includes(`<meta property="og:image:alt" content="${cut}">`));
  assert.ok(!html.includes('x'.repeat(100)));
});

test('a long name is not cut in the middle of a surrogate pair', async () => {
  const { filePage } = await builtSite();
  const html = fileCardHtml(filePage, { ...PNG_CARD, title: '\u{1F600}'.repeat(150) }, at);
  assert.ok(html.includes(`<title>${'\u{1F600}'.repeat(99)}…</title>`));
});
