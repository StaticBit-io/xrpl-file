// Draws the site's icons from the header mark (MARK_CELLS in chrome-markup.mjs) on the desktop app's
// dark tile, and writes them into site/src, where the build copies them to the site root:
//
//   favicon.svg            what current browsers use, at any size
//   favicon.ico            16, 32 and 48 px for browsers and tools that only ask for /favicon.ico
//   apple-touch-icon.png   180 px, full bleed: iOS rounds the corners itself
//
//   node site/tools/favicon.mjs            rewrite the three files
//   node site/tools/favicon.mjs --preview <dir>  also write 8x enlargements of the .ico sizes there
//
// The raster files come from a small rasteriser here rather than from an image library, so the site
// keeps no build dependency. site/test/favicon.test.mjs checks that the committed files match.
//
// One tile in both colour schemes: the icon is the app's icon, and a dark square reads on a light tab
// strip as well as on a dark one, while a media query inside the SVG would not reach the .ico or .png.
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync, crc32 } from 'node:zlib';
import { MARK_CELLS } from '../chrome-markup.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const OUT_DIR = join(here, '..', 'src');

// The desktop app's palette (app/XrplFile.App/Resources/AppIcon), the site's dark tokens as hex.
const PAPER = '#04090c';
const TILE_EDGE = '#2a2c2c';
const OUTLINE = '#89949a';
const LIT = '#bdc6ca';
const VERIFIED = '#bcf053';
// An empty cell in the small icons: a solid step above the tile instead of an outline.
const EMPTY = '#3a454b';

// The mark is drawn in its own 19-unit square: 5-unit cells on a 6.5 pitch, stroked 1 unit wide like
// the header's, and scaled onto a 32-unit tile.
//
// The favicon (svg and ico) scales it to 22 units, so a 16 px icon still has 3 px cells a pixel apart.
// There an empty cell is filled, not outlined: at 16 px an outline is under a pixel wide, the hole in it
// closes up, and the cell turns into the same grey square as a lit one, so the pattern is lost (checked
// on 8x enlargements of the 16 and 32 px rasters). The svg cannot change with its size, and browsers
// draw it at 16 px too, so it takes the filled form as well.
//
// The touch icon has 180 px to spare: it keeps the outlines and the app icon's proportions (the mark
// over 58% of the tile).
const TILE = 32;
const MARK = 19;
const FAVICON = { markSize: 22, outlined: false };
export const TOUCH = { markSize: 18.7, outlined: true, fullBleed: true };

/** The shapes of one icon, in tile units, painted in order. `fullBleed` drops the rounded corners. */
export function iconShapes({ markSize, outlined, fullBleed = false } = FAVICON) {
  const shapes = fullBleed
    ? [{ x: 0, y: 0, w: TILE, h: TILE, r: 0, fill: PAPER }]
    : [{ x: 0.5, y: 0.5, w: TILE - 1, h: TILE - 1, r: 7, fill: PAPER, stroke: TILE_EDGE, strokeWidth: 1 }];
  const scale = markSize / MARK;
  const offset = (TILE - markSize) / 2;
  for (const [column, row, state] of MARK_CELLS) {
    const cell = {
      x: offset + (0.5 + column * 6.5) * scale,
      y: offset + (0.5 + row * 6.5) * scale,
      w: 5 * scale,
      h: 5 * scale,
      r: 0.5 * scale,
      strokeWidth: scale,
    };
    if (state === '') shapes.push(outlined ? { ...cell, stroke: OUTLINE } : { ...cell, fill: EMPTY, stroke: EMPTY });
    else {
      const colour = state === 'is-verified' ? VERIFIED : LIT;
      shapes.push({ ...cell, fill: colour, stroke: colour });
    }
  }
  return shapes;
}

const round = (n) => Math.round(n * 1000) / 1000;

export function iconSvg() {
  const body = iconShapes().map((s) => {
    const paint = `fill="${s.fill ?? 'none'}"${s.stroke ? ` stroke="${s.stroke}" stroke-width="${round(s.strokeWidth)}"` : ''}`;
    return `  <rect x="${round(s.x)}" y="${round(s.y)}" width="${round(s.w)}" height="${round(s.h)}" rx="${round(s.r)}" ${paint}/>`;
  });
  return [`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${TILE} ${TILE}">`, ...body, '</svg>', ''].join('\n');
}

const hex = (colour) => [1, 3, 5].map((i) => parseInt(colour.slice(i, i + 2), 16));

