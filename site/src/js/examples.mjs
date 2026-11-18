// The examples page's list, in page order. Changing an example is one entry here; the words for it
// are in examples-strings.mjs under the same key. The cover of a video is not an example.
import { HERO_LINK } from './config.mjs';

/**
 * @typedef {{ key: string, link: string, kind: 'drawing'|'video'|'svg'|'text'|'logo', bytes: number, width?: number, height?: number }} Example
 */

/** @type {Example[]} */
export const EXAMPLES = [
  // Phase 1 stand-ins published on testnet; phase 2 replaces them with the owner's mainnet files.
  { key: 'stand-in-drawing-large', link: 'xrpl-file://testnet/7A140C609804309021A99C481BC9D1D8315CACC938B106FF73247312D7200217', kind: 'drawing', bytes: 49302, width: 1280, height: 960 },
  { key: 'stand-in-drawing-small', link: 'xrpl-file://testnet/500190E5BBE2E9334A1D5845F29153F672B174C67E9FC471EED4BAED311528A1', kind: 'drawing', bytes: 59972, width: 800, height: 600 },
  { key: 'stand-in-clip', link: 'xrpl-file://testnet/9B3FBDF2E0FFF6F6EB4CACCF95216FADB36CDAC31C3892BF3E005732EB5FD42B', kind: 'video', bytes: 383570, width: 640, height: 360 },
  { key: 'chunks', link: 'xrpl-file://testnet/6345AA97EBA5A536814E6CC8AA31D23B1F78571A22F6D27E95CF3FAA85C8949C', kind: 'svg', bytes: 1618 },
  { key: 'about', link: 'xrpl-file://testnet/3109A8759DF34C027BE2285B4EB4CC8FD46661F81013C681ACAB2956427E87ED', kind: 'text', bytes: 2231 },
  { key: 'logo', link: HERO_LINK, kind: 'logo', bytes: 122958, width: 1422, height: 1231 },
];
