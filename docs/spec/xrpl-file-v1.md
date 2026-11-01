# xrpl-file, version 1

A link format for files stored inside the XRP Ledger, and the layout that makes such a file
readable and verifiable from the link alone.

This document is normative for version 1. The words MUST, MUST NOT, SHOULD and MAY are used as
in RFC 2119. The reference implementations are `XrplFile.Core` (.NET) and the `web/` library
(browser) in this repository; `tests/vectors/v1.json` holds test vectors both are held to.

## 1. Link

```
xrpl-file://<network>/<tx-hash>
```

- `network` MUST be one of `mainnet`, `testnet`, `devnet`, or the decimal `NetworkID` of another
  XRPL-protocol network without a leading zero. Names are case-insensitive; the canonical form is
  lower case.
- `tx-hash` MUST be the 64-character hexadecimal hash of the manifest transaction (section 4).
  It is case-insensitive; the canonical form is upper case.
- A link MUST NOT carry a query string, a fragment, or anything after the hash.

The canonical link is 84 bytes (`xrpl-file://` is 12, `mainnet` is 7, the slash is 1, the hash is
64). Hex-encoded into an NFT `URI` it uses 168 of the 256 bytes rippled allows.

## 2. Transactions

A file is written by one account, the *writer*, as a series of transactions each carrying one
memo. The writer SHOULD use `AccountSet` with no fields other than the memo; a reader MUST NOT
depend on the transaction type.

| Kind | `MemoType` (decoded) | `MemoData` | `MemoFormat` (decoded) | Data budget |
|---|---|---|---|---|
| chunk | `xf/c/<i>` | raw file bytes | absent | 1000 bytes |
| block list | `xf/b/<j>` | JSON array of block entries | `application/json` | 982 bytes |
| manifest | `xf/m` | JSON object | `application/json` | 995 bytes |

`<i>` and `<j>` are decimal indices from 0 with no leading zeros and at most 12 digits. A
marker that does not match this exactly is not a marker.

The budgets follow from rippled's limit of 1024 bytes on the serialized `Memos` array: a memo
carrying only `MemoData` costs 5 bytes of framing, leaving 1019; a `MemoType` or `MemoFormat`
costs 2 bytes of framing plus its own length. A chunk marker is at most 17 bytes (`xf/c/` or
`xf/b/` plus 12 digits), which is where the 1000-byte chunk budget comes from
(`1019 − 2 − 17 = 1000`); a block-list memo additionally carries a `MemoFormat` of
`application/json` (16 bytes, 2 bytes of framing), which is where 982 comes from
(`1000 − 2 − 16 = 982`); a manifest's `MemoType` is the fixed 4-byte `xf/m`, which is where 995
comes from (`1019 − 2 − 4 − 2 − 16 = 995`).

The writer MUST write chunks first, then block lists, then the manifest last.

## 3. Chunks and the tree

The file is cut into chunks of `chunk` bytes (section 4), the last one shorter. Version 1
writers MUST use `chunk` = 1000; readers MUST accept any `chunk` from 1 to 1000. The chunk count
is `ceil(size / chunk)`.

Hashing uses SHA-256 with domain separation:

- leaf = `SHA256(0x00 || chunk bytes)`, over exactly the bytes in `MemoData`;
- node = `SHA256(0x01 || left || right)`;
- the root over a list of hashes pairs them left to right; an unpaired element at any level
  is promoted to the next level unchanged. It is never duplicated.

Chunks group into **blocks** of 1024 by index: block `b` holds chunks `1024·b` to
`1024·b + 1023`. A block's hash is the root over its chunks' leaves in index order. The file's
`root` is the root over the block hashes in block order. A file of one block has `root` equal
to its block hash.

## 4. Manifest

`MemoData` of the manifest transaction is a JSON object, its fields in this order:

