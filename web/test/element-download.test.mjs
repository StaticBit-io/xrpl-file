import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BLOB_LIMIT, saveFile } from '../src/element/download.mjs';
import { PRIORITY } from '../src/element/page.mjs';
import { XrplFileError } from '../src/link.mjs';

function makeBlocks(count, length) {
  return Array.from({ length: count }, (_, i) => Uint8Array.from({ length }, (__, j) => (i * 37 + j) & 0xff));
}

/** A handle that serves fixed blocks, can fail one, and records how it was asked. */
function fakeHandle(blocks) {
  const handle = {
    reads: 0,
    asked: [],
    failures: new Map(),
    failAt(index, error) { handle.failures.set(index, error); },
    async block(index, options) {
      handle.reads++;
      handle.asked.push({ index, ...options });
      options?.signal?.throwIfAborted();
      if (handle.failures.has(index)) throw handle.failures.get(index);
      return blocks[index].slice();
    },
  };
  return handle;
}

function recordOf(blocks, extra = {}) {
  const size = blocks.reduce((sum, block) => sum + block.length, 0);
  return { manifest: { v: 1, type: 'application/octet-stream', size, blocks: blocks.length, name: 'vector-2500500.bin', ...extra } };
}

/** A save-file picker whose writable logs every call. */
function fakePicker() {
  const writable = {
    calls: [],
    chunks: [],
    failClose: null,
    async write(bytes) {
      writable.chunks.push(bytes);
      writable.calls.push(`write:${writable.chunks.length - 1}`);
    },
    async close() {
      writable.calls.push('close');
      if (writable.failClose) throw writable.failClose;
    },
    async abort() { writable.calls.push('abort'); },
  };
  const rejections = [];
  const picker = async (options) => {
    picker.names.push(options.suggestedName);
    picker.options = options;
    if (rejections.length > 0) throw rejections.shift();
    return { createWritable: async () => writable };
  };
  picker.names = [];
  picker.writable = writable;
  picker.rejectOnceWith = (error) => rejections.push(error);
  picker.reject = (error) => { for (let i = 0; i < 10; i++) rejections.push(error); };
  return picker;
}

test('blocks are written in order and the file is closed', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  assert.equal(await saveFile({ handle, record: recordOf(blocks), picker }), 'saved');
  assert.deepEqual(picker.writable.calls, ['write:0', 'write:1', 'write:2', 'close']);
  assert.equal(picker.options.suggestedName, 'vector-2500500.bin');
  assert.equal(picker.options.startIn, 'downloads');
  assert.deepEqual(picker.writable.chunks, blocks);
});

test('every block is read now, in order, through the sequential reader', async () => {
  const blocks = makeBlocks(3, 4);
  const handle = fakeHandle(blocks);
  const controller = new AbortController();
  await saveFile({ handle, record: recordOf(blocks), picker: fakePicker(), signal: controller.signal });
  assert.deepEqual(handle.asked.map((a) => a.index), [0, 1, 2]);
  for (const call of handle.asked) {
    assert.equal(call.priority, PRIORITY.NOW);
    assert.equal(call.sequential, true);
    assert.equal(call.signal, controller.signal);
  }
});

test('a block that fails verification is never written and the file is aborted', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  handle.failAt(1, new XrplFileError('BlockMismatch', 'x', { block: 1 }));
  await assert.rejects(saveFile({ handle, record: recordOf(blocks), picker }), (e) => e.code === 'BlockMismatch' && e.block === 1);
  assert.deepEqual(picker.writable.calls, ['write:0', 'abort']);
  assert.equal(handle.reads, 2);
});

test('a failing write aborts the file and rethrows', async () => {
  const blocks = makeBlocks(2, 8);
  const picker = fakePicker();
  const disk = new Error('disk full');
  picker.writable.write = async () => { picker.writable.calls.push('write'); throw disk; };
  await assert.rejects(saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker }), (e) => e === disk);
  assert.deepEqual(picker.writable.calls, ['write', 'abort']);
});

