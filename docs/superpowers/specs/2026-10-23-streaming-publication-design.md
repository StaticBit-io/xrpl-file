# Streaming publication — design

The application refuses any file above 64 MiB (`PublishModel.MaxFileBytes`). A person tried to
publish a Full HD video of 1,689,809,534 bytes and was told only that. The limit exists because the
whole publication path holds the file, and much more than the file, in memory:

- `PublishModel.BuildRequestAsync` reads the file with `File.ReadAllBytesAsync`.
- `FileTree.Split` copies it into one `byte[]` per chunk, and the session and the writer each split
  it again.
- The writer builds every chunk's `MemoSubmission` up front, at about 4 KB of UTF-16 hex per
  1000-byte chunk, which is about 7 GB for this file.
- A Batch round plans every pending memo at once and keeps every candidate's signed blob and every
  history record with its memo data until it ends.
- Resume reads and hashes the file twice in the application and once more in the library.
- At "done" the state file writes a dictionary of every chunk hash, about 170 MB of JSON for this file.

This document removes the limit for Batch publications and makes the path stream from and to disk.
It also replaces the full read-back with a sampled one, prices time as well as money before a run,
asks for an explicit confirmation above a threshold, and keeps the computer awake during a run.

The decisions were taken with the person on 2026-10-23:

- No size limit for Batch.
- A cost-and-time summary with a checkbox above one hour or 1 XRP.
- One sampled verification rule for every size.
- The computer stays awake and the vault stays unlocked by its timer while a run works.
- The ticket path keeps its 64 MiB limit.

## 1. What is known

From a survey of `main` at `b794067`:

| Place | What it holds today |
|---|---|
| `PublishRequest.File` | the whole file as `ReadOnlyMemory<byte>`, read by `PublishModel.BuildRequestAsync` and by `UnfinishedModel` on resume |
| `FileTree.Split` | a second full copy as `byte[]` per chunk. It is called by `PublicationSession` at start and on resume, and by `RootOf` and `XrplFileWriter` |
| `XrplFileWriter.PublishCoreAsync` | `chunkMemos` for every chunk; `chunkByIndex`, `chunkLedgers` and `chunkHashes` as dictionaries and lists of strings |
| `ISubmissionStrategy.SubmitAllAsync` | `IReadOnlyList<MemoSubmission>` with every memo of a pass |
| `BatchStrategy` | `pending` (every index); `landed` and `candidates` dictionaries; one `BatchRound` over all of `pending` |
| `BatchRegistry` | every candidate's `SignedTransaction.TxBlob`, about 18 KB per 8-inner batch, until the round ends |
| `BatchRound._writerHistory`, `BatchSweep.FinalAsync` | every writer record with its `MemoDataHex`, kept for the final sweep's reuse |
| `PublicationSession.Reconcile` | `LandedMemo(TxHash, LedgerIndex, MemoDataHex)` per landed chunk, and the whole history since `StartedInLedger` as one list |
| `PublicationState.ChunkHashes` | every chunk hash, written at "done" |
| `PublicationReport.ChunkHashes`, `ListHashes` | every hash |

What already works and stays as it is:

- Hashing is per chunk and per block. `MerkleTree.Leaf`, `FileTree.BlockHash` over one block's
  1,024 chunks, and `FileTree.RootHex` over block hashes need no whole-file array.
- The reader's block API (`ManifestAsync`, `BlockListAsync`, `BlockAsync`) is per block. The Read
  screen already writes each verified block to a temporary file and saves by stream.
- `VaultSigner.SignAsync` touches the auto-lock on every signature, and
  `PublicationRun.Observe` touches it on every progress report.

What is absent:

- A ledger-hash read on `ILedgerSource`.
- Any sleep prevention.
- A duration in the estimate.

Constants: a chunk is 1,000 bytes (`MemoLayout.ChunkBytes`), a block is 1,024 chunks
(`FileTree.BlockChunks`), and a block-list memo carries 982 bytes of JSON. The 1,689,809,534-byte
file has 1,689,810 chunks and 1,651 blocks.

## 2. Scope

**In:**
- A publication source that reads chunks from disk.
- Memos built on demand.
- Bounded Batch rounds.
- Compact landed records.
- No per-chunk data in the state file or the report.
- Streaming root computation on start and on resume.
- Sampled verification after writing.
- Duration in the estimate.
- Confirmation above a threshold.
- Sleep prevention and auto-lock activity during a run.
- No reader limit on the application's Read screen.

**Out:**
- The ticket and sequence paths beyond keeping their 64 MiB limit.
- The browser reader: it still assembles a file in memory. The `<xrpl-file>` web component is its
  own design, next.