```json
{"format":"https://github.com/StaticBit-io/xrpl-file","v":1,
 "type":"image/png","size":734512,"chunk":1000,
 "root":"<64 hex>","ledgers":[106497159,106497394],
 "blocks":1,"lists":0,"cover":"<64 hex>","name":"cover.png"}
```

| Field | Required | Meaning |
|---|---|---|
| `format` | no | URL of the repository holding this specification; for people reading the ledger, not for readers |
| `v` | yes | format version, `1` |
| `type` | yes | MIME type of the file |
| `size` | yes | file length in bytes, ≥ 1 (a zero-length file has no chunks and no root, so `blocks` = 0 would force a `lists` ≠ 0 whose list is empty, and an empty list is refused) |
| `chunk` | yes | chunk payload size the writer used, 1..1000 |
| `root` | yes | the file root, 64 hex characters |
| `ledgers` | yes | `[first, last]`: the range of validated ledgers holding every chunk and every block list, `0 < first ≤ last` |
| `blocks` | yes | `ceil(chunkCount / 1024)` |
| `lists` | yes | number of block-list transactions; `0` if and only if `blocks` = 1 |
| `cover` | no | hash of the manifest transaction of another xrpl-file file: a picture that stands for this one, 64 hex characters (section 4.1) |
| `name` | no | file name |

The manifest MUST be written in a ledger after `last`. Writers SHOULD emit the fields in the
order `format`, `v`, `type`, `size`, `chunk`, `root`, `ledgers`, `blocks`, `lists`, `cover`,
`name` without whitespace, and SHOULD emit `format` as exactly the string
`https://github.com/StaticBit-io/xrpl-file`, first. Writers SHOULD shorten `name` by whole
characters (never splitting a UTF-16 surrogate pair) until the JSON fits 995 bytes, dropping it if
nothing fits. The name is shortened after `format` and `cover` are placed, and neither is ever
dropped to make room for the name. `format` takes 53 bytes (`"format":` is 9 bytes, the quoted
41-character URL 43, and the comma after it 1); `cover` takes 75 bytes (`,"cover":"` plus 64 hex
characters plus the closing quote). The 995-byte budget is unchanged. A `type` long enough that the
manifest does not fit even without a name is a writer error, not something a reader ever sees.

`format` is a repository URL and not the address of a site because GitHub keeps redirecting a
repository's URL after a rename or a transfer, so a manifest written today still leads a person to
the specification.

A reader MUST ignore manifest members it does not recognise. A writer MUST NOT emit members other
than those this specification defines; `format` is one of them. A reader MUST NOT require `format`
and MUST NOT check its value: any value of any JSON type, or its absence, reads the same, and none
of them is a `CorruptManifest`. A manifest written before `format` existed has none and stays fully
valid; the version stays `1`. A `cover` that is not exactly 64 hexadecimal characters (or not a
JSON string at all) reads as absent; it is not a `CorruptManifest`.

A reader MUST reject a manifest:

- whose `v`, `type`, `size`, `chunk`, `root`, `blocks` or `lists` field is missing or the wrong
  JSON type;
- whose `root` is not exactly 64 hexadecimal characters;
- whose `ledgers` is not a two-element array of numbers with `first > 0` and `last ≥ first`;
- whose `chunk` is not in `1..1000`;
- whose `size` is past `chunk` × 2147483647, the most bytes a chunk index can name;
- whose `blocks` does not equal `ceil(ceil(size / chunk) / 1024)`;
- whose `lists` is negative, or is zero exactly when `blocks` ≠ 1 (equivalently: nonzero exactly
  when `blocks` = 1);
- whose `ledgers[1]` (`last`) is not strictly less than the ledger the manifest transaction
  itself was validated in.

Any of these is `CorruptManifest` (section 8).

### 4.1 Cover

`cover` names another xrpl-file file, its *cover*: a small picture that stands for the file the
manifest describes. It is optional. The version stays `1`: a reader that predates the field
ignores it (section 4).

A writer that sets `cover` MUST first publish the cover as a complete xrpl-file file:

