# Batch publishing: second-publication hang and throughput

Two follow-ups of the testnet acceptance (`../2026-10-15-batch-publishing/report.md`), run on
2026-10-21 against testnet (`wss://s.altnet.rippletest.net:51233`).

## 1. The second publication in one session

Defect 1 of the acceptance: the second publication of a session did not start for about 5 min,
then reported "The node did not answer. Nothing was spent." Commit `80c1e91` bounded every SDK
wait in the connect and request paths and made `ClientKeeper` replace a connection that stopped
answering, but the hang was never reproduced.

The application at `839421c` (main) ran in the MauiLab VM with the vault's `batch-writer` and
`batch-payer` keys. Four 1,000,000-byte files of random bytes and the 7,123,692-byte file of the
acceptance were published in one application session.

| Run | Before it | Starting | Total | Result |
|---|---|---|---|---|
| 1 | first of the session | 11.7 s | 39.8 s | Published |
| 2 | straight after run 1 | 10.7 s | 41.2 s | Published |
| 3 | 6 min idle on the Publish screen | 12.7 s | 41.1 s | Published |
| 4 | Estimate, then 6 min on the Estimate screen before Publish | 19.9 s | 45.3 s | Published |
| 5 | straight after run 4, 7,123,692 bytes (`accept-7mb.bin`) | 20.8 s | 121.4 s | Published |

Links:

- `xrpl-file://testnet/122DC020DFF080C22D06BFB07B2E3DB812143A0C3284CD5B9ADD01BD2DF52657`
- `xrpl-file://testnet/387D4EB80BA600C99D0C97FA4C4144AD69173E1DF0A9556BBB724E1662DD4909`
- `xrpl-file://testnet/FD764ECA44A04FDE0256699E46A68BBBA9B1259428708A457F4238002875D570`
- `xrpl-file://testnet/87610DCFB9F09F494E9F479467DB981A2A6ABE505C7165B0268D792C2FC507F7`
- `xrpl-file://testnet/BEFAD78EDA9B9F1AA2A62B8CC6C84EF1EBB279AB4872D3D9D2B9A3769EADA766`

Each link was read back on the host through `XrplFileReader`. Every one returned its file's size
and the SHA-256 the guest computed for the source file.

The core was also run from a host harness on one `ClientKeeper` client: two 200 KB publications
with 8 min of idle socket between them took 34.7 s and 34.5 s. Both read back.

The hang did not reproduce in five publications. Each application run opens its own connection,
so an idle socket is not the trigger in the application. The likely trigger is a connect that hung
inside the SDK, and that wait is now bounded at 20 s.

"Starting" lasts until the first progress sweep finds landed chunks, 5 s or more after the round
started sending. The chunks are already going out while the screen says "Starting".

## 2. Where Batch throughput goes

### The comparison

The 26.8 chunks/s of the acceptance is a whole publication: chunks, block lists, read-back and
manifest, with a 95 s stall in that first run. Later runs of the same file took 115–167 s. The
74–87 chunks/s of xrpl-video-platform and the spike's 5 MB in 16–26 s count the chunk upload
alone. On that basis this path already sent 7,124 chunks in about 28 s.

### Method

A console harness on the host (kept out of the repository) ran `PublicationSession` with
`BatchStrategy` exactly as the application builds it: `ClientKeeper.CreateClient`, vault signers
with the wallet cache, the default options, read-back on. Decorators around the sink, the source,
the signers and the fence recorder timed every call. Fresh faucet accounts, random files.

### Phases, before and after

Seconds. A round's "send" runs from its first submit to its last answer; "settle" from the last
answer to the settling snapshot; "after" from settling to the next round, which holds the final
sweep and, after the lists, the read-back.

| Phase | 1 MB before | 1 MB after | 7 MB before | 7 MB after |
|---|---|---|---|---|
| Chunks: send | 2.6 | 2.8 | 28.2 | 32.4 |
| Chunks: settle | 14.7 | 9.7 | 13.1 | 6.7 |
| Chunks: final sweep and the next stage's setup | 6.2 | 5.5 | 17.5 | 6.6 |
| of which the writer's history read in the final sweep | 2.6 | 2.1 | 14.3 (7,124 records) | 2.9 (1,332 records) |
| Lists: send, settle | — | — | 0.8, 13.5 | 0.8, 5.8 |
| Read-back | 3.3 | 3.3 | about 21 | about 18 |
| Manifest: send, settle | 0.8, 13.6 | 0.8, 5.8 | 0.7, 13.5 | 0.8, 8.4 |
| **Whole run** | **39.5** | **26.5** | **113.9** | **86.5** |
| **Chunks/s, whole run** | **25.3** | **37.8** | **62.5** | **82.3** |

