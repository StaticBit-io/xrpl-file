# Streaming publication — design

The application refuses any file above 64 MiB (`PublishModel.MaxFileBytes`). A person tried to
publish a Full HD video of 1,689,809,534 bytes and was told only that. The limit exists because the
whole publication path holds the file, and much more than the file, in memory:

- `PublishModel.BuildRequestAsync` reads the file with `File.ReadAllBytesAsync`.
- `FileTree.Split` copies it into one `byte[]` per chunk, and the session and the writer each split
  it again.
- The writer builds every chunk's `MemoSubmission` up front, at about 4 KB of UTF-16 hex per
  1000-byte chunk, which is about 7 GB for this file.
- A Batch round plans every pending memo at once, and keeps every candidate's signed blob and every
  history record with its memo data until it ends.
- Resume reads and hashes the file twice in the application and once more in the library.
- At "done" the state file writes a dictionary of every chunk hash.

This document:

- removes the limit for Batch publications, up to a ceiling set by real memory;
- makes the publication path stream from and to disk;
- replaces the full read-back of Batch publications with deterministic checks plus a sampled read;
- prices time as well as money before a run;
- asks for an explicit confirmation that is bound to what each session may spend;
- keeps the computer awake and the vault unlocked by its timer during a run.

The decisions were taken with the person on 2026-10-23:

- no size limit for Batch;
- a cost-and-time summary with a checkbox above one hour or 1 XRP;
- one sampled verification rule for every size;
- the computer stays awake and the vault stays unlocked by its timer while a run works;
- the ticket path keeps its 64 MiB limit.

**Revised twice the same day after reviews by Codex.** The second revision chooses simpler rules
over repairs:

- the sample is the writer's self-check, seeded by the root;
- a conflicting copy in the window fails the run instead of being repaired;
- sampling applies to Batch only;
- per-chunk tracking keeps no hashes;
- the file handle is held from the estimate on;
- every session has its own immutable approval.

## 1. What is known

From a survey of `main` at `b794067`:

| Place | What it holds today |
|---|---|
| `PublishRequest.File` | the whole file as `ReadOnlyMemory<byte>`, read by `PublishModel.BuildRequestAsync` and by `UnfinishedModel` on resume |
| `FileTree.Split` | a second full copy as `byte[]` per chunk; called by `PublicationSession` at start and on resume, by `RootOf` and by `XrplFileWriter` |
| `XrplFileWriter.PublishCoreAsync` | `chunkMemos` for every chunk; `chunkByIndex`, `chunkLedgers` and `chunkHashes` as dictionaries and lists of strings |
| `ISubmissionStrategy.SubmitAllAsync` | `IReadOnlyList<MemoSubmission>` with every memo of a pass; returns `IReadOnlyList<SubmittedMemo>` |
| `BatchStrategy` | `pending` (every index), and `landed` and `candidates` dictionaries; one `BatchRound` over all of `pending` |
| `BatchRegistry` | every candidate's `SignedTransaction.TxBlob`, about 18 KB per 8-inner batch, until the round ends |
| `BatchRound._writerHistory`, `BatchSweep.FinalAsync` | every writer record with its `MemoDataHex` |
| `BatchProgress._found` | a pass-wide `HashSet<int>` |
| `BatchTrace._entries`, `PublicationRun.WriteTraceAsync`, `RunTraceLog.AppendAsync` | session-wide trace strings, copied again to format the log; the log writer builds its buffers before waiting for its lock |
| `PublicationSession.Reconcile`, `CompleteHistoryAsync`, `HistoryAsync` | the whole history since `StartedInLedger` as one list with memo data; `LandedMemo(TxHash, LedgerIndex, MemoDataHex)` per landed chunk; resume coverage as a `HashSet<uint>` over the sequence interval |
| `PublicationSession.FindManifest` | a chunk-ledger dictionary and the full hash list |
| `PublicationState.ChunkHashes` | every chunk hash, written at "done" |
| `PublicationReport.ChunkHashes`/`ListHashes`, `PublishResult` | every hash, as strings |
| `ForwardHistory` (reader) | up to 4,096 records with memo data, but it never evicts the ledger it is walking |

