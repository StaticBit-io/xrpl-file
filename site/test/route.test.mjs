import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { pathFromLink, linkFromPath } = await import('../src/js/route.mjs');

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const LOWER = HASH.toLowerCase();

test('pathFromLink canonicalises case and trims whitespace and a trailing newline', () => {
  assert.equal(pathFromLink(` XRPL-FILE://TestNet/${LOWER} \n`), `/testnet/${HASH}`);
});

test('a decimal NetworkID round-trips through the path unchanged', () => {
  assert.equal(pathFromLink(`xrpl-file://21338/${HASH}`), `/21338/${HASH}`);
  const back = linkFromPath(`/21338/${LOWER}`);
  assert.equal(back.link, `xrpl-file://21338/${HASH}`);
  assert.equal(back.canonicalPath, `/21338/${HASH}`);
});

test('linkFromPath accepts any case and a trailing slash, and returns the canonical path', () => {
  const result = linkFromPath(`/TESTNET/${LOWER}/`);
  assert.equal(result.canonicalPath, `/testnet/${HASH}`);
  assert.equal(result.link, `xrpl-file://testnet/${HASH}`);
});

test('text that is not a link gives null', () => {
  assert.equal(pathFromLink(''), null);
  assert.equal(pathFromLink(`https://example.com/testnet/${HASH}`), null);
  assert.equal(pathFromLink('xrpl-file://testnet/'), null);
  assert.equal(pathFromLink('xrpl-file://testnet/xyz'), null);
  assert.equal(pathFromLink(undefined), null);
});

test('linkFromPath gives null for paths that are not a file page', () => {
  assert.equal(linkFromPath('/ru/'), null);
  assert.equal(linkFromPath('/'), null);
  assert.equal(linkFromPath('/testnet/xyz'), null);
  assert.equal(linkFromPath('/ru/index.html'), null);
  assert.equal(linkFromPath('/js/route.mjs'), null);
  assert.equal(linkFromPath(`/nonet/${HASH}`), null);
  assert.equal(linkFromPath(`/testnet/${HASH}/extra`), null);
});
