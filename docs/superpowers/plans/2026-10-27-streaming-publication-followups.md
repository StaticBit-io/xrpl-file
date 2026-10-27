# Streaming Publication Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close what the 2026-10-26 testnet acceptance and the final review left open: the library carries its share of the history read's memory (D1), "Finished in" counts the whole publication (D2), the resume confirmation reads as waiting for the person (D3), the duration range matches what testnet measured and includes the hash pass, while a resume quote prices only the remaining work (M9, O1), the ticket strategy stops rebuilding memos for their markers (M8), the Accounts screen shows balances (O4), the history read's time is measured before anyone speeds it up (O2), the deferred minors that carry real risk get their fix or test, and the VM acceptance runs again.

**Architecture:** Core's `XrplClientLedgerSource` asks the SDK for a memberless result type and parses each `account_tx` page once, out of the SDK's frame into pooled buffers it returns when the page is parsed; hosts are told about the collector setting in the README and the XML docs. The app computes durations and working time in `XrplFile.App.Core` (`PublicationDuration`, `CostSummary`, `PublicationRun`, `RunModel`); the state file carries the earlier sessions' working time as one optional field the library never reads. The Accounts screen reads balances through the same `ILedgerSource.AccountStateAsync` the estimate card uses, one connection per network. The history-read measurement runs in a scratch harness and lands as a report; speedup code follows only on a measured go.

**Tech Stack:** .NET 10, C# latest, `Xrpl` 11.2.0 (pinned; sources at `E:\GIT\XRPL\XrplCSharp`, tag `v11.2.0`), System.Text.Json, xUnit v3 (`dotnet run` runner), MAUI Blazor (Windows head), Node 22+ `node:test` for `web/`, Docker stand `xrpl-file-ci`, testnet for measurement and acceptance.

**Spec:** `docs/superpowers/specs/2026-10-23-streaming-publication-design.md` (S1–S27). Inputs: `docs/superpowers/reports/2026-10-26-streaming-publication/report.md` section 10 (D1–D3, O1–O4); the final review and the ledger of deferred minors (shared notes, see the brief). Where this plan amends the spec, the amendment is listed under **Spec amendments** and the owning task writes it into the spec file.

## Global Constraints

- Repository: this worktree, `E:\Claude\xrpl-file\streaming-publication-2d065e`, branch `claude/streaming-publication-2d065e`. Every path below is relative to it. Do not push, merge or switch branches.
- `Directory.Build.props`: `net10.0`, `LangVersion latest`, `Nullable enable`, `TreatWarningsAsErrors true`, `GenerateDocumentationFile true`. Every `<see cref>` must resolve.
- No new NuGet packages.
- xUnit v3: `dotnet test` does not work. Run `dotnet run --project tests/<Project> -- -class <FullName>` or `-method <FullName>`. Unit runs exclude both categories: `-trait- "Category=Integration" -trait- "Category=Slow"`. Add `-showLiveOutput` when a step says to read output. Web: `cd web && node --test test/*.test.mjs`. Test namespaces: `XrplFile.Tests` (Core), `XrplFile.App.Core.Tests`, `XrplFile.Keys.Tests`.
- Baselines on `edd370b`: Core 832, Keys 252, App.Core 782 unit tests, web 87; stand Core 44/44, App.Core 1/1.
- Stand: compose project `xrpl-file-ci`, `ws://localhost:16006` (JSON-RPC 15005); every stand run sets `XRPL_FILE_NODE_URL=ws://localhost:16006`. Start or recreate it only from `E:\GIT\XRPL\xrpl-file` with `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d`. Check first with `docker ps --format '{{.Names}} {{.Label "com.docker.compose.project"}}'`. Never touch other compose projects or the process holding port 6006.
- Desktop GUI only in the MauiLab VM (skill `maui-vm-lab`); never launch `XrplFile.App.exe` on the host. Building `app/XrplFile.App/XrplFile.App.csproj` on the host is fine.
- C# style: explicit types, no `var`; `async`/`await` with `CancellationToken` passed through; `ConfigureAwait(false)` in `src/` and `app/XrplFile.App.Core`; comments explain why only; XML docs on public API; `StringComparison.Ordinal`; English only.
- Never change file encodings; save new and edited files as UTF-8. Existing literals carry "—", "…" and "·" and stay as they are.
- New user-facing strings (skill `writing-site-copy`): no "—" in new text; say what the person waits for or does next; one fact in one place. The exact new strings are given in the tasks.
- Never decompile Xrpl NuGet packages. Read `E:\GIT\XRPL\XrplCSharp` with `git show v11.2.0:<path>`; do not switch that repository's branch.
- Commits: conventional, English, subject and body; no attribution lines of any kind (no `Co-Authored-By`, no "Generated with").
- Exact values used by this plan: Batch writing 160 and 70 memos/s; hashing 9,000 and 4,500 chunks/s; one-hour threshold (S12) unchanged; `System.GC.LOHThreshold` = 4,194,304; Argon2id collection threshold 16 MiB (16,384 KiB); `account_tx` page size 400 (unchanged); S27 bounds unchanged (peak(800) < 300,000,000 bytes, slope < 40 MB); `ResumeMemoryStandaloneTests` bounds unchanged (resume peak < 300,000,000 bytes, unlock residue < 64 MiB).

## Review Focus

1. **A history page's marker after its page is released.** The marker goes back to the node on the next request; if it were read from a released document the walk would throw or send garbage. Test in Task 1 (`A_history_walk_releases_each_page_and_sends_its_marker_on`).
2. **A resume after the application restarted.** "Finished in" must include the sessions of the earlier process, which only the state file knows. Test in Task 6 (`A_new_run_instance_resuming_a_state_counts_its_recorded_working_time`).
3. **Accounts on two networks, one unreachable.** The reachable network's balances still show, and only the other network's rows say they could not be read. Test in Task 8 (`A_network_that_cannot_be_reached_marks_only_its_rows`).
4. **A resume quote prices only what is still to come.** When "Resuming costs" appears, the hash pass and the history read are already done (user decision 2026-10-27); the quote must not add a hash pass priced at the remaining chunks either. Tests in Task 5 (`A_resume_quote_prices_only_the_work_after_continue`, `A_resume_summary_prices_only_the_remaining_work`).
5. **The person leaves the Accounts screen while balances are being read.** The read stops, nothing renders after the screen is gone, and no row stays "Checking…". Test in Task 8 (`Leaving_while_balances_are_read_leaves_no_row_checking`).

## Spec amendments

Each owning task appends its line to a new final section `## Amendments (2026-10-27)` of `docs/superpowers/specs/2026-10-23-streaming-publication-design.md`, and Task 5 also edits S11's row in place.

1. **S11 (Task 5).** Batch writing is priced at 160 and 70 memos/s (best and conservative testnet rates, acceptance 2026-10-26), plus the hash pass over the whole file at 9,000 and 4,500 chunks/s (300,344 chunks in 0:32 on the first pass and 1:05 on resume, rounded down). A resume's quote is shown after its hash pass and history read are done, so it prices only the work after Continue: the remaining chunks' writing, settles and verification, without a hash pass (user decision 2026-10-27). The one-account paths are unchanged (their hash pass is at most 64 MiB, seconds). The one-hour threshold of S12 is unchanged.
2. **Run screen, D2 (Task 6).** "Finished in" is the publication's working time: the sum, over its sessions that got past their approval, of each session's time from start to stop less the time a resume confirmation waited for the person. A session that sends nothing (declined, unanswered, refused before submitting) leaves the state file as it was and adds nothing. The sum of earlier sessions is the state's optional `WorkedSeconds`, written by the application when such a session ends; the library carries it and never reads it.
3. **Run screen, D3 (Task 7).** While a resume confirmation waits, the stage line reads "Waiting for you to confirm the cost below.", the progress bar and the run's own Cancel are hidden, and the card's Cancel is the one way to decline.
4. **S27 (Task 2).** S27 bounds the publication run. The vault unlock's transient Argon2id peak (about 1 s at the default 256 MiB cost) is outside it; the lanes are released before the run starts (ruling 2026-10-26).

## Already done (verified on `edd370b`, not redone)

The final review's M1–M6 and I3 are in the code: `EstimateModelConfirmationTests.Discarding_a_small_estimate_leaves_nothing_to_publish_either` and `An_estimate_that_finishes_after_a_newer_one_is_ignored_and_lets_its_file_go` (M1); `EstimateModelSizeTests` (M2); no `EstimateAsync` in `PublicationRun.RunBatchAsync` (M3); `HeldSourceTests.A_fresh_run_whose_file_cannot_be_read_while_it_is_hashed_does_not_offer_a_resume` (M4); `BatchStrategy` constructor validates `MaxRoundMemos` and `MaxAttemptsPerMemo` (`BatchStrategy.cs:40-42`, M5); "170 GB" in `RunModel.cs:267` (M6); `SpendingApprovalTests` (a)–(c) and `PublicationRunBatchTests.A_run_whose_fee_unit_fell_below_the_approved_ceiling_starts_and_finishes` (I3). I1, I2 and M7 are fixed too (`final-fix-report.md`).

---

## File Structure

| File | Change | Task |
|---|---|---|
| `src/XrplFile.Core/NodeAnswer.cs` | gains an owner it disposes, and `Parse` from a UTF-8 span into pooled buffers | 1 |
| `src/XrplFile.Core/XrplClientLedgerSource.cs` | memberless result type; pages parsed once and released; host remarks | 1, 2, (4) |
| `src/XrplFile.Keys/Argon2idKdf.cs` | public remarks on the collection; comment without measurement figures | 2 |
| `app/DesktopGarbageCollection.props` | comment trimmed to mechanism and cap | 2 |
| `README.md` | "Collector settings for hosts" subsection | 2 |
| `src/XrplFile.Core/PublicationSession.cs` | class remarks point at the host setting; two state saves stop masking the error | 2, 10 |
| `tests/XrplFile.ResumeMemoryProbe/*`, `tests/XrplFile.Core.Tests/ResumeMemoryStandaloneTests.cs` | probe reports the configured LOH threshold; named minimum | 2 |
| `docs/superpowers/reports/2026-10-27-history-read/report.md` | O2 measurement | 3 |
| `app/XrplFile.App.Core/PublicationDuration.cs`, `CostSummary.cs`, `PublicationRun.cs` | rates, hash pass, resume quote of the remaining work | 5 |
| `src/XrplFile.Core/PublicationState.cs` | optional `WorkedSeconds`; `Load` validates `Size` | 6, 10 |
| `app/XrplFile.App.Core/PublicationRun.cs`, `RunModel.cs` | working time; waiting line; `RunView.ShowsProgress` | 6, 7, 14 |
| `app/XrplFile.App/Components/Run.razor` | confirmation without bar and second Cancel | 7 |
| `app/XrplFile.App.Core/AccountsModel.cs`, `AccountRow.cs`, `app/XrplFile.App/Components/Accounts.razor` | balances | 8 |
| `tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs` | `BeforeConnect` hook, `ConnectedNetworks` | 8, 14 |
| `src/XrplFile.Core/TicketStrategy.cs` | markers through `RoundMarkers.MarkerHexAt` | 9 |
| `src/XrplFile.Core/FilePublicationSource.cs` | ownership doc on refusal | 10 |
| `src/XrplFile.Core/BatchTrace.cs`, `app/XrplFile.App.Core/RunTraceChannel.cs` | listener cannot throw into the run; bounded completion | 13 |
| `app/XrplFile.App.Core/ReadRun.cs` | keep-awake release cannot replace the outcome | 14 |
| `docs/superpowers/reports/2026-10-27-streaming-publication-followups/` | acceptance report and screenshots | 17 |

