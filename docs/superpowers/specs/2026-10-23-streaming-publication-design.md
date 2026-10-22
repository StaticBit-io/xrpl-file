# Streaming publication — design

The application refuses any file above 64 MiB (`PublishModel.MaxFileBytes`). A person tried to
publish a Full HD video of 1,689,809,534 bytes and was told only that. The limit exists because the
whole publication path holds the file, and much more than the file, in memory:

- `PublishModel.BuildRequestAsync` reads the file with `File.ReadAllBytesAsync`.
- `FileTree.Split` copies it into one `byte[]` per chunk, and the session and the writer each split
  it again.
- The writer builds every chunk's `MemoSubmission` up front: about 4 KB of UTF-16 hex per
  1000-byte chunk, about 7 GB for this file.
- A Batch round plans every pending memo at once. It keeps every candidate's signed blob, and every
  history record with its memo data, until it ends.
- Resume reads and hashes the file twice in the application and once more in the library.
- At "done" the state file writes a dictionary of every chunk hash.

This document removes the limit for Batch publications and makes the path stream from and to disk.
It also:

- replaces the full read-back with a sampled one on the paths that already check every chunk's
  bytes;
- prices time as well as money before a run;
- asks for an explicit confirmation, bound to what the run may spend;
- keeps the computer awake and the vault unlocked by its timer during a run.

The decisions were taken with the person on 2026-10-23:

- No size limit for Batch.
- A cost-and-time summary with a checkbox above one hour or 1 XRP.
- One sampled verification rule for every size.
- The computer stays awake and the vault stays unlocked by its timer while a run works.
- The ticket path keeps its 64 MiB limit.

**Revised the same day after a review by Codex.** The review found that:

- sampling alone missed block reachability and copies that conflict;
- the sequence path has no byte check to stand on;
- the compact record lacked fields that B29 and B30 need;
- list and manifest reconciliation, and several other memory holders, were unaccounted for;
- the confirmation was not bound to the run's budget, and resume bypassed it;
- the keep-awake request is thread-affine.

Decisions S5, S6, S8, S9, S11–S14 and S17–S27, and sections 4–6, carry the changes.

## 1. What is known

From a survey of `main` at `b794067`:

| Place | What it holds today |
|---|---|
| `PublishRequest.File` | the whole file as `ReadOnlyMemory<byte>`, read by `PublishModel.BuildRequestAsync` and by `UnfinishedModel` on resume |
| `FileTree.Split` | a second full copy as `byte[]` per chunk; called by `PublicationSession` at start and on resume, by `RootOf` and by `XrplFileWriter` |
| `XrplFileWriter.PublishCoreAsync` | `chunkMemos` for every chunk; `chunkByIndex`, `chunkLedgers` and `chunkHashes` as dictionaries and lists of strings |
| `ISubmissionStrategy.SubmitAllAsync` | `IReadOnlyList<MemoSubmission>` with every memo of a pass |
| `BatchStrategy` | `pending` (every index), and `landed` and `candidates` dictionaries; one `BatchRound` over all of `pending` |
| `BatchRegistry` | every candidate's `SignedTransaction.TxBlob`, about 18 KB per 8-inner batch, until the round ends |
| `BatchRound._writerHistory`, `BatchSweep.FinalAsync` | every writer record with its `MemoDataHex` |
| `BatchProgress._found` | a pass-wide `HashSet<int>` |
| `BatchTrace._entries`, `PublicationRun.WriteTraceAsync` | session-wide trace strings, copied again to format the log |
| `PublicationSession.Reconcile`, `CompleteHistoryAsync`, `HistoryAsync` | the whole history since `StartedInLedger` as one list with memo data, and `LandedMemo(TxHash, LedgerIndex, MemoDataHex)` per landed chunk; resume coverage builds a `HashSet<uint>` over the sequence interval |
| `PublicationSession.FindManifest` | a chunk-ledger dictionary and the full hash list |
| `PublicationState.ChunkHashes` | every chunk hash, written at "done" |
| `PublicationReport.ChunkHashes`/`ListHashes`, `PublishResult` | every hash, as strings |
| `ForwardHistory` (reader) | up to 4,096 records with memo data, but it never evicts the ledger it is walking, so one ledger with more records exceeds the cap |

