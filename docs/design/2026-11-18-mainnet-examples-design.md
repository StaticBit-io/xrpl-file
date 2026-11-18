# Mainnet examples and the examples page

Status: design, awaiting the owner's approval.

## Why

The site explains what xrpl-file does but shows only one real file: the StaticBit logo in the hero.
Every other published example is a testnet fixture in `web/demo/`, and those links die at the next
testnet reset. A visitor who wants to see a drawing, a video or a text file read from the ledger has
nowhere to go.

Files published on mainnet stay readable for as long as full-history nodes keep the ledgers. A page of
mainnet examples is therefore a showcase that does not rot, and every example link is also a live
sample of the link cards the site writes for Telegram and X (`/og/<network>/<hash>`): sharing an
example link shows what a shared file looks like.

## What is published

The owner decided the content:

- **Drawings and a video: the owner's own work**, delivered by the owner. The testnet sources
  (`run/fixtures/source/picture.webp`, a drawing the owner does not own, and `en-back.mp4`, a wallet
  tutorial that will be replaced) are **not** published on mainnet, nor is anything derived from them.
- **A text file and an SVG drawing, written for this purpose** and committed to the repository, so
  the published bytes can be checked against the repository.
- **The logo already in the hero**
  (`xrpl-file://mainnet/9B1A9E33DBD9B445BFE2854D4D489DC860869D873AD36DA30A69DD19DDBC0DF5`) is shown on
  the page too. It costs nothing new.

The video gets a **cover** (format spec 4.1): without one, its link card falls back to the site
banner. The test-only fixtures (broken covers, HEVC, the 302 MB file, active HTML) stay on testnet.

Nothing is published by Claude. The owner signs and submits with their own keys, using the tool
described below, after reading its estimate.

## Goals

1. An **examples page** at `/examples`, in all six site languages (`/ru/examples`, `/de/examples`,
   `/es/examples`, `/pt/examples`, `/zh/examples`), showing every example through `<xrpl-file>`.
2. A **mainnet mode in `tools/XrplFile.Fixtures`** that publishes a plan from the owner's own writer
   and payer accounts, cover first, then the file that names it, and writes the links to
   `links.json`.
3. A **preparation script** that turns the owner's raw files into publishable ones (sizes, formats,
   the cover) and checks each property it promises, like `tools/fixtures/make-media.sh` does.
4. An **estimate before any spending**: the tool prices the whole plan from the live network and
   asks for an explicit confirmation before its first transaction.

## Non-goals

- Choosing a cover in the desktop app. A useful feature, but a project of its own, and it would
  touch files open in other work (PR #9, the in-app tutorial).
- Any change to `web/src`. The element's bundle is pinned byte for byte by
  `web/test/released-integrity.test.mjs`; the page uses element 1.2.0 as it is.
- Moving `web/demo/` to mainnet. The demo pages exercise edge cases (inert HTML and SVG, a broken
  cover, HEVC, a file larger than memory) that are test material, not a showcase. They stay on
  testnet and are left untouched.
- Publishing the 302 MB fixture or any stress file on mainnet: about 300,000 transactions for no
  visitor benefit.

## Anti-goals (a failure even if it works)

- **A key on disk, in an argument, in an environment variable or in a log.** The owner's seeds are
  typed into a hidden prompt and live only in the process's memory.
- **Mainnet spending without a number in front of the owner.** The tool never submits before it has
  printed the total for the whole plan and the owner has typed the confirmation.
- **A page that ships with links nobody verified.** Every link on the page is one the tool read back
  and verified after publishing (it already does this for testnet), and the owner opened in a browser
  at `/mainnet/<HASH>`.
- **Content the owner does not have the rights to.** Only the owner's own files and files written for
  this page.
- **A page that is empty or broken without script, or when a node lacks history.** The captions and
  links are static HTML; an element that cannot read shows its own error, the rest of the page stands.

## Constraints

- Everything in the repository is English: code, comments, commit messages, the PR. The site's text
  is in six languages, written by the `writing-site-copy` skill; German, Spanish, Portuguese and
  Chinese stay marked as translated from English, awaiting a native speaker's review (site README,
  "Languages").
- No change under `web/src` and no change to the files of PR #9 (`feat/desktop-release`) or of the
  in-app tutorial work under `app/`. The branch starts from `origin/dev`.
