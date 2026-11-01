import { XrplFileElement } from './element.mjs';

if (!customElements.get('xrpl-file')) customElements.define('xrpl-file', XrplFileElement);