---

### Task 1: History pages without our own copies (D1, library part)

D1's root cause: each 400-record `account_tx` page is about 1.2 MB of SDK frame, plus the SDK's `JsonElement` for `GRequest<object, …>`, plus our `Raw.ToJsonElement()` clone, all large-object-heap garbage (`d1-report.md` section 1). The frame is the SDK's; the other two are ours to avoid. `RawJson` (v11.2.0) exposes only `Span`, so one copy remains, into a pooled buffer that is returned when the page is parsed.

**Files:**
- Modify: `src/XrplFile.Core/NodeAnswer.cs`, `src/XrplFile.Core/XrplClientLedgerSource.cs` (`SendAsync`, `Send`, `AskAsync`, both `AskOrAbsentAsync`, `AccountTransactionsAsync`)
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`

**Interfaces:**
- Produces:
  - `internal readonly record struct NodeAnswer(JsonElement Result, string? Warning, IDisposable? Owner = null) : IDisposable` — `Dispose()` disposes `Owner`. Existing two-argument constructions (sink, tests) compile unchanged.
  - `internal static NodeAnswer NodeAnswer.Parse(ReadOnlySpan<byte> result, string? warning)` — empty span gives `new NodeAnswer(default, warning)`; otherwise `Utf8JsonReader` over the span and `JsonDocument.ParseValue(ref reader)` (which copies into `ArrayPool` buffers and returns them on `Dispose`), the document as `Owner`.
  - `internal sealed class UnreadResult` (in `XrplClientLedgerSource.cs`): no members; the `T` of every `GRequest` this source sends, so the SDK deserializes nothing of the page (`RequestManager.DeserializeResult` skips unknown members; `XrplJsonOptions.Default` does not disallow them).
  - `private async Task<NodeAnswer?> AskPageOrAbsentAsync(BaseRequest request, string command, string absentCode, CancellationToken cancellationToken)` — same absent and failure rules as `AskOrAbsentAsync`, returns the owning answer un-cloned; the caller disposes it.
  - `AskAsync` / `AskOrAbsentAsync` keep returning `JsonElement`, now `raw.Owner is null ? raw.Result : raw.Result.Clone()` inside `using` of the answer (small answers keep one copy, as before).
  - `AccountTransactionsAsync`: per page `using` the answer, `ParseTransactions(answer.Result)` into a list, `marker = next.Clone()`, dispose, then yield the list. Page size stays 400.
  - `XrplClientLedgerSink` is unchanged (submit answers are small).

- [ ] **Step 1: Write the failing tests**

Add to `XrplClientLedgerSourceTests`, with a helper `private static byte[] AccountTxPage(int records, string? marker)` that writes a `result` object `{"account":…,"transactions":[…],"marker":…}` of `records` Batch-inner-shaped records (`tx_json` with `Account`, `TransactionType` "AccountSet", `Sequence`, `Fee`, one memo with `MemoType` `MemoLayout.ChunkMarkerHex(i)` and 2,000 hex characters of `MemoData`; `meta` with `TransactionResult` "tesSUCCESS", `TransactionIndex`, `ParentBatchID`; `hash`, `ledger_index`, `validated: true`), about 3 KB a record:

```csharp
[Fact]
public void A_page_is_parsed_from_the_frame_into_pooled_buffers()
{
    byte[] frame = AccountTxPage(400, marker: "m1");
    using (NodeAnswer warm = NodeAnswer.Parse(frame, null)) { }

    long before = GC.GetAllocatedBytesForCurrentThread();
    using (NodeAnswer answer = NodeAnswer.Parse(frame, null))
    {
        Assert.Equal(400, answer.Result.GetProperty("transactions").GetArrayLength());
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    Assert.True(allocated < 64 * 1024, $"parsing a {frame.Length:N0}-byte page allocated {allocated:N0} bytes");
}

[Fact]
public void The_result_type_the_source_asks_for_reads_nothing_of_the_page()
{
    byte[] frame = AccountTxPage(400, marker: null);
    _ = JsonSerializer.Deserialize<UnreadResult>(frame, XrplJsonOptions.Default);

    long before = GC.GetAllocatedBytesForCurrentThread();
    Assert.NotNull(JsonSerializer.Deserialize<UnreadResult>(frame, XrplJsonOptions.Default));
    Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 * 1024);
}

[Fact]
public async Task A_history_walk_releases_each_page_and_sends_its_marker_on()
// Script over the internal NodeAnswer seam: page 1 = Parse(AccountTxPage(3, "{\"ledger\":7,\"seq\":2}" as a JSON object marker)) whose
// Owner is wrapped in a RecordingOwner(inner) that counts Dispose; page 2 = Parse(AccountTxPage(2, null)).
// Assert: 5 records yielded with the hashes of both pages (read after both owners are disposed);
// each RecordingOwner.Disposals == 1; the second request's Marker serialises to exactly the first page's marker JSON.

[Fact]
public async Task A_small_answer_outlives_the_frame_it_came_in()
// Script a server_state answer as NodeAnswer.Parse(bytes, null); FeesAsync returns; then read every field of the
// returned NetworkFees: equal to the scripted values (the element was cloned before the document went back to the pool).
```

`RecordingOwner(IDisposable inner) : IDisposable` is a private test class (`Disposals`, forwards to `inner`).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests`
Expected: build errors for `NodeAnswer.Parse`, `UnreadResult`, the three-argument `NodeAnswer`.

- [ ] **Step 3: Implement the Interfaces above**

`SendAsync` becomes `XrplResponse<UnreadResult> response = await ClientKeeper.For(_client!).SendAsync(() => Send(request, cancellationToken), cancellationToken)…; return NodeAnswer.Parse(response.Raw.Span, response.Warning);` and every arm of `Send` asks `GRequest<UnreadResult, …>`. Comment why once at `UnreadResult`: the source reads only `Raw`, and `object` made the SDK build a page-sized `JsonElement` per page.

If `A_page_is_parsed_from_the_frame_into_pooled_buffers` fails because the shared pool does not keep buffers of this size, stop and report BLOCKED with the measured allocation; do not loosen the bound.

- [ ] **Step 4: Run the source tests and the Core unit suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests`, then `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration" -trait- "Category=Slow"`
Expected: PASS; Core 836 total (832 + 4), 0 failed.

- [ ] **Step 5: Run the stand's transport and history tests**

The real client is exercised only on the stand. With the stand up: `XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TransportStandaloneTests -class XrplFile.Tests.LedgerDataStandaloneTests -class XrplFile.Tests.BatchPublicationStandaloneTests`
Expected: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/NodeAnswer.cs src/XrplFile.Core/XrplClientLedgerSource.cs tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs
git commit -m "fix(core): parse each history page once, out of the SDK's frame" -m "<body: the two page-sized copies removed, the one pooled copy kept and why (RawJson exposes only Span), page size unchanged>"
```

---

### Task 2: D1 for library hosts — documentation and guards

**Files:**
- Modify: `src/XrplFile.Keys/Argon2idKdf.cs`, `app/DesktopGarbageCollection.props`, `README.md` (new subsection at the end of "Publishing a file (.NET)", before "### Publishing without a funded account"), `src/XrplFile.Core/XrplClientLedgerSource.cs` (class `<remarks>`), `src/XrplFile.Core/PublicationSession.cs` (class summary gains one `<para>`), `tests/XrplFile.ResumeMemoryProbe/ResumeProbe.cs`, `tests/XrplFile.Core.Tests/ResumeMemoryStandaloneTests.cs`, `docs/superpowers/specs/2026-10-23-streaming-publication-design.md` (amendment 4)
- Test: `tests/XrplFile.Keys.Tests/Argon2idKdfTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `ResumeProbeResult` gains `long LohThreshold` (last positional parameter): `Convert.ToInt64(AppContext.GetData("System.GC.LOHThreshold") ?? 0, CultureInfo.InvariantCulture)`. `ResumeMemoryStandaloneTests.ProbeResult` mirrors it.

- [ ] **Step 1: Write the failing Argon2id vector test**

The vectors were computed independently with argon2-cffi 25.1.0 (`argon2.low_level.hash_secret_raw`, `Type.ID`, version 0x13, password `"correct horse battery staple"`, salt sixteen 0x07 bytes, 32 bytes out):

```csharp
[Theory]
[InlineData(16_384, 1, 1, "8a06458623ef3ebec7e6704bf05b32eb942ca25b0aeae0be0ded08e750685169")]
[InlineData(16_383, 1, 1, "d972ede7cab4bf85576585f6595a7f6b4f21c5f130a2e6c8c1c5acfdabb74e93")]
[InlineData(16_384, 1, 4, "beff63dcb56d703ac0e56cc4f095fdfc371d0fbd4cf22bb1499be2b48798fc0f")]
public void A_derivation_at_and_below_the_collection_threshold_is_standard_argon2id(int memoryKib, int iterations, int parallelism, string expectedHex)
{
    byte[] derived = new Argon2idKdf().Derive(
        "correct horse battery staple"u8, Enumerable.Repeat((byte)0x07, 16).ToArray(), new KdfParameters(memoryKib, iterations, parallelism), 32);

    Assert.Equal(Convert.FromHexString(expectedHex), derived);
}
```

Run: `dotnet run --project tests/XrplFile.Keys.Tests -- -method XrplFile.Keys.Tests.Argon2idKdfTests.A_derivation_at_and_below_the_collection_threshold_is_standard_argon2id`
Expected: PASS already (it pins the branch against a regression; there is no RED for a guard of unchanged output). If it fails, Konscious and argon2-cffi disagree: stop and report.