What already works and stays:

- Hashing is per chunk and per block: `MerkleTree.Leaf`, `FileTree.BlockHash` over one block's
  1,024 chunks, and `FileTree.RootHex` over block hashes.
- The reader's block API (`ManifestAsync`, `BlockListAsync`, `BlockAsync`) is per block.
- The Read screen writes each verified block to a temporary file and saves by stream.
- `VaultSigner.SignAsync` and `PublicationRun.Observe` touch the auto-lock.

Two facts the design has to respect:

- **The root does not cover `FirstLedger`.** `FileTree.RootHex` hashes block hashes, not each
  entry's `FirstLedger`. A list that roots correctly does not prove that a block's chunks are
  reachable from its first ledger.
- **The reader takes the first copy.** It takes the first decodable, size-eligible copy of a chunk
  in canonical order from the block's first ledger. A copy with other data ahead of the good one
  breaks the block, whatever the sweep adopted.

Absent today: any sleep prevention, and a duration in the estimate.

Constants: a chunk is 1,000 bytes (`MemoLayout.ChunkBytes`); a block is 1,024 chunks
(`FileTree.BlockChunks`); a block-list memo carries 982 bytes of JSON. The 1,689,809,534-byte file
has 1,689,810 chunks and 1,651 blocks.

## 2. Scope

**In:**
- A publication source that reads chunks from a held file handle.
- Memos built on demand.
- Bounded Batch rounds.
- Compact records and compact tracking.
- No per-chunk data in the state file, the report or the result.
- A streaming root on start and on resume.
- Streaming, filtered reconciliation.
- A reader history cache that is bounded and correct.
- Deterministic reachability checks plus a sampled read for Batch.
- A duration in the estimate.
- Per-session approval, shown above a threshold.
- Keep-awake and an activity lease.
- Manual Lock stopping a run.
- No reader limit on the application's Read screen.

**Out:**
- Memory work on the ticket and sequence paths, which keep their 64 MiB limit and the full
  read-back.