/** Signed distance from (px, py) to a rounded rectangle: negative inside. */
function roundedRectDistance(px, py, { x, y, w, h, r }) {
  const qx = Math.abs(px - (x + w / 2)) - (w / 2 - r);
  const qy = Math.abs(py - (y + h / 2)) - (h / 2 - r);
  return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - r;
}

/** The colour a shape paints at a point, or null; a stroke straddles the outline, as in SVG. */
function paintAt(shape, px, py) {
  const d = roundedRectDistance(px, py, shape);
  if (shape.stroke && Math.abs(d) <= shape.strokeWidth / 2) return shape.stroke;
  if (shape.fill && d <= 0) return shape.fill;
  return null;
}

const SAMPLES = 8;

/** RGBA pixels of the icon at `size` px, each pixel the average of SAMPLES x SAMPLES points. */
export function rasterize(size, options) {
  const shapes = iconShapes(options).map((s) => ({ ...s, rgb: { fill: s.fill && hex(s.fill), stroke: s.stroke && hex(s.stroke) } }));
  const pixels = Buffer.alloc(size * size * 4);
  const unit = TILE / size;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0; let g = 0; let b = 0; let covered = 0;
      for (let sy = 0; sy < SAMPLES; sy++) {
        for (let sx = 0; sx < SAMPLES; sx++) {
          const px = (x + (sx + 0.5) / SAMPLES) * unit;
          const py = (y + (sy + 0.5) / SAMPLES) * unit;
          let colour = null;
          for (const shape of shapes) {
            const paint = paintAt(shape, px, py);
            if (paint) colour = paint === shape.stroke ? shape.rgb.stroke : shape.rgb.fill;
          }
          if (colour) { r += colour[0]; g += colour[1]; b += colour[2]; covered++; }
        }
      }
      const i = (y * size + x) * 4;
      if (covered > 0) {
        pixels[i] = Math.round(r / covered);
        pixels[i + 1] = Math.round(g / covered);
        pixels[i + 2] = Math.round(b / covered);
        pixels[i + 3] = Math.round((covered / (SAMPLES * SAMPLES)) * 255);
      }
    }
  }
  return pixels;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

/** An 8-bit RGBA PNG, every row unfiltered. */
export function encodePng(size, pixels) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8;
  header[9] = 6;
  const rows = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) pixels.copy(rows, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** An .ico holding PNG images, which every browser that reads .ico accepts. */
export function encodeIco(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  const entries = [];
  let offset = 6 + 16 * images.length;
  for (const { size, png } of images) {
    const entry = Buffer.alloc(16);
    entry[0] = size >= 256 ? 0 : size;
    entry[1] = size >= 256 ? 0 : size;
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(png.length, 8);
    entry.writeUInt32LE(offset, 12);
    entries.push(entry);
    offset += png.length;
  }
  return Buffer.concat([header, ...entries, ...images.map((image) => image.png)]);
}

export const ICO_SIZES = [16, 32, 48];
export const TOUCH_SIZE = 180;

export function iconFiles() {
  return {
    'favicon.svg': Buffer.from(iconSvg(), 'utf8'),
    'favicon.ico': encodeIco(ICO_SIZES.map((size) => ({ size, png: encodePng(size, rasterize(size)) }))),
    'apple-touch-icon.png': encodePng(TOUCH_SIZE, rasterize(TOUCH_SIZE, TOUCH)),
  };
}

function enlarge(size, pixels, factor) {
  const big = Buffer.alloc(size * factor * size * factor * 4);
  for (let y = 0; y < size * factor; y++) {
    for (let x = 0; x < size * factor; x++) {
      pixels.copy(big, (y * size * factor + x) * 4, (Math.floor(y / factor) * size + Math.floor(x / factor)) * 4, (Math.floor(y / factor) * size + Math.floor(x / factor)) * 4 + 4);
    }
  }
  return encodePng(size * factor, big);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  for (const [name, bytes] of Object.entries(iconFiles())) {
    writeFileSync(join(OUT_DIR, name), bytes);
    console.log(`wrote ${join(OUT_DIR, name)} (${bytes.length} bytes)`);
  }
  const previewDir = process.argv.includes('--preview') ? process.argv[process.argv.indexOf('--preview') + 1] : null;
  if (previewDir) {
    for (const size of ICO_SIZES) writeFileSync(join(previewDir, `favicon-${size}-x8.png`), enlarge(size, rasterize(size), 8));
    console.log(`previews in ${previewDir}`);
  }
}
