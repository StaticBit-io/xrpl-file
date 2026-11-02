// The few values the site's owner changes by hand. site/README.md says when and how.

/** The file the hero mosaic reads. After the first mainnet publication: a real NFT picture there. */
export const HERO_LINK = 'xrpl-file://testnet/32D96008F01F85480F7566BFDFB636990FA45A1082139F78FE7BBF993EB6EE59';

/** Picture width / height. The manifest carries no dimensions, so it lives next to the link. */
export const HERO_ASPECT = 1;

/** The publisher's GitHub Releases URL. Null keeps the download button visible, disabled and marked "soon". */
export const PUBLISHER_URL = null;

/** The origin the embed snippet names, so it keeps working when pasted into another site. */
export const SITE_ORIGIN = 'https://xrpl-file.staticbit.io';

/** The npm package jsDelivr serves the element from. The final name is not chosen yet. */
export const ELEMENT_PACKAGE = 'xrpl-file-element';

/** False until the package is on npm: the page then says so and points at the site's own copy. */
export const ELEMENT_PACKAGE_PUBLISHED = false;
