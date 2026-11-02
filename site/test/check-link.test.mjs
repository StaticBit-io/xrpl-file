import test from 'node:test';
import assert from 'node:assert/strict';
import './support/register-reader.mjs';

const { initCheckLink } = await import('../src/js/check-link.mjs');

const HASH = '32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';
const STRINGS = { empty: 'Paste a link first.', invalid: 'That is not an xrpl-file link.' };

function fakeField() {
  const listeners = {};
  const attrs = {};
  const input = {
    value: '',
    attrs,
    focused: false,
    setAttribute(name, value) { attrs[name] = String(value); },
    removeAttribute(name) { delete attrs[name]; },
    focus() { this.focused = true; },
    addEventListener(type, handler) { (listeners[`input:${type}`] ??= []).push(handler); },
  };
  const message = { id: 'link-error', textContent: '', hidden: true };
  const form = {
    addEventListener(type, handler) { (listeners[`form:${type}`] ??= []).push(handler); },
  };
  const submit = () => {
    let prevented = false;
    for (const handler of listeners['form:submit'] ?? []) handler({ preventDefault: () => { prevented = true; } });
    return prevented;
  };
  const type = (value) => {
    input.value = value;
    for (const handler of listeners['input:input'] ?? []) handler({});
  };
  return { form, input, message, submit, type };
}

test('a valid link navigates to its canonical file path', () => {
  const field = fakeField();
  const visited = [];
  initCheckLink({ ...field, strings: STRINGS, navigate: (path) => visited.push(path) });
  field.type(` XRPL-FILE://TestNet/${HASH.toLowerCase()}\n`);
  assert.equal(field.submit(), true, 'the native submit is always taken over');
  assert.deepEqual(visited, [`/testnet/${HASH}`]);
  assert.equal(field.input.attrs['aria-invalid'], undefined);
});

test('an invalid value shows an accessible inline error and does not navigate', () => {
  const field = fakeField();
  const visited = [];
  initCheckLink({ ...field, strings: STRINGS, navigate: (path) => visited.push(path) });
  field.type('https://example.com/not-a-link');
  field.submit();
  assert.deepEqual(visited, []);
  assert.equal(field.message.textContent, STRINGS.invalid);
  assert.equal(field.message.hidden, false);
  assert.equal(field.input.attrs['aria-invalid'], 'true');
  assert.equal(field.input.attrs['aria-describedby'], 'link-error');
  assert.equal(field.input.focused, true);
});

test('an empty field has its own message', () => {
  const field = fakeField();
  initCheckLink({ ...field, strings: STRINGS, navigate: () => assert.fail('must not navigate') });
  field.submit();
  assert.equal(field.message.textContent, STRINGS.empty);
});

test('editing the field clears the error', () => {
  const field = fakeField();
  initCheckLink({ ...field, strings: STRINGS, navigate: () => {} });
  field.type('nope');
  field.submit();
  field.type('xrpl-file://');
  assert.equal(field.message.textContent, '');
  assert.equal(field.message.hidden, true);
  assert.equal(field.input.attrs['aria-invalid'], undefined);
  assert.equal(field.input.attrs['aria-describedby'], undefined);
});
