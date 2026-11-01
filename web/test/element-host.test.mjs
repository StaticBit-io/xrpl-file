import { test } from 'node:test';
import assert from 'node:assert/strict';
import { hostStyle, linkReadiness, captionState } from '../src/element/host.mjs';

test('the host rule has the reserved proportion and no size by default', () => {
  assert.equal(hostStyle({ width: null, height: null, ratio: '16 / 9' }),
    ':host { display: block; position: relative; aspect-ratio: 16 / 9; }');
});

test('width and height follow the proportion, in that order', () => {
  assert.equal(hostStyle({ width: '320px', height: '50vh', ratio: '16 / 9' }),
    ':host { display: block; position: relative; aspect-ratio: 16 / 9; width: 320px; height: 50vh; }');
  assert.equal(hostStyle({ width: '320px', height: null, ratio: '4 / 3' }),
    ':host { display: block; position: relative; aspect-ratio: 4 / 3; width: 320px; }');
  assert.equal(hostStyle({ width: null, height: '200px', ratio: '4 / 3' }),
    ':host { display: block; position: relative; aspect-ratio: 4 / 3; height: 200px; }');
});

test('the link is ready from src or from text, waits while the page is parsed, is missing after', () => {
  assert.equal(linkReadiness({ src: 'xrpl-file://testnet/A', text: '', parsing: true }), 'ready');
  assert.equal(linkReadiness({ src: null, text: '  ', parsing: true }), 'wait');
  assert.equal(linkReadiness({ src: null, text: '', parsing: false }), 'missing');
  assert.equal(linkReadiness({ src: null, text: ' xrpl-file://testnet/A ', parsing: false }), 'ready');
  assert.equal(linkReadiness({ src: ' ', text: ' xrpl-file://testnet/A ', parsing: true }), 'ready');
  assert.equal(linkReadiness({ src: '', text: '', parsing: true }), 'wait');
});

test('the caption marks verified only in the phases that follow a verified read', () => {
  assert.deepEqual(captionState({ phase: 'idle', network: 'testnet' }), { verified: false, text: 'testnet' });
  assert.deepEqual(captionState({ phase: 'loading', network: 'testnet' }), { verified: false, text: 'testnet' });
  assert.deepEqual(captionState({ phase: 'error', network: 'testnet' }), { verified: false, text: 'testnet' });
  for (const phase of ['image', 'text', 'video', 'download']) {
    assert.deepEqual(captionState({ phase, network: 'testnet' }), { verified: true, text: '✓ verified · testnet' }, phase);
  }
});