- the `Account` of the cover's manifest transaction is the `Account` of the decorated file's
  manifest transaction (the same writer);
- the ledger in which the cover's manifest transaction was validated is strictly below
  `ledgers[0]` of the decorated file's manifest, so the cover's chunks never fall inside the
  decorated file's chunk range;
- the cover's manifest has `type` `image/webp` or `image/jpeg`, `size` at most 65,536 bytes, and
  therefore one block and `lists` = 0.

The hash in `cover` names a manifest transaction on the same network as the decorated file; a
reader looks it up there. A reader accepts the 64 hexadecimal digits in either case; a writer
writes them in upper case, the canonical form of a transaction hash (section 1).

A reader that wants the cover fetches the manifest transaction named by `cover` and reads it as an
ordinary file (section 6), plus these checks:

- the `Account` of the cover's manifest transaction equals the `Account` of the decorated file's
  manifest transaction;
- the ledger in which the cover's manifest transaction was validated is strictly below
  `ledgers[0]` of the decorated file's manifest;
- `type`, `size` and `blocks` of the cover's manifest are within the limits above (`lists` = 0
  follows from one block);
- the bytes start with the magic of the declared type: `RIFF....WEBP` for WebP (`RIFF`, any four
  bytes, `WEBP`), `FF D8` for JPEG.

A cover that fails any check, or cannot be read, is **ignored**: the reader carries on as if
`cover` were absent. It is never an error of the decorated file. A reader MUST NOT follow a
cover's own `cover` field.

The format fixes no dimensions for a cover.

## 5. Block list

When `blocks` > 1 the writer MUST publish the block list: entries
`{"i":<block>,"h":"<block hash, 64 hex>","l":<earliest ledger holding any of the block's chunks>}`
packed greedily into JSON arrays of at most 982 bytes, one array per `xf/b/<j>` transaction,
entries in block order across the transactions. When `blocks` = 1 there is no list and a reader
uses the single entry `{i: 0, h: root, l: first}`.

`l` is the **earliest** of the block's chunks' ledgers, not the ledger of its lowest-numbered
chunk. The two agree only when one run sent every chunk in order. A writer resuming an
interrupted publication re-sends the chunks that are missing, so a low index can land after the
ones around it; a reader pages a block forward from `l` (section 6), so `l` taken from the first
chunk would leave that block's earliest chunk below the range the reader searches, and the file
would not read back. The manifest's `first` follows the same rule for the same reason: it is the
earliest ledger any chunk is in.

A block-list transaction whose marker index `j` is not below `lists` is not part of the list —
the same way a chunk marker outside its range is not a chunk. A reader MUST refuse
(`ListMismatch`) unless it finds exactly one usable transaction for every `j` in `0..lists-1`;
a stray transaction re-using an in-range marker for something else, or a marker numbered `lists`
or above, must not be mistaken for the missing entry, and must not surface as anything but
`ListMismatch` either.

Having collected the `lists` payloads, a reader MUST reject the decoded list:

- if any entry is not a JSON object carrying all three of an integer `i`, a 64-hex-character `h`
  and an integer `l`. A member that is absent, null or of another type makes the list unreadable;
  it is never read as a zero. An entry read as `{i: 0, l: 0}` would send a reader paging block 0
  from the first ledger the network ever had;
- if it is not indexed exactly `0, 1, ..., N-1` for the `N` entries it contains (a gap, a
  duplicate index, or a hash that is not 64 hex characters make it unreadable);
- if `N` does not equal the manifest's `blocks`;
- if the root computed over the entries' hashes, in index order, does not equal the manifest's
  `root`.

**Duplicate block-list transactions.** When more than one transaction carries the same marker
`j`, a reader uses the **last** copy in canonical ledger order — the opposite of the rule for
chunks (section 6), which uses the first. A reader reaches this by walking the writer's
transactions backwards from `last` and stopping as soon as every index `0..lists-1` has a
value, which is far cheaper than walking a large file's chunks in reverse just to reach the
lists at the end of the range; the first copy such a backward walk meets for an index is the
last one in canonical order.