- Push, PR, merge into `dev` and anything touching `main` only on the owner's explicit command. A
  merge into `main` deploys the site.
- Ledger writes by Claude: testnet only. Mainnet transactions are signed and submitted by the owner.
- The site has no npm dependencies and no framework; the page follows the home page's structure
  (chrome markers, ids identical across languages, build-written canonical and hreflang).
- `node --test site/test/*.test.mjs` and `node site/build.mjs` pass.

## Approaches considered

**The page.**

1. *Six static pages, like the home page* — chosen by the owner. Text is in the HTML, visible
   without script, with hreflang alternates for search engines. The cost, the same markup six times,
   is cut by keeping the example list in one data module and letting the build write the cards into
   each page.
2. *One page for every language, text from a JS table*, like the file page. Fewer files, but no
   hreflang and an empty page until the script runs: worse for a showcase.
3. *A section on the home page.* Least work, but the home page is already long, and a page of its own
   is what people share.

**Publishing the video with a cover.**

1. *No cover, everything through the desktop app*, as the logo was. No code; the video's card is the
   generic banner and the element shows no poster.
2. *A mainnet mode in `XrplFile.Fixtures`* — chosen by the owner. A real cover, one command for the
   whole set, links written straight to `links.json`. The cost is new code the owner's key passes
   through; the owner runs it, Claude signs nothing.
3. *Cover selection in the desktop app.* Out of scope (non-goals).

## Design

### 1. Preparing the owner's files

`tools/examples/prepare.sh <owner-dir> <out-dir>` (Bash, ffmpeg and node, like `make-media.sh`).
Each check that fails stops the script.

- **Drawings** (PNG, JPEG or WebP). A drawing that is already one of those types and at most
  1,000,000 bytes is copied unchanged: the bytes are the owner's work, and re-encoding them is not
  ours to do. A larger one is converted to WebP, lowering the quality until it fits under 1,000,000
  bytes, the ceiling under which a link card shows the file itself (`FILE_PICTURE_MAX_BYTES` in
  `site/cards/rules.mjs`). Above that ceiling the card falls back to the banner, which defeats the
  point of a picture example. The script prints the dimensions, which the page needs for its aspect
  ratio.
- **Video.** Accepted as H.264/AAC MP4 (the format every browser plays; the element also plays
  WebM, but one format is enough here). If the `moov` box is not before `mdat`, the file is remuxed
  with `+faststart`, without re-encoding, so playback can start before the whole file is read. The
  script prints duration, dimensions and size.