What already works and stays:

- Hashing is per chunk and per block: `MerkleTree.Leaf`, `FileTree.BlockHash` (one block's 1,024
  chunks), and `FileTree.RootHex` (block hashes).
- The reader's block API (`ManifestAsync`, `BlockListAsync`, `BlockAsync`) works per block.
- The Read screen writes each verified block to a temporary file and saves by stream.
- `VaultSigner.SignAsync` and `PublicationRun.Observe` touch the auto-lock.

Two facts the design has to respect:

- **The block list proves less than it looks.** `FileTree.RootHex` hashes block hashes, not each
  entry's `FirstLedger`. So a list that roots correctly does not prove that a block's chunks are
  reachable from its first ledger.
- **The reader reads the first copy, not the right one.** It takes the first decodable, size-eligible
  copy of a chunk in canonical order, starting at the block's first ledger. Reconciliation adopts a
  matching copy even when a conflicting one comes first.

Absent today: a ledger-hash read on `ILedgerSource`, any sleep prevention, a duration in the
estimate.

Constants: a chunk is 1,000 bytes (`MemoLayout.ChunkBytes`); a block is 1,024 chunks
(`FileTree.BlockChunks`); a block-list memo carries 982 bytes of JSON. The 1,689,809,534-byte file
has 1,689,810 chunks and 1,651 blocks.

## 2. Scope

**In:**
- A publication source that reads chunks from disk.
- Memos built on demand.
- Bounded Batch rounds.
- Compact records and compact tracking.
- No per-chunk data in the state file, the report or the result.
- Streaming root on start and resume.
- Streaming reconciliation.
- A bounded reader history.
- Deterministic reachability checks plus sampled verification.
- Duration in the estimate.
- A confirmation bound to the run's budget, on start and on resume.
- Keep-awake.
- An activity lease on the auto-lock.
- Manual Lock stopping a run.
- No reader limit on the application's Read screen.

**Out:**
- Ticket- and sequence-path memory: they keep their 64 MiB limit.
- The browser reader, which still assembles in memory. The `<xrpl-file>` web component is designed
  next, separately.