The asymmetry is what makes a resumed publication readable. A conforming writer never resubmits
a *chunk* index with different bytes — a chunk's bytes are fixed by the file — so for chunks it
does not matter which copy a reader picks. A block list is different: its entries carry the
per-block ledgers, and a resume that lands chunks in new ledgers MUST rewrite them, so the same
`j` is legitimately resubmitted with different bytes (section 7). Taking the last copy is what
makes the rewrite the one a reader sees; taking the first would hand it the interrupted run's
window. A stray or malformed resubmission is still caught either way — by `DecodeEntries` or by
the root check — because the entries a reader accepts must hash to the manifest's `root`.

## 6. Reading

Given a link:

1. Fetch the transaction named by the link's hash.
   - If the node does not know it: ask for the node's earliest ledger. If that is unknown or
     later than the network's first ledger (32570 on mainnet), the node does not have full
     history and the correct error is `NodeLacksHistory`; otherwise the transaction genuinely
     does not exist and the error is `ManifestNotFound`. A version 1 reader applies the mainnet
     value on every network — it does not know each network's own first ledger — so this split
     is only accurate on mainnet. Elsewhere a full-history node whose ledgers start above 32570
     is reported as lacking history, which is the safe way round: it names a doubt rather than
     asserting a transaction does not exist.
   - If it is not validated, or its result is not `tesSUCCESS`, or its first memo does not
     decode to `xf/m` with `MemoData` present: `ManifestNotFound`.
   - The *writer* is the transaction's `Account`.
2. Decode `MemoData` as the manifest (section 4) and check it: a JSON or shape failure, an
   internal inconsistency, or a `last` ledger not before the manifest transaction's own ledger
   is `CorruptManifest`.
3. Reject a manifest whose `size` exceeds the reader's configured limit before fetching any
   chunk (`TooLarge`). This is a resource guard on the reader, not a limit the format itself
   places on file size (section 9); the reference readers default it to 20,000,000 bytes.
4. Confirm the node's history reaches back to `first`: if its earliest ledger is later,
   `NodeLacksHistory`.
5. Obtain the block list (section 5).
6. For each block, read the writer's transactions from the block's `l` up to `last` and collect
   its chunks by the chunk rule below. Hash the block and compare with its entry.
7. Concatenate the blocks. The assembled length MUST equal `size` exactly.

**Length rule.** The chunk rule below accepts a `MemoData` *shorter* than `chunk`, because the
file's last chunk is short. Every other chunk is exactly `chunk` bytes, so a block's assembled
length is fixed by the manifest alone: `min(size − 1024·b·chunk, 1024·chunk)` for block `b`. A
block that assembles to any other length is `BlockMismatch`, reported with the block index, and
a reader MUST NOT deliver it — even when its hash matches its entry. It can: a writer that cut
its file into short chunks and hashed them honestly produces a block whose hash is right and
whose bytes are fewer than the manifest promises, and padding that block out to `size` would
hand a caller bytes no hash covers. A conforming writer always produces the exact length.

**Chunk rule.** A transaction is chunk `i` when all of the following hold: its `Account` is
the writer; it is validated with result `tesSUCCESS`; its ledger is within `[first, last]`;
its first memo's `MemoType` decodes to `xf/c/<i>`; `MemoData` is present; and `MemoData` decodes
from hex to no more than `chunk` bytes. A transaction of the writer that does not match — including
one whose `MemoData` decodes to more bytes than `chunk`, which is treated exactly like `MemoData`
that is not valid hex — is not a chunk and not an error. If several transactions qualify for
the same `i`, the **earliest** in canonical ledger order is the chunk (contrast the block list's
rule, section 5, which keeps the latest).

A reader MUST NOT deliver bytes of a block whose hash does not match its entry, and SHOULD
report the block index (`BlockMismatch`). A block short of its full chunk count by the time the
range `[l, last]` is exhausted is `ChunkMissing`, also reported with the block index.

