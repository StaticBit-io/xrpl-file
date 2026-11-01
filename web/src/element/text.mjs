export const TEXT_LIMIT = 1_048_576;

// A UTF-8 sequence is at most four bytes, so at most three continuation bytes can precede the cut.
const MAX_BACKOFF = 3;

/**
 * The text of the first `limit` bytes, decoded as UTF-8 with replacement characters. A cut inside a
 * multi-byte sequence drops the whole sequence instead of showing a replacement character for half of it.
 */
export function textPreview(bytes, limit = TEXT_LIMIT) {
  if (bytes.length <= limit) return { text: decode(bytes), truncated: false };
  let end = limit;
  for (let back = 0; back < MAX_BACKOFF && end > 0 && (bytes[end] & 0xc0) === 0x80; back++) end--;
  return { text: decode(bytes.subarray(0, end)), truncated: true };
}

function decode(bytes) {
  return new TextDecoder('utf-8').decode(bytes);
}
