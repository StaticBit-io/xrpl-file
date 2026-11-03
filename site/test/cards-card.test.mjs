import test from 'node:test';
import assert from 'node:assert/strict';
import { readCard, readPicture, describeFile, cardTitle } from '../cards/card.mjs';
import { FakeSource, smallFileLedger } from '../../web/test/vectors.mjs';
import { decodeManifest, encodeManifest } from '../../web/src/manifest.mjs';
import { hexUtf8, utf8Hex } from '../../web/src/hex.mjs';
import { XrplFileError } from '../../web/src/link.mjs';

const ascii = (text) => [...text].map((c) => c.charCodeAt(0));
function image(head, size) {
  const bytes = new Uint8Array(size).fill(0x5a);
  bytes.set(head);
  return bytes;
}
const PNG = image([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], 1500);
const WEBP = image([...ascii('RIFF'), 0x10, 0, 0, 0, ...ascii('WEBP'), ...ascii('VP8 ')], 1500);
const PDF = image(ascii('%PDF-1.7'), 3000);
const MP4 = image([0, 0, 0, 0x18, ...ascii('ftypmp42')], 5000);

/** The file's manifest transaction with `patch` applied to the manifest. */
function patchManifest(file, patch) {
  const tx = file.transactions.find((t) => t.hash === file.hash);
  const manifest = { ...decodeManifest(hexUtf8(tx.memoDataHex)), ...patch };
  tx.memoDataHex = utf8Hex(encodeManifest(manifest));
  return file;
}

async function file({ label = 'file', bytes, type, name = null, cover = null, firstLedger = 1000 }) {
  return patchManifest(await smallFileLedger({ label, bytes, type, firstLedger, cover }), { name });
}

const ON = (hash) => ({ network: 'testnet', hash });

test('a named PNG: its name, type and size, and the file itself as the picture', async () => {
  const png = await file({ bytes: PNG, type: 'image/png', name: 'logo.png' });
  const { card, cover } = await readCard(new FakeSource(png.transactions), ON(png.hash));
  assert.deepEqual(card, { status: 'file', title: 'logo.png', description: 'PNG image, 1.5 KB, stored in the XRP Ledger.', picture: 'file' });
  assert.equal(cover, null);
});

test('a video with a WebP cover: the cover is read now and returned', async () => {
  const coverFile = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const video = await file({ bytes: MP4, type: 'video/mp4', name: 'clip.mp4', cover: coverFile.hash });
  const { card, cover } = await readCard(new FakeSource([...coverFile.transactions, ...video.transactions]), ON(video.hash));
  assert.deepEqual(card, { status: 'file', title: 'clip.mp4', description: 'MP4 video, 5.0 KB, stored in the XRP Ledger.', picture: 'cover' });
  assert.deepEqual(cover, { bytes: WEBP, type: 'image/webp' });
});

test('a cover that fails section 4.1 gives the banner', async () => {
  const coverFile = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990, writer: 'rSomeoneElse' });
  const video = await file({ bytes: MP4, type: 'video/mp4', cover: coverFile.hash });
  const { card, cover } = await readCard(new FakeSource([...coverFile.transactions, ...video.transactions]), ON(video.hash));
  assert.equal(card.picture, 'banner');
  assert.equal(cover, null);
});

test('a PDF without a cover gives the banner and a document description', async () => {
  const pdf = await file({ bytes: PDF, type: 'application/pdf', name: 'paper.pdf' });
  const { card, cover } = await readCard(new FakeSource(pdf.transactions), ON(pdf.hash));
  assert.deepEqual(card, { status: 'file', title: 'paper.pdf', description: 'PDF document, 3.0 KB, stored in the XRP Ledger.', picture: 'banner' });
  assert.equal(cover, null);
});

test('no name: the title is xrpl-file and the first eight hash characters', async () => {
  const pdf = await file({ bytes: PDF, type: 'application/pdf' });
  const { card } = await readCard(new FakeSource(pdf.transactions), ON(pdf.hash));
  assert.equal(card.title, `xrpl-file ${pdf.hash.slice(0, 8)}`);
});

test('a name of only invisible or blank characters counts as no name', () => {
  const hash = '1A2B3C4D'.repeat(8);
  assert.equal(cardTitle({ name: '‮ ​ ' }, hash), 'xrpl-file 1A2B3C4D');
  assert.equal(cardTitle({ name: 'inv‮oice.pdf' }, hash), 'invoice.pdf');
  assert.equal(cardTitle({ name: null }, hash), 'xrpl-file 1A2B3C4D');
});

test('an unknown hash is missing; a source that throws is unavailable', async () => {
  const unknown = await readCard(new FakeSource([]), ON('AB'.repeat(32)));
  assert.deepEqual(unknown, { card: { status: 'missing' }, cover: null, coverUnread: false });

  const broken = {
    async transaction() { throw new XrplFileError('NodeUnreachable', 'down'); },
    async *accountTransactions() { throw new Error('down'); },
    async earliestLedger() { throw new Error('down'); },
  };
  assert.deepEqual(await readCard(broken, ON('AB'.repeat(32))), { card: { status: 'unavailable' }, cover: null, coverUnread: false });
  assert.deepEqual(await readPicture(broken, ON('AB'.repeat(32))), { picture: null, outcome: 'transient' });
});