Step 6 describes what each block's read sees, not how often the node is asked. A block usually
starts in the ledger the block before it ended in, so a reader MAY keep the writer's transactions
from one block's read and give the next block those from its `l` on, followed by the rest of the
same forward read. The validated ledgers in `[first, last]` do not change, so the next block sees
exactly the transactions, in exactly the canonical order, that a read of its own from `l` would
return. The reference readers do this within one read, holding at most 4,096 transactions.

### Nodes

Reading needs a node that holds the ledgers in `[first, last]` and the manifest's ledger.
Public nodes with full history exist on mainnet (`xrplcluster.com`, `s2.ripple.com`); not every
node keeps full history — `s1.ripple.com`, for one, does not. When a node does not know the
manifest's hash, a reader SHOULD consult `server_info.complete_ledgers`: a node whose history
does not start at the network's first ledger (32570 on mainnet) may simply not hold it. Even
when the manifest transaction itself is found, a reader still MUST check that the node's
earliest ledger reaches back to the file's first chunk before trusting a "chunk missing" result:
a node can hold a recent manifest while lacking history for the (possibly much older) ledgers
its chunks live in.

## 7. Writing

A writer MUST submit chunks with their markers, then block lists (when `blocks` > 1), then the
manifest, in that order, and MUST NOT write the manifest until every earlier transaction is
validated. It MUST NOT resubmit a chunk index with different bytes. It SHOULD read the file back
by section 6 before writing the manifest.

A block-list index is not under that rule: a writer resuming an interrupted publication MUST
rewrite any `xf/b/<j>` whose entries' ledgers have changed, because those ledgers are what a
reader pages each block from. The resubmission is safe because a reader takes the last copy of a
list index in canonical order (section 5), so the rewritten payload is the one it reads.

The reference writer sets no `LastLedgerSequence` on any of its transactions. It numbers them
with consecutive `Sequence` values, and one expired transaction in such a run strands every
later one as `terPRE_SEQ` — a whole upload lost to a single ledger's delay, where without an
expiry the delayed transaction simply lands late. A writer that does set one MUST be prepared to
resubmit the gap it leaves.

A writer SHOULD NOT place more than a few hundred of its own transactions into a single open
ledger. Measured against `xrpld` 3.3.0 in standalone mode, a node starts refusing a writer's
account's transactions with `telCAN_NOT_QUEUE_FULL` somewhere past about 1010 outstanding,
unvalidated transactions; the reference writer stays well clear of that by waiting for every
earlier transaction to validate after each batch of 200 submissions before continuing.

## 8. Errors

The two reference implementations raise eleven named errors between them — the .NET library as
an `XrplFileError` enum value on an `XrplFileException`, the browser library as the same names in
string form on an `XrplFileError`. Nine of the eleven — every one but `SubmitRefused` and
`ValidationTimeout` — are raised while **reading**: resolving a link, decoding a manifest, and
verifying a file's blocks against it. `SubmitRefused` and `ValidationTimeout` are raised only
while **writing**, inside `XrplFileWriter`'s submission and validation-waiting loop (section 7).
The browser library has no writer: `web/xrpl-file.js` exports only the reading side
(`XrplFileReader` and its supporting modules), never a writer, so it can never raise those two.
An implementation that only reads, as the browser library does, therefore raises the nine
reading errors and never the two writing ones. The error is part of the format's contract: a
caller depends on being able to switch on it.

