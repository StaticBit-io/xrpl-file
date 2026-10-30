# `<xrpl-file>` Element Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One script and one tag show any `xrpl-file://` link inside a web page — images, text, video that streams and seeks, a download view for everything else — and the format gains an optional `cover` that both reference readers parse.

**Architecture:** The format change lands first (spec text, .NET `Manifest.Cover`, web `decodeManifest` and `readCover`, shared vectors), then the .NET writer learns to emit a cover so the testnet fixtures can be published in the background while the browser work proceeds. The element lives in `web/src/element/` as plain ES modules over the existing reader: a page-level layer shares one `LedgerClient` per node set, one read per link and a priority queue of block reads; pure modules decide views, parse attributes, cut text, name downloads and parse containers (ISO BMFF, WebM) and are tested under `node --test`; browser-only modules (the custom element, Media Source Extensions) are checked by hand against testnet. A Node-only bundler writes one classic script per version to `web/dist/<version>/` beside mp4box 0.5.4 and an SRI file.

**Tech Stack:** .NET 10, C# latest, `Xrpl` 11.2.0 (pinned), System.Text.Json, xUnit v3 (`dotnet run` runner); browser JavaScript as dependency-free ES modules, Node 22+ `node:test`, mp4box.js 0.5.4 (vendored), ffmpeg/ffprobe on PATH for media; testnet for fixtures and acceptance.

**Spec:** `docs/superpowers/specs/2026-10-29-xrpl-file-element-design.md` with its approved mockup `docs/superpowers/specs/2026-10-29-xrpl-file-element-mockup.html` (caption variant C). Format: `docs/spec/xrpl-file-v1.md`. Gaps in the spec and how this plan rules on them are under **Spec questions** below; the plan follows the rulings.

## Global Constraints

- Repository `E:\GIT\XRPL\xrpl-file`; every path below is relative to it. Branch as the executing session decides.
- `Directory.Build.props` sets `TreatWarningsAsErrors` and `GenerateDocumentationFile`: every new public .NET member carries an XML doc that resolves.
- .NET style: explicit types, no `var`; `async`/`await` with `CancellationToken` passed through; `ConfigureAwait(false)` in `src/`; comments explain why, English only.
- .NET tests: `dotnet run --project tests/<Project> -- -class <FQN>` or `-method <FQN>`; the unit run is `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration" -trait- "Category=Slow"`. `dotnet test` does not work here. Test namespace is `XrplFile.Tests`.
- Web: `web/package.json` stays free of dependencies; no npm packages. Tests are flat files `web/test/<name>.test.mjs` (the glob `test/*.test.mjs` does not recurse) run with `cd web && node --test test/*.test.mjs`.
- Never change file encodings. `.gitattributes` has `* text=auto eol=lf`; binary media added to git gets an explicit `binary` line.
- Commits: conventional commits, English subject and body, no attribution or `Co-Authored-By` lines.
- Testnet only, faucet-funded throwaway accounts, node `wss://s.altnet.rippletest.net:51233`. Never mainnet. No seed is printed, logged or committed.
- Fixture media and their state stay under `run/fixtures/` (already in `.gitignore`); only links reach git (the demo page).
- Element text is English only. File-derived strings (name, type, file content) reach the DOM through `textContent` or attributes only, never `innerHTML`.
- Values verbatim from the spec:
  - manifest field order `v, type, size, chunk, root, ledgers, blocks, lists, cover, name`; `cover` = 64 hex characters, costs 75 bytes, the 995-byte budget is unchanged, name shortening runs after it;
  - a cover: same `Account` as the decorated file, its manifest's ledger strictly below the decorated `ledgers[0]`, `type` `image/webp` or `image/jpeg`, `size` ≤ 65,536, one block, `lists` = 0, magic `RIFF....WEBP` or `FF D8`; a cover's own `cover` is never followed; a failing cover is ignored, never an error;
  - tag `xrpl-file`; attributes `src`, `width`, `height`, `loading` (`lazy` default, `eager`, `click`), `loader` (`true` default, `false`), `node` (`wss://`; `ws://` only for `localhost` and `127.0.0.1`); text content is the link when `src` is absent;
  - CSS custom properties `--xrpl-file-accent`, `--xrpl-file-radius`, `--xrpl-file-background`, `--xrpl-file-foreground`; parts `loader`, `caption`, `message`;
  - events `xrpl-file-progress` (`detail`: `{ bytesDone, bytesTotal, blocksVerified, blocksTotal }`), `load`, `error` (`detail`: `{ name, message }`, `name` from spec section 8);
  - reserved proportion 16:9; lazy start margin one viewport height; video keeps 30 s ahead; up to 3 block reads ahead at once; on `QuotaExceededError` drop ranges further than 120 s from the playhead; in-memory download limit 2 GiB = 2,147,483,648 bytes; mp4box.js 0.5.4; SRI SHA-384.
- Sentences verbatim from the spec: "This network has no built-in node. Set the node attribute."; "The node does not have the history this file needs."; "This file did not pass verification. Block {n} does not match the file's fingerprint. Nothing was shown."
- Mockup values: background `#14161a`, foreground `#e8e8ea`, accent `#4da3ff`, radius `10px`, muted `#9a9ca3`, error title `#ff8a8a`, progress bar 4 px, text view `#f7f7f5` on `#222` in `13px/1.5 ui-monospace, Consolas, monospace`.
- Reused from `E:\GIT\XRPL\xrpl-video-platform` (read-only, same owner): `src/Platform.Web/wwwroot/lib/mp4box-0.5.4.min.js` (vendored), `js/sidx.mjs` (`parseSidx`, `groupAt`, copied), `js/streamer.mjs` (MediaSource/ManagedMediaSource, `segments` mode, the quota drop, adapted), `js/player.mjs` (30 s read-ahead, seek = `abort()` then append from inside the block, generation counter, adapted), `js/mp4.mjs` and `js/fragments.mjs` (box walking, duration, avcC/esds codec strings, adapted), `js/remux.mjs` (`assembleFragmented`, called only by the fixture script), `tests/Platform.Web.Js/helpers/mp4box.mjs` (mp4box in a `vm` sandbox, adapted for tests and the fixture script). Every copied or adapted file says where it came from in its header comment.

## Review Focus

1. **A manifest `name` carrying markup or path characters** (`<img src=x onerror=alert(1)>`, `../../evil.exe`, `con:`, control characters): shown as literal text in the caption and download view, saved under a sanitized name that `showSaveFilePicker` accepts — tests in Task 5 (`downloadName`) and Task 8 (no `innerHTML` from data).
2. **A file declared as a picture that is far too large to hold** (`image/png` of 1.6 GB): the element shows the download view instead of assembling it in memory — test in Task 5 (`viewForType` above 20,000,000 bytes).
3. **One element removed, or moved in the DOM as frameworks do when reordering, while another shows the same link or uses the same node**: the other keeps loading, the shared read is not cancelled, the socket is not closed and reopened — tests in Task 6.
4. **The node drops the WebSocket in the middle of a video, or stays away for minutes**: the read is retried with a capped backoff under the buffering overlay for as long as the element is on the page, and playback continues; only verification failures (`BlockMismatch`, `ChunkMissing`, `ListMismatch`) stop it — test in Task 12 (`retryDelay`).
5. **Rapid scrubbing, several seeks in a second**: only the last seek's blocks reach the source buffer; a read that completes for an older seek is dropped, never appended out of place — test in Task 12 (`ReadAhead` generations).

---

## Spec questions and rulings

