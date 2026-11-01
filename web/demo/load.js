// Demo only: loads the built element with its Subresource Integrity value read from the same
// version's integrity.json, so these pages exercise SRI and never go stale against a rebuild.
// A site embeds the two-line snippet from the README instead (see snippet.html).
(async () => {
  const base = '../dist/1.0.0/';
  const script = document.createElement('script');
  script.src = `${base}xrpl-file-element.js`;
  script.crossOrigin = 'anonymous';
  try {
    const hashes = await (await fetch(`${base}integrity.json`)).json();
    script.integrity = hashes['xrpl-file-element.js'];
  } catch (error) {
    console.warn('integrity.json could not be read; loading the element without a check:', error);
  }
  document.head.append(script);
})();
