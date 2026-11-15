// A small evaluator for the subset of Cloudflare Pages' _headers syntax the site uses, so the
// production server sends what Pages sends. Semantics from the Pages docs
// (developers.cloudflare.com/pages/configuration/headers) and the live site:
//   - a block is an unindented path pattern followed by indented "Name: value" lines;
//   - every block whose pattern matches the requested path (before any _redirects rewrite) applies,
//     and their headers are added together;
//   - a placeholder (:name) matches one path segment, a splat (*) matches anything.

const PATTERN_TOKEN = /:[A-Za-z]\w*|\*|[^:*]+|:/g;

function compilePattern(pattern) {
  let source = '';
  for (const token of pattern.match(PATTERN_TOKEN) ?? []) {
    if (token === '*') source += '.*';
    else if (token.length > 1 && token[0] === ':') source += '[^/]+';
    else source += token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }
  return new RegExp(`^${source}$`);
}

/** @returns {{ pattern: string, regex: RegExp, headers: Array<[string, string]> }[]} */
export function parseHeaders(text) {
  const rules = [];
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim() || raw.trim().startsWith('#')) continue;
    if (!/^\s/.test(raw)) {
      const pattern = raw.trim();
      rules.push({ pattern, regex: compilePattern(pattern), headers: [] });
      continue;
    }
    const colon = raw.indexOf(':');
    if (colon < 0 || rules.length === 0) throw new Error(`malformed _headers line: ${raw.trim()}`);
    rules.at(-1).headers.push([raw.slice(0, colon).trim(), raw.slice(colon + 1).trim()]);
  }
  return rules;
}

/** The headers of every block that matches the path, in file order. */
export function headersFor(pathname, rules) {
  return rules.filter((rule) => rule.regex.test(pathname)).flatMap((rule) => rule.headers);
}
