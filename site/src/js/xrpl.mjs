// The only door from the site's modules to the reader. The path resolves inside the built site
// (/js/xrpl.mjs -> /element/1.3.0/xrpl-file-reader.mjs): the site and the embeddable element share one
// build, never two copies. Bump the version here and in build.mjs together with the element.
export {
  XrplFileReader,
  LedgerClient,
  parseLink,
  formatLink,
  XrplFileError,
  ENDPOINTS,
} from '../element/1.3.0/xrpl-file-reader.mjs';
