// The few values the site's owner changes by hand. site/README.md says when and how.

/** The file the hero reads: the StaticBit logo (PNG, transparent), published on mainnet. */
export const HERO_LINK = 'xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5';

/** Picture width / height (1422 x 1231). The manifest carries no dimensions, so it lives next to the link. */
export const HERO_ASPECT = 1422 / 1231;

/** The publisher's GitHub Releases URL. Null keeps the download button visible, disabled and marked "soon". */
export const PUBLISHER_URL = 'https://github.com/StaticBit-io/xrpl-file/releases/latest';

/** The origin the embed snippet names, so it keeps working when pasted into another site. */
export const SITE_ORIGIN = 'https://xrpl-file.staticbit.io';

/** The npm package jsDelivr serves the element from. The final name is not chosen yet. */
export const ELEMENT_PACKAGE = 'xrpl-file-element';
