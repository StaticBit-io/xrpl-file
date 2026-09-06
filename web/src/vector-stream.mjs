// Block k of the stream is SHA256(seed ‖ k as a big-endian uint32); blocks concatenated, cut to size.
export async function vectorBytes(seed, size) {
  const seedBytes = new TextEncoder().encode(seed);
  const input = new Uint8Array(seedBytes.length + 4);
  input.set(seedBytes, 0);
  const output = new Uint8Array(size);
  let at = 0;
  let counter = 0;
  while (at < size) {
    input[input.length - 4] = (counter >>> 24) & 0xff;
    input[input.length - 3] = (counter >>> 16) & 0xff;
    input[input.length - 2] = (counter >>> 8) & 0xff;
    input[input.length - 1] = counter & 0xff;
    const block = new Uint8Array(await crypto.subtle.digest('SHA-256', input));
    const take = Math.min(block.length, size - at);
    output.set(block.subarray(0, take), at);
    at += take;
    counter++;
  }
  return output;
}
