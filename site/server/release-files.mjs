// What a release of the site server holds, as repository-relative paths. The layout is the
// repository's, so the server's relative imports (../cards, ../../web/src) work unchanged.
// site/test/server-release.test.mjs starts a server from a packed release, so a missing import fails
// there rather than on the server.
export const RELEASE_PATHS = [
  'site/dist',
  'site/server',
  'site/cards',
  'site/resolve.mjs',
  'site/headers.mjs',
  'site/redirects.mjs',
  'site/chrome-markup.mjs',
  'site/src/js',
  'web/src',
];
