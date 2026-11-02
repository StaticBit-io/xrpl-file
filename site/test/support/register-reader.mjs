// Import this before dynamically importing any site module that imports ./xrpl.mjs:
//   import './support/register-reader.mjs';
//   const { pathFromLink } = await import('../src/js/route.mjs');
import { register } from 'node:module';

register('./reader-hook.mjs', {
  parentURL: import.meta.url,
  data: { shimUrl: new URL('./reader-shim.mjs', import.meta.url).href },
});
