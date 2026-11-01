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

/**
 * The right-hand side of the caption in three pieces: the mark, the word and the network, so that a
 * narrower box can drop the word and the smallest keeps the mark alone. The pieces are empty until
 * something verified was read.
 */
export function captionParts({ phase, network }) {
  const verified = VERIFIED_PHASES.has(phase);
  return { verified, mark: verified ? '✓ ' : '', word: verified ? 'verified · ' : '', network };
}

/** The right-hand side of the caption: the network, with the verified mark once something verified was read. */
export function captionState({ phase, network }) {
  const { verified, mark, word } = captionParts({ phase, network });
  return { verified, text: `${mark}${word}${network}` };
}

/**
 * How the shadow root gets its styles. Constructable stylesheets are not subject to a page's
 * `style-src` policy, which drops `<style>` elements; browsers without them (and only those) fall
 * back to `<style>` elements.
 * @returns 'adopted' when `adoptedStyleSheets` and `CSSStyleSheet#replaceSync` both exist, else 'fallback'
 */
export function styleSupport(scope = globalThis) {
  const hasReplaceSync = typeof scope.CSSStyleSheet === 'function'
    && typeof scope.CSSStyleSheet.prototype?.replaceSync === 'function';
  const hasAdopted = typeof scope.ShadowRoot === 'function' && 'adoptedStyleSheets' in scope.ShadowRoot.prototype;
  return hasReplaceSync && hasAdopted ? 'adopted' : 'fallback';
}