- The browser reader's assembly in memory; the `<xrpl-file>` component is designed next.
- Playing a video while it downloads.
- Repairing a block when another publication's chunks sit in the window.
- Network file systems beyond what Windows' sharing modes give.
- Mainnet measurements.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| S1 | The application publishes up to `MaxPublicationBytes` = 16 GiB through Batch. Without a payer (tickets or sequence), the 64 MiB limit stays. A larger file without a payer is refused with "Choose a payer — files larger than 64 MiB publish through Batch."; a file above 16 GiB with "This application publishes files up to 16 GiB." | The person removed the limit for Batch. 16 GiB is set by memory: per-chunk tracking (S19) is about 1.1 bytes per 1,000-byte chunk, about 19 MB at 16 GiB, and every index stays an `int`. It also covers the 1.7 GB video with room to spare. A ticket run of a gigabyte would hold about 50 XRP for hours |
| S2 | Core gains `IPublicationSource`: `long Length`; `int ReadChunk(long index, Span<byte> destination)`; `ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken)`. A read fills exactly min(1000, Length − index × 1000) bytes, retrying partial reads. The destination must be at least that long, the method returns the count, and bytes past it are untouched. A premature end throws `IOException`. Offsets use checked `long` arithmetic. `FilePublicationSource` holds one `SafeFileHandle` opened with `FileShare.Read`, which denies other writers and deletes, and rejects a non-seekable handle. `MemoryPublicationSource` wraps `ReadOnlyMemory<byte>`. The creator owns and disposes the source; a session borrows it | Positional reads on one handle are thread-safe. On local Windows file systems a handle that denies write and delete sharing blocks writes, deletes and replace-by-rename for its lifetime. Other file systems give what their driver gives |
| S3 | The Merkle root and block hashes are computed in one forward pass, keeping only each block's 32-byte hash. `ManifestPlan.Entries` takes block hashes and per-block first ledgers | 1,651 block hashes are 53 KB |
| S4 | A pass's memos are a lazy `IReadOnlyList<MemoSubmission>`. Element *i* is built from the source when it is read, and never cached. Consumers read it by index or in round-sized slices; no code materializes it | A chunk read from a local disk costs microseconds against a submit round trip of hundreds of milliseconds |
| S5 | A Batch round takes at most `MaxRoundMemos` = 16,384 pending memos (2,048 batches); a pass with more runs a series of rounds. Each round's registry, history and sweep cache are released when it ends. **Session-wide state stays in `BatchContext` across rounds, as now:** the persisted fence (B23), candidate counts per memo (B13), committed spending (B26), the ledger floor (B6 amendment) and the trace. Between two normal rounds, B9 resolution and B30 coverage of the finished round are the barrier | It bounds the signed blobs to about 37 MB and a round's history to 16,384 compact records. Resetting any session-wide state at a round boundary would allow a fourth candidate or reuse of consumed sequences |
| S6 | A history record is classified the moment a sweep reads it, then kept only as a compact record. **Kept:** hash, account, ledger, transaction index in the ledger, validated, result class (`tes`/`tec`/other), transaction type, `Sequence`, `TicketSequence`, `TicketCount`, `Fee`, `ParentBatchID`, the memo marker's kind and index. **Data verdict** for a chunk marker: matches the source, or conflicts. Records of any verdict take part in identity (B12), coverage (B30) and fee attribution (B29). Chunk memo data is dropped after the verdict | Identity, coverage and fees need these fields, and no chunk data |
| S7 | Resume computes the root of the source opened on `SourcePath` (or a picked file) in one streaming pass. It compares size and root, then reconciles with streaming history (S20). The application never reads the file into memory | Today's three in-memory reads of the file are what limits resume |
| S8 | No per-chunk data in persisted or returned objects. State version 3 drops `ChunkHashes` (still read and ignored if present); version 2 still reads it and no longer writes it. `PublicationReport` and `PublishResult` replace their hash lists with `ChunkCount`, `ListCount` and `ManifestHash`. `FindManifest` builds its expected manifest from block hashes and block first ledgers | Nothing in the application reads per-chunk hashes; the link is the manifest hash. The library is unpublished, so its API can change now |
| S9 | **Verification of a Batch publication** has two parts. (a) **Deterministic, every block, from the run's own data, no reads:** the block's `FirstLedger` is the minimum over its chunks' adopted ledgers; every adopted ledger is within the manifest window; the block list's entries equal the plan, ledgers included. (b) **A sampled read through the reader:** the full block list rooted to the manifest root, then the blocks of S21 through `BlockAsync`. **Ticket and sequence publications keep the full read-back** | The root does not cover `FirstLedger`, so (a) is what proves reachability. (b) proves that the reader assembles the file through its own code. Ticket and sequence publications are at most 64 MiB, so a full read stays affordable, and the sequence path checks no bytes as memos land |
| S10 | *(Removed in the second revision: no ledger-hash read is needed; see S21.)* | — |
| S11 | The estimate shows a duration as a range, per strategy. **Batch:** writing at 160 and 70 memos/s, plus `LedgerTiming.ApproximateLedgerInterval` × 3 per round (S5); a pass's last round is already one of these, so there is no separate per-pass term; plus the hash pass over the whole file at 9,000 and 4,500 chunks/s. A resume's quote, shown after its hash pass and history read, prices only the remaining chunks' writing, settles and verification. **Tickets:** writing at 2 and 1 memos/s, the measured testnet range of the ticket path, with a full read-back at the reader rate. **Sequence:** one memo per ledger, with a full read-back. Verification assumes each sampled block's chunks are contiguous: about 1,024 records, at the measured 0.7 s per 400 records. The estimator groups memos per round as the strategy will: rounds of `MaxRoundMemos`, 8 + 1 becoming 7 + 2 within a round, and one memo alone as a solo. Fees, transaction counts and the writer's solo fees follow the same grouping. During the run, the time left uses measured rates, and the verification ETA uses the real planned traversal once the chunks have landed | The 1.7 GB file runs 104 chunk rounds, so 104 settles. A pre-spend estimate cannot know where late chunks will land, so it states its assumption; the run corrects it with facts. Rates amended 2026-10-27 from the testnet acceptance (see Amendments). |
| S12 | When the estimate's longer duration exceeds 1 hour on any path, or the worst case of either account exceeds 1 XRP, the Estimate screen shows a summary: duration range, transactions, each account's required balance, fees in the worst case. Then a checkbox, "I understand the cost and the time", without which Publish stays disabled. The checkbox resets when the file, a key, the payer, the network or the estimate changes | The person chose a threshold and an explicit confirmation |
| S13 | Keep-awake is held by a dedicated background thread that the Windows `IKeepAwake` implementation owns. Acquire starts the thread, which calls `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)`, checks the return value and waits on an event the thread owns until it exits. Release signals the event; the thread clears the state and exits. Release is idempotent. A failed call is logged and does not fail the run. It is held from the start of a run's or resume's preparation to its end by any path | The state belongs to the calling thread. With `ConfigureAwait(false)` and pool threads, acquire and release land on different threads |
| S14 | **Activity lease.** On acquisition it touches the auto-lock immediately, then once a minute until it ends. It runs from the first preparation step of a run or resume to its end by any path. A touch that races an auto-lock callback already firing changes nothing: the run then finds the vault locked at the next signature and ends resumable. **Manual Lock stops the run:** the shell's Lock cancels an active run's token at once, then waits a bounded time for it to stop, then locks. Cancellation does not depend on the wait finishing | Progress reports do not cover long awaits. The person chose to keep the vault unlocked by its timer, not against their own Lock. Today a lock followed by an unlock lets the signer continue |
| S15 | The application's Read screen reads any size: it no longer applies `ReaderOptions.MaxBytes`, though the library's default stays. Before downloading, the screen shows the size and a duration at the measured reader rate | The Read screen already streams to disk |
| S16 | `PublishModel.MaxFileBytes` becomes `TicketPathMaxBytes`, applied only with no payer; `MaxPublicationBytes` (S1) applies with one. The Publish screen drops the warning about the reader's default | S1 |
| S17 | **A conflicting copy fails the run.** A conflicting copy is a validated, successful writer record with a chunk marker of this publication whose data differs from the source, inside the publication's window. When a sweep or reconciliation finds one, the run stops with a new error, `WriterWindowConflict`: "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key." Identical duplicates stay harmless. The error is recorded on the run's state, so a resume reports it again without sending | A reader takes the first eligible copy, so a conflict can break a block whatever the sweep adopted. It arises only when the same writer published other data while this run was interrupted, because B12 stops concurrent use during a round. Repairing it means re-sending whole blocks and re-pricing resumes. Refusing is simple, safe and rare |
| S18 | The trace is bounded at both ends. `BatchTrace` keeps a ring of the last 10,000 entries. Each entry goes to the trace log through a bounded channel of 10,000 entries; when the channel is full, the oldest pending entry is dropped and a counter records how many. One background writer drains it with awaited writes. `PublicationRun.WriteTraceAsync` no longer copies the trace | A session-wide list grows with the run, and fire-and-forget appends to a slow disk queue without bound |
| S19 | **Compact per-chunk tracking, per pass:** a landed bit set, and a `byte[]` of candidate counts kept as session state (S5). Plus, per block: first ledger (minimum), last ledger (maximum) and a landed count. No per-chunk transaction hash and no per-chunk ledger. Missing indices are an `int[]` or ranges. Each structure has one owner and one copy | About 1.1 bytes per chunk plus 20 bytes per block: about 2 MB for the 1.7 GB file and about 19 MB at 16 GiB. A hash per chunk is what made 1 TiB unrepresentable, and nothing needs one |
| S20 | Reconciliation streams history page by page and keeps only what this publication can use. **Chunk records:** classified per S6 and folded into S19's per-block minima and maxima, the first matching copy in canonical order adopted, a conflict raising S17. **List records:** kept only when their index is below this file's list count and their decoded entries carry this file's block hashes, last copy per index. **Manifest records:** not kept in the first pass. Once reconciliation has built the expected manifest bytes, a second streaming pass over the same pinned range compares every manifest-marker record against those exact bytes, and stops at the first validated, successful match (B27). Resume coverage uses sorted sequence ranges. A B30 retry discards the partial result and restarts the stream | The writer's history may hold many other publications. Filtering lists by this file's identity bounds what is kept by this file's own list count. Manifests are matched by streaming against the expected bytes, so no cap can drop the one that matters |
| S21 | **The sample.** A file of one block checks that block. Up to four blocks, all are checked. Otherwise blocks 0, n−1, r₁ and r₂. **r₁, r₂:** from SHA-256(root (32 bytes) ‖ counter as 4 bytes big-endian), counter starting at 0; the first 8 bytes as an unsigned 64-bit big-endian integer v; the block is 1 + (v mod (n−2)). A duplicate is skipped until two distinct middle blocks are found | The verification is the writer's check of its own publication, not a proof for third parties: the writer gains nothing by steering its own sample. A seed from the root is deterministic and recomputable, and needs no extra read. Third parties who need assurance read the link themselves; the Read screen and the reader always verify every block they read |
| S22 | **After the manifest lands,** the writer reads it once through `ManifestAsync` on the real link and checks it as a public reader would: its ledger is after `LastLedger`, it decodes, and it equals the plan. The same check runs on a manifest that resume recovers through B27, before the run is called done. A failure records the manifest hash in the state as "landed, rejected", reports the link with the failure, and is not turned into success by a later resume | Only the real transaction proves a reader accepts the link, and a recovered manifest deserves the same proof |
| S23 | `SubmittedMemos` replaces `IReadOnlyList<SubmittedMemo>` as `SubmitAllAsync`'s result. It carries `Count`, the landed bit set, `TryLedgerOf(int, out long)` and `TryHashOf(int, out string)`. Each `Try` method is false when the strategy did not keep that fact. **Hashes are kept for every memo of the list and manifest passes, and for every memo on the ticket and sequence paths. All of these are bounded: by the block count, by one memo, and by the 64 MiB limit.** On the Batch chunk pass hashes are not kept (S19). The writer looks up a memo's ledger by its hash when only the hash is known, as today on the sequence path. Batch and ticket strategies also hand the writer per-block first and last ledgers. This is a breaking change to `ISubmissionStrategy`, applied to all three strategies, every caller and every fake | The library is unpublished. One shape for all three strategies |
| S24 | **The file is held from the estimate on.** When the Estimate screen estimates, it opens the `FilePublicationSource` (S2) and keeps it. The same source is handed to the run and disposed when the run ends, or when the estimate is discarded: another file, a key change, leaving the screen. While held, no other process can write or delete the file, so what was approved is what is published | Metadata such as size and write time cannot prove identity. Holding the handle makes the question disappear |
| S25 | **Every session has its own immutable approval.** An approval names the writer, the payer, the fee-unit ceiling (B15), the writer's ceiling and the payer's ceiling. **A fresh run:** made on the Estimate screen, checkbox-confirmed above the threshold (S12). **A resume:** made after reconciliation from the remaining work; automatic below the threshold, confirmed on the run screen above it. The strategy is built from the approval and enforces each ceiling (B26). Any re-estimate inside the run that would exceed an approval's ceiling stops before spending with "Fees or balances changed since this was approved. Estimate it again." | Today the run re-estimates and approves itself. A resume is a new session with new spending, and must carry its own limits |
| S26 | **The reader's history cache stays correct and bounded.** The cache is only an optimization. It records the ledger range it holds completely: every record of the writer from ledger X to its current position. A block request is answered from the cache only when the block's `FirstLedger` ≥ X; otherwise the walk restarts at `FirstLedger` from the node. The cache holds at most 4,096 records. When it would exceed that, it drops the oldest whole ledgers and raises X past them. A single ledger larger than the cap is not cached: the walk streams through it and the cache restarts after it. The active walk itself never skips a record. The same rule applies to `web/src/history.mjs` | The current cap never drops the ledger being walked. A selective cache would drop records that a later or revisited block needs. Answering only from a range known to be complete keeps the reader's result identical, with or without the cache |
| S27 | **Memory test.** A dedicated fake ledger for this test keeps compact records, not memo data. The run under test executes in a child process, which reports its peak private bytes. Sources of 50, 200 and 800 MB are generated deterministically, with one interrupted and resumed run, retries and lagging history. Two assertions: peak(800) < 300 MB absolute; and peak(800) − peak(50) < 40 MB, about S19's per-chunk cost at 800 MB plus a round's constant. A reader test feeds one ledger of 20,000 records and a revisited block, asserting identical results with and without the cache, as well as the cap | The general fake keeps every memo, which would measure the fake. A slope bound alone lets a leak through, and an absolute bound alone lets linear growth through |

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

