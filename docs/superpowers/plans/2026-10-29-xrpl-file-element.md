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
4. **The node drops the WebSocket in the middle of a video**: the read is retried and playback continues; only verification failures (`BlockMismatch`, `ChunkMissing`, `ListMismatch`) stop it — test in Task 12 (`retryDelay`).
5. **Rapid scrubbing, several seeks in a second**: only the last seek's blocks reach the source buffer; a read that completes for an older seek is dropped, never appended out of place — test in Task 12 (`ReadAhead` generations).

---

## Spec questions and rulings

1. **"Picture, WebP 178 KB … multi-block image".** 178 KB is one block (a block is 1,024,000 bytes at `chunk` 1000). Ruling: keep the WebP for the image view and add a PNG of the same picture written with `-compression_level 0` (about 7.7 MB, eight blocks) as the multi-block image fixture.
2. **The broken-cover fixture against the writer's MUST.** Spec 1.2 says a writer MUST publish the cover first, by the same account. The fixture "MP4 with a cover of the wrong account" violates that on purpose. Ruling: the .NET writer checks only that the hash is 64 hex characters; the rest is the caller's job (the desktop app will check it when it chooses covers). The fixture tool checks it for every fixture except the deliberately broken one.
3. **Error names the spec does not list.** "No built-in node", an unusable `node` attribute, and a thrown value that is not an `XrplFileError` have no name in section 8. Ruling: the first two raise `NodeUnreachable` with their own sentence; an unexpected exception gives `detail.name` `null` and "Something went wrong while showing this file."
4. **"Above 1 MB only the first 1 MB is shown" next to "images and text download completely".** Ruling: the limit is 1,048,576 bytes, and a text file reads only the blocks that cover it (two blocks for anything over 1,024,000 bytes), all verified.
5. **No size limit for pictures.** Ruling: above 20,000,000 bytes (the reference readers' default limit, format spec section 6 step 3) an image type shows the download view with "This picture is too large to show here."
6. **A dropped connection during video.** The spec says a slow node means pauses, not an error, and that a block that fails stops reading. Ruling: `NodeUnreachable` on a block read is retried after 1, 2 and 4 s; integrity errors are never retried.
7. **"In the style of the existing fiddle bundler".** Neither repository has one. Ruling: `web/build.mjs`, a module-registry bundler of about 150 lines that handles exactly the import/export forms `web/src` uses and fails the build on anything else.
8. **A writer must not emit unknown members, yet the vectors need one.** Ruling: the vector generator inserts the extra member textually into the 1001-byte vector's manifest, never through `Manifest.Encode`.
9. **Positional order of the .NET record.** `Cover` is appended after `Name` in `Manifest(...)` so every existing call keeps compiling; the JSON order (`cover` before `name`) is the serializer's business.
10. **`readCover` placement.** It is a method, `XrplFileReader#readCover(record)`, beside the pure checks in `web/src/cover.mjs` (which imports nothing from `reader.mjs`, so there is no import cycle). It reads through a fresh reader over the same source, so it never disturbs a forward history walk (spec 2.7).

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
| `web/src/element/{element,define,load,template}.mjs` (new), `web/demo/{dev.html,serve.mjs}` (new) | the custom element, static views, dev page | 8 |
| `web/src/element/media/{boxes,container,movie,sidx}.mjs` (new), `web/test/media/` (new) | ISO BMFF parsing | 9 |
| `web/src/element/media/webm.mjs` (new) | WebM parsing | 10 |
| `web/src/element/media/{feeds,mp4box-loader}.mjs` (new), `web/test/mp4box.mjs` (new) | bytes to source-buffer segments | 11 |
| `web/src/element/media/{playback,mse}.mjs`, `web/src/element/video.mjs` (new) | read-ahead, seeking, MSE, the video view | 12 |
| `web/build.mjs` (new), `web/package.json`, `.gitignore` | `dist/<version>/` with SRI | 13 |
| `web/demo/{index.html,many.html,snippet.html,load.js}` (new), `README.md` | demo pages, "Embed a file" | 14 |
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
Expected: `wrote 7 file vectors to tests/vectors/v1.json`; `git diff --stat tests/vectors/v1.json` shows only additions.

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
- Produces: `SessionOptions.Cover { get; init; }` (string?, 64 hex, stored upper-case); `PublicationState(..., [JsonIgnore(WhenWritingNull)] string? Cover = null)` appended last; `WriterOptions.Cover { get; init; }`; `ManifestPlan.Build(string type, string? name, long size, IReadOnlyList<BlockEntry> entries, long firstLedger, long lastLedger, int lists, string? cover)`.
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

`StartAsync` (both families) checks `options.Cover` first among the argument checks and puts it, upper-cased, into the new state. The writer is built with `Cover = state.Cover`; `PublishCoreAsync` and `FindManifestAsync` pass it to `ManifestPlan.Build`, so a resume looks for exactly the bytes the writer sends (spec B27). XML docs on `SessionOptions.Cover` and `WriterOptions.Cover` say the caller must have published the cover first from the same writer, below the file's first ledger, as `image/webp` or `image/jpeg` of at most 65,536 bytes (format 4.1), and that the library does not check it.

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

`tools/fixtures/make-media.sh <source.mp4> <source.webp> <platform remux.mjs> <out-dir>` (bash, `set -euo pipefail`), writing into `<out-dir>`:

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

- [ ] **Step 2: Run the media script and check its output**

Run: `bash tools/fixtures/make-media.sh "C:/Users/Evril/Downloads/en-back.mp4" "C:/Users/Evril/AppData/Local/Temp/claude/E--Claude-XrplCSharp-xrpl-encrypted-messenger-59ef6a/d479ab54-9cf5-4a63-b54d-aca137110701/images/15.webp" "E:/GIT/XRPL/xrpl-video-platform/src/Platform.Web/wwwroot/js/remux.mjs" run/fixtures/media`
Expected: thirteen files, every check in the table printed as passing; the source is H.264/AAC 1080×1920, 58 s, so `plain-*.mp4` are about 9.2 MB.

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
- Produces (`messages.mjs`): `elementError(code, sentence) → XrplFileError` with a `sentence` property; `messageFor(error, { playing = false } = {}) → string`; `detailFor(error) → { name: string | null, message: string }`; constants `NO_NODE = 'This network has no built-in node. Set the node attribute.'`, `BAD_NODE = 'The node attribute must be a wss:// address.'`.
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
assert.equal(downloadName({ name: 'con\u0007\u0000.txt', type: 'text/plain' }), 'con.txt');
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
assert.notEqual(messageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 }), { playing: true }),
  messageFor(new XrplFileError('BlockMismatch', 'x', { block: 4 })));  // a playing video does not claim "nothing was shown"