- Playing a video while it downloads.
- Network file systems beyond what Windows' sharing modes give.
- Mainnet measurements.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| S1 | The application publishes any size up to `MaxPublicationBytes` = 1 TiB through Batch. Without a payer (tickets or sequence), the 64 MiB limit stays. A larger file without a payer is refused with "Choose a payer — files larger than 64 MiB publish through Batch." | The person removed the limit for Batch and said the ticket path does not matter. The 1 TiB ceiling keeps every per-memo index an `int`. A ticket run of a gigabyte would hold about 50 XRP for hours, and its sweep rereads history quadratically |
| S2 | Core gains `IPublicationSource`: `long Length`, `int ReadChunk(long index, Span<byte> destination)`, `ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken)`. Reads fill the destination exactly, retrying partial reads, and throw `IOException` on a premature end. Offsets are computed with checked arithmetic. `FilePublicationSource` opens one `SafeFileHandle` with `FileShare.Read`, rejects a non-seekable handle, and keeps that handle from hashing through the end of the run. `MemoryPublicationSource` wraps `ReadOnlyMemory<byte>`. The caller owns the source and disposes it; a session borrows it | Positional reads on one handle are thread-safe. On local Windows file systems a handle that denies write and delete sharing blocks writes, deletes and replace-by-rename for its lifetime. Other file systems get what their driver gives; the root check on resume (S7) and the bound approval (S24) cover the rest |
| S3 | The Merkle root and the block hashes are computed in one forward pass, keeping only each block's 32-byte hash. `ManifestPlan.Entries` takes block hashes and per-block first ledgers | 1,651 block hashes are 53 KB |
| S4 | A pass's memos are a lazy `IReadOnlyList<MemoSubmission>`: element *i* is built from the source when read and never cached. Every consumer reads it by index or in slices; no code materializes it (enforced by review and by the memory test, S27) | A chunk read from a local disk costs microseconds against a submit round trip of hundreds of milliseconds |
| S5 | A Batch round takes at most `MaxRoundMemos` = 16,384 pending memos, which is 2,048 batches; a pass with more runs as a series of rounds. Each round's registry, history and sweep cache are released when the round ends. **The session-wide state lives in `BatchContext` across rounds, as now:** the persisted fence (B23), candidate counts per memo (B13), committed spending (B26), the ledger floor (B6 amendment) and the trace. Between two normal rounds, B9 resolution and B30 coverage of the finished round are the barrier; the next round does not wait for the persisted fence | It bounds the signed blobs to about 37 MB and a round's history to 16,384 compact records. Resetting any session-wide state at a round boundary would let a memo get a fourth candidate, or let a lagging node reuse consumed sequences |
| S6 | A history record is checked the moment a sweep reads it, then kept only as a compact record. **Compact record:** hash, account, ledger, transaction index in the ledger, validated, result code (`tes`/`tec`/other), transaction type, `Sequence`, `TicketSequence`, `TicketCount`, `Fee`, `ParentBatchID`. It also keeps the memo marker's parsed kind and index, and a data verdict for the chunk it names: matches the source, conflicts, or not a chunk. Records of any verdict take part in identity (B12), coverage (B30) and fee attribution (B29). Only chunk memo data is dropped after the verdict. List and manifest memo data is kept, because it is bounded by the block count and needed by S20 | Identity, coverage and fees need these fields and no chunk data. A record that conflicts with the source still consumed a sequence and may be someone else's |
| S7 | Resume computes the root of the file at `SourcePath` (or a picked file) in one streaming pass on the run's own handle, compares size and root, then reconciles with streaming history (S20). The application never reads the file into memory | Today's three in-memory reads of the file are what limits resume |
| S8 | No per-chunk data in persisted or returned objects. State: version 3 drops `ChunkHashes` (still read, ignored, if present); version 2 still reads it and no longer writes it. `PublicationReport` and `PublishResult` replace their hash lists with `ChunkCount`, `ListCount` and `ManifestHash`. A `Landed` accessor (`HashOf(int)`, `LedgerOf(int)`) reads the run's compact arrays while the report object lives. `FindManifest` builds its expected manifest from block hashes and block first ledgers, not from a chunk dictionary | Nothing in the application reads the per-chunk hashes; the link is the manifest hash. The library is unpublished, so its API can change now |
| S9 | **Verification after writing** has two parts. (a) **Deterministic, from the run's own compact data, every block:** every chunk's adopted copy lies at or after its block's `FirstLedger` and within the manifest window. No copy of a block's chunk that conflicts with the source (S17) lies in [`FirstLedger`, adopted copy] in canonical order. The block list's entries equal the plan, ledgers included. (b) **Sampled read through the reader**, for paths whose strategy checked every chunk's bytes on landing (Batch, tickets): the full block list rooted to the manifest root, plus the blocks of S21 read with `BlockAsync`. The sequence path (`SequenceStrategy`, small files only, S1) keeps the full read-back | The root does not cover `FirstLedger`, so (a) is what proves reachability, and it costs no reads. (b) proves that the reader assembles the file through its own code. The sequence path checks no bytes when memos land, so sampling has nothing to stand on there |
| S10 | `ILedgerSource` gains `LedgerHashAsync(long ledgerIndex, CancellationToken)`: `ledger` with that `ledger_index`. It returns `ledger_hash` only when the answer says `validated: true` and carries the requested index; anything else is `NodeUnreachable` | The sample's seed (S21) |
| S11 | The estimate includes a duration as a range. It counts writing at 70 memos/s (the existing prior, low end) and 85 memos/s (high end), plus one settle per **round** (S5) and per pass at `LedgerTiming.ApproximateLedgerInterval × 3`, plus verification. Verification is estimated from the planned traversal (S21), not from bytes. The estimator groups memos per round as the strategy will: rounds of `MaxRoundMemos`, 8 + 1 becoming 7 + 2 within a round, and a round with one memo as a solo. Fees, transaction counts and the solo fees on the writer follow the same grouping | 104 chunk rounds for the 1.7 GB file are 104 settles and final sweeps. Grouping per pass misplaces a fee on the wrong account |
| S12 | When the estimate's longer duration exceeds 1 hour or its payer worst case exceeds 1 XRP, the Estimate screen shows a summary below the figures: duration range, transactions, each account's required balance, fees in the worst case. Then a checkbox, "I understand the cost and the time", and Publish stays disabled until it is ticked. The checkbox resets whenever the file, a key, the payer, the network or the estimate changes | The person chose a threshold and an explicit confirmation |
| S13 | Keep-awake is held by a dedicated background thread that the `IKeepAwake` implementation owns. Acquire starts the thread, which calls `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)`, checks its return value and waits on an event. Release signals the event; the same thread clears the state and exits. A failed call is logged and does not fail the run. Held from the moment a run or a resume starts preparing until it ends by any path | The state belongs to the calling thread. With `ConfigureAwait(false)` and pool threads, acquire and release land on different threads, or the holder thread exits mid-run. A dedicated thread is the only stable owner |
| S14 | **Activity lease.** While a run or resume is active, from the first preparation step to the end, a timer touches the auto-lock every minute, independently of progress reports. The lease ends on success, failure and cancellation. **Manual Lock stops the run:** the shell's Lock action cancels an active run (`PublicationRun.StopAsync`) before it locks, so the run ends resumable instead of finding the vault unlocked again later | Progress reports do not cover long awaits: a sparse block walk, a history page, a file read. The person chose to keep the vault unlocked by its timer but not against their own Lock, and today a lock followed by an unlock lets the signer continue |
| S15 | The application's Read screen reads any size. It no longer applies `ReaderOptions.MaxBytes`; the library's default stays. Before downloading, the screen shows the manifest's size and a duration at the measured reader rate | The Read screen already streams to disk. The library default protects library callers that assemble in memory |
| S16 | `PublishModel.MaxFileBytes` becomes `TicketPathMaxBytes`, applied only when no payer is chosen; `MaxPublicationBytes` (S1) applies with one. The Publish screen drops the warning about the reader default | S1. The application's reader no longer has that default |
| S17 | **Conflicting copies.** A conflicting copy is a validated, successful writer record with a chunk marker whose data differs from the source. When a block has one in canonical order at or after its `FirstLedger` and before the adopted copy of the same chunk, that block is re-sent whole in a later round, and its adopted copies are discarded. The re-sent block's `FirstLedger` is then past the conflict. Identical duplicates stay harmless | A reader takes the first eligible copy, so a conflict ahead of the good copy breaks the block whatever the sweep adopted. Re-sending the whole block moves its first ledger past the conflict. Re-sending one chunk would not: the bad copy would still come first |
| S18 | The trace is bounded. `BatchTrace` keeps a ring of the last 10,000 entries in memory and appends every entry to the trace log file as it is produced, through the bounded writer of `26e37fb`. `PublicationRun.WriteTraceAsync` no longer copies the trace | A session-wide list of strings grows with the run, and formatting it copied it again |
| S19 | Compact tracking: candidate counts per memo are a `byte[]` per pass; progress is a bit set per pass; missing indices are an `int[]` or ranges; the landed hash and ledger are two arrays per pass (32 bytes and 8 bytes per memo). At most one copy of each lives at a time, owned by the pass and handed to the report by reference | About 41 bytes per memo: about 70 MB for the 1.7 GB file in the chunk pass, against gigabytes today |
| S20 | Reconciliation streams history page by page. Chunk records are compacted per S6. List and manifest records keep their data, with last-copy semantics for lists as today. Resume coverage uses sorted sequence ranges, not a `HashSet` over the interval. A B30 retry discards the partial result and restarts the stream, so one consistent result comes out. `FindManifest` (B27) runs after it on the kept list and manifest data | A list's expected bytes depend on the final chunk ledgers, and the manifest's on both, so they must be kept to recognise a finished run. They are bounded by the block count: about 100 KB for 1,651 blocks |
| S21 | **The sample.** One block is checked alone. Four or fewer blocks are all checked. Otherwise blocks 0, n−1, r₁ and r₂ are checked. **The seed:** the validated ledger hash, as 32 bytes, of the ledger that holds the adopted copy of the list memo with the highest list index; when there is one block and no list, the ledger of chunk n−1's adopted copy. **r₁, r₂:** SHA-256(seed ‖ root (32 bytes) ‖ counter as 4 bytes big-endian), counter from 0. The first 8 bytes read big-endian give a value v; the block is 1 + (v mod (n−2)). A duplicate is skipped until two distinct middle blocks are found. The planned traversal per sampled block, from its `FirstLedger` to its last adopted chunk, is known from the compact data and feeds S11 | Exact encodings let anyone recompute the sample. A ledger hash cannot be chosen by the writer. The writer can still retry a publication until a sample suits it, but each retry costs a full publication, so the claim is "hard to steer", not "cannot". Verification time is bounded by the planned traversal, which a late-landing chunk can make long; the estimate shows it rather than promising a constant |
| S22 | **After the manifest lands,** the writer reads it once through `ManifestAsync` on the real link and checks it as a public reader would: the manifest's ledger is after `LastLedger`, it decodes, and it equals the plan. A failure is reported as a failed publication with the link | The pre-send verification uses a synthetic manifest record. Only the real transaction proves that a reader accepts the link |
| S23 | `SubmittedMemos` replaces `IReadOnlyList<SubmittedMemo>` as `SubmitAllAsync`'s result: `Count`, `HashOf(int)`, `TryLedgerOf(int, out long)` (false when unknown). `SequenceStrategy` returns unknown ledgers, and the writer then looks them up as today. This is a breaking change to `ISubmissionStrategy`, applied to all three strategies and every caller and fake | The strategies already know ledgers from their sweeps, except the sequence path. The library is unpublished |
| S24 | **The approval is bound.** The Estimate screen's request carries the approved estimate: its worst-case total and per-account requirements, and the file's identity (path, length, last-write time). `PublicationRun` opens the file, compares length and last-write time with the approved identity, and estimates again. When the new worst case exceeds the approved one, or the identity differs, it stops before spending with "The file or the network's fees changed since you approved this publication. Estimate it again." `BudgetDrops` is the approved worst case | Today the run re-estimates and approves itself at whatever the new estimate says, and a file replaced between Estimate and Publish would be priced or published unconfirmed |
| S25 | **Resume asks too.** Resume prices the remaining work after reconciliation (spec 2026-10-15, 5.2 step 6). When that crosses the S12 threshold, the run shows the same summary and checkbox on the run screen before any transmission. Below the threshold, resume proceeds as today | A resume is a new session with new spending (B26). It must not spend above the threshold unconfirmed |
| S26 | The reader's `ForwardHistory` cap becomes hard. A walk buffers only records whose chunk marker belongs to a block not yet read within the next four blocks; anything else is dropped at intake. When even that exceeds the cap inside one ledger, the walk keeps its place by ledger and transaction index, drops the buffer, and replays that ledger from the node when a later block needs it | A single ledger with many records, written by another implementation, would otherwise hold them all. Rounds of 16,384 memos already put more than 4,096 of this writer's records in one ledger |
| S27 | The memory claim is tested as a scaling bound, not one number: publish deterministic generated sources of 50, 100 and 200 MB on the fake ledger, including one interrupted and resumed run, retries and lagging history. Sample peak private bytes of the process. Assert peak(200) − peak(50) < 150 MB, a slope of under 1 byte of memory per byte of file plus a constant. A separate reader test feeds one ledger of 20,000 records and asserts the history buffer never holds more than the cap | A single forced-GC measurement misses transient peaks and linear growth |