1. **"Picture, WebP 178 KB … multi-block image".** 178 KB is one block (a block is 1,024,000 bytes at `chunk` 1000). Ruling: keep the WebP for the image view and add a PNG of the same picture written with `-compression_level 0` (about 7.7 MB, eight blocks) as the multi-block image fixture.
2. **The broken-cover fixture against the writer's MUST.** Spec 1.2 says a writer MUST publish the cover first, by the same account. The fixture "MP4 with a cover of the wrong account" violates that on purpose. Ruling: the .NET writer checks only that the hash is 64 hex characters; the rest is the caller's job (the desktop app will check it when it chooses covers). The fixture tool checks it for every fixture except the deliberately broken one.
3. **Error names the spec does not list.** "No built-in node", an unusable `node` attribute, and a thrown value that is not an `XrplFileError` have no name in section 8. Ruling: the first two raise `NodeUnreachable` with their own sentence; an unexpected exception gives `detail.name` `null` and "Something went wrong while showing this file."
4. **"Above 1 MB only the first 1 MB is shown" next to "images and text download completely".** Ruling: the limit is 1,048,576 bytes, and a text file reads only the blocks that cover it (two blocks for anything over 1,024,000 bytes), all verified.
5. **No size limit for pictures.** Ruling: above 20,000,000 bytes (the reference readers' default limit, format spec section 6 step 3) an image type shows the download view with "This picture is too large to show here."
6. **A dropped connection during video.** The spec says a slow node means pauses, not an error, with no failure threshold, and that a block that fails verification stops reading. Ruling: `NodeUnreachable` on a video block read is retried with backoff 1, 2, 4, 8, 16 s, then every 30 s, for as long as the element is connected, under the buffering overlay; integrity errors are never retried.
7. **"In the style of the existing fiddle bundler".** Neither repository has one. Ruling: `web/build.mjs`, a module-registry bundler of about 150 lines that handles exactly the import/export forms `web/src` uses and fails the build on anything else.
8. **A writer must not emit unknown members, yet the vectors need one.** Ruling: the vector generator inserts the extra member textually into the 1001-byte vector's manifest, never through `Manifest.Encode`.
9. **Positional order of the .NET record.** `Cover` is appended after `Name` in `Manifest(...)` so every existing call keeps compiling; the JSON order (`cover` before `name`) is the serializer's business.
10. **`readCover` placement.** It is a method, `XrplFileReader#readCover(record)`, beside the pure checks in `web/src/cover.mjs` (which imports nothing from `reader.mjs`, so there is no import cycle). It reads through a fresh reader over the same source, so it never disturbs a forward history walk (spec 2.7).
11. **What a video reads before the visitor presses play.** Spec 2.3 says video reads further only while playing. Ruling: before the first `play`, a video reads the blocks its initialization needs and the first media block (enough for the first frame and `loadeddata`), nothing more; the 30 s read-ahead runs only while playing.
12. **Fragmented or plain MP4 when `moov` is larger than block 0.** The `mvex` that tells them apart may sit past block 0. Ruling: `detectContainer` says only `iso`, `webm` or `unknown`; the feed decides fragmented or plain from the whole `moov` once `locateMoov` has read it.
13. **`crypto.subtle` exists only in secure contexts.** A site served over plain `http://` (other than `localhost`) cannot hash. Ruling: the element checks first and shows "This page is not served over HTTPS, so the browser cannot check this file." (`detail.name` null); the README says so.
14. **Cancelling reads without changing the reader.** `XrplFileReader#block` takes no signal and spec 2.7 keeps the reader unchanged. Ruling: cancellable reads (video) run a per-read `XrplFileReader` over an `AbortableSource` that ends its `account_tx` walk between pages once aborted; sequential reads (images, text, downloads) use the page's shared reader per history key, whose `ForwardHistory` is the history cache spec 2.7 asks for. Concurrent walks never shared a `ForwardHistory` anyway.
15. **Seeking in plain MP4 with B-frames.** mp4box's `seek(t, true)` works in composition time (the last sync sample with cts ≤ t, per track the last sample with cts ≤ t) and does not reset segmentation state already running. Ruling: our `seekPoint` follows exactly that rule and is cross-checked against mp4box in Node; the feed makes a fresh `ISOFile` per seek and reads from the offset mp4box returns (verified in `mp4box-0.5.4.min.js`: `seekTrack`, `ISOFile.prototype.seek`, `processIncompleteBox`).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `docs/spec/xrpl-file-v1.md` | unknown-member rule, `cover` field, section 4.1 Cover, section 10 variants | 1 |
| `src/XrplFile.Core/Manifest.cs` | `Cover` parse/encode | 1 |
| `tools/XrplFile.Vectors/Program.cs`, `tests/vectors/v1.json` | `manifestVariants` | 1 |
| `tests/XrplFile.Core.Tests/{ManifestTests,VectorTests,Vectors}.cs` | cover, unknown members, variants | 1 |
| `web/src/manifest.mjs`, `web/src/cover.mjs` (new), `web/src/reader.mjs`, `web/xrpl-file.js` | `cover` in the web reader, `readCover` | 2 |
| `web/test/{manifest,vectors,cover}.test.mjs`, `web/test/vectors.mjs` | tests, `smallFileLedger`, call counters | 2 |
| `src/XrplFile.Core/{PublicationSession,PublicationState,XrplFileWriter,ManifestPlan}.cs` | the writer emits a cover | 3 |
| `tools/XrplFile.Fixtures/` (new), `tools/fixtures/` (new), `web/vendor/` (new) | testnet fixtures, media script, vendored mp4box | 4 |
| `web/src/element/{attributes,view,text,format,messages}.mjs` (new) | pure decisions | 5 |
| `web/src/element/page.mjs` (new) | `NodePool`, `SharedSource`, `ReadQueue`, `FileRegistry` | 6 |
| `web/src/element/download.mjs` (new) | saving verified bytes | 7 |
| `web/src/element/{element,define,load,host,template}.mjs` (new), `web/demo/{dev.html,serve.mjs}` (new) | the custom element, static views, dev page | 8 |
| `web/src/element/media/{boxes,container,movie,sidx}.mjs` (new), `web/test/media/` (new) | ISO BMFF parsing | 9 |
| `web/src/element/media/webm.mjs` (new) | WebM parsing | 10 |
| `web/src/element/media/{feeds,mp4box-loader}.mjs` (new), `web/test/mp4box.mjs` (new) | bytes to source-buffer segments | 11 |
| `web/src/element/media/{playback,mse}.mjs`, `web/src/element/video.mjs` (new) | read-ahead, seeking, MSE, the video view | 12 |
| `web/build.mjs` (new), `web/package.json`, `.gitignore` | `dist/<version>/` with SRI | 13 |
| `web/demo/{index.html,many.html,snippet.html,head.html,load.js}` (new), `README.md` | demo pages, "Embed a file" | 14 |
| `docs/superpowers/reports/2026-10-29-xrpl-file-element/report.md` (new) | acceptance record | 15 |

---

### Task 1: The format gains `cover`; the .NET reader parses it and ignores unknown members

The video platform's session starts as soon as Tasks 1 and 2 land; keep them first and self-contained.

**Files:**
- Modify: `docs/spec/xrpl-file-v1.md` (section 4, new 4.1, section 10)
- Modify: `src/XrplFile.Core/Manifest.cs`
- Modify: `tools/XrplFile.Vectors/Program.cs`, `tests/vectors/v1.json` (regenerated)
- Modify: `tests/XrplFile.Core.Tests/Vectors.cs`, `ManifestTests.cs`, `VectorTests.cs`

**Interfaces:**
- Produces: `public sealed record Manifest(string Type, long Size, int Chunk, string Root, long FirstLedger, long LastLedger, int Blocks, int Lists, string? Name, string? Cover = null)`; `Cover` is upper-case hex or null.
- Produces: vector JSON top-level `"manifestVariants": [{ "of": 1001, "note": "cover", "cover": "<SHA256('cover') hex>", "manifest": "…" }, { "of": 1001, "note": "unknown member", "cover": null, "manifest": "…" }]`; test record `VectorManifestVariant(long Of, string Note, string? Cover, string Manifest)` and `VectorSet.ManifestVariants`.

- [ ] **Step 1: Write the format text in `docs/spec/xrpl-file-v1.md`**

Section 4: the example and field table gain `cover` (row text from spec 1.2), the order sentence reads `v, type, size, chunk, root, ledgers, blocks, lists, cover, name`, and the shortening sentence says the name is shortened after `cover` is placed. Add, before "A reader MUST reject a manifest:", the rule verbatim from spec 1.1: "A reader MUST ignore manifest members it does not recognise. A writer MUST NOT emit members other than those this specification defines." Add the clarification that a `cover` that is not 64 hexadecimal characters reads as absent, not as `CorruptManifest`. New subsection **4.1 Cover**: the writer's three requirements (spec 1.2 bullets), the reader's checks (spec 1.3 bullets), "ignored, never an error of the decorated file", "MUST NOT follow a cover's own `cover` field", and that the format fixes no dimensions. Section 10 gains one sentence: `manifestVariants` give manifests that must decode to the same file as the vector they name, one with `cover` and one with a member this version does not define. The version stays `1`.

- [ ] **Step 2: Write the failing manifest tests in `ManifestTests.cs`**

```csharp
private static readonly string CoverHash = new('C', 64);

[Fact]
public void A_cover_is_written_after_lists_and_before_the_name()
{
    Assert.Equal(
        "{\"v\":1,\"type\":\"image/png\",\"size\":734512,\"chunk\":1000,\"root\":\"" + Root + "\"," +
        "\"ledgers\":[106497159,106497394],\"blocks\":1,\"lists\":0,\"cover\":\"" + CoverHash + "\",\"name\":\"cover.png\"}",
        (Sample() with { Cover = CoverHash }).Encode());
}

[Fact]
public void A_cover_round_trips_upper_cased()
{
    string json = (Sample() with { Cover = CoverHash }).Encode().Replace(CoverHash, CoverHash.ToLowerInvariant(), StringComparison.Ordinal);
    Assert.Equal(Sample() with { Cover = CoverHash }, Manifest.Decode(json));
}

[Theory]
[InlineData("123")]
[InlineData("\"AB\"")]
[InlineData("null")]
[InlineData("[]")]
[InlineData("{}")]
public void A_cover_that_is_not_a_hash_reads_as_absent(string value)
{
    string json = Sample().Encode().Replace(",\"name\":", ",\"cover\":" + value + ",\"name\":", StringComparison.Ordinal);
    Assert.Equal(Sample(), Manifest.Decode(json));
}

[Fact]
public void A_cover_with_a_character_that_is_not_hex_reads_as_absent()
{
    string json = Sample().Encode().Replace(",\"name\":", ",\"cover\":\"" + new string('C', 63) + "Z\",\"name\":", StringComparison.Ordinal);
    Assert.Null(Manifest.Decode(json).Cover);
}

[Fact]
public void Members_this_version_does_not_define_are_ignored()
{
    string json = Sample().Encode().Replace(",\"name\":", ",\"future\":{\"tags\":[\"a\",1],\"on\":true},\"x\":null,\"name\":", StringComparison.Ordinal);
    Assert.Equal(Sample(), Manifest.Decode(json));
}

[Fact]
public void The_name_is_shortened_after_the_cover_is_placed()
{
    string encoded = (Sample(name: new string('n', 2000)) with { Cover = CoverHash }).Encode();
    Assert.Equal(MemoLayout.ManifestBytes, Encoding.UTF8.GetByteCount(encoded));
    Assert.Equal(CoverHash, Manifest.Decode(encoded).Cover);
}

[Fact]
public void A_cover_that_is_not_a_hash_is_never_written()
{
    Assert.Throws<InvalidOperationException>(() => (Sample() with { Cover = "AB" }).Encode());
}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ManifestTests`
Expected: build error, `Manifest` has no `Cover`.

- [ ] **Step 4: Implement `Cover` in `src/XrplFile.Core/Manifest.cs`**

Append `string? Cover = null` to the record with a `<param>` doc (hash of the cover file's manifest transaction, format section 4.1). `Decode` reads it through a helper that returns the upper-cased string only for a JSON string of exactly `MerkleTree.HashBytes * 2` hex digits, else null. `Serialize` writes `cover` after `lists` and before `name`. `Encode` throws `InvalidOperationException("the cover is not a 64-hex transaction hash")` for a non-null malformed `Cover` before the shortening loop.

- [ ] **Step 5: Run the manifest tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ManifestTests`
Expected: all pass, including the existing ones.

- [ ] **Step 6: Write the failing variant tests in `VectorTests.cs`**

```csharp
public static TheoryData<string> Variants => ["cover", "unknown member"];

[Theory]
[MemberData(nameof(Variants))]
public void A_manifest_variant_decodes_to_its_base_files_manifest(string note)
{
    VectorManifestVariant variant = Vectors.Load().ManifestVariants.Single(v => v.Note == note);
    Manifest baseline = Manifest.Decode(Vectors.File(variant.Of).Manifest);
    Manifest decoded = Manifest.Decode(variant.Manifest);

    Assert.Equal(variant.Cover, decoded.Cover);
    Assert.Equal(baseline, decoded with { Cover = null });
}

[Theory]
[MemberData(nameof(Variants))]
public async Task A_manifest_variant_reads_the_same_bytes(string note)
{
    VectorManifestVariant variant = Vectors.Load().ManifestVariants.Single(v => v.Note == note);
    VectorFile vector = Vectors.File(variant.Of);
    List<LedgerTransaction> ledger = [.. Vectors.Ledger(vector).Select(tx => tx.Hash == Vectors.ManifestHash
        ? tx with { MemoDataHex = MemoLayout.Utf8Hex(variant.Manifest) } : tx)];

    ReadResult read = await new XrplFileReader(new FakeLedgerSource().AddRange(ledger)).ReadAsync(Vectors.Link, TestContext.Current.CancellationToken);

    Assert.Equal(vector.Sha256, Convert.ToHexString(SHA256.HashData(read.Bytes)));
}
```

Add `VectorManifestVariant` and the `ManifestVariants` member to `Vectors.cs`.

- [ ] **Step 7: Generate the variants in `tools/XrplFile.Vectors/Program.cs`**

After the file loop, from the 1001-byte vector's `Manifest`: the cover variant is `manifest with { Cover = <SHA256("cover") upper hex> }` encoded; the unknown-member variant is the base JSON with `,"future":{"tags":["a",1],"on":true}` inserted before `,"name":`, and the generator throws if the insertion did not change the string. Add `ManifestVariants` to its `VectorSet` record (camelCase `manifestVariants`).

Run: `dotnet run --project tools/XrplFile.Vectors -- tests/vectors/v1.json`
Expected: `wrote 7 file vectors to tests/vectors/v1.json`; `git diff tests/vectors/v1.json` shows the seven file vectors unchanged and a new `manifestVariants` array (the line closing `files` gains a comma).

- [ ] **Step 8: Run the vector tests and the unit suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.VectorTests`, then the unit run from Global Constraints.
Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add docs/spec/xrpl-file-v1.md src/XrplFile.Core/Manifest.cs tools/XrplFile.Vectors/Program.cs tests/vectors/v1.json tests/XrplFile.Core.Tests/ManifestTests.cs tests/XrplFile.Core.Tests/VectorTests.cs tests/XrplFile.Core.Tests/Vectors.cs
git commit -m "feat(format): an optional cover in the manifest, and readers ignore unknown members"
```

### Task 2: The web reader parses `cover` and reads a cover by the rules of 4.1

**Files:**
- Modify: `web/src/manifest.mjs`, `web/src/reader.mjs`, `web/xrpl-file.js`
- Create: `web/src/cover.mjs`, `web/test/cover.test.mjs`
- Modify: `web/test/manifest.test.mjs`, `web/test/vectors.test.mjs`, `web/test/vectors.mjs`

**Interfaces:**
- Consumes: vector JSON `manifestVariants` (Task 1).
- Produces: `decodeManifest(json)` result gains `cover: string | null`; `encodeManifest(m)` writes `m.cover` between `lists` and `name` and throws on a malformed one.
- Produces (`web/src/cover.mjs`): `COVER_MAX_BYTES = 65536`; `COVER_TYPES = ['image/webp', 'image/jpeg']`; `coverProblem(coverRecord, decoratedRecord) → string | null`; `coverMagicAgrees(type, bytes) → boolean`. `COVER_MAX_BYTES` and `COVER_TYPES` are exported from `web/xrpl-file.js`.
- Produces (`web/src/reader.mjs`): `async XrplFileReader#readCover(record) → { bytes: Uint8Array, type: string, txHash: string } | null`.
- Produces (`web/test/vectors.mjs`): `FakeSource#transactionCalls` (count of `transaction()` calls); `smallFileLedger({ label, bytes, type, writer = WRITER, firstLedger, cover = null }) → Promise<{ transactions, hash }>` laying out one block of chunks at `firstLedger` and its manifest at `firstLedger + 1`, `hash = labelHash(label)`.

- [ ] **Step 1: Write the failing manifest and vector tests**

In `manifest.test.mjs`, `sample()` gains `cover: null` (decode now returns it), plus:

```js
const COVER = 'C'.repeat(64);
test('a cover is written after lists and before the name, and decodes upper-cased', () => {
  const json = encodeManifest({ ...sample(), cover: COVER });
  assert.ok(json.endsWith(`"lists":0,"cover":"${COVER}","name":"cover.png"}`));
  assert.equal(decodeManifest(json.replace(COVER, COVER.toLowerCase())).cover, COVER);
});
test('a cover that is not 64 hex characters reads as absent', () => {
  for (const value of ['123', '"AB"', 'null', '[]', '{}', `"${'C'.repeat(63)}Z"`]) {
    const json = encodeManifest(sample()).replace(',"name":', `,"cover":${value},"name":`);
    assert.deepEqual(decodeManifest(json), sample(), value);
  }
});
test('members this version does not define are ignored', () => {
  const json = encodeManifest(sample()).replace(',"name":', ',"future":{"tags":["a",1],"on":true},"x":null,"name":');
  assert.deepEqual(decodeManifest(json), sample());
});
test('the name is shortened after the cover is placed', () => {
  const json = encodeManifest({ ...sample('n'.repeat(2000)), cover: COVER });
  assert.equal(new TextEncoder().encode(json).length, MANIFEST_BYTES);
  assert.equal(decodeManifest(json).cover, COVER);
});
test('a malformed cover is never written', () => {
  assert.throws(() => encodeManifest({ ...sample(), cover: 'AB' }));
});
```

In `vectors.test.mjs`: for every `manifestVariants` entry, `decodeManifest(variant.manifest)` has `cover === variant.cover` and equals the base vector's decoded manifest with `cover: null`; and an `XrplFileReader` over the base vector's ledger with the manifest memo replaced reads bytes whose SHA-256 equals `vector.sha256`.

- [ ] **Step 2: Write the failing cover tests in `web/test/cover.test.mjs`**

Fixtures: `WEBP = RIFF + 4 size bytes + WEBP + 'VP8 ' + padding`, `JPEG = FF D8 FF E0 …`. The decorated file is the 1001-byte vector (its `firstLedger` is 1000), so a cover lives at ledgers below 1000.

```js
test('a WebP cover by the writer, below the file, is read and verified', async () => {
  const cover = await smallFileLedger({ label: 'cover', bytes: WEBP, type: 'image/webp', firstLedger: 990 });
  const reader = await decoratedReader({ cover: cover.hash, extra: cover.transactions });
  const record = await reader.manifest(link);
  assert.deepEqual(await reader.readCover(record), { bytes: WEBP, type: 'image/webp', txHash: cover.hash });
});
// Each of these returns null and never throws:
//  - JPEG cover: read (positive control for the second type)
//  - cover written by another account
//  - cover manifest in ledger 1000 (equal to ledgers[0]); the same cover with its manifest at 999 is read
//  - type image/png; size 65,537 (66 chunks); declared image/webp with JPEG bytes
//  - cover hash unknown to the node; cover tx that is not an xf/m memo; cover manifest that is corrupt JSON
//  - source.transaction throwing NodeUnreachable for the cover hash
test('a cover\'s own cover is not followed', async () => {
  // the cover's manifest names a third hash: readCover asks the node for exactly one transaction
  const before = source.transactionCalls;
  await reader.readCover(record);
  assert.equal(source.transactionCalls - before, 1);
});
test('no cover means no request', async () => { /* record.manifest.cover === null → null, transactionCalls unchanged */ });
test('reading a cover leaves the reader\'s forward history alone', async () => {
  // after readCover, reader.history.account is still null and reader.history.size is 0
});
```

`decoratedReader({ cover, extra })` builds the 1001-byte vector ledger with the manifest memo re-encoded with `cover`, adds `extra`, and returns `new XrplFileReader(source)` (keep `source` reachable for the counters).

- [ ] **Step 3: Run to see them fail**

Run: `cd web && node --test test/manifest.test.mjs test/vectors.test.mjs test/cover.test.mjs`
Expected: failures on `cover` and missing `readCover`.

- [ ] **Step 4: Implement**

`manifest.mjs`: `decodeManifest` adds `cover` (`typeof m.cover === 'string' && /^[0-9a-fA-F]{64}$/.test(m.cover) ? m.cover.toUpperCase() : null`); `serialize` emits `cover` when non-null. `readCover` returns null when `record.manifest.cover` is null; otherwise, inside one `try` that returns null on any throw: `coverReader = new XrplFileReader(this.source, { maxBytes: COVER_MAX_BYTES })`, `coverRecord = await coverReader.manifest({ txHash: record.manifest.cover })`, `coverProblem` (account equal, `coverRecord.ledgerIndex < record.manifest.firstLedger`, type in `COVER_TYPES`, `size ≤ COVER_MAX_BYTES`, `blocks === 1`, `lists === 0`), `bytes = await coverReader.block(coverRecord, { i: 0, h: coverRecord.manifest.root, l: coverRecord.manifest.firstLedger })`, `coverMagicAgrees` (`RIFF` at 0 and `WEBP` at 8 for WebP; `FF D8` for JPEG). `vectors.mjs` gains `transactionCalls` and `smallFileLedger`.

- [ ] **Step 5: Run the whole web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add web/src/manifest.mjs web/src/cover.mjs web/src/reader.mjs web/xrpl-file.js web/test/manifest.test.mjs web/test/vectors.test.mjs web/test/vectors.mjs web/test/cover.test.mjs
git commit -m "feat(web): parse a manifest's cover and read it by the rules of section 4.1"
```

### Task 3: The .NET writer emits an optional cover

**Files:**
- Modify: `src/XrplFile.Core/PublicationSession.cs` (`SessionOptions.Cover`, both `StartAsync` families, `FindManifestAsync`, the writer construction near line 1297)
- Modify: `src/XrplFile.Core/PublicationState.cs` (`Cover`), `src/XrplFile.Core/XrplFileWriter.cs` (`WriterOptions.Cover`), `src/XrplFile.Core/ManifestPlan.cs` (`Build(..., string? cover)`)
- Test: `tests/XrplFile.Core.Tests/{PublicationSessionBatchTests,PublicationSessionBatchResumeTests,PublicationSessionTests,PublicationStateTests,XrplFileWriterTests}.cs`

**Interfaces:**
- Consumes: `Manifest.Cover` (Task 1).
- Produces: `SessionOptions.Cover { get; init; }` (string?, 64 hex, stored upper-case); `PublicationState(..., [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cover = null)` appended last, with its own `<param name="Cover">` (the record documents every parameter, and a missing one is CS1573, an error here); `WriterOptions.Cover { get; init; }`; `ManifestPlan.Build(string type, string? name, long size, IReadOnlyList<BlockEntry> entries, long firstLedger, long lastLedger, int lists, string? cover)`.
- The library does not read or check the cover file (Spec question 2); `StartAsync` refuses only a malformed hash.

- [ ] **Step 1: Write the failing tests**

```csharp
// PublicationSessionBatchTests
[Fact]
public async Task A_cover_from_the_options_is_in_the_state_and_the_landed_manifest()
{
    BatchTestNode node = Funded();
    string cover = Vectors.Hash("cover");
    SessionOptions options = new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1), Cover = cover.ToLowerInvariant() };
    PublicationSession session = await StartAsync(node, VectorStream.Bytes("cover", 20_000), options);

    PublicationReport report = await session.RunAsync(null, Token);

    Assert.Equal(cover, report.Manifest.Cover);
    Assert.Equal(cover, (await new XrplFileReader(node.Ledger).ManifestAsync(report.Link, Token)).Manifest.Cover);
    Assert.Equal(cover, PublicationState.Load(session.StatePath).Cover);
}

