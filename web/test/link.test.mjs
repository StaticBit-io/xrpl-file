import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseLink, formatLink, XrplFileError } from '../src/link.mjs';

const HASH = '9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B';

test('a canonical link round-trips', () => {
  const link = parseLink(`xrpl-file://mainnet/${HASH}`);
  assert.deepEqual(link, { network: 'mainnet', txHash: HASH });
  assert.equal(formatLink(link), `xrpl-file://mainnet/${HASH}`);
});

test('scheme, network and hash are case-insensitive on input', () => {
  assert.deepEqual(parseLink(`XRPL-FILE://TestNet/${HASH.toLowerCase()}`), { network: 'testnet', txHash: HASH });
});

test('a numeric network id is kept as digits', () => {
  assert.equal(parseLink(`xrpl-file://21338/${HASH}`).network, '21338');
});

for (const text of [
  '',
  'ipfs://bafybeigdyrzt5sfp7udm7hu76uh7y26nf3efuylqabf3oclgtqy55fbzdi',
  'xrpl-file://mainnet',
  'xrpl-file:///9F2C',
  'xrpl-file://mainnet/9F2C',
  `xrpl-file://mainnet/${HASH.slice(0, 63)}G`,
  `xrpl-file://main net/${HASH}`,
  `xrpl-file://0123/${HASH}`,
  `xrpl-file://mainnet/${HASH}?x=1`,
  `xrpl-file://mainnet/${HASH}/more`,
]) {
  test(`'${text}' is refused as an invalid link`, () => {
    assert.throws(() => parseLink(text), (e) => e instanceof XrplFileError && e.code === 'InvalidLink');
  });
}
