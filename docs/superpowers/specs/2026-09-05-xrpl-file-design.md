# xrpl-file: a link format for files stored inside the XRP Ledger

Date: 2026-09-05
Status: approved design, ready to be broken into implementation plans
Repository: `StaticBit-io/xrpl-file` (this repository)

## Why

An XLS-20 NFT carries a `URI` of at most 256 bytes. In practice that URI points to a JSON
metadata file, and the metadata points to an image, a video or another file. Almost always
both hops go to IPFS, and IPFS content survives only as long as somebody pins it. Pins lapse,
gateways go away, and the NFT is left holding a pointer into nothing. The ledger entry is
permanent; the thing it names is not.

Two earlier experiments in this organisation showed that the ledger itself can hold the bytes:
[war-and-peace-on-xrpl](https://github.com/StaticBit-io/war-and-peace-on-xrpl) put a 3.3 MB
novel into 3,278 memos on mainnet for 0.04 XRP, and the video platform streams a clip out of
625 memos. Both proved the storage. Neither produced a *link*: war-and-peace keeps its index in
a JSON file beside the site, and the video platform's memo layout is tied to the platform's own
account and claim flow.

This project defines the missing piece: a URI that points at a file inside the ledger, carries
enough to locate, order and verify every byte, and fits in an NFT `URI` field. Around it sit a
.NET library, a browser library, a command-line uploader, an HTTP gateway, a demo page and an
embeddable HTML element.

## Decisions

| Decision | Choice |
|---|---|
| What the link identifies | the hash of a *manifest* transaction; the manifest holds everything else |
| Content integrity | Merkle root over chunks, inside the manifest; the transaction hash authenticates the manifest |
| Mutability | none in v1; a link always resolves to the same bytes or fails |
| Chunk carrier | `AccountSet` with no fields, 1000 bytes of `MemoData`, chunk index in `MemoType` |
| Manifest and block list encoding | JSON in `MemoData`, `MemoFormat` = `application/json` |
| Size ceiling | none in the format; the v1 reader refuses files above 20 MB by default |
| Where the code lives | this repository, on top of the published `Xrpl` NuGet package; the SDK is not changed |
| Language of everything in the repository | English: prose, identifiers, error messages, commit messages |

Each of these has consequences, and they are spelled out below rather than left implicit.

## 1. The link

```
xrpl-file://<network>/<tx-hash>
```

| Part | Rule |
|---|---|
| scheme | `xrpl-file`. Distinct from the `xrpl:` prefix discussed for payment request URIs (XLS-32d). Parses with a standard URL parser: `new URL()` puts the network in `hostname` and the hash in `pathname` |
| network | `mainnet`, `testnet`, `devnet`, or the decimal `NetworkID` of any other XRPL-protocol network. Required: a link without a network is ambiguous |
| tx-hash | 64 hexadecimal characters, the hash of the manifest transaction. Parsed case-insensitively, emitted in upper case |

No query string and no fragment in v1. The MIME type, the size and the name live in the
manifest, not in the link, so there is one source of truth.

The canonical link is 84 bytes. Hex-encoded into `NFTokenMint.URI` it uses 168 of the 256
bytes rippled allows (`kMaxTokenUriLength`).

### NFT integration (XLS-24)

Two files are published: the media, then a metadata JSON whose `image` (or `animation_url`)
is the media's `xrpl-file://` link. The NFT's `URI` is the link to the metadata JSON.

Resolving an NFT therefore runs: `NFTokenID` → `URI` (via `nft_info` on a Clio node, or
`account_nfts` when the owner is known) → metadata manifest → metadata bytes → media manifest
→ media bytes. Each hop is a link of the same shape, verified the same way.

The metadata JSON may additionally carry `image_gateway` with an HTTPS URL to the gateway
(section 6), so marketplaces that only understand HTTP can show the image today. That field
is a convenience, not part of the format, and a reader never needs it.

## 2. Ledger layout

Every transaction is an `AccountSet` with no fields set, sent by one account — the *writer*.
It is the cheapest transaction there is: nothing changes, one `AccountRoot` is touched, the
metadata is minimal. A `Payment` would carry a memo equally well and write roughly twice the
metadata.

Three kinds of transaction, told apart by `MemoType`:

| Kind | MemoType | MemoData | MemoFormat |
|---|---|---|---|
| chunk | `xf/c/<i>` — decimal chunk index from 0, no leading zeros | raw file bytes, 1000 per chunk, the last one shorter | absent |
| block list | `xf/b/<j>` — decimal list index from 0 | JSON array of block entries (below) | `application/json` |
| manifest | `xf/m` | JSON object (below) | `application/json` |

Order of writing: chunks, then block lists, then the manifest last. The manifest needs the
ledger range and the block ledgers, which are only known once everything else has validated.

### Byte budget

rippled refuses a transaction whose serialized `Memos` array exceeds 1024 bytes
(`isMemoOkay`, `STTx.cpp`). A memo carrying only `MemoData` costs 5 bytes of framing, which is
where war-and-peace's 1,019-byte chunks come from. A `MemoType` costs 2 bytes of framing plus
its length. `xf/c/` is 5 characters and an index of up to 12 digits is 12 more, so
1019 − 2 − 17 = 1000. The chunk payload is fixed at **1000 bytes**, which also makes the
chunk count derivable: `ceil(size / chunk)`.

The index lives in `MemoType`, not in front of the data, for two reasons: the Merkle leaf is
then exactly the bytes in `MemoData`, and chunks can be submitted in parallel through tickets
without the account's sequence numbers carrying the order.

The framing model above is checked against two measured points: 1019 bytes with no `MemoType`
is accepted (war-and-peace) and 1019 with an 11-byte `MemoType` is refused. With the longest
marker the serialized array is exactly 1024 bytes, which rippled accepts (`> 1024` refuses).
The video platform settled on 999 by measurement — it tried 1011 and 999, never anything
between — so the first integration test of the writer submits a 1000-byte chunk under the
12-digit marker `xf/c/999999999999` to a standalone node and keeps that as a regression test.

### Manifest

```json
{
  "v": 1,
  "type": "image/png",
  "size": 734512,
  "chunk": 1000,
  "root": "9F2C…64 hex…",
  "ledgers": [106497159, 106497394],
  "blocks": 1,
  "lists": 0,
  "name": "cover.png"
}
```

| Field | Meaning |
|---|---|
| `v` | format version; this document defines 1 |
| `type` | MIME type of the file |
| `size` | file length in bytes |
| `chunk` | chunk payload size the writer used. v1 writers always write 1000; readers accept any value ≤ 1000 so the field can change without a new version |
| `root` | Merkle root over the chunks (section 3), 64 hex |
| `ledgers` | `[first, last]`: the validated ledger range holding every chunk and every block list. The manifest itself lands after `last` |
| `blocks` | number of Merkle blocks, `ceil(chunkCount / 1024)` |
| `lists` | number of `xf/b/*` transactions. 0 when `blocks` is 1 |
| `name` | optional file name, used by the gateway for `Content-Disposition` |

The manifest is one memo and must fit in 1019 bytes including its `MemoType`/`MemoFormat`
framing; the writer refuses a `name` that would push it over.

### Block list

One entry per block: `{"i": <block index>, "h": "<block hash, 64 hex>", "l": <ledger of the
block's first chunk>}`. Entries are about 95 bytes, so a list transaction holds about ten. A
1 GB file is 1000 blocks and about 100 list transactions; a 5 MB image is 5 blocks and one.

`l` is what makes targeted reading possible: any block can be read from its own ledger with a
few `account_tx` pages, however large the whole file is. The video platform arrived at this
after finding that a single tree over all chunks cannot be verified piecemeal.

### The chunk rule

The same definition is used by the writer, the reader and the gateway, and it is stated once:

> A chunk is a validated transaction whose `Account` is the writer, whose result is
> `tesSUCCESS`, whose ledger lies within the manifest's `ledgers` range, and whose `MemoType`
> is `xf/c/<i>` with `MemoData` present.

If several transactions carry the same index, the earliest in canonical ledger order is
authoritative. A writer that retries an index must resubmit the same bytes; nothing else is
ever valid, because a differing duplicate would fail the block hash regardless of which one
the reader picked.

## 3. Merkle tree

The scheme is the one the video platform uses on mainnet, adopted unchanged so both projects
can share test vectors.

- Leaf: `sha256(0x00 || chunk bytes)` — the bytes of `MemoData`, nothing else.
- Node: `sha256(0x01 || left || right)`. The domain prefixes stop a node from being passed off
  as a leaf.
- Leaves are grouped into **blocks of 1024 chunks** (about 1 MB). A block hash is the Merkle
  root over its leaves; the file root is the Merkle root over the block hashes, in block order.
- An odd element on any level is promoted to the next level unpaired. It is never duplicated:
  duplicating the last element is the classic construction under which two different leaf
  sets share a root.
- A file of a single block has no list; its `root` *is* the block hash.
- Both levels are built by chunk index, not by ledger order, so a re-sent chunk cannot change
  the root.

## 4. Reading and verifying

The algorithm is identical in the .NET library and the browser library. Divergence between two
implementations of "what is a chunk" cost the video platform a mismatched checksum on its first
real publication; the shared test vectors (section 8) exist to keep them in step.

1. **Parse the link.** Network and hash. The network maps to an endpoint table; an unknown
   network with no explicit node URL is a parse error, not a network error.
2. **Fetch the manifest** with `tx` by hash. Accept only `validated: true`, `tesSUCCESS`,
   `MemoType` = `xf/m`, JSON that parses, `v` = 1, and internally consistent counts:
   `ceil(size / chunk)` chunks, `ceil(chunks / 1024)` blocks equal to `blocks`, `lists` = 0 iff
   `blocks` = 1. The writer is the transaction's `Account`. Anything else is a corrupt
   manifest, and reading stops there.
3. **Collect the block list.** With one block it is the single entry
   `{i: 0, h: root, l: ledgers[0]}`. Otherwise page `account_tx` backwards from `ledgers[1]`
   until `lists` transactions of kind `xf/b/*` are found, concatenate their entries by `i`,
   and check the Merkle root over the hashes against `root`. A mismatch is a refusal before the
   first chunk is read; this is cheap, and the root is authenticated by the transaction hash.
4. **Read the blocks.** For each block, page `account_tx` forwards from its `l` until every
   index of the block is present or the range's `ledgers[1]` is passed. Hash the assembled
   block and compare with its entry. A mismatch is reported with the block index — enough for
   anyone to re-check that single block.
5. **Assemble.** Chunks in index order, the last one cut to `size`. The result carries `type`
   and `name` from the manifest.

The v1 reader returns the whole file. Because verification is per block, the later streaming
reader replaces "assemble everything" with "release each block as it verifies" and the format
does not change.

### Nodes

Reading needs a node with **full history**. `s1.ripple.com` does not keep it; `xrplcluster.com`
and `s2.ripple.com` do. When `tx` returns `txnNotFound`, the reader checks
`server_info.complete_ledgers`; if the requested ledger is outside the node's range the error
says the node lacks history and suggests another, rather than claiming the file does not exist.

This is the one dependency the design names honestly: the bytes are as permanent as the ledger,
but reading them depends on full-history nodes. They are few, public, and run by more than one
operator — which is still a stronger guarantee than a single IPFS pin.

### Caching

Cache key is `root`, not the link. Two manifests with the same root describe the same bytes.
The gateway caches assembled files by root; the browser library caches nothing.

## 5. Writing

The writer does exactly what the reader will later verify, in that order.

1. **Prepare.** Cut the file into 1000-byte chunks, build the tree, learn `root`, chunk count
   and block count. Detect the MIME type from content (magic bytes), overridable by flag.
2. **Estimate.** Transactions × current open-ledger fee, capped by `--max-fee`, plus the
   account reserve, plus ticket reserve if tickets are used. Print before sending. Writing to
   mainnet requires `--yes`. Refuse if the balance would not survive the worst case.
3. **Choose the submission strategy by balance.** Tickets in batches of up to 250 (each holds
   0.2 XRP of reserve until spent) when the account can carry them: about 100 tx/s measured.
   Otherwise sequential by `Sequence`, awaiting each: about 3 tx/s, so 1 MB takes five minutes.
   For NFT-scale files — a few KB of metadata and a few MB of media — sequential is acceptable.
4. **Chunks**, then **block lists** (only when `blocks` > 1; `l` comes from each block's first
   validated chunk), then **read everything back** through the same reader and compare with
   `root`, then the **manifest**. The manifest is written only after a successful read-back:
   an upload that broke half-way leaves junk in an account's history, never a dead link.
5. **Report.** The link, and a run dataset (transaction hashes, ledgers, fees, timings) in the
   shape war-and-peace produces.

**Resume.** A state file records `root`, the strategy and the index → transaction hash map.
`--resume` refuses if the file changed (different root). Missing indices are sent; re-sending
an index with identical bytes is safe under the chunk rule.

**NFT in one command.** Media → its link → XLS-24 JSON containing that link → its link →
`NFTokenMint` with that link as `URI`. The minting account need not be the writer; a link is not
bound to an issuer.

## 6. Components

```
xrpl-file/
├── docs/spec/xrpl-file-v1.md        # the normative format document
├── docs/superpowers/specs/          # design documents, this one first
├── src/XrplFile.Core/               # .NET: Link, Manifest, MerkleTree, Reader, Writer
├── src/XrplFile.Cli/                # dotnet tool: publish, resolve, verify, mint-nft
├── src/XrplFile.Gateway/            # ASP.NET Core minimal API, Dockerfile
├── web/                             # ESM library, <xrpl-file> element, demo page (GitHub Pages)
├── tests/XrplFile.Core.Tests/       # xunit.v3, built-in Assert
├── tests/vectors/                   # shared test vectors for C# and JS
└── web/test/                        # node --test
```

| Component | Responsible for | Depends on |
|---|---|---|
| `XrplFile.Core` | link parse/format, manifest and list codec, Merkle tree, reader, writer | the `Xrpl` NuGet package and nothing else |
| `web/xrpl-file.js` | the same reading in dependency-free ESM, SHA-256 through WebCrypto; the `<xrpl-file>` custom element | a public node over WebSocket |
| CLI | flags, cost estimate, state file, run report, the NFT scenario | Core |
| Gateway | HTTP over the reader, cache by root, limits | Core |
| Demo page | a link or an `NFTokenID` as input, rendered through the same element | `web/xrpl-file.js` |

### The embeddable element

Two lines that paste into any page:

```html
<script type="module" src="https://staticbit-io.github.io/xrpl-file/xrpl-file.js"></script>
<xrpl-file href="xrpl-file://mainnet/ABCD…"></xrpl-file>
```

or `<xrpl-file nft="000800…"></xrpl-file>`. Attributes: `node` (own endpoint), `max-size`.

Rendering follows the manifest's `type`: `image/*` → `<img>`; `video/*` and `audio/*` → a
media element over a blob (whole file, v1); `text/*` → `<pre>`; `application/json` with XLS-24
fields → an NFT card (name, description, attributes, and a nested `<xrpl-file>` for `image`);
anything else → size and a save button. While loading, the element shows chunks fetched out of
total and the verification state. On a mismatch it shows the error with the block index and
renders nothing.

`NFTokenID` → `URI` uses `nft_info`, available on Clio (`s1`/`s2.ripple.com`,
`xrplcluster.com`). On a node without it the element says so and suggests another. If the NFT's
`URI` is not an `xrpl-file://` link the element says it is a foreign scheme and does not try
to resolve it.

### Gateway

| Route | Returns |
|---|---|
| `GET /{network}/{txhash}` | the bytes; `Content-Type` from the manifest, `Content-Disposition` from `name`, `ETag` = root, `Cache-Control: public, max-age=31536000, immutable` |
| `GET /{network}/{txhash}/manifest` | the manifest JSON |
| `GET /nft/{network}/{nftokenId}` | the NFT's metadata, when its `URI` is an `xrpl-file://` link |

On-disk cache keyed by root, a size limit, per-IP rate limiting, a pool of full-history nodes.
A file that fails verification is a 502 with the block index, never a partial body. A node
without history is a 503 with `Retry-After`, distinct from a 404 for a hash no node knows.

## 7. Limits the format fixes

| | |
|---|---|
| chunk payload | 1000 bytes |
| manifest | one memo, ≤ 1019 bytes with framing |
| block | 1024 chunks |
| block list transaction | about ten entries |
| file size | unbounded by the format (12-digit index); v1 reader default ceiling 20 MB, configurable |
| link | 84 bytes canonical, 168 hex in an NFT `URI` |
| cost | 10–12 drops per transaction regardless of size: about 0.012 XRP per MB |

## 8. Testing

- **Shared test vectors** are the main defence against C# and JS drifting apart: a
  deterministic pseudo-random file of about 2.5 MB (three blocks, the last partial), with its
  chunk hashes, block hashes, root, manifest JSON, list JSON and serialized memos fixed in
  `tests/vectors/`. Both test suites assert against the same files.
- **Reader** against recorded `tx` / `account_tx` responses: happy path; duplicate index; a
  corrupt chunk reported with its block index; a manifest with inconsistent counts; a node
  without history.
- **Tree**: odd counts on every level; one chunk; exactly 1024; 1025; a node presented as a
  leaf is rejected by the domain prefixes.
- **Integration**: standalone rippled in Docker (the compose setup from XrplCSharp) — publish,
  resolve, byte-for-byte match; resume after an interrupted run.
- **Gateway**: `WebApplicationFactory` with a substituted reader; headers, limits, 404 versus
  503.

## 9. Build order

Four independent specs and plans, in this order. Each later step consumes the earlier ones as
finished artefacts.

1. The normative format document, `XrplFile.Core`, `web/xrpl-file.js`, shared vectors.
2. The CLI; the first real publication on testnet, then mainnet — the demo starts from it.
3. The demo page and the `<xrpl-file>` element.
4. The gateway.

## Relationship to the XRPL video platform

The platform's `Platform.Ledger` already implements the storage half of this design, and this
design deliberately copies it where it can, so the two stay byte-compatible in what matters:

| | Video platform | xrpl-file | Compatible |
|---|---|---|---|
| Merkle tree | `0x00`/`0x01` prefixes, promote unpaired, blocks of 1024 | identical | yes |
| Block list payload | JSON `[{"i","h","l"}]`, greedy packing | identical | yes |
| Chunk marker | `vp/c/<i>` | `xf/c/<i>` | no |
| Block list marker | `vp/t/<j>` | `xf/b/<j>` | no |
| Chunk payload | 999 bytes | 1000 bytes | no |
| Identity and location | root inside a signed `vp/claim` to the platform account; chunk range known from the claim and report | `xf/m` manifest on the writer's account, addressed by its transaction hash | no |
| Chunk rule | successful transaction from the uploader with no `Destination`, carrying `MemoData`; a missing marker means a legacy upload | successful transaction from the writer within the manifest's ledger range with an `xf/c/<i>` marker | no |

A video published by the platform today therefore has no `xrpl-file://` link, and a file
published by xrpl-file is not a video the platform's verifier recognises.

The platform should adopt xrpl-file for storage, not the other way round. The reasons: the
platform has no release and no published video on the new tree format, so there is nothing to
migrate; a video that is also an `xrpl-file://` link can be embedded anywhere with the
`<xrpl-file>` element and served by the gateway; and making the xrpl-file reader accept `vp/*`
markers would tie a general format to one application's conventions.

What changes on the platform side, all in `Platform.Ledger` and the browser uploader:

- markers `vp/c/` → `xf/c/`, `vp/t/` → `xf/b/`; chunk size 999 → 1000 (after the boundary test
  above passes on a real node);
- the uploader writes an `xf/m` manifest after the read-back, and the `vp/report` carries the
  manifest's transaction hash so the catalogue can print the link;
- the verifier and the player apply the xrpl-file chunk rule. Legacy uploads without a marker
  — the mainnet demo clip — stay readable only through the old path, and the platform decides
  whether to keep that path or re-publish the clip;
- `vp/claim`, `vp/report`, comments, tips and the rest are platform events, not storage, and
  are untouched. The claim keeps signing the root; the root is the same number in both formats.

Once step 1 of the build order ships, `Platform.Ledger` can take `XrplFile.Core` as a package
and delete its own `MerkleTree`, `VideoTree` and marker code. Until then the platform can
switch markers and chunk size on its own; the tree and list code need no change.

## Rejected alternatives

- **Content-addressed link `xrpl-file://<account>/<root>`.** Same file, same link, as with an
  IPFS CID — but finding the manifest means scanning the account's history page by page, and
  only "one account per file" makes that fast. That imposes a storage model the link should
  not care about. The root is still inside the manifest, so caching and deduplication by
  content work after one `tx` call.
- **A mutable pointer through a DID object.** `DID.URI` (256 bytes) could hold the current
  manifest hash and be rewritten with `DIDSet`. It gives updatable metadata, costs an owner
  reserve, and defeats the purpose: an NFT whose bytes can be swapped is no better than a
  lapsed pin. Kept as a possible extension on top of v1, not as its basis.
- **CTI (XLS-16d's Concise Transaction Identifier).** 48 bits instead of 256, but it locates a
  transaction without authenticating its content and needs `ledger` with transactions instead
  of `tx`. The NFT `URI` has room for a full hash.
- **Order by `Sequence` without a manifest** (war-and-peace). Incompatible with tickets, no
  MIME type, no verification until the whole file is down, and the index has to live outside
  the ledger.

## Prior art

- XLS-16d NFT Metadata: metadata in the memos of one transaction addressed by a CTI. Predates
  XLS-20 and has no chunking; its memo-type table (`NFT/0`…`NFT/4`) is the closest earlier
  attempt at conventions for ledger-resident metadata.
- XLS-48d Document Storage: a 256-byte ledger object per owner reserve. Retired in October
  2023 for lack of interest.
- XLS-24d: the metadata JSON structure this project's NFT scenario produces.
- war-and-peace-on-xrpl and the XRPL video platform (this organisation): the storage
  experiments whose measurements and Merkle scheme this design builds on.

## Future extensions (not v1)

- Streaming reader over MediaSource for long video, reusing the block list's `l` for seeking.
- A DID-backed mutable pointer for content that is meant to change.
- Optional compression flagged in the manifest.