[Fact]
public async Task A_cover_that_is_not_a_hash_is_refused_before_any_state_exists()
{
    BatchTestNode node = Funded();
    SessionOptions options = new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1), Cover = "xyz" };

    await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(node, VectorStream.Bytes("cover", 20_000), options));
    Assert.Empty(Directory.EnumerateFiles(_stateDirectory));
}
```

Also, each in its class's own idiom:
- `PublicationSessionBatchResumeTests.A_resume_after_the_manifest_landed_finds_it_with_its_cover`: run with a cover; set the state to `Phase = "manifest", ManifestHash = null`; resume as the class's other tests do; `Assert.Equal(first.ManifestHash, resumed.ManifestHash)` and the writer's history holds exactly one record for which `MemoLayout.IsManifest` is true.
- `PublicationSessionTests.A_sequence_run_writes_the_cover_too`: the first test's three assertions on the sequence path.
- `PublicationStateTests.A_cover_round_trips_and_an_absent_one_is_not_written`: `Assert.Equal(cover, PublicationState.Load(path).Cover)`; for a state without one, `Assert.DoesNotContain("\"Cover\"", File.ReadAllText(path), StringComparison.Ordinal)`.
- `XrplFileWriterTests.A_writer_with_a_cover_writes_it_into_the_manifest`: `new WriterOptions { Cover = cover }` → `Assert.Equal(cover, result.Manifest.Cover)`.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionBatchTests -class XrplFile.Tests.PublicationSessionBatchResumeTests -class XrplFile.Tests.PublicationSessionTests -class XrplFile.Tests.PublicationStateTests -class XrplFile.Tests.XrplFileWriterTests`
Expected: build errors on the new members.

- [ ] **Step 3: Implement**

`StartAsync` (both families) checks `options.Cover` first among the argument checks and puts it, upper-cased, into the new state. The writer is built with `Cover = state.Cover`; `PublishCoreAsync` and `FindManifestAsync` pass it to `ManifestPlan.Build`, so a resume looks for exactly the bytes the writer sends (spec B27). Known limit, stated in the `Cover` param doc and not fixed by a state version bump: an application built before this change ignores `Cover` when it resumes such a run, so it looks for a manifest without the cover and sends a second one. Only the fixture tool sets a cover until the desktop work, which will bump the state version when it adds a cover to its UI. XML docs on `SessionOptions.Cover` and `WriterOptions.Cover` say the caller must have published the cover first from the same writer, below the file's first ledger, as `image/webp` or `image/jpeg` of at most 65,536 bytes (format 4.1), and that the library does not check it.

- [ ] **Step 4: Run the classes, then the unit suite**

Run: the command from Step 2, then the unit run from Global Constraints.
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): a publication can name a cover in its manifest"
```

### Task 4: Testnet fixtures (start the publication, let it run in the background)

The publication takes about an hour (the 300 MB file alone took 38 minutes on testnet on 2026-10-26). Start it at the end of this task and continue with Task 5; Task 14 needs its links.

**Files:**
- Create: `tools/XrplFile.Fixtures/XrplFile.Fixtures.csproj`, `tools/XrplFile.Fixtures/Program.cs`
- Create: `tools/fixtures/plan.json`, `tools/fixtures/make-media.sh`, `tools/fixtures/fragment.mjs`, `tools/fixtures/top-boxes.mjs`
- Create: `web/vendor/mp4box-0.5.4.min.js` (copied byte for byte from the video platform's `src/Platform.Web/wwwroot/lib/`), `web/vendor/mp4box-LICENSE.txt` (copyright line and BSD-3-Clause text from the platform's `THIRD-PARTY-NOTICES.md`)
- Modify: `XrplFile.sln`, `XrplFile.Libraries.slnf` (the tool is built by CI, never run there), `.gitattributes` (`*.mp4 binary`, `*.webm binary`, `*.webp binary`, `*.png binary`, `*.jpg binary`)

**Interfaces:**
- Consumes: `SessionOptions.Cover` (Task 3); Batch `PublicationSession.StartAsync(BatchStrategyFactory, …)` and `ResumeAsync`; `WalletSugar.FundWallet(client, wallet, WalletSugar.FaucetNetwork.Testnet)` from `Xrpl` 11.2.0.
- Produces: `run/fixtures/links.json`, `{ "<key>": "xrpl-file://testnet/<HASH>", … }` with the keys of `plan.json`; `web/vendor/mp4box-0.5.4.min.js` for Tasks 11 and 13.

Why a tool and not an opt-in test: this is a one-off, hour-long publication to a public network that must print its links, resume after an interruption and take its file list as input; the test runner gives none of that, and `tools/XrplFile.Vectors` is the repository's precedent for a program that produces data from the library.

- [ ] **Step 1: Write the media script and its helpers**

`tools/fixtures/make-media.sh <source-dir> <platform remux.mjs> <out-dir>` (bash, `set -euo pipefail`), reading `en-back.mp4` and `picture.webp` from `<source-dir>` and writing into `<out-dir>`:

| File | Command (essentials) | Check |
|---|---|---|
| `picture.webp` | copy | — |
| `picture-blocks.png` | `ffmpeg -i picture.webp -compression_level 0` | size > 1,024,000 |
| `cover.webp`, `cover-b.webp` | `crop=1600:900,scale=960:540` (`-b` adds `hflip`), `-quality` 85 down to 40 in steps of 5 until ≤ 65,536 bytes | ≤ 65,536 |
| `text-cyrillic.txt` | heredoc: about 40 lines of Russian and English, including `<b>not bold</b>` | — |
| `text-large.txt` | lines `line N — строка N` until ≥ 1,100,000 bytes | > 1,048,576 |
| `page.html` | `<script>alert('xrpl-file')</script>` and `<img src=x onerror="alert(2)">` | — |
| `drawing.svg` | `<svg onload="alert(1)">` with a `<script>` | — |
| `plain-start.mp4` | copy of the source (moov at start) | `top-boxes` lists `moov` before `mdat` |
| `plain-end.mp4` | `ffmpeg -i src -map 0 -c copy` | `mdat` before `moov` |
| `fragmented.mp4` | `node tools/fixtures/fragment.mjs <remux.mjs> plain-start.mp4 fragmented.mp4` | `ftyp moov sidx moof …` |
| `clip.webm` | `-c:v libvpx-vp9 -b:v 1M -g 60 -row-mt 1 -c:a libopus -b:a 96k` | — |
| `hevc.mp4` | `-c:v libx265 -crf 28 -tag:v hvc1 -c:a copy -movflags +faststart` | — |
| `large-end.mp4` | `-stream_loop 32 -i src -c copy` | 250–400 MB, `mdat` before `moov` |

`top-boxes.mjs <file>` prints top-level ISO box types in order (largesize-aware). `fragment.mjs <remux.mjs> <in> <out>` loads `web/vendor/mp4box-0.5.4.min.js` in a `vm` sandbox as the platform's `tests/Platform.Web.Js/helpers/mp4box.mjs` does, segments with `nbSamples: Number.MAX_SAFE_INTEGER` as its `remuxSample` does, and writes `assembleFragmented({ inits, segments, tracks })` imported from the given `remux.mjs` by file URL.

- [ ] **Step 2: Copy the sources, run the media script and check its output**

The WebP lives in another session's temporary folder, which can be cleaned at any time, so both sources are copied first:

Run: `mkdir -p run/fixtures/source && cp "C:/Users/Evril/Downloads/en-back.mp4" run/fixtures/source/en-back.mp4 && cp "C:/Users/Evril/AppData/Local/Temp/claude/E--Claude-XrplCSharp-xrpl-encrypted-messenger-59ef6a/d479ab54-9cf5-4a63-b54d-aca137110701/images/15.webp" run/fixtures/source/picture.webp`
Expected: two files, 9,164,247 and 178,448 bytes. If the WebP is gone, ask the owner for it again before going on.

Run: `bash tools/fixtures/make-media.sh run/fixtures/source "E:/GIT/XRPL/xrpl-video-platform/src/Platform.Web/wwwroot/js/remux.mjs" run/fixtures/media`
Expected: fourteen files, every check in the table printed as passing; the source is H.264/AAC 1080×1920, 58 s, so `plain-*.mp4` are about 9.2 MB.

- [ ] **Step 3: Write `tools/fixtures/plan.json`**

Ordered entries `{ "key", "file", "type", "name", "writer": "a"|"b", "cover": null|"<earlier key>" }`: `cover` (cover.webp, image/webp, a), `cover-b` (cover-b.webp, image/webp, b), `picture` (image/webp), `picture-blocks` (image/png), `text` (text/plain), `text-large` (text/plain), `html` (text/html), `svg` (image/svg+xml), `mp4-start`, `mp4-end`, `fmp4` (video/mp4), `webm` (video/webm), `mp4-cover` (plain-end.mp4, cover `cover`), `mp4-broken-cover` (plain-end.mp4, cover `cover-b`), `hevc` (video/mp4), `large` (large-end.mp4, video/mp4). Writer `a` unless stated.

- [ ] **Step 4: Write the tool**

`dotnet run --project tools/XrplFile.Fixtures -- tools/fixtures/plan.json run/fixtures/media run/fixtures`. Behaviour, in order:
1. Validates the plan before any network call: files exist; every `cover` names an earlier key; a cover entry is `image/webp` or `image/jpeg` and ≤ 65,536 bytes; a cover by another writer is allowed only on a key ending in `-broken-cover`.
2. Connects to `wss://s.altnet.rippletest.net:51233` (a constant; no override) and publishes as network `testnet`.
3. Loads `run/fixtures/accounts.json` (writer a, writer b, payer seeds) or generates and saves it, then funds each new account with `FundWallet`. Prints addresses, never seeds.
4. For each entry not yet in `links.json`: resumes an unfinished state under `run/fixtures/state/<key>/` through the Batch `ResumeAsync`, else estimates with the Batch `EstimateAsync` (funding the payer or writer again, at most three times, when it is short) and starts a Batch session with `SessionOptions { StateDirectory = …/<key>, Cover = <hash of the cover key's link> }` and a `BatchStrategy` over `WalletSigner`s. After `RunAsync`, reads the manifest back; for files up to 20,000,000 bytes reads the whole file and compares SHA-256 with the source. Writes `links.json` after every entry and prints `key -> link`.
The csproj sets `<RuntimeHostConfigurationOption Include="System.GC.LOHThreshold" Value="4194304" />` (README, "Collector settings for hosts").

- [ ] **Step 5: Build and dry-check**

Run: `dotnet build XrplFile.Libraries.slnf`, then the tool with a plan copy whose `cover` names a later key.
Expected: build clean; the tool exits non-zero naming the entry before connecting anywhere.

- [ ] **Step 6: Commit**

```bash
git add tools/XrplFile.Fixtures tools/fixtures web/vendor XrplFile.sln XrplFile.Libraries.slnf .gitattributes
git commit -m "feat(tools): publish the element's testnet fixtures through the Batch writer"
```

- [ ] **Step 7: Start the publication in the background**

Run (background): `dotnet run --project tools/XrplFile.Fixtures --configuration Release -- tools/fixtures/plan.json run/fixtures/media run/fixtures`
Expected, when it finishes: sixteen `key -> link` lines and `run/fixtures/links.json` with sixteen entries. Continue with Task 5 meanwhile. If it stops, run it again: finished keys are skipped and an unfinished one resumes.

### Task 5: Pure decisions — attributes, views, text, names, messages

**Files:**
- Create: `web/src/element/attributes.mjs`, `view.mjs`, `text.mjs`, `format.mjs`, `messages.mjs`
- Test: `web/test/element-attributes.test.mjs`, `element-view.test.mjs`, `element-text.test.mjs`, `element-format.test.mjs`, `element-messages.test.mjs`

**Interfaces:**
- Consumes: `parseLink`, `XrplFileError` (`web/src/link.mjs`), `ENDPOINTS` (`web/src/ledger.mjs`).
- Produces (`attributes.mjs`): `parseLength(value: string | null) → string | null` (a number → `"<n>px"`; `auto`; a number with `px|%|vh|vw|vmin|vmax|em|rem|ch|cm|mm|in|pt|pc`; else null); `parseLoading(value) → 'lazy' | 'eager' | 'click'`; `parseLoader(value) → boolean` (only `"false"`, case-insensitive, gives false); `linkText({ src, text }) → string | null` (trimmed `src`, else trimmed text content); `endpointsFor(link: { network }, node: string | null) → string[]`.
- Produces (`messages.mjs`): `elementError(code, sentence) → XrplFileError` with a `sentence` property; `messageFor(error, { playing = false } = {}) → string`; `detailFor(error) → { name: string | null, message: string }`; `canVerify(scope = globalThis) → boolean` (`scope.isSecureContext === true && typeof scope.crypto?.subtle?.digest === 'function'`); constants `NO_NODE = 'This network has no built-in node. Set the node attribute.'`, `BAD_NODE = 'The node attribute must be a wss:// address.'`, `INSECURE = 'This page is not served over HTTPS, so the browser cannot check this file.'`, `NO_LINK = 'There is no xrpl-file link in this element.'`.
- Produces (`view.mjs`): `IMAGE_MAX_BYTES = 20_000_000`; `normalizeType(type) → string` (lower case, parameters dropped); `viewForType(manifest) → { view: 'image' | 'video' | 'text' | 'download', reason: null | 'unsupported' | 'too-large' }`; `magicAgrees(type, head: Uint8Array) → boolean`.
- Produces (`text.mjs`): `TEXT_LIMIT = 1_048_576`; `textPreview(bytes, limit = TEXT_LIMIT) → { text: string, truncated: boolean }`.
- Produces (`format.mjs`): `formatBytes(n) → string` (`"999 B"`, `"12.4 KB"`, `"36.5 MB"`, `"1.6 GB"`, decimal units, one decimal); `extensionFor(type) → string`; `downloadName(manifest) → string`.