```

Exact `downloadName` rule: drop control characters (U+0000–U+001F, U+007F); replace each of `/ \ : * ? " < > |` with `_`; trim spaces and leading dots; cut to 200 characters keeping the extension; empty → `xrpl-file.<extensionFor(type)>`.

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
- Consumes: `LedgerClient`, `XrplFileReader` (and its `readCover`), `formatLink`, `BLOCK_CHUNKS`.
- Produces: `PRIORITY = { NOW: 0, AHEAD: 1, BACKGROUND: 2 }`.
- Produces: `class ReadQueue { constructor(limit = 3); run(task: (signal) => Promise<T>, { priority = PRIORITY.BACKGROUND, signal } = {}) → Promise<T>; get active(): number; get waiting(): number }` — lower priority value first, first-in-first-out among equals; a queued task whose signal aborts is removed and rejects with a `DOMException` named `AbortError`; a running task gets the signal and may ignore it.
- Produces: `class SharedSource` over a client: `transaction(hash)` shares an in-flight request per hash and keeps validated answers for the page's life (a validated transaction does not change); `earliestLedger()` shares an in-flight request; `accountTransactions(...)` passes through.
- Produces: `class NodePool { constructor(createClient = (endpoints) => new LedgerClient(endpoints)); acquire(endpoints: string[]) → Lease; get size(): number }`; `Lease { source: SharedSource, queue: ReadQueue, files: FileRegistry, release(): void }`. One entry per `endpoints.join(' ')`; `release()` is idempotent per lease; the last release closes the client in a microtask, and an `acquire` of the same set before that microtask runs keeps the client. Module singleton `export const pool = new NodePool()`.
- Produces: `class FileRegistry { open(link) → FileHandle }` keyed by `formatLink(link)`; `FileHandle { link; record(): Promise<Record>; entries(): Promise<Entry[]>; blockBytes(): Promise<number> (BLOCK_CHUNKS × manifest.chunk); block(index, { priority, signal, onChunk }) → Promise<Uint8Array>; cover(): Promise<{ bytes, type, txHash } | null>; release(): void }`. Handles of one link share one manifest request, one block list, one in-flight read per block and the four most recently verified blocks. Block reads go through the lease's queue and `XrplFileReader#block(record, entry, onChunk)` of a reader shared per history key `${writer} ${firstLedger} ${lastLedger}` (its `ForwardHistory` keeps the S26 rule: it answers from a range only when it knows the range is complete for that account). `onChunk` listeners of every caller sharing an in-flight read are called. A failed read is not cached. The last `release()` of a link drops its handle state.

- [ ] **Step 1: Write the failing tests** (fake clients count `close()`; `FakeSource` from `web/test/vectors.mjs` stands in for the client through `createClient`)

