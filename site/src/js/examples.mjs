// The examples page's list, in page order. Changing an example is one entry here; the words for it
// are in examples-strings.mjs under the same key. The cover of a video is not an example.
import { HERO_LINK } from './config.mjs';

/**
 * @typedef {{ key: string, link: string, kind: 'drawing'|'video'|'svg'|'text'|'logo', bytes: number, width?: number, height?: number }} Example
 */

/** @type {Example[]} */
export const EXAMPLES = [
  { key: 'forest-guardian', link: 'xrpl-file://mainnet/5B7B036AEEDCF8D9A3FD17CA829614EAB3CA9BF699C66F02DCF8D5D6CC27E13F', kind: 'drawing', bytes: 127082, width: 960, height: 640 },
  { key: 'xrpl-file-en', link: 'xrpl-file://mainnet/EE2B3C105D17E3A1954696BD3BF0069DED00363839B12385AEBE5BFE4C0AB077', kind: 'video', bytes: 1880596, width: 1280, height: 720 },
  { key: 'chunks', link: 'xrpl-file://mainnet/ADDD46866CC60F631D6829C1F6B7E4DED30B6DDA3B26116A556D8FC6C7FF58AB', kind: 'svg', bytes: 1618 },
  { key: 'about', link: 'xrpl-file://mainnet/893484332BE9202205F38AD34EC1CB0610E4F2AF4187D31D187C10B14B0C0228', kind: 'text', bytes: 2202 },
  { key: 'logo', link: HERO_LINK, kind: 'logo', bytes: 122958, width: 1422, height: 1231 },
];
