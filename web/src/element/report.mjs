// What the element tells a page about its own read: the manifest, then each chunk and each block as
// the read gets them. A page can draw the file's layout from these without reading the ledger itself.

/**
 * A report for one load. `emit(type, detail)` dispatches the element's events; the read calls
 * `manifest()` once the manifest is known, `chunk(info)` for every chunk a block read takes and
 * `block(index)` once a block has verified.
 *
 * The same chunk and block reach the report more than once (the image path reads block 0 twice,
 * a verified-cache hit replays its chunks, a video retries), and each event is emitted once. A
 * report is tied to one load: once its signal aborts, it is silent.
 */
export function createReadReport({ manifest, blockBytes, signal, emit }) {
  const chunksPerBlock = blockBytes / manifest.chunk;
  const chunks = Math.ceil(manifest.size / manifest.chunk);
  const seenChunks = new Uint8Array(chunks);
  const seenBlocks = new Uint8Array(manifest.blocks);
  let announced = false;

  return {
    manifest() {
      if (signal.aborted || announced) return;
      announced = true;
      emit('xrpl-file-manifest', {
        size: manifest.size,
        chunk: manifest.chunk,
        chunks,
        blocks: manifest.blocks,
        firstLedger: manifest.firstLedger,
        lastLedger: manifest.lastLedger,
      });
    },

    chunk({ index, ledger }) {
      if (signal.aborted || !(index >= 0 && index < chunks) || seenChunks[index] === 1) return;
      seenChunks[index] = 1;
      emit('xrpl-file-chunk', { index, ledger });
    },

    block(index) {
      if (signal.aborted || !(index >= 0 && index < manifest.blocks) || seenBlocks[index] === 1) return;
      seenBlocks[index] = 1;
      const firstChunk = index * chunksPerBlock;
      emit('xrpl-file-block', {
        index,
        firstChunk,
        lastChunk: Math.min(chunks, firstChunk + chunksPerBlock) - 1,
      });
    },
  };
}
