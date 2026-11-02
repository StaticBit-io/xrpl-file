# Site acceptance, 2026-11-02

Scope: the site as amended on 2026-11-02 (Vantage look, dark by default, six languages, point-cloud
hero reading the StaticBit logo from testnet), built from branch `claude/project-site-8f79c1`.

Method: real Chrome 156 (headless, throwaway profile, `--lang=en-US`) driven over the DevTools
protocol, with real key events, `localStorage`, WebSocket and clipboard. The driver scripts were
throwaway and are not in the repository. The Claude browser pane was used for a first look and the
final state reset. Fixture links were read in place from `run/fixtures/` (`links.json` holds `logo`;
the other fixtures come from `links.before-logo.json`). Download used a stubbed
`showSaveFilePicker` that counts bytes, so nothing was written to disk.

| # | Row | Checked | Result | Defect / fix | Screenshot |
|---|---|---|---|---|---|
| 1 | Home, 6 languages × dark/light × 375/1280 | 24 combinations. `scrollWidth` equals the viewport everywhere; the price table (432 px at 375) scrolls inside its own focusable, named region. `<html lang>` and title are correct. The menu lists the six names inside the viewport. `/zh/` falls back to Microsoft YaHei for CJK. | pass | none | home-en-dark-1280, home-ru-light-375, home-zh-dark-1280, home-de-light-375 |
| 2 | Hero cloud | Dark/light × 1280/375, fresh session each: reading, note at 1.4 s, developing at 2.4 s, done at about 4.9 s. The card has the page theme and "verified" and links to `/testnet/2AEB…0DB4`; the link was followed. The transparent logo is legible on both grounds. | pass | none | hero-card-verified-dark-1280, hero-card-verified-light-375 |
| 3 | Hero failure | Dev-only WebSocket stubs (opens and never answers; never opens), not shipped. Both settle into `failed` at 20.2 s with the quiet caption "The XRP Ledger node did not answer. Reload the page to try again.", no card, empty note. | pass | none | hero-failed-dark-1280 |
| 4 | Fast replay, reduced motion | Second load in the same tab: done at 2.3 s against 5.0 s. Reduced motion emulated: 6 animation frames against 291, a still frame, finished at 2.2 s. | pass | none | (card as in row 2) |
| 5 | Check a link | Home and 404. A valid link and a messy one (` XRPL-FILE://TestNet/<lower-case hash> ` plus a newline) both open the canonical file page. Invalid and empty input fill the `role="alert"` region, set `aria-invalid` and `aria-describedby`, and keep focus. | pass | none | check-invalid-dark-1280 |
| 6 | File page | Fixtures picture, logo, svg, text, mp4-start, webm. Download is disabled until `load` (1.3–9.4 s); a click before load does nothing; the saved byte count is exact; the status says "Saved.". The clipboard holds the link after Copy. Lower-case hash, upper-case network and trailing-slash paths are canonicalised with `replaceState`; `/21338/<HASH>` stays unchanged. A bad hash shows the element's "The node does not have the history this file needs." after 2.3 s and the page stays intact. In-place language switch (ru, zh, de, en) keeps the URL; the home link follows the language. | fixed | A 9.2 MB video took 20 s to save with a disabled button and an empty status line: the page now says "Saving…" in all six languages (7c3fe51) | file-picture-dark-1280, file-picture-light-375, file-video-dark-1280, file-bad-hash-dark-1280 |
| 7 | 404 | `/nope`, `/a/b/c`, `/testnet`: HTTP 404, "Check a link" works, assets load on nested paths, no horizontal scroll at 375. | pass | none | 404-dark-1280, 404-light-375 |
| 8 | Theme, storage, first-visit language | Fresh profile: `/` stays English and is dark. Light survives `/de/`, a file page, `/nope` and `/`; the element follows the switch. With `localStorage` throwing, all four page types render dark and the switch works both ways; the language choice works. Stored `ru`: `/` goes to `/ru/`. Stored `en` stays. Stored `de`: `/?x=1` goes to `/de/?x=1`. Choosing English in the `/ru/` menu lands on `/` and stays. `/de/` with stored `ru` stays. | pass | none | |
| 9 | Keyboard | Full tab order on home and file page. Every stop has a 2 px outline (lime in dark, moss in light). Menu: Enter opens, Escape closes and returns focus. Theme switch with Enter and Space. Check field with Enter. Copy with Space, Download with Enter. The disabled publisher button is skipped by design. | pass | none | kbd-menu-open |
| 10 | sloplint | Static tier, relative-href copy of the six homes, file page, 404 and CSS: 0 FAIL, 0 WARN, 8 NOTE (word count and hero length, advisory). Render tier (`--render`, puppeteer-core 24.10.0 in a scratch folder, Chrome 156) on `/ru/`: gate 58 FAIL, "hero carries 3 text blocks before its actions" at 1440 px; one REFLEX, the "Модуль чтения для браузера" link wraps at 768 px (advisory, left). | fixed | The paragraph that explains the cubes moved from the hero text to the figure, under the cloud, in all six languages; the hero text is now headline, one sentence, actions, and a test pins it | |
| 11 | Network | Home with hero, file page, 404 and `/zh/`: 88 requests to the site origin and 4 WebSockets to `wss://s.altnet.rippletest.net:51233`, nothing else. | pass | none | |
| 12 | Contrast | Live computed values against `contrast.md`. Dark: body 11.52, muted 6.45, button 13.75, field border 4.16, error 7.59, focus 14.98. Light: body 9.95, muted 5.79, button 5.83, field border 3.34, error 6.05, focus 6.83. All meet AA and match `contrast.md`. | pass | none | |
| 13 | Chinese price fallback | `zh/index.html` said 暂时 ("temporarily") where English says "right now". | fixed | 目前; a test pins the qualifier of the price fallback in all six languages (c76181e) | |
| 14 | Element contrast over pictures | The round play button over a cover and the caption Copy border over a white picture, both non-text UI needing 3:1. | fixed | The play fill alone measured 1.15–1.7:1 on a dark cover: a 2 px ring in the overlay foreground now keeps one edge at 3.47:1 or better on any grey. The Copy border went from 40 % to 60 % white: 4.53:1, was 2.92:1 (8d25496) | |
| 15 | Transparent box (`background="transparent"`) | The element's text and accent on the site's own grounds, both themes: the file page's `--paper-2` and, in the hero, the cloud's `--paper`: the owner asked for the hero card without a backing or border, so the logo, the check and the link sit on the cloud itself. 16 new rows in `contrast.md`. A loaded picture on the file page has no box (computed `rgba(0, 0, 0, 0)`), and the logo sits on the page. | fixed | The element's muted grey measured 4.44:1 on the light hero card's former `--paper-3` backing; the site now gives the element its `--ink-muted`, 4.75:1 or better on every ground | |

Also checked: the snippet's `integrity` equals the SHA-384 of
`dist/element/1.1.0/xrpl-file-element.js`, and `_headers` carries the `/element/*` CORS and
immutable-cache rule.

## Open rows

1. The render tier covered `/ru/` only: that one page took about 35 minutes, and the process did not
   exit after printing its result. The other homes share the markup and styles of `/ru/`; the file
   page and 404 were not rendered.
2. The `<xrpl-file>` element's own text (loader, errors, buttons) stays English on every language
   page; the element has no localisation API (ruling R23).
3. The German, Spanish, Portuguese and Chinese copy is translated from English and awaits a native
   speaker's review (listed in `site/README.md`).
4. Expected until publication, not a defect: the jsDelivr URL in the main embed snippet answers 404
   until the npm package is published; the alternative URL on the site works once the site is
   deployed.
5. Single-URL pages (file page, 404) carry no `hreflang` alternates (ruling R21).
