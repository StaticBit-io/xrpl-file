// A file's card written into the static file page: the robot reads the tags, and a robot (or a person
// with a robot's user agent) that follows through still gets the working page, which reads the file
// in the browser as it always does.
import { bannerImage, cardTags } from '../chrome-markup.mjs';
import { SITE_ORIGIN } from '../src/js/config.mjs';

// Code points, not UTF-16 units: a cut must not leave half of a surrogate pair.
const MAX_TITLE = 100;

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function cutTitle(title) {
  const points = [...title];
  return points.length <= MAX_TITLE ? title : `${points.slice(0, MAX_TITLE - 1).join('')}…`;
}

const CARD_TAG_LINE = /^<meta (?:property="og:[^"]*"|name="twitter:[^"]*") content="[^"]*">\n/gm;
const TITLE = /<title>[^<]*<\/title>/;
const DESCRIPTION = /<meta name="description" content="[^"]*">/;

/**
 * The static file page with the card of a file in its head: its <title>, description and every
 * og/twitter tag replaced. A card without a file (missing, unavailable) leaves the page as it is,
 * which already carries the site's generic card.
 * @param {string} staticHtml the built file.html
 * @param {import('./card.mjs').Card} card
 * @param {{ origin: string, network: string, hash: string }} at the origin the card picture is served
 *   from (the one the robot asked), and the file
 */
export function fileCardHtml(staticHtml, card, { origin, network, hash }) {
  if (card.status !== 'file') return staticHtml;
  // The name is a stranger's text: cut, then escaped, so the cut never splits an entity.
  const title = escapeHtml(cutTitle(card.title));
  const description = escapeHtml(card.description);
  const pagePath = `/${network}/${hash}`;
  const picture = card.picture !== 'banner';
  const tags = cardTags({
    title,
    description,
    pagePath,
    langFromStorage: false,
    url: `${SITE_ORIGIN}${pagePath}`,
    image: picture ? { url: `${origin}/og${pagePath}`, alt: title } : bannerImage(origin),
    twitterCard: picture ? 'summary_large_image' : 'summary',
  });
  // Functions as replacements, so a "$&" in a name is text and not a pattern.
  return staticHtml
    .replace(CARD_TAG_LINE, '')
    .replace(TITLE, () => `<title>${title}</title>`)
    .replace(DESCRIPTION, () => `<meta name="description" content="${description}">\n${tags}`);
}
