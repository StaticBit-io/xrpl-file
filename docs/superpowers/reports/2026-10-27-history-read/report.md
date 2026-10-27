# Where the resume's history read spends its time

Follow-up O2 of the streaming-publication plan: before overlapping the history read with our reconciliation (Task 4), measure how the time of one `account_tx` walk splits into the network (N), the parse (P) and our reconciliation (C). Nothing in the repository changed for this; the harness is scratch and is not committed. Testnet was read-only: only `account_tx`, nothing signed or submitted.

## Environment

| | |
|---|---|
| Host | 11th Gen Intel Core i5-11400F (12 logical cores), 64 GB, Windows 11 Pro |
| Runtime | .NET 10.0.11, Release, workstation concurrent GC, `System.GC.LOHThreshold` = 4,194,304 (`app/DesktopGarbageCollection.props`, as the resume probe) |
| Commit measured | `8f22185` (includes Task 1, `758c7ec`: each page parsed once, out of the SDK's frame) |
| Stand | compose project `xrpl-file-ci`, `xrpllabsofficial/xrpld:3.3.0` (rippled 3.3.0), `ws://localhost:16006`, behind Docker port forwarding; an admin connection |
| Testnet | `wss://s.altnet.rippletest.net:51233`, account `r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW`; the node's version was not asked for, since only `account_tx` was allowed there |

## Method

A console harness referencing `XrplFile.Core`, `XrplFile.Keys` and `XrplFile.TestSupport` with three modes.

- `raw <url> <account> <limit> <pages>`: pages `account_tx` forward (`ledger_index_min` and `ledger_index_max` −1) with the SDK client from `ClientKeeper.CreateClient`. N is a `Stopwatch` around `await GRequest`. P is `JsonDocument.Parse(response.Raw.ToArray())` plus `XrplClientLedgerSource.ParseTransactions`, timed alone. The request type is an empty class, the same idea as the library's internal `UnreadResult`, so N is the production request; the pre-Task-1 form `GRequest<object, ...>` is measured in the allocation section only.
- `setup`: copy of `ResumeMemoryStandaloneTests`' setup: a writer and a payer funded from the stand's genesis account, 40,000,000 bytes of `VectorStream` published through Batch, stopped by a `StoppingSink` after 4,000 outers.
- `resume`: `PublicationSession.ResumeAsync` (Batch overload) declined at the quote, as `ResumeProbe` does, over a `TimingLedgerSource : ILedgerSource` that forwards every member. In `AccountTransactionsAsync` it sums the time spent awaiting the inner enumerator (source: N + P) apart from the time between a yield and the next `MoveNextAsync` (consumer: C, our reconciliation), and counts records. The consumer figure also contains the harness's own two `Stopwatch` reads per record, well below a microsecond each.

Stand: `setup`, `resume` three times, `raw` at limit 400 and 200, 50 pages each. Testnet: `raw` at limit 400 and 200, 50 pages each; no `warning: load` appeared and no retry was needed.

The stand's writer holds 32,064 records, which one resume walks in a single enumeration (the resume found 7,936 of 40,000 chunks missing).

Deviations from the plan's method: `response.Raw` is a `RawJson` (`ToArray()`, `Length`, `ToJsonElement()`), not `ReadOnlyMemory<byte>`; the thread allocation counter does not survive async hops, so requests are measured with the process-wide precise counter and only the synchronous parse with the thread counter.

## Stand

### Paging with the SDK client (`raw`, limit 400, 50 pages of 400 records)

| | median | p90 |
|---|---:|---:|
| N, ms per page | 68.9 | 78.1 |
| P, ms per page | 2.9 | 9.7 |
| Frame, bytes per page | 1,236,150 | 1,236,575 |
| Frame, bytes per record | 3,090 | 3,091 |

End to end (requests and parses back to back): 5,228 records/s. A repeat gave N 67.4 / 75.0 ms, P 5.1 / 9.4 ms, 5,302 records/s: P's median moves between 2.7 and 5.1 ms from run to run, which is collector noise on a 1.2 MB frame, and does not change anything below.

### The resume (`resume`, three runs, one enumeration of 32,064 records each)

| Run | Source N + P, ms | Consumer C, ms | N + P per 400 records, ms | C per 400 records, ms | Records/s end to end |
|---|---:|---:|---:|---:|---:|
| 1 | 5,306 | 651 | 66.19 | 8.12 | 5,383 |
| 2 | 5,659 | 646 | 70.60 | 8.06 | 5,085 |
| 3 | 5,665 | 656 | 70.67 | 8.19 | 5,072 |
| median | 5,659 | 651 | 70.60 | 8.12 | 5,085 |

Three runs are too few for a 90th percentile; C's spread is 8.06 to 8.19 ms per 400 records. The whole resume took 7.0 s (11.4 s the first time, a cold file cache), of which the history read is about 6.3 s; the rest is hashing the file and the fence wait. The resume's source time matches the `raw` run (66 to 71 ms per 400 records against 72 for N + P there).

## Testnet (`raw`, read-only)

| | limit 400 | limit 200 |
|---|---:|---:|
| Pages fetched | 50 | 50 |
| Records per page (median) | **200** | 200 |
| N, ms per page, median / p90 | 436.5 / 444.8 | 416.5 / 423.3 |
| N, first page (cold connection) | 1,258 | 1,237 |
| P, ms per page, median / p90 | 1.42 / 3.17 | 1.64 / 3.29 |
| Frame, bytes per page (median) | 621,989 | 621,989 |
| Frame, bytes per record | 3,110 | 3,110 |
| Wall, s | 22.75 | 21.86 |
| Records/s end to end | 440 | 457 |

The node clamps `limit` to 200 for a client that is not an administrator, so a request for 400 returns 200 records and the two columns are the same request. The stand answers an admin connection and returns the full 400. The library's page size of 400 therefore reads as 200 records per round trip on a public node, which makes a history of the stand's size cost twice the round trips it appears to.

## Page size, 400 against 200

- Testnet: 457 records/s at 200 against 440 at 400, 3.9% faster, but the two are the same request (the node serves 200 either way), so the difference is run-to-run noise.
- Stand: N is 34.8 ms for 200 records and 68.9 ms for 400, a line of 0.17 ms per record plus about 0.7 ms per request. The per-request overhead is about 1% of a 400-record page; 400 reads 5% to 6% faster end to end than 200 (5,228 to 5,302 against 5,000 records/s).

200 is not faster by more than 10% anywhere, so no other page size is recommended; this plan keeps 400.

## Allocation per page (stand, limit 400)

Process-wide precise counter around the request, and the thread counter around the synchronous parse, median of 40 pages each.

| | Before Task 1 | After Task 1 |
|---|---:|---:|
| Request: SDK frame, plus SDK `JsonElement` for `GRequest<object>` | 2,938,136 | 1,278,248 |
| Parse: `Raw.ToJsonElement()` clone, records, marker; after: pooled `ParseValue`, records, marker | 3,565,060 | 1,906,312 |
| Per page | 6,503,196 | 3,184,560 |

The `raw` mode's old-path run measured 6,502,952 bytes per page in total against these 6,503,196. Both parses allocate the same records (about 1.6 MB of strings for 400 records); what disappeared are two page-sized large-object-heap arrays, the SDK's `JsonElement` and our clone, leaving the SDK's frame as the only one. Task 1's own test bounds the parse of a frame into pooled buffers below 64 KiB (`A_page_is_parsed_from_the_frame_into_pooled_buffers`); it asserts a bound and records no figure, so the figures above are this harness's.

Task 2's stand figures (`ResumeMemoryStandaloneTests`, Release): floor 8,380,416 bytes, after unlock 12,320,768, resume peak 78,995,456, 32,088 of 40,000 chunks found; `PublicationMemoryTests`: peak(50) 123,867,136, peak(200) 135,233,536, peak(800) 156,327,936, raw slope 32.5 MB (13.0 MB net of the fake's records, the asserted figure).

## Decision

Per 400 records, with N and P from testnet (two 200-record pages of the limit-400 run) and C from the stand:

| Term | ms per 400 records |
|---|---:|
| N | 873.0 |
| P | 2.84 |
| C | 8.12 |
| N + P + C | 883.96 |

- Our share **S = (P + C) / (N + P + C) = 0.0124**, a little over one percent.
- Projected saving of overlapping the source with the consumer **V = min(N + P, C) / (N + P + C) = 0.0092**, below one percent: the consumer is so much shorter than the source that overlapping hides about 8 ms of every 884.

On the stand, where the network is a local socket, S is 0.138 and V is 0.102 (N 68.9, P 2.9, C 8.1), and Task 4's thresholds fail there too. On a public node the history read is the wait for the node: 436 ms per 200 records, of which the parse is 1.4 ms and our reconciliation 4 ms. A 32,064-record history is about 70 s of waiting for the node against 0.65 s of our work.

Task 4 requires S ≥ 0.5 and V ≥ 0.2; both fail by a factor of 40 and more, and the largest term is N.

Decision: NO-GO (N, the node's answer per page: 436.5 ms of every 442 ms spent on a 200-record page)
