// Above this a browser may refuse to decode, or hold hundreds of megabytes of pixels, so the file is offered as a download.
export const IMAGE_MAX_BYTES = 20_000_000;

const IMAGES = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/avif']);
const VIDEOS = new Set(['video/mp4', 'video/webm', 'video/quicktime']);

/** The media type in lower case without parameters. */
export function normalizeType(type) {
  return typeof type === 'string' ? type.split(';')[0].trim().toLowerCase() : '';
}

function isText(type) {
  return type.startsWith('text/') || type === 'application/json';
}

/** Which view a manifest's declared type and size call for; `reason` says why a file is only offered as a download. */
export function viewForType(manifest) {
  const type = normalizeType(manifest.type);
  if (IMAGES.has(type)) {
    return manifest.size > IMAGE_MAX_BYTES
      ? { view: 'download', reason: 'too-large' }
      : { view: 'image', reason: null };
  }
  if (VIDEOS.has(type)) return { view: 'video', reason: null };
  if (isText(type)) return { view: 'text', reason: null };
  return { view: 'download', reason: 'unsupported' };
}

const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const JPEG = [0xff, 0xd8, 0xff];
const PDF = [0x25, 0x50, 0x44, 0x46];
const ZIP = [0x50, 0x4b, 0x03, 0x04];
const EBML = [0x1a, 0x45, 0xdf, 0xa3];
const GIF87 = [0x47, 0x49, 0x46, 0x38, 0x37, 0x61];
const GIF89 = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];
const RIFF = [0x52, 0x49, 0x46, 0x46];
const WEBP = [0x57, 0x45, 0x42, 0x50];
const FTYP = [0x66, 0x74, 0x79, 0x70];
const AVIF_BRANDS = [[0x61, 0x76, 0x69, 0x66], [0x61, 0x76, 0x69, 0x73]];

function startsWith(head, magic, offset = 0) {
  if (head.length < offset + magic.length) return false;
  return magic.every((byte, i) => head[offset + i] === byte);
}

function isAvif(head) {
  if (!startsWith(head, FTYP, 4)) return false;
  if (AVIF_BRANDS.some((brand) => startsWith(head, brand, 8))) return true;
  // Bytes 0-3 are the box size; compatible brands run from byte 16 to its end, never beyond the head we have.
  const size = ((head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3]) >>> 0;
  const end = Math.min(size, head.length);
  for (let offset = 16; offset + 4 <= end; offset += 4) {
    if (AVIF_BRANDS.some((brand) => startsWith(head, brand, offset))) return true;
  }
  return false;
}

function looksBinary(head) {
  return [PNG, JPEG, GIF87, GIF89, PDF, ZIP, EBML].some((magic) => startsWith(head, magic))
    || (startsWith(head, RIFF) && startsWith(head, WEBP, 8))
    || startsWith(head, FTYP, 4);
}

/**
 * Whether the file's first bytes fit its declared type. Video containers are checked where the video
 * is parsed, so they agree here; types with no signature here agree because they only ever get the download view.
 */
export function magicAgrees(type, head) {
  const normalized = normalizeType(type);
  switch (normalized) {
    case 'image/png': return startsWith(head, PNG);
    case 'image/jpeg': return startsWith(head, JPEG);
    case 'image/gif': return startsWith(head, GIF87) || startsWith(head, GIF89);
    case 'image/webp': return startsWith(head, RIFF) && startsWith(head, WEBP, 8);
    case 'image/avif': return isAvif(head);
    default: return isText(normalized) ? !looksBinary(head) : true;
  }
}