test('a failing abort does not hide the error that caused it', async () => {
  const blocks = makeBlocks(2, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  picker.writable.abort = async () => { throw new Error('abort failed'); };
  handle.failAt(0, new XrplFileError('ChunkMissing', 'x', { block: 0 }));
  await assert.rejects(saveFile({ handle, record: recordOf(blocks), picker }), (e) => e.code === 'ChunkMissing');
});

test('a failing close aborts the file and rethrows', async () => {
  const blocks = makeBlocks(1, 8);
  const picker = fakePicker();
  const failure = new Error('close failed');
  picker.writable.failClose = failure;
  await assert.rejects(saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker }), (e) => e === failure);
  assert.deepEqual(picker.writable.calls, ['write:0', 'close', 'abort']);
});

test('an aborted signal stops the save between blocks and aborts the file', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  const controller = new AbortController();
  const write = picker.writable.write;
  picker.writable.write = async (bytes) => { await write(bytes); controller.abort(); };
  await assert.rejects(
    saveFile({ handle, record: recordOf(blocks), picker, signal: controller.signal }),
    (e) => e.name === 'AbortError',
  );
  assert.deepEqual(picker.writable.calls, ['write:0', 'abort']);
  assert.equal(handle.reads, 1);
});

test('a name the picker refuses falls back to xrpl-file.<ext>', async () => {
  const blocks = makeBlocks(2, 8);
  const picker = fakePicker();
  picker.rejectOnceWith(new TypeError('bad name'));
  assert.equal(await saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker }), 'saved');
  assert.deepEqual(picker.names, ['vector-2500500.bin', 'xrpl-file.bin']);
});

test('the fallback name carries the extension of the type', async () => {
  const blocks = makeBlocks(1, 8);
  const picker = fakePicker();
  picker.rejectOnceWith(new TypeError('bad name'));
  await saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks, { type: 'video/mp4' }), picker });
  assert.deepEqual(picker.names, ['vector-2500500.bin', 'xrpl-file.mp4']);
});

test('the picker is asked once more at most', async () => {
  const blocks = makeBlocks(1, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  picker.reject(new TypeError('bad name'));
  await assert.rejects(saveFile({ handle, record: recordOf(blocks), picker }), TypeError);
  assert.equal(picker.names.length, 2);
  assert.equal(handle.reads, 0);
});

test('a manifest without a name is not asked twice under the same name', async () => {
  const blocks = makeBlocks(1, 8);
  const picker = fakePicker();
  picker.reject(new TypeError('bad name'));
  await assert.rejects(saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks, { name: undefined }), picker }), TypeError);
  assert.deepEqual(picker.names, ['xrpl-file.bin']);
});

test('a picker failure that is not about the name is not retried', async () => {
  const blocks = makeBlocks(1, 8);
  const picker = fakePicker();
  const failure = new DOMException('no', 'NotFoundError');
  picker.reject(failure);
  await assert.rejects(saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker }), (e) => e === failure);
  assert.equal(picker.names.length, 1);
});

for (const name of ['SecurityError', 'NotAllowedError'])
  test(`a picker refused with ${name} falls back to the in-memory save`, async () => {
    const blocks = makeBlocks(3, 8);
    const handle = fakeHandle(blocks);
    const picker = fakePicker();
    picker.reject(new DOMException('cross-origin subframe', name));
    let received;
    const result = await saveFile({
      handle, record: recordOf(blocks), picker, saveBlob: (blob, fileName) => { received = { blob, fileName }; },
    });
    assert.equal(result, 'saved');
    assert.equal(picker.names.length, 1);
    assert.equal(received.fileName, 'vector-2500500.bin');
    assert.equal(received.blob.size, 24);
    assert.deepEqual(picker.writable.calls, []);
  });

test('a picker blocked on the second ask falls back too', async () => {
  const blocks = makeBlocks(1, 8);
  const picker = fakePicker();
  picker.rejectOnceWith(new TypeError('bad name'));
  picker.rejectOnceWith(new DOMException('', 'SecurityError'));
  let saved = false;
  assert.equal(await saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker, saveBlob: () => { saved = true; } }), 'saved');
  assert.equal(saved, true);
});