- `PublicationSession.StartAsync` and `ResumeAsync` take an `IPublicationSource`. Overloads with
  `ReadOnlyMemory<byte>` wrap it in a `MemoryPublicationSource` that they own and dispose after
  the run.
- `PublicationSession.RootOf` gains an asynchronous `IPublicationSource` overload.
- A source longer than `MaxPublicationBytes` is refused with `TooLarge`.

### 4.2 Tree and plan (S3)

- `FileTree.BlockHashesAsync(IPublicationSource, IProgress<long>?, CancellationToken)` computes
  the block hashes.
- `ManifestPlan.Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers)`
  builds the entries.
- `FileTree.Split` stays only for small in-memory use and tests.

### 4.3 Writer

- `PublishAsync` takes `IPublicationSource`, and the chunk pass gives the strategy a
  `LazyMemoList(source, missing)`.
- Per-block first and last ledgers come from `SubmittedMemos` and from reconciliation (S19, S20).
- Verification follows S9 and S21 for Batch, and the full read-back for the other two paths. The
  check after landing follows S22.
- The progress stage name "read-back" stays; its count is the blocks read.

### 4.4 Strategies (S5, S19, S23)

- **`BatchStrategy`:**
  - Rounds are capped (S5), and session state stays in `BatchContext`.
  - Tracking is compact (S19), and sweep records are compact with their verdicts (S6).
  - A conflict raises S17.
  - `RequireDistinctMarkers` is replaced by construction: chunk and list markers carry their index,
    and a pass's indices are distinct.
