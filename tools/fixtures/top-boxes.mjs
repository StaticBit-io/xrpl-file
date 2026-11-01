// Prints the top-level ISO base media file format box types of a file, in file order.
// Usage: node tools/fixtures/top-boxes.mjs <file>
//
// Reads only the box headers (8 bytes, 16 with a 64-bit largesize) through positioned reads, so a
// 300 MB file is walked without being loaded. A size of 0 means "to the end of the file" and a size
// of 1 means the real size follows as a 64-bit value; both are handled because ffmpeg writes the
// second form for an mdat above 4 GiB and the first for a streamed one.
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

export function topBoxes(path) {
  const fd = fs.openSync(path, 'r');

  try {
    const length = fs.fstatSync(fd).size;
    const header = Buffer.alloc(16);
    const types = [];
    let at = 0;

    while (at + 8 <= length) {
      fs.readSync(fd, header, 0, 16, at);

      let size = header.readUInt32BE(0);
      const type = header.toString('latin1', 4, 8);
      if (size === 1) size = Number(header.readBigUInt64BE(8));
      else if (size === 0) size = length - at;

      if (size < 8 || at + size > length) {
        throw new Error(`${path}: box "${type}" at ${at} claims ${size} bytes in a file of ${length}`);
      }

      types.push(type);
      at += size;
    }

    if (at !== length) throw new Error(`${path}: ${length - at} stray bytes after the last box`);

    return types;
  } finally {
    fs.closeSync(fd);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv.length !== 3) {
    console.error('usage: node top-boxes.mjs <file>');
    process.exit(2);
  }

  console.log(topBoxes(process.argv[2]).join(' '));
}