- [ ] **Step 2: Make the aggressive collection public knowledge**

`Argon2idKdf`: keep `<inheritdoc cref="IPasswordKdf"/>` and add a class `<remarks>`: a derivation with 16 MiB of working memory or more ends with a blocking, compacting, aggressive full collection (`GCCollectionMode.Aggressive`), so its lanes do not stay committed after the key exists; it pauses every thread of the process for the duration of a full collection of its heap; call `Derive` off any UI thread (`KeyVault` already does, inside `Task.Run`). The inline comment keeps the why (one 64 MiB managed array per lane at the default cost; a forced collection leaves the freed regions committed) and drops the acceptance figures ("341-395 MiB … 100-205 MiB").

- [ ] **Step 3: Trim the props comment, write the host recommendation**

`app/DesktopGarbageCollection.props` comment, exactly:

```xml
  <!--
    The desktop application's collector settings, imported by the application and by
    tests/XrplFile.ResumeMemoryProbe so that the probe measures a resume under exactly these.

    Each account_tx page arrives as one SDK frame of about 1.2 MB, a large-object-heap array that
    only a gen2 collection reclaims. After an unlock the collector's budget for that heap is high
    enough to let hundreds of megabytes of dead pages pile up. At this threshold the frames are
    small objects that die in gen0. The runtime caps the threshold at about 4 MiB.
  -->
```

The removed figures (860 MiB over 98,000 records) go into this task's commit body.

`README.md`, new subsection `### Collector settings for hosts`: a host that resumes Batch publications or reads long histories should set `System.GC.LOHThreshold` to `4194304`, with the csproj snippet `<ItemGroup><RuntimeHostConfigurationOption Include="System.GC.LOHThreshold" Value="4194304" /></ItemGroup>` and the environment form `DOTNET_GCLOHThreshold=0x400000`; why in two sentences (page frames are LOH garbage; after an Argon2id unlock the LOH budget lets them pile up); and that `Argon2idKdf.Derive` at 16 MiB or more runs one aggressive full collection. `XrplClientLedgerSource` class `<remarks>` and `PublicationSession`'s summary each gain one `<para>` saying the same in one sentence with the setting's name and value, pointing at the README section by name.

- [ ] **Step 4: Name the minimum and assert the threshold reached the probe**

`ResumeMemoryStandaloneTests`: `private const long MinimumChunksFound = StopAfterOuters * 6L;` with the doc "Six of every eight chunks an outer carries: the run is stopped mid-round, so the last rounds' chunks may not all have landed." The `Assert.InRange` uses it. After the probe returns: `Assert.Equal(4_194_304, result.LohThreshold);` with the message "the probe did not run under the application's collector settings".

- [ ] **Step 5: Spec amendment 4**

Append the section `## Amendments (2026-10-27)` to the spec with amendment 4 of this plan, verbatim; later tasks insert amendments 1-3 above it so the section reads in numeric order.

- [ ] **Step 6: Run the unit suites and the app build**

Run: Keys and Core unit suites (both exclusions); `dotnet build app/XrplFile.App/XrplFile.App.csproj`
Expected: Keys 255 (252 + 3), Core 836, 0 failed; build 0 warnings, 0 errors.

- [ ] **Step 7: Run the D1 stand test and the S27 Slow test, Release**

Run: `XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -c Release -- -class XrplFile.Tests.ResumeMemoryStandaloneTests -showLiveOutput` (about 7 min), then `dotnet run --project tests/XrplFile.Core.Tests -c Release -- -class XrplFile.Tests.PublicationMemoryTests -showLiveOutput` (about 26 min).
Expected: both PASS with the bounds unchanged. Record the printed figures (floor, after unlock, resume peak, chunks found; peak(50), peak(800), slope) for Task 3's report. On D1 they were 85,323,776 resume peak and 157.8 MB / 21.2 MB.

- [ ] **Step 8: Commit**

```bash
git add src/XrplFile.Keys/Argon2idKdf.cs app/DesktopGarbageCollection.props README.md src/XrplFile.Core/XrplClientLedgerSource.cs src/XrplFile.Core/PublicationSession.cs tests/XrplFile.ResumeMemoryProbe tests/XrplFile.Core.Tests/ResumeMemoryStandaloneTests.cs tests/XrplFile.Keys.Tests/Argon2idKdfTests.cs docs/superpowers/specs/2026-10-23-streaming-publication-design.md
git commit -m "docs: tell library hosts about the collector settings a resume needs" -m "<body: README and XML docs; Argon2id remarks; the props narrative moved here with its figures; probe asserts the LOH threshold; named minimum; S27 amendment; stand and S27 figures from Step 7>"
```

---

### Task 3: Measure where the history read's time goes (O2)

Nothing in the repository changes except the report. Read-only on testnet: only `account_tx`, nothing signed or submitted.

**Files:**
- Create: `docs/superpowers/reports/2026-10-27-history-read/report.md`
- Scratch only (never committed): a console project `history-timing` in your scratchpad, referencing `src/XrplFile.Core/XrplFile.Core.csproj`, `src/XrplFile.Keys/XrplFile.Keys.csproj` and `tests/XrplFile.TestSupport/XrplFile.TestSupport.csproj` by absolute path, importing `app/DesktopGarbageCollection.props`, workstation concurrent GC (as the probe).

**Interfaces:**
- Consumes: Task 1's source (the code being measured); public API only: `ClientKeeper.CreateClient`, `IXrplClient.GRequest<object, AccountTransactionsRequest>`, `XrplClientLedgerSource.ParseTransactions(JsonElement)`, `ILedgerSource`, `PublicationSession.ResumeAsync` (Batch overload), `ResumeApproval`.
- Produces: the report, and its GO/NO-GO line that Task 4 reads.

- [ ] **Step 1: Write the harness's three modes**

1. `raw <url> <account> <limit> <pages>`: pages `account_tx` (forward, `ledger_index_min` −1, `ledger_index_max` −1, `limit`) with the SDK client directly. Per page record: request time N (`Stopwatch` around `await GRequest`), frame bytes (`response.Raw.Length`), parse time P (`ParseTransactions(JsonDocument.Parse(response.Raw.ToArray()).RootElement)`, timed alone), records.
2. `setup <url> <dir>`: the setup of `ResumeMemoryStandaloneTests` copied (fund a writer and a payer from genesis, publish 40,000,000 bytes of `VectorStream` through Batch with `StoppingSink` after 4,000 outers), keeping the state file in `<dir>`; prints the state path and payer.
3. `resume <url> <state> <payer>`: `PublicationSession.ResumeAsync` declined at the quote (as `ResumeProbe` does), over a `TimingLedgerSource : ILedgerSource` that forwards everything and, in `AccountTransactionsAsync`, sums time spent awaiting the inner enumerator (source: N + P) apart from time between a yield and the next `MoveNextAsync` (consumer: C, our reconciliation), and counts records.

- [ ] **Step 2: Stand**

With the stand up: `setup`, then `resume` three times; then `raw` at `limit` 400 and 200, 50 pages each, against the stand writer.

- [ ] **Step 3: Testnet, read-only**

`raw wss://s.altnet.rippletest.net:51233 r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW 400 50`, then the same at 200. If the node refuses or rate-limits (`warning: load`), wait a minute and retry once; record it.

- [ ] **Step 4: Write the report**

`docs/superpowers/reports/2026-10-27-history-read/report.md`: environment (host, commit, node versions), method, a table per environment of medians and 90th percentiles of N, P per page, C per 400 records, frame bytes per record, records/s end to end; page size 400 against 200 (per-request overhead); Task 1's per-page allocation before and after (old path: `GRequest<object>` + `ToJsonElement`, measured in the harness with `GC.GetAllocatedBytesForCurrentThread`; new path: Task 1's test figure); Task 2 Step 7's stand figures. Then:

- Our share S = (P + C) / (N + P + C), with N and P from testnet and C from the stand.
- Projected saving of overlapping the source with the consumer: V = min(N + P, C) / (N + P + C).
- **GO** for Task 4 when S ≥ 0.5 and V ≥ 0.2; otherwise **NO-GO**, naming the largest term. A page size other than 400 is recommended only if 200 measured faster end to end on testnet by more than 10%; this plan does not change it either way.

The report ends with one line: `Decision: GO` or `Decision: NO-GO (<largest term>)`.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/reports/2026-10-27-history-read/report.md
git commit -m "docs(reports): where the resume's history read spends its time"
```

---

### Task 4 (conditional on Task 3's GO): Ask for the next page while this one is consumed

Skip this task entirely when Task 3's report says NO-GO; record "skipped: NO-GO" in the ledger.

**Files:**
- Modify: `src/XrplFile.Core/XrplClientLedgerSource.cs` (`AccountTransactionsAsync`)
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`

**Interfaces:**
- Consumes: Task 1 (`AskPageOrAbsentAsync`, owning `NodeAnswer`).
- Produces: no signature change. Behaviour: once page k's marker is known, the request for page k+1 is started before page k's records are yielded; at most one page is asked for ahead; the iterator's `finally` cancels the pending request through a linked token source, awaits it, and disposes its answer.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task The_next_page_is_asked_for_before_this_pages_records_are_handed_out()
// Scripted three pages; the seam records "request k" and the consumer records "record of page k".
// Assert the order contains "request 2" before the first "record of page 1", and "request 3" before the first "record of page 2".

