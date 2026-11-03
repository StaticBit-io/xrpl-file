// Resolve hook for site tests. Site modules reach the reader through one specifier that only exists
// inside the built site (/js/xrpl.mjs -> /element/1.2.0/xrpl-file-reader.mjs); tests answer it with the
// web/src modules, so the site never carries a second copy of the reader code.
const BUNDLE_SPECIFIER = '../element/1.2.0/xrpl-file-reader.mjs';

let shimUrl = null;

export function initialize(data) {
  shimUrl = data.shimUrl;
}

export function resolve(specifier, context, nextResolve) {
  if (specifier === BUNDLE_SPECIFIER) return { url: shimUrl, shortCircuit: true };
  return nextResolve(specifier, context);
}
