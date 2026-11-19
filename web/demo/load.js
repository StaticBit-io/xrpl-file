// Demo only: loads the built element with its Subresource Integrity value read from the same
// version's integrity.json, so these pages exercise SRI and never go stale against a rebuild.
// A site embeds the two-line snippet from the README instead (see snippet.html).
(async () => {
  const base = '../dist/1.2.1/';
  const script = document.createElement('script');
  script.src = `${base}xrpl-file-element.js`;
  script.crossOrigin = 'anonymous';
  // fetch resolves on a 404, so a missing build must be caught by status, not by a throw.
  const response = await fetch(`${base}integrity.json`).catch(() => null);
  if (!response?.ok) {
    console.error(`${base} is not built: run \`cd web && node build.mjs\` and reload.`);
    return;
  }
  const hash = (await response.json())['xrpl-file-element.js'];
  if (typeof hash === 'string') script.integrity = hash;
  document.head.append(script);
})();