- Playing a video while it downloads.
- Publishing from a source that is not a file on disk in the application.
- Mainnet measurements.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| S1 | The application publishes any size through Batch. Without a payer (tickets or sequence) the 64 MiB limit stays. A larger file without a payer is refused with "Choose a payer — files larger than 64 MiB publish through Batch." | The person removed the limit for Batch and said the ticket path does not matter. A ticket run of a gigabyte would hold about 50 XRP for hours, and its sweep rereads history quadratically. Keeping its limit avoids rebuilding a path nobody will use for large files |
| S2 | Core gains `IPublicationSource`: `long Length`, `int ReadChunk(long index, Span<byte> destination)`, `ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken)`. `FilePublicationSource` reads with `RandomAccess` from a `SafeFileHandle` opened with `FileShare.Read` for the whole run, so no writer can change the file mid-run. `MemoryPublicationSource` wraps `ReadOnlyMemory<byte>` for the library API and tests | Positional reads are thread-safe and need no shared stream position. A file held open without write sharing cannot be modified under the run, and the root check on resume (S7) covers a file changed between runs |
| S3 | The Merkle root and the block hashes are computed in one forward pass over the source, block by block, keeping only the 32-byte hash of each block. `ManifestPlan.Entries` takes block hashes and per-block first ledgers, not chunks | 1,651 block hashes are 53 KB. Leaf hashing is per chunk already |
| S4 | A pass's memos are a lazy `IReadOnlyList<MemoSubmission>`: element *i* is built from the source when it is read and is not cached. The list is the same type strategies take today | Every strategy indexes and enumerates the list. A lazy list changes no strategy signature and makes "build all memos up front" impossible by construction. A chunk read from a local disk is microseconds against a submit round trip of hundreds of milliseconds |
| S5 | A Batch round takes at most `MaxRoundMemos` = 16,384 pending memos (2,048 batches); a pass with more runs as a series of rounds. A round's registry, history and sweep state are released when the round ends | That bounds the signed blobs to about 37 MB and the round's history to 16,384 compact records. The fence, deadlines, identity and coverage rules (spec 2026-10-15, B5–B30) are per round and unchanged |
| S6 | A history record is checked against the source the moment a sweep reads it (marker and data, spec 2026-10-12 I12). After that only a compact record is kept: hash (32 bytes), ledger, sequence, ticket sequence, transaction type code, result code, parent-batch hash, memo index. Landed memos of a pass are kept in arrays indexed by memo: hash and ledger, about 40 bytes per memo | Identity (B12) and coverage (B30) need no memo data, and the data check needs it only once. 1.69 million memos are about 68 MB of arrays, against gigabytes of strings |
| S7 | Resume computes the root of the file at `SourcePath` (or a picked file) in one streaming pass, compares size and root, and reconciles from history with the compact records of S6. The application no longer reads the file into memory anywhere | Today's three in-memory reads of the file are what limits resume. The root check is what makes reusing a path safe (spec 2026-10-15, B17) |
| S8 | The state file carries no per-chunk data. `ChunkHashes` is no longer written: version 3 drops it, and version 2 stops writing it while still reading it. `PublicationReport` replaces `ChunkHashes` and `ListHashes` with `ChunkCount`, `ListCount` and the manifest hash, plus a `Landed` accessor over the compact arrays for library callers that need a chunk's hash | Nothing in the application reads the per-chunk hashes; the link is the manifest hash. 170 MB of JSON per save is what a gigabyte publication would write. The library is not published yet, so its API can change now |
| S9 | Verification after writing is sampled, one rule for every size. It checks the manifest, the full block list (every entry, rooted to the manifest root), and the hash of blocks {0, last, r₁, r₂}. r₁ and r₂ are chosen by SHA-256 over the ledger hash of the ledger holding the last block-list memo (or the last chunk, when there is one block) concatenated with the root, read in counter mode and reduced modulo the block count, skipping duplicates. A file of 4 blocks or fewer has all of them checked | The final sweep already checked every chunk's bytes against the source (S6). The read-back answers a different question: does a reader assemble the file from the link? That needs the structure plus a sample. A ledger hash is outside the writer's control and recomputable by anyone, so the sample cannot be steered. Verification costs a few megabytes whatever the size |
| S10 | `ILedgerSource` gains `LedgerHashAsync(long ledgerIndex, CancellationToken)`: the `ledger_hash` of a validated ledger, through the `ledger` method | The sample's seed (S9). The verification runs before the manifest is sent, so the manifest's own ledger is not available |
| S11 | The estimate includes a duration: writing at 70 memos/s (the existing prior), plus one settle per pass (`RunModel.SettlePerPass`), plus verification at the measured reader rate of about 0.7 s per 400 records for the blocks it reads. The Estimate screen shows it as a range, rounded up | The person decides with the time in front of them. The prior is conservative against the measured 82 chunks/s |
| S12 | When the estimate's duration exceeds 1 hour or its payer worst case exceeds 1 XRP, the Estimate screen shows a summary: duration, transactions, the payer's and the writer's required balance, and fees in the worst case. Below it is a checkbox, "I understand the cost and the time". Publish stays disabled until it is ticked. Smaller publications are unchanged | The person chose a threshold and an explicit confirmation over a dialog or none |
| S13 | While a publication or a resume runs, the application asks Windows to stay awake with `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` through an `IKeepAwake` service. App.Core declares it, the Windows head implements it, and it is released on every exit path. The display may sleep | A run of hours must survive an idle system's sleep. The request is the documented per-thread mechanism and ends with the process |
| S14 | The run's own work counts as vault activity throughout, including the preparation stages: streaming the root, reading history on resume, and the fence wait. Those stages report progress at least every 10 seconds, and progress already touches the auto-lock. A manual Lock still stops the run, resumable | Signing already touches the lock, but the preparation of a gigabyte resume can outlast the 15-minute idle timeout without a report. The person chose to keep the vault unlocked by its timer, not against their own Lock |
| S15 | The application's Read screen reads any size: it no longer applies `ReaderOptions.MaxBytes`. The library's default stays. Before downloading, the screen shows the manifest's size and a duration at the measured reader rate | The Read screen already streams to disk. The library default protects library callers that assemble in memory |
| S16 | `PublishModel.MaxFileBytes` becomes `TicketPathMaxBytes`, applied only when no payer is chosen, and the size warning against the reader default (`ReaderDefaultMaxBytes`) is dropped from the Publish screen | S1. The reader-default warning was about readers that assemble in memory, and the application's own reader no longer does |

