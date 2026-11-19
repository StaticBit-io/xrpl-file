// The examples page's list, in page order. Changing an example is one entry here; the words for it
// are in examples-strings.mjs under the same key. The cover of a video is not an example.
import { HERO_LINK } from './config.mjs';

/**
 * @typedef {{ key: string, link: string, kind: 'drawing'|'video'|'svg'|'text'|'logo', bytes: number, width?: number, height?: number }} Example
 */

/** @type {Example[]} */
export const EXAMPLES = [
  { key: 'forest-guardian', link: 'xrpl-file://testnet/D1E0AADD2B118CDBC43AA73DF1C696DA49A8E241752A1E753E617297BFD51348', kind: 'drawing', bytes: 127082, width: 960, height: 640 },
  { key: 'xrpl-file-en', link: 'xrpl-file://testnet/D345607DBF512A442E99417FE32147F851DEF026388496F7378024C914B2BBB8', kind: 'video', bytes: 1880596, width: 1280, height: 720 },
  { key: 'chunks', link: 'xrpl-file://testnet/ADAB5DD105663299DA3B98A94F2F3C88ABCE7E649C1CBCB10F6A7470E5439A9E', kind: 'svg', bytes: 1618 },
  { key: 'about', link: 'xrpl-file://testnet/113A706B9AA28827FFB3291871A43B30208F8A8E9E8FDA8723F42CFB663FBEFB', kind: 'text', bytes: 2202 },
  { key: 'logo', link: HERO_LINK, kind: 'logo', bytes: 122958, width: 1422, height: 1231 },
];
