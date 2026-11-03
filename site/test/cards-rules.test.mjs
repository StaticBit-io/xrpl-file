import test from 'node:test';
import assert from 'node:assert/strict';
import {
  FILE_PICTURE_MAX_BYTES, FILE_PICTURE_TYPES, isFilePictureCandidate, isLinkPreviewRobot, parseCardPath, pictureMagicAgrees,
} from '../cards/rules.mjs';

test('Telegram, X and the common unfurlers are robots; browsers are not', () => {
  for (const ua of ['TelegramBot (like TwitterBot)', 'Twitterbot/1.0', 'facebookexternalhit/1.1',
    'Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)', 'Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)',
    'WhatsApp/2.23.20.0', 'LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)']) {
    assert.equal(isLinkPreviewRobot(ua), true, ua);
  }
  for (const ua of [null, '', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Safari/604.1',
    'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0']) {
    assert.equal(isLinkPreviewRobot(ua), false, String(ua));
  }
});

test('card paths: a file page with or without its slash, and a card picture', () => {
  const h = 'ab'.repeat(32);
  assert.deepEqual(parseCardPath(`/mainnet/${h}`), { kind: 'page', network: 'mainnet', hash: h.toUpperCase() });
  assert.deepEqual(parseCardPath(`/testnet/${h}/`), { kind: 'page', network: 'testnet', hash: h.toUpperCase() });
  assert.deepEqual(parseCardPath(`/og/devnet/${h}`), { kind: 'picture', network: 'devnet', hash: h.toUpperCase() });
});

test('anything else is not a card path', () => {
  const h = 'ab'.repeat(32);
  for (const p of ['/mainnet/xyz', `/mainnet/${h}/extra`, `/mainnet/${h.slice(1)}`, `/21338/${h}`, `/MAINNET/${h}`,
    `/og/mainnet/${h}/`, `/og/21338/${h}`, `/og/${h}`, '/og/', '/mainnet/', '/']) {
    assert.equal(parseCardPath(p), null, p);
  }
});

test('a file is a picture candidate only for the four raster types up to 1 MB', () => {
  assert.equal(FILE_PICTURE_MAX_BYTES, 1_000_000);
  assert.deepEqual(FILE_PICTURE_TYPES, ['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
  for (const type of FILE_PICTURE_TYPES) assert.equal(isFilePictureCandidate({ type, size: 1_000_000 }), true, type);
  assert.equal(isFilePictureCandidate({ type: 'image/png', size: 1_000_001 }), false);
  assert.equal(isFilePictureCandidate({ type: 'image/svg+xml', size: 300 }), false);
  assert.equal(isFilePictureCandidate({ type: 'video/mp4', size: 300 }), false);
});

const ascii = (text) => [...text].map((c) => c.charCodeAt(0));
const bytes = (head) => {
  const out = new Uint8Array(Math.max(32, head.length)).fill(0x5a);
  out.set(head);
  return out;
};

test('magic bytes: PNG, JPEG, WebP and GIF agree with their own signature only', () => {
  const PNG = bytes([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const JPEG = bytes([0xff, 0xd8, 0xff, 0xe0]);
  const WEBP = bytes([...ascii('RIFF'), 0x10, 0, 0, 0, ...ascii('WEBP')]);
  const GIF87 = bytes(ascii('GIF87a'));
  const GIF89 = bytes(ascii('GIF89a'));
  assert.equal(pictureMagicAgrees('image/png', PNG), true);
  assert.equal(pictureMagicAgrees('image/jpeg', JPEG), true);
  assert.equal(pictureMagicAgrees('image/webp', WEBP), true);
  assert.equal(pictureMagicAgrees('image/gif', GIF87), true);
  assert.equal(pictureMagicAgrees('image/gif', GIF89), true);

  assert.equal(pictureMagicAgrees('image/png', JPEG), false);
  assert.equal(pictureMagicAgrees('image/png', PNG.subarray(0, 7)), false);
  assert.equal(pictureMagicAgrees('image/jpeg', PNG), false);
  assert.equal(pictureMagicAgrees('image/webp', GIF89), false);
  assert.equal(pictureMagicAgrees('image/gif', bytes(ascii('GIF88a'))), false);
  assert.equal(pictureMagicAgrees('image/svg+xml', bytes(ascii('<svg xmlns="http://www.w3.org/2000/svg">'))), false);
});