## 4. The library

### 4.1 The source

```csharp
public interface IPublicationSource : IAsyncDisposable
{
    long Length { get; }
    int ReadChunk(long index, Span<byte> destination);
    ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken);
}

public sealed class FilePublicationSource : IPublicationSource   // RandomAccess over a SafeFileHandle, FileShare.Read
public sealed class MemoryPublicationSource : IPublicationSource // over ReadOnlyMemory<byte>
```

- A chunk *i* is bytes [i × 1000, min((i + 1) × 1000, Length)).
- A read past the end, or a short read before the end, throws `IOException`. A file that shrank
  under the run is a failure, not a short chunk.
- `PublicationSession.StartAsync` and `ResumeAsync` take an `IPublicationSource` in place of
  `ReadOnlyMemory<byte>`. Overloads that take `ReadOnlyMemory<byte>` wrap it in a
  `MemoryPublicationSource`, so existing library callers compile.
- `PublicationSession.RootOf` gains an `IPublicationSource` overload and is asynchronous for files.

### 4.2 Tree and plan

- `FileTree.BlockHashesAsync(IPublicationSource, CancellationToken, IProgress<long>?)` returns the
  block hashes in one pass, reporting chunks hashed.
- `FileTree.RootHex` already works over block hashes.
- `ManifestPlan.Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers)`
  replaces the chunk-taking overload.
- `FileTree.Split` remains for small in-memory use and tests only; nothing on the publication path
  calls it.

### 4.3 Writer

- `XrplFileWriter.PublishAsync` takes `IPublicationSource`. The chunk pass's memos are a
  `LazyMemoList(source, indices)`: indices are the chunks not already landed, held as an
  `int[]` or as ranges.
- Landed results come back from the strategy as compact arrays (S6). Each block's first ledger
  is the minimum over its chunks, folded as the results arrive, not over a dictionary.
- The list pass and the manifest are unchanged; they are small.
- The read-back stage becomes the sampled verification of S9. Its progress stage name stays
  "read-back", and its count is the number of blocks it reads.

### 4.4 Strategies

- `ISubmissionStrategy.SubmitAllAsync` keeps its signature. Its result type gains a compact form:
  `SubmittedMemos` with `Count`, `HashOf(int)` and `LedgerOf(int)`. It replaces
  `IReadOnlyList<SubmittedMemo>`, and the writer is its only consumer.
- **BatchStrategy:**
  - Rounds take at most `MaxRoundMemos` (S5).
  - `pending` is an index structure, not a `List<int>` of every memo.
  - The registry, the history and the sweep reuse cache live and die with the round.
  - A sweep record is checked and compacted on read (S6).
  - `RequireDistinctMarkers` checks the markers a pass generates by construction. Chunk markers are
    `xf/c/<index>`, so distinct indices imply distinct markers, and the check needs no set of all
    markers.
