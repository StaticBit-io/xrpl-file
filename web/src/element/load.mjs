// What the element reads from a file, apart from the browser: the manifest with its cover, and a
// whole file (or the first blocks of one) read in order, every block verified before it is kept.
import { formatLink } from '../link.mjs';
import { downloadName } from './format.mjs';
import { normalizeType } from './view.mjs';

/** The `detail` of `xrpl-file-progress`: a fresh plain object with exactly these four numbers. */
export function progressDetail({ bytesDone, bytesTotal, blocksVerified, blocksTotal }) {
  return { bytesDone, bytesTotal, blocksVerified, blocksTotal };
}

/**
 * The `detail` of `load`: what a page needs to describe the file without a second read. The name is
 * the one the element shows and saves under (the manifest's is attacker-chosen), or null when the
 * manifest has none; the link is canonical, so it can be compared and used as a path.
 */
export function loadDetail(manifest, link) {
  const named = typeof manifest.name === 'string' && manifest.name !== '';
  return {
    name: named ? downloadName(manifest) : null,
    type: normalizeType(manifest.type),
    size: manifest.size,
    network: link.network,
    link: formatLink(link),
  };
}

/**
 * The manifest and the cover it names (null when there is none or it fails the format's checks),
 * and no block of the file: this is all a `loading="click"` element reads before the visitor asks.
 */
export async function prepare(handle) {
  const record = await handle.record();
  const cover = await handle.cover();
  return { record, cover };
}

/**
 * The blocks that cover the first `min(size, limitBytes)` bytes, in order, concatenated. Blocks are
 * whole, so the result may run past the limit by up to one block; the caller cuts it. A block that
 * fails verification rejects the whole call with the reader's error and nothing is returned.
 *
 * `priority` is a PRIORITY value, or a function returning one, asked again before each block: an
 * element that scrolls into view while it loads reads its remaining blocks at the new priority.
 *
 * `onProgress(detail, { block })` is called once per chunk read (`block: false`, bytes the node
 * has delivered but whose block is not yet verified) and once per verified block (`block: true`).
 * Bytes are counted from one source per block: chunks while it is read, the block's own length once
 * it verified, so the two never add up twice.
 *
 * `onRead` is a read report (report.mjs) or null: it gets every chunk of every block read, and the
 * block's index once it verified.
 */
export async function loadWhole(handle, record, { limitBytes = Infinity, priority, signal, onProgress, onRead = null } = {}) {
  const { size, chunk } = record.manifest;
  const blockBytes = await handle.blockBytes();
  const wanted = Math.min(size, limitBytes);
  const blocksTotal = Math.min(record.manifest.blocks, Math.ceil(wanted / blockBytes));
  const bytesTotal = Math.min(size, blocksTotal * blockBytes);

  const parts = [];
  let bytesDone = 0;
  for (let index = 0; index < blocksTotal; index++) {
    signal?.throwIfAborted();
    const blockLength = Math.min(blockBytes, size - index * blockBytes);
    let chunks = 0;
    const onChunk = onProgress || onRead
      ? (info) => {
        onRead?.chunk(info);
        chunks++;
        onProgress?.(progressDetail({
          bytesDone: bytesDone + Math.min(chunks * chunk, blockLength),
          bytesTotal,
          blocksVerified: index,
          blocksTotal,
        }), { block: false });
      }
      : undefined;

    const blockPriority = typeof priority === 'function' ? priority() : priority;
    const bytes = await handle.block(index, { priority: blockPriority, signal, onChunk, sequential: true });
    parts.push(bytes);
    onRead?.block(index);
    bytesDone += bytes.length;
    onProgress?.(progressDetail({ bytesDone, bytesTotal, blocksVerified: index + 1, blocksTotal }), { block: true });
  }

  const whole = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    whole.set(part, offset);
    offset += part.length;
  }
  return whole;
}
