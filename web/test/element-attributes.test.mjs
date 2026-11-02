import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseLength, parseLoading, parseLoader, parseTheme, captionHidden, linkText, endpointsFor } from '../src/element/attributes.mjs';
import { NO_NODE, BAD_NODE } from '../src/element/messages.mjs';
import { ENDPOINTS } from '../src/ledger.mjs';

test('a bare number is CSS pixels, as on <img>', () => {
  assert.equal(parseLength('320'), '320px');
});

test('a CSS length with a known unit, or auto, passes through', () => {
  for (const v of ['100%', '50vh', 'auto', '12.5rem']) assert.equal(parseLength(v), v);
});

test('anything that is not a plain length is refused, so no CSS can ride in an attribute', () => {
  for (const v of ['', 'red', '10px; color:red', 'calc(1px)', null]) assert.equal(parseLength(v), null);
});

test('loading is lazy unless eager or click is named', () => {
  assert.equal(parseLoading(null), 'lazy');
  assert.equal(parseLoading('CLICK'), 'click');
  assert.equal(parseLoading('eager'), 'eager');
  assert.equal(parseLoading('soon'), 'lazy');
});

test('the loader stays on unless the attribute says false', () => {
  assert.equal(parseLoader(null), true);
  assert.equal(parseLoader('false'), false);
  assert.equal(parseLoader('FALSE'), false);
  assert.equal(parseLoader('no'), true);
});

test('the theme is dark unless light or auto is named, so an existing block keeps its look', () => {
  assert.equal(parseTheme(null), 'dark');
  assert.equal(parseTheme(''), 'dark');
  assert.equal(parseTheme('light'), 'light');
  assert.equal(parseTheme(' Light '), 'light');
  assert.equal(parseTheme('AUTO'), 'auto');
  assert.equal(parseTheme('dark'), 'dark');
  for (const v of ['sepia', 'light dark', 'light;color:red', 'system']) assert.equal(parseTheme(v), 'dark', v);
});

test('the link comes from src, else from the trimmed text content', () => {
  assert.equal(linkText({ src: null, text: '  xrpl-file://testnet/AB…  ' }), 'xrpl-file://testnet/AB…');
  assert.equal(linkText({ src: ' xrpl-file://testnet/CD ', text: 'xrpl-file://testnet/AB' }), 'xrpl-file://testnet/CD');
  assert.equal(linkText({ src: '  ', text: 'xrpl-file://testnet/AB' }), 'xrpl-file://testnet/AB');
  assert.equal(linkText({ src: null, text: '   ' }), null);
});

test('the built-in endpoints serve a named network', () => {
  assert.deepEqual(endpointsFor({ network: 'testnet' }, null), ENDPOINTS.testnet);
});

test('a node attribute replaces the built-in endpoints', () => {
  assert.deepEqual(endpointsFor({ network: '21337' }, 'wss://node.example'), ['wss://node.example']);
});

test('ws:// is accepted for localhost and 127.0.0.1 only', () => {
  assert.deepEqual(endpointsFor({ network: 'testnet' }, 'ws://localhost:6006'), ['ws://localhost:6006']);
  assert.deepEqual(endpointsFor({ network: 'testnet' }, 'ws://127.0.0.1:6006'), ['ws://127.0.0.1:6006']);
});

test('a numeric network without a node has no endpoints to use', () => {
  assert.throws(() => endpointsFor({ network: '21337' }, null), (e) => e.code === 'NodeUnreachable' && e.sentence === NO_NODE);
});

test('a node that is not wss:// (or local ws://) is refused', () => {
  for (const bad of ['ws://example.com', 'http://localhost', 'https://x', 'not a url'])
    assert.throws(() => endpointsFor({ network: 'testnet' }, bad), (e) => e.sentence === BAD_NODE, bad);
});

test('only caption="off" hides the caption; any other value or none keeps it', () => {
  for (const v of ['off', ' OFF ', 'Off']) assert.equal(captionHidden(v), true, v);
  for (const v of [null, undefined, '', 'on', 'false', 'hidden', 'of']) assert.equal(captionHidden(v), false, String(v));
});
