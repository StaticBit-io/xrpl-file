// Which paths go to the card handler: the same site/src/_routes.json Pages reads, with Pages' rules
// (developers.cloudflare.com/pages/functions/routing): `*` matches anything, `/` included, and an
// exclude pattern wins over an include pattern.

function compile(pattern) {
  if (typeof pattern !== 'string' || !pattern.startsWith('/')) throw new Error(`bad _routes.json pattern: ${pattern}`);
  const source = pattern.split('*').map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp(`^${source}$`);
}

/** @returns {{ include: RegExp[], exclude: RegExp[] }} */
export function parseRoutes(text) {
  const routes = JSON.parse(text);
  if (!Array.isArray(routes.include) || routes.include.length === 0) {
    throw new Error('_routes.json needs at least one include pattern');
  }
  return { include: routes.include.map(compile), exclude: (routes.exclude ?? []).map(compile) };
}

export function routedToFunction(pathname, routes) {
  if (routes.exclude.some((pattern) => pattern.test(pathname))) return false;
  return routes.include.some((pattern) => pattern.test(pathname));
}