## 4. The library

### 4.1 The source (S2)

```csharp
public interface IPublicationSource : IAsyncDisposable
{
    long Length { get; }
    int ReadChunk(long index, Span<byte> destination);
    ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken);
}

public sealed class FilePublicationSource : IPublicationSource
public sealed class MemoryPublicationSource : IPublicationSource
```

- Chunk *i* is bytes [i × 1000, min((i + 1) × 1000, Length)), and it is read to the full span or
  throws.
- `PublicationSession.StartAsync` and `ResumeAsync` take an `IPublicationSource`. Overloads with
  `ReadOnlyMemory<byte>` wrap it in a `MemoryPublicationSource` that the overload owns and disposes
  after the run.
- `PublicationSession.RootOf` gains an asynchronous `IPublicationSource` overload.
- A source longer than `MaxPublicationBytes` is refused by the session with `TooLarge`.

### 4.2 Tree and plan (S3)

- `FileTree.BlockHashesAsync(IPublicationSource, IProgress<long>?, CancellationToken)` computes the
  block hashes in one pass.
- `ManifestPlan.Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers)`
  builds the entries.
- `FileTree.Split` stays only for small in-memory use and tests.

### 4.3 Writer

- `PublishAsync` takes `IPublicationSource`.
- The chunk pass gives the strategy a `LazyMemoList(source, missing)`.
- `SubmittedMemos` (S23) folds each block's first ledger as the minimum over the adopted copies of
  its chunks, both reconciled and new, and folds the window's last ledger over every chunk and
  list memo.
