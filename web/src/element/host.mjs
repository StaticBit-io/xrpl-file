// Decisions the element makes that need no DOM, kept here so node --test can pin them.

/**
 * The `:host` rule. It sits in the shadow root's own stylesheet, so any rule of the site's wins
 * over it: that is what makes `width` and `height` presentational defaults, as on <img>.
 */
export function hostStyle({ width, height, ratio }) {
  let rule = `:host { display: block; position: relative; aspect-ratio: ${ratio};`;
  if (width) rule += ` width: ${width};`;
  if (height) rule += ` height: ${height};`;
  return `${rule} }`;
}

/**
 * Whether there is a link to work with. `parsing` is true while the document is still being parsed:
 * a script that runs before the element's children exist sees an empty text and must wait for it.
 * @returns 'ready' when `src` or the text holds anything, 'wait' while it may still arrive, else 'missing'
 */
export function linkReadiness({ src, text, parsing }) {
  const has = (value) => typeof value === 'string' && value.trim() !== '';
  if (has(src) || has(text)) return 'ready';
  return parsing ? 'wait' : 'missing';
}

// The phases reached only after a read in which every block verified.
const VERIFIED_PHASES = new Set(['image', 'text', 'video', 'download']);

/** The right-hand side of the caption: the network, with the verified mark once something verified was read. */
export function captionState({ phase, network }) {
  const verified = VERIFIED_PHASES.has(phase);
  return { verified, text: verified ? `✓ verified · ${network}` : network };
}
