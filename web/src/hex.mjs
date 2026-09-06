const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** @param {Uint8Array} bytes */
export function hex(bytes) {
  let out = '';
  for (const b of bytes) out += b.toString(16).padStart(2, '0');
  return out.toUpperCase();
}

/** @param {string} text */
export function unhex(text) {
  if (typeof text !== 'string' || text.length % 2 !== 0 || /[^0-9a-fA-F]/.test(text)) throw new Error('not hex');
  const out = new Uint8Array(text.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(text.slice(i * 2, i * 2 + 2), 16);
  return out;
}

export const utf8Hex = (text) => hex(encoder.encode(text));
export const hexUtf8 = (text) => decoder.decode(unhex(text));