```js
test('the same endpoints share one client, closed after the last release', async () => {
  const lease1 = pool.acquire(['wss://a']); const lease2 = pool.acquire(['wss://a']);
  assert.equal(created.length, 1);
  lease1.release(); lease1.release(); await microtask();
  assert.equal(created[0].closed, 0);
  lease2.release(); await microtask();
  assert.equal(created[0].closed, 1);
});
test('a release followed by an acquire in the same tick keeps the client', async () => {   // Review Focus 3 (a moved element)
  const lease = pool.acquire(['wss://a']); lease.release();
  const again = pool.acquire(['wss://a']); await microtask();
  assert.equal(created.length, 1); assert.equal(created[0].closed, 0); again.release();
});
test('different endpoint sets get different clients', …);
test('two handles of one link share the manifest request and each block read', async () => {
  // 2500500 vector: h1, h2 of the same link; Promise.all of block 0..2 on both
  assert.equal(source.transactionCalls, 1);
  assert.equal(walksFrom(source, 1000), 1);           // block 0 walked once
  assert.deepEqual(concat(bytes1), vectorBytes);       // both get the verified file
});
test('releasing one handle mid-read leaves the other\'s read intact', async () => {  // Review Focus 3
  const p = h2.block(1, {}); h1.release();
  assert.deepEqual(await p, expectedBlock1);
});
test('the queue runs at most three tasks and lower priority values first', async () => {
  // six gated tasks: priorities [2,2,2,2,0,1] submitted in that order; active never exceeds 3;
  // after the first three, the start order is 0, 1, then the remaining 2s in submission order
});
test('an aborted queued task rejects with AbortError and never starts', …);
test('a tampered block rejects with BlockMismatch and is read again next time', async () => {
  // block 1 tampered: h.block(1) rejects code BlockMismatch, block === 1; source repaired; h.block(1) resolves
});
test('cover() reads the cover once for every handle of the link', …);
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-page.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `page.mjs`**

- [ ] **Step 4: Run the whole web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Commit**

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
- Produces: `BLOB_LIMIT = 2_147_483_648`; `async saveFile({ handle, record, picker = globalThis.showSaveFilePicker, saveBlob = defaultSaveBlob, onProgress = () => {}, signal }) → 'saved' | 'cancelled'`. With a `picker`: `picker({ suggestedName: downloadName(manifest) })`, `createWritable()`, then each block in order through `handle.block(i, { priority: PRIORITY.NOW, signal })` and `write(bytes)`; any failure calls `writable.abort()` and rethrows; success calls `close()`. A picker rejecting with `AbortError` returns `'cancelled'` before any block read. Without a picker: `size > BLOB_LIMIT` throws `elementError('TooLarge', 'This file is too large to save in this browser.')` before any read; otherwise blocks are collected and `saveBlob(new Blob(parts, { type }), name)` is called (`defaultSaveBlob` clicks a temporary `<a download>` and revokes the object URL after the click).

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
- Create: `web/src/element/load.mjs`, `template.mjs`, `element.mjs`, `define.mjs`
- Create: `web/demo/serve.mjs`, `web/demo/dev.html`
- Test: `web/test/element-load.test.mjs`, `web/test/element-markup.test.mjs`

**Interfaces:**
- Consumes: Tasks 5, 6 and 7.
- Produces (`load.mjs`): `async prepare(handle) → { record, cover }` (manifest and cover, nothing else); `async loadWhole(handle, record, { limitBytes = Infinity, priority, signal, onProgress }) → Uint8Array` (the blocks covering `min(size, limitBytes)`, in order, all verified, concatenated); `progressDetail({ bytesDone, bytesTotal, blocksVerified, blocksTotal })` returns a fresh plain object with exactly those four numbers.
- Produces (`template.mjs`): `TEMPLATE` (constant shadow-root markup with the mockup's styles and empty slots for every state) and `hostStyle({ width, height, ratio }) → string` (the `:host` rule text).
- Produces (`element.mjs`): `class XrplFileElement extends HTMLElement`, `static observedAttributes = ['src', 'width', 'height', 'loading', 'loader', 'node']`. `define.mjs`: `if (!customElements.get('xrpl-file')) customElements.define('xrpl-file', XrplFileElement);`.
- Produces (`web/demo/serve.mjs`): `node web/demo/serve.mjs [port=8790]` serves `web/` on `127.0.0.1` with content types for `.html .js .mjs .json .css .txt`, refuses paths outside `web/` and directory listings, and answers `/fixtures/links.json` from `run/fixtures/links.json` (404 until it exists).

Behaviour the element implements (browser-only; checked in Task 15):
- **Shadow root** `mode: 'open'`, filled once with `TEMPLATE`; dynamic text through `textContent`, images through `src` of blob URLs revoked on reset.
- **Sizing**: a `<style>` in the shadow root holds `hostStyle(...)`: `:host { display: block; position: relative; aspect-ratio: <ratio> }` plus `width`/`height` from `parseLength`. `:host` rules lose to the site's own CSS, which is what makes the attributes presentational defaults. Ratio: `16 / 9`, then the cover's natural size once decoded, then the image's natural size (or video's `videoWidth/videoHeight` in Task 12), set once each. Content uses `object-fit: contain`. Text view keeps the reserved box and scrolls inside it.
- **Loading**: `src` (or text content, read in `connectedCallback`) → `parseLink` → `endpointsFor` → `pool.acquire` → `files.open`. `lazy`: one `IntersectionObserver` with `rootMargin: '100% 0px'` starts the load; a second with no margin marks the element visible, which sets block priority `NOW` (visible) or `BACKGROUND`. `eager`: at once. `click`: `prepare` only, then the idle view with name, size, the cover if any, and an Open (play symbol for video types) button that starts the load. `src` change aborts the current generation and starts over. `disconnectedCallback` aborts and releases (the pool keeps the socket through a same-tick reconnect).
- **Views**: `viewForType` first; image: `loadWhole` then `magicAgrees`, then `<img>`; text: `loadWhole` with `limitBytes: TEXT_LIMIT`, `textPreview`, `<pre>` via `textContent`, a note "Showing the first 1 MB of {size}." and a Download button when truncated; download view: name, type, size, network, Download button (Task 7); mismatch or unsupported type: download view; error: `messageFor`, `::part(message)`.
- **Loader**: spinner, percent, "12.4 of 36.5 MB · 4 of 37 blocks verified", 4 px bar; `role="progressbar"` with `aria-valuenow`; `loader="false"` keeps the box empty. Spinner animation off under `prefers-reduced-motion`.
- **Caption (variant C)**: name · size on the left, "✓ verified · <network>" and a Copy link button (`navigator.clipboard.writeText(link)`, `aria-label="Copy link"`) on the right, the full `xrpl-file://` link as its `title`; shown on `:hover` and `:focus-within`, toggled by a tap when `pointerType === 'touch'`; at the top edge for video so it never covers the player's controls (bottom elsewhere). `part="caption"`.
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
test('a tampered block rejects with its index and returns nothing', …);
test('prepare reads the manifest and the cover and no block of the file', async () => {
  const { record, cover } = await prepare(spyHandle);
  assert.deepEqual(spyHandle.blocksAsked, []); assert.equal(cover.type, 'image/webp');
});

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

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/element-load.test.mjs test/element-markup.test.mjs`
Expected: `load.mjs` not found; the markup test passes vacuously until `element.mjs` exists, then must keep passing.

- [ ] **Step 3: Implement `load.mjs`, `template.mjs`, `element.mjs`, `define.mjs`, `serve.mjs`, `dev.html`**

`dev.html` loads `<script type="module" src="../src/element/define.mjs">` and shows one element per fixture key (`?only=<key>` shows that one alone) from `run/fixtures/links.json`, fetched through `serve.mjs`'s `/fixtures/links.json` route; `?link=<xrpl-file link>` shows any one link, for use before the publication has finished. Nothing in it is a committed link; the committed demo pages come in Task 14.

- [ ] **Step 4: Run the web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Smoke check in the built-in browser pane**

Add a local (uncommitted) `.claude/launch.json` configuration `xrpl-file-demo`: `runtimeExecutable` `node`, `runtimeArgs` `["web/demo/serve.mjs"]`, `port` 8790. Start it with `preview_start`, open `http://127.0.0.1:8790/demo/dev.html`.
Expected: an image fixture goes loader → picture with no console errors; the caption appears on hover; a text fixture shows Cyrillic intact; the console shows no request to any host but the testnet node and `127.0.0.1:8790`.