The application in the VM agreed with the harness: 7 MB took 121.4 s on `839421c` and 93 s with
these changes (58.7 and 76.6 chunks/s), each read back with the right SHA-256. The second run's link
is `xrpl-file://testnet/C3C4247FA9472777508B5605F6525FF26D0C92D5635FD9822F0FF4C491CE4CD8`.

The 7 MB send is paced by `MaxAhead` (B10), not by the window: batches go out 256 at a time in
2–5 s, then nothing until a chain check sees B's validated sequence move, 5–8 s later. Before
the change the window grew to 105 and had 95 submits in flight at one point.

### What was not the cause

- Signing: 2–8 ms a signature, 3.8–7 s over a 7 MB run, inside a send loop that waits for
  allowance anyway. The wallet cache is used (`cacheWallet: true` in `PublicationRun`).
- The writer's per-memo ledger lookup: no `tx` request in any run; `SubmittedMemo.LedgerIndex` is
  always set.
- The fence: 9–11 writes of 3 ms.
- Chain checks and progress sweeps: concurrent with sending.

### Changes

| Commit | Change | Measured gain |
|---|---|---|
| `b08f724` | A round settles at the snapshot that resolves its last candidate (`SettleLedgers` 2 → 0); the resume fence keeps its 2 ledgers. The window stops growing at 32 | 5–8 s a round: about 13 s on 1 MB, about 20 s on 7 MB. The window cap is safety, not speed |
| `9dae453` | The final sweep keeps what the progress sweeps read and reads the writer's history only from where they stopped. If the records then do not cover the round (B30), it reads the round in full | 11 s on 7 MB |
| `fd2ae2b` | The read-back takes a read again, up to three times, when the node did not answer it | none; see below |

`fd2ae2b` fixes a defect the faster rounds exposed. The stand test of a silent second publication
(`PublicationRunStandaloneTests`) failed 2 of 4 times: the silence now began after the chunks
round, the read-back's `account_tx` waited out the SDK's 40 s request timeout, and nothing
retried it. On `839421c` the same test passed 4 of 4 only because the silence fell in reads that
the Batch path retries. After the fix it passed 5 of 5.

Tests: Core 664, App.Core 676 and Keys 251 unit tests pass. On the stand,
`BatchPublicationStandaloneTests`, `BatchSigningStandaloneTests` and `TransportStandaloneTests`
pass (14 of 14).

### Spec amendment

Spec 2026-10-15 is amended (B9, B10, the options block, and a dated note at the top):

- **B9.** A round sweeps at the snapshot that resolves its last candidate. Resolution comes from a
  validated snapshot past each candidate's deadline or sequence, so nothing the round sent can
  apply after it. B30's wait for complete history covers an `account_tx` that lags. The resume
  fence (B23) keeps `Fence + 2`.
- **B10.** The window grows by one per ten clean sends up to 32, the spike's best testnet figure.
  Unbounded, it reached 105 on a 7 MB run, against a public node that closes a client at about
  100 unanswered requests.

## What remains

Ranked by the time each would save on 7 MB:

1. **Read-back, about 18 s.** Each block is read from its first ledger. Two blocks share a ledger
   (2,048 chunks a ledger on testnet), so the reader pages about 12,300 records for 7,124 chunks.
   A page cache across the blocks of one read would cut about 40% of that, for the read-back and
   for every reader's read. It is a change to the reader, with `web/xrpl-file.js` to match, so it
   is not done here. Turning the read-back off is a product decision: the final sweep already
   compares every memo's bytes.
2. **`MaxAhead` pacing, 15–20 s of the send.** One run with `MaxAhead` = 1024 sent all 891
   batches in 8.6 s, but the whole run took 85.7 s, the same as with 256. The final sweep (18 s,
   because the progress sweeps had less time to read ahead) and a slower read-back on testnet ate
   the gain. B10 bounds what a divergence can cost at 256 batches, so raising it changes a risk
   bound; one sample does not justify that.
3. **Settle cadence, 1–2 s a round.** Settlement polls every 2 s, plus a 0.6–1.1 s snapshot.
   Polling every second would save about a second a round.
4. **"Starting" on the run screen.** The screen says "Starting" until the first progress sweep
   finds landed chunks, 5–10 s into sending. Showing the chunks stage when the round starts
   sending is a display change.
5. **A start snapshot behind the last settled one (low, pre-existing, from the review).** A round
   does not require its start snapshot to be at or past the previous round's settling ledger. A
   reconnect to a lagging node between rounds could start a round on stale sequences. The cost is
   a wasted round (`tefPAST_SEQ` or a divergence halt), not wrong data. Carrying the settled
   ledger into the next round as a floor would close it.