[Fact]
public async Task A_walk_stopped_early_cancels_and_releases_the_page_asked_for_ahead()
// Consumer breaks after the first record. Assert the second request's token was cancelled and its RecordingOwner disposed once.
```

- [ ] **Step 2: Run, implement, run**

Run the class (FAIL on order), implement, run the class and the Core unit suite (PASS), then the stand classes of Task 1 Step 5 and `ResumeMemoryStandaloneTests` (Release, PASS, bounds unchanged).

- [ ] **Step 3: Measure again and append to the report**

Rerun Task 3's `resume` mode on the stand and `raw` is unaffected; append "After Task 4" with end-to-end records/s on the stand and the projected testnet figure.

- [ ] **Step 4: Commit**

```bash
git add src/XrplFile.Core/XrplClientLedgerSource.cs tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs docs/superpowers/reports/2026-10-27-history-read/report.md
git commit -m "perf(core): ask for the next history page while this one is read"
```

---

### Task 5: The duration range, as testnet measured it (M9, O1, S11)

**Files:**
- Modify: `app/XrplFile.App.Core/PublicationDuration.cs`, `app/XrplFile.App.Core/CostSummary.cs`, `app/XrplFile.App.Core/PublicationRun.cs` (`ApproveResume`), `docs/superpowers/specs/2026-10-23-streaming-publication-design.md` (S11 row and amendment 1)
- Test: `tests/XrplFile.App.Core.Tests/PublicationDurationTests.cs`, `tests/XrplFile.App.Core.Tests/CostSummaryTests.cs`

**Interfaces:**
- Produces:
  - `PublicationDuration.BatchFastMemosPerSecond = 160` (was 85); `BatchSlowMemosPerSecond = 70` unchanged; `RunModel.BatchPriorMemosPerSecond` unchanged.
  - `public const double HashingFastChunksPerSecond = 9_000;` `public const double HashingSlowChunksPerSecond = 4_500;` (doc: from the acceptance, 300,344 chunks in 0:32 and 1:05, rounded down).
  - `public static DurationRange Hashing(long chunks)`.
  - `ForBatch(BatchPublicationEstimate estimate)` = `ForBatchRemaining(estimate)` + `Hashing(estimate.Chunks)`.
  - `public static DurationRange ForBatchRemaining(BatchPublicationEstimate remaining)` = writing at 160 / 70 + settles + verification (today's `ForBatch` body), no hash pass.
  - `public static CostSummary CostSummary.OfResume(BatchPublicationEstimate remaining, PublishRequest request)` — duration `ForBatchRemaining(remaining)`, transactions and accounts as `Of(BatchPublicationEstimate, …)`.
  - `PublicationRun.ApproveResume`: the Batch branch uses `CostSummary.OfResume`; the one-account branch is unchanged.
  - No history-read rate is added: the quote appears after the history read.

- [ ] **Step 1: Write the failing tests**

In `PublicationDurationTests`, replace the 1.7 GB test's expectations and add three:

```csharp
// The_spec_example_of_a_1_7_GB_file_takes_about_three_to_seven_hours_through_Batch (renamed)
double fixedPart = 106 * 12 + 4 * 1_024 * 0.7 / 400;
Assert.Equal(1_689_977 / 160.0 + fixedPart + 1_689_810 / 9_000.0, range.Shortest.TotalSeconds, precision: 3);
Assert.Equal(1_689_977 / 70.0 + fixedPart + 1_689_810 / 4_500.0, range.Longest.TotalSeconds, precision: 3);
Assert.Equal("3 h 21 min to 7 h 10 min", DurationText.Of(range));

[Fact]
public async Task The_300_MB_acceptance_file_is_priced_around_what_it_took()
// EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 300_343_426, Token); range = ForBatch(estimate).
// Assert range.Shortest <= TimeSpan.FromSeconds(37 * 60 + 53) <= range.Longest (the run's net wall time),
// and Assert.True(range.Longest > CostSummary.ConfirmationDuration) (still "large").

[Fact]
public async Task A_resume_quote_prices_only_the_work_after_continue()
// remaining = EstimateBatchAsync(…, 202_040_000, Token)  (Chunks 202,040).
// quote = ForBatchRemaining(remaining); fresh = ForBatch(remaining).
Assert.Equal(202_040 / 9_000.0, (fresh.Shortest - quote.Shortest).TotalSeconds, precision: 3);
Assert.Equal(202_040 / 4_500.0, (fresh.Longest - quote.Longest).TotalSeconds, precision: 3);
```

In `CostSummaryTests`: `A_resume_summary_prices_only_the_remaining_work` — `CostSummary.OfResume(remaining, request).Duration == PublicationDuration.ForBatchRemaining(remaining)`, transactions `remaining.Memos + remaining.Batches`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublicationDurationTests -class XrplFile.App.Core.Tests.CostSummaryTests`
Expected: build errors (`ForBatchRemaining`, `OfResume`), then value failures.

- [ ] **Step 3: Implement the Interfaces above**

- [ ] **Step 4: Amend S11 and add amendment 1**

In S11's row, the Batch sentence becomes, verbatim: "**Batch:** writing at 160 and 70 memos/s, plus `LedgerTiming.ApproximateLedgerInterval` × 3 per round (S5); a pass's last round is already one of these, so there is no separate per-pass term; plus the hash pass over the whole file at 9,000 and 4,500 chunks/s. A resume's quote, shown after its hash pass and history read, prices only the remaining chunks' writing, settles and verification." The row's last cell gains: "Rates amended 2026-10-27 from the testnet acceptance (see Amendments)." Append amendment 1 to the Amendments section.

- [ ] **Step 5: Run the App.Core unit suite**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration" -trait- "Category=Slow"`
Expected: 784 (782 + 2 new; the renamed 1.7 GB test is not new), 0 failed. A test that relied on the 85 memos/s figure for a threshold decision fails here: fix its fixture size, never the threshold.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App.Core/PublicationDuration.cs app/XrplFile.App.Core/CostSummary.cs app/XrplFile.App.Core/PublicationRun.cs tests/XrplFile.App.Core.Tests/PublicationDurationTests.cs tests/XrplFile.App.Core.Tests/CostSummaryTests.cs docs/superpowers/specs/2026-10-23-streaming-publication-design.md
git commit -m "fix(app): price Batch time at the measured rates, with the hash pass, and quote a resume's remaining work"
```

---

### Task 6: "Finished in" counts the whole publication (D2)

Decision (amendment 2): the working time is the sum over sessions that got past their approval of (stop − start − time waited for the person), persisted as the state's optional `WorkedSeconds`, written by the app when such a session ends. A session that sent nothing writes nothing, which keeps today's guarantee that a declined, unanswered or Lock-stopped resume leaves the state file byte for byte as it was (`ResumeConfirmationTests.A_declined_resume_sends_nothing_and_stays_resumable` and the Lock and timeout tests assert exactly that, and must pass unchanged). A successful run's state is deleted, so its last segment lives in memory only.

**Files:**
- Modify: `src/XrplFile.Core/PublicationState.cs`, `app/XrplFile.App.Core/PublicationRun.cs` (`RunSnapshot`, fields, `Snapshot`, `Begin`, `Reset`, `RunAsync`, `ApproveResume`), `app/XrplFile.App.Core/RunModel.cs` (`StoppedTextOf`), the spec (amendment 2)
- Test: `tests/XrplFile.Core.Tests/PublicationStateTests.cs`, `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs`, `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`

**Interfaces:**
- Produces:
  - `PublicationState` gains the last positional parameter `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WorkedSeconds = null`, documented: "How long the sessions before this one worked, in whole seconds, as the application measured it. The library carries it and never reads it. Absent until a session that sent something has ended."
  - `RunSnapshot` gains `public TimeSpan WorkedBefore { get; init; }` (earlier sessions, from the state) and `public TimeSpan WaitedForPerson { get; init; }` (this session's time on a resume confirmation).
  - `PublicationRun`: `_workedBefore`, `_waited` (both reset in `Begin` and `Reset`); `RunAsync` sets `_workedBefore = TimeSpan.FromSeconds(recorded?.WorkedSeconds ?? 0)` under the gate with the generation check; `ApproveResume` adds the time between showing the confirmation and its end to `_waited` in its `finally`; after a non-success terminal state, `private void RecordWorked(RunAttempt attempt, int generation)` writes `WorkedSeconds = floor(WorkedBefore + (FinishedAt − StartedAt − WaitedForPerson))` to `attempt.StatePath` when `attempt.Point == RunFailurePoint.WhileSubmitting` and the file exists (`PublicationState.Load` → `with` → `Save`); IO, access, format and JSON failures are swallowed (the figure is shown, never decided on).
  - `RunModel.StoppedTextOf` for `Succeeded`: `Finished in {Clock(WorkedBefore + (FinishedAt − StartedAt − WaitedForPerson))}`. Stopped and failed lines keep this session's elapsed time.

- [ ] **Step 1: Write the failing tests**

```csharp
// PublicationStateTests
[Fact]
public void The_working_time_round_trips_and_is_absent_until_set()
// A state without it serialises without "WorkedSeconds"; with WorkedSeconds = 645 it reloads as 645.
// LegacyStateTests (byte-for-byte fixtures) must pass unchanged.

// RunModelEtaTests
[Fact]
public void A_resumed_run_finishes_in_the_whole_publications_working_time()
{
    RunSnapshot published = Running("manifest", 1, 1, batch: true, TimeSpan.FromSeconds(26 * 60 + 43)) with
    {
        State = PublicationRunState.Succeeded,
        FinishedAt = Now,
        WorkedBefore = TimeSpan.FromSeconds(10 * 60 + 45),
        WaitedForPerson = TimeSpan.FromSeconds(19),
    };

    Assert.Equal("Finished in 37:09", RunModel.StageTextOf(published, Now, rate: null));
}
// The existing A_published_run_shows_how_long_it_took_and_stops_counting keeps "Finished in 4:26".

// ResumeConfirmationTests — ReadyAsync and InterruptedExpensiveAsync gain an optional TimeProvider? time = null passed to PublicationRun.
[Fact]
public async Task A_resumed_publication_counts_both_sessions_and_not_the_wait_for_the_person()
// ManualTimeProvider time. Interrupt: the Transmitting hook advances time by 10 min, then cancels at ordinal 1.
// Assert PublicationState.Load(statePath).WorkedSeconds == 600.
// Resume; when Confirmation is shown, advance 3 min, confirm; the hook advances 5 min at the resumed run's first transmission.
// After Completion: Snapshot.WorkedBefore == 600 s, WaitedForPerson == 180 s,
// RunModel.StageTextOf(snapshot, time.GetUtcNow(), null) == "Finished in 15:00".

[Fact]
public async Task A_new_run_instance_resuming_a_state_counts_its_recorded_working_time()
// Interrupt as above (600 s recorded), then resume with a NEW PublicationRun over the same paths;
// after Completion its Snapshot.WorkedBefore == 600 s (the figure came from the file, not from memory).
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationStateTests -class XrplFile.Tests.LegacyStateTests`, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunModelEtaTests -class XrplFile.App.Core.Tests.ResumeConfirmationTests`
Expected: build errors for the new members.

- [ ] **Step 3: Implement the Interfaces above, and add amendment 2 to the spec**

- [ ] **Step 4: Run the Core and App.Core unit suites**