test('the fallback keeps the 2 GiB refusal, before any read', async () => {
  const blocks = makeBlocks(1, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  picker.reject(new DOMException('', 'SecurityError'));
  await assert.rejects(
    saveFile({ handle, record: recordOf(blocks, { size: BLOB_LIMIT + 1 }), picker, saveBlob: () => {} }),
    (e) => e.code === 'TooLarge',
  );
  assert.equal(handle.reads, 0);
});

test('a cancelled save dialog reads nothing', async () => {
  const blocks = makeBlocks(2, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  picker.reject(new DOMException('', 'AbortError'));
  assert.equal(await saveFile({ handle, record: recordOf(blocks), picker }), 'cancelled');
  assert.equal(handle.reads, 0);
});

test('a dialog cancelled on the second ask is cancelled too', async () => {
  const blocks = makeBlocks(1, 8);
  const handle = fakeHandle(blocks);
  const picker = fakePicker();
  picker.rejectOnceWith(new TypeError('bad name'));
  picker.rejectOnceWith(new DOMException('', 'AbortError'));
  assert.equal(await saveFile({ handle, record: recordOf(blocks), picker }), 'cancelled');
  assert.equal(handle.reads, 0);
});

test('progress is reported once per written block', async () => {
  const blocks = makeBlocks(3, 8);
  const seen = [];
  await saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks), picker: fakePicker(), onProgress: (p) => seen.push(p) });
  assert.deepEqual(seen, [
    { bytesDone: 8, bytesTotal: 24, blocksVerified: 1, blocksTotal: 3 },
    { bytesDone: 16, bytesTotal: 24, blocksVerified: 2, blocksTotal: 3 },
    { bytesDone: 24, bytesTotal: 24, blocksVerified: 3, blocksTotal: 3 },
  ]);
});

test('without a picker, a file over 2 GiB is refused before any read', async () => {
  const blocks = makeBlocks(1, 8);
  const handle = fakeHandle(blocks);
  const record = recordOf(blocks, { size: BLOB_LIMIT + 1 });
  let saved = false;
  await assert.rejects(
    saveFile({ handle, record, picker: null, saveBlob: () => { saved = true; } }),
    (e) => e.code === 'TooLarge' && e.sentence === 'This file is too large to save in this browser.',
  );
  assert.equal(handle.reads, 0);
  assert.equal(saved, false);
});

test('the in-memory limit is exactly 2 GiB', () => {
  assert.equal(BLOB_LIMIT, 2_147_483_648);
});

test('without a picker, the blob holds exactly the file', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  const record = recordOf(blocks, { type: 'video/mp4', name: 'a/b:c.mp4' });
  let received;
  const result = await saveFile({ handle, record, picker: null, saveBlob: (blob, name) => { received = { blob, name }; } });
  assert.equal(result, 'saved');
  assert.equal(received.blob.size, record.manifest.size);
  assert.equal(received.name, 'a_b_c.mp4');
  const expected = new Uint8Array(24);
  blocks.forEach((block, i) => expected.set(block, i * 8));
  assert.deepEqual(new Uint8Array(await received.blob.arrayBuffer()), expected);
});

for (const type of ['text/html', 'image/svg+xml', 'application/xhtml+xml', 'video/mp4'])
  test(`the saved blob is octet-stream whatever the manifest says (${type})`, async () => {
    const blocks = makeBlocks(1, 8);
    let received;
    await saveFile({ handle: fakeHandle(blocks), record: recordOf(blocks, { type }), picker: null, saveBlob: (blob) => { received = blob; } });
    assert.equal(received.type, 'application/octet-stream');
  });

test('without a picker, a failed block means nothing is saved', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  handle.failAt(2, new XrplFileError('BlockMismatch', 'x', { block: 2 }));
  let saved = false;
  await assert.rejects(
    saveFile({ handle, record: recordOf(blocks), picker: null, saveBlob: () => { saved = true; } }),
    (e) => e.code === 'BlockMismatch',
  );
  assert.equal(saved, false);
});

test('without a picker, an aborted signal stops the reads and saves nothing', async () => {
  const blocks = makeBlocks(3, 8);
  const handle = fakeHandle(blocks);
  const controller = new AbortController();
  controller.abort();
  let saved = false;
  await assert.rejects(
    saveFile({ handle, record: recordOf(blocks), picker: null, saveBlob: () => { saved = true; }, signal: controller.signal }),
    (e) => e.name === 'AbortError',
  );
  assert.equal(saved, false);
});