- **Cover.** Either `cover.webp` / `cover.jpg` supplied by the owner, or a frame taken from the video
  at `--cover-at <seconds>`. Scaled to at most 960 px on the long side and saved as WebP, lowering
  the quality until it is at most 65,536 bytes (the format's cover limit, spec 4.1).
- **Text and SVG** are not prepared: they are committed in `tools/examples/media/` and copied as they
  are.

The owner's raw and prepared files live under `run/examples/` (ignored by git), not in the
repository: the ledger is where they are published, and a multi-megabyte video does not belong in
git history.

### 2. The text and the SVG

- **Text** (`tools/examples/media/about.txt`, `text/plain`, UTF-8, a few KB): a short note on what
  xrpl-file is, in the six site languages one after another, so the example also shows that
  multi-byte text (Cyrillic, CJK, accents) comes back exactly. Written with the `writing-site-copy`
  skill, plain and factual, no markup.
- **SVG** (`tools/examples/media/chunks.svg`, `image/svg+xml`, about 2 KB): a drawing in the site's
  visual language, chunks gathering into a manifest, like the "From a file to a link" drawings on the
  home page. Self-contained: no script, no external references, no event attributes, explicit
  `width`, `height` and `viewBox`, colours written in (it is shown outside the site's CSS).

Both are committed before publishing, so the bytes on the ledger are the bytes in git.

### 3. The mainnet mode of `XrplFile.Fixtures`

The current behaviour (testnet, faucet-funded throwaway accounts in `accounts.json`) stays the
default and is not changed. A second mode is selected by options:

```
dotnet run --project tools/XrplFile.Fixtures -- --network mainnet --writer <rAddress> --payer <rAddress> \
  tools/examples/plan.json run/examples/media run/examples
```

- **Network and node.** `--network mainnet|testnet`; the node defaults to `wss://xrplcluster.com`
  for mainnet (the site price tool's default) and the existing testnet node for testnet, and
  `--node <wss url>` overrides it. The network written into the links is the one named, and a
  `links.json` that already holds a link of another network is refused, so one output folder never
  mixes networks.
- **Accounts.** The owner's own writer and payer. A Batch run needs two different accounts (spec
  B1, enforced in `PublicationSession`); the tool refuses `--writer` equal to `--payer`. No
  `accounts.json` is read or written in this mode and the faucet is never called.
- **Seeds.** After the plan is validated and before any network call, the tool asks for the writer's
  seed and then the payer's in a prompt that does not echo. The key type comes from the seed's own
  encoding (`sEd…` is Ed25519, otherwise secp256k1): `XrplWallet.FromSeed` with no algorithm, not the
  testnet mode's fixed Ed25519. A seed whose derived address is the address on the command line is a
  master key. One that derives another address is accepted only as that account's regular key
  (`FromSeed(seed, masterAddress)`), confirmed against the account's `RegularKey` in `account_info`
  right after connecting and before anything is priced or submitted; otherwise the tool stops. Seeds are never accepted from arguments, environment variables or files. When standard
  input is redirected, the tool accepts seeds from it **on testnet only**, so the same code path can
  be run end to end by a script on testnet; on mainnet a redirected input is refused.
- **Plan rules.** In this mode every entry is written by the one writer: an entry with writer `b`, or
  a key ending in `-broken-cover`, is refused. A cover is an earlier entry, as today.
- **Estimate and confirmation.** Before the first transaction the tool prices every entry still
  missing from `links.json` with `PublicationSession.EstimateAsync` and prints one line per file
  (bytes, transactions, fee in XRP) and the totals, plus what the writer and the payer hold. The
  total the payer must cover is its own base reserve, counted once, plus the sum of every entry's
  worst-case fees (`PayerRequiredDrops` carries the reserve per entry, so it is not summed as is); the
  writer must cover its own reserve and its fees the same way. The tool refuses when either falls
  short (today's check is per entry). It then
  asks the owner to type `publish to mainnet`; anything else ends the run with nothing submitted. On
  testnet the confirmation is skipped.
- **Spending cap.** Each session runs under a `SpendingApproval` built from **the printed estimate of
  that entry**, passed as `SessionOptions.Approval`, with no headroom. Today the tool sets no approval
  and `PublicationSession.StartAsync` derives one from a fresh estimate at session start, which would
  silently approve a fee that rose after the owner confirmed. With the printed numbers as the cap, a
  rise stops the run; running it again prints a new estimate and asks again. A resume's
  `ApproveResume` callback compares the quote with the printed estimate of what is left in the same
  way.
- **Resume and read-back.** Unchanged: state under `<out-dir>/state/<key>`, keys already in
  `links.json` skipped, every published file read back from the ledger and verified before its link is
  written. A resumed mainnet run asks for the seeds and the confirmation again, with the estimate of
  what is left.

The plan for the examples is `tools/examples/plan.json`, committed, written once the owner's files
are known: the cover first, then the video naming it, then the drawings, the text and the SVG.

**Names on the ledger** (owner's decision). The manifest `name` of each of the owner's files is the
owner's own file name as delivered. When the preparation script converts a drawing to WebP, the base
name is kept and only the extension changes. The cover's name is the video's base name with
`-cover.webp`. The text and the SVG keep their repository names (`about.txt`, `chunks.svg`).

### 4. Rehearsal on testnet

The work runs in two phases. Until the owner's files arrive, the tool, the preparation script and the
page are built and tested against **stand-ins made for the purpose**: a drawing and a short H.264
clip generated with ffmpeg (test patterns, nothing anybody owns), with captions marked as stand-ins.
Nothing derived from the testnet fixtures' sources is used. When the owner's files arrive, the
stand-ins are replaced and the rehearsal is run again on them.

Before the owner publishes, Claude runs the same prepared files and the same plan through the new
mode on testnet with faucet-funded accounts whose seeds are passed on redirected input. This
exercises the cover-then-video order, the read-back and the page against real links. The page is
built and checked in a browser with the testnet links first; the mainnet links replace them once the
owner has published.

### 5. The examples page

**URLs and files.** `site/src/examples.html` and `site/src/<lang>/examples.html` for the five other
languages, served as `/examples` and `/<lang>/examples` (no trailing slash: `/ru/examples/` would be
taken by the file-page rule `/:network/:hash/` in `_redirects`). The build check that every built
file is covered by a rule, and the tests that compare the home pages across languages, extend to these
pages; the chrome (header, footer, canonical, hreflang) is written by the build as for the home pages.

**One list of examples.** `site/src/js/examples.mjs` exports the examples in page order, each with its
link, its kind (`drawing`, `video`, `svg`, `text`, `logo`), its size in bytes and, for a picture or a
video, its aspect ratio. Per-language words (a title and one line for each example) live in a strings
table with the same keys in every language, as the home page's tables do. A build marker
(`<!--@examples-->`) is replaced in each page by the cards, written from the list and that language's
strings and escaped like the embed snippet. Changing a link is one line in one file.

**A card.** A `<figure>` with:

- `<xrpl-file src="xrpl-file://mainnet/<HASH>" loading="lazy">` in a box whose aspect ratio is
  reserved from the list, so the page does not jump as files load;
- the element's own caption left on (its default): the file's name, size and type, with its copy and
  download controls. The page does not duplicate them;
- the page's caption: the example's title and one line on what it shows. For the owner's works the
  captions are neutral (owner's decision): "Drawing", "Short video" and a plain line on what the
  example demonstrates (a picture checked block by block, a video that starts before it is fully
  read), with no title of the work and no credit line;
- a link to the file page (`/mainnet/<HASH>`), which is also the link whose card Telegram and X show.

The logo's entry is written from what `site/README.md` records about it: `image/png`, 1422 x 1231,
122,958 bytes. Nothing is read from the ledger at build time.

The element is loaded from the site's own copy (`/element/1.2.0/`), as on the home page, and follows
the site's theme the way the hero card does.

**Order:** the owner's drawings, the video, the SVG, the text, the logo.

**Text.** A heading, a short lead (every file below is read from the XRP Ledger mainnet and checked
in your browser), and one sentence inviting the reader to share an example's link to see its card.
Written with `writing-site-copy`; nothing about internals beyond what the home page already says.

**Getting there.** A link to the page from the home page, next to the hero's actions, and in the
footer of every page. The footer is shared chrome (`renderFooter` in `chrome-markup.mjs`), so the link
gets a key in `chrome-strings.mjs` in all six languages.

**Host files.** `_headers` gives `Cache-Control: no-cache` to `/*.html` and the language homes only;
the pretty URLs `/examples` and `/<lang>/examples` match neither, so they get entries of their own,
with the tests in `site/test/headers.test.mjs`. `_redirects` needs no new rule: `/examples` has one
segment, and `/<lang>/examples` is already passed through by the `/<lang>/:file` rules.

### 6. Documentation

- `site/README.md`: the examples page, its URLs, and how to add or replace an example.
- `tools/examples/README.md`: preparing the files, the rehearsal, the owner's publishing command, what
  to check afterwards.
- `web/demo/index.html` keeps its testnet note; no change.

## The owner's steps

1. Put the drawings and the video (and optionally a cover) in a folder; say which frame the cover
   should be taken from if there is no cover file.
2. After Claude has prepared the files, rehearsed on testnet and committed the plan: run the command
   in section 3 with their own writer and payer, read the estimate, type the confirmation.
3. Hand over `run/examples/links.json`; Claude puts the links into `site/src/js/examples.mjs`.
4. Open each `/mainnet/<HASH>` page and the examples page in a browser; share one link in Telegram to
   see the card.

**Cost.** The estimate printed by the tool is the authority. For orientation, `site/data/price.json`
(2026-11-02) puts the fees at about 0.021 XRP per MiB at the base fee, so a set of 25 MiB costs about
0.5 XRP in fees and takes about 30,000 transactions. The price table's held and returned reserve
(1 XRP held, 0.8 XRP returned) describes a throwaway writer that is created for a run and deleted
after it. With the owner's existing accounts nothing is created or deleted: their base reserves are
already locked and stay so, and the run spends only fees.

## Left to the builder

- The exact copy in six languages (through `writing-site-copy`).
- Card styles, the grid, how a tall portrait video sits beside landscape drawings.
- The prompt implementation (hidden input on Windows and Linux consoles) and how the tool's options are
  parsed.
- Test design: offline tests for the plan rules and option checks of the new mode; site tests for the
  page in every language and for the build marker.
- Default quality steps and the long-side limit for the cover, within the limits above.
