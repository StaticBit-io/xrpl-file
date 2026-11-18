// Packs a release of the site server: the paths in release-files.mjs plus release.json naming the
// commit, as a .tar.gz the server's release script unpacks.
//
//   node site/server/pack.mjs <out.tar.gz> <commit>      (after web/build.mjs and site/build.mjs)
import { execFileSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, rename, rm, writeFile, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RELEASE_PATHS } from './release-files.mjs';

const REPO = fileURLToPath(new URL('../..', import.meta.url));

/**
 * @param {{ out: string, commit: string, repoRoot?: string, distDir?: string }} options
 *   distDir replaces repoRoot/site/dist (tests pack a site built elsewhere).
 */
export async function packRelease({ out, commit, repoRoot = REPO, distDir = join(repoRoot, 'site', 'dist') }) {
  if (!/^[0-9a-f]{40}$/.test(commit)) throw new Error(`commit must be 40 lowercase hex characters, got "${commit}"`);
  const work = await mkdtemp(join(tmpdir(), 'xf-pack-'));
  try {
    const stage = join(work, 'stage');
    for (const path of RELEASE_PATHS) {
      const source = path === 'site/dist' ? distDir : join(repoRoot, path);
      await mkdir(dirname(join(stage, path)), { recursive: true });
      await cp(source, join(stage, path), { recursive: true });
    }
    await writeFile(join(stage, 'release.json'), JSON.stringify({ commit }) + '\n');
    // Relative names only: GNU tar in Git Bash reads "C:" in an absolute path as a remote host.
    execFileSync('tar', ['-czf', 'release.tar.gz', '-C', 'stage', 'release.json', 'site', 'web'], { cwd: work });
    await mkdir(dirname(resolve(out)), { recursive: true });
    await rename(join(work, 'release.tar.gz'), resolve(out)).catch(() => copyFile(join(work, 'release.tar.gz'), resolve(out)));
  } finally {
    await rm(work, { recursive: true, force: true });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [out, commit] = process.argv.slice(2);
  if (!out || !commit) {
    console.error('usage: node site/server/pack.mjs <out.tar.gz> <commit>');
    process.exit(2);
  }
  await packRelease({ out, commit });
  console.log(out);
}