- [ ] **Step 1: Write the failing tests**

```js
// element-attributes
assert.equal(parseLength('320'), '320px');
for (const v of ['100%', '50vh', 'auto', '12.5rem']) assert.equal(parseLength(v), v);
for (const v of ['', 'red', '10px; color:red', 'calc(1px)', null]) assert.equal(parseLength(v), null);
assert.equal(parseLoading(null), 'lazy'); assert.equal(parseLoading('CLICK'), 'click'); assert.equal(parseLoading('soon'), 'lazy');
assert.equal(parseLoader(null), true); assert.equal(parseLoader('false'), false); assert.equal(parseLoader('no'), true);
assert.equal(linkText({ src: null, text: '  xrpl-file://testnet/AB…  ' }), 'xrpl-file://testnet/AB…');
assert.deepEqual(endpointsFor({ network: 'testnet' }, null), ENDPOINTS.testnet);
assert.deepEqual(endpointsFor({ network: '21337' }, 'wss://node.example'), ['wss://node.example']);
assert.deepEqual(endpointsFor({ network: 'testnet' }, 'ws://localhost:6006'), ['ws://localhost:6006']);
assert.deepEqual(endpointsFor({ network: 'testnet' }, 'ws://127.0.0.1:6006'), ['ws://127.0.0.1:6006']);
assert.throws(() => endpointsFor({ network: '21337' }, null), (e) => e.code === 'NodeUnreachable' && e.sentence === NO_NODE);
for (const bad of ['ws://example.com', 'http://localhost', 'https://x', 'not a url'])
  assert.throws(() => endpointsFor({ network: 'testnet' }, bad), (e) => e.sentence === BAD_NODE);

// element-view
for (const t of ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/avif']) assert.equal(viewForType(m(t, 1000)).view, 'image');
assert.deepEqual(viewForType(m('image/png', 1_600_000_000)), { view: 'download', reason: 'too-large' });   // Review Focus 2
assert.equal(viewForType(m('image/png', 20_000_000)).view, 'image');
assert.deepEqual(viewForType(m('image/svg+xml', 1000)), { view: 'download', reason: 'unsupported' });
for (const t of ['video/mp4', 'video/webm', 'video/quicktime']) assert.equal(viewForType(m(t, 1000)).view, 'video');
for (const t of ['video/x-msvideo', 'audio/mpeg', 'application/pdf', 'application/zip']) assert.equal(viewForType(m(t, 1000)).view, 'download');
for (const t of ['text/plain', 'TEXT/PLAIN; charset=utf-8', 'text/csv', 'text/markdown', 'application/json', 'text/html', 'text/xml', 'text/x-log'])
  assert.equal(viewForType(m(t, 1000)).view, 'text', t);
assert.equal(magicAgrees('image/png', PNG), true);  assert.equal(magicAgrees('image/jpeg', PNG), false);
assert.equal(magicAgrees('image/webp', RIFF_WEBP), true); assert.equal(magicAgrees('image/webp', RIFF_AVI), false);
assert.equal(magicAgrees('image/gif', ascii('GIF87a')), true); assert.equal(magicAgrees('image/gif', ascii('GIF89a')), true);
assert.equal(magicAgrees('image/avif', FTYP_AVIF), true); assert.equal(magicAgrees('image/avif', FTYP_ISOM), false);
assert.equal(magicAgrees('text/plain', PNG), false); assert.equal(magicAgrees('text/plain', ascii('hello')), true);
assert.equal(magicAgrees('image/png', new Uint8Array()), false);

// element-text
const cyr = new TextEncoder().encode('я'.repeat(10)); // 20 bytes
assert.deepEqual(textPreview(cyr, 5), { text: 'яя', truncated: true });       // never splits a sequence
assert.deepEqual(textPreview(cyr, 100), { text: 'я'.repeat(10), truncated: false });
assert.equal(textPreview(new Uint8Array([0x61, 0xff, 0x62])).text, 'a\uFFFDb');
assert.equal(textPreview(new TextEncoder().encode('<b>x</b>')).text, '<b>x</b>');

// element-format — Review Focus 1
assert.equal(downloadName({ name: '../../evil.exe', type: 'application/octet-stream' }), '_.._evil.exe');
assert.equal(downloadName({ name: 'a<b>:c|d?.txt', type: 'text/plain' }), 'a_b__c_d_.txt');
assert.equal(downloadName({ name: 'con\u0007\u0000.txt', type: 'text/plain' }), '_con.txt');   // Windows device name
for (const [name, out] of [['NUL', '_NUL'], ['com1.log', '_com1.log'], ['LPT9', '_LPT9'], ['aux.tar.gz', '_aux.tar.gz'], ['console.txt', 'console.txt'], ['name. . ', 'name']])
  assert.equal(downloadName({ name, type: 'text/plain' }), out, name);
assert.equal(downloadName({ name: '   ', type: 'image/webp' }), 'xrpl-file.webp');
assert.equal(downloadName({ name: null, type: 'video/mp4' }), 'xrpl-file.mp4');
assert.equal(downloadName({ name: null, type: 'application/x-unknown' }), 'xrpl-file.bin');
assert.ok(downloadName({ name: 'n'.repeat(500), type: 'text/plain' }).length <= 200);
assert.equal(formatBytes(36_500_000), '36.5 MB'); assert.equal(formatBytes(999), '999 B');

// element-messages: every reading error has a sentence
for (const code of ['InvalidLink', 'ManifestNotFound', 'NodeLacksHistory', 'CorruptManifest', 'ListMismatch', 'BlockMismatch', 'ChunkMissing', 'TooLarge', 'NodeUnreachable'])
  assert.ok(messageFor(new XrplFileError(code, 'x', { block: 3 })).length > 0, code);
assert.equal(messageFor(new XrplFileError('BlockMismatch', 'x', { block: 12 })),
  "This file did not pass verification. Block 12 does not match the file's fingerprint. Nothing was shown.");
assert.equal(messageFor(new XrplFileError('NodeLacksHistory', 'x')), 'The node does not have the history this file needs.');
assert.equal(messageFor(elementError('NodeUnreachable', NO_NODE)), NO_NODE);
assert.deepEqual(detailFor(new TypeError('boom')), { name: null, message: 'Something went wrong while showing this file.' });
assert.equal(canVerify({ isSecureContext: true, crypto: { subtle: { digest() {} } } }), true);
assert.equal(canVerify({ isSecureContext: false, crypto: {} }), false);
assert.deepEqual(detailFor(elementError(null, INSECURE)), { name: null, message: INSECURE });
assert.notEqual(messageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 }), { playing: true }),
  messageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 })));  // a playing video does not claim "nothing was shown"
```

Exact `downloadName` rule: drop control characters (U+0000–U+001F, U+007F); replace each of `/ \ : * ? " < > |` with `_`; trim leading dots and leading and trailing spaces and dots; prefix `_` when the part before the first dot is, case-insensitively, `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9` or `LPT1`–`LPT9`; cut to 200 characters keeping the extension; empty → `xrpl-file.<extensionFor(type)>`. `elementError` accepts a null code, which `detailFor` reports as `name: null`.