Expected: Core 837 (836 + 1), App.Core 789 (786 + 3), 0 failed; `LegacyStateTests` and the three state-unchanged resume tests pass unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/PublicationState.cs app/XrplFile.App.Core/PublicationRun.cs app/XrplFile.App.Core/RunModel.cs tests/XrplFile.Core.Tests/PublicationStateTests.cs tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs docs/superpowers/specs/2026-10-23-streaming-publication-design.md
git commit -m "fix(app): show a resumed publication's whole working time when it finishes"
```

---

### Task 7: The resume confirmation reads as waiting for the person (D3)

**Files:**
- Modify: `app/XrplFile.App.Core/RunModel.cs` (`StageTextOf`, `RunView`, `Capture`), `app/XrplFile.App/Components/Run.razor`, the spec (amendment 3)
- Test: `tests/XrplFile.App.Core.Tests/RunModelTests.cs`, `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`

**Interfaces:**
- Produces:
  - `public const string RunModel.AwaitingConfirmationText = "Waiting for you to confirm the cost below.";`
  - `StageTextOf`: a running snapshot with `Confirmation is not null` returns `AwaitingConfirmationText`, with no elapsed time (checked before the "Starting" branch).
  - `RunView` gains `bool ShowsProgress` (last positional parameter): false while `Confirmation is not null`. `Capture` sets `CanCancel` to `Running && Confirmation is null`.
  - `Run.razor`: the progress bar and the bottom action bar render only when `view.ShowsProgress`; the card's decline button is labelled `Cancel` and calls `Model.DeclineResume()`; Continue is unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_resume_waiting_for_its_confirmation_reads_as_waiting_for_the_person()
// Snapshot: Running, Progress ("history", 98_000, 0), StartedAt = now - 5:02, Confirmation = a CostSummary.
Assert.Equal("Waiting for you to confirm the cost below.", RunModel.StageTextOf(snapshot, now, rate: null));

// ResumeConfirmationTests
[Fact]
public async Task While_a_confirmation_waits_the_screen_offers_one_way_to_decline_and_no_progress()
// InterruptedExpensiveAsync, Resume, wait for Confirmation; RunModel model = new(run); RunView view = model.Capture():
// view.ShowsProgress false, view.CanCancel false, view.Confirmation not null, view.StageText == RunModel.AwaitingConfirmationText.
// Then AnswerConfirmation(false) and await Completion (nothing else asserted after the answer).
```

- [ ] **Step 2: Run, implement, run**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunModelTests -class XrplFile.App.Core.Tests.ResumeConfirmationTests` (FAIL), implement, run again (PASS), then `dotnet build app/XrplFile.App/XrplFile.App.csproj` (0 warnings, 0 errors) and the App.Core unit suite (791, 0 failed). Add amendment 3 to the spec.

- [ ] **Step 3: Commit**

```bash
git add app/XrplFile.App.Core/RunModel.cs app/XrplFile.App/Components/Run.razor tests/XrplFile.App.Core.Tests/RunModelTests.cs tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs docs/superpowers/specs/2026-10-23-streaming-publication-design.md
git commit -m "fix(app): show a waiting resume as waiting for the person, with one way to decline"
```

---

### Task 8: Balances on the Accounts screen (O4)

The estimate card reads balances with `ILedgerSource.AccountStateAsync` over `IConnectionFactory.ConnectAsync(network)` (`PublicationEstimate.cs:120,212-213`); `AccountsModel.RefreshFromLedgerAsync` already makes the same call for one row. This task reuses it for every row, one connection per network, when the list opens.

**Files:**
- Modify: `app/XrplFile.App.Core/AccountsModel.cs`, `app/XrplFile.App.Core/AccountRow.cs`, `app/XrplFile.App/Components/Accounts.razor`, `tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs`
- Test: `tests/XrplFile.App.Core.Tests/AccountsModelTests.cs`

**Interfaces:**
- Produces:
  - `AccountRow`: `public bool CheckFailed { get; init; }` (the last read of this row failed).
  - `AccountsModel`:
    - `public async Task RefreshAllFromLedgerAsync(CancellationToken cancellationToken)`: rows not `Closed` and not already checking, grouped by `Network` (ordinal); per group one `ConnectAsync`, then each row checked in order; a connection failure marks every row of its group `CheckFailed` and sets `Message` (`PublishErrorMessages.For(ex)`), other groups go on; cancellation propagates after clearing `_checking` for every row it added.
    - `private async Task CheckOnAsync(ILedgerConnection connection, AccountRow row, CancellationToken cancellationToken)`: today's body of `RefreshFromLedgerAsync` after the connection (balance, current ledger, sequence, stage), setting `CheckFailed = false` on success and `true` on an `XrplFileException`; the vault `UpdateAsync` runs only when `deletableFrom != row.DeletableFromLedger`. `RefreshFromLedgerAsync` opens its connection and calls it.
    - `public string BalanceTextOf(AccountRow row)`, in this order: checking → `CheckingBalance`; `Closed` → `string.Empty`; `CheckFailed` → `UnreadBalance`; `BalanceDrops is null` → `string.Empty`; `KeyOnly` → `NotFundedBalance`; otherwise `XrpAmount.Format(row.BalanceDrops.Value)`.
    - `public const string CheckingBalance = "Checking…";` `public const string NotFundedBalance = "Not funded";` `public const string UnreadBalance = "Not available. Press Check to try again.";`
  - `FakeConnectionFactory`: `public Func<string, CancellationToken, Task>? BeforeConnect { get; set; }` awaited at the start of `ConnectAsync` (a test may throw from it, gate on it, or ignore the token in it); `public List<string> ConnectedNetworks { get; } = [];`.
  - `Accounts.razor`: a `Balance` column after `Network` showing `Model.BalanceTextOf(row)`; the `Funded` status line drops its balance span (one fact, one place); `OnInitializedAsync` and `ReturnToListAsync` start `ReadBalancesAsync()` after `RefreshAsync` without awaiting it before the first render; `ReadBalancesAsync` awaits `Model.RefreshAllFromLedgerAsync(_lifetime.Token)`, ignores `OperationCanceledException`, and calls `InvokeAsync(StateHasChanged)` only when not `_disposed`. The header comment's "per-row Check button rather than something this screen does for every row" paragraph is rewritten to say the list reads every row once on open, one connection per network.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Opening_the_list_reads_every_accounts_balance_with_one_connection_per_network()
// Three generated keys: two on testnet (one funded 25 XRP, one unfunded), one on devnet (funded 5 XRP).
// After RefreshAsync + RefreshAllFromLedgerAsync: ConnectedNetworks == ["devnet", "testnet"] in any order, Connections == 2;
// BalanceTextOf: "25 XRP"-formatted (XrpAmount.Format(25_000_000)), NotFundedBalance, XrpAmount.Format(5_000_000).

[Fact] public async Task A_row_reads_as_checking_while_its_balance_is_read()
// BeforeConnect awaits a TaskCompletionSource. Start RefreshAllFromLedgerAsync without awaiting: every row's BalanceTextOf == CheckingBalance.
// Release; await; none is.

[Fact] public async Task A_network_that_cannot_be_reached_marks_only_its_rows()
// BeforeConnect throws XrplFileException(NodeUnreachable) for "devnet". The devnet row: CheckFailed, BalanceTextOf == UnreadBalance;
// Message == PublishErrorMessages.For(that exception); the testnet rows show their balances.

[Fact] public async Task A_closed_account_is_not_asked_about_when_the_list_opens()
// Only a closed row: Connections == 0, BalanceTextOf == string.Empty.

[Fact] public async Task Reading_balances_writes_the_vault_only_when_the_closable_ledger_moved()
// A funded row read once (the vault entry gains DeletableFromLedger), then the vault file's bytes captured, then read again with the
// same sequence: the vault file's bytes are unchanged.

[Fact] public async Task Leaving_while_balances_are_read_leaves_no_row_checking()
// BeforeConnect awaits Task.Delay(Infinite, token). Cancel the token passed to RefreshAllFromLedgerAsync:
// the task ends with OperationCanceledException, IsChecking is false for every row, rows unchanged.
```

- [ ] **Step 2: Run, implement, run**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.AccountsModelTests` (FAIL), implement, run again (PASS; the existing nine tests unchanged), App.Core unit suite (797, 0 failed), app build (0 warnings, 0 errors).

- [ ] **Step 3: Commit**

```bash
git add app/XrplFile.App.Core/AccountsModel.cs app/XrplFile.App.Core/AccountRow.cs app/XrplFile.App/Components/Accounts.razor tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs tests/XrplFile.App.Core.Tests/AccountsModelTests.cs
git commit -m "feat(app): show every account's balance on the Accounts screen"
```

---

### Task 9: The ticket strategy reads markers without the file (M8)

**Files:**
- Modify: `src/XrplFile.Core/TicketStrategy.cs` (`SubmitAllAsync`: the duplicate-marker loop at :137-148 becomes an index loop; :188, :209, :240, :245)
- Test: `tests/XrplFile.Core.Tests/TicketStrategyTests.cs`

**Interfaces:**
- Consumes: `RoundMarkers.MarkerHexAt(IReadOnlyList<MemoSubmission> memos, int index)` (internal, `RoundMarkers.cs:42`). Every marker read in `SubmitAllAsync` goes through it; `memos[index]` stays only where the memo's data is needed (`SendOneAsync` at :490 and the sweep's data at :217).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_lazy_chunk_pass_reads_each_chunk_once_to_send_it_and_once_to_sweep_for_it()
{
    CountingPublicationSource source = new(new VectorPublicationSource("tickets", 50_000));
    LazyMemoList memos = new(source, IndexRanges.Of([(0, 50)]));
    // FakeLedgerSink sink / FakeLedgerSource ledger as the other tests in this class; Strategy(sink, ledger, ticketsPerBatch: 50).
    SubmittedMemos landed = await strategy.SubmitAllAsync(memos, null, Token);

    Assert.Equal(50, landed.Landed.Count);   // or the SubmittedMemos member the other tests use for "all landed"
    Assert.Equal(2 * 50, source.Reads);
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -method XrplFile.Tests.TicketStrategyTests.A_lazy_chunk_pass_reads_each_chunk_once_to_send_it_and_once_to_sweep_for_it`
Expected: FAIL with 250 reads (five per memo: the duplicate check, `wanted`, send, sweep data, result).

- [ ] **Step 3: Implement, run the class and the Core unit suite**