- [ ] **Step 6: Commit**

```bash
git add web/src/element web/test/element-load.test.mjs web/test/element-markup.test.mjs web/demo/serve.mjs web/demo/dev.html
git commit -m "feat(web): the <xrpl-file> element with image, text, download and error views"
```

### Task 9: ISO BMFF — boxes, container, `moov` anywhere, sample tables, `sidx`

**Files:**
- Create: `web/src/element/media/boxes.mjs`, `container.mjs`, `movie.mjs`, `sidx.mjs` (copied from the platform)
- Create: `web/test/media/make.sh` and the media it writes (committed, each well under 200 KB): `plain-start.mp4`, `plain-end.mp4`, `fragmented.mp4` (ffmpeg fragmenting, no `sidx`), `fragmented-sidx.mp4` (through `tools/fixtures/fragment.mjs`), `hevc.mp4`, `clip.webm`, `clip-live.webm`, `expected.json`
- Test: `web/test/media-boxes.test.mjs`, `media-container.test.mjs`, `media-movie.test.mjs`, `media-sidx.test.mjs`

**Interfaces:**
- Produces (`boxes.mjs`): `readBox(bytes, at) → { type, start, headerSize, size, end } | null` (handles 64-bit `largesize` and size 0 = to the end; null when the header is not all in `bytes`); `topLevelBoxes(bytes, from = 0) → Box[]` (stops at the first incomplete header and reports it as the last element with `partial: true`); `children(bytes, box) → Box[]`; `find(bytes, box, ...path) → Box | null`; `async locateMoov(readBlock: (i) => Promise<Uint8Array>, blockBytes: number, size: number) → { offset: number, bytes: Uint8Array, blocks: number[] }` (walks top-level headers from block 0, jumping over `mdat` by its size to the block holding the next header, then reads the blocks spanning `moov`; `blocks` lists every block it read).
- Produces (`container.mjs`): `detectContainer(head) → 'fmp4' | 'mp4' | 'webm' | 'unknown'` (EBML magic `1A 45 DF A3` with DocType `webm` → `webm`; `moov` with an `mvex` child → `fmp4`; `moov` without one, or `mdat` before any `moov` → `mp4`); `containerAgrees(type, kind) → boolean` (`video/mp4` and `video/quicktime` ↔ `fmp4` or `mp4`; `video/webm` ↔ `webm`).
- Produces (`movie.mjs`): `parseMovie(moovBytes) → Movie` (sample offsets in `stco`/`co64` are already file offsets), `Movie { timescale, duration /* s */, fragmented, tracks: Track[] }`, `Track { id, kind: 'video' | 'audio' | 'other', timescale, codec /* e.g. 'avc1.64001F', 'mp4a.40.2', 'hvc1.1.6.L120.B0' */, width, height, table }` where `table` is null for a fragmented file; `seekPoint(movie, seconds) → { time, offset }` (time = the last video sync sample at or before `seconds`, from `stts` and `stss`; offset = the least file offset, over all tracks, of the first sample whose decode time is at or after that time, from `stsc`, `stco`/`co64`, `stsz`); `mimeFor(movie) → string` (`video/mp4; codecs="<video>,<audio>"`). Duration from `mvhd`, else `mehd`, as the platform's `mp4.mjs` reads it. Codec strings: avcC profile/compat/level hex and esds object type/audio object type as in the platform's `readVideoCodec`/`readAudioCodec`; hvcC per ISO/IEC 14496-15 Annex E (`hvc1.<profile>.<compat flags bit-reversed, hex>.<L|H><level>.<constraint bytes, hex, trailing zero bytes dropped>`).
- Produces (`sidx.mjs`): the platform's `parseSidx(bytes, start)` and `groupAt(groups, seconds)` unchanged, plus `findSidx(bytes) → number | null` (start of a top-level `sidx` after `moov`).