`extensionFor`: png, jpg, gif, webp, avif, mp4, webm, mov, txt, csv, md, json, html, xml, svg, pdf; anything else `bin`. Remaining sentences (wording is the builder's, short and plain): InvalidLink "This is not an xrpl-file link."; ManifestNotFound "This file is not on the ledger."; CorruptManifest "This file's description on the ledger is damaged."; ListMismatch "This file did not pass verification. Its block list does not match the file's fingerprint. Nothing was shown."; ChunkMissing "Part of this file is missing from the ledger: block {n} is incomplete. Nothing was shown."; TooLarge "This file is too large to save in this browser."; NodeUnreachable "The node is not answering. Try again later."; while playing, BlockMismatch and ChunkMissing say "Playback stopped at block {n}: it did not pass verification." and "Playback stopped at block {n}: part of it is missing from the ledger."

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-*.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement the five modules**

`magicAgrees`: PNG `89 50 4E 47 0D 0A 1A 0A`; JPEG `FF D8 FF`; GIF `GIF87a`/`GIF89a`; WebP `RIFF` at 0 and `WEBP` at 8; AVIF `ftyp` at 4 with major or a compatible brand `avif`/`avis`; `video/*` types are checked by Task 9's `containerAgrees`, so here they return true; text types agree unless the head starts with one of the binary magics above, `%PDF`, `PK\x03\x04`, `ftyp` at 4 or `1A 45 DF A3`.

- [ ] **Step 4: Run the tests**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add web/src/element web/test/element-*.test.mjs
git commit -m "feat(web): the element's attribute, view, text, naming and message rules"
```

### Task 6: The page-level shared layer

**Files:**
- Create: `web/src/element/page.mjs`
- Test: `web/test/element-page.test.mjs`

**Interfaces:**
- Consumes: `LedgerClient`, `XrplFileReader` (its `manifest`, `blockList`, `block`, `requireHistoryFrom`, `readCover`), `formatLink`, `BLOCK_CHUNKS`.
- Produces: `PRIORITY = { NOW: 0, AHEAD: 1, BACKGROUND: 2 }`; `PAGE_HISTORY_RECORDS = 1024` (a page-layer reader's history cache: one block's worth, about 4 MB, against the reader default of 4,096 records).
- Produces: `class ReadQueue { constructor(limit = 3); run(task: (signal) => Promise<T>, { priority = PRIORITY.BACKGROUND, signal } = {}) → Promise<T>; get active(): number; get waiting(): number }` — lower priority value first, first-in-first-out among equals; at most `limit` tasks run, except that a `NOW` task may start while `active === limit` (one reserved slot, so a seek never waits behind stale read-ahead); a queued task whose signal aborts is removed and rejects with a `DOMException` named `AbortError`; a running task receives the signal.
- Produces: `class AbortableSource { constructor(source, signal) }` — `transaction` and `earliestLedger` pass through; `accountTransactions` checks the signal before handing on each record and, once aborted, calls `return()` on the underlying iterator and throws `AbortError`, so an aborted walk ends with the page in flight.
- Produces: `class SharedSource` over a client: `transaction(hash)` shares an in-flight request per hash and keeps validated answers for the page's life (a validated transaction does not change); `earliestLedger()` shares an in-flight request; `accountTransactions(...)` passes through.
- Produces: `class NodePool { constructor(createClient = (endpoints) => new LedgerClient(endpoints)); acquire(endpoints: string[]) → Lease; get size(): number }`; `Lease { source: SharedSource, queue: ReadQueue, files: FileRegistry, release(): void }`. One entry per `endpoints.join(' ')`; `release()` is idempotent per lease; the last release closes the client in a microtask, and an `acquire` of the same set before that microtask runs keeps it. Module singleton `export const pool = new NodePool()`.
- Produces: `class FileRegistry { open(link) → FileHandle }` keyed by `formatLink(link)`; `FileHandle { link; record(): Promise<Record>; entries(): Promise<Entry[]>; blockBytes(): Promise<number> /* BLOCK_CHUNKS × manifest.chunk */; block(index, { priority, signal, onChunk, sequential = false }) → Promise<Uint8Array>; cover(): Promise<{ bytes, type, txHash } | null>; release(): void }`.
  - Handles of one link share one manifest request, one block list, one in-flight read per block and the four most recently verified blocks.
  - `entries()` first awaits `requireHistoryFrom(manifest.firstLedger)` once per link (format section 6 step 4), so a node without the history gives `NodeLacksHistory`, not `ListMismatch` or `ChunkMissing`; `block()` awaits `entries()`.
  - A shared in-flight read counts its consumers and has its own `AbortController`, aborted only when every consumer's signal has aborted; the read goes through the lease's queue with the highest priority any consumer asked for.
  - `sequential: true` (images, text, downloads read in order) reads through the page's reader for the history key `${writer} ${firstLedger} ${lastLedger}`, whose `ForwardHistory` is the shared history cache (it keeps the S26 rule: it answers from a range only when it knows the range is complete for that account). Otherwise (video) the read uses `new XrplFileReader(new AbortableSource(lease.source, inner.signal), { historyCacheRecords: PAGE_HISTORY_RECORDS })`, so a seek stops the walk (Spec question 14). Both call `XrplFileReader#block(record, entry, onChunk)`; every consumer's `onChunk` is called.
  - A failed or aborted read is not cached. The last `release()` of a link drops its state in a microtask; an `open` of the same link before then keeps it.

- [ ] **Step 1: Write the failing tests**

A fake client delegates to `FakeSource` from `web/test/vectors.mjs` and counts `close()`; `created` lists the fake clients `createClient` made; `microtask = () => Promise.resolve()`.

```js
test('the same endpoints share one client, closed after the last release', async () => {
  const lease1 = pool.acquire(['wss://a']); const lease2 = pool.acquire(['wss://a']);
  assert.equal(created.length, 1);
  lease1.release(); lease1.release(); await microtask();
  assert.equal(created[0].closed, 0);
  lease2.release(); await microtask();
  assert.equal(created[0].closed, 1);
});
test('a release followed by an acquire in the same tick keeps the client', async () => {   // Review Focus 3
  const lease = pool.acquire(['wss://a']); lease.release();
  const again = pool.acquire(['wss://a']); await microtask();
  assert.equal(created.length, 1); assert.equal(created[0].closed, 0); again.release();
});
test('different endpoint sets get different clients', () => {
  pool.acquire(['wss://a']); pool.acquire(['wss://b']); pool.acquire(['wss://a', 'wss://b']);
  assert.equal(created.length, 3);
});
test('two handles of one link share the manifest request and each block read', async () => {
  const [b1, b2] = await Promise.all([readAll(h1), readAll(h2)]);         // 2500500 vector
  assert.equal(source.transactionCalls, 1);
  assert.equal(source.calls.filter((c) => c.forward && c.fromLedger === 1000).length, 1);
  assert.deepEqual(b1, vectorBytes); assert.deepEqual(b2, vectorBytes);
});
test('releasing one handle mid-read leaves the other\'s read intact', async () => {  // Review Focus 3
  const p = h2.block(1, {}); h1.release();
  assert.deepEqual(await p, expectedBlock1);
});
test('a handle released and reopened in the same tick keeps its verified blocks', async () => {   // Review Focus 3
  await h1.block(0, {}); h1.release(); const again = lease.files.open(link); await microtask();
  const before = source.yielded; await again.block(0, {});
  assert.equal(source.yielded, before);
});
test('a shared read is aborted only when every consumer aborted', async () => {
  const c1 = new AbortController(); const c2 = new AbortController();
  const p1 = h1.block(2, { signal: c1.signal }); const p2 = h2.block(2, { signal: c2.signal });
  c1.abort(); await assert.rejects(p1, { name: 'AbortError' });
  assert.deepEqual(await p2, expectedBlock2);
});
test('a node without the history gives NodeLacksHistory before any list or block read', async () => {
  const lacking = new FakeSource(ledger, 1001);                     // earliest 1001 > firstLedger 1000
  await assert.rejects(handleOver(lacking).block(0, {}), (e) => e.code === 'NodeLacksHistory');
  assert.equal(lacking.calls.length, 0);
});
test('the queue runs at most three tasks and lower priority values first', async () => {
  // six gated tasks submitted with priorities [2, 2, 2, 2, 1, 2]; active never exceeds 3;
  // as the first three finish, the start order is 1, then the remaining 2s in submission order
});
test('a NOW task starts at once even when three are running, and only one such', async () => {
  // three gated BACKGROUND tasks running; run(NOW) starts immediately (active 4); a second NOW waits
});
test('an aborted queued task rejects with AbortError and never starts', async () => {
  const c = new AbortController(); let started = false;
  fillQueue(3); const p = queue.run(async () => { started = true; }, { signal: c.signal });
  c.abort(); await assert.rejects(p, { name: 'AbortError' }); releaseAll(); await settle();
  assert.equal(started, false);
});
test('an aborted walk ends between pages', async () => {
  const c = new AbortController(); const walk = new AbortableSource(paged, c.signal).accountTransactions(WRITER, 1000, 1026, true);
  await walk.next(); c.abort();
  await assert.rejects(walk.next(), { name: 'AbortError' }); assert.equal(paged.returned, true);
});
test('a tampered block rejects with BlockMismatch and is read again next time', async () => {
  await assert.rejects(h.block(1, {}), (e) => e.code === 'BlockMismatch' && e.block === 1);
  repair(source); assert.deepEqual(await h.block(1, {}), expectedBlock1);
});
test('cover() reads the cover once for every handle of the link', async () => {
  const [c1, c2] = await Promise.all([h1.cover(), h2.cover()]);
  assert.equal(c1.type, 'image/webp'); assert.strictEqual(c1, c2);
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-page.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `ReadQueue` and `AbortableSource`, run their tests**

Run: `cd web && node --test test/element-page.test.mjs`
Expected: the queue and walk tests pass; the rest still fail.

- [ ] **Step 4: Implement `SharedSource`, `NodePool`, `FileRegistry`**

- [ ] **Step 5: Run the whole web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add web/src/element/page.mjs web/test/element-page.test.mjs
git commit -m "feat(web): one connection per node set, one read per link, a queue of block reads"
```

### Task 7: Downloads write only verified bytes

**Files:**
- Create: `web/src/element/download.mjs`
- Test: `web/test/element-download.test.mjs`

**Interfaces:**
- Consumes: `FileHandle` (Task 6), `downloadName` (Task 5), `elementError` (Task 5).
- Produces: `BLOB_LIMIT = 2_147_483_648`; `async saveFile({ handle, record, picker = globalThis.showSaveFilePicker, saveBlob = defaultSaveBlob, onProgress = () => {}, signal }) → 'saved' | 'cancelled'`. With a `picker`: `picker({ suggestedName: downloadName(manifest) })`, `createWritable()`, then each block in order through `handle.block(i, { priority: PRIORITY.NOW, signal })` and `write(bytes)`; any failure calls `writable.abort()` and rethrows; success calls `close()`. A picker rejecting with `AbortError` returns `'cancelled'` before any block read; one throwing `TypeError` (a name the platform still refuses) is asked once more with `suggestedName: 'xrpl-file.<extensionFor(type)>'`. Without a picker: `size > BLOB_LIMIT` throws `elementError('TooLarge', 'This file is too large to save in this browser.')` before any read; otherwise blocks are collected and `saveBlob(new Blob(parts, { type }), name)` is called (`defaultSaveBlob` clicks a temporary `<a download>` and revokes the object URL after the click).

Block reads pass `sequential: true`.

- [ ] **Step 1: Write the failing tests**

```js
test('blocks are written in order and the file is closed', async () => {
  assert.equal(await saveFile({ handle, record, picker }), 'saved');
  assert.deepEqual(writable.calls, ['write:0', 'write:1', 'write:2', 'close']);
  assert.equal(picker.options.suggestedName, 'vector-2500500.bin');
});
test('a block that fails verification is never written and the file is aborted', async () => {
  handle.failAt(1, new XrplFileError('BlockMismatch', 'x', { block: 1 }));
  await assert.rejects(saveFile({ handle, record, picker }), (e) => e.code === 'BlockMismatch');
  assert.deepEqual(writable.calls, ['write:0', 'abort']);
});
test('a name the picker refuses falls back to xrpl-file.<ext>', async () => {
  picker.rejectOnceWith(new TypeError('bad name'));
  assert.equal(await saveFile({ handle, record, picker }), 'saved');
  assert.deepEqual(picker.names, ['vector-2500500.bin', 'xrpl-file.bin']);
});
test('a cancelled save dialog reads nothing', async () => {
  picker.reject(new DOMException('', 'AbortError'));
  assert.equal(await saveFile({ handle, record, picker }), 'cancelled');
  assert.equal(handle.reads, 0);
});
test('without a picker, a file over 2 GiB is refused before any read', async () => {
  await assert.rejects(saveFile({ handle: bigHandle, record: big, picker: null }), (e) => e.code === 'TooLarge');
  assert.equal(bigHandle.reads, 0);
});
test('without a picker, the blob holds exactly the file', async () => {
  // saveBlob receives a Blob of size === manifest.size whose bytes equal the vector, and the sanitized name
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-download.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `download.mjs`**

- [ ] **Step 4: Run the whole web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add web/src/element/download.mjs web/test/element-download.test.mjs
git commit -m "feat(web): save a file block by block, never writing a block that did not verify"
```

### Task 8: The custom element with its static views

Images, text, download view, error, loading modes, sizing, cover, caption, events. Video shows the download view until Task 12 replaces it.

**Files:**
- Create: `web/src/element/load.mjs`, `host.mjs`, `template.mjs`, `element.mjs`, `define.mjs`
- Create: `web/demo/serve.mjs`, `web/demo/dev.html`
- Test: `web/test/element-load.test.mjs`, `web/test/element-host.test.mjs`, `web/test/element-markup.test.mjs`

**Interfaces:**
- Consumes: Tasks 5, 6 and 7.
- Produces (`load.mjs`): `async prepare(handle) → { record, cover }` (manifest and cover, nothing else); `async loadWhole(handle, record, { limitBytes = Infinity, priority, signal, onProgress }) → Uint8Array` (the blocks covering `min(size, limitBytes)`, in order, read with `sequential: true`, all verified, concatenated); `progressDetail({ bytesDone, bytesTotal, blocksVerified, blocksTotal })` returns a fresh plain object with exactly those four numbers.
- Produces (`host.mjs`): `hostStyle({ width, height, ratio }) → string` (the `:host` rule text); `linkReadiness({ src, text, parsing }) → 'ready' | 'wait' | 'missing'` (`src` or non-blank text → `ready`; neither while `parsing` → `wait`; neither after → `missing`); `captionState({ phase, network }) → { verified: boolean, text: string }` (`verified` only in `image`, `text`, `video` and `download` phases reached after a verified read; `idle`, `loading` and `error` show the network without the mark).
- Produces (`template.mjs`): `TEMPLATE` (constant shadow-root markup with the mockup's styles and empty slots for every state).
- Produces (`element.mjs`): `class XrplFileElement extends HTMLElement`, `static observedAttributes = ['src', 'width', 'height', 'loading', 'loader', 'node']`. `define.mjs`: `if (!customElements.get('xrpl-file')) customElements.define('xrpl-file', XrplFileElement);`.
- Produces (`web/demo/serve.mjs`): `node web/demo/serve.mjs [port=8790]` serves `web/` on `127.0.0.1` with content types for `.html .js .mjs .json .css .txt`, refuses paths outside `web/` and directory listings, and answers `/fixtures/links.json` from `run/fixtures/links.json` (404 until it exists).

Behaviour the element implements (browser-only; checked in Task 15):
- **Before anything**: `canVerify()` false → error view with `INSECURE`, `error` event, no request.
- **Shadow root** `mode: 'open'`, filled once with `TEMPLATE`; dynamic text through `textContent`, images through `src` of blob URLs revoked on reset.
- **Sizing**: a `<style>` in the shadow root holds `hostStyle(...)`: `:host { display: block; position: relative; aspect-ratio: <ratio> }` plus `width`/`height` from `parseLength`. `:host` rules lose to the site's own CSS, which is what makes the attributes presentational defaults. Ratio: `16 / 9`, then the cover's natural size once decoded, then the image's natural size (or the video's `videoWidth/videoHeight` in Task 12), set once each. Content uses `object-fit: contain`. Text view keeps the reserved box and scrolls inside it.
- **The link**: `linkReadiness` with `parsing = document.readyState === 'loading'`; on `wait`, a `MutationObserver` (`childList`, `characterData`, `subtree`) and `DOMContentLoaded` re-check, so the original form `<xrpl-file>xrpl-file://…</xrpl-file>` works when the script runs before the children are parsed; `missing` → `InvalidLink` with `NO_LINK`.
- **Loading**: `parseLink` → `endpointsFor` → `pool.acquire` → `files.open`. `lazy`: one `IntersectionObserver` with `rootMargin: '100% 0px'` starts the load; a second with no margin marks the element visible, which sets block priority `NOW` (visible) or `BACKGROUND`. `eager`: at once. `click`: `prepare` only, then the idle view with name, size, the cover if any, and an Open (play symbol for video types) button that starts the load. `src` change aborts the current generation and starts over. `disconnectedCallback` aborts and releases (pool and registry keep their state through a same-tick reconnect).
- **Views**: `viewForType` first. Image: read block 0 (`sequential: true`), `magicAgrees` on it, and only then `loadWhole` (block 0 comes from the handle's recent blocks), then `<img>`. Text: `loadWhole` with `limitBytes: TEXT_LIMIT`, `textPreview`, `<pre>` via `textContent`, a note "Showing the first 1 MB of {size}." and a Download button when truncated. Download view: name, type, size, network, Download button (Task 7). Mismatch or unsupported type: download view. Error: `messageFor`, `::part(message)`.
- **Loader**: spinner, percent, "12.4 of 36.5 MB · 4 of 37 blocks verified", 4 px bar; `role="progressbar"` with `aria-valuenow`; `loader="false"` keeps the box empty. Spinner animation off under `prefers-reduced-motion`.
- **Caption (variant C)**: name · size on the left; on the right `captionState(...).text` ("✓ verified · testnet" once verified, "testnet" before) and a Copy link button (`navigator.clipboard.writeText(link)`, `aria-label="Copy link"`); the full `xrpl-file://` link as its `title`; shown on `:hover` and `:focus-within`, toggled by a tap when `pointerType === 'touch'`; at the top edge for video so it never covers the player's controls (bottom elsewhere). `part="caption"`.
- **Events**: `xrpl-file-progress` with `progressDetail(...)` after every verified block (and every chunk, throttled to one per animation frame); `load` when content (or the download view) is shown; `error` with `detailFor(error)`.
- **Custom properties** with the mockup defaults: `--xrpl-file-accent: #4da3ff`, `--xrpl-file-radius: 10px`, `--xrpl-file-background: #14161a`, `--xrpl-file-foreground: #e8e8ea`.

- [ ] **Step 1: Write the failing tests**

```js
// element-load
test('loadWhole reports each verified block and returns the whole file', async () => {
  const details = []; const bytes = await loadWhole(handle, record, { onProgress: (d) => details.push(d) });
  assert.deepEqual(bytes, await vectorFileBytes(v.seed, v.size));
  assert.deepEqual(details.at(-1), { bytesDone: 2500500, bytesTotal: 2500500, blocksVerified: 3, blocksTotal: 3 });
  assert.ok(details.every((d, i) => i === 0 || d.bytesDone >= details[i - 1].bytesDone));
});
test('a limit reads only the blocks that cover it', async () => {
  const bytes = await loadWhole(spyHandle, record, { limitBytes: 1_048_576 });
  assert.equal(bytes.length, 2_048_000); assert.deepEqual(spyHandle.blocksAsked, [0, 1]);
});
test('a tampered block rejects with its index and returns nothing', async () => {
  await assert.rejects(loadWhole(tamperedHandle, record), (e) => e.code === 'BlockMismatch' && e.block === 2);
});
test('prepare reads the manifest and the cover and no block of the file', async () => {
  const { cover } = await prepare(spyHandle);
  assert.deepEqual(spyHandle.blocksAsked, []); assert.equal(cover.type, 'image/webp');
});

// element-host
assert.equal(hostStyle({ width: null, height: null, ratio: '16 / 9' }), ':host { display: block; position: relative; aspect-ratio: 16 / 9; }');
assert.equal(hostStyle({ width: '320px', height: '50vh', ratio: '16 / 9' }), ':host { display: block; position: relative; aspect-ratio: 16 / 9; width: 320px; height: 50vh; }');
assert.equal(linkReadiness({ src: 'xrpl-file://testnet/A', text: '', parsing: true }), 'ready');
assert.equal(linkReadiness({ src: null, text: '  ', parsing: true }), 'wait');
assert.equal(linkReadiness({ src: null, text: '', parsing: false }), 'missing');
assert.equal(linkReadiness({ src: null, text: ' xrpl-file://testnet/A ', parsing: false }), 'ready');
assert.deepEqual(captionState({ phase: 'idle', network: 'testnet' }), { verified: false, text: 'testnet' });
assert.deepEqual(captionState({ phase: 'image', network: 'testnet' }), { verified: true, text: '✓ verified · testnet' });

// element-markup — Review Focus 1
test('no element module assigns markup built from data', async () => {
  for (const file of await readdir(new URL('../src/element/', import.meta.url), { recursive: true })) {
    if (!file.endsWith('.mjs')) continue;
    const source = await readFile(new URL(`../src/element/${file}`, import.meta.url), 'utf8');
    for (const line of source.split('\n').filter((l) => /innerHTML|outerHTML|insertAdjacentHTML|document\.write/.test(l)))
      assert.match(line, /\.innerHTML = TEMPLATE;/, `${file}: ${line.trim()}`);
  }
});
```

`hostStyle` writes exactly the order shown: `display`, `position`, `aspect-ratio`, then `width` and `height` when set.

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-load.test.mjs test/element-host.test.mjs test/element-markup.test.mjs`
Expected: `load.mjs` and `host.mjs` not found; the markup test passes vacuously until `element.mjs` exists, then must keep passing.

- [ ] **Step 3: Implement `load.mjs` and `host.mjs`**

Run: `cd web && node --test test/element-load.test.mjs test/element-host.test.mjs`
Expected: all pass.

- [ ] **Step 4: Write `serve.mjs` and `dev.html`, and check the server**

`dev.html` loads `<script type="module" src="../src/element/define.mjs">` (which does not exist yet: the page shows nothing until Step 5) and shows one element per fixture key (`?only=<key>` shows that one alone) from `run/fixtures/links.json`, fetched through `serve.mjs`'s `/fixtures/links.json` route; `?link=<xrpl-file link>` shows any one link, for use before the publication has finished. Nothing in it is a committed link; the committed demo pages come in Task 14.

Run: `node web/demo/serve.mjs 8790` (background), then `curl -s -o /dev/null -w "%{http_code} %{content_type}\n" http://127.0.0.1:8790/demo/dev.html` and `curl -s -o /dev/null -w "%{http_code}\n" "http://127.0.0.1:8790/../README.md"`.
Expected: `200 text/html; charset=utf-8`, then `404` (or `403`). Stop the server.

- [ ] **Step 5: Implement `template.mjs`, `element.mjs`, `define.mjs`; run the suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass, the markup test now over real files.

- [ ] **Step 6: Smoke check in the built-in browser pane**

Add a local (uncommitted) `.claude/launch.json` configuration `xrpl-file-demo`: `runtimeExecutable` `node`, `runtimeArgs` `["web/demo/serve.mjs"]`, `port` 8790. Start it with `preview_start`, open `http://127.0.0.1:8790/demo/dev.html`.
Expected: an image fixture goes loader → picture with no console errors; the caption appears on hover and shows the mark only once the picture is shown; a text fixture shows Cyrillic intact; `?link=` with the link written as the element's text content instead of `src` also loads; the network log shows no request to any host but the testnet node and `127.0.0.1:8790`.

- [ ] **Step 7: Commit**

```bash
git add web/src/element web/test/element-load.test.mjs web/test/element-host.test.mjs web/test/element-markup.test.mjs web/demo/serve.mjs web/demo/dev.html
git commit -m "feat(web): the <xrpl-file> element with image, text, download and error views"
```

### Task 9: ISO BMFF — boxes, container, `moov` anywhere, sample tables, `sidx`

**Files:**
- Create: `web/src/element/media/boxes.mjs`, `container.mjs`, `movie.mjs`, `sidx.mjs` (copied from the platform)
- Create: `web/test/media/make.sh` and the media it writes (committed, each well under 200 KB): `plain-start.mp4`, `plain-end.mp4`, `fragmented.mp4` (ffmpeg fragmenting, no `sidx`), `fragmented-sidx.mp4` (through `tools/fixtures/fragment.mjs`), `hevc.mp4`, `clip.webm`, `clip-live.webm`, `expected.json`
- Test: `web/test/media-boxes.test.mjs`, `media-container.test.mjs`, `media-movie.test.mjs`, `media-sidx.test.mjs`

**Interfaces:**
- Produces (`boxes.mjs`): `readBox(bytes, at) → { type, start, headerSize, size, end } | null` (handles 64-bit `largesize` and size 0 = to the end; null when the header is not all in `bytes`); `topLevelBoxes(bytes, from = 0) → Box[]` (stops at the first incomplete header and reports it as the last element with `partial: true`); `children(bytes, box) → Box[]`; `find(bytes, box, ...path) → Box | null`; `async locateMoov(readBlock: (i) => Promise<Uint8Array>, blockBytes: number, size: number) → { offset: number, bytes: Uint8Array, blocks: number[] }` (walks top-level headers from block 0, jumping over `mdat` by its size to the block holding the next header, then reads the blocks spanning `moov`; `blocks` lists every block it read).
- Produces (`container.mjs`): `detectContainer(head) → 'iso' | 'webm' | 'unknown'` (EBML magic `1A 45 DF A3` with DocType `webm` → `webm`; a first top-level box `ftyp`, `moov`, `mdat`, `free`, `skip` or `wide` with a sane size → `iso`; fragmented or plain is decided from the whole `moov`, Spec question 12); `containerAgrees(type, kind) → boolean` (`video/mp4` and `video/quicktime` ↔ `iso`; `video/webm` ↔ `webm`).
- Produces (`movie.mjs`): `parseMovie(moovBytes) → Movie` (sample offsets in `stco`/`co64` are already file offsets), `Movie { timescale, duration /* s */, fragmented, tracks: Track[] }`, `Track { id, kind: 'video' | 'audio' | 'other', timescale, codec /* e.g. 'avc1.64001F', 'mp4a.40.2', 'hvc1.1.6.L120.B0' */, width, height, table }` where `table` is null for a fragmented file; `seekPoint(movie, seconds) → { time, offset }` — mp4box 0.5.4's rule exactly (Spec question 15): per track whose duration covers `seconds`, the last sample whose composition time (`stts` plus `ctts`, edit lists ignored as mp4box ignores them) is at or before `seconds`, and for a track with `stss` the last sync sample up to it; `offset` is the least of those samples' file offsets (`stsc`, `stco`/`co64`, `stsz`) and `time` the least of their composition times in seconds; `mimeFor(movie) → string` (`video/mp4; codecs="<video>,<audio>"`). Duration from `mvhd`, else `mehd`, as the platform's `mp4.mjs` reads it. Codec strings: avcC profile/compat/level hex and esds object type/audio object type as in the platform's `readVideoCodec`/`readAudioCodec`; hvcC per ISO/IEC 14496-15 Annex E (`hvc1.<profile>.<compat flags bit-reversed, hex>.<L|H><level>.<constraint bytes, hex, trailing zero bytes dropped>`).
- Produces (`sidx.mjs`): the platform's `parseSidx(bytes, start)` and `groupAt(groups, seconds)` unchanged, plus `findSidx(bytes) → number | null` (start of a top-level `sidx` after `moov`).

- [ ] **Step 1: Generate the test media**

`web/test/media/make.sh` (bash, ffmpeg): `testsrc2=size=160x90:rate=10` with `sine=frequency=440:sample_rate=48000`, 4 s, `libx264 -g 10 -keyint_min 10 -sc_threshold 0 -pix_fmt yuv420p`, `aac -b:a 32k`: `plain-start.mp4` (`-movflags +faststart`), `plain-end.mp4` (no faststart), `fragmented.mp4` (`-movflags +frag_keyframe+empty_moov+default_base_moof`), `hevc.mp4` (`libx265 -tag:v hvc1`), `clip.webm` (`libvpx-vp9 -g 10`, `libopus`), `clip-live.webm` (same, written to `pipe:1` so it has no Cues and unknown sizes); `fragmented-sidx.mp4` through `tools/fixtures/fragment.mjs` (needs the platform's `remux.mjs` path as `$1`). x264's defaults put B-frames in the H.264 media, as in `en-back.mp4`, so composition and decode times differ. `expected.json` holds, from `ffprobe -select_streams v -show_packets -of json`, the file offsets (`pos`) of the video keyframes (`flags` containing `K`) in `plain-start.mp4` and `plain-end.mp4`. The binaries are covered by the `.gitattributes` lines from Task 4.

Run: `bash web/test/media/make.sh E:/GIT/XRPL/xrpl-video-platform/src/Platform.Web/wwwroot/js/remux.mjs`
Expected: seven media files and `expected.json`, each under 200 KB.

- [ ] **Step 2: Write the failing tests**

```js
// media-boxes
assert.deepEqual(topLevelBoxes(read('plain-start.mp4')).map((b) => b.type).filter((t) => t !== 'free'), ['ftyp', 'moov', 'mdat']);
test('moov at the end is found without reading the blocks between', async () => {
  const bytes = read('plain-end.mp4'); const blockBytes = 4096;
  const { offset, bytes: moov, blocks } = await locateMoov(reader(bytes, blockBytes), blockBytes, bytes.length);
  assert.equal(ascii(bytes.subarray(offset + 4, offset + 8)), 'moov');
  assert.deepEqual(moov, bytes.subarray(offset, offset + moov.length));
  const total = Math.ceil(bytes.length / blockBytes);
  assert.ok(blocks.length < total / 2); assert.equal(blocks[0], 0);
});
test('a 64-bit box size is read', () => {
  const bytes = new Uint8Array(16); new DataView(bytes.buffer).setUint32(0, 1); bytes.set(ascii('mdat'), 4);
  new DataView(bytes.buffer).setBigUint64(8, 2n ** 32n + 16n);
  assert.deepEqual(readBox(bytes, 0), { type: 'mdat', start: 0, headerSize: 16, size: 2 ** 32 + 16, end: 2 ** 32 + 16 });
});
test('a header cut by the end of the bytes is reported as partial', () => {
  const bytes = read('plain-start.mp4').subarray(0, 36);              // ftyp (32) and half of the next header
  assert.equal(readBox(bytes, 32), null);
  assert.equal(topLevelBoxes(bytes).at(-1).partial, true);
});

// media-container
for (const f of ['plain-start.mp4', 'plain-end.mp4', 'fragmented.mp4', 'fragmented-sidx.mp4', 'hevc.mp4']) assert.equal(detectContainer(read(f)), 'iso', f);
assert.equal(detectContainer(read('clip.webm')), 'webm');
assert.equal(detectContainer(new TextEncoder().encode('hello world')), 'unknown');
assert.equal(containerAgrees('video/mp4', 'webm'), false); assert.equal(containerAgrees('video/quicktime', 'iso'), true);

// media-movie
for (const f of ['plain-start.mp4', 'plain-end.mp4']) test(`${f}: a seek lands on the keyframe at or before the target`, () => {
  const movie = parseMovie(moovOf(f)); const video = movie.tracks.find((t) => t.kind === 'video');
  for (const target of [0, 1.3, 2.5, 3.9]) {
    const { time, offset } = seekPoint(movie, target);
    const sync = syncSamples(video);                               // [{ cts, offset }] from the parsed table
    const chosen = sync.filter((s) => s.cts <= target).at(-1);
    assert.ok(time <= chosen.cts && time <= target);
    assert.ok(expected[f].videoKeyframeOffsets.includes(chosen.offset));
    assert.ok(offset <= chosen.offset && isSampleStart(movie, offset));
    const next = sync.find((s) => s.cts > chosen.cts); if (next) assert.ok(next.cts > target);
  }
});
assert.match(parseMovie(moovOf('plain-start.mp4')).tracks.find((t) => t.kind === 'video').codec, /^avc1\.[0-9A-F]{6}$/);
assert.equal(parseMovie(moovOf('plain-start.mp4')).tracks.find((t) => t.kind === 'audio').codec, 'mp4a.40.2');
assert.match(parseMovie(moovOf('hevc.mp4')).tracks.find((t) => t.kind === 'video').codec, /^hvc1\.1\.6\.L\d+\.B0$/);
assert.ok(Math.abs(parseMovie(moovOf('plain-start.mp4')).duration - 4) < 0.2);
assert.equal(parseMovie(moovOf('fragmented.mp4')).fragmented, true);

// media-sidx
test('every sidx group starts at a moof', () => {
  const bytes = read('fragmented-sidx.mp4'); const { groups } = parseSidx(bytes, findSidx(bytes));
  for (const g of groups) assert.equal(ascii(bytes.subarray(g.offset + 4, g.offset + 8)), 'moof');
});
```

- [ ] **Step 3: Run to see them fail**

Run: `cd web && node --test test/media-*.test.mjs`
Expected: modules not found.

`syncSamples` and `isSampleStart` are test helpers over `track.table`. Task 11 checks `seekPoint` against mp4box's own `seek` on the same files.

- [ ] **Step 4: Implement the four modules** (header comments name what came from the platform's `mp4.mjs`, `fragments.mjs`, `sidx.mjs`)

- [ ] **Step 5: Run the web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add web/src/element/media web/test/media web/test/media-*.test.mjs
git commit -m "feat(web): read MP4 structure: moov anywhere, sample tables for seeking, sidx"
```

### Task 10: WebM — the header, the Cues and seeking through them

**Files:**
- Create: `web/src/element/media/webm.mjs`
- Test: `web/test/media-webm.test.mjs`

**Interfaces:**
- Produces: `parseWebmHead(bytes) → { segmentStart, initEnd, timecodeScale, duration /* s, NaN if absent */, cuesOffset /* absolute or null */, codecs: string[], width, height }` — `segmentStart` is the first byte of the Segment's data, `initEnd` the offset of the first Cluster (everything before it is the initialization segment), `cuesOffset` from the SeekHead entry for Cues (`1C 53 BB 6B`) plus `segmentStart`; unknown-size elements are accepted; codecs map `V_VP9`→`vp9`, `V_VP8`→`vp8`, `A_OPUS`→`opus`, `A_VORBIS`→`vorbis`; any other codec id (AV1 included, whose full string needs its sequence header) is passed through raw so `isTypeSupported` refuses it and the download view shows.
- Produces: `async locateCues(readBlock, blockBytes, head) → Uint8Array | null` (reads the block(s) holding the Cues element when `cuesOffset` is set); `parseCues(cuesBytes, segmentStart, timecodeScale) → { time /* s */, offset /* absolute */ }[]` ascending; `cueAt(cues, seconds) → number` (index of the last cue at or before; 0 before the first); `webmMime(head) → string` (`video/webm; codecs="vp9,opus"`).

- [ ] **Step 1: Write the failing tests**

```js
const head = parseWebmHead(read('clip.webm'));
assert.equal(hex4(read('clip.webm'), head.initEnd), '1F43B675');            // a Cluster starts there
assert.deepEqual(head.codecs, ['vp9', 'opus']); assert.equal(webmMime(head), 'video/webm; codecs="vp9,opus"');
assert.ok(Math.abs(head.duration - 4) < 0.2);
test('cues are found through the seek head and point at clusters', async () => {
  const bytes = read('clip.webm'); const cues = parseCues(await locateCues(reader(bytes, 4096), 4096, head), head.segmentStart, head.timecodeScale);
  assert.ok(cues.length >= 2);
  for (const c of cues) assert.equal(hex4(bytes, c.offset), '1F43B675');
  assert.equal(cues[cueAt(cues, 2.5)].time <= 2.5, true);
});
test('a live-written file has no cues and still has its init segment', () => {
  const live = parseWebmHead(read('clip-live.webm'));
  assert.equal(live.cuesOffset, null); assert.equal(hex4(read('clip-live.webm'), live.initEnd), '1F43B675');
});
test('truncated or foreign bytes are refused with an Error, not a hang', () => {
  assert.throws(() => parseWebmHead(read('clip.webm').subarray(0, 40)), Error);
  assert.throws(() => parseWebmHead(read('plain-start.mp4')), Error);
  assert.throws(() => parseWebmHead(new Uint8Array([0x1a, 0x45, 0xdf, 0xa3, 0xff])), Error);
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/media-webm.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `webm.mjs`** (EBML variable-length IDs and sizes, 8-byte size max, all-ones size = unknown)

- [ ] **Step 4: Run the web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add web/src/element/media/webm.mjs web/test/media-webm.test.mjs
git commit -m "feat(web): read WebM headers and seek through Cues"
```

### Task 11: Feeds — from verified blocks to source-buffer segments

**Files:**
- Create: `web/src/element/media/feeds.mjs`, `web/src/element/media/mp4box-loader.mjs`
- Create: `web/test/mp4box.mjs` (the platform's vm loader, pointed at `web/vendor/mp4box-0.5.4.min.js`)
- Test: `web/test/media-feeds.test.mjs`

**Interfaces:**
- Consumes: Tasks 9 and 10; `web/vendor/mp4box-0.5.4.min.js` (Task 4).
- Produces (`mp4box-loader.mjs`): `MP4BOX_INTEGRITY = '__MP4BOX_INTEGRITY__'` (replaced by the build, Task 13); `mp4boxUrl(scriptUrl) → string` (the same directory, file `mp4box.min.js`; a module URL ending in `/src/element/media/mp4box-loader.mjs` maps to `/vendor/mp4box-0.5.4.min.js` for the unbundled dev page); `loadMp4Box({ scriptUrl, integrity = MP4BOX_INTEGRITY }) → Promise<MP4Box>` (one `<script>` per page, `crossOrigin = 'anonymous'`, `integrity` set unless it is still the placeholder; resolves the `MP4Box` the script defined, and puts back whatever `window.MP4Box` held before, so a host page with its own copy keeps it).
- Produces (`feeds.mjs`): `async openFeed({ kind: 'iso' | 'webm', head, readBlock, blockBytes, size, loadMp4Box }) → Feed`.
  `Feed { kind: 'fmp4' | 'mp4' | 'webm'; tracks: { id, mime }[]; duration: number; seekable: boolean; init(): Promise<Segment[]>; seekTo(seconds) → Promise<{ block, offset, time } | null>; media(block, bytes, from = block * blockBytes) → Promise<Segment[]>; close(): void }`, `Segment { track, bytes: Uint8Array }`. `seekTo` both answers where to read from and makes the next `media` calls continue from there; `seekTo(0)` starts playback.
  - `iso`: `locateMoov`, then `parseMovie(moov).fragmented` decides `fmp4` or `mp4`.
  - `fmp4`: one track `{ id: 0, mime: mimeFor(movie) }`; `init()` = bytes up to the end of `moov` (and `sidx` when present); `media` returns the block bytes from `from` as one segment; `seekTo` from `groupAt` over `sidx` groups; without `sidx`, `seekable` is false and `seekTo(t)` returns null for t > 0.
  - `mp4` (plain, also QuickTime): every `seekTo(t)` makes a fresh mp4box `ISOFile` (mp4box's `seek` does not reset segment streams already running): `appendBuffer` of block 0 with `fileStart = 0` (it holds `ftyp` and the `mdat` header, without which mp4box cannot skip to a `moov` at the end), then the `moov` bytes with their `fileStart` when `moov` is past block 0; `onReady` → per track `setSegmentOptions(id, null, { nbSamples: 100 })` and `initializeSegmentation()` (`init()` returns these once; later ones are dropped, they are byte-identical); `seek(t, true)` gives `{ offset, time }`; `start()`. The feed answers `{ block: Math.floor(offset / blockBytes), offset, time }`. Tracks: one per mp4box track, mime `video/mp4; codecs="<codec>"` or `audio/mp4; codecs="<codec>"` from `info.tracks[].codec`. `media` appends `bytes.subarray(from - block * blockBytes)` with `fileStart = from`, collects `onSegment` output and calls `releaseUsedSamples(id, sampleNumber)` for each; at the last block it calls `flush()`.
  - `webm`: one track `{ id: 0, mime: webmMime(head) }`; `init()` = bytes up to `initEnd`; `media` as fmp4; `seekTo` through `locateCues`/`parseCues`/`cueAt`; without Cues, `seekable` false.

- [ ] **Step 1: Write the failing tests**

Blocks of 4096 bytes over the test media; `loadMp4Box` is the vm loader from `web/test/mp4box.mjs`; `countSamples` and `tfdtSeconds(segment, timescale)` walk `moof/traf/trun` and `tfdt` with Task 9's `boxes.mjs`.

```js
test('plain MP4 with moov at the end fragments from block reads', async () => {
  const feed = await openFeed({ kind: 'iso', head: block(0), readBlock, blockBytes: 4096, size, loadMp4Box });
  assert.equal(feed.kind, 'mp4');
  assert.deepEqual(feed.tracks.map((t) => t.mime.split(';')[0]).sort(), ['audio/mp4', 'video/mp4']);
  assert.equal((await feed.init()).length, 2);
  const start = await feed.seekTo(0);
  let samples = 0; for (let b = start.block; b < blocks; b++) samples += countSamples(await feed.media(b, await readBlock(b), b === start.block ? start.offset : undefined));
  assert.equal(samples, totalSamplesFromMoov);
  assert.deepEqual(readsBeforeFirstSegment, [0, ...moovBlocks]);
});
for (const f of ['plain-start.mp4', 'plain-end.mp4']) test(`${f}: a seek feeds both tracks from the keyframe at or before the target`, async () => {
  const feed = await openFeed({ kind: 'iso', head: block(0), readBlock, blockBytes: 4096, size, loadMp4Box });
  const target = await feed.seekTo(2.5);
  assert.ok(target.time <= 2.5);
  const segments = [];
  for (let b = target.block; b < blocks && segments.length < 4; b++) segments.push(...await feed.media(b, await readBlock(b), b === target.block ? target.offset : undefined));
  const video = segments.find((s) => s.track === videoId); const audio = segments.find((s) => s.track === audioId);
  assert.ok(video && audio);
  assert.ok(tfdtSeconds(video, videoTimescale) <= 2.5); assert.ok(tfdtSeconds(audio, audioTimescale) <= 2.5);
});
test('seekPoint agrees with mp4box on where to read from', async () => {
  // plain-start.mp4: an ISOFile fed only bytes [0, end of moov), so mp4box's buffer-end adjustment does not apply
  for (const t of [0, 1.3, 2.5, 3.9]) assert.deepEqual(seekPoint(movie, t), mp4boxSeek(t));
});
test('fragmented MP4 with sidx: init ends where the first moof starts, seeks land on a moof', async () => {
  const feed = await openFeed({ kind: 'iso', head: block(0), readBlock, blockBytes: 4096, size, loadMp4Box });
  assert.equal(feed.kind, 'fmp4'); assert.equal(feed.seekable, true);
  const [init] = await feed.init(); assert.equal(boxAt(bytes, init.bytes.length), 'moof');
  const target = await feed.seekTo(2.5); assert.equal(boxAt(bytes, target.offset), 'moof'); assert.ok(target.time <= 2.5);
  assert.equal(loadMp4Box.calls, 0);
});
test('fragmented MP4 without sidx is not seekable outside the buffer', async () => {
  const feed = await openFeed({ kind: 'iso', head: block(0), readBlock, blockBytes: 4096, size, loadMp4Box });
  assert.equal(feed.seekable, false); assert.equal(await feed.seekTo(2), null);
});
test('WebM with Cues seeks to a cluster; without Cues it does not', async () => {
  const withCues = await openFeed({ kind: 'webm', head: webm(0), readBlock: readWebm, blockBytes: 4096, size: webmSize });
  const target = await withCues.seekTo(2.5);
  assert.equal(hex4(webmBytes, target.offset), '1F43B675'); assert.ok(target.time <= 2.5);
  const live = await openFeed({ kind: 'webm', head: live(0), readBlock: readLive, blockBytes: 4096, size: liveSize });
  assert.equal(live.seekable, false); assert.equal(await live.seekTo(2.5), null);
});
test('mp4boxUrl', () => {
  assert.equal(mp4boxUrl('https://cdn.example/1.0.0/xrpl-file-element.js'), 'https://cdn.example/1.0.0/mp4box.min.js');
  assert.equal(mp4boxUrl('http://127.0.0.1:8790/src/element/media/mp4box-loader.mjs'), 'http://127.0.0.1:8790/vendor/mp4box-0.5.4.min.js');
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/media-feeds.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `mp4box-loader.mjs`, then the `fmp4` and `webm` feeds; run**

Run: `cd web && node --test test/media-feeds.test.mjs`
Expected: the fragmented, WebM and `mp4boxUrl` tests pass.

- [ ] **Step 4: Implement the `mp4` feed; run the web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add web/src/element/media/feeds.mjs web/src/element/media/mp4box-loader.mjs web/test/mp4box.mjs web/test/media-feeds.test.mjs
git commit -m "feat(web): turn verified blocks into source-buffer segments for fMP4, plain MP4 and WebM"
```

### Task 12: Playback — read-ahead, seeking anywhere, buffering, the video view

**Files:**
- Create: `web/src/element/media/playback.mjs`, `web/src/element/media/mse.mjs` (adapted from the platform's `streamer.mjs`), `web/src/element/video.mjs`
- Modify: `web/src/element/element.mjs` (video types use `VideoView`)
- Test: `web/test/media-playback.test.mjs`

**Interfaces:**
- Consumes: `FileHandle` (Task 6), `openFeed` and `loadMp4Box` (Task 11), `detectContainer`/`containerAgrees` (Task 9), `messageFor` (Task 5).
- Produces (`playback.mjs`, all pure):
  - `AHEAD_SECONDS = 30`, `READ_AHEAD = 3`, `KEEP_SECONDS = 120`, `RETRY_CAP_MS = 30_000`;
  - `retryDelay(error, attempt) → number | null`: `NodeUnreachable` → `Math.min(1000 * 2 ** attempt, RETRY_CAP_MS)` with no attempt limit; every other error → null;
  - `class ReadAhead { constructor({ blocks, limit = READ_AHEAD }); restart(fromBlock, fromOffset) → generation; next() → { block, generation } | null; completed(block, generation) → number[]; failed(block, generation); get stoppedAt(): number | null; get inFlight(): number }` — `next()` gives the lowest unread block from the frontier while fewer than `limit` are in flight and no block has failed before it; `completed` returns the blocks now appendable in file order and `[]` for an old generation; after `failed(k)` nothing at or past `k` is handed out;
  - `shouldRead({ started, playing, streamingAllowed, bufferedAhead, firstMediaAppended }) → boolean`: false when `streamingAllowed` is false; before the first `play`, true only until the first media block is appended (Spec question 11); after, true while playing and `bufferedAhead < AHEAD_SECONDS`;
  - `seekDecision({ time, buffered: [start, end][], seekable }) → { kind: 'buffered' } | { kind: 'read' } | { kind: 'snap', time }` (`snap` to the nearest point of a buffered range, `nearestBuffered`);
  - `overlayState({ started, waiting, readsInFlight, retrying, stoppedAt, atGap }) → 'none' | 'buffering' | 'error'` (`error` only when a block failed verification and playback stands at the gap; `buffering` while waiting on a read or a retry, and before the first frame);
  - `farRanges(buffered, time, keep = KEEP_SECONDS) → [start, end][]` (the parts of `buffered` further than `keep` seconds from `time`).
- Produces (`mse.mjs`): `class Streamer { constructor(video, tracks, duration); open(); append(track, bytes) → Promise; abort(); buffered(): [start, end][]; end(); close(); onStreamingChange(callback) }` — chooses `ManagedMediaSource ?? MediaSource`, asks that same class's `isTypeSupported`, `segments` mode, `duration` set before the first append, one `SourceBuffer` per track, appends queued per buffer; with `ManagedMediaSource`, `disableRemotePlayback = true` and `startstreaming`/`endstreaming` reported through `onStreamingChange` (plain `MediaSource` always allows); on `QuotaExceededError`, removes `farRanges(buffered(), currentTime)` and retries once.
- Produces (`video.mjs`): `class VideoView { constructor({ root, handle, record, cover, priority, onProgress, onError, onLoad }); start(); close() }` (browser-only glue over the pure functions above).

`VideoView` behaviour:
1. Read block 0 at `NOW`; `detectContainer`; `containerAgrees` false → download view; open the feed; any track the chosen MediaSource class refuses → download view with the line "This browser cannot play this video here." Show `<video controls playsinline preload="metadata">` with the cover as `poster` (blob URL) and no `autoplay`; set the host ratio from `videoWidth/videoHeight` on `loadedmetadata`.
2. `seekTo(0)`, append `init()`, then loop on `shouldRead(...)`: start `next()` blocks through `handle.block(i, { priority, signal })` (`NOW` for the first block past the playhead, `AHEAD` for the rest); appendable blocks go through `feed.media` to `streamer.append`; the last block ends the stream. A read error asks `retryDelay`; a number waits and retries the same block (the overlay shows); null → `failed(block)`, and `overlayState` turns to `error` when playback reaches the gap: `messageFor(error, { playing: true })` and the `error` event.
3. `seeking`: `seekDecision`; `buffered` → nothing; `snap` → set `currentTime`; `read` → abort the signals of the old generation's reads (Task 6 stops their walks), `streamer.abort()`, `feed.seekTo(currentTime)`, `restart(target.block, target.offset)`; the first appended segment comes from `target.offset` inside its block. Seeking back into time already dropped by the quota path is a `read` like any other.
4. Overlay from `overlayState`; `xrpl-file-progress` counts verified bytes of this file; `load` on `loadeddata`.
5. `close()`: aborts reads, `streamer.close()`, revokes URLs.

- [ ] **Step 1: Write the failing tests**

```js
test('never more than three reads at once, from the frontier', () => {
  const r = new ReadAhead({ blocks: 10 }); r.restart(0, 0);
  assert.deepEqual([r.next(), r.next(), r.next(), r.next()].map((x) => x?.block ?? null), [0, 1, 2, null]);
});
test('blocks finished out of order are appended in order', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0); r.next(); r.next(); r.next();
  assert.deepEqual(r.completed(2, g), []); assert.deepEqual(r.completed(0, g), [0]); assert.deepEqual(r.completed(1, g), [1, 2]);
});
test('a read for an older seek is dropped', () => {      // Review Focus 5
  const r = new ReadAhead({ blocks: 50 }); const g1 = r.restart(0, 0); r.next();
  const g2 = r.restart(30, 30 * 1_024_000 + 17); const g3 = r.restart(40, 40 * 1_024_000);
  assert.deepEqual(r.completed(0, g1), []); assert.deepEqual(r.completed(30, g2), []);
  assert.equal(r.next().block, 40);
});
test('a failed block stops reading at that block', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0); r.next(); r.next();
  r.failed(1, g); assert.equal(r.stoppedAt, 1); assert.deepEqual(r.completed(0, g), [0]); assert.equal(r.next(), null);
});
test('a dropped connection is retried for as long as it takes, a verification failure never', () => {   // Review Focus 4
  const unreachable = new XrplFileError('NodeUnreachable', 'x');
  assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 50].map((a) => retryDelay(unreachable, a)), [1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
  for (const code of ['BlockMismatch', 'ChunkMissing', 'ListMismatch']) assert.equal(retryDelay(new XrplFileError(code, 'x'), 0), null);
  assert.equal(retryDelay(new TypeError('x'), 0), null);
});
test('before play only the first media block is read; while playing, up to 30 s ahead', () => {
  const base = { streamingAllowed: true, bufferedAhead: 0 };
  assert.equal(shouldRead({ ...base, started: false, playing: false, firstMediaAppended: false }), true);
  assert.equal(shouldRead({ ...base, started: false, playing: false, firstMediaAppended: true }), false);
  assert.equal(shouldRead({ ...base, started: true, playing: true, firstMediaAppended: true, bufferedAhead: 29 }), true);
  assert.equal(shouldRead({ ...base, started: true, playing: true, firstMediaAppended: true, bufferedAhead: 30 }), false);
  assert.equal(shouldRead({ ...base, started: true, playing: false, firstMediaAppended: true }), false);
  assert.equal(shouldRead({ ...base, streamingAllowed: false, started: true, playing: true, firstMediaAppended: true }), false);
});
test('a seek inside the buffer reads nothing; outside, it reads or snaps', () => {
  const buffered = [[0, 40], [300, 330]];
  assert.deepEqual(seekDecision({ time: 310, buffered, seekable: true }), { kind: 'buffered' });
  assert.deepEqual(seekDecision({ time: 200, buffered, seekable: true }), { kind: 'read' });
  assert.deepEqual(seekDecision({ time: 200, buffered, seekable: false }), { kind: 'snap', time: 300 });
  assert.deepEqual(seekDecision({ time: 100, buffered, seekable: false }), { kind: 'snap', time: 40 });
});
test('the overlay shows buffering while waiting and the error only at the gap', () => {
  const quiet = { started: true, waiting: false, readsInFlight: 0, retrying: false, stoppedAt: null, atGap: false };
  assert.equal(overlayState(quiet), 'none');
  assert.equal(overlayState({ ...quiet, waiting: true, readsInFlight: 1 }), 'buffering');
  assert.equal(overlayState({ ...quiet, waiting: true, retrying: true }), 'buffering');
  assert.equal(overlayState({ ...quiet, stoppedAt: 7, atGap: false }), 'none');
  assert.equal(overlayState({ ...quiet, stoppedAt: 7, atGap: true }), 'error');
  assert.equal(overlayState({ ...quiet, started: false }), 'buffering');
});
test('a full buffer gives up what is further than two minutes from the playhead', () => {
  assert.deepEqual(farRanges([[0, 600]], 300), [[0, 180], [420, 600]]);
  assert.deepEqual(farRanges([[250, 350]], 300), []);
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/media-playback.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `playback.mjs`; run its tests**

Run: `cd web && node --test test/media-playback.test.mjs`
Expected: all pass.

- [ ] **Step 4: Implement `mse.mjs`, `video.mjs`, and switch `element.mjs` to `VideoView`; run the suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass (the markup test covers the new files).

- [ ] **Step 5: Smoke check on `dev.html`** (server from Task 8)

Expected: `mp4-start`, `mp4-end`, `fmp4`, `webm` each show a play button and read only their first blocks until play (`xrpl-file-progress` stops); they play while loading and jump to 0:45 with a short buffering overlay; no console errors.

- [ ] **Step 6: Commit**

```bash
git add web/src/element web/test/media-playback.test.mjs
git commit -m "feat(web): stream video with read-ahead, seeking anywhere and a buffering overlay"
```

### Task 13: Build `web/dist/<version>/` with integrity hashes

**Files:**
- Create: `web/build.mjs`
- Modify: `web/package.json` (`"version": "1.0.0"`, `"scripts": { "test": …, "build": "node build.mjs" }`), `.gitignore` (`web/dist/`)
- Test: `web/test/build.test.mjs`

**Interfaces:**
- Consumes: entry `web/src/element/define.mjs`; `web/vendor/mp4box-0.5.4.min.js` and `mp4box-LICENSE.txt`.
- Produces: `export async function build({ webDir, outDir, version, force = false }) → { integrity: { 'xrpl-file-element.js': string, 'mp4box.min.js': string } }`; CLI `node build.mjs` builds `dist/<package version>/`. Output files: `xrpl-file-element.js`, `mp4box.min.js`, `mp4box-LICENSE.txt`, `integrity.json` (`{ "xrpl-file-element.js": "sha384-<base64>", "mp4box.min.js": "sha384-<base64>" }`).

Bundler rules: follow static relative imports from the entry; refuse a bare specifier, a dynamic `import()`, or an import cycle with a message naming the file. Each module becomes `__define('<path>', function (__import, __export) { … })`; `import { a, b as c } from './x.mjs'` → `const { a, b: c } = __import('<path>')`; `import * as ns` → `const ns = __import(…)`; `export function|async function|class|const|let` → declaration kept, name added to one `__export({ … })` at the module's end; `export { a, b as c } from` and `export * as ns from` → re-exports; `import.meta.url` → `__scriptUrl`. The whole is one IIFE in strict mode: `/*! xrpl-file element <version> | MIT */`, `const __scriptUrl = document.currentScript && document.currentScript.src;`, the registry, then `__import('<entry>')`. The placeholder `__MP4BOX_INTEGRITY__` is replaced with the mp4box hash computed first. Module order and output bytes are deterministic. An existing `dist/<version>/` whose files differ from the new build is refused ("dist/<version> exists with other content; raise the version in web/package.json, or pass --force for a version not yet released") unless `build({ force: true })` / `node build.mjs --force`; `dist/` is not in git, so `--force` is what fix loops before a release use.

- [ ] **Step 1: Write the failing tests**

```js
test('the bundle is a classic script that defines the element once', async () => {
  const out = await tempDir(); await build({ webDir, outDir: out, version: '0.0.0-test' });
  const code = await readFile(join(out, 'xrpl-file-element.js'), 'utf8');
  assert.doesNotThrow(() => new vm.Script(code));                      // no import/export left
  const defined = []; const context = browserStubs(defined);            // HTMLElement, customElements, document.currentScript
  vm.runInNewContext(code, context); vm.runInNewContext(code, context);
  assert.deepEqual(defined, ['xrpl-file']);
});
test('integrity.json holds the SHA-384 of both files, and the element carries mp4box\'s', async () => {
  const integrity = JSON.parse(await readFile(join(out, 'integrity.json'), 'utf8'));
  for (const name of ['xrpl-file-element.js', 'mp4box.min.js'])
    assert.equal(integrity[name], `sha384-${createHash('sha384').update(await readFile(join(out, name))).digest('base64')}`);
  assert.ok(code.includes(integrity['mp4box.min.js'])); assert.ok(!code.includes('__MP4BOX_INTEGRITY__'));
});
test('two builds are byte for byte the same', async () => {
  await build({ webDir, outDir: a, version: '0.0.0-test' }); await build({ webDir, outDir: b, version: '0.0.0-test' });
  for (const name of ['xrpl-file-element.js', 'integrity.json']) assert.deepEqual(await readFile(join(a, name)), await readFile(join(b, name)));
});
test('a different build into an existing version directory is refused unless forced', async () => {
  await writeFile(join(out, 'xrpl-file-element.js'), 'stale');
  await assert.rejects(build({ webDir, outDir: out, version: '0.0.0-test' }), /raise the version/);
  await build({ webDir, outDir: out, version: '0.0.0-test', force: true });
  assert.notEqual(await readFile(join(out, 'xrpl-file-element.js'), 'utf8'), 'stale');
});
test('a bare import fails the build naming the file', async () => {
  const web = await copyOfWebWith('src/element/define.mjs', "import x from 'lodash';\n");
  await assert.rejects(build({ webDir: web, outDir: await tempDir(), version: '0.0.0-test' }), /define\.mjs.*lodash/);
});
test('the bundle names no host but the built-in nodes', async () => {
  const urls = code.match(/\b(?:https?|wss?):\/\/[^\s'"`)]+/g) ?? [];
  const allowed = new Set([...Object.values(ENDPOINTS).flat(), 'http://www.w3.org/2000/svg']);
  assert.deepEqual(urls.filter((u) => !allowed.has(u)), []);
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/build.test.mjs`
Expected: `build.mjs` not found.

- [ ] **Step 3: Implement `build.mjs`, bump the version, ignore `web/dist/`**

- [ ] **Step 4: Run the suite and build**

Run: `cd web && node --test test/*.test.mjs && node build.mjs`
Expected: all pass; `dist/1.0.0/` holds the four files.

- [ ] **Step 5: Commit**

```bash
git add web/build.mjs web/package.json .gitignore web/test/build.test.mjs
git commit -m "build(web): a versioned classic-script build of the element with SRI hashes"
```

### Task 14: Demo pages and "Embed a file"

**Files:**
- Create: `web/demo/index.html`, `web/demo/many.html`, `web/demo/snippet.html`, `web/demo/head.html`, `web/demo/load.js`
- Modify: `README.md`

**Interfaces:**
- Consumes: `run/fixtures/links.json` (Task 4; the publication must have finished), `web/dist/1.0.0/` (Task 13), `web/demo/serve.mjs` (Task 8).

- [ ] **Step 1: Write the demo pages**

- `load.js` (demo only): fetches `../dist/1.0.0/integrity.json` and inserts `<script src="../dist/1.0.0/xrpl-file-element.js" integrity crossorigin="anonymous">`, so the demo exercises SRI and never goes stale against a rebuild.
- `index.html`: one section per state with a heading and the link written out: loading (`large` with `loading="eager"`), image (`picture`, `picture-blocks`), text (`text`, `text-large`, `html`), download (`svg`, `hevc`), video (`mp4-start`, `mp4-end`, `fmp4`, `webm`, `mp4-cover`, `mp4-broken-cover`, `large`), `loading="click"` (`mp4-cover`), `loader="false"` (`picture`), sizing (`width="320"`, `height="50vh"`, a CSS grid of three), errors (`xrpl-file://testnet/` + 64 zeros → ManifestNotFound; `xrpl-file://21337/<hash>` → no built-in node; `xrpl-file://nowhere/x` → InvalidLink). A small event log under the page listens for `load`, `error`, `xrpl-file-progress` on every element.
- `many.html`: before the element script, an inline script wraps `window.WebSocket` to count constructions into `window.socketCount` and wraps `WebSocket.prototype.send` to count each JSON request by its `command` into `window.socketCommands` (frames are not visible to `read_network_requests`), both shown on the page; then ten elements: seven different fixtures and `picture` three times.
- `snippet.html`: exactly the README's two lines (no integrity, `defer`) and one fixture.
- `head.html`: the element script in `<head>` without `defer`, and one element in the original form `<xrpl-file>xrpl-file://…</xrpl-file>`.
Links come from `run/fixtures/links.json`, written into the HTML literally. A comment at the top of `index.html` says how to re-publish after a testnet reset: delete `run/fixtures/accounts.json`, `links.json` and `state/`, run Task 4 Steps 2 and 7 again, and replace the links in the three pages.

- [ ] **Step 2: Write the README section "Embed a file"** after "Reading a file (browser)"

The two-line snippet (`<script src="https://<host>/1.0.0/xrpl-file-element.js" integrity="sha384-…" crossorigin="anonymous" defer></script>` and `<xrpl-file src="xrpl-file://testnet/…"></xrpl-file>`); one line each on `width`/`height`, `loading`, `loader`, `node`; the CSS custom properties, parts and events; that the integrity value comes from `integrity.json` of the same version; that the host serves both files with `Access-Control-Allow-Origin` (SRI on a cross-origin script); and the CSP a site needs: `script-src` for the script's origin, `connect-src` for the node URLs (`wss://xrplcluster.com wss://s2.ripple.com` on mainnet, `wss://s.altnet.rippletest.net:51233` on testnet), `img-src blob:`, `media-src blob:`; and that the page must be served over HTTPS (or `localhost`), because the browser hashes only in a secure context. Build: `cd web && npm run build`. In "What is here", add `web/src/element` and drop the element and the demo page from the "planned next" sentence, keeping the rest of it.

- [ ] **Step 3: Check the pages load**

Run the `xrpl-file-demo` server, open `http://127.0.0.1:8790/demo/index.html`, `many.html`, `snippet.html` and `head.html` with `get_page_text`.
Expected: every section heading present, no console errors from the pages themselves (`read_console_messages` with `onlyErrors`).

- [ ] **Step 4: Commit**

```bash
git add web/demo README.md
git commit -m "docs(web): demo pages on testnet fixtures and an Embed a file section"
```

### Task 15: Acceptance in a real browser against testnet (by hand)

**Files:**
- Create: `docs/superpowers/reports/2026-10-29-xrpl-file-element/report.md` and its screenshots

Run the `xrpl-file-demo` server and use the built-in browser pane (skill `anthropic-skills:built-in-browser`) at desktop size, then `resize_window` preset `mobile` for the touch rows, then preset `desktop` again. Record each row as Pass or Fail with a screenshot name; a Fail gets a one-line reproduction. Fix nothing in this task: failures go back to the owning task.

- [ ] **Step 1: Walk the checklist**

| # | Where | Check |
|---|---|---|
| 1 | index, `large` eager | loader shows spinner, percent, "x of y MB · n of m blocks verified", 4 px bar; `xrpl-file-progress` details in the log rise monotonically |
| 2 | index, `picture`, `picture-blocks` | nothing until every block verified, then the picture; proportions 1:1 after load, 16:9 before; `load` fires once |
| 3 | index, `text` | Cyrillic intact, `<b>not bold</b>` shown as source, scrolls inside the box |
| 4 | index, `text-large` | first 1 MB, the note and a Download button; the saved file's SHA-256 equals the source |
| 5 | index, `html`, `svg` | `html` as source text; `svg` download view; no alert dialog at any point |
| 6 | index, `hevc` | download view with "This browser cannot play this video here." where HEVC is refused, or plays where it is supported (say which) |
| 7 | index, `mp4-start` | plain MP4 plays while loading; mp4box requested once, from `127.0.0.1:8790` (`read_network_requests`) |
| 8 | index, `mp4-end` | plays while loading; the network log shows the block holding `moov` read before most of the file |
| 9 | `dev.html?only=fmp4` | plays, appended as-is: `mp4box.min.js` / `mp4box-0.5.4.min.js` is never requested on this page |
| 10 | index, `webm` | plays while loading |
| 11 | seeking, each of 7–10 and `large` | jump to the middle (`currentTime = duration / 2` via `javascript_tool`, and by clicking the native timeline): buffering overlay for about one block read, playback resumes from a keyframe at or before the target; seeking back into watched time plays from the buffer. (Both `fmp4` and `webm` fixtures carry an index; the index-less cases are covered by Task 11's tests.) |
| 12 | `large` | jump to 3/4 within the first ten seconds; a few megabytes read, not the whole file (network requests or `xrpl-file-progress` bytes) |
| 13 | rapid scrubbing on `large` | five seeks within two seconds; playback resumes at the last target, no garbled frames |
| 14 | `mp4-cover` | the cover shows as poster before load and sets the proportions; `loading="click"` copy reads only the manifest and cover until clicked, and its caption shows "testnet" without the verified mark until then |
| 15 | `mp4-broken-cover` | no cover, the video plays as normal, no `error` event |
| 16 | caption | hover and keyboard focus show name · size · ✓ verified · testnet; Copy link puts the `xrpl-file://` link on the clipboard; on video it sits at the top and does not cover the controls; on `mobile`, a tap toggles it |
| 17 | sizing | `width="320"` and `height="50vh"` honoured; a page CSS rule on the element overrides both; the grid of three fills its cells |
| 18 | `loader="false"` | empty box until content |
| 19 | errors | ManifestNotFound, no built-in node ("This network has no built-in node. Set the node attribute."), InvalidLink: one sentence each, `error` detail names in the log |
| 20 | download | Download on `svg` saves `drawing.svg` with the source's SHA-256 (Chromium, picker path) |
| 21 | many.html | `socketCount` is 1 after all ten blocks load; `socketCommands.tx` equals the number of distinct manifests and covers on the page (`picture` three times costs one `tx`) |
| 22 | hosts | across all pages, requests go only to `s.altnet.rippletest.net` and `127.0.0.1:8790` |
| 23 | snippet.html | the README's two lines work as written |
| 24 | lazy | on index, elements far below the fold have not started (`xrpl-file-progress` absent) until scrolled near |
| 26 | before play | a video scrolled into view and not played stops reading after its first media block (`xrpl-file-progress` bytes stop at a block or two) |
| 27 | text content | `<xrpl-file>xrpl-file://…</xrpl-file>` with the script in `<head>` without `defer` loads (`head.html`) |
| 25 | memory | after watching `large` for five minutes, DevTools-free check: `performance.memory.usedJSHeapSize` (Chromium) stays below 300 MB |

- [ ] **Step 2: Write the report** (verdict table, environment: browser and version, node, commit, fixture links, and every Fail with its reproduction)

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/reports/2026-10-29-xrpl-file-element
git commit -m "docs(reports): the element's acceptance on testnet"
```