| Error | Raised when |
|---|---|
| `InvalidLink` | A link fails to parse: wrong scheme, an unrecognized network, or a transaction hash that is not 64 hex characters (section 1). A writer raises it for a network it was asked to publish to that no link could name — before it submits anything, because the link the caller is owed is built from that name at the end. |
| `ManifestNotFound` | The named transaction does not exist (on a node that has full history), is not a validated `tesSUCCESS` transaction, or does not carry an `xf/m` memo (section 6). |
| `NodeLacksHistory` | The connected node's earliest ledger is later than the network's first ledger while the manifest transaction is missing, or later than the manifest's `first` while resolving chunks (section 6). A version 1 reader uses the mainnet first ledger (32570) on every network, so this error and `ManifestNotFound` are told apart accurately only on mainnet. |
| `CorruptManifest` | The manifest transaction's `MemoData` does not parse as a manifest, fails its internal consistency checks, or names a ledger range that does not end before the manifest's own ledger (section 4). |
| `ListMismatch` | The block list is missing an index, is unreadable, is not indexed `0..blocks-1`, or its root does not match the manifest's `root` (section 5). |
| `BlockMismatch` | A block's assembled chunks (or, for a single block, the file itself) hash to something other than the entry the block list — or the manifest — promises, or assemble to a length the manifest does not imply (section 6). |
| `ChunkMissing` | A block's ledger range was exhausted before every one of its chunks was found. |
| `TooLarge` | The manifest's `size` exceeds the reader's configured limit. |
| `SubmitRefused` | A node answered a writer's transaction with an engine result other than `tesSUCCESS` or `terQUEUED`, immediately on submission; or a transaction accepted as `terQUEUED` was later found validated with a non-`tesSUCCESS` result — a `tec*` code reached only after the ledger closes (section 7). It also carries a node's own error answer to the requests a writer makes to submit and to follow its account's sequence — `actNotFound` for an unfunded wallet, say: the node answered, so the failure is a refusal that names what it said. The reads a publication makes are not among these; see `NodeUnreachable`. |
| `ValidationTimeout` | The account's validated sequence did not reach the value expected after a batch of submissions within the configured timeout; or, right after that wait succeeds, the specific transaction the writer then looks up for its ledger comes back missing or unvalidated — which surfaces at once, not after a timeout of its own (section 7). |
| `NodeUnreachable` | The node did not answer usably: a connection that was never made or was lost, a request that timed out, or an error answer to a **read** — `tx`, `account_tx` or `server_info` — whose code the message names. `txnNotFound` is not one of these; it is the node saying the transaction is not there (section 6). The read rule holds wherever the read happens, including the reads a publication itself makes to learn which ledger a transaction landed in and to check its own work. Only where the node answers a **submission** does its error become `SubmitRefused`. |

## 9. Limits

| | |
|---|---|
| chunk payload | 1000 bytes |
| block | 1024 chunks |
| block list transaction | ≤ 982 bytes of JSON |
| manifest | ≤ 995 bytes of JSON |
| index | ≤ 12 decimal digits |
| file size | unbounded by the format |

## 10. Test vectors

`tests/vectors/v1.json` gives, for each of several file sizes, a seed and the derived hashes,
block list and manifest. The vector file is generated by `tools/XrplFile.Vectors` from
`XrplFile.Core` and asserted by both the .NET and the browser test suites, so the two
implementations are held to the same known answers. The file bytes are regenerated, not stored:
block `k` of the stream is `SHA256(UTF8(seed) || k as a big-endian uint32)`, blocks concatenated
and cut to `size`. The vectors also fix the Merkle known answers shared with the XRPL video
platform. `manifestVariants` give manifests that must decode to the same file as the vector they
name (`of` is that vector's `size`; each also names the `name` it decodes to, which differs from
the vector's only in the last variant): one with `cover`, one with a member this version does not
define, one without `format` as written before the member existed, two whose `format` is a JSON
number and an object, and one with `format`, `cover` and a long name that the writer shortened to
fill the 995-byte budget exactly. The manifests of the file vectors begin with `format`.

## 11. NFT usage

An NFT's `URI` MAY be an xrpl-file link to an XLS-24 metadata JSON whose `image` or
`animation_url` is itself an xrpl-file link. Readers resolve each hop by section 6. The
metadata MAY additionally carry `image_gateway` with an HTTPS URL for clients that only
understand HTTP; a reader never needs it.