- Verification follows S9 and S21. The manifest check after landing follows S22.
- The progress stage name "read-back" stays. Its count is the blocks read.

### 4.4 Strategies (S5, S19, S23)

- **`BatchStrategy`:**
  - Rounds are capped by `MaxRoundMemos`.
  - Tracking is compact (S19).
  - A round-local registry, history and cache.
  - Session-wide state stays in `BatchContext` (S5).
  - Sweep records are compacted with verdicts (S6), and conflicts trigger S17.
  - `RequireDistinctMarkers` is replaced by construction: chunk and list markers carry their index,
    and a pass's indices are distinct.
- **`TicketStrategy` and `SequenceStrategy`** return `SubmittedMemos`. Their memory is otherwise
  unchanged (S1).

### 4.5 Session and state (S7, S8, S20)

- The session holds the source, not bytes.
- History is streamed and reconciliation is compact.
- The state file and the report follow S8.
- Existing unfinished version-2 and version-3 runs keep everything resume needs:
  - strategy, payer, fence, `StartSequence`;
  - exact manifest recognition (B27);
  - old fields still read.

### 4.6 Ledger hash (S10)

`LedgerHashAsync` as S10 says.

### 4.7 Reader (S26)

`ForwardHistory` changes as S26 says. The same rule applies to `web/src/history.mjs`, to keep the
two readers equal.