- [ ] **Step 1: Generate the test media**

`web/test/media/make.sh` (bash, ffmpeg): `testsrc2=size=160x90:rate=10` with `sine=frequency=440:sample_rate=48000`, 4 s, `libx264 -g 10 -keyint_min 10 -sc_threshold 0 -pix_fmt yuv420p`, `aac -b:a 32k`: `plain-start.mp4` (`-movflags +faststart`), `plain-end.mp4` (no faststart), `fragmented.mp4` (`-movflags +frag_keyframe+empty_moov+default_base_moof`), `hevc.mp4` (`libx265 -tag:v hvc1`), `clip.webm` (`libvpx-vp9 -g 10`, `libopus`), `clip-live.webm` (same, written to `pipe:1` so it has no Cues and unknown sizes); `fragmented-sidx.mp4` through `tools/fixtures/fragment.mjs` (needs the platform's `remux.mjs` path as `$1`). `expected.json` holds, from `ffprobe -show_packets -of json`, the file offset (`pos`) of the video keyframe at 2.0 s in `plain-start.mp4` and `plain-end.mp4`, and every Cue's cluster offset is checked by the test itself. The binaries are covered by the `.gitattributes` lines from Task 4.

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
test('a 64-bit box size is read', …);   // hand-built bytes: size 1, largesize 2^32 + 16
test('a header cut by the end of the bytes is reported as partial', …);

// media-container
assert.equal(detectContainer(read('plain-start.mp4')), 'mp4');
assert.equal(detectContainer(read('plain-end.mp4')), 'mp4');
assert.equal(detectContainer(read('fragmented.mp4')), 'fmp4');
assert.equal(detectContainer(read('fragmented-sidx.mp4')), 'fmp4');
assert.equal(detectContainer(read('clip.webm')), 'webm');
assert.equal(detectContainer(new TextEncoder().encode('hello world')), 'unknown');
assert.equal(containerAgrees('video/mp4', 'webm'), false); assert.equal(containerAgrees('video/quicktime', 'mp4'), true);

// media-movie
test('a seek lands on the keyframe at or before the target', () => {
  const movie = parseMovie(moovOf('plain-start.mp4'));
  const { time, offset } = seekPoint(movie, 2.5);
  assert.equal(time, 2);
  assert.ok(offset <= expected.plainStartKeyframeAt2);
  assert.ok(isSampleStart(movie, offset));      // some track's sample begins exactly there
});
assert.equal(seekPoint(movie, 0).time, 0); assert.equal(seekPoint(movie, 99).time, 3);
assert.equal(seekPoint(parseMovie(moovOf('plain-end.mp4')), 2.5).time, 2);
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
- Produces: `parseWebmHead(bytes) → { segmentStart, initEnd, timecodeScale, duration /* s, NaN if absent */, cuesOffset /* absolute or null */, codecs: string[], width, height }` — `segmentStart` is the first byte of the Segment's data, `initEnd` the offset of the first Cluster (everything before it is the initialization segment), `cuesOffset` from the SeekHead entry for Cues (`1C 53 BB 6B`) plus `segmentStart`; unknown-size elements are accepted; codecs map `V_VP9`→`vp9`, `V_VP8`→`vp8`, `V_AV1`→`av01.0.08M.08` is out of scope (returns the raw id so `isTypeSupported` refuses), `A_OPUS`→`opus`, `A_VORBIS`→`vorbis`.
- Produces: `async locateCues(readBlock, blockBytes, head) → Uint8Array | null` (reads the block(s) holding the Cues element when `cuesOffset` is set); `parseCues(cuesBytes, segmentStart, timecodeScale) → { time /* s */, offset /* absolute */ }[]` ascending; `cueAt(cues, seconds) → number` (index of the last cue at or before; 0 before the first); `webmMime(head) → string` (`video/webm; codecs="vp9,opus"`).

- [ ] **Step 1: Write the failing tests**

```js
const head = parseWebmHead(read('clip.webm'));
assert.equal(ascii4(read('clip.webm'), head.initEnd), '1F43B675');          // a Cluster starts there
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
test('truncated or foreign bytes are refused with an Error, not a hang', …);
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
- Produces (`mp4box-loader.mjs`): `MP4BOX_INTEGRITY = '__MP4BOX_INTEGRITY__'` (replaced by the build, Task 13); `mp4boxUrl(scriptUrl) → string` (the same directory, file `mp4box.min.js`; a module URL ending in `/src/element/media/mp4box-loader.mjs` maps to `/vendor/mp4box-0.5.4.min.js` for the unbundled dev page); `loadMp4Box({ scriptUrl, integrity = MP4BOX_INTEGRITY }) → Promise<MP4Box>` (one `<script>` per page, `crossOrigin = 'anonymous'`, `integrity` set unless it is still the placeholder; resolves `window.MP4Box`).
- Produces (`feeds.mjs`): `async openFeed({ kind, head, readBlock, blockBytes, size, loadMp4Box }) → Feed`.
  `Feed { tracks: { id, mime }[]; duration: number; seekable: boolean; init(): Promise<Segment[]>; seekTarget(seconds) → { block, offset, time } | null; media(block, bytes, from = block * blockBytes) → Promise<Segment[]>; seek(time): void; close(): void }`, `Segment { track, bytes: Uint8Array }`.
  - `fmp4`: one track `{ id: 0, mime: mimeFor(movie) }`; `init()` = bytes up to the end of `moov` (and `sidx` when present); `media` returns the block bytes from `from` as one segment; `seekTarget` from `groupAt` over `sidx` groups, else null (`seekable` false).
  - `mp4` (plain, also QuickTime): `locateMoov`, then mp4box: `appendBuffer` of `ftyp`+`moov` with their `fileStart`, `onReady` → per track `setSegmentOptions(id, null, { nbSamples: 100 })` and `initializeSegmentation()`; one track per mp4box track with mime `video/mp4; codecs="<codec>"` or `audio/mp4; codecs="<codec>"` from `info.tracks[].codec`; `media` appends `bytes.subarray(from - blockStart)` with `fileStart = from`, collects `onSegment` output and calls `releaseUsedSamples(id, sampleNumber)` for each; `seekTarget` from `seekPoint`, and `seek(time)` calls mp4box `seek(time, true)` so extraction resumes at that keyframe; at the last block it calls `flush()`.
  - `webm`: one track `{ id: 0, mime: webmMime(head) }`; `init()` = bytes up to `initEnd`; `media` as fmp4; `seekTarget` through `locateCues`/`parseCues`/`cueAt`, else null.

- [ ] **Step 1: Write the failing tests** (blocks of 4096 bytes over the test media; `loadMp4Box` is the vm loader)

```js
test('plain MP4 with moov at the end fragments from block reads', async () => {
  const feed = await openFeed({ kind: 'mp4', head: block(0), readBlock, blockBytes: 4096, size, loadMp4Box });
  assert.deepEqual(feed.tracks.map((t) => t.mime.split(';')[0]).sort(), ['audio/mp4', 'video/mp4']);
  const inits = await feed.init(); assert.equal(inits.length, 2);
  let samples = 0; for (let b = 0; b < blocks; b++) samples += countSamples(await feed.media(b, await readBlock(b)));
  assert.equal(samples, totalSamplesFromMoov);
  assert.ok(readsBeforeInit <= movBlocks + 1);       // init needed block 0 and the moov blocks only
});
test('a seek in plain MP4 starts at the keyframe at or before the target', async () => {
  const target = feed.seekTarget(2.5); assert.equal(target.time, 2);
  feed.seek(target.time);
  const first = (await feed.media(target.block, await readBlock(target.block), target.offset)).find((s) => isVideo(s));
  assert.equal(tfdtSeconds(first.bytes, videoTimescale), 2);
});
test('fragmented MP4 with sidx: init ends where the first moof starts, seeks land on a moof', …);
test('fragmented MP4 without sidx is not seekable outside the buffer', () => assert.equal(feed.seekTarget(2), null));
test('WebM with Cues seeks to a cluster; without Cues it does not', …);
test('mp4boxUrl', () => {
  assert.equal(mp4boxUrl('https://cdn.example/1.0.0/xrpl-file-element.js'), 'https://cdn.example/1.0.0/mp4box.min.js');
  assert.equal(mp4boxUrl('http://127.0.0.1:8790/src/element/media/mp4box-loader.mjs'), 'http://127.0.0.1:8790/vendor/mp4box-0.5.4.min.js');
});
```

`countSamples` and `tfdtSeconds` walk `moof/traf/trun` and `tfdt` with Task 9's `boxes.mjs`.

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/media-feeds.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `mp4box-loader.mjs` and `feeds.mjs`**

- [ ] **Step 4: Run the web suite**

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
- Produces (`playback.mjs`): `AHEAD_SECONDS = 30`, `READ_AHEAD = 3`, `KEEP_SECONDS = 120`, `RETRY_DELAYS = [1000, 2000, 4000]`; `retryDelay(error, attempt) → number | null` (`NodeUnreachable` and attempt < 3 → `RETRY_DELAYS[attempt]`; every other error → null); `class ReadAhead { constructor({ blocks, limit = READ_AHEAD }); restart(fromBlock, fromOffset) → generation; next() → { block, generation } | null; completed(block, generation) → number[]; failed(block, generation); get stoppedAt(): number | null; get inFlight(): number }`. `next()` gives the lowest unread block from the frontier while fewer than `limit` are in flight and no block has failed before it; `completed` returns the blocks that are now appendable in file order (a block finished early waits for its predecessors) and `[]` for an old generation; after `failed(k)` nothing at or past `k` is handed out and `stoppedAt` is `k`.
- Produces (`mse.mjs`): `class Streamer { constructor(video, tracks, duration); open(); append(track, bytes) → Promise; abort(); bufferedAhead(time) → seconds; removeFar(time, keep = KEEP_SECONDS); end(); close() }` — `ManagedMediaSource ?? MediaSource`, `segments` mode, `duration` set before the first append, one `SourceBuffer` per track, appends queued per buffer, `QuotaExceededError` → `removeFar(currentTime)` then retry once; `disableRemotePlayback = true` for ManagedMediaSource.
- Produces (`video.mjs`): `class VideoView { constructor({ root, handle, record, cover, priority, onProgress, onError, onLoad }); start(); close() }` (browser-only).

`VideoView` behaviour:
1. Read block 0 at `NOW`; `detectContainer`; `containerAgrees` false → download view; open the feed; any track `isTypeSupported` refuses → download view with the line "This browser cannot play this video here." Show `<video controls playsinline preload="metadata">` with the cover as `poster` (blob URL) and no `autoplay`; set the host ratio from `videoWidth/videoHeight` on `loadedmetadata`.
2. Read-ahead loop: while `streamer.bufferedAhead(currentTime) < AHEAD_SECONDS` and (the video is playing, or playback has not started yet), start `next()` blocks through `handle.block(i, { priority })` (`NOW` for the first block past the playhead, `AHEAD` for the rest); appendable blocks go through `feed.media` to `streamer.append`; the last block ends the stream. A read error asks `retryDelay`; null → `failed(block)`, and when playback reaches the buffered end (`waiting` with `currentTime` within 0.5 s of it) the error overlay shows `messageFor(error, { playing: true })` and `error` fires.
3. `seeking`: target inside a buffered range → nothing; else `feed.seekTarget(currentTime)`; null → set `currentTime` to the end of the buffered range nearest the target; otherwise `streamer.abort()`, `feed.seek(time)`, `restart(target.block, target.offset)` (the queue's waiting reads of the old generation are aborted through their signals), and the first appended segment comes from `target.offset` inside its block.
4. Buffering overlay (the loader with bar, no percent) on `waiting` while a read is in flight, hidden on `playing`/`canplay`; also shown before playback starts while the first blocks load. `xrpl-file-progress` counts verified bytes of this file; `load` on `loadeddata`.
5. `close()`: aborts reads, `streamer.close()`, revokes URLs.

- [ ] **Step 1: Write the failing tests**

```js
test('never more than three reads at once, from the frontier', () => {
  const r = new ReadAhead({ blocks: 10 }); const g = r.restart(0, 0);
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
test('a dropped connection is retried, a verification failure is not', () => {   // Review Focus 4
  const unreachable = new XrplFileError('NodeUnreachable', 'x');
  assert.deepEqual([0, 1, 2, 3].map((a) => retryDelay(unreachable, a)), [1000, 2000, 4000, null]);
  for (const code of ['BlockMismatch', 'ChunkMissing', 'ListMismatch']) assert.equal(retryDelay(new XrplFileError(code, 'x'), 0), null);
  assert.equal(retryDelay(new TypeError('x'), 0), null);
});
```

- [ ] **Step 2: Run to see them fail**

Run: `cd web && node --test test/media-playback.test.mjs`
Expected: module not found.

- [ ] **Step 3: Implement `playback.mjs`, `mse.mjs`, `video.mjs`, and switch `element.mjs` to `VideoView` for the video view**

- [ ] **Step 4: Run the web suite**

Run: `cd web && node --test test/*.test.mjs`
Expected: all pass.

- [ ] **Step 5: Smoke check on `dev.html`** (server from Task 8)

Expected: `mp4-start`, `mp4-end`, `fmp4`, `webm` each show a play button, play while loading, and jump to 0:45 with a short buffering overlay; no console errors.

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
- Produces: `export async function build({ webDir, outDir, version }) → { integrity: { 'xrpl-file-element.js': string, 'mp4box.min.js': string } }`; CLI `node build.mjs` builds `dist/<package version>/`. Output files: `xrpl-file-element.js`, `mp4box.min.js`, `mp4box-LICENSE.txt`, `integrity.json` (`{ "xrpl-file-element.js": "sha384-<base64>", "mp4box.min.js": "sha384-<base64>" }`).

Bundler rules: follow static relative imports from the entry; refuse a bare specifier, a dynamic `import()`, or an import cycle with a message naming the file. Each module becomes `__define('<path>', function (__import, __export) { … })`; `import { a, b as c } from './x.mjs'` → `const { a, b: c } = __import('<path>')`; `import * as ns` → `const ns = __import(…)`; `export function|async function|class|const|let` → declaration kept, name added to one `__export({ … })` at the module's end; `export { a, b as c } from` and `export * as ns from` → re-exports; `import.meta.url` → `__scriptUrl`. The whole is one IIFE in strict mode: `/*! xrpl-file element <version> | MIT */`, `const __scriptUrl = document.currentScript && document.currentScript.src;`, the registry, then `__import('<entry>')`. The placeholder `__MP4BOX_INTEGRITY__` is replaced with the mp4box hash computed first. Module order and output bytes are deterministic. An existing `dist/<version>/` whose files differ from the new build is refused: "dist/<version> exists with other content; raise the version in web/package.json".

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
test('integrity.json holds the SHA-384 of both files, and the element carries mp4box\'s', …);
test('two builds are byte for byte the same', …);
test('a different build into an existing version directory is refused', …);
test('a bare import fails the build naming the file', …);
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
- Create: `web/demo/index.html`, `web/demo/many.html`, `web/demo/snippet.html`, `web/demo/load.js`
- Modify: `README.md`

**Interfaces:**
- Consumes: `run/fixtures/links.json` (Task 4; the publication must have finished), `web/dist/1.0.0/` (Task 13), `web/demo/serve.mjs` (Task 8).

- [ ] **Step 1: Write the demo pages**

- `load.js` (demo only): fetches `../dist/1.0.0/integrity.json` and inserts `<script src="../dist/1.0.0/xrpl-file-element.js" integrity crossorigin="anonymous">`, so the demo exercises SRI and never goes stale against a rebuild.
- `index.html`: one section per state with a heading and the link written out: loading (`large` with `loading="eager"`), image (`picture`, `picture-blocks`), text (`text`, `text-large`, `html`), download (`svg`, `hevc`), video (`mp4-start`, `mp4-end`, `fmp4`, `webm`, `mp4-cover`, `mp4-broken-cover`, `large`), `loading="click"` (`mp4-cover`), `loader="false"` (`picture`), sizing (`width="320"`, `height="50vh"`, a CSS grid of three), errors (`xrpl-file://testnet/` + 64 zeros → ManifestNotFound; `xrpl-file://21337/<hash>` → no built-in node; `xrpl-file://nowhere/x` → InvalidLink). A small event log under the page listens for `load`, `error`, `xrpl-file-progress` on every element.
- `many.html`: before the element script, an inline script wraps `window.WebSocket` to count constructions into `window.socketCount`, shown on the page; then ten elements: seven different fixtures and `picture` three times.
- `snippet.html`: exactly the README's two lines (no integrity, `defer`) and one fixture.
Links come from `run/fixtures/links.json`, written into the HTML literally.

- [ ] **Step 2: Write the README section "Embed a file"** after "Reading a file (browser)"

The two-line snippet (`<script src="https://<host>/1.0.0/xrpl-file-element.js" integrity="sha384-…" crossorigin="anonymous" defer></script>` and `<xrpl-file src="xrpl-file://testnet/…"></xrpl-file>`); one line each on `width`/`height`, `loading`, `loader`, `node`; the CSS custom properties, parts and events; that the integrity value comes from `integrity.json` of the same version; that the host serves both files with `Access-Control-Allow-Origin` (SRI on a cross-origin script); and the CSP a site needs: `script-src` for the script's origin, `connect-src` for the node URLs (`wss://xrplcluster.com wss://s2.ripple.com` on mainnet, `wss://s.altnet.rippletest.net:51233` on testnet), `img-src blob:`, `media-src blob:`. Build: `cd web && npm run build`. In "What is here", add `web/src/element` and drop the element and the demo page from the "planned next" sentence, keeping the rest of it.

- [ ] **Step 3: Check the pages load**

Run the `xrpl-file-demo` server, open `http://127.0.0.1:8790/demo/index.html`, `many.html` and `snippet.html` with `get_page_text`.
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
| 14 | `mp4-cover` | the cover shows as poster before load and sets the proportions; `loading="click"` copy reads only the manifest and cover until clicked |
| 15 | `mp4-broken-cover` | no cover, the video plays as normal, no `error` event |
| 16 | caption | hover and keyboard focus show name · size · ✓ verified · testnet; Copy link puts the `xrpl-file://` link on the clipboard; on video it sits at the top and does not cover the controls; on `mobile`, a tap toggles it |
| 17 | sizing | `width="320"` and `height="50vh"` honoured; a page CSS rule on the element overrides both; the grid of three fills its cells |
| 18 | `loader="false"` | empty box until content |
| 19 | errors | ManifestNotFound, no built-in node ("This network has no built-in node. Set the node attribute."), InvalidLink: one sentence each, `error` detail names in the log |
| 20 | download | Download on `svg` saves `drawing.svg` with the source's SHA-256 (Chromium, picker path) |
| 21 | many.html | `socketCount` is 1 after all ten blocks load; `picture` three times is read once (one `tx` for its manifest in the network log) |
| 22 | hosts | across all pages, requests go only to `s.altnet.rippletest.net` and `127.0.0.1:8790` |
| 23 | snippet.html | the README's two lines work as written |
| 24 | lazy | on index, elements far below the fold have not started (`xrpl-file-progress` absent) until scrolled near |
| 25 | memory | after watching `large` for five minutes, DevTools-free check: `performance.memory.usedJSHeapSize` (Chromium) stays below 300 MB |

- [ ] **Step 2: Write the report** (verdict table, environment: browser and version, node, commit, fixture links, and every Fail with its reproduction)

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/reports/2026-10-29-xrpl-file-element
git commit -m "docs(reports): the element's acceptance on testnet"
```