Expected: PASS; Core 838, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add src/XrplFile.Core/TicketStrategy.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs
git commit -m "perf(core): read ticket-pass markers without rebuilding the memos"
```

---

### Task 10: Minors — sources and state (m1–m6)

**Files:**
- Modify: `src/XrplFile.Core/FilePublicationSource.cs` (doc of the `SafeFileHandle` constructor), `src/XrplFile.Core/PublicationState.cs` (`Load`), `src/XrplFile.Core/PublicationSession.cs` (`RecordFailure`, `RecordRejectedManifest`)
- Test: `tests/XrplFile.Core.Tests/FilePublicationSourceTests.cs`, `SessionSourceTests.cs`, `PublicationSessionBatchResumeTests.cs`, `PublicationStateTests.cs`, `WindowConflictTests.cs`, `ManifestAcceptanceTests.cs`

**Interfaces:**
- Produces:
  - m1: the constructor's doc: "Takes ownership of the handle once it is constructed. A handle it refuses stays the caller's and is not closed."
  - m4: `PublicationState.Load` throws `FormatException` for `Size < 1` or `Size > PublicationSession.MaxPublicationBytes`, in both versions, with the message "a state records a size of {Size} bytes, which no run can have".
  - m5, m6: both saves catch `IOException` and `UnauthorizedAccessException`, keep the in-memory state, set `_stateSaveFailure ??= e`, and return, so the caller's `WriterWindowConflict` or `ManifestRejected` (with its `Link`) is what is thrown. Comment why: the conflict is found again by the next resume's reconciliation, and a recovered manifest is checked again (B27, S22).

- [ ] **Step 1: Write the failing tests**

```csharp
// FilePublicationSourceTests.A_handle_that_cannot_seek_is_refused gains: Assert.False(handle.IsClosed); (m1)

// SessionSourceTests (m2): borrowed sources are never disposed by the session
[Fact] public async Task A_session_never_disposes_a_source_it_borrowed()
// CountingPublicationSource over a VectorPublicationSource: one-account StartAsync + RunAsync, then a stopped run's ResumeAsync,
// Batch StartAsync + RunAsync, Batch ResumeAsync: after each, Assert.False(source.Disposed).

// PublicationSessionBatchResumeTests (m3)
[Fact] public async Task A_Batch_resume_of_a_file_of_another_size_is_a_changed_file_refused_before_the_ledger_is_read()
// ResumeMismatch with SourceChanged true; FakeLedgerSource.AccountTransactionsCalls unchanged; no transmission.

// PublicationStateTests (m4)
[Theory] [InlineData(0L)] [InlineData(-1L)] [InlineData(16L * 1024 * 1024 * 1024 + 1)]
public void A_state_whose_size_no_run_could_have_is_refused(long size)  // FormatException from Load

// WindowConflictTests (m5)
[Fact] public async Task A_conflict_whose_record_cannot_be_written_still_fails_with_the_conflict()
// As A_conflicting_copy_in_the_window_fails_the_resume_and_is_recorded, but before ResumeAsync create a directory at
// statePath + ".writing" (the temporary file PublicationState.Save writes first), so the conflict's save throws.
// Assert: the thrown error is WriterWindowConflict; the state file on disk has no Failure.

// ManifestAcceptanceTests (m6)
[Fact] public async Task A_rejected_manifest_whose_record_cannot_be_written_still_reports_the_rejection_with_its_link()
// As A_manifest_a_resume_recovers_is_checked_too_and_a_rejection_is_not_success, with the same ".writing" directory created
// before ResumeAsync. Assert: ManifestRejected, its Link names the recovered manifest's hash.
```

Make the save fail with a directory at the temporary path, not a read-only attribute: CI runs on Linux, where the attribute does not stop a write. On those two resume paths the failing save is the first save, so nothing earlier trips over the directory.

- [ ] **Step 2: Run, implement, run**

Run the six classes (m1 and m2 may pass at once: they pin behaviour; m3 passes at once if the shared path is right, which is the point of pinning it; m4–m6 FAIL), implement, run the classes and the Core unit suite (Core 845, 0 failed).

- [ ] **Step 3: Commit**

```bash
git add src/XrplFile.Core/FilePublicationSource.cs src/XrplFile.Core/PublicationState.cs src/XrplFile.Core/PublicationSession.cs tests/XrplFile.Core.Tests
git commit -m "fix(core): keep a conflict or a rejected manifest as the error when its record cannot be saved"
```

---

### Task 11: Minors — reconciliation and resume tests (m7–m12)

Tests only; a test that fails is a defect to fix in the same task, named in the commit body.

**Files:**
- Test: `tests/XrplFile.Core.Tests/WindowConflictTests.cs`, `StreamingReconciliationTests.cs`, `SequenceRangesTests.cs`, `tests/XrplFile.App.Core.Tests/RunModelTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
// WindowConflictTests
[Fact] public async Task A_one_account_resume_ignores_a_writer_copy_with_other_bytes()          // m7, I12: resumes, reads back byte for byte
// An_identical_duplicate_is_harmless: plant the duplicate in a ledger after the original's and assert the block's first ledger is the original's   // m8
[Fact] public async Task A_record_of_another_account_carrying_this_files_marker_is_ignored()     // m9: neither landed nor a conflict

// StreamingReconciliationTests
// A_Batch_resume_whose_history_stream_breaks_part_way_starts_it_again_and_pays_for_nothing_twice gains:
//   Assert.Equal(2, ledger.Calls.Count(call => call.FromLedger == state.StartedInLedger)) for the first pass  // m10
[Fact] public async Task A_list_record_whose_index_is_past_this_files_list_count_is_not_kept()          // m11
[Fact] public async Task A_list_record_mixing_this_files_entries_with_another_files_is_not_kept()       // m11
[Fact] public async Task A_finished_run_is_recognised_with_another_publications_manifest_before_its_own() // m11, B27: no second manifest sent
[Fact] public async Task A_record_of_another_account_with_the_writers_sequence_covers_nothing()         // m11, B30

// SequenceRangesTests (m11)
[Fact] public void Disjoint_ranges_stay_apart_and_a_record_bridging_them_joins_them()

// RunModelTests (m12)
[Fact] public void A_run_whose_manifest_was_rejected_is_not_offered_a_resume()   // IsResumable false for a Failed snapshot whose state has ManifestStatus "landed, rejected"
```

- [ ] **Step 2: Run the classes and both unit suites**

Expected: PASS; Core 852, App.Core 798, 0 failed.

- [ ] **Step 3: Commit**

```bash
git add tests/XrplFile.Core.Tests tests/XrplFile.App.Core.Tests/RunModelTests.cs
git commit -m "test: pin what reconciliation keeps, ignores and recognises on a resume"
```

---

### Task 12: Minors — the Batch estimate groups as the strategy sends (m13)

**Files:**
- Test: `tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs`

**Interfaces:**
- Consumes: the internal `PublicationEstimator.EstimateBatchAsync(source, writer, payer, chunks, blocks, lists, token)`; `BatchStrategy` over `FakeBatchLedgerSink` as `BatchStrategyTests` builds it.

- [ ] **Step 1: Write the tests**

```csharp
[Fact] public async Task A_pass_of_exactly_one_round_has_no_solo()   // 16,384 chunks, one block's worth of lists 0: chunk pass 2,048 batches, the manifest the only solo

[Theory] [InlineData(1)] [InlineData(9)] [InlineData(16)] [InlineData(17)] [InlineData(16_384)] [InlineData(16_385)]
public async Task The_estimate_counts_the_batches_and_solos_the_strategy_sends(int memos)
// Strategy: SubmitAllAsync over `memos` chunk memos, every memo landing at its first candidate; count outers and solos transmitted.
// Estimate: EstimateBatchAsync(…, chunks: memos, blocks: 1, lists: 0): Batches == outers; SoloTransactions == solos + 1 (the manifest).
```

If the two 16k cases take more than 30 s each, mark only those two `[Trait("Category", "Slow")]` and say so in the commit body.

- [ ] **Step 2: Run the class and the Core unit suite; commit**

Expected: PASS; Core 859, 0 failed.

```bash
git add tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs
git commit -m "test(core): hold the Batch estimate's grouping to what the strategy sends"
```

---

### Task 13: Minors — the trace cannot stop or hang a run (m14, m15)

**Files:**
- Modify: `src/XrplFile.Core/BatchTrace.cs` (`Add`), `app/XrplFile.App.Core/RunTraceChannel.cs`
- Test: `tests/XrplFile.Core.Tests/BatchTraceTests.cs`, `tests/XrplFile.App.Core.Tests/RunTraceChannelTests.cs`

**Interfaces:**
- Produces:
  - `BatchTrace.Add`: the listener call is wrapped; an exception is counted in `internal long ListenerFailures` and not rethrown; the entry stays in the ring.
  - `RunTraceChannel(Func<IReadOnlyList<string>, Task> write, TimeProvider? time = null)`; `public static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(10);` `CompleteAsync()` waits for the drain at most `CompletionLimit` on that clock (`_drain.WaitAsync(CompletionLimit, _time)`, timeout swallowed), so a hung log write never keeps a run from ending. Existing call sites unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_listener_that_throws_does_not_reach_the_caller_and_the_entry_is_kept()   // BatchTraceTests
[Fact] public async Task Completing_stops_waiting_for_a_write_that_never_returns_after_its_limit() // RunTraceChannelTests
// write = _ => new TaskCompletionSource().Task; ManualTimeProvider; Post one line; Task done = CompleteAsync();
// Assert.False(done.IsCompleted); time.Advance(CompletionLimit); await done.WaitAsync(TimeSpan.FromSeconds(5)).
```

- [ ] **Step 2: Run, implement, run both unit suites; commit**

Expected: Core 860, App.Core 799, 0 failed.

```bash
git add src/XrplFile.Core/BatchTrace.cs app/XrplFile.App.Core/RunTraceChannel.cs tests/XrplFile.Core.Tests/BatchTraceTests.cs tests/XrplFile.App.Core.Tests/RunTraceChannelTests.cs
git commit -m "fix: keep the trace from failing or holding up the run it describes"
```

---

### Task 14: Minors — run, resume and Lock (m16–m23)