## 5. The application

1. **Publish.**
   - No bytes are read when a file is picked.
   - `PublishRequest` carries `SourcePath`, `Length`, `LastWriteTimeUtc` and the approved estimate
     (S24), not bytes.
   - `PublishModel` refuses only per S1/S16.
2. **Estimate.**
   - Duration range (S11).
   - Summary and checkbox above the threshold (S12).
   - `EstimateModel` owns the threshold, the checkbox and its reset rules; it is tested.
3. **Run.**
   - `PublicationRun` owns the `FilePublicationSource`, `IKeepAwake` (S13) and the activity lease
     (S14) from preparation to the end, on every path.
   - Preparation stages report progress: "Hashing the file — 412,000 of 1,689,810 chunks",
     "Reading the ledger history".
   - The approval check (S24) runs before the first transmission.
   - The resume confirmation (S25) appears on the run screen.
4. **Unfinished and resume.**
   - The `FilePublicationSource` is opened by `PublicationRun`, not by `UnfinishedModel`.
   - The streaming root (S7) runs inside the run, under the lease and keep-awake.
   - No `File.ReadAllBytesAsync` is left in the application.
5. **Shell.** Lock cancels an active run first (S14).
6. **Read.** No size ceiling; size and duration are shown before downloading (S15).
7. **Windows head.** `KeepAwake` with its dedicated thread (S13), registered in `MauiProgram`.