- **`TicketStrategy` and `SequenceStrategy`** return `SubmittedMemos`. Their memory is otherwise
  unchanged (S1).

### 4.5 Session and state (S7, S8, S17, S20, S22)

- The session holds a borrowed source, not bytes, and reconciles as a stream.
- State version 3 drops `ChunkHashes`, and gains `ManifestStatus` ("landed, rejected") and the
  conflict error.
- Existing unfinished version-2 and version-3 runs keep everything resume needs: strategy, payer,
  fence, `StartSequence`, exact manifest recognition (B27) and the old fields.

### 4.6 Reader (S26)

`ForwardHistory` (.NET) and `web/src/history.mjs` follow S26.

## 5. The application

1. **Publish.** No bytes are read when a file is picked. `PublishRequest` carries the held source
   (S24) and the approval (S25), not bytes. `PublishModel` refuses only per S1 and S16.
2. **Estimate.** Opens and holds the source (S24). Shows the duration range (S11), and the summary
   and checkbox above the threshold (S12). `EstimateModel` owns the threshold, the checkbox, its
   reset rules and the approval. All of it is tested.
3. **Run.**
   - `PublicationRun` takes the source, `IKeepAwake` (S13) and the activity lease (S14) for the
     whole run, and builds the strategy from the approval (S25).
   - Preparation stages report progress: "Hashing the file — 412,000 of 1,689,810 chunks",
     "Reading the ledger history".
   - The resume confirmation appears on the run screen when S25 requires it.
