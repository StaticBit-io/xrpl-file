// Names the metadata a still image carries, so prepare.sh can warn before the file is published
// permanently, byte for byte.
//
//   node tools/examples/scan-metadata.mjs <image>
//
// Prints one comma-separated line, empty when nothing was found: JPEG APP1 Exif/XMP, PNG tEXt/iTXt/zTXt
// (with their keywords) and eXIf, WebP EXIF and XMP chunks. Exif that holds a GPS block is reported as
// "EXIF with GPS location". Nothing here decodes pixels; a file that is not one of the three formats
// yields an empty line.
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const EXIF_PREFIX = Buffer.from('Exif\0\0', 'latin1');
const XMP_PREFIX = Buffer.from('http://ns.adobe.com/xap/1.0/\0', 'latin1');
const GPS_IFD_POINTER = 0x8825;

/** True when the Exif block (with or without the "Exif\0\0" prefix) has a GPS IFD pointer in its first IFD. */
export function exifHasGps(block) {
  const tiff = block.subarray(0, 6).equals(EXIF_PREFIX) ? block.subarray(6) : block;
  if (tiff.length < 8) return false;
  const order = tiff.toString('latin1', 0, 2);
  if (order !== 'II' && order !== 'MM') return false;
  const little = order === 'II';
  const u16 = (at) => (little ? tiff.readUInt16LE(at) : tiff.readUInt16BE(at));
  const u32 = (at) => (little ? tiff.readUInt32LE(at) : tiff.readUInt32BE(at));
  const ifd = u32(4);
  if (ifd + 2 > tiff.length) return false;
  const count = u16(ifd);
  for (let i = 0; i < count; i++) {
    const entry = ifd + 2 + i * 12;
    if (entry + 12 > tiff.length) return false;
    if (u16(entry) === GPS_IFD_POINTER) return true;
  }
  return false;
}

function exifLabel(block) {
  return exifHasGps(block) ? 'EXIF with GPS location' : 'EXIF';
}

function scanJpeg(buf) {
  const found = [];
  let at = 2;
  while (at + 4 <= buf.length && buf[at] === 0xff) {
    const marker = buf[at + 1];
    if (marker === 0xda || marker === 0xd9) break; // start of scan / end of image: no more headers
    const length = buf.readUInt16BE(at + 2);
    const body = buf.subarray(at + 4, at + 2 + length);
    if (marker === 0xe1) {
      if (body.subarray(0, 6).equals(EXIF_PREFIX)) found.push(exifLabel(body));
      else if (body.subarray(0, XMP_PREFIX.length).equals(XMP_PREFIX)) found.push('XMP');
    }
    at += 2 + length;
  }
  return found;
}

function scanPng(buf) {
  const found = [];
  const keywords = [];
  let at = 8;
  while (at + 12 <= buf.length) {
    const length = buf.readUInt32BE(at);
    const type = buf.toString('latin1', at + 4, at + 8);
    const body = buf.subarray(at + 8, at + 8 + length);
    if (type === 'eXIf') found.push(exifLabel(body));
    if (type === 'tEXt' || type === 'iTXt' || type === 'zTXt') {
      const end = body.indexOf(0);
      keywords.push(body.toString('latin1', 0, end < 0 ? body.length : end));
    }
    if (type === 'IEND') break;
    at += 12 + length;
  }
  if (keywords.length > 0) found.push(`text chunks (${[...new Set(keywords)].join(', ')})`);
  return found;
}

function scanWebp(buf) {
  const found = [];
  let at = 12;
  while (at + 8 <= buf.length) {
    const type = buf.toString('latin1', at, at + 4);
    const length = buf.readUInt32LE(at + 4);
    const body = buf.subarray(at + 8, at + 8 + length);
    if (type === 'EXIF') found.push(exifLabel(body));
    if (type === 'XMP ') found.push('XMP');
    at += 8 + length + (length % 2);
  }
  return found;
}

/** The metadata kinds found in a still image's bytes, in file order. */
export function scanMetadata(buf) {
  if (buf.length > 3 && buf[0] === 0xff && buf[1] === 0xd8) return scanJpeg(buf);
  if (buf.length > 8 && buf.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]))) return scanPng(buf);
  if (buf.length > 12 && buf.toString('latin1', 0, 4) === 'RIFF' && buf.toString('latin1', 8, 12) === 'WEBP') return scanWebp(buf);
  return [];
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  if (process.argv.length !== 3) {
    console.error('usage: node scan-metadata.mjs <image>');
    process.exit(2);
  }
  process.stdout.write(`${scanMetadata(readFileSync(process.argv[2])).join(', ')}\n`);
}