test('a node without the history is unavailable, not missing', async () => {
  const source = new FakeSource([], 5_000_000);
  assert.deepEqual((await readCard(source, ON('AB'.repeat(32)))).card, { status: 'unavailable' });
});

test('readPicture: the file itself when it is a picture whose bytes agree', async () => {
  const png = await file({ bytes: PNG, type: 'image/png' });
  assert.deepEqual(await readPicture(new FakeSource(png.transactions), ON(png.hash)), { picture: { bytes: PNG, type: 'image/png' }, outcome: 'final' });
});

test('readPicture: a PNG manifest over non-PNG bytes falls back to the cover, then to null', async () => {
  const coverFile = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const lying = await file({ bytes: PDF, type: 'image/png', cover: coverFile.hash });
  assert.deepEqual(await readPicture(new FakeSource([...coverFile.transactions, ...lying.transactions]), ON(lying.hash)),
    { picture: { bytes: WEBP, type: 'image/webp' }, outcome: 'final' });

  const bare = await file({ bytes: PDF, type: 'image/png' });
  assert.deepEqual(await readPicture(new FakeSource(bare.transactions), ON(bare.hash)), { picture: null, outcome: 'final' }, 'verified bytes do not change');
});

test('readPicture: an SVG with no cover has no picture', async () => {
  const svg = await file({ bytes: image(ascii('<svg xmlns="http://www.w3.org/2000/svg"/>'), 600), type: 'image/svg+xml' });
  assert.deepEqual(await readPicture(new FakeSource(svg.transactions), ON(svg.hash)), { picture: null, outcome: 'final' });
});

test('readPicture never reads a file picture over 1 MB', async () => {
  const big = patchManifest(await smallFileLedger({ label: 'big', bytes: PNG, type: 'image/png', firstLedger: 1000 }), { size: 1_000_001 });
  const source = new FakeSource(big.transactions);
  assert.deepEqual(await readPicture(source, ON(big.hash)), { picture: null, outcome: 'final' });
  assert.equal(source.accountTransactionsCalls, 0);
});

test('the description names the kind of file in plain words', () => {
  const at = (type) => describeFile({ type, size: 999 });
  assert.equal(at('image/jpeg'), 'JPEG image, 999 B, stored in the XRP Ledger.');
  assert.equal(at('image/svg+xml'), 'SVG image, 999 B, stored in the XRP Ledger.');
  assert.equal(at('audio/mpeg'), 'MPEG audio, 999 B, stored in the XRP Ledger.');
  assert.equal(at('text/plain; charset=utf-8'), 'Text file, 999 B, stored in the XRP Ledger.');
  assert.equal(at('application/zip'), 'File, 999 B, stored in the XRP Ledger.');
  assert.equal(at('image/"><script>alert(1)</script>'), 'Image, 999 B, stored in the XRP Ledger.');
  assert.equal(describeFile({ type: 'video/mp4', size: 4_000_000 }), 'MP4 video, 4.0 MB, stored in the XRP Ledger.');
});

test('readPicture: an unknown hash is missing', async () => {
  assert.deepEqual(await readPicture(new FakeSource([]), ON('AB'.repeat(32))), { picture: null, outcome: 'missing' });
});

test('readPicture: a picture whose chunks the node does not return is transient, not final', async () => {
  const png = await file({ label: 'gone', bytes: PNG, type: 'image/png' });
  const manifestOnly = png.transactions.filter((t) => t.hash === png.hash);
  assert.deepEqual(await readPicture(new FakeSource(manifestOnly), ON(png.hash)), { picture: null, outcome: 'transient' });
});

test('readPicture: a cover that cannot be used is cover-unread, since readCover cannot tell a bad cover from a failed read', async () => {
  const coverFile = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990, writer: 'rSomeoneElse' });
  const video = await file({ bytes: MP4, type: 'video/mp4', cover: coverFile.hash });
  assert.deepEqual(await readPicture(new FakeSource([...coverFile.transactions, ...video.transactions]), ON(video.hash)),
    { picture: null, outcome: 'cover-unread' });
});

test('readCard says when the cover named by the manifest could not be used', async () => {
  const coverFile = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990, writer: 'rSomeoneElse' });
  const video = await file({ bytes: MP4, type: 'video/mp4', cover: coverFile.hash });
  assert.equal((await readCard(new FakeSource([...coverFile.transactions, ...video.transactions]), ON(video.hash))).coverUnread, true);
  const pdf = await file({ bytes: PDF, type: 'application/pdf' });
  assert.equal((await readCard(new FakeSource(pdf.transactions), ON(pdf.hash))).coverUnread, false);
});