4. **Unfinished and resume.** `PublicationRun` opens the source, then computes the streaming root
   and reconciles (S7, S20) under the lease and keep-awake. No `File.ReadAllBytesAsync` is left in
   the application.
5. **Shell.** Lock cancels an active run first (S14).
6. **Read.** No size ceiling; the size and a duration are shown before downloading (S15).
7. **Windows head.** `KeepAwake` with its dedicated thread (S13), registered in `MauiProgram`.

## 6. Testing

1. **Core unit:**
   - **`FilePublicationSource`:** chunk boundaries, the short final chunk read into a 1,000-byte
     buffer, partial-read retry, a read past the end, truncation under the source, write and delete
     sharing denied while held, a non-seekable handle refused.
   - **Streaming root:** equals the in-memory root for 1 to 3 blocks and odd tails.
   - **`LazyMemoList`:** builds nothing until read.
   - **`BatchStrategy` with `MaxRoundMemos` = 64 over 300 memos:**
     - rounds split;
     - every memo lands once;
     - each round's registry is released;
     - candidate counts, budget, fence and ledger floor carry across rounds — a fourth candidate is
       refused across rounds.
   - **Compact records:**
     - identity and coverage hold, including a foreign `TicketCreate` covering several sequences;
     - payer fees are attributed by hash;
     - a conflicting copy raises `WriterWindowConflict`, and an identical duplicate does not.
   - **S9(a):** a plan entry whose `FirstLedger` is after one of its chunks fails before any read.
   - **S21:** the sample for a fixed root, with exact indices pinned; every block checked at 1 to 4
     blocks.
   - **S22:** a landed manifest the reader rejects fails the run, and a resume that recovers it
     does not turn it into success.
   - **State:** version 3 without `ChunkHashes`; version 2 and 3 files written by the current
     application still resume; the strategy is recorded.
   - **Reconciliation:** streaming, with the S20 filters, recognises a finished run (B27) with a
     foreign publication's lists and manifests in history, and survives a B30 restart.
   - **Reader (S26):** identical results with and without the cache through a 20,000-record ledger
     and a revisited block, in .NET and in `web/test`.
   - **Trace (S18):** a slow writer drops the oldest entries and counts them, without unbounded
     queueing.
