// A small evaluator for the subset of Cloudflare Pages' _redirects syntax the site uses. It exists so
// the dev server and the build check agree with the rules file, not to emulate Pages in general.
//
// Semantics taken from the Pages docs (developers.cloudflare.com/pages/configuration/redirects):
//   - rules are evaluated top-down and only the first match applies;
//   - a 200 rule proxies the target and the target is not matched against the rules again;
//   - redirects are "always followed, regardless of whether or not an asset matches". The docs do not
//     say a static file beats a 200 rule, so this evaluator assumes it does not.
// A placeholder (:name) matches one non-empty path segment; a splat (*) matches the rest.

const SOURCE_TOKEN = /:[A-Za-z]\w*|\*|[^:*]+|:/g;

function compileSource(source) {
  const names = [];
  let pattern = '';
  for (const token of source.match(SOURCE_TOKEN) ?? []) {
    if (token === '*') {
      names.push('splat');
      pattern += '(.*)';
    } else if (token.length > 1 && token[0] === ':') {
      names.push(token.slice(1));
      pattern += '([^/]+)';
    } else {
      pattern += token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }
  }
  return { regex: new RegExp(`^${pattern}$`), names };
}

export function parseRedirects(text) {
  const rules = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const [source, destination, code] = line.split(/\s+/);
    if (!source || !destination) throw new Error(`malformed _redirects line: ${line}`);
    rules.push({ source, destination, status: code ? Number(code) : 302, ...compileSource(source) });
  }
  return rules;
}

function fillDestination(destination, names, values) {
  let result = destination;
  names.forEach((name, i) => {
    result = result.split(`:${name}`).join(values[i]);
  });
  return result;
}

/**
 * The first matching rule's outcome for a request path: { status: 200, path } for a rewrite,
 * { status, location } for a redirect, or null when no rule matches (serve the path as it is).
 */
export function applyRedirects(pathname, rules) {
  for (const rule of rules) {
    const match = rule.regex.exec(pathname);
    if (!match) continue;
    const target = fillDestination(rule.destination, rule.names, match.slice(1));
    return rule.status === 200 ? { status: 200, path: target } : { status: rule.status, location: target };
  }
  return null;
}

/**
 * URLs of built files that a rule sends somewhere other than the file itself. Such a file would be
 * unreachable if Pages follows the rule before looking at assets, so the build refuses to ship it.
 * @param {string[]} files paths relative to the site root, '/'-separated
 */
export function findShadowed(files, rules) {
  const shadowed = [];
  for (const file of files) {
    const url = `/${file}`;
    const urls = file.endsWith('/index.html') ? [url, url.slice(0, -'index.html'.length)] : [url];
    for (const candidate of urls) {
      const outcome = applyRedirects(candidate, rules);
      const servesItself = outcome === null || (outcome.status === 200 && outcome.path === candidate);
      if (!servesItself) {
        shadowed.push(url);
        break;
      }
    }
  }
  return shadowed;
}
