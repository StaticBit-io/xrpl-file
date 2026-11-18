// Turns prepared.json (written by prepare.sh) into the publication plan read by
// tools/XrplFile.Fixtures, and puts the text and the SVG next to the prepared files.
//
//   node tools/examples/make-plan.mjs <prepared.json> > tools/examples/plan.json
//
// Order matters: a cover must be published before the video that names it, so the plan runs
// cover, video, drawings, about.txt, chunks.svg. Every entry has the one writer "a".
import { copyFileSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const mediaSource = join(dirname(fileURLToPath(import.meta.url)), 'media');

const extras = [
  { file: 'about.txt', type: 'text/plain' },
  { file: 'chunks.svg', type: 'image/svg+xml' },
];

/** Kebab-case key from a file name: base name, lower-cased, anything outside a-z0-9 becomes '-'. */
export function keyOf(file) {
  const dot = file.lastIndexOf('.');
  const base = dot > 0 ? file.slice(0, dot) : file;
  const key = base.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  if (!key) throw new Error(`no key can be made from "${file}"`);
  return key;
}

/** Builds the plan from the prepared.json records. Throws on a missing video or cover, or a duplicate key. */
export function buildPlan(prepared) {
  const cover = prepared.filter((p) => p.role === 'cover');
  const video = prepared.filter((p) => p.role === 'video');
  const drawings = prepared.filter((p) => p.role === 'drawing');
  if (cover.length !== 1) throw new Error(`expected exactly one cover, found ${cover.length}`);
  if (video.length !== 1) throw new Error(`expected exactly one video, found ${video.length}`);

  const coverKey = keyOf(cover[0].file);
  const entry = (file, type, coverOf = null) => ({
    key: keyOf(file), file, type, name: file, writer: 'a', cover: coverOf,
  });

  const plan = [
    entry(cover[0].file, cover[0].type),
    entry(video[0].file, video[0].type, coverKey),
    ...drawings.map((d) => entry(d.file, d.type)),
    ...extras.map((e) => entry(e.file, e.type)),
  ];

  const seen = new Set();
  for (const p of plan) {
    if (seen.has(p.key)) throw new Error(`two files share the key "${p.key}"`);
    seen.add(p.key);
  }
  return plan;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  if (process.argv.length !== 3) {
    console.error('usage: node make-plan.mjs <prepared.json>');
    process.exit(2);
  }
  const preparedPath = process.argv[2];
  const plan = buildPlan(JSON.parse(readFileSync(preparedPath, 'utf8')));
  // The plan's media directory is the one that holds prepared.json.
  for (const { file } of extras) copyFileSync(join(mediaSource, file), join(dirname(preparedPath), file));
  process.stdout.write(`${JSON.stringify(plan, null, 2)}\n`);
}