**Files:**
- Modify: `app/XrplFile.App.Core/PublicationRun.cs` (`StopAsync` delays on `_time`; `keepAwake?.Release()` in `RunAsync`'s `finally` becomes a quiet release), `app/XrplFile.App.Core/ReadRun.cs` (same quiet release)
- Test: `tests/XrplFile.App.Core.Tests/HeldSourceTests.cs`, `ResumeConfirmationTests.cs`, `PublicationRunBatchTests.cs`, `ReadRunTests.cs`

**Interfaces:**
- Consumes: Task 8's `FakeConnectionFactory.BeforeConnect`; `RecordingExecutionState` (`ThreadKeepAwakeTests.cs:7`).
- Produces: `StopAsync(TimeSpan timeout)` uses `Task.Delay(timeout, _time)`; `private static void ReleaseQuietly(IKeepAwake? keepAwake)` (swallows any exception; the keep-awake is a courtesy, the outcome is the run's) in both runs.

- [ ] **Step 1: Write the tests**

```csharp
// HeldSourceTests
// A_resume_refused_for_another_reason_than_a_changed_file_does_not_mark_the_file gains:
//   Assert.StartsWith(PublishErrorMessages.For(XrplFileError.ResumeMismatch), run.Snapshot.Failure);           // m16
[Fact] public async Task A_resume_whose_recorded_file_cannot_be_opened_asks_for_it_again_without_a_node()     // m17
// openSource throws IOException: Failed, Failure == $"The file at {path} could not be read. Choose it again.", _connections.Connections == 0.

// ResumeConfirmationTests
[Fact] public async Task Cancelling_while_a_resume_waits_for_the_person_sends_nothing_and_stays_resumable()  // m18: state bytes unchanged
[Fact] public async Task Reset_while_a_resume_waits_changes_nothing()                                       // m18: still Running, Confirmation kept
// A_resume_above_the_threshold_waits_for_the_person_and_goes_on_when_confirmed gains (m19):
//   BatchPublicationEstimate whole = await PublicationEstimator.EstimateBatchAsync(_connections.Ledger, writer, payer, 400_000, Token);
//   Assert.True(asked.Transactions < whole.Memos + whole.Batches);   // the quote is the remaining work
//   after AnswerConfirmation(true): await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is null) before awaiting Completion.
[Fact] public async Task A_declined_resume_lets_the_computer_sleep_again_from_the_thread_that_held_it()      // m21
[Fact] public async Task Lock_cancels_the_run_before_it_locks_the_vault()                                    // m22
// BeforeConnect awaits Task.Delay(Infinite, token) and, when cancelled, records vault.IsUnlocked. Assert it recorded true, then vault locked.
[Fact] public async Task Lock_locks_the_vault_even_when_the_run_does_not_stop_within_its_bound()            // m23
// PublicationRun with a ManualTimeProvider; BeforeConnect awaits a TaskCompletionSource ignoring the token. Task lock = unlock.LockAsync();
// Assert.False(lock.IsCompleted); time.Advance(UnlockModel.RunStopTimeout); await lock; Assert.False(vault.IsUnlocked); release the gate;
// await run.Completion; Cancelled.

// PublicationRunBatchTests and ReadRunTests (m20)
[Fact] public async Task A_keep_awake_that_throws_on_release_does_not_change_the_runs_outcome()   // Succeeded stays Succeeded
[Fact] public async Task A_keep_awake_that_throws_on_release_does_not_change_the_reads_outcome()
```

- [ ] **Step 2: Run, implement, run the App.Core unit suite; commit**

Expected: App.Core 807, 0 failed.

```bash
git add app/XrplFile.App.Core/PublicationRun.cs app/XrplFile.App.Core/ReadRun.cs tests/XrplFile.App.Core.Tests
git commit -m "test(app): pin Lock, resume and keep-awake behaviour the branch left uncovered"
```

---

### Task 15: Minors — the Estimate screen's confirmation gate (m24)

**Files:**
- Test: `tests/XrplFile.App.Core.Tests/EstimateModelConfirmationTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
[Fact] public async Task A_ticketed_file_longer_than_an_hour_needs_confirming_even_under_one_XRP()
// One-account request (no payer) of 4,000,000 bytes: Assert.True(model.Estimate!.FeeDropsWorstCase < CostSummary.ConfirmationDrops) first,
// then NeedsConfirmation true, CanPublish false, Approval null.

// Above_the_threshold_Publish_waits_for_the_checkbox gains: Assert.NotNull(model.Approval) after Confirm(true),
// then model.Confirm(false): CanPublish false, Approval null, HandOff() null.
```

- [ ] **Step 2: Run the class and the App.Core unit suite; commit**

Expected: App.Core 808, 0 failed.

```bash
git add tests/XrplFile.App.Core.Tests/EstimateModelConfirmationTests.cs
git commit -m "test(app): pin the Estimate screen's confirmation gate on the time trigger and on unticking"
```

---

### Task 16: Full verification before the acceptance

- [ ] **Step 1: Unit suites and web**

Run the Core, Keys and App.Core unit suites (both exclusions) and `cd web && node --test test/*.test.mjs`.
Expected: 0 failed everywhere; web 87. Record the totals.

- [ ] **Step 2: Builds**

`dotnet build XrplFile.Libraries.slnf` and `dotnet build app/XrplFile.App/XrplFile.App.csproj -c Release`: 0 warnings, 0 errors.

- [ ] **Step 3: Stand integration**

`XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration" -trait- "Category=Slow"` and the same for `tests/XrplFile.App.Core.Tests`. Expected: Core 44/44 (more if a task added stand tests), App.Core 1/1. Then `ResumeMemoryStandaloneTests` and `PublicationMemoryTests` in Release as in Task 2 Step 7, if any task after Task 2 touched `src/XrplFile.Core`: PASS, bounds unchanged.

No commit.

---

### Task 17: Acceptance in the MauiLab VM against testnet (by hand)

Testnet only, with the vault's existing `batch-writer` (`r44DFo16gPv4k3ug61QnLxArL3kLzUbPkW`) and `batch-payer` (`rL54SuvLqRrKtvx5izGEwKCBTdr2KPYzHa`) keys; the payer holds about 91.5 XRP. Budget: the 300 MB run needs about 68.6 XRP on the payer before it starts (worst case) and spends about 6.2; the small run spends about 0.7. Nothing here is fixed; defects are reported.

**The subagent does not write report files.** It saves screenshots and CSVs into the report folder with the guest/host scripts, and returns the report's full Markdown text in its hand-back; the controller writes `report.md` from it and commits.

**Files:**
- Create (controller): `docs/superpowers/reports/2026-10-27-streaming-publication-followups/report.md`; screenshots and `private-bytes-300mb.csv`, `private-bytes-30mb.csv` in the same folder (JPEG via CDP, quality 70, 1280 px, under 100 KB each, as last time)
- Scratch only: `vm-cdp.ps1` (the CDP helper of `docs/superpowers/plans/2026-10-13-dark-theme.md` Task 8 Step 2), `sample-private-bytes.ps1` (as `docs/superpowers/plans/2026-10-25-streaming-publication.md` Task 22 Step 5, with `Start-Sleep -Seconds 5`)

**Interfaces:**
- Consumes: Tasks 1–16; skill `maui-vm-lab` (lab at `D:\Lab\maui-vm-lab`); testnet `wss://s.altnet.rippletest.net:51233`.

- [ ] **Step 1: Deploy and launch**

Read the `maui-vm-lab` skill. On the host: `cd D:\Lab\maui-vm-lab; .\06-deploy-and-run.ps1 -ProjectPath E:\Claude\xrpl-file\streaming-publication-2d065e\app\XrplFile.App\XrplFile.App.csproj -ExeName XrplFile.App.exe -Action Deploy`. Start it non-elevated with DevTools exactly as the 2026-10-26 report section 2 describes (task `XrplPlain`, `-RunLevel Limited`, `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`). Unlock the existing lab vault (its password is the one plan 2026-10-25 Task 22 Step 1 names). Record the deployed commit.

- [ ] **Step 2: Accounts (O4)**

Open Accounts. Expected: every row shows "Checking…" briefly, then a balance (`batch-writer` about 99.99 XRP, `batch-payer` about 91.5 XRP), "Not funded" for an unfunded key, nothing for a closed one; the Status column no longer repeats the balance. Screenshot `01-accounts-balances.jpg`.

- [ ] **Step 3: The files**

On the host make two videos with ffmpeg as the 2026-10-25 plan's Task 22 Step 3 does: `accept-300mb.mp4` (600 s) and `accept-30mb.mp4` (`-t 60`). Copy both to `C:\Lab\`. Record sizes and SHA-256.

- [ ] **Step 4: (b) The fence wait, on the 30 MB file**

Publish › `accept-30mb.mp4`, writer `batch-writer`, paid by `batch-payer`, Estimate (screenshot `02-estimate-30mb.jpg`: record the range), tick if asked, Publish; start the sampler with output `C:\Lab\private-bytes-30mb.csv`. Press Lock as soon as the line reads "Writing the file" with a count above 0 (the fence is then about 22 ledgers ahead, a minute or more on testnet). Unlock at once, open Publish › Unfinished runs, press Resume at once; note each time. Expected: "Hashing the file", then the fence line "Waiting for earlier transactions to expire — about …" (screenshot `03-fence-wait.jpg`), then "Reading the ledger history", then the confirmation if above the threshold (screenshot `04-confirm-30mb.jpg`: the line reads "Waiting for you to confirm the cost below.", no progress bar, one Cancel inside the card, none below it); Continue; "Published." with "Finished in …" (screenshot `05-published-30mb.jpg`). If the fence had passed before the resume reached it, say so, and repeat the step once more faster.

- [ ] **Step 5: (a) The 300 MB file with Lock and Resume, memory sampled**

Publish `accept-300mb.mp4` the same way; screenshot the estimate (`06-estimate-300mb.jpg`: the range should read about 36 min to 1 h 17 min, and "This publication is large"). Start the sampler (`C:\Lab\private-bytes-300mb.csv`). About ten minutes in, Lock; wait about a minute; Unlock (note the press time); Resume. Expected: no file dialog, hashing, history, the confirmation as in Step 4 with "Time" pricing only the remaining work, not the hash pass or the history read already done (screenshot `07-confirm-300mb.jpg`); wait about 20 s, then Continue; then "Published." with "Finished in …" (screenshot `08-published-300mb.jpg`). Stop the sampler and copy both CSVs to the report folder.

- [ ] **Step 6: (d) Read back both links**

In Read, look each link up (screenshot `09-read-lookup.jpg` for the 300 MB one), read and save; the saved files' SHA-256 equal Step 3's (screenshot `10-read.jpg`).

- [ ] **Step 7: Restore the guest and hand back**

Delete the sources and read-back copies from the guest, close the application, leave the VM Saved. Return the report text, with: environment and commit; the files; for each run the estimate's range against net wall time (Publish to "Published." less the time from Lock to Resume and less the time the confirmation waited) and against "Finished in" (D2 holds when "Finished in" equals that net figure within the seconds of the waits you timed by hand); the fence wait seen or not and for how long (O3/b); the confirmation's look (D3); the Accounts balances before and after with the payer's spend (O4); private bytes: the peak over the 300 MB run excluding the window from the unlock press to 5 s after it, against 300,000,000 bytes, and the peak inside that window reported separately (D1, a); the read-back hashes (d); every defect with a screenshot and a one-line reproduction.

- [ ] **Step 8: Controller writes and commits the report**

```bash
git add docs/superpowers/reports/2026-10-27-streaming-publication-followups
git commit -m "docs(reports): streaming publication follow-ups acceptance on testnet"
```

---

## Deferred minors

### Taken

| Id | Source | Minor | Task |
|---|---|---|---|
| m1 | Task 1 | `FilePublicationSource(SafeFileHandle)` says it takes ownership, but a refused handle is not disposed | 10 (doc + assertion; triage: "document it") |
| m2 | Task 3 | No test pins 4.1 ownership: a session never disposes a borrowed source | 10 |
| m3 | Task 3 | Batch resume size refusal has no test of its own | 10 |
| m4 | Task 3 | `PublicationState.Load` does not validate `Size` | 10 |
| m5 | Task 5 | `RecordFailure` IOException masks `WriterWindowConflict` | 10 |
| m6 | Task 7 | `RecordRejectedManifest` IOException replaces `ManifestRejected` and loses the link | 10 |
| m7 | Task 5 | No test that a one-account resume ignores a differing writer copy (I12) | 11 |
| m8 | Task 5 | `An_identical_duplicate_is_harmless` does not prove the copy is a later one | 11 |
| m9 | Task 5 | No test for the non-writer skip | 11 |
| m10 | Task 6 | Batch break test does not prove the reconciliation restarted | 11 |
| m11 | Task 6 | S20 cases uncovered: list index ≥ list count, mixed entries, foreign manifest first, disjoint/bridging ranges, foreign record with the writer's sequence | 11 |
| m12 | Task 7 | No test that `RunModel.IsResumable` is false on a rejected manifest | 11 |
| m13 | Task 10 | No boundary test at exactly one round, no estimator-vs-strategy parity test (8 + 1 and `MaxRoundMemos` + 1 already covered) | 12 |
| m14 | Task 8 | `RunTraceChannel.CompleteAsync` waits indefinitely on a hung write | 13 |
| m15 | Task 8 | `BatchTrace.Add` lets a listener exception escape into the strategy | 13 |
| m16 | Task 12 | `A_resume_refused_for_another_reason…` does not assert the reason | 14 |
| m17 | Task 12 | `SourceUnreadableException` path untested | 14 |
| m18 | Task 14 | No cancel or Reset while a resume waits | 14 |
| m19 | Task 14 | Confirmed test does not assert the quote is the remaining work nor `Confirmation` cleared after the answer | 14 |
| m20 | Task 16 | An exception from `Release` in `RunAsync`'s `finally` would replace the outcome | 14 |
| m21 | Task 16 | No declined-resume keep-awake test | 14 |
| m22 | Task 17 | Lock test cannot tell cancel-then-lock from lock-then-cancel | 14 |
| m23 | Task 17 | Lock's 10 s bound untested | 14 |
| m24 | Task 13 | No model tests for the > 1 h trigger, the one-account path, `Confirm(false)`, `Approval` around the tick | 15 |
| D1-1…5 | D1 review | Aggressive GC only in a private comment; no unit test for the ≥ 16 MiB `Derive` branch; nothing asserts the LOH threshold reached the probe; props narrative; `StopAfterOuters * 6L` | 2 |
| D1-6 | D1 review | Unlock peak vs S27 wording | 2 (amendment 4) |

### Not taken (46)

| Source | Minor | Reason |
|---|---|---|
| Task 1 | `BlockHashesAsync` has no per-block cancellation check | Final review: leave, every read takes the token |
| Task 1 | `MemoryPublicationSource` reads after dispose succeed | Final review: leave; it holds no resource |
| Task 1 | `VectorPublicationSource` duplicate checks, missing dispose tests | Final review: leave; test support only |
| Task 2 | Block-range test cannot tell a range from one ledger | Final review: leave; not money, resume or Lock |
| Task 2 | Dead `onDisk` local in `PublicationSessionTests` | Harmless dead code; final review: fix only if touching the file, and no task does |
| Task 2 | Stale `Advance` remark | Verified on `edd370b`: the remark describes the current rule |
| Task 2 | `LandedSet` concurrency wording; writer-guard tests | Final review: leave; the wording is accurate for writers |
| Task 3 | Batch start snapshot before the hash pass | Final review: leave; an earlier floor is the safe direction |
| Task 3 | Writer's byte overload never disposes its memory source | It wraps memory and holds nothing |
| Task 3 | Reconcile reads the chunk before a length check; `Missing` walks bit by bit | Final review: leave; cost only |
| Task 4 | Duplicate-marker refusal test, `TransactionIndex` without meta, floor test over calls, growing seams | Final review: leave; not money, resume or Lock |
| Task 5 | Redundant `IsLanded` guard | Final review: leave; a report's rationale, not code behaviour |
| Task 5 | `IsResumable` treats any `Failure` as final | Final review: `Failure` has one value |
| Task 6 | One-account streaming pass not retried | Final review: S20 mandates the restart for B30 only |
| Task 6 | `SequenceRanges.Add` linear `FindIndex` | Final review: leave; cost only, one range in practice |
| Task 7 | `RequireReachable` cannot fail on the writer path | Final review: leave; invariant guard |
| Task 7 | No test that the ticket path calls `RequireListed` | Ticket verification, not money, resume or Lock |
| Task 8 | `Task.Delay(50)` in a drain test | Test timing only |
| Task 8 | No test of the "(no state file)" header | Diagnostic text only |
| Task 8 | "dropped N" written after the end line | Diagnostic log order only |
| Task 8 | `RunTraceChannel.Capacity` without XML doc | Documentation completeness only |
| Task 9 | S26 boundary cases; `InRange(peak, 0, cap)` | Final review: leave; reader cache, not money, resume or Lock |
| Task 9 | Second `ReaderOptions` in `XrplFileReader` | Same values; no behaviour differs |
| Task 9 | Negative cache cap behaves like 0 | Internal option with no public caller |
| Task 10 | Estimator uses the default round size | Final review: leave; worst cases do not depend on grouping and the app uses the default |
| Task 10 | `Rounds` doc mentions a duration member | Resolved: `PublicationDuration` exists |
| Task 11 | Payer-mismatch `ArgumentException`, XML docs, `AtCeiling` multiples | Final review: leave; `AtCeiling` is exact |
| Task 12 | `Resume` with a non-null `Source` undocumented | Behaviour is correct; documentation only |
| Task 12 | Assignment in an expression | Style |
| Task 12 | `EstimateModel` opens the file synchronously | Opening a handle is fast; no wrong behaviour |
| Task 12 | Stale `MaxFileBytes` remarks | Resolved by Task 15 of the previous plan |
| Task 13 | `PublicationDuration.ForRead` untested | Resolved: `ReadModelTests` covers the read duration |
| Task 14 | Measured read-back rate branch untested | Final review: practically dead |
| Task 14 | Long summary lines | Style |
| Task 14 | "Starting" while waiting for the answer | Resolved by Task 7 |
| Task 14 | C7 helper margin | Test helper only |
| Task 15 | Stale comment in `ReadModelTests.cs:41-43` | Verified on `edd370b`: gone |
| Task 15 | No test that a payer change replaces a file-dialog message | Not money, resume or Lock |
| Task 16 | `DllImport` against `LibraryImport` | Style |
| Task 16 | Acquire/release race and throwing `Require` tests | Final review: leave; S13's contract is covered by the refused-call test |
| Task 17 | `RunStopTimeout` duplicates `App.xaml.cs`'s `StopTimeout` | Same value; duplication only |
| Task 17 | Lock does not stop `ReadRun` | By design: a read signs nothing |
| Task 17 | One long comment line | Style |
| Task 20 | `HistoryCacheRecords = 0` test, mixed-case blob, yield after the last round, `VmHWM` | Final review: leave |
| Task 21 | 50 MB test does not count records; duplicated setup | Final review: leave; the read-back proves completeness |
| Final | Answer in the same instant as the timeout; plain "Cancelled" after a timeout; public `VaultAutoLock.Idle` | Harmless, not wrong, documented and tested respectively |

Not a deferred minor, recorded for completeness: O3's "Choose a file to publish." under "Published." is by design (the outcome sits above the picker, `Run.razor` remarks).

---

## Self-review

**Coverage of the brief.** D1 structural: Task 1 (two page copies removed), Task 2 (README and XML docs, page size unchanged, `ResumeMemoryStandaloneTests` and S27 Slow re-run with bounds unchanged). D2: Task 6, amendment 2. D3: Task 7, amendment 3. M8: Task 9. M9 + O1: Task 5, S11 amended in place and amendment 1, S12 threshold untouched. O2: Task 3 (report under `docs/superpowers/reports/2026-10-27-history-read/`), Task 4 conditional with S ≥ 0.5 and V ≥ 0.2. O4: Task 8 (loading "Checking…", error "Not available. Press Check to try again.", not funded "Not funded"). Deferred minors: Tasks 10–15 and the tables. D1 review minors: Task 2. VM acceptance: Task 17 with (a)–(d), report text returned to the controller. Measurement and code tasks precede the acceptance.

**Review Focus.** Each line has its test in the owning task: marker after release (1), restart (6), two networks (8), resume quote edges (5), leaving the Accounts screen (8).

**Type consistency.** `NodeAnswer(JsonElement, string?, IDisposable?)`, `NodeAnswer.Parse`, `UnreadResult`, `AskPageOrAbsentAsync` (1, 4); `ResumeProbeResult.LohThreshold` (2); `PublicationDuration.Hashing`, `ForBatchRemaining`, `CostSummary.OfResume` (5, used in 14); `PublicationState.WorkedSeconds`, `RunSnapshot.WorkedBefore`, `WaitedForPerson` (6); `RunModel.AwaitingConfirmationText`, `RunView.ShowsProgress` (7); `AccountRow.CheckFailed`, `AccountsModel.RefreshAllFromLedgerAsync`, `BalanceTextOf`, `CheckingBalance`, `NotFundedBalance`, `UnreadBalance`, `FakeConnectionFactory.BeforeConnect`, `ConnectedNetworks` (8, used in 14); `RunTraceChannel.CompletionLimit` (13); `ReleaseQuietly` (14).

**Test counts.** The expected totals after each task assume the tasks run in order and each new `[Theory]` counts its cases; an executor who finds a different total reports it rather than adjusting the expectation silently.