2. **Memory (S27):** in a child process, marked `Category=Slow`.
3. **Core on the stand (`xrpl-file-ci`):**
   - 50 MB through Batch, then read through `XrplFileReader`;
   - a stopped 50 MB run resumed from its path.
4. **App.Core:**
   - policy S1 and S16;
   - duration, threshold and checkbox reset (S11, S12);
   - the held source and its disposal on every exit (S24);
   - approvals for a fresh run and a resume, below and above the threshold, and a ceiling exceeded
     by a re-estimate (S25);
   - resume reads no bytes into memory;
   - `IKeepAwake` acquired and released on one thread through success, failure and cancellation,
     with a fake that records thread ids;
   - the lease touching immediately and during a long await;
   - Lock cancelling an active run;
   - Read without a ceiling.
5. **Acceptance on testnet in the MauiLab VM:** a file of about 300 MB, a video.
   - The summary and the checkbox appear.
   - Private bytes stay bounded through the run.
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
- It does not repair a publication whose writer was used for other data while it was interrupted.
  It says so and asks for a fresh writer.
- It does not promise more than Windows sharing modes give on network file systems.
- It does not measure mainnet. A gigabyte publication there is about 1.7 million transactions in
  the shared network, at the payer's cost.

## Amendments (2026-10-27)

1. **S11 (Task 5).** Batch writing is priced at 160 and 70 memos/s (best and conservative testnet rates, acceptance 2026-10-26), plus the hash pass over the whole file at 9,000 and 4,500 chunks/s (300,344 chunks in 0:32 on the first pass and 1:05 on resume, rounded down). A resume's quote is shown after its hash pass and history read are done, so it prices only the work after Continue: the remaining chunks' writing, settles and verification, without a hash pass (user decision 2026-10-27). The one-account paths are unchanged (their hash pass is at most 64 MiB, seconds). The one-hour threshold of S12 is unchanged.
2. **Run screen, D2 (Task 6).** "Finished in" is the publication's working time: the sum, over its sessions that got past their approval, of each session's time from start to stop less the time a resume confirmation waited for the person. A session that sends nothing (declined, unanswered, refused before submitting) leaves the state file as it was and adds nothing. The sum of earlier sessions is the state's optional `WorkedSeconds`, written by the application when such a session ends; the library carries it and never reads it.
4. **S27 (Task 2).** S27 bounds the publication run. The vault unlock's transient Argon2id peak (about 1 s at the default 256 MiB cost) is outside it; the lanes are released before the run starts (ruling 2026-10-26).
