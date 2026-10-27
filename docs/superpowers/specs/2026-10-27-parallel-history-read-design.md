# Concurrent history read for a resume — design

Builds on `2026-10-23-streaming-publication-design.md` (S17, S20, S26, S27 and its Amendments of 2026-10-27) and `2026-10-15-batch-publishing-design.md` (B27, B30). Evidence: `docs/superpowers/reports/2026-10-27-history-read/report.md`.

## 1. Why

A resume reads the writer's history from `PublicationState.StartedInLedger` to a pinned ledger, once for the reconciliation (`PublicationSession.StreamReconcileAsync`) and once more for the manifest (`ScanForManifestAsync`, B27). Both are one forward `account_tx` walk, page after page, each page waiting for the marker of the one before.

On testnet the node's answer is 98.8% of that walk: 436.5 ms per page, and a public node clamps `limit` to 200, so a page is 200 records whatever the library asks for. Parse and reconciliation together are about 1%. A resume of 300,000 chunks waits about 12 minutes for the node per pass. Overlapping our work with the node's (the report's Task 4) cannot help; asking the node several things at once can.

## 2. Goals, non-goals, anti-goals

**Goals**

- G1. Both resume passes read their range as several ledger ranges at once and hand the consumer exactly the records, in exactly the order, that one forward walk over the whole range hands it today. `StreamReconcileAsync` (B27/B30 coverage, S20 `SequenceRanges`, amendment 5's one-account S17 window rule) and `ScanForManifestAsync` do not change.
- G2. On testnet the history read is at least twice as fast as today's single walk, measured read-only.
- G3. Memory stays bounded by a constant independent of the file: S27 (`PublicationMemoryTests`) and `ResumeMemoryStandaloneTests` pass with their bounds unchanged.

**Non-goals**

- The reader (`XrplFileReader`, `ForwardHistory`), the strategies' sweeps (`BatchSweep`, `TicketStrategy`), `ThrowawayAccount` and the browser mirror keep their single walks.
- The page size stays 400 (the report found nothing better).
- No change to `ILedgerSource` or to any implementation of it.

**Anti-goals** (a failure even if it is faster)

- A record delivered twice, missed, or out of canonical order: S17 and "first matching copy wins" depend on order.
- An error that a single walk would not have raised first, raised first (a node failure in a later range reported before a conflict in an earlier one).
- A request still in flight after the pass returned or threw.
- A client that pushes a public node into `warning: load` and keeps pushing.

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| H1 | **Where.** A new internal class `ConcurrentHistory` in Core over any `ILedgerSource`, with one method `ReadAsync(account, fromLedger, toLedger, token)` returning `IAsyncEnumerable<LedgerTransaction>`. `PublicationSession` owns one per session and both resume passes read through it. | The ordering rule is then proved once, against any source, including the test fakes; the source's paging stays where it is. A session-long instance carries what it learned about the node (H6) from the first pass to the second and to B30 retries. |
| H2 | **Contract.** What `ReadAsync` yields equals what `source.AccountTransactionsAsync(account, from, to, forward: true)` yields when validated history does not change during the read: the range is cut into disjoint, adjacent ledger segments `[first, last]`; each segment is one forward walk of its own; segments are delivered whole, in ledger order. `from > to` is passed to the source as one walk, which refuses it as it does today. | Forward `account_tx` orders by ledger, then by position in the ledger, so the concatenation of adjacent ledger ranges is the walk of their union. A ledger is never split between two segments. |
| H3 | **Lanes.** `SessionOptions.HistoryLanes`, default **4**, at least 1: how many segments are read at once. With 1, `ReadAsync` is exactly today's single walk (one call over the whole range, nothing else). | 4 leaves room above the 2× goal for uneven segments and for H6 halving once; a public node sees about 9 requests a second instead of 2.3. 1 keeps an exit for a node that should not be read concurrently. |
| H4 | **Cutting segments.** Segments are cut at a frontier, in ledger order, when a lane is free: the first starts at `from` and spans 1 ledger. Each later one spans the current span estimate, never past `to`. When a segment finishes with `r` records over `s` ledgers, the estimate becomes `2s` (at least the estimate already in force) if `r` is 0, and otherwise `clamp(1000 · s / r, 1, 2s)` — about 1,000 records a segment (`TargetRecords`). | The history's density is unknown and uneven: dense Batch ledgers (hundreds to about 2,000 records each), then days with nothing between two sessions, then dense again. Doubling crosses an empty stretch in a logarithmic number of requests; the density rule sizes segments where records are. |
| H5 | **Splitting a running segment.** A segment that has handed over 2,000 records (`2 · TargetRecords`) since it started or last split, whose current record's ledger `p` is below its `last`, and while more lanes within the allowed number (H9) are idle than there are split pieces nobody has started, gives up the upper part of what is left: a new segment `[m, last]`, with `m = p + 1 + (last − p − 1) / 2`, goes right after it in the order, and its own `last` becomes `m − 1`. Its walk keeps the request it has (the marker belongs to it) and stops at the first record past its new `last`. | A span sized on an empty stretch can land on a dense session and would otherwise read the whole session alone. Halving the rest each time gives free lanes work right behind the consumer. At most one page is read past the new end and dropped; nothing is read twice into the stream. Splitting only for an idle lane means a piece never waits for a lane while every lane is held up by H7 behind it (a deadlock the builder check found): an idle lane is waiting for exactly such work and takes it at once. A lane is idle only once the frontier is past `to`, so splitting is what spreads a dense stretch that one large segment covers. |
| H6 | **Order of taking work.** A free lane takes the earliest segment that no lane has started (a split piece comes before anything later); only when there is none does it cut a new one at the frontier. A lane with nothing to take waits while any segment is still being read (it may split), and stops when every segment has been read and the frontier is past `to`. | Work closest to the consumer first is what turns concurrency into speed rather than into records waiting in memory. |
| H7 | **Back-pressure and memory.** A lane may hand a record to its segment only while the records held by that segment and every segment before it total fewer than **4,096** (`HeldRecords`); otherwise it waits for the consumer. Records leave the hold as the consumer takes them. At most `2 · 4,096` records are held, about 35 MB at a full chunk memo (about 4.2 KB a record as strings), plus one page per lane inside the source. The constant does not grow with the file. | Counting a segment's predecessors and not its successors means the segment the consumer is reading can always go on (no deadlock), and a far segment cannot take the hold from a near one. 4,096 is above the `(lanes − 1) · TargetRecords` = 3,000 records that equal segments need ahead of the consumer for full speed, and is the reader's own cache size (S26). |
| H8 | **Errors, in the walk's order.** A segment whose walk throws keeps the records it handed over and the exception; the consumer receives every record of the segments before it, then that segment's records, then the exception — what a single walk that failed on the same page would have done. Segments after the failed one are cancelled and never delivered. A consumer that stops early (the manifest found, a `WriterWindowConflict` thrown, its own token cancelled, or the enumerator disposed) cancels every lane and awaits them before the enumerator's disposal completes. | Keeps S17's conflict ahead of a later node failure, keeps B30's "a pass that breaks part-way is discarded and taken again" unchanged (`BatchReads.RetryAsync` still sees one exception per pass), and leaves no request running behind a returned pass. |
| H9 | **A node under load.** Whenever a lane is about to take work and `source.LoadWarnings` has grown since the instance last looked, the lanes allowed halve (at least 1). A pass that ends in `NodeUnreachable` halves them too. The figure never rises again in the session. A lane above the allowed number finishes the segment it has, still takes a split piece nobody has started, cuts nothing new at the frontier and is never counted idle for H5, and stops when there is no such piece; the first lane never stops early. When a read begins with one lane allowed it is a single walk over the whole range, as H3's 1. The baseline for "has grown" is the source's count when the instance is built (the session's start or resume): a warning during the fence wait comes from the same node and counts. | `warning: load` is a node telling this client it is near its limit; a node that dropped the connection or refused a request is not helped by a retry at the same pressure. Lanes are halved, not dropped to 1, so one transient warning costs half the gain rather than all of it. |
| H10 | **A node lacking part of the range.** No special handling. The library's client asks api_version 2, under which rippled refuses an `account_tx` range reaching outside its validated range (`lgrIdxMalformed`); the first segment starts at the pass's first ledger and the last ends at its last, so such a node fails the pass exactly as the single walk does today (`NodeUnreachable`, retried per B30). A node that is merely behind (`account_tx` lagging the validated ledger) leaves records out of whichever segments it is behind in, and B30's coverage over the merged stream sees the same shortfall and ends in `NodeLacksHistory` the same way. | rippled `AccountTx.cpp` `getLedgerRange`. An api_version 1 node would clamp the whole walk silently but refuse a segment wholly below its range; this library does not send version 1, so that difference is noted and not handled. |
| H11 | **Progress.** The "history" stage counts records as the consumer takes them, as today. | Nothing on the screen changes. |

## 4. Where it plugs in

- `PublicationSession` gets a field `ConcurrentHistory _history = new(source, options.HistoryLanes)`. `StreamReconcileAsync` and `ScanForManifestAsync` iterate `_history.ReadAsync(writer, _state.StartedInLedger, to, token)` instead of `_source.AccountTransactionsAsync(writer, _state.StartedInLedger, to, forward: true, token)`. Nothing else in either method changes.
- H9's "a pass that ends in `NodeUnreachable`" is observed inside `ReadAsync` (the exception it rethrows), so no caller changes for it.
- `SessionOptions.HistoryLanes` is public with XML docs; values below 1 are refused when the session is built (`ArgumentOutOfRangeException`).

## 5. Testing

1. **Unit, `ConcurrentHistoryTests`** over a scripted source that pages its records like the node (a page size, a delay per page, a count of calls in flight):
   - equivalence: for seeded random histories (dense single ledgers of up to 20,000 records, long empty stretches between dense runs, sparse records, nothing at all, one record), `ReadAsync` yields the same sequence as one walk, for lanes 1, 2, 4 and 8;
   - concurrency is real: with a delay per page, more than one call is in flight and the read takes less than half the single walk's time over the same script;
   - the hold: the most records held never exceeds `2 · HeldRecords`, with a consumer slower than the source;
   - errors: a failure in segment k after a record that a conflict in an earlier segment would stop at reaches the consumer only after every earlier record; a failure is rethrown once;
   - stopping early: breaking out of the loop and cancelling the token both leave no call in flight once disposal completes;
   - load: a source whose `LoadWarnings` grows mid-read leaves at most half the lanes taking work afterwards; a `NodeUnreachable` pass halves them for the next;
   - lanes = 1 makes exactly one source call with the pass's whole range.
2. **Existing suites** with the default of 4 lanes: `StreamingReconciliationTests`, `WindowConflictTests`, `PublicationSessionBatchTests` and everything else in `XrplFile.Core.Tests` and `XrplFile.App.Core.Tests`. A test fake that counts reads per `AccountTransactionsAsync` call (for example `FakeLedgerSource`'s history that hides records for a number of reads, its delayed records, `HistoryFailsAfter`) now sees several calls per pass; where a test's intent was "a pass", the fake or the test is adjusted to keep that intent, never by setting lanes to 1 to hide the concurrent path.
3. **Stand** (`Category=Integration`, `ws://localhost:16006`, compose project `xrpl-file-ci`): the full integration run.
4. **Slow**: `PublicationMemoryTests` (S27) and `ResumeMemoryStandaloneTests`, bounds unchanged.
5. **Testnet, read-only** (`account_tx` only, never mainnet): a scratch harness, not committed, reads the same account and range as the report (`r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW`) with lanes 1 and 4, alternating, at least three runs each; records per second, request count and load warnings are reported in `docs/superpowers/reports/2026-10-27-history-read/concurrent.md`. Done when lanes 4 reads at least twice as fast as lanes 1 and the two runs yield identical sequences (count and a hash over the records' hashes in order).

## 6. Rulings after the builder check

- **The testnet gate.** If lanes 4 is under 2× or draws load warnings, `TargetRecords`, `HeldRecords` and the default of `HistoryLanes` may be tuned within this design (H7's bound must stay constant in the file) and measured again; the report names the values measured. If no setting reaches 2× without warnings, the work stops and the report says so instead of claiming the goal. The builder runs the measurement (read-only `account_tx`, testnet only).
- **Test doubles.** `FakeLedgerSource` (shared TestSupport) may gain pass-aware or range-aware versions of its per-call knobs, and test-local decorators may forward `LoadWarnings` or count passes rather than calls, as long as each test keeps what it asserted about a pass.
- **A source that ignores cancellation.** Disposal waits for every lane, so a request that ignores its token holds the disposal up to that request's own timeout. Accepted: `XrplClientLedgerSource` passes the token to the SDK, which honours it, and a request outliving the pass is the worse outcome (section 2).

## 7. Left to the builder

- The data structure for segments (a linked list or an indexable list) and the signalling between lanes and the consumer, provided H7's count is exact under concurrency.
- Exposing `PeakHeld`, `PeakLanesInFlight` or similar as internal properties for the tests.
- Whether `ConcurrentHistory` checks `LoadWarnings` per segment or per record, provided H9 holds by the next segment taken.
