# xrpl-file

A link that points at a file stored inside the XRP Ledger, and the tools to write and read one.

```
xrpl-file://mainnet/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B
```

An NFT's `URI` usually points at IPFS, and IPFS content lasts only as long as somebody pins it.
The ledger entry is permanent; the thing it names is not. An xrpl-file link names a *manifest*
transaction; the manifest names the ledger range holding the file's bytes in memos, and a
Merkle root to verify them. Nothing to pin, nothing to expire, and every byte checked.

The format is specified in [docs/spec/xrpl-file-v1.md](docs/spec/xrpl-file-v1.md). The design
and its reasoning are in [docs/design](docs/design). A manifest opens with a
`format` member holding this repository's URL, so a person who finds the JSON in an explorer can
see what it is and where the rules are; readers never look at it.

## What is here

| | |
|---|---|
| `src/XrplFile.Core` | .NET library: parse a link, read and verify a file, publish one. On top of the [`Xrpl`](https://www.nuget.org/packages/Xrpl) package |
| `web/xrpl-file.js` | the same reading in dependency-free browser JavaScript |
| `web/src/element` | the embeddable `<xrpl-file>` element: shows an image, text, video or a download from a link, built into one script by `cd web && npm run build` |
| `tests/vectors/v1.json` | vectors both implementations are held to |
| `tools/XrplFile.Vectors` | regenerates the vectors from the .NET code |
| `app/XrplFile.App.Core` | models for the desktop application's screens — no MAUI, no UI |
| `app/XrplFile.App` | the desktop application itself: MAUI Blazor Hybrid, Windows only so far |
| `site/` | [xrpl-file.staticbit.io](https://xrpl-file.staticbit.io): opens a link in the browser and gives each file link its own preview card; see [site/README.md](site/README.md) |
| `deploy/server` | how that site is provisioned and released on its own server |

A command-line uploader and an HTTP gateway are planned next. The desktop application below already
does the rest: publishing a file, reclaiming a throwaway account's reserve, and reading a link back.

## Reading a file (.NET)

```csharp
using Xrpl.Client;
using XrplFile;

IXrplClient client = new XrplClient("wss://xrplcluster.com");
await client.Connect();

XrplFileReader reader = new(new XrplClientLedgerSource(client));
ReadResult file = await reader.ReadAsync(XrplFileLink.Parse("xrpl-file://mainnet/…"), CancellationToken.None);

Console.WriteLine($"{file.Manifest.Type}, {file.Bytes.Length} bytes, root {file.Manifest.Root}");
```

## Reading a file (browser)

```js
import { LedgerClient, ENDPOINTS, XrplFileReader, parseLink } from './web/xrpl-file.js';

const link = parseLink('xrpl-file://mainnet/…');
const reader = new XrplFileReader(new LedgerClient(ENDPOINTS[link.network]));
const { manifest, bytes } = await reader.read(link, ({ chunksDone, chunksTotal }) => console.log(chunksDone, chunksTotal));
```

## Embed a file

The `<xrpl-file>` element shows a file from the ledger on any web page: an image, text, a video, or a
verified download for anything else. Build it, put the files of one version on a host, and add two lines:

```html
<script src="https://<host>/1.2.0/xrpl-file-element.js" integrity="sha384-3Ng6D0oyDdNcgbs1hTua5zQaHdn4E/3o7gnxr0ecp2jVh2kP2KbJymnO++iWWvP+" crossorigin="anonymous" defer></script>
<xrpl-file src="xrpl-file://testnet/…"></xrpl-file>
```

```bash
cd web && npm run build
```

The build writes `web/dist/<version>/`: `xrpl-file-element.js` (one classic script that defines the
element), `mp4box.min.js` (loaded beside it only when a plain MP4 is played), `mp4box-LICENSE.txt`,
`xrpl-file-reader.mjs` (the reader the element uses, as one ES module, for a page that reads files
itself) and `integrity.json` with the SHA-384 value of each script. The `integrity` attribute takes the
value for `xrpl-file-element.js` from the `integrity.json` of the same version. The version is in the
path, so a page that embeds `1.2.0` never changes under itself. The host serves both scripts with
`Access-Control-Allow-Origin`: Subresource Integrity on a script from another origin fails without it.

| Attribute | Values | Meaning |
|---|---|---|
| `src` | an `xrpl-file://` link | the file to show; when absent, the element's text content is the link, so `<xrpl-file>xrpl-file://…</xrpl-file>` works too |
| `width`, `height` | a number (pixels, as on `<img>`) or a CSS length such as `100%`, `50vh`, `auto` | presentational defaults that the site's own CSS overrides; the element reserves 16:9 until it knows the real proportions |
| `loading` | `lazy` (default), `eager`, `click` | `lazy` starts one viewport height before the element is visible, `eager` at once, `click` reads only the manifest and cover and loads the content on a click |
| `loader` | `true` (default), `false` | `false` keeps the reserved space empty until the content is ready, for sites that draw their own placeholder |
| `node` | a `wss://` URL | read from this node instead of the built-in ones; `ws://` is accepted only for `localhost` and `127.0.0.1`. A numeric network ID has no built-in node and needs this attribute |
| `theme` | `dark` (default), `light`, `auto` | the colour palette; `auto` follows the visitor's `prefers-color-scheme`, any other value is `dark` |
| `background` | `transparent` | `transparent` drops the box behind the file and the dark theme's shading of an empty box, so a picture with transparent parts sits on the page itself; it wins over `--xrpl-file-background`. A text file, the bars around a video, the backing of a cover, the overlay and the caption keep theirs. Text colours still follow `theme`: pick the theme that matches your page. Any other value, or none, keeps the box |
| `caption` | `off` | `off` removes the caption that appears on hover, with its copy button, for a page that shows the name, the link and a download control of its own; any other value, or none, keeps it |
| `lang` | `en`, `ru`, `de`, `es`, `pt`, `zh` (or a longer tag that starts with one: `pt-BR`, `zh-Hans`) | the language of every word the element draws: the loader, buttons, messages, notes and sizes (`12,4 МБ` in Russian, `12,4 MB` in German and Spanish and Portuguese, `12.4 MB` in English and Chinese). Without the attribute the element follows the nearest `lang` above it on the page, then the document's. Any other language is English. The element reads that `lang` when it connects and when its own `lang` changes; a page that switches language without a reload sets the attribute on the element. The `error` event's `detail.message` and the errors `save()` rejects with stay in English whatever the language: scripts match on them |

The inside is styled with the `theme` attribute, with the CSS custom properties below, and with
`::part(loader)`, `::part(caption)` and `::part(message)`. A property the site sets wins over the theme's
value, in every theme; set it on the element or on any ancestor:

```css
xrpl-file { --xrpl-file-accent: #7a3cff; --xrpl-file-on-accent: #ffffff; }
```

Both palettes meet WCAG 2.2 AA; a site that overrides a colour keeps that contrast itself, and an
accent goes together with `--xrpl-file-on-accent`, the text on it. These follow the theme:

| Property | Dark | Light | Used for |
|---|---|---|---|
| `--xrpl-file-accent` | `#4da3ff` | `#0b62c4` | buttons, progress, spinner, focus ring |
| `--xrpl-file-on-accent` | `#06121f` | `#ffffff` | text and icons on the accent |
| `--xrpl-file-background` | `#14161a` | `#f3f4f6` | the box, unless `background="transparent"` |
| `--xrpl-file-foreground` | `#e8e8ea` | `#1b1d22` | names, percentages, the type badge |
| `--xrpl-file-muted` | `#9a9ca3` | `#5b5f6b` | sizes, progress details, notes, the badge border |
| `--xrpl-file-track` | `rgba(255,255,255,.15)` | `rgba(0,0,0,.16)` | the unfilled part of the progress bar and spinner |
| `--xrpl-file-danger` | `#ff8a8a` | `#b3261e` | an error message in the box |
| `--xrpl-file-text-background` | `#1b1e23` | `#ffffff` | a text file |
| `--xrpl-file-text-foreground` | `#e8e8ea` | `#1f2328` | a text file and its bar |
| `--xrpl-file-text-bar` | `#262a30` | `#e6e8eb` | the bar above a text file cut short |

The caption, the loader over a video and the click-to-open view over a cover sit on a picture, not on
the page, so they stay dark in both themes. These are the same in both themes:

| Property | Default | Used for |
|---|---|---|
| `--xrpl-file-media-background` | `#000` | around a video, behind a cover |
| `--xrpl-file-overlay` | `rgba(0,0,0,.72)` | the caption, the play button, a failed video |
| `--xrpl-file-overlay-foreground` | `#ddd` | text on the overlay, over a cover and over a video; the ring around the play button |
| `--xrpl-file-overlay-accent` | `--xrpl-file-accent`, else `#4da3ff` | buttons, progress and focus ring on the overlay, over a cover and on the caption |
| `--xrpl-file-overlay-on-accent` | `--xrpl-file-on-accent`, else `#06121f` | text and icons on the overlay accent |
| `--xrpl-file-overlay-border` | `rgba(255,255,255,.6)` | the caption's copy button |
| `--xrpl-file-overlay-track` | `rgba(255,255,255,.15)` | the progress track over a video |
| `--xrpl-file-overlay-success` | `#6fd08c` | the verified mark, "Copied" |
| `--xrpl-file-overlay-danger` | `#ffa0a0` | "Copy failed", a failed video |
| `--xrpl-file-scrim` | `linear-gradient(to top, rgba(0,0,0,.65), rgba(0,0,0,.15))` | over a cover; the dark theme also draws it on an empty box |
| `--xrpl-file-radius` | `10px` | the corners |

Over a cover and on the caption the accent keeps its dark-theme default, `#4da3ff` with `#06121f` on it,
unless the site sets `--xrpl-file-accent`, which then applies there too, or `--xrpl-file-overlay-accent` and
`--xrpl-file-overlay-on-accent`, which apply only there. A site whose frame accent is a dark colour sets the overlay pair
to a light one: the overlay is dark in both themes. The element dispatches `xrpl-file-progress` (`detail`:
`{ bytesDone, bytesTotal, blocksVerified, blocksTotal }`), `load` once the content is shown (`detail`:
`{ name, type, size, network, link }`) and `error` (`detail`: `{ name, message }`; the message is English whatever the `lang`).

Three more events let a page draw the file's layout from the element's own read, with no ledger
requests of its own. Each fires at most once per load for a given manifest, chunk or block, and none
bubbles. `xrpl-file-manifest` fires when the element starts reading the content, before its first block is read
(`detail`: `{ size, chunk, chunks, blocks, firstLedger, lastLedger }`; `chunks` is `ceil(size / chunk)`).
`xrpl-file-chunk` fires when a chunk has been read (`detail`: `{ index, ledger }`, the chunk's index in the
file and the ledger of its transaction), in the order the chunks arrive, which is not always index order.
`xrpl-file-block` fires when a block has verified (`detail`: `{ index, firstChunk, lastChunk }`, the chunks
the block covers; the last block ends at the file's last chunk). "Chunk" means read, not verified: a chunk
event can precede the block event that vouches for it, and a block that fails verification emits no
block event after its chunks. The events report what the element reads to show the file, which is not
always the whole file: a text preview stops at 1 MB (the blocks that cover it are read whole), an image
whose bytes do not match its type reads only its first block, and a download view reads no block, so
only `xrpl-file-manifest` fires. A video reads as it plays: its chunk and block events continue after
`load`, blocks may arrive out of index order, and some may never be read. With `loading="click"` nothing
fires until the visitor opens the content. A `save()` reads the file again but reports nothing, and a load
that is reset (the `src` changes, or the element is disconnected) emits nothing more.

The `load` detail describes the file from the same read, so a page needs no second one: `name` is the
file name the element shows and saves under, or `null` when the manifest has none; `type` is the media
type without parameters; `size` is in bytes; `network` and `link` are canonical (`link` is
`xrpl-file://<network>/<HASH>`). It is present on every `load`, including the download view of a type the
element does not display.

`element.save()` runs the same verified download as the element's own Download button and returns a
promise of how it went: `'saved'` (written through the browser's save dialog), `'started'` (handed to
the browser as a download; a page cannot see whether the browser kept it) or `'cancelled'` (the visitor
closed the dialog). It rejects with an `XrplFileError` whose `code` is `NotLoaded` before the file's
description is known, with the element's own error while the element shows one, and with the reason when
the save fails; a block that does not verify is never written. It can also reject with a `DOMException`
that is not an `XrplFileError`: `AbortError` when the element is reset or loads another file while the
save runs, and `NotAllowedError` or `SecurityError` when the browser's file APIs refuse the write
(a refused save dialog alone does not reject: the save then falls back to an in-memory download). Call
it after `load`, from a user gesture: the browser refuses the save dialog otherwise.

The layout follows the element's own width, whatever the page's. From 240 px it is the full layout; from
120 to 239 px a compact one (one line per text, a "Save" button, a caption of "✓ testnet" with a copy icon);
under 120 px, or under 56 px of height, an icon one (a type badge and a download icon, a warning icon for an
error, a small ✓ that copies the link). The full error sentence stays available to screen readers and as a tooltip in every tier.
In a box under 120 px high, the bar above a text file cut short to its first megabyte keeps to one line,
its note and save status shortened with an ellipsis (each keeps its full text as a tooltip), and the margins
tighten, so that at least one line of the text shows.

```js
const element = document.querySelector('xrpl-file');
const button = document.querySelector('#download');
element.addEventListener('xrpl-file-progress', (event) => console.log(event.detail.blocksVerified, event.detail.blocksTotal));
element.addEventListener('error', (event) => console.warn(event.detail.name, event.detail.message));
element.addEventListener('load', (event) => {
  const { name, size, link } = event.detail;
  button.disabled = false;
  button.onclick = async () => {
    try {
      console.log(await element.save(), name, size, link);
    } catch (error) {
      console.warn(error.code ?? error.name, error.message);
    }
  };
});
```

The browser hashes the file only in a secure context, so the page must be served over HTTPS (or from
`localhost`). A site with a Content Security Policy needs:

- `script-src` for the origin that serves the scripts;
- `connect-src` for the node URLs: `wss://xrplcluster.com wss://s2.ripple.com` on mainnet,
  `wss://s.altnet.rippletest.net:51233` on testnet, or the URL given in `node`;
- `img-src blob:` and `media-src blob:`.

The element styles itself with constructable stylesheets, so the policy does not need
`style-src 'unsafe-inline'`.

Sites that enforce Trusted Types (`require-trusted-types-for 'script'`) are not supported in this
version: the element assigns one static template through `innerHTML`.

Inside a cross-origin `<iframe>` the browser refuses the save dialog, so Download assembles the file in
memory (up to 2 GiB) and hands it to the browser as an ordinary download instead of streaming it to
disk. An `<iframe sandbox>` that hosts the element must include `allow-downloads`; without it the
browser drops the download silently, and the element can only say "Download started.". Copy link
works there too: where the Clipboard API is refused it falls back to `execCommand('copy')`, so
`allow="clipboard-write"` on the iframe is optional.

`web/demo` has pages that exercise it against published testnet files: `index.html` (every state),
`many.html` (ten elements, with counters of the connections and requests they make), `snippet.html` and
`head.html`; `dev.html` loads the unbundled sources from `web/src` and needs no build. After a build,
`node web/demo/serve.mjs 8790` serves them, starting at `http://127.0.0.1:8790/demo/index.html`.

## Publishing a file (.NET)

Publishing is a session, not a single call: it prices the run before spending anything, writes a
state file as it goes, and hands back a report once the manifest is in the ledger. Underneath,
the file still moves one `AccountSet` per 1000 bytes, then a block list, then a read-back against
the whole file's Merkle root, and only then the manifest — a run that breaks before the manifest
lands leaves no link behind, whichever strategy is carrying the chunks.

```csharp
XrplWallet wallet = XrplWallet.FromSeed(Environment.GetEnvironmentVariable("XRPL_SEED")!);
XrplClientLedgerSource source = new(client);
XrplClientLedgerSink sink = new(client);
SessionOptions options = new() { StateDirectory = "runs", BudgetDrops = 5_000_000 };

// What it will cost, before any of it is spent.
PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, wallet.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

Console.WriteLine($"{estimate.Transactions} transactions, {estimate.RequiredDrops} drops needed, {estimate.SpendableDrops} spendable");

// The strategy's fee and the estimate's must be the same number, or the run is approved at a
// price nobody submits. StartAsync refuses the pair rather than letting that through, so the
// estimate's fee goes into the strategy that will do the submitting.
await using TicketStrategy strategy = new(sink, source, new WalletSigner(wallet),
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    wallet.ClassicAddress, keyReference: "my-key", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(
    new Progress<PublishProgress>(p => Console.WriteLine($"{p.Stage} {p.Done}/{p.Total}")), CancellationToken.None);

Console.WriteLine(report.Link);
```

The estimate judges the account by what it may actually spend rather than by its balance: one
whose trust lines, offers or leftover tickets already lock an owner reserve is refused before the
run starts, instead of after the chunks are paid for and the first `TicketCreate` comes back
`tecINSUFFICIENT_RESERVE`. `RequiredDrops` is still what the run needs; `SpendableDrops` is what
the account has to meet it with.

`TicketStrategy` mints tickets and rides them, so chunks go to the ledger in parallel rather than
one after another — on a standalone stand, 200 chunk memos landed in about 22 seconds on tickets
against 50-plus seconds projected for one at a time. `SequenceStrategy` is the slower alternative,
one `AccountSet` at a time on the account's own sequence; it needs no reserve beyond what the
account already holds, which is what to reach for when the account cannot carry a batch of
tickets.

Every ticket this library mints carries a mark, so an interrupted publication's leftovers are not
lost: the next ticketed publication from the same account spends them before it mints, and any
successful publication — on either strategy — spends whatever is still left on transactions that
do nothing, within `SessionOptions.TicketReleaseTimeout` (two minutes by default). The report says
how many were released (`TicketsReleased`), had already gone (`TicketsAlreadyGone`) or are still
live (`TicketsNotReleased`), and why the tidy-up failed if it did (`TicketReleaseFailure`); the
publication itself never fails because of it. It also says what the tidy-up cost
(`TicketReleaseFeeDrops`) and what it may still cost (`TicketReleaseFeeDropsInDoubt`) — a spend
still queued when time ran out may land until its `LastLedgerSequence`, ten ledgers on. On a loaded
public node the tidy-up resends a spend the node was too busy to take, and slows down when the node
warns that the client is nearing its rate limit. Tickets without the mark — minted by anything else,
or by a version of this library from before the mark — are never touched. A `SequenceStrategy`
tidies up only when it is given the ledger source: `new SequenceStrategy(sink, signer, options, source)`.

The session writes a state file before the first transaction. If the process dies mid-run,
`PublicationSession.ResumeAsync` picks it back up from that file — but the file is where to look,
not what to believe: a resume reconciles against the ledger itself (the same `account_tx` sweep a
strategy uses) rather than trusting what the file last recorded, because a crash can fall between
a submission and its record. The state file never holds a seed; it holds a `keyReference` string
the caller chose, meaningful only to whatever key store the caller uses to resolve it back to a
signer.

### Collector settings for hosts

A host that resumes Batch publications or reads long histories should set `System.GC.LOHThreshold`
to `4194304`. Every `account_tx` page reaches the SDK as one frame of up to about 1.2 MB, which is
large-object-heap garbage, and after an Argon2id unlock the collector's budget for that heap lets
hundreds of megabytes of dead pages pile up during a resume's history read; at this threshold the
frames die in gen0 collections instead. The setting goes in the csproj of the host executable:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="System.GC.LOHThreshold" Value="4194304" />
</ItemGroup>
```

The environment form is `DOTNET_GCLOHThreshold=0x400000`. The runtime caps the threshold at about
4 MiB, so a higher value changes nothing.

`Argon2idKdf.Derive` with 16 MiB of working memory or more ends with one aggressive, blocking,
compacting full collection, so the key derivation's lanes do not stay committed. It pauses every
thread of the process while it runs: derive off the UI thread, as `KeyVault` does.

### Publishing without a funded account

An application can publish on behalf of somebody who never hands it a seed. A key is generated
for this one run, its address is shown for the person to fund from a wallet they already trust,
and the library never asks for, holds, or moves the account the money came from — the whole
surface is *show this address, watch this address, spend from this key*.

```csharp
XrplWallet throwaway = XrplWallet.Generate();
ThrowawayAccount account = new(sink, source, new WalletSigner(throwaway));
long startedIn = await source.CurrentLedgerAsync(CancellationToken.None);

PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, throwaway.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

// account.Address (== request.QrPayload) goes on screen next to estimate.RequiredDrops, as text
// to copy and as a QR code to scan. The money may arrive from a phone a minute from now or an
// hour from now, so the wait has no deadline of its own.
FundingRequest request = await account.AwaitFundingAsync(estimate, TimeSpan.FromSeconds(5), CancellationToken.None);

await using TicketStrategy strategy = new(sink, source, new WalletSigner(throwaway),
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    throwaway.ClassicAddress, keyReference: "throwaway", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(null, CancellationToken.None);

// A proposal, read from whoever's payment funded the account — never assumed, and RefundAsync
// takes only what the caller actually passes, so a person confirms it or types another address.
string funder = await account.FunderAsync(startedIn, CancellationToken.None)
    ?? throw new InvalidOperationException("nobody has paid this account yet");

RefundResult refund = await account.RefundAsync(funder, CancellationToken.None);
```

`RefundAsync` sends back everything the account may actually spend — the balance less the fee
and less every reserve the account is still under. The base reserve always stays behind: getting
it back needs `AccountDelete`, and that refuses with `tecTOO_SOON` until the account's sequence
plus 254 ledgers has passed, counted against the last *validated* ledger index (rippled's own
rule reads `+ 255` against the ledger it is still building, one ahead of what a client can read —
see the design document for the one-ledger difference and how it was measured). A ticketed
transaction — everything this library sends once tickets are minted — never moves the account's
sequence at all, so a publication that ran entirely on tickets leaves the boundary exactly where
its last mint left it: nothing a caller submits afterwards brings it closer, only ledgers closing
does. `ThrowawayAccount.DeletableFromLedgerAsync` reports the ledger index at which deletion (not
implemented by this library) would stop refusing.

## Keys and the vault (.NET)

Every sample above needs a wallet from somewhere, and `XrplFile.Keys` is where one actually lives
between publications. It is one file, encrypted under a single master password, holding as many
entries as an application cares to put in it. The file is deliberately portable: a backup is a
copy of it, and a machine that dies does not take the keys with it. That is the whole point of
the one-factor design — nothing about the file depends on the machine that wrote it, except
"remember on this device", which is convenience layered on top and simply inert on a copy.

The other side of that portability is that a forgotten master password loses every key that was
never taken out of the vault another way. `IKeyVault.ExportAsync` is that other way: it reveals
one entry and seals it into a password-protected container — bytes an application can write to a
file or turn into a QR code. It writes nothing itself, and deliberately does not mark the entry
`Exported`: that flag means the secret survives somewhere other than this vault, and only the
caller that actually put the bytes somewhere knows whether it does. Record it with
`UpdateAsync(reference, new VaultEntryEdit(Exported: true), ...)` once the write has succeeded,
and the vault can then warn "these keys exist nowhere else" before the day the password is
actually forgotten instead of after. Erring the other way — marking a container nobody kept —
silences that warning exactly where it was needed.

A vault that opens with `VaultUnlockResult.VaultDegraded` is open — listing, revealing and
exporting all keep working, which is how the keys that are still intact get rescued. What it will
not do is write. The record's integrity code disagreeing with its own contents is the only evidence
there is that an entry was removed from the file or that its bytes were edited under it, and every
write recomputes that code, so the first write is refused with `VaultError.Degraded` until the
application has told the user and called `AcknowledgeDamageAsync`. Nothing is written by the
acknowledgement itself, so a vault locked and opened again reports `VaultDegraded` once more.

Signing goes through the vault rather than through a wallet held in memory. `VaultSigner`
implements the same `ITransactionSigner` seam `WalletSigner` does in the samples above: it asks
the vault to reveal its entry for exactly one signature and lets the derived wallet go immediately
afterwards, so locking the vault genuinely takes the signing key away rather than only hiding a
reference to one still sitting in memory. A vault that locks mid-publication — an idle
`VaultAutoLock`, or an explicit `LockAsync` from elsewhere in the application — fails the very
next signature with `VaultError.Locked`, and that failure is recovered exactly the way any
interrupted publication is recovered (see "Publishing a file" above): unlock the vault and resume
the session from its state file, rather than starting the run over and paying for its chunks a
second time.

```csharp
using System.Text;
using XrplFile.Keys;

await using KeyVault vault = new("vault.json", new Argon2idKdf());
await vault.CreateAsync(Encoding.UTF8.GetBytes("a strong master password"), CancellationToken.None);

XrplWallet generated = XrplWallet.Generate();
string reference = await vault.StoreAsync(
    new NewVaultEntry(VaultEntryKind.Generated, "run one", "mainnet", generated.ClassicAddress, generated.Seed),
    CancellationToken.None);

// `reference` is what a publication's state file records as its KeyReference — never a seed.
// Nothing about `generated` itself has to survive past this call.
```

Publishing then looks exactly like "Publishing a file" above, with the wallet replaced by a
signer over the vault entry `reference` names:

```csharp
using XrplFile.Keys;

await using KeyVault vault = new("vault.json", new Argon2idKdf());
VaultUnlockResult unlocked = await vault.UnlockAsync(
    Encoding.UTF8.GetBytes("a strong master password"), CancellationToken.None);
if (unlocked != VaultUnlockResult.Success)
{
    throw new InvalidOperationException($"the vault did not open: {unlocked}");
}

await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(15), TimeProvider.System);
VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, CancellationToken.None, autoLock);

XrplClientLedgerSource source = new(client);
XrplClientLedgerSink sink = new(client);
SessionOptions options = new() { StateDirectory = "runs", BudgetDrops = 5_000_000 };

PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, signer.Account, new FileInfo("cover.png").Length, options, CancellationToken.None);

await using TicketStrategy strategy = new(sink, source, signer,
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    signer.Account, keyReference: reference, "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(null, CancellationToken.None);
```

`autoLock.Touch()` is called after every signature `VaultSigner` makes, so a run signing
continuously for tens of minutes never locks itself out mid-run even while `VaultAutoLock`'s idle
window keeps a machine left alone from staying unlocked forever. It is optional: a `VaultSigner`
built with none touches nothing, and the vault only ever locks when something calls `LockAsync`.

## The desktop application

`app/XrplFile.App` is a MAUI Blazor Hybrid application, Windows only for now, built on
`XrplFile.Keys`. It is the first slice of a larger application (design in
[docs/design](docs/design)): today it creates and unlocks a vault, lists keys,
adds one by generating it, importing a typed seed, or opening a backup container and choosing a key
out of it, exports and reveals a seed, changes the master password, turns "remember on this device"
on and off, deletes a key, and reports damage. It also spends XRP: the Publish tab writes a file into
the ledger from a key held in the vault, with an estimate shown before anything is signed and a
publications list kept afterward; the Accounts tab reclaims a throwaway account's reserve once the
account is old enough for the ledger to allow deleting it; and the Read tab takes an `xrpl-file://`
link — pasted in, or reached with one click beside a row of that publications list — shows the name,
size and type its manifest describes before downloading anything, then reads it block by block with
progress and cancellation and saves the verified result wherever the person chooses. The application
reimplements no hash and no tree: every cryptographic check — the block list against the manifest's
root, each block against its own hash — stays in `XrplFile.Core`, the same reader the samples above
call directly. What the Read tab does add on top, so a large file can show progress and be cancelled
rather than downloading as one uninterruptible call, is a block-by-block loop, and that loop still
only calls the library's own verifying methods for each block.

The "?" (Tutorials) item on the rail opens short tutorials for the five topics, and the application
offers them once on first start.

Every key the application manages lives in one vault file, encrypted under a single master password
chosen when the vault is created. The file is deliberately portable: a backup is a copy of it, and a
machine that dies does not take the keys with it. The other side of that is a forgotten master
password loses every key that was never taken out of the vault another way — which is exactly why
exporting exists, and why the key list marks a generated key that has never been exported: that flag
is how the application shows which throwaway accounts exist nowhere else, before the day the
password is the only thing standing between someone and them.

Signing goes through the vault rather than through a wallet held in memory, so locking it takes the
signing key away for real, not just a reference to one still resident. A vault that locks in the
middle of a publication fails the very next signature, and that failure is recovered the same way
any interrupted publication is: unlock the vault and resume the session from its state file, rather
than starting the run over and paying for its chunks a second time.

A container exported here can be imported into the Wallet application, and a container written there
can be imported here: the export format is the Wallet project's own — `"SBW1"`, PBKDF2-HMAC-SHA256
at up to 1,000,000 iterations — adopted so that a key is not locked into whichever of the two
products happened to create it. The vault file itself is untouched by that and stays Argon2id; the
container is a different artifact, the one that actually travels.

### Building and running it

.NET 10 SDK with the `maui-windows` workload:

```bash
dotnet workload install maui-windows
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
dotnet run --project app/XrplFile.App/XrplFile.App.csproj
```

On Windows, `run-desktop-app.bat` in the repository root does the same in one step: it checks for
the SDK and the workload, closes a running instance, publishes a Release build to
`app\XrplFile.App\bin\publish` and starts it. Double-click it or run it from any directory; no
administrator rights are needed, because the application is unpackaged. To start the last build
again without rebuilding, run `app\XrplFile.App\bin\publish\XrplFile.App.exe`.

Windows is the only target this repository builds or tests. `XrplFile.App.Core` carries no
Windows-specific code — every decision the screens make lives there and is covered by
`XrplFile.App.Core.Tests` with no operating system in the loop — but the two places the platform is
unavoidable, "remember on this device" and the clipboard and file-picker services, have only a
Windows implementation so far. CI mirrors this split: the Linux jobs build and test everything in
`XrplFile.Libraries.slnf`, and a separate `windows-latest` job builds `XrplFile.App.csproj` itself,
with no tests of its own to run — its job is proving the markup still compiles against the models.

## Building and testing

.NET 10 SDK and Node 22+.

```bash
dotnet build XrplFile.Libraries.slnf
dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.Keys.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.SitePrice.Tests
node --test site/test/*.test.mjs
cd web && node --test test/*.test.mjs
```

`XrplFile.Libraries.slnf` is a solution filter over `XrplFile.sln` that excludes `XrplFile.App`
(`net10.0-windows10.0.19041.0`) — the only project in the solution that cannot build outside
Windows. `dotnet build XrplFile.sln` itself now needs Windows and the `maui-windows` workload for
that reason; see "The desktop application" above for building and running `XrplFile.App` itself.

`web/integrity/<version>.json` is the integrity the released version was built with, and a web test
fails when `web/src` builds that version to anything else: `/element/<version>/` is served as immutable,
so a changed file under a published version breaks the `integrity` attribute of every page that embeds
it. Bump the version in `web/package.json`; update the record in place only for a version not yet released.

The browser element's npm package (the jsDelivr embed URL) is assembled, not published, by one more
command after `cd web && npm run build`: `node web/pack.mjs` writes `web/npm/<version>/` with the build's
files copied byte for byte (so the `integrity` values stay valid on jsDelivr), a `package.json`, the "Embed
a file" section as `README.md` and the `LICENSE`. The package name is `ELEMENT_PACKAGE` in
`site/src/js/config.mjs`. Publishing that directory to npm is the owner's step.

`dotnet test` does not run xunit.v3 on the .NET 10 SDK: the test project is an executable and
is driven with `dotnet run`, taking xunit's own filters (`-class`, `-method`, `-trait`, `-trait-`)
after the `--` separator instead of `dotnet test`'s `--filter`.

Integration tests need a standalone rippled, brought up with the Compose file in `.ci-config`.
They read the node's WebSocket URL from `XRPL_FILE_NODE_URL`, defaulting to `ws://localhost:6006`
(the port the Compose file publishes):

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml down
```

If ports 5005/5006/6006/6007 are already held by another stand on the same host, layer
`.ci-config/docker-compose.local-ports.yml` on top to move this repo's rippled to
15005/15006/16006/16007, and point the tests at the moved WebSocket port:

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
```

To regenerate the vectors after a deliberate change to the format:

```bash
dotnet run --project tools/XrplFile.Vectors -- tests/vectors/v1.json
```

## Reporting a vulnerability

See [SECURITY.md](SECURITY.md).

## Licence

MIT, see [LICENSE](LICENSE).