- **TicketStrategy and SequenceStrategy** adapt to the new result type. Their memory is not
  reworked (S1).

### 4.5 Session and state

- `PublicationSession` holds the source, not bytes. `Reconcile` streams history and keeps compact
  records (S6, S7).
- `PublicationState`:
  - **Version 3:** `ChunkHashes` is gone. A version-3 file that carries it is still read, and the
    field is ignored.
  - **Version 2:** `ChunkHashes` is still read and no longer written.
- `PublicationReport`: `ChunkCount`, `ListCount` and `ManifestHash`, plus `Landed` (S8).

### 4.6 Ledger hash

`LedgerHashAsync(long ledgerIndex, CancellationToken)` on `ILedgerSource` asks `ledger` with
`ledger_index` set to that index and `validated` set to true, and returns `ledger_hash`. Any
failure is `NodeUnreachable`.

## 5. The application

1. **Publish.** No file bytes are read when a file is picked.
   - `PublishRequest` carries `SourcePath` and `Length`, not bytes.
   - `PublishModel` refuses only per S1/S16.
   - `PublicationRun` opens a `FilePublicationSource` for the run and disposes it on every exit path.
2. **Estimate.** Shows the duration (S11). Above the threshold it shows the summary and the
   checkbox (S12). `EstimateModel` owns the threshold and the checkbox state. Both are tested.
3. **Run.**
   - `IKeepAwake` is held from start to end (S13).
   - The preparation stages report progress (S14): "Hashing the file — 412,000 of 1,689,810" and
     "Reading the ledger history".
   - The time left uses the same rate model as today.
4. **Unfinished and resume.** Uses `FilePublicationSource` and the streaming root (S7). There is no
   `File.ReadAllBytesAsync` left in the application.
5. **Read.** No size ceiling. The size and a duration are shown before downloading (S15).
6. **Windows head.** `KeepAwake` calls `SetThreadExecutionState` from the thread that holds the
   request, and is registered in `MauiProgram`.

## 6. Testing

1. **Core unit:**
   - `FilePublicationSource`: chunk boundaries, the last short chunk, a read past the end, a file
     truncated under the source, write sharing denied.
   - Streaming root equals the in-memory root for sizes 0 < n ≤ 3 blocks and odd tails.
   - `LazyMemoList` builds nothing until read.
   - `BatchStrategy` with `MaxRoundMemos` = 64 over 300 memos: rounds split, every memo lands once,
     each round's registry is released.
   - Compact records: identity and coverage hold; the data check rejects a record whose bytes differ.
   - `SubmittedMemos` arrays.
   - Sampled verification: block choice from a fixed ledger hash and root; all blocks when there are
     4 or fewer; a corrupted sampled block fails; a corrupted list entry fails.
   - `LedgerHashAsync` parsing.
   - State: version 3 without `ChunkHashes`, version 2 read with them.
2. **A memory bound test.** On the fake ledger, publish a source of 200 MB generated on the fly (a
   deterministic pseudo-random source, not a file). Assert that managed memory after a forced
   collection stays under a fixed bound, say 400 MB, through the run, and that the result
   verifies. Mark it `Category=Slow` so the default unit run can skip it.
3. **Core on the stand (`xrpl-file-ci`):**
   - 50 MB through Batch with `MaxRoundMemos` at its default, then read back by `XrplFileReader`.
   - Resume of a stopped 50 MB run from its file path.
4. **App.Core:**
   - Policy S1/S16: the size limit with and without a payer.
   - The duration and the threshold (S11, S12) and the disabled Publish without the checkbox.
   - Resume without loading bytes: a fake file system counts reads.
   - `IKeepAwake` taken and released on success, failure and cancellation.
   - Preparation progress touching the auto-lock.
   - Read without a ceiling.
5. **Acceptance on testnet in the MauiLab VM:** a file of about 300 MB (a video).
   - The summary and checkbox appear.
   - The run's working set stays bounded (sample the process's private bytes).
   - The VM does not sleep with a short sleep timeout set.
   - The link reads back in the application.
   - The time is recorded against the estimate.

   A full 1.7 GB run is not part of acceptance: it costs about 35 XRP of testnet funds from the
   faucet, and the 300 MB run exercises every path.

## 7. What this does not do

- It does not make the browser reader stream. The web component is designed next, separately.
- It does not lift the ticket path's limit.
- It does not play a video before it has fully downloaded.
- It does not measure mainnet. A gigabyte publication there is about 1.7 million transactions in
  the shared network, at the payer's cost.