## 6. Testing

1. **Core unit:**
   - **`FilePublicationSource`:** chunk boundaries, the last short chunk, partial-read retry, a
     read past the end, truncation under the source, write and delete sharing denied, a
     non-seekable handle refused.
   - **Tree:** the streaming root equals the in-memory root for 1 to 3 blocks and odd tails.
   - **Memos:** `LazyMemoList` builds nothing until read.
   - **Rounds:** `BatchStrategy` with `MaxRoundMemos` = 64 over 300 memos. Rounds split; every memo
     lands once; each round's registry is released; candidate counts, budget, fence and ledger
     floor carry across rounds (a fourth candidate is still refused across rounds).
   - **Compact records:**
     - identity and coverage hold, including a foreign `TicketCreate` covering several sequences;
     - payer fees are attributed;
     - a conflicting copy is recorded as such.
   - **S17:** a conflicting copy ahead of the adopted one makes its block re-send, and the new
     `FirstLedger` is past the conflict; an identical duplicate does not.
   - **S9 (a):** a plan whose entry's `FirstLedger` is after one of its chunks fails before any read.
   - **S21:** the sample from fixed seed bytes and root (exact indices pinned); all blocks at 4 or
     fewer; one block alone.
   - **S22:** a manifest that the reader rejects after landing fails the publication.
   - **S10:** `LedgerHashAsync` refuses an unvalidated answer or one for a different index.
   - **State:** version 3 without `ChunkHashes`; version 2 and 3 files written by the current
     application still resume; the strategy is recorded.
   - **Reconciliation:** streaming, with list and manifest data kept, recognises a finished run
     (B27) and survives a B30 restart.
   - **Reader (S26):** the cap holds through a 20,000-record ledger, in .NET and in
     `web/test`.
2. **Memory (S27):** the scaling test, marked `Category=Slow`.
3. **Core on the stand (`xrpl-file-ci`):**
   - 50 MB through Batch, then read through `XrplFileReader`;
   - a stopped 50 MB run resumed from its path;
   - a run whose file is replaced between estimate and start is refused (S24).
4. **App.Core:**
   - policy S1/S16;
   - duration, threshold and checkbox reset (S11, S12);
   - the approval bound (S24) for a fee rise and for a replaced file;
   - the resume confirmation (S25);
   - resume reads no bytes into memory (a fake file system counts reads);
   - `IKeepAwake` acquire and release on one thread through success, failure and cancellation, with
     a fake that records thread ids;
   - the activity lease touching the auto-lock during a long await with no progress;
   - Lock cancelling an active run;
   - Read without a ceiling.
5. **Acceptance on testnet in the MauiLab VM:** a file of about 300 MB, a video.
   - The summary and checkbox appear.
   - Private bytes stay bounded through the run, sampled.
   - The VM does not sleep with a 5-minute sleep timeout.
   - Lock mid-run stops it, and Resume finishes it.
   - The link reads back in the application.
   - The time is recorded against the estimate's range.

   A full 1.7 GB run is not part of acceptance: it costs about 35 XRP of testnet funds, and the
   300 MB run exercises every path.

## 7. What this does not do

- It does not make the browser reader stream; the web component is designed next.
- It does not lift the ticket path's limit.
- It does not play a video before it has fully downloaded.
- It does not promise more than Windows sharing modes give on network file systems.
- It does not measure mainnet. A gigabyte publication there is about 1.7 million transactions in
  the shared network, at the payer's cost.
