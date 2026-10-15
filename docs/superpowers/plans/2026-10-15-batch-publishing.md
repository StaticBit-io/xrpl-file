# Batch Publishing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish a file through XLS-56 `Batch` with two accounts — chunks as inners of writer A, outers signed and paid by payer B — next to the existing ticket path, with a persisted fence, identity-checked settlement, manifest reconciliation on every resume, live progress with time left, and resume without picking the file again.

**Architecture:** A new `BatchStrategy` behind the existing `ISubmissionStrategy` seam, built from small internal parts (`BatchFees`, `BatchGrouping`, `BatchTransactions`, `BatchWindow`, `BatchRegistry`, `BatchRound`, `BatchSweep`). The transport gains a single-transmission `SubmitBlobAsync` and a reconnecting client; the source gains pinned snapshots, the open-ledger fee and the amendment check. `PublicationSession` gets Batch overloads that build the strategy through a factory so the session can be its fence recorder; state version 3 records payer and fence. The application adds "Paid by", a two-account estimate, time left, Resume on the failed-run screen and resume from the recorded source path.

**Tech Stack:** .NET 10, C# latest, `Xrpl` 11.2.0 (pinned in `Directory.Packages.props`), System.Text.Json, xUnit v3 (`dotnet run` runner), MAUI Blazor (desktop app), Docker standalone stand `xrpl-file-ci` (xrpld 3.3.0, `BatchV1_1` enabled), testnet for acceptance.

**Spec:** `docs/superpowers/specs/2026-10-15-batch-publishing-design.md` (decisions B1–B30). Background: `docs/superpowers/spikes/2026-10-13-batch-publishing.md`, `docs/superpowers/specs/2026-10-12-leftover-tickets-design.md` (I6–I8, I16), `docs/superpowers/specs/2026-09-15-publishing-design.md`. Where this plan departs from the spec, the departure is listed under **Spec questions** at the end with its ruling, and the plan follows the ruling.

## Global Constraints

- Target `net10.0`, `LangVersion latest`, `Nullable enable`, `TreatWarningsAsErrors true`, `GenerateDocumentationFile true` (`Directory.Build.props`): every `<see cref>` and `<paramref>` must resolve, or the build fails.
- No new NuGet packages. `Xrpl` stays at `11.2.0` unless Task 1 Step F2 fires; that bump is a separate commit that runs the whole suite.
- Tests are xUnit v3. `dotnet test` does not work in this repository. Run tests with `dotnet run --project tests/<Project> -- -class <FullName>`, `-method <FullName>`, `-trait- "Category=Integration"` (unit only) or `-trait "Category=Integration"` (stand only). Add `-showLiveOutput` when a step says to read test output.
- Integration tests carry `[Trait("Category", "Integration")]` and `[Collection("Standalone")]` and read the node from `XRPL_FILE_NODE_URL`, else `ws://localhost:6006`.
- The stand is the compose project `xrpl-file-ci`: `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d`. Before starting it run `docker ps --format '{{.Names}} {{.Label "com.docker.compose.project"}}'` (PowerShell; the same single-quoted form works in Git Bash). If `rippled-service` is up under another project, stop and ask; never start, stop or restart another compose project.
- Code style: explicit types (no `var`); `async`/`await` with `CancellationToken` passed through; `ConfigureAwait(false)` in library code; English comments only where the surrounding code would comment; Core builds transactions as `Dictionary<string, object>`.
- Exact values from the spec, verbatim: `InitialWindow` 16, `MaxAhead` 256, `DeadlineLedgers` 20, `SettleLedgers` 2, `ChainCheck` 2 s, `ProgressSweep` 5 s, `SnapshotMaxAge` 10 s, `HistoryLagLimit` 30 s, `FeeWait` 1 min, `MaxAttemptsPerMemo` 3; up to 4 transmissions of one blob, 400 ms × (retry + 1) between them; the window halves after 3 busy answers in a row and grows by 1 after 10 clean sends; batches hold 2–8 inners and a trailing 8 + 1 becomes 7 + 2; fee unit = max(base, ceil(`open_ledger_fee` × 1.5)); ceiling = 2 × the approved unit; a batch pays (inners + 3) × unit, a solo one unit; the estimate refuses `open_ledger_fee` above 200 drops; `tfIndependent` = `0x00080000`, `tfInnerBatchTxn` = `0x40000000`; Amendments singleton index `7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4`; amendment cache 5 min; Batch prior 70 memos/s; rate window 30 s. The 400 ms step is `BatchStrategyOptions.TransportRetryStep`; only tests shorten it.
- State JSON names, verbatim: `Strategy` (`"sequence"`, `"tickets"`, `"batch"`), `SourcePath`, `ManifestHash`, `Payer`, `PayerKeyReference`, `Fence`; version 2 for sequence/ticket runs, 3 for Batch runs.
- UI and error copy, verbatim from spec 6.1–6.5 (quoted in the tasks that own them).
- Commits: conventional, English, subject and body; no attribution lines of any kind (no `Co-Authored-By`, no "Generated with").
- `XrplFile.App.Core` sees only Core's public API (Core's `InternalsVisibleTo` names its two test projects only); App tasks use public members.
- Save every new or edited file as UTF-8: the plan's literals carry "—", "…" and "·".

## Review Focus

1. **The validated ledger stops advancing during a round** (a stuck public node): the run fails with `NodeUnreachable` within `NodeOutageLimit` instead of hanging on settlement forever — test in Task 11.
2. **The window is closed during a resume's fence wait**: the state file is left byte-for-byte as it was, nothing was read for reconciliation or sent, and a later resume finishes the file — test in Task 16.
3. **Paid by offers the writer's own account, or misses a payer whose network differs only in case**: the list never contains the selected key or its address, and networks match in canonical form — test in Task 18.
4. **The run screen when nothing lands for 30 s, or when the fence is already behind the ledger**: it shows "estimating…" rather than an infinite or negative time, and the fence line never shows a negative wait — test in Task 21.
5. **A one-chunk file on the Batch path**: the chunk pass and the manifest pass are both solo transactions of the writer, the payer pays nothing, and the estimate says so — tests in Tasks 8, 12 and 14.
6. **A round larger than `MaxAhead` when the node stops**: the sender is waiting for allowance when the chain check dies; the run still fails with `NodeUnreachable` — test in Task 11.
7. **An answer lost on a dropped connection**: the same blob is resent before settlement resolves it, and nothing lands twice — test in Task 11.
8. **History's Forget survives a restart**: start-up recovery never brings back a link the person removed — test in Task 22.

(6–8 were added after the plan review.)

---

## File Structure

**Core (`src/XrplFile.Core`)**

| File | Responsibility | Task |
|---|---|---|
| `BatchTransactions.cs` (new, internal) | inner/outer/solo dictionaries; writer-then-payer signing | 1 |
| `LedgerSnapshot.cs`, `OpenLedgerFee.cs`, `Amendments.cs` (new) | snapshot of two sequences at one ledger; `fee` method answer; amendment ids | 2 |
| `LedgerTransaction.cs` | + `TransactionType`, `Sequence`, `TicketSequence`, `TicketCount`, `Fee`, `ParentBatchID` | 2 |
| `ILedgerSource.cs`, `XrplClientLedgerSource.cs` | + `SnapshotAsync`, `OpenLedgerFeeAsync`, `IsAmendmentEnabledAsync`; record parsing | 2 |
| `ILedgerSink.cs`, `XrplClientLedgerSink.cs` | + `SubmitBlobAsync` | 3 |
| `ClientKeeper.cs` (new) | the one owner of reconnection; builds and retires clients | 3 |
| `ISubmissionStrategy.cs` | + `SubmittedMemo.LedgerIndex` | 8 |
| `XrplFileException.cs` | + 4 errors, `AccountRole`, `Role` | 4 |
| `BatchFees.cs`, `BatchGrouping.cs`, `BatchStrategyOptions.cs`, `BatchBudget.cs`, `IBatchFenceRecorder.cs` (new) | pricing, grouping and assignment, options, budget, fence seam | 6 |
| `BatchWindow.cs`, `BatchFence.cs`, `BatchCandidate.cs`, `BatchRegistry.cs`, `BatchTrace.cs` (new, internal) | send window; fence tracker; registration under the round lock | 7 |
| `BatchContext.cs`, `BatchChain.cs`, `BatchRound.cs`, `BatchSweep.cs`, `BatchStrategy.cs` (new) | rounds, chain check, settlement, solo, sweep, strategy | 8–11 |
| `BatchSpending.cs`, `BatchProgress.cs` (new, internal) | runtime budget; monotonic progress | 10 |
| `BatchPublicationEstimate.cs` (new), `PublicationEstimate.cs` | two-account estimate | 12 |
| `PublicationState.cs` | version 3, new optional fields, validation | 13 |
| `PublicationSession.cs`, `PublicationReport.cs` | Batch overloads, fence recorder, report, B27, resume order | 14–16 |
| `ManifestPlan.cs` (new, internal), `XrplFileWriter.cs` | one manifest builder for writer and session | 15 |
| `TicketStrategy.cs` | progress per sweep poll | 17 |

**Keys (`src/XrplFile.Keys`)**: `VaultSigner.cs` changes only if Task 1 Step F1 or F3 fires.

**Test support (`tests/XrplFile.TestSupport`)**: `FakeBatchLedgerSink.cs` (new), `FakeLedgerSource.cs` (snapshots, fee, amendments, history hiding, ledger-read event) — Task 5.

**App core (`app/XrplFile.App.Core`)**: `XrplConnectionFactory.cs` (3), `PublishErrorMessages.cs` (4), `IPayerSettingsStore.cs`, `FilePayerSettingsStore.cs`, `BatchAvailability.cs`, `PublishRequest.cs`, `PublishModel.cs`, `PublishPolicy.cs` (18), `EstimateModel.cs` (19), `PublicationRun.cs`, `PublicationRecord.cs` (20), `RunModel.cs` (21), `UnfinishedModel.cs`, `PublicationHistory.cs` (22).

**App (`app/XrplFile.App`)**: `MauiProgram.cs` (18, 20, 22), `Components/Publish.razor` (18), `Estimate.razor` (19), `Run.razor` (21), `Unfinished.razor`, `History.razor` (22).

**Tests**: `tests/XrplFile.Core.Tests/*` and `tests/XrplFile.App.Core.Tests/*`, file per task as listed.

---

### Task 1: Signing proof on the stand

Spec 4.2 steps 1–3 and the signing-time measurement. Nothing after this task is built on a guess about signing.

**What 11.2.0 actually does** (read from `git show v11.2.0:Xrpl/Wallet/XrplWallet.cs` in `E:\GIT\XRPL\XrplCSharp`; the 11.2.0 XML docs omit it): `XrplWallet.Sign(Dictionary)` routes a `Batch` to `SignAsBatchPart` when the wallet's account is among the **inner** transactions' `Account`s and is not the outer's `Account` — not when `BatchSigners` names it. `SignAsBatchPart` is public, appends a `BatchSigner { Account, SigningPubKey, TxnSignature }` to whatever `BatchSigners` the outer already carries, and refuses a second signature for an account that already signed (`VerifyBatchSubmitter` throws `UnauthorizedAccessException`; a duplicate `BatchSigner` would reach the node as `temBAD_SIGNER`). It leaves the outer's `SigningPubKey` as given and writes no `TxnSignature`. So the outer handed to the writer carries **no** `BatchSigners`; the writer's signature creates the entry, and the payer signs the decoded result. The final shape is still the spec's.

**Files:**
- Create: `src/XrplFile.Core/BatchTransactions.cs`
- Create: `tests/XrplFile.Core.Tests/BatchTransactionsTests.cs`
- Create: `tests/XrplFile.Core.Tests/BatchSigningStandaloneTests.cs`

**Interfaces:**
- Consumes: `ITransactionSigner`, `SignedTransaction`, `MemoSubmission`, `VaultSigner.CreateAsync(IKeyVault, string, CancellationToken, VaultAutoLock?)`, `StandaloneFixture.FundedAccountAsync`, `StandaloneFixture.CurrentLedgerAsync`, `StandaloneFixture.CloseLedgersUntilAsync`.
- Produces (internal, visible to `XrplFile.Core.Tests` and `XrplFile.TestSupport`):
  - `BatchTransactions.InnerBatchFlag` (`uint` 0x40000000), `BatchTransactions.IndependentFlag` (`uint` 0x00080000)
  - `Dictionary<string, object> BatchTransactions.Inner(string writer, uint sequence, MemoSubmission memo)`
  - `Dictionary<string, object> BatchTransactions.Outer(string payer, string payerPublicKey, uint sequence, long feeDrops, uint lastLedger, IReadOnlyList<Dictionary<string, object>> inners)`
  - `Dictionary<string, object> BatchTransactions.Solo(string writer, string writerPublicKey, uint sequence, long feeDrops, uint lastLedger, MemoSubmission memo)`
  - `Task<SignedTransaction> BatchTransactions.SignAsync(Dictionary<string, object> outer, ITransactionSigner writer, ITransactionSigner payer, CancellationToken cancellationToken)`
  - `object BatchTransactions.Plain(JsonNode? node)`

- [ ] **Step 1: Write the failing unit tests**

`tests/XrplFile.Core.Tests/BatchTransactionsTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class BatchTransactionsTests
{
    private static readonly XrplWallet WriterWallet = XrplWallet.Generate();
    private static readonly XrplWallet PayerWallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Writer = new WalletSigner(WriterWallet);
    private static readonly ITransactionSigner Payer = new WalletSigner(PayerWallet);

    private static MemoSubmission Memo(int index) =>
        new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex(VectorStream.Bytes("batch", 1000)), null);

    private static Dictionary<string, object> Outer(int inners) =>
        BatchTransactions.Outer(
            Payer.Account, Payer.PublicKey, sequence: 7, feeDrops: (inners + 3) * 15L, lastLedger: 1020,
            [.. Enumerable.Range(0, inners).Select(i => BatchTransactions.Inner(Writer.Account, 5u + (uint)i, Memo(i)))]);

    [Fact]
    public void An_inner_is_an_AccountSet_of_the_writer_with_no_fee_no_key_and_the_inner_flag()
    {
        Dictionary<string, object> inner = BatchTransactions.Inner(Writer.Account, 5, Memo(0));

        Assert.Equal("AccountSet", inner["TransactionType"]);
        Assert.Equal(Writer.Account, inner["Account"]);
        Assert.Equal(5u, inner["Sequence"]);
        Assert.Equal("0", inner["Fee"]);
        Assert.Equal(string.Empty, inner["SigningPubKey"]);
        Assert.Equal(0x40000000u, inner["Flags"]);
        Assert.False(inner.ContainsKey("LastLedgerSequence"));
    }

    [Fact]
    public void An_outer_is_the_payers_independent_batch_with_its_deadline_and_no_batch_signers_yet()
    {
        Dictionary<string, object> outer = Outer(8);

        Assert.Equal("Batch", outer["TransactionType"]);
        Assert.Equal(Payer.Account, outer["Account"]);
        Assert.Equal(7u, outer["Sequence"]);
        Assert.Equal("165", outer["Fee"]);
        Assert.Equal(0x00080000u, outer["Flags"]);
        Assert.Equal(1020u, outer["LastLedgerSequence"]);
        Assert.Equal(Payer.PublicKey, outer["SigningPubKey"]);
        Assert.Equal(8, ((List<object>)outer["RawTransactions"]).Count);
        Assert.False(outer.ContainsKey("BatchSigners"));
    }

    [Fact]
    public async Task The_writer_signs_first_and_the_payer_signs_what_the_writer_returned()
    {
        SignedTransaction signed = await BatchTransactions.SignAsync(Outer(2), Writer, Payer, TestContext.Current.CancellationToken);

        JsonObject decoded = XrplBinaryCodec.Decode(signed.TxBlob).AsObject();
        JsonObject signer = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(decoded["BatchSigners"]))!["BatchSigner"]);

        Assert.Equal(Writer.Account, (string?)signer["Account"]);
        Assert.Equal(Writer.PublicKey, (string?)signer["SigningPubKey"], ignoreCase: true);
        Assert.False(string.IsNullOrEmpty((string?)signer["TxnSignature"]));
        Assert.Equal(Payer.PublicKey, (string?)decoded["SigningPubKey"], ignoreCase: true);
        Assert.False(string.IsNullOrEmpty((string?)decoded["TxnSignature"]));
        Assert.Equal(64, signed.Hash.Length);
    }

    [Fact]
    public void A_solo_memo_is_a_plain_AccountSet_of_the_writer_with_its_own_deadline()
    {
        Dictionary<string, object> solo = BatchTransactions.Solo(Writer.Account, Writer.PublicKey, 9, 15, 1020, Memo(0));

        Assert.Equal("AccountSet", solo["TransactionType"]);
        Assert.Equal(9u, solo["Sequence"]);
        Assert.Equal("15", solo["Fee"]);
        Assert.Equal(1020u, solo["LastLedgerSequence"]);
        Assert.Equal(Writer.PublicKey, solo["SigningPubKey"]);
        Assert.False(solo.ContainsKey("Flags"));
    }

    [Fact]
    public void Plain_turns_decoded_json_into_dictionaries_lists_strings_and_numbers()
    {
        JsonNode node = JsonNode.Parse("""{"A":"x","B":7,"C":[{"D":4294967295}],"E":5000000000}""")!;

        Dictionary<string, object> plain = Assert.IsType<Dictionary<string, object>>(BatchTransactions.Plain(node));

        Assert.Equal("x", plain["A"]);
        Assert.Equal(7u, plain["B"]);
        Assert.Equal(4294967295u, ((Dictionary<string, object>)((List<object>)plain["C"])[0])["D"]);
        Assert.Equal(5000000000L, plain["E"]);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchTransactionsTests`
Expected: build FAILS, `The name 'BatchTransactions' does not exist in the current context`.

- [ ] **Step 3: Write `BatchTransactions`**

`src/XrplFile.Core/BatchTransactions.cs`:

```csharp
using System.Globalization;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;

namespace XrplFile;

/// <summary>
/// The transactions of the Batch path (spec 4.2): an inner <c>AccountSet</c> of the writer per memo,
/// the payer's outer <c>Batch</c> that carries them, and the writer's plain <c>AccountSet</c> for a
/// round's one memo.
/// </summary>
internal static class BatchTransactions
{
    /// <summary>rippled's <c>tfInnerBatchTxn</c>: every inner carries it.</summary>
    public const uint InnerBatchFlag = 0x40000000;

    /// <summary>rippled's <c>tfIndependent</c> (spec B2).</summary>
    public const uint IndependentFlag = 0x00080000;

    public static Dictionary<string, object> Inner(string writer, uint sequence, MemoSubmission memo) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = writer,
        ["Sequence"] = sequence,
        ["Fee"] = "0",
        ["SigningPubKey"] = string.Empty,
        ["Flags"] = InnerBatchFlag,
        ["Memos"] = Memos(memo),
    };

    public static Dictionary<string, object> Outer(
        string payer, string payerPublicKey, uint sequence, long feeDrops, uint lastLedger,
        IReadOnlyList<Dictionary<string, object>> inners) => new()
    {
        ["TransactionType"] = "Batch",
        ["Account"] = payer,
        ["Sequence"] = sequence,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["Flags"] = IndependentFlag,
        ["LastLedgerSequence"] = lastLedger,
        ["SigningPubKey"] = payerPublicKey,
        ["RawTransactions"] = inners.Select(inner => (object)new Dictionary<string, object> { ["RawTransaction"] = inner }).ToList(),
    };

    public static Dictionary<string, object> Solo(
        string writer, string writerPublicKey, uint sequence, long feeDrops, uint lastLedger, MemoSubmission memo) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = writer,
        ["Sequence"] = sequence,
        ["Fee"] = feeDrops.ToString(CultureInfo.InvariantCulture),
        ["LastLedgerSequence"] = lastLedger,
        ["SigningPubKey"] = writerPublicKey,
        ["Memos"] = Memos(memo),
    };

    /// <summary>
    /// The writer signs first, then the payer (spec 4.2). The SDK routes a <c>Batch</c> to its
    /// batch-part signature when the signing account owns an inner and not the outer, and that
    /// signature adds the writer's <c>BatchSigner</c>. The payer then signs exactly what the writer
    /// returned, decoded from its blob, so the inners it commits to are the inners the writer's
    /// signature covers.
    /// </summary>
    public static async Task<SignedTransaction> SignAsync(
        Dictionary<string, object> outer, ITransactionSigner writer, ITransactionSigner payer, CancellationToken cancellationToken)
    {
        SignedTransaction part = await writer.SignAsync(outer, cancellationToken).ConfigureAwait(false);
        JsonObject decoded = XrplBinaryCodec.Decode(part.TxBlob).AsObject();

        if (!CarriesBatchSignature(decoded, writer.Account))
        {
            throw new InvalidOperationException(
                $"the writer's signer returned a Batch without a signed BatchSigner for {writer.Account}");
        }

        return await payer.SignAsync((Dictionary<string, object>)Plain(decoded), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A decoded blob as the plain objects the SDK's signer serializes: dictionaries, lists,
    /// strings, and numbers as <see cref="uint"/> where they fit and <see cref="long"/> otherwise.
    /// </summary>
    public static object Plain(JsonNode? node) => node switch
    {
        JsonObject json => json.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(Plain).ToList(),
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonValue value => Number(value.ToJsonString()),
        null => throw new FormatException("a decoded transaction carried a null field"),
        _ => throw new FormatException($"a decoded transaction carried {node.GetType().Name}"),
    };

    private static object Number(string text)
    {
        long number = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        return number is >= 0 and <= uint.MaxValue ? (uint)number : number;
    }

    private static bool CarriesBatchSignature(JsonObject decoded, string writer) =>
        decoded["BatchSigners"] is JsonArray signers
        && signers.Any(entry => entry?["BatchSigner"] is JsonObject signer
            && string.Equals((string?)signer["Account"], writer, StringComparison.Ordinal)
            && !string.IsNullOrEmpty((string?)signer["SigningPubKey"])
            && !string.IsNullOrEmpty((string?)signer["TxnSignature"]));

    private static List<object> Memos(MemoSubmission memo)
    {
        Dictionary<string, object> fields = new() { ["MemoType"] = memo.MemoTypeHex, ["MemoData"] = memo.MemoDataHex };
        if (memo.MemoFormatHex is not null) fields["MemoFormat"] = memo.MemoFormatHex;

        return [new Dictionary<string, object> { ["Memo"] = fields }];
    }
}
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchTransactionsTests`
Expected: PASS (5 tests). If `The_writer_signs_first…` fails with the `InvalidOperationException` above, apply Step F1 now and run again.

- [ ] **Step 5: Write the stand tests (spec 4.2 steps 1–3 and the timing)**

`tests/XrplFile.Core.Tests/BatchSigningStandaloneTests.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Spec 4.2, proven before anything else is built: two vault signers on one vault sign a Batch the
/// stand applies, and the inners land as AccountSets of the writer. Also measures what signing a
/// megabyte's batches costs (spec 4.2's last paragraph).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class BatchSigningStandaloneTests : IDisposable
{
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");

    /// <summary>One fifth of the 4.1 s the spike measured for 1 MB on this stand, over its 125 batches.</summary>
    private static readonly TimeSpan SigningBudgetPerBatch = TimeSpan.FromMilliseconds(6.5);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-batch-signing-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    private static MemoSubmission Memo(int index) =>
        new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex(VectorStream.Bytes($"signing-{index}", 1000)), null);

    private async Task<(KeyVault Vault, VaultSigner Writer, VaultSigner Payer)> TwoSignersAsync(
        IXrplClient client, XrplClientLedgerSink sink, CancellationToken token)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
        XrplWallet payer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

        KeyVault vault = new(Path.Combine(_directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);
        await vault.CreateAsync(Password, token);

        string writerReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "writer", "1", writer.ClassicAddress, writer.Seed), token);
        string payerReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "payer", "1", payer.ClassicAddress, payer.Seed), token);

        return (vault,
            await VaultSigner.CreateAsync(vault, writerReference, token),
            await VaultSigner.CreateAsync(vault, payerReference, token));
    }

    [Fact]
    public async Task The_writers_vault_signature_over_an_outer_carries_its_batch_signer()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, new XrplClientLedgerSink(client), token);
            await using (vault)
            {
                Dictionary<string, object> outer = BatchTransactions.Outer(
                    payer.Account, payer.PublicKey, sequence: 7, feeDrops: 75, lastLedger: 1000,
                    [BatchTransactions.Inner(writer.Account, 5, Memo(0)), BatchTransactions.Inner(writer.Account, 6, Memo(1))]);

                SignedTransaction part = await writer.SignAsync(outer, token);

                JsonObject decoded = XrplBinaryCodec.Decode(part.TxBlob).AsObject();
                JsonObject signer = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(decoded["BatchSigners"]))!["BatchSigner"]);

                Assert.Equal(writer.Account, (string?)signer["Account"]);
                Assert.Equal(writer.PublicKey, (string?)signer["SigningPubKey"], ignoreCase: true);
                Assert.False(string.IsNullOrEmpty((string?)signer["TxnSignature"]));
                Assert.Null(decoded["TxnSignature"]);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task Two_vault_signers_on_one_vault_sign_a_batch_the_stand_applies()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, sink, token);
            await using (vault)
            {
                uint writerSequence = await sink.SequenceAsync(writer.Account, validated: false, token);
                uint payerSequence = await sink.SequenceAsync(payer.Account, validated: false, token);
                long ledger = await StandaloneFixture.CurrentLedgerAsync(client, token);

                List<MemoSubmission> memos = [.. Enumerable.Range(0, 8).Select(Memo)];
                Dictionary<string, object> outer = BatchTransactions.Outer(
                    payer.Account, payer.PublicKey, payerSequence, feeDrops: 11 * 15, lastLedger: (uint)(ledger + 20),
                    [.. memos.Select((memo, i) => BatchTransactions.Inner(writer.Account, writerSequence + (uint)i, memo))]);

                SignedTransaction signed = await BatchTransactions.SignAsync(outer, writer, payer, token);

                XrplResponse<object> response = await client.GRequest<object, SubmitRequest>(new SubmitRequest { TxBlob = signed.TxBlob }, token);
                Assert.Equal("tesSUCCESS", response.Raw.ToJsonElement().GetProperty("engine_result").GetString());

                while (await sink.SequenceAsync(writer.Account, validated: true, token) < writerSequence + 8)
                {
                    await StandaloneFixture.CloseLedgersUntilAsync(client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 1, token);
                }

                List<JsonElement> records = await AccountTransactionsAsync(client, writer.Account, token);

                foreach (MemoSubmission memo in memos)
                {
                    JsonElement record = Assert.Single(records, candidate => MemoTypeOf(candidate) == memo.MemoTypeHex);
                    JsonElement tx = TxOf(record);
                    JsonElement meta = record.GetProperty("meta");

                    Assert.Equal("AccountSet", tx.GetProperty("TransactionType").GetString());
                    Assert.Equal(writer.Account, tx.GetProperty("Account").GetString());
                    Assert.Equal("0", tx.GetProperty("Fee").GetString());
                    Assert.Equal("tesSUCCESS", meta.GetProperty("TransactionResult").GetString());
                    Assert.Equal(memo.MemoDataHex, MemoDataOf(record), ignoreCase: true);
                    Assert.Equal(signed.Hash, ParentBatchOf(record), ignoreCase: true);
                }
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task Signing_a_megabytes_batches_through_two_vault_signers_is_measured()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            (KeyVault vault, VaultSigner writer, VaultSigner payer) = await TwoSignersAsync(client, new XrplClientLedgerSink(client), token);
            await using (vault)
            {
                const int Batches = 125;
                Stopwatch watch = Stopwatch.StartNew();

                for (int batch = 0; batch < Batches; batch++)
                {
                    Dictionary<string, object> outer = BatchTransactions.Outer(
                        payer.Account, payer.PublicKey, (uint)(100 + batch), 11 * 15, 5000,
                        [.. Enumerable.Range(0, 8).Select(i => BatchTransactions.Inner(writer.Account, (uint)(1000 + batch * 8 + i), Memo(batch * 8 + i)))]);

                    await BatchTransactions.SignAsync(outer, writer, payer, token);
                }

                watch.Stop();
                TimeSpan perBatch = watch.Elapsed / Batches;

                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"signing: {Batches} batches in {watch.Elapsed.TotalMilliseconds:F0} ms, {perBatch.TotalMilliseconds:F2} ms per batch; "
                    + $"budget {SigningBudgetPerBatch.TotalMilliseconds} ms per batch");
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    private static async Task<List<JsonElement>> AccountTransactionsAsync(IXrplClient client, string account, CancellationToken token)
    {
        XrplResponse<object> page = await client.GRequest<object, AccountTransactionsRequest>(
            new AccountTransactionsRequest(account) { Limit = 100 }, token);

        return [.. page.Raw.ToJsonElement().GetProperty("transactions").EnumerateArray().Select(record => record.Clone())];
    }

    private static JsonElement TxOf(JsonElement record) =>
        record.TryGetProperty("tx_json", out JsonElement tx) ? tx : record.GetProperty("tx");

    private static string? MemoTypeOf(JsonElement record) =>
        TxOf(record).TryGetProperty("Memos", out JsonElement memos)
            ? memos[0].GetProperty("Memo").GetProperty("MemoType").GetString()?.ToUpperInvariant()
            : null;

    private static string? MemoDataOf(JsonElement record) =>
        TxOf(record).GetProperty("Memos")[0].GetProperty("Memo").GetProperty("MemoData").GetString();

    private static string? ParentBatchOf(JsonElement record) =>
        record.GetProperty("meta").TryGetProperty("ParentBatchID", out JsonElement parent) ? parent.GetString()
        : TxOf(record).TryGetProperty("ParentBatchID", out JsonElement inTx) ? inTx.GetString()
        : null;
}
```

- [ ] **Step 6: Start the stand and run the stand tests**

```bash
docker ps --format '{{.Names}} {{.Label "com.docker.compose.project"}}'
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchSigningStandaloneTests -showLiveOutput
```

Expected: PASS (3 tests), and one output line `signing: 125 batches in … ms, … ms per batch; budget 6.5 ms per batch`. Write the measured figure down; it goes into the commit body.

Decide the fallbacks from the result, in this order:
- `The_writers_vault_signature…` fails (no `BatchSigners`, or the writer's key missing) → Step F1, then rerun Step 6.
- `Two_vault_signers…` fails with an `engine_result` of `temBAD_SIGNATURE`, `tefBAD_SIGNATURE`, `temBAD_SIGNER`, or a `RippledException` `invalidTransaction` → Step F2, then rerun Step 6.
- per batch > 6.5 ms → Step F3.
- Otherwise skip F1–F3.

- [ ] **Step F1 (only if Step 6 says so): `SignBatchPartAsync`**

In `src/XrplFile.Core/ITransactionSigner.cs`, add to `ITransactionSigner`:

```csharp
    /// <summary>
    /// Signs <paramref name="outer"/> as one of its BatchSigners (spec 4.2): the returned blob is
    /// the outer with this account's <c>BatchSigner</c> added and no <c>TxnSignature</c>.
    /// </summary>
    Task<SignedTransaction> SignBatchPartAsync(IDictionary<string, object> outer, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not sign batch parts");
```

(A default member, so every other signer — the test doubles, and Task 8's `GatedSigner` — keeps compiling.) Add to `WalletSigner`:

```csharp
    public Task<SignedTransaction> SignBatchPartAsync(IDictionary<string, object> outer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SignatureResult signed = _wallet.SignAsBatchPart(new Dictionary<string, object>(outer), false, null);
        return Task.FromResult(new SignedTransaction(signed.TxBlob, signed.Hash.ToUpperInvariant()));
    }
```

In `src/XrplFile.Keys/VaultSigner.cs` add:

```csharp
    /// <inheritdoc/>
    public async Task<SignedTransaction> SignBatchPartAsync(
        IDictionary<string, object> outer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outer);

        VaultEntry entry = await _vault.RevealAsync(_reference, Reason, cancellationToken).ConfigureAwait(false);
        XrplWallet wallet = XrplWallet.FromSeed(entry.Secret, masterAddress: null, algorithm: entry.Info.Algorithm);
        SignatureResult signed = wallet.SignAsBatchPart(new Dictionary<string, object>(outer), false, null);

        _autoLock?.Touch();

        return new SignedTransaction(signed.TxBlob, signed.Hash.ToUpperInvariant());
    }
```

Every other signer keeps the default: only a writer's signer is asked for a batch part, and no test double signs as a writer of a batch. In `BatchTransactions.SignAsync` replace `writer.SignAsync(outer, cancellationToken)` with `writer.SignBatchPartAsync(outer, cancellationToken)`, and in the first stand test replace `writer.SignAsync(outer, token)` with `writer.SignBatchPartAsync(outer, token)`. Build, rerun Step 4 and Step 6.

- [ ] **Step F2 (only if Step 6 says so): move `Xrpl` to the current release in its own commit**

```bash
dotnet package search Xrpl --exact-match --format json
```

Take the highest stable version it lists (the local cache held `11.14.0` when this plan was written) and set it in `Directory.Packages.props`: `<PackageVersion Include="Xrpl" Version="<that version>" />`. Then run every suite:

```bash
dotnet build XrplFile.sln
dotnet run --project tests/XrplFile.Core.Tests
dotnet run --project tests/XrplFile.Keys.Tests
dotnet run --project tests/XrplFile.App.Core.Tests
```

Expected: everything passes (the stand is up, so integration tests run too). Commit alone:

```bash
git add Directory.Packages.props
git commit -m "build(deps): move Xrpl to <version> for Batch signatures the node accepts" -m "Xrpl 11.2.0 signatures were refused by the stand: <the engine result Step 6 printed>."
```

Rerun Step 6.

- [ ] **Step F3 (only if a measurement says so): a wallet cache for one session**

In `src/XrplFile.Keys/VaultSigner.cs`:

1. Add fields after `_autoLock`:

```csharp
    private readonly bool _cacheWallet;
    private readonly Lock _cacheGate = new();
    private XrplWallet? _cached;
```

2. Add `bool cacheWallet = false` as the last parameter of the constructor (assign `_cacheWallet = cacheWallet;`) and of `CreateAsync` (pass it through to the constructor).

3. Replace the reveal-and-derive lines in `SignAsync` (and in `SignBatchPartAsync` if F1 fired) with `XrplWallet wallet = await WalletAsync(cancellationToken).ConfigureAwait(false);` and add:

```csharp
    /// <summary>
    /// Drops the wallet a caching signer holds. The session that built the signer calls this when it
    /// ends; a locked vault drops it on the next signature by itself.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheGate) _cached = null;
    }

    private async Task<XrplWallet> WalletAsync(CancellationToken cancellationToken)
    {
        if (_cacheWallet)
        {
            lock (_cacheGate)
            {
                // A lock means the person put the keys away: nothing derived from them may outlive it.
                if (!_vault.IsUnlocked) _cached = null;
                else if (_cached is { } cached) return cached;
            }
        }

        VaultEntry entry = await _vault.RevealAsync(_reference, Reason, cancellationToken).ConfigureAwait(false);
        XrplWallet wallet = XrplWallet.FromSeed(entry.Secret, masterAddress: null, algorithm: entry.Info.Algorithm);

        if (_cacheWallet)
        {
            lock (_cacheGate) _cached = wallet;
        }

        return wallet;
    }
```

4. Add to `tests/XrplFile.Keys.Tests/VaultSignerTests.cs` a test `A_caching_signer_stops_signing_once_the_vault_locks` that builds a signer with `cacheWallet: true`, signs once, calls `vault.LockAsync()`, and asserts the next `SignAsync` throws `VaultException` with `VaultError.Locked` (follow that file's existing vault setup), and run `dotnet run --project tests/XrplFile.Keys.Tests -- -class XrplFile.Keys.Tests.VaultSignerTests`. Expected: PASS. Task 20 passes `cacheWallet: true` and calls `ClearCache()` when it builds the Batch signers, if this step fired.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/BatchTransactions.cs tests/XrplFile.Core.Tests/BatchTransactionsTests.cs tests/XrplFile.Core.Tests/BatchSigningStandaloneTests.cs
git commit -m "test(core): prove two vault signers sign a Batch the stand applies" -m "Signing 125 batches of 8 through two VaultSigners took <N> ms (<M> ms per batch) against a budget of 6.5 ms per batch."
```

(Add the files F1/F3 touched to the same commit if they fired.)

---
### Task 2: Ledger data — transaction fields, pinned snapshots, open-ledger fee, the amendment

Spec B25 (data half), B6, B14, 4.3, 7.2.

**Files:**
- Create: `src/XrplFile.Core/LedgerSnapshot.cs`, `src/XrplFile.Core/OpenLedgerFee.cs`, `src/XrplFile.Core/Amendments.cs`
- Modify: `src/XrplFile.Core/LedgerTransaction.cs`, `src/XrplFile.Core/ILedgerSource.cs`, `src/XrplFile.Core/XrplClientLedgerSource.cs` (`ParseRecord`, `SendAsync`, three new methods)
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs` (append), `tests/XrplFile.Core.Tests/AmendmentsTests.cs`, `tests/XrplFile.Core.Tests/LedgerDataStandaloneTests.cs`

**Interfaces:**
- Consumes: `BatchTransactions` (Task 1) in the stand test.
- Produces:
  - `public sealed record LedgerSnapshot(long LedgerIndex, IReadOnlyDictionary<string, uint> Sequences)` with `uint SequenceOf(string account)` (0 for an account the snapshot does not carry)
  - `public sealed record OpenLedgerFee(long OpenLedgerFeeDrops, long BaseFeeDrops)`
  - `public static class Amendments` with `const string Batch = "BatchV1_1"`, `const string SingletonIndex`, `static string IdOf(string name)`
  - `LedgerTransaction` init properties: `string? TransactionType`, `uint Sequence`, `uint? TicketSequence`, `uint? TicketCount`, `long? Fee`, `string? ParentBatchID`
  - `ILedgerSource.SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken)` → `Task<LedgerSnapshot>`; `ILedgerSource.OpenLedgerFeeAsync(CancellationToken)` → `Task<OpenLedgerFee>`; `ILedgerSource.IsAmendmentEnabledAsync(string name, CancellationToken)` → `Task<bool>`. Default implementations throw `NotSupportedException`, so the test decorators that wrap a source keep compiling.

- [ ] **Step 1: Write the failing unit tests**

`tests/XrplFile.Core.Tests/AmendmentsTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class AmendmentsTests
{
    [Fact]
    public void An_amendment_id_is_the_sha512_half_of_its_name()
    {
        // The id .ci-config/rippled.cfg lists for BatchV1_1.
        Assert.Equal("9F287AED3CDB50A7BD1ACEC24296A30C9B5230CCD136219317AC790E3B884377", Amendments.IdOf(Amendments.Batch));
    }

    [Fact]
    public void The_amendments_object_is_read_by_its_fixed_index()
    {
        Assert.Equal("7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4", Amendments.SingletonIndex);
    }
}
```

Append to `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`, inside the class (the file's own `ScriptedNode` is used; add `using Xrpl.Models.Methods;` at the top if absent):

```csharp
    [Fact]
    public void A_batch_inner_is_read_with_its_type_sequence_fee_and_parent_batch()
    {
        JsonElement record = JsonDocument.Parse("""
            {"hash":"inner1","ledger_index":10,"validated":true,
             "meta":{"TransactionResult":"tesSUCCESS","ParentBatchID":"abcdef"},
             "tx_json":{"Account":"rWriter","TransactionType":"AccountSet","Sequence":41,"Fee":"0"}}
            """).RootElement;

        LedgerTransaction tx = XrplClientLedgerSource.ParseRecord(record);

        Assert.Equal("AccountSet", tx.TransactionType);
        Assert.Equal(41u, tx.Sequence);
        Assert.Equal(0L, tx.Fee);
        Assert.Equal("ABCDEF", tx.ParentBatchID);
        Assert.Null(tx.TicketSequence);
    }

    [Fact]
    public void A_ticket_create_and_a_ticketed_transaction_are_read_with_their_ticket_fields()
    {
        LedgerTransaction create = XrplClientLedgerSource.ParseRecord(JsonDocument.Parse("""
            {"hash":"c","ledger_index":3,"validated":true,"meta":{"TransactionResult":"tesSUCCESS"},
             "tx_json":{"Account":"r","TransactionType":"TicketCreate","Sequence":7,"TicketCount":3,"Fee":"12"}}
            """).RootElement);
        LedgerTransaction ticketed = XrplClientLedgerSource.ParseRecord(JsonDocument.Parse("""
            {"hash":"t","ledger_index":4,"validated":true,"meta":{"TransactionResult":"tesSUCCESS"},
             "tx_json":{"Account":"r","TransactionType":"AccountSet","Sequence":0,"TicketSequence":9,"Fee":"12"}}
            """).RootElement);

        Assert.Equal(3u, create.TicketCount);
        Assert.Equal(7u, create.Sequence);
        Assert.Equal(12L, create.Fee);
        Assert.Equal(0u, ticketed.Sequence);
        Assert.Equal(9u, ticketed.TicketSequence);
        Assert.Null(ticketed.ParentBatchID);
    }

    [Fact]
    public async Task A_snapshot_reads_every_account_at_the_one_validated_ledger_it_read_first()
    {
        ScriptedNode node = new();
        node.Answer("""{"ledger_index":700}""");
        node.Answer("""{"account_data":{"Sequence":11}}""");
        node.Answer("""{"account_data":{"Sequence":22}}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        LedgerSnapshot snapshot = await source.SnapshotAsync(["rA", "rB"], TestContext.Current.CancellationToken);

        Assert.Equal(700, snapshot.LedgerIndex);
        Assert.Equal(11u, snapshot.SequenceOf("rA"));
        Assert.Equal(22u, snapshot.SequenceOf("rB"));
        Assert.Equal(["ledger", "account_info", "account_info"], node.Commands);
        Assert.All(node.Requests.OfType<AccountInfoRequest>(), request => Assert.Equal((uint?)700, request.LedgerIndex.Index));
    }

    [Fact]
    public async Task An_unfunded_account_in_a_snapshot_has_sequence_zero()
    {
        ScriptedNode node = new();
        node.Answer("""{"ledger_index":5}""");
        node.Answer("""{"error":"actNotFound"}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        LedgerSnapshot snapshot = await source.SnapshotAsync(["rNobody"], TestContext.Current.CancellationToken);

        Assert.Equal(0u, snapshot.SequenceOf("rNobody"));
    }

    [Fact]
    public async Task A_snapshot_whose_ledger_cannot_be_read_fails_rather_than_reading_validated()
    {
        ScriptedNode node = new();
        node.Answer("""{"ledger_index":5}""");
        node.Answer("""{"error":"lgrNotFound"}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => source.SnapshotAsync(["rA"], TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Equal(2, node.Commands.Count);
    }

    [Fact]
    public async Task The_open_ledger_fee_and_the_base_fee_come_from_the_fee_method()
    {
        ScriptedNode node = new();
        node.Answer("""{"drops":{"base_fee":"10","median_fee":"5000","minimum_fee":"10","open_ledger_fee":"12"}}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        OpenLedgerFee fee = await source.OpenLedgerFeeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new OpenLedgerFee(12, 10), fee);
        Assert.Equal(["fee"], node.Commands);
    }

    [Fact]
    public async Task A_fee_answer_without_drops_is_a_node_failure()
    {
        ScriptedNode node = new();
        node.Answer("""{"current_ledger_size":"3"}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => source.OpenLedgerFeeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Theory]
    [InlineData("BatchV1_1", true)]
    [InlineData("NoSuchAmendmentAnywhere", false)]
    public async Task An_amendment_is_enabled_when_its_id_is_in_the_amendments_object(string name, bool expected)
    {
        ScriptedNode node = new();
        node.Answer("""{"node":{"Amendments":["42426C4D4F1009EE67080A9B7965B44656D7714D104A72F9B4369F97ABF044EE","9f287aed3cdb50a7bd1acec24296a30c9b5230ccd136219317ac790e3b884377"],"LedgerEntryType":"Amendments"}}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        Assert.Equal(expected, await source.IsAmendmentEnabledAsync(name, TestContext.Current.CancellationToken));

        LedgerEntryRequest request = Assert.IsType<LedgerEntryRequest>(Assert.Single(node.Requests));
        Assert.Equal(Amendments.SingletonIndex, request.Index);
    }

    [Fact]
    public async Task No_amendments_object_means_nothing_is_enabled()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"entryNotFound"}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        Assert.False(await source.IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Any_other_failure_to_read_the_amendments_is_a_node_failure()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"noNetwork"}""");
        XrplClientLedgerSource source = new(node.HandleAsync);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => source.IsAmendmentEnabledAsync(Amendments.Batch, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests -class XrplFile.Tests.AmendmentsTests`
Expected: build FAILS (`LedgerSnapshot`, `Amendments`, `TransactionType` … do not exist).

- [ ] **Step 3: Add the types**

`src/XrplFile.Core/LedgerSnapshot.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// Accounts' next sequences, all read at one validated ledger (spec B6). Two separate "validated"
/// reads can straddle a close, so every comparison of two accounts' sequences uses one of these.
/// </summary>
/// <param name="LedgerIndex">The validated ledger every sequence here was read at.</param>
/// <param name="Sequences">Each account's next sequence at that ledger; zero for an account the ledger does not carry.</param>
public sealed record LedgerSnapshot(long LedgerIndex, IReadOnlyDictionary<string, uint> Sequences)
{
    public uint SequenceOf(string account) => Sequences.TryGetValue(account, out uint sequence) ? sequence : 0;
}
```

`src/XrplFile.Core/OpenLedgerFee.cs`:

```csharp
namespace XrplFile;

/// <summary>What the <c>fee</c> method says now (spec B15): the open ledger's price and the base fee under it.</summary>
public sealed record OpenLedgerFee(long OpenLedgerFeeDrops, long BaseFeeDrops);
```

`src/XrplFile.Core/Amendments.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace XrplFile;

/// <summary>The amendments this library asks about (spec B14).</summary>
public static class Amendments
{
    /// <summary>The amendment the Batch path needs.</summary>
    public const string Batch = "BatchV1_1";

    /// <summary>The fixed index of the ledger's singleton <c>Amendments</c> object.</summary>
    public const string SingletonIndex = "7DB0788C020F02780A673DC74757F23823FA3014C1866E72CC4CD8B226CD6EF4";

    /// <summary>An amendment's id: the first half of SHA-512 over its name, upper-case hex.</summary>
    public static string IdOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Convert.ToHexString(SHA512.HashData(Encoding.ASCII.GetBytes(name)).AsSpan(0, 32));
    }
}
```

In `src/XrplFile.Core/LedgerTransaction.cs`, give the record a body (the positional parameters stay as they are):

```csharp
public sealed record LedgerTransaction(
    string Hash,
    long LedgerIndex,
    string Account,
    bool Succeeded,
    bool Validated,
    string? Result,
    string? MemoTypeHex,
    string? MemoDataHex,
    string? MemoFormatHex)
{
    /// <summary>The transaction's type as the node names it, or null when the record does not say.</summary>
    public string? TransactionType { get; init; }

    /// <summary>The sequence it consumed; zero for a transaction that rode a ticket (spec B30).</summary>
    public uint Sequence { get; init; }

    /// <summary>The ticket it rode, or null.</summary>
    public uint? TicketSequence { get; init; }

    /// <summary>How many tickets a <c>TicketCreate</c> asked for, or null for anything else.</summary>
    public uint? TicketCount { get; init; }

    /// <summary>The fee it carried, in drops: "0" for a Batch inner. Null when the record does not say.</summary>
    public long? Fee { get; init; }

    /// <summary>The outer Batch an inner belongs to, upper case; null for anything that is not an inner (spec B12).</summary>
    public string? ParentBatchID { get; init; }
}
```

In `src/XrplFile.Core/ILedgerSource.cs`, add to `ILedgerSource` after `CurrentLedgerAsync`:

```csharp
    /// <summary>
    /// The latest validated ledger index, then each account's next sequence read at exactly that
    /// ledger (spec B6). A ledger that cannot be read at that index fails the snapshot; it never
    /// falls back to "validated". An account the ledger does not carry has sequence zero.
    /// </summary>
    Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not take ledger snapshots");

    /// <summary>The <c>fee</c> method's open-ledger fee and base fee (spec B15).</summary>
    Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not read the open-ledger fee");

    /// <summary>
    /// Whether the validated ledger's <c>Amendments</c> object lists the amendment named
    /// <paramref name="name"/> (spec B14, 4.3). No such object means nothing is enabled.
    /// </summary>
    Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not read amendments");
```

- [ ] **Step 4: Implement them in `XrplClientLedgerSource`**

Add `using System.Globalization;` at the top. Add after `CurrentLedgerAsync`:

```csharp
    public async Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        long ledger = await CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        if (ledger is <= 0 or > uint.MaxValue)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node answered ledger index {ledger}, which no ledger has");
        }

        Dictionary<string, uint> sequences = new(StringComparer.Ordinal);

        foreach (string account in accounts)
        {
            // Pinned to the index just read (spec B6). An error other than actNotFound -- lgrNotFound
            // above all -- leaves here as NodeUnreachable, and the caller takes the snapshot again.
            JsonElement? answer = await AskOrAbsentAsync(
                new AccountInfoRequest(account) { LedgerIndex = new LedgerIndex((uint)ledger) },
                "account_info", XrplErrorCodes.ActNotFound, cancellationToken).ConfigureAwait(false);

            sequences[account] =
                answer is { } found && found.TryGetProperty("account_data", out JsonElement data) && UInt(data, "Sequence") is { } sequence
                    ? sequence
                    : 0;
        }

        return new LedgerSnapshot(ledger, sequences);
    }

    public async Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken)
    {
        JsonElement answer = await AskAsync(new FeeRequest(), "fee", cancellationToken).ConfigureAwait(false);

        if (!answer.TryGetProperty("drops", out JsonElement drops)
            || Drops(drops, "open_ledger_fee") is not { } open
            || Drops(drops, "base_fee") is not { } baseFee)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, "the node answered fee without its open-ledger and base fees");
        }

        return new OpenLedgerFee(open, baseFee);
    }

    public async Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken)
    {
        string id = Amendments.IdOf(name);

        // entryNotFound is an answer: a ledger with no Amendments object has none enabled (spec 4.3).
        JsonElement? answer = await AskOrAbsentAsync(
            new LedgerEntryRequest
            {
                Index = Amendments.SingletonIndex,
                LedgerIndex = new LedgerIndex(LedgerIndexType.Validated),
            },
            "ledger_entry", "entryNotFound", cancellationToken).ConfigureAwait(false);

        if (answer is not { } found
            || !found.TryGetProperty("node", out JsonElement node)
            || !node.TryGetProperty("Amendments", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && string.Equals(entry.GetString(), id, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>A drops amount, sent as a string by rippled and as a number by some proxies.</summary>
    private static long? Drops(JsonElement json, string name) =>
        Text(json, name) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ? parsed
        : Number(json, name);

    private static uint? UInt(JsonElement json, string name) =>
        Number(json, name) is { } value and >= 0 and <= uint.MaxValue ? (uint)value : null;
```

In `SendAsync`, add two arms before the `_ =>` arm:

```csharp
            FeeRequest fee => _client!.GRequest<object, FeeRequest>(fee, cancellationToken),
            LedgerEntryRequest entry => _client!.GRequest<object, LedgerEntryRequest>(entry, cancellationToken),
```

and extend the arm's message to "…, ledger, fee and ledger_entry, nothing else".

Replace the tail of `ParseRecord` (from `string? outcome = …` to the end of the method) with:

```csharp
        JsonElement meta = record.TryGetProperty("meta", out JsonElement found) ? found : default;
        string? outcome = Text(meta, "TransactionResult");
        bool validated = record.TryGetProperty("validated", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

        // An inner carries ParentBatchID in its metadata (measured on the stand); the transaction is
        // read as a fallback for a node that puts it there instead.
        string? parentBatch = Text(meta, "ParentBatchID") ?? Text(tx, "ParentBatchID");

        return new LedgerTransaction(
            Hash: (Text(record, "hash") ?? Text(tx, "hash") ?? string.Empty).ToUpperInvariant(),
            LedgerIndex: Number(record, "ledger_index") ?? Number(tx, "ledger_index") ?? 0,
            Account: Text(tx, "Account") ?? string.Empty,
            Succeeded: string.Equals(outcome, "tesSUCCESS", StringComparison.Ordinal),
            Validated: validated,
            Result: outcome,
            MemoTypeHex: memoType,
            MemoDataHex: memoData,
            MemoFormatHex: memoFormat)
        {
            TransactionType = Text(tx, "TransactionType"),
            Sequence = UInt(tx, "Sequence") ?? 0,
            TicketSequence = UInt(tx, "TicketSequence"),
            TicketCount = UInt(tx, "TicketCount"),
            Fee = Drops(tx, "Fee"),
            ParentBatchID = parentBatch?.ToUpperInvariant(),
        };
```

- [ ] **Step 5: Run the unit tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSourceTests -class XrplFile.Tests.AmendmentsTests`
Expected: PASS, the old tests of the class included.

- [ ] **Step 6: Write the stand tests (spec 7.2, data half)**

`tests/XrplFile.Core.Tests/LedgerDataStandaloneTests.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Client.Json;
using Xrpl.Models.Methods;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

[Trait("Category", "Integration")]
[Collection("Standalone")]
public class LedgerDataStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    [Fact]
    public async Task A_snapshot_reads_two_accounts_at_one_ledger()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet first = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            XrplWallet second = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);

            LedgerSnapshot snapshot = await source.SnapshotAsync([first.ClassicAddress, second.ClassicAddress], token);

            foreach (XrplWallet wallet in new[] { first, second })
            {
                XrplResponse<object> pinned = await client.GRequest<object, AccountInfoRequest>(
                    new AccountInfoRequest(wallet.ClassicAddress) { LedgerIndex = new Xrpl.Models.Common.LedgerIndex((uint)snapshot.LedgerIndex) }, token);

                uint expected = pinned.Raw.ToJsonElement().GetProperty("account_data").GetProperty("Sequence").GetUInt32();
                Assert.Equal(expected, snapshot.SequenceOf(wallet.ClassicAddress));
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task The_open_ledger_fee_is_at_least_the_base_fee()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            OpenLedgerFee fee = await new XrplClientLedgerSource(client).OpenLedgerFeeAsync(timeout.Token);

            Assert.True(fee.BaseFeeDrops > 0);
            Assert.True(fee.OpenLedgerFeeDrops >= fee.BaseFeeDrops);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task The_stand_enables_BatchV1_1_and_not_a_made_up_amendment()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(timeout.Token);

        try
        {
            XrplClientLedgerSource source = new(client);

            Assert.True(await source.IsAmendmentEnabledAsync(Amendments.Batch, timeout.Token));
            Assert.False(await source.IsAmendmentEnabledAsync("XrplFileMadeUpAmendment", timeout.Token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task An_inner_is_read_back_with_its_parent_batch_and_a_zero_fee()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            XrplWallet writerWallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            XrplWallet payerWallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            ITransactionSigner writer = new WalletSigner(writerWallet);
            ITransactionSigner payer = new WalletSigner(payerWallet);

            uint writerSequence = await sink.SequenceAsync(writer.Account, validated: false, token);
            uint payerSequence = await sink.SequenceAsync(payer.Account, validated: false, token);
            long from = await source.CurrentLedgerAsync(token);

            MemoSubmission[] memos =
            [
                new(MemoLayout.ChunkMarkerHex(0), MemoLayout.Hex([1, 2, 3]), null),
                new(MemoLayout.ChunkMarkerHex(1), MemoLayout.Hex([4, 5, 6]), null),
            ];

            SignedTransaction signed = await BatchTransactions.SignAsync(
                BatchTransactions.Outer(payer.Account, payer.PublicKey, payerSequence, 5 * 15, (uint)(from + 20),
                    [.. memos.Select((memo, i) => BatchTransactions.Inner(writer.Account, writerSequence + (uint)i, memo))]),
                writer, payer, token);

            XrplResponse<object> submitted = await client.GRequest<object, SubmitRequest>(new SubmitRequest { TxBlob = signed.TxBlob }, token);
            Assert.Equal("tesSUCCESS", submitted.Raw.ToJsonElement().GetProperty("engine_result").GetString());

            while (await sink.SequenceAsync(writer.Account, validated: true, token) < writerSequence + 2)
            {
                await StandaloneFixture.CloseLedgersUntilAsync(client, await source.CurrentLedgerAsync(token) + 1, token);
            }

            List<LedgerTransaction> inners = [];
            await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer.Account, from, await source.CurrentLedgerAsync(token), forward: true, token))
            {
                if (tx.ParentBatchID is not null) inners.Add(tx);
            }

            Assert.Equal(2, inners.Count);
            Assert.All(inners, inner =>
            {
                Assert.Equal(signed.Hash, inner.ParentBatchID);
                Assert.Equal("AccountSet", inner.TransactionType);
                Assert.Equal(0L, inner.Fee);
                Assert.True(inner.Succeeded);
            });
            Assert.Equal([writerSequence, writerSequence + 1], inners.Select(inner => inner.Sequence).Order().ToArray());
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

- [ ] **Step 7: Run the stand tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LedgerDataStandaloneTests`
Expected: PASS (4 tests).

- [ ] **Step 8: Run the whole Core unit suite and commit**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

```bash
git add src/XrplFile.Core/LedgerSnapshot.cs src/XrplFile.Core/OpenLedgerFee.cs src/XrplFile.Core/Amendments.cs src/XrplFile.Core/LedgerTransaction.cs src/XrplFile.Core/ILedgerSource.cs src/XrplFile.Core/XrplClientLedgerSource.cs tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs tests/XrplFile.Core.Tests/AmendmentsTests.cs tests/XrplFile.Core.Tests/LedgerDataStandaloneTests.cs
git commit -m "feat(core): pinned snapshots, the open-ledger fee, the Batch amendment and inner fields"
```

---

### Task 3: Transport — one transmission of a signed blob, and reconnection

Spec B24, 7.2 (transport half).

**Files:**
- Create: `src/XrplFile.Core/ClientKeeper.cs`
- Modify: `src/XrplFile.Core/ILedgerSink.cs`, `src/XrplFile.Core/XrplClientLedgerSink.cs`, `src/XrplFile.Core/XrplClientLedgerSource.cs` (`SendAsync`), `app/XrplFile.App.Core/XrplConnectionFactory.cs`
- Create: `tests/XrplFile.Core.Tests/DroppingProxy.cs`, `tests/XrplFile.Core.Tests/TransportStandaloneTests.cs`
- Test: `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs` (append)

**Interfaces:**
- Produces:
  - `ILedgerSink.SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)` → `Task<string>`: the `engine_result`, or `error:<code>` for an error answer in either shape; one `submit`, no retry, no `tx` lookup; a transport failure throws `XrplFileException(NodeUnreachable)`. Default implementation throws `NotSupportedException`.
  - `public sealed class ClientKeeper`, the one owner of reconnection (spec B24, Spec question 25). Public: `static XrplClient CreateClient(string url)` — the only way this repository builds a client for publishing, with the SDK's own reconnection set for whichever owner Step 8 keeps — and `static void Retire(IXrplClient client)`, which a connection's owner calls when it closes it, so nothing reconnects it afterwards. Internal: `static ClientKeeper For(IXrplClient client)`, `Task EnsureConnectedAsync(CancellationToken)`.
  - Test helper `internal sealed class DroppingProxy : IAsyncDisposable` with `DroppingProxy(Uri target)`, `Uri Endpoint`, `void DropAll()`.

- [ ] **Step 1: Write the failing unit tests**

Append to `tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_blob_is_transmitted_once_and_its_answer_returned_as_it_came()
    {
        ScriptedNode node = new();
        node.Answer("""{"engine_result":"terPRE_SEQ"}""");
        XrplClientLedgerSink sink = new(node.HandleAsync);

        string answer = await sink.SubmitBlobAsync("ABCDEF", TestContext.Current.CancellationToken);

        Assert.Equal("terPRE_SEQ", answer);
        Assert.Equal(("submit", "ABCDEF"), Assert.Single(node.Requests));
    }

    [Fact]
    public async Task A_used_sequence_is_reported_without_looking_the_hash_up()
    {
        ScriptedNode node = new();
        node.Answer("""{"engine_result":"tefPAST_SEQ"}""");
        XrplClientLedgerSink sink = new(node.HandleAsync);

        Assert.Equal("tefPAST_SEQ", await sink.SubmitBlobAsync("ABCDEF", TestContext.Current.CancellationToken));
        Assert.Equal(["submit"], node.Commands);
    }

    [Fact]
    public async Task A_busy_node_answers_with_its_error_code_in_either_shape()
    {
        ScriptedNode node = new();
        node.Answer("""{"error":"tooBusy"}""");
        node.Throw(new RippledException("slowDown", new ErrorResponse { Error = "slowDown" }));
        XrplClientLedgerSink sink = new(node.HandleAsync);

        Assert.Equal("error:tooBusy", await sink.SubmitBlobAsync("AA", TestContext.Current.CancellationToken));
        Assert.Equal("error:slowDown", await sink.SubmitBlobAsync("AA", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_transmission_the_node_never_answers_is_NodeUnreachable_and_is_not_retried()
    {
        ScriptedNode node = new();
        node.Throw(new Xrpl.Client.Exceptions.TimeoutException("submit timed out"));
        XrplClientLedgerSink sink = new(node.HandleAsync);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => sink.SubmitBlobAsync("AA", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Equal(["submit"], node.Commands);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSinkTests`
Expected: build FAILS, `'XrplClientLedgerSink' does not contain a definition for 'SubmitBlobAsync'`.

- [ ] **Step 3: Add `SubmitBlobAsync` to the interface and the sink**

In `src/XrplFile.Core/ILedgerSink.cs`, add to `ILedgerSink` after `SubmitAsync`:

```csharp
    /// <summary>
    /// One transmission of a blob already signed (spec B24): the node's <c>engine_result</c>, or
    /// <c>error:&lt;code&gt;</c> for an error answer. No retry and no lookup -- the caller owns both.
    /// A transmission the node did not answer throws <see cref="XrplFileException"/> with
    /// <see cref="XrplFileError.NodeUnreachable"/>, and the next call on this sink reconnects.
    /// </summary>
    Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not submit signed blobs");
```

In `src/XrplFile.Core/XrplClientLedgerSink.cs` add after `SubmitAsync`:

```csharp
    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(txBlob);

        try
        {
            NodeAnswer answer = await AskAsync(new SubmitRequest { TxBlob = txBlob }, cancellationToken).ConfigureAwait(false);

            return answer.Result.TryGetProperty("engine_result", out JsonElement code) ? code.GetString() ?? string.Empty
                : answer.Result.TryGetProperty("error", out JsonElement error) ? "error:" + error.GetString()
                : answer.Result.GetRawText();
        }
        catch (RippledException e)
        {
            return "error:" + e.Classify().RawError;
        }
        catch (Exception e) when (DidNotAnswer(e, cancellationToken))
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node did not answer a submit: {e.Message}", inner: e);
        }
    }
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplClientLedgerSinkTests`
Expected: PASS.

- [ ] **Step 5: Write the reconnect stand tests and the proxy**

`tests/XrplFile.Core.Tests/DroppingProxy.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace XrplFile.Tests;

/// <summary>
/// A TCP relay in front of the stand that can cut every live connection at once, the way a public
/// node closes a client it finds too slow. It keeps listening, so a client that reconnects gets
/// through again.
/// </summary>
internal sealed class DroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly Lock _gate = new();
    private readonly List<(TcpClient Inbound, TcpClient Outbound)> _pairs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public DroppingProxy(Uri target)
    {
        _targetHost = target.Host;
        _targetPort = target.Port;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public Uri Endpoint => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public void DropAll()
    {
        lock (_gate)
        {
            foreach ((TcpClient inbound, TcpClient outbound) in _pairs)
            {
                // A reset rather than an orderly close: the client sees the connection fail.
                inbound.Client.LingerState = new LingerOption(true, 0);
                inbound.Close();
                outbound.Close();
            }

            _pairs.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAll();

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
            // The listener is gone; nothing waits on the loop any more.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient inbound;

            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            TcpClient outbound = new();

            try
            {
                await outbound.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
                inbound.Close();
                outbound.Dispose();
                continue;
            }

            lock (_gate) _pairs.Add((inbound, outbound));

            _ = PumpAsync(inbound.GetStream(), outbound.GetStream());
            _ = PumpAsync(outbound.GetStream(), inbound.GetStream());
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer);
                if (read == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            // The pair was dropped.
        }
        finally
        {
            from.Close();
            to.Close();
        }
    }
}
```

`tests/XrplFile.Core.Tests/TransportStandaloneTests.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

[Trait("Category", "Integration")]
[Collection("Standalone")]
public class TransportStandaloneTests
{
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    /// <summary>
    /// The call that meets the drop may fail as NodeUnreachable; spec B24 asks only that the next
    /// call reconnects. Three tries cover the one that fails and the one after it.
    /// </summary>
    private static async Task<T> AfterAtMostOneFailureAsync<T>(Func<Task<T>> call)
    {
        int failures = 0;

        while (true)
        {
            try
            {
                return await call();
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable && ++failures <= 1)
            {
            }
        }
    }

    [Fact]
    public async Task A_signed_blob_is_transmitted_once_and_a_second_copy_meets_its_used_sequence()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplWallet wallet = await StandaloneFixture.FundedAccountAsync(client, sink, 50_000_000, token);
            uint sequence = await sink.SequenceAsync(wallet.ClassicAddress, validated: false, token);

            SignedTransaction signed = await new WalletSigner(wallet).SignAsync(new Dictionary<string, object>
            {
                ["TransactionType"] = "AccountSet",
                ["Account"] = wallet.ClassicAddress,
                ["Sequence"] = sequence,
                ["Fee"] = "12",
                ["SigningPubKey"] = wallet.PublicKey,
            }, token);

            Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(signed.TxBlob, token));
            Assert.Contains(await sink.SubmitBlobAsync(signed.TxBlob, token), new[] { "tefPAST_SEQ", "tefALREADY" });
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_client_its_owner_retired_is_refused_rather_than_reconnected()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        IXrplClient client = ClientKeeper.CreateClient(Endpoint);
        await client.Connect(timeout.Token);
        XrplClientLedgerSource source = new(client);

        ClientKeeper.Retire(client);
        await client.Disconnect();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => source.CurrentLedgerAsync(timeout.Token));
        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.False(client.IsConnected());
    }

    [Fact]
    public async Task A_socket_the_node_dropped_is_reconnected_on_the_next_call()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        await using DroppingProxy proxy = new(new Uri(Endpoint));
        IXrplClient client = ClientKeeper.CreateClient(proxy.Endpoint.ToString());
        await client.Connect(token);

        try
        {
            XrplClientLedgerSource source = new(client);
            XrplClientLedgerSink sink = new(client);
            long before = await source.CurrentLedgerAsync(token);

            proxy.DropAll();
            long after = await AfterAtMostOneFailureAsync(() => source.CurrentLedgerAsync(token));
            Assert.True(after >= before);

            proxy.DropAll();
            string answer = await AfterAtMostOneFailureAsync(() => sink.SubmitBlobAsync("00", token));
            Assert.StartsWith("error:", answer, StringComparison.Ordinal);
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

- [ ] **Step 6: Run the stand tests to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TransportStandaloneTests`
Expected: build FAILS (`ClientKeeper` does not exist).

- [ ] **Step 7: Write `ClientKeeper` and call it before every client request**

`src/XrplFile.Core/ClientKeeper.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xrpl.Client;

namespace XrplFile;

/// <summary>
/// The one owner of reconnection (spec B24): a client this repository builds has the SDK's own
/// reconnection switched off, and the next request after a dropped socket connects it again here.
/// The sink and the source of one connection share one client, and so one keeper: two callers that
/// meet the drop together reconnect once. A client its owner retired is refused, never brought back.
/// </summary>
public sealed class ClientKeeper
{
    private static readonly ConditionalWeakTable<IXrplClient, ClientKeeper> Keepers = new();

    private readonly IXrplClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _retired;

    private ClientKeeper(IXrplClient client) => _client = client;

    /// <summary>A client for <paramref name="url"/> whose reconnection this class owns: the SDK's own is off.</summary>
    public static XrplClient CreateClient(string url) =>
        new(url, new XrplClient.ClientOptions { MaxReconnectAttempts = 0, StopAfterMaxAttempts = true });

    /// <summary>The client's owner has closed it: from now on every request on it is refused.</summary>
    public static void Retire(IXrplClient client) => For(client)._retired = true;

    internal static ClientKeeper For(IXrplClient client) => Keepers.GetValue(client, static owner => new ClientKeeper(owner));

    internal async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_retired) throw Closed();
        if (_client.IsConnected()) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_retired) throw Closed();
            if (_client.IsConnected()) return;

            try
            {
                await _client.Connect(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable, $"the node did not take a new connection: {e.Message}", inner: e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static XrplFileException Closed() => new(XrplFileError.NodeUnreachable, "the connection was closed by its owner");
}
```

In `XrplClientLedgerSink.SendAsync`, add as the first statement:

```csharp
        await ClientKeeper.For(_client!).EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
```

and the same line as the first statement of `XrplClientLedgerSink.SequenceAsync` after its `_client is null` check (with `_client` instead of `_client!`). In `XrplClientLedgerSource.SendAsync`, add the same first statement.

In `app/XrplFile.App.Core/XrplConnectionFactory.cs`, build the client with `client = ClientKeeper.CreateClient(url);` instead of `new XrplClient(url)`, and make `Connection.DisposeAsync` call `ClientKeeper.Retire(_client);` before `Disconnect()`, so a late call on a closed connection is refused instead of reopening it.

- [ ] **Step 8: Run the stand tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TransportStandaloneTests`
Expected: PASS (3 tests).

This decides the owner (Spec question 25): when all three pass, **the keeper owns reconnection and the SDK's is off.**

If `A_socket_the_node_dropped…` fails with `NodeUnreachable` naming "did not take a new connection" — an 11.2.0 client cannot `Connect` again once its socket died — switch to the other owner: **the SDK reconnects, and the keeper only waits.** Replace `CreateClient` and `EnsureConnectedAsync` with the code below, delete `_gate`, change the class summary to say the SDK owns reconnection and this class waits for it, rerun, and name the owner kept in the commit body.

```csharp
    /// <summary>How long a request waits for the SDK to restore a lost socket before it is a failure.</summary>
    internal static readonly TimeSpan ReconnectWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>A client for <paramref name="url"/> that restores a lost socket itself; this class never calls Connect.</summary>
    public static XrplClient CreateClient(string url) =>
        new(url, new XrplClient.ClientOptions { MaxReconnectAttempts = 10, StopAfterMaxAttempts = true });

    internal async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + ReconnectWait;

        while (true)
        {
            if (_retired) throw Closed();
            if (_client.IsConnected()) return;

            if (DateTimeOffset.UtcNow >= until)
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable, $"the client did not reconnect within {ReconnectWait}");
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }
```

- [ ] **Step 9: Run every Core unit test and commit**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

```bash
git add src/XrplFile.Core/ClientKeeper.cs src/XrplFile.Core/ILedgerSink.cs src/XrplFile.Core/XrplClientLedgerSink.cs src/XrplFile.Core/XrplClientLedgerSource.cs app/XrplFile.App.Core/XrplConnectionFactory.cs tests/XrplFile.Core.Tests/XrplClientLedgerSinkTests.cs tests/XrplFile.Core.Tests/DroppingProxy.cs tests/XrplFile.Core.Tests/TransportStandaloneTests.cs
git commit -m "feat(core): submit a signed blob once and reconnect a dropped client" -m "Reconnection is owned by <ClientKeeper | the SDK>, as the stand test decided."
```

---

### Task 4: The new errors and their sentences

Spec 4.7, 6.5, B22. Added in one place, early, so no later task leaves the exhaustiveness test red.

**Files:**
- Modify: `src/XrplFile.Core/XrplFileException.cs`
- Modify: `app/XrplFile.App.Core/PublishErrorMessages.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs` (append)

**Interfaces:**
- Produces:
  - `XrplFileError.BatchNotEnabled`, `.PayerInsufficientBalance`, `.AccountInUse`, `.FeeTooHigh`
  - `public enum AccountRole { Writer, Payer }`; `XrplFileException.Role` (`AccountRole?`), set through a new last constructor parameter `AccountRole? role = null`
  - `PublishErrorMessages.FeeTooHighNow` (const string) — the sentence for `FeeTooHigh` before any state exists (Spec question 6)
  - `PublishErrorMessages.HistoryLaggedWhilePublishing` (const string) — `NodeLacksHistory` raised mid-run by B30

- [ ] **Step 1: Write the failing tests**

Append inside `PublishErrorMessagesTests`:

```csharp
    [Fact]
    public void Batch_not_enabled_sends_the_person_to_Nobody()
    {
        Assert.Equal(
            "This network does not enable Batch. Choose Nobody under Paid by to publish with tickets.",
            PublishErrorMessages.For(XrplFileError.BatchNotEnabled));
    }

    [Fact]
    public void A_short_payer_is_told_apart_from_a_short_writer()
    {
        Assert.Equal(
            "The payer does not hold enough XRP for this publication. Fund it, or choose another payer.",
            PublishErrorMessages.For(XrplFileError.PayerInsufficientBalance));
    }

    [Theory]
    [InlineData(AccountRole.Writer, "writer")]
    [InlineData(AccountRole.Payer, "payer")]
    public void An_account_in_use_names_its_role(AccountRole role, string word)
    {
        XrplFileException thrown = new(XrplFileError.AccountInUse, "rX sent a payment", role: role);

        Assert.Equal(
            $"The {word} account sent a transaction of its own during the publication. Is this key in use somewhere else? "
                + "The run is listed under unfinished runs; resume it once nothing else uses the key.",
            PublishErrorMessages.For(thrown));
    }

    [Fact]
    public void Fees_that_rose_mid_run_say_the_run_can_be_resumed()
    {
        Assert.Equal(
            "Network fees rose above what this publication was approved for. The run is listed under unfinished runs; "
                + "resume it when fees are back down.",
            PublishErrorMessages.For(XrplFileError.FeeTooHigh));
    }

    [Fact]
    public void Fees_too_high_before_anything_started_do_not_mention_an_unfinished_run()
    {
        XrplFileException thrown = new(XrplFileError.FeeTooHigh, "open ledger fee 250");

        string sentence = PublishErrorMessages.ForRun(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0);

        Assert.Equal(PublishErrorMessages.FeeTooHighNow, sentence);
        Assert.DoesNotContain("unfinished", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void History_that_lags_a_running_publication_is_not_described_as_a_reader_problem()
    {
        XrplFileException thrown = new(XrplFileError.NodeLacksHistory, "account_tx lagged");

        Assert.Equal(
            PublishErrorMessages.HistoryLaggedWhilePublishing,
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.WhileSubmitting, usesTickets: false, ownerReserveDrops: 0));
        Assert.Equal(
            PublishErrorMessages.For(XrplFileError.NodeLacksHistory),
            PublishErrorMessages.ForRun(thrown, RunFailurePoint.BeforeSubmitting, usesTickets: false, ownerReserveDrops: 0));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishErrorMessagesTests`
Expected: build FAILS (`XrplFileError` has no `BatchNotEnabled`).

- [ ] **Step 3: Add the errors and the role**

In `src/XrplFile.Core/XrplFileException.cs`, append to `XrplFileError` after `ResumeMismatch`:

```csharp
    /// <summary>A Batch run on a network whose ledger does not enable <c>BatchV1_1</c> (spec 4.7).</summary>
    BatchNotEnabled,

    /// <summary>The payer's spendable balance is below its part of a Batch run (spec 4.7).</summary>
    PayerInsufficientBalance,

    /// <summary>
    /// A transaction of the writer or the payer that the run did not send applied during a round
    /// (spec B12). <see cref="XrplFileException.Role"/> says which account.
    /// </summary>
    AccountInUse,

    /// <summary>
    /// The open-ledger fee is above 200 drops at estimate time, or above the approved ceiling for a
    /// minute during a run (spec B15).
    /// </summary>
    FeeTooHigh,
```

Add before `XrplFileException`:

```csharp
/// <summary>Which of a Batch run's two accounts an error is about.</summary>
public enum AccountRole
{
    Writer,
    Payer,
}
```

In `XrplFileException`, add the property and extend the constructor:

```csharp
    /// <summary>For <see cref="XrplFileError.AccountInUse"/>: the account that was in use; otherwise null.</summary>
    public AccountRole? Role { get; }

    public XrplFileException(
        XrplFileError error, string message, int? block = null, Exception? inner = null, string? engineResult = null,
        AccountRole? role = null)
        : base(message, inner)
    {
        Error = error;
        Block = block;
        EngineResult = engineResult;
        Role = role;
    }
```

- [ ] **Step 4: Add the sentences**

In `app/XrplFile.App.Core/PublishErrorMessages.cs`, add after `FeeTooLargeForApplication`:

```csharp
    /// <summary>
    /// <see cref="XrplFileError.FeeTooHigh"/> raised before a run exists -- by an estimate, or by a
    /// fresh start that never wrote a state file -- where "listed under unfinished runs" would be false.
    /// </summary>
    public const string FeeTooHighNow =
        "Network fees are above what this application publishes at right now. Try again when they are back down.";

    /// <summary>
    /// <see cref="XrplFileError.NodeLacksHistory"/> raised while publishing (spec B30): the node's history
    /// lagged the run, which is not the reader's "history far enough back" problem.
    /// </summary>
    public const string HistoryLaggedWhilePublishing =
        "The node's history fell behind the publication, so what landed could not be confirmed. The run is listed under "
        + "unfinished runs; resume it, or choose another node in Settings first.";

    private static string AccountInUse(string role) =>
        $"The {role} account sent a transaction of its own during the publication. Is this key in use somewhere else? "
        + "The run is listed under unfinished runs; resume it once nothing else uses the key.";
```

Add four arms to `For(XrplFileError)` before `_ =>`:

```csharp
        XrplFileError.BatchNotEnabled =>
            "This network does not enable Batch. Choose Nobody under Paid by to publish with tickets.",
        XrplFileError.PayerInsufficientBalance =>
            "The payer does not hold enough XRP for this publication. Fund it, or choose another payer.",
        XrplFileError.AccountInUse => AccountInUse("writer or payer"),
        XrplFileError.FeeTooHigh =>
            "Network fees rose above what this publication was approved for. The run is listed under unfinished runs; "
            + "resume it when fees are back down.",
```

Replace `For(XrplFileException exception)` with:

```csharp
    public static string For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string sentence = exception is { Error: XrplFileError.AccountInUse, Role: { } role }
            ? AccountInUse(role == AccountRole.Writer ? "writer" : "payer")
            : For(exception.Error);

        return exception.Block is int block ? $"{sentence} (block {block})" : sentence;
    }
```

In `ForRun`, replace its first line `if (exception.Error != XrplFileError.NodeUnreachable) return For(exception);` with:

```csharp
        if (exception.Error == XrplFileError.FeeTooHigh && point == RunFailurePoint.BeforeSubmitting) return FeeTooHighNow;

        if (exception.Error == XrplFileError.NodeLacksHistory && point == RunFailurePoint.WhileSubmitting) return HistoryLaggedWhilePublishing;

        if (exception.Error != XrplFileError.NodeUnreachable) return For(exception);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishErrorMessagesTests`
Expected: PASS, including the existing exhaustiveness and distinctness tests.

- [ ] **Step 6: Build everything and commit**

Run: `dotnet build XrplFile.sln` — Expected: 0 errors, 0 warnings.

```bash
git add src/XrplFile.Core/XrplFileException.cs app/XrplFile.App.Core/PublishErrorMessages.cs tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs
git commit -m "feat: errors and sentences for Batch publishing"
```

---
### Task 5: `FakeBatchLedgerSink` and the fake source's new answers

Spec 7.3 (the fake). The fake is real enough to be worth its own review: it decodes the signed blob with the SDK's codec, applies an outer to the open ledger at submit, and applies the inners at a close in the order rippled does.

**Files:**
- Create: `tests/XrplFile.TestSupport/FakeBatchLedgerSink.cs`
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSource.cs`
- Test: `tests/XrplFile.Core.Tests/FakeBatchLedgerSinkTests.cs`

**Interfaces:**
- Consumes: `BatchTransactions` (Task 1), `LedgerSnapshot`, `OpenLedgerFee`, `Amendments` (Task 2), `ILedgerSink.SubmitBlobAsync` (Task 3).
- Produces (namespace `XrplFile.Tests`):
  - `FakeLedgerSource`: `event Action? LedgerRead` (raised at the start of `SnapshotAsync` and `CurrentLedgerAsync`, outside the fake's lock); `void RecordSequences(long ledger, IReadOnlyDictionary<string, uint> sequences)`; `OpenLedgerFee OpenLedgerFee { get; set; }` (default `(10, 10)`); `Queue<OpenLedgerFee> OpenLedgerFeeScript`; `HashSet<string> EnabledAmendments` (default `{ "BatchV1_1" }`); `Exception? AmendmentFailure`; `int SnapshotFailures`; `int SnapshotReads`; `Func<LedgerTransaction, bool>? HistoryHides`; `int HistoryHideReads`; `void SetTicketCount(string account, int count)` (under the fake's lock); implementations of the three new `ILedgerSource` members.
  - `FakeBatchLedgerSink : ILedgerSink` with: ctor `(FakeLedgerSource ledger)`; `bool AutoClose` (default true: every ledger read closes one ledger first); `void SetSequence(string account, uint next)`; `void CloseLedger()`; `int Closes`; `IReadOnlyList<FakeBatchLedgerSink.Transmission> Transmissions`; `int PeakInFlight`; `TimeSpan SubmitDelay`; `Action<Transmission>? Transmitting`; scripts `Dictionary<(uint Sequence, int Copy), string> AnswerAt`, `HashSet<int> ApplyThenDropAt`, `Dictionary<uint, TimeSpan> DelayOuter`, `HashSet<uint> LoseOuter`, `Dictionary<uint, string> OuterResult`, `Dictionary<string, string> InnerResult`, `HashSet<string> InnerVanishes`; `void Foreign(string account, string kind, uint ticketCount = 0)` with kinds `"AccountSet"`, `"TicketCreate"`, `"TicketedAccountSet"`; `void AddLiveTicket(string account, uint ticket, string creatorHash)`.
  - `public sealed record Transmission(int Ordinal, string Hash, string TransactionType, string Account, uint Sequence, uint LastLedger, int Inners, long OpenLedgerIndex, uint PayerValidatedSequence, string Answer)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/FakeBatchLedgerSinkTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The fake the Batch strategy is tested against, held to what the spike measured on the stand.</summary>
public class FakeBatchLedgerSinkTests
{
    private static readonly ITransactionSigner Writer = new WalletSigner(XrplWallet.Generate());
    private static readonly ITransactionSigner Payer = new WalletSigner(XrplWallet.Generate());

    private static MemoSubmission Memo(int index) => new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex([(byte)index]), null);

    private static (FakeLedgerSource Ledger, FakeBatchLedgerSink Sink) Node()
    {
        FakeLedgerSource ledger = new();
        FakeBatchLedgerSink sink = new(ledger) { AutoClose = false };
        sink.SetSequence(Writer.Account, 100);
        sink.SetSequence(Payer.Account, 500);
        return (ledger, sink);
    }

    private static async Task<SignedTransaction> BatchAsync(uint payerSequence, uint writerSequence, int[] memos, uint lastLedger = 10_000) =>
        await BatchTransactions.SignAsync(
            BatchTransactions.Outer(Payer.Account, Payer.PublicKey, payerSequence, (memos.Length + 3) * 15L, lastLedger,
                [.. memos.Select((memo, i) => BatchTransactions.Inner(Writer.Account, writerSequence + (uint)i, Memo(memo)))]),
            Writer, Payer, TestContext.Current.CancellationToken);

    private static async Task<List<LedgerTransaction>> HistoryAsync(FakeLedgerSource ledger, string account)
    {
        List<LedgerTransaction> records = [];
        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(account, 0, ledger.CurrentLedger, true, TestContext.Current.CancellationToken))
        {
            records.Add(tx);
        }

        return records;
    }

    [Fact]
    public async Task An_outer_takes_the_payers_sequence_at_submit_and_its_inners_land_at_the_close()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction batch = await BatchAsync(500, 100, [0, 1, 2]);

        Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(batch.TxBlob, TestContext.Current.CancellationToken));
        Assert.Empty(await HistoryAsync(ledger, Writer.Account));

        sink.CloseLedger();

        List<LedgerTransaction> inners = await HistoryAsync(ledger, Writer.Account);
        Assert.Equal(3, inners.Count);
        Assert.All(inners, inner => Assert.Equal(batch.Hash, inner.ParentBatchID));
        Assert.Equal([100u, 101u, 102u], inners.Select(inner => inner.Sequence).ToArray());

        LedgerSnapshot snapshot = await ledger.SnapshotAsync([Writer.Account, Payer.Account], TestContext.Current.CancellationToken);
        Assert.Equal(103u, snapshot.SequenceOf(Writer.Account));
        Assert.Equal(501u, snapshot.SequenceOf(Payer.Account));
        Assert.Equal(6 * 15L, Assert.Single(await HistoryAsync(ledger, Payer.Account)).Fee);
    }

    [Fact]
    public async Task A_snapshot_answers_for_the_ledger_it_reads_not_for_the_open_one()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        await sink.SubmitBlobAsync((await BatchAsync(500, 100, [0, 1])).TxBlob, TestContext.Current.CancellationToken);

        LedgerSnapshot before = await ledger.SnapshotAsync([Writer.Account, Payer.Account], TestContext.Current.CancellationToken);

        Assert.Equal(100u, before.SequenceOf(Writer.Account));
        Assert.Equal(500u, before.SequenceOf(Payer.Account));
    }

    [Fact]
    public async Task A_later_outer_that_arrives_first_is_held_and_applies_after_the_earlier_one()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction first = await BatchAsync(500, 100, [0, 1]);
        SignedTransaction second = await BatchAsync(501, 102, [2, 3]);

        Assert.Equal("terPRE_SEQ", await sink.SubmitBlobAsync(second.TxBlob, TestContext.Current.CancellationToken));
        Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(first.TxBlob, TestContext.Current.CancellationToken));
        sink.CloseLedger();

        Assert.Equal(4, (await HistoryAsync(ledger, Writer.Account)).Count);
        Assert.Equal("tefPAST_SEQ", await sink.SubmitBlobAsync(first.TxBlob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tec_inner_consumes_its_sequence_and_a_vanished_inner_leaves_a_gap()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        sink.InnerResult[MemoLayout.ChunkMarkerHex(1)] = "tecFAKE";
        sink.InnerVanishes.Add(MemoLayout.ChunkMarkerHex(3));

        await sink.SubmitBlobAsync((await BatchAsync(500, 100, [0, 1, 2, 3, 4])).TxBlob, TestContext.Current.CancellationToken);
        sink.CloseLedger();

        List<LedgerTransaction> inners = await HistoryAsync(ledger, Writer.Account);
        Assert.Equal([100u, 101u, 102u], inners.Select(inner => inner.Sequence).ToArray());
        Assert.Equal("tecFAKE", inners[1].Result);
        Assert.Equal(103u, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
    }

    [Fact]
    public async Task A_foreign_ticket_create_consumes_one_plus_its_count()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        sink.Foreign(Writer.Account, "TicketCreate", 3);
        sink.CloseLedger();

        LedgerTransaction create = Assert.Single(await HistoryAsync(ledger, Writer.Account));
        Assert.Equal("TicketCreate", create.TransactionType);
        Assert.Equal(3u, create.TicketCount);
        Assert.Equal(104u, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
    }

    [Fact]
    public async Task Every_ledger_read_closes_a_ledger_when_auto_close_is_on()
    {
        FakeLedgerSource ledger = new();
        FakeBatchLedgerSink sink = new(ledger);
        long start = ledger.CurrentLedger;

        await ledger.SnapshotAsync([], TestContext.Current.CancellationToken);
        await ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(start + 2, ledger.CurrentLedger);
        Assert.Equal(2, sink.Closes);
    }

    [Fact]
    public async Task An_outer_past_its_deadline_is_refused_at_submit()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction late = await BatchAsync(500, 100, [0, 1], lastLedger: (uint)ledger.CurrentLedger);

        Assert.Equal("tefMAX_LEDGER", await sink.SubmitBlobAsync(late.TxBlob, TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FakeBatchLedgerSinkTests`
Expected: build FAILS, `The type or namespace name 'FakeBatchLedgerSink' could not be found`.

- [ ] **Step 3: Extend `FakeLedgerSource`**

In `tests/XrplFile.TestSupport/FakeLedgerSource.cs`:

1. Add fields and members after `_liveTickets`:

```csharp
    /// <summary>Each account's next sequence from the ledger it was recorded at on: what a snapshot reads at a ledger.</summary>
    private readonly Dictionary<string, SortedList<long, uint>> _sequences = new(StringComparer.Ordinal);

    private int _snapshotFailures;
    private int _snapshotReads;
    private int _historyHideReads;

    /// <summary>
    /// Raised at the start of every <see cref="SnapshotAsync"/> and <see cref="CurrentLedgerAsync"/>,
    /// outside this fake's lock. <see cref="FakeBatchLedgerSink"/> closes a ledger here, so time on
    /// this fake moves at the pace its reader asks.
    /// </summary>
    public event Action? LedgerRead;

    /// <summary>What <see cref="OpenLedgerFeeAsync"/> answers once <see cref="OpenLedgerFeeScript"/> is empty.</summary>
    public OpenLedgerFee OpenLedgerFee { get; set; } = new(10, 10);

    /// <summary>Answers <see cref="OpenLedgerFeeAsync"/> gives first, one per call.</summary>
    public Queue<OpenLedgerFee> OpenLedgerFeeScript { get; } = new();

    /// <summary>The amendments this fake ledger enables, by name.</summary>
    public HashSet<string> EnabledAmendments { get; } = new(StringComparer.Ordinal) { Amendments.Batch };

    /// <summary>Thrown by <see cref="IsAmendmentEnabledAsync"/> instead of answering.</summary>
    public Exception? AmendmentFailure { get; set; }

    /// <summary>How many of the next snapshots fail as a ledger that cannot be read at its index.</summary>
    public int SnapshotFailures
    {
        get { lock (_gate) return _snapshotFailures; }
        set { lock (_gate) _snapshotFailures = value; }
    }

    public int SnapshotReads
    {
        get { lock (_gate) return _snapshotReads; }
    }

    /// <summary>Records account_tx leaves out while <see cref="HistoryHideReads"/> lasts: a history index that lags the ledger.</summary>
    public Func<LedgerTransaction, bool>? HistoryHides { get; set; }

    /// <summary>How many more account_tx calls <see cref="HistoryHides"/> applies to.</summary>
    public int HistoryHideReads
    {
        get { lock (_gate) return _historyHideReads; }
        set { lock (_gate) _historyHideReads = value; }
    }

    /// <summary>Records each account's next sequence as of <paramref name="ledger"/>, and validates that ledger.</summary>
    public void RecordSequences(long ledger, IReadOnlyDictionary<string, uint> sequences)
    {
        lock (_gate)
        {
            foreach ((string account, uint sequence) in sequences)
            {
                if (!_sequences.TryGetValue(account, out SortedList<long, uint>? history))
                {
                    history = [];
                    _sequences[account] = history;
                }

                history[ledger] = sequence;
            }

            if (ledger > _currentLedger) _currentLedger = ledger;
        }
    }

    /// <summary>Sets AccountRoot's TicketCount under the lock <see cref="AccountStateAsync"/> reads it under.</summary>
    public void SetTicketCount(string account, int count)
    {
        lock (_gate) TicketCounts[account] = count;
    }

    public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
    {
        LedgerRead?.Invoke();

        lock (_gate)
        {
            _snapshotReads++;

            if (_snapshotFailures > 0)
            {
                _snapshotFailures--;
                return Task.FromException<LedgerSnapshot>(
                    new XrplFileException(XrplFileError.NodeUnreachable, "fake: the ledger could not be read at the index asked for"));
            }

            Dictionary<string, uint> sequences = new(StringComparer.Ordinal);
            foreach (string account in accounts) sequences[account] = SequenceAt(account, _currentLedger);

            return Task.FromResult(new LedgerSnapshot(_currentLedger, sequences));
        }
    }

    /// <summary>The account's next sequence at <paramref name="ledger"/>. Call under the gate.</summary>
    private uint SequenceAt(string account, long ledger)
    {
        if (!_sequences.TryGetValue(account, out SortedList<long, uint>? history)) return 0;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history.Keys[i] <= ledger) return history.Values[i];
        }

        return 0;
    }

    public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(OpenLedgerFeeScript.TryDequeue(out OpenLedgerFee? next) ? next : OpenLedgerFee);
    }

    public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return AmendmentFailure is { } failure
                ? Task.FromException<bool>(failure)
                : Task.FromResult(EnabledAmendments.Contains(name));
        }
    }
```

2. In `AccountTransactionsAsync`, inside the `lock (_gate)` block, replace the `matching` assignment with:

```csharp
            bool hiding = HistoryHides is not null && _historyHideReads > 0;
            if (hiding) _historyHideReads--;

            IEnumerable<(LedgerTransaction Tx, int Position)> matching = _transactions
                .Select((tx, position) => (Tx: tx, Position: position))
                .Where(pair => Touches(pair.Tx, account)
                    && pair.Tx.LedgerIndex >= fromLedger && pair.Tx.LedgerIndex <= toLedger
                    && !(hiding && HistoryHides!(pair.Tx)));
```

3. In `CurrentLedgerAsync`, add `LedgerRead?.Invoke();` as the first statement, before `lock (_gate)`.

- [ ] **Step 4: Write `FakeBatchLedgerSink`**

`tests/XrplFile.TestSupport/FakeBatchLedgerSink.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Utils.Hashes;

namespace XrplFile.Tests;

/// <summary>
/// A node for the Batch path, modelled on what the spike measured (spec section 1, 7.3).
///
/// A submitted blob is decoded with the SDK's own codec. An outer is applied to the open ledger at
/// submit -- it takes the payer's sequence there -- and its inners are applied when the ledger
/// closes, against the writer's validated sequence, in the order the outers were accepted. A
/// <c>tfIndependent</c> inner that ends in tec consumes its sequence; an inner whose sequence is not
/// the writer's next fails with no record. A later outer that arrives first answers terPRE_SEQ and
/// is held until its turn or its deadline. Foreign transactions queued with <see cref="Foreign"/>
/// apply first at the next close. With <see cref="AutoClose"/> every ledger read closes a ledger.
/// </summary>
public sealed class FakeBatchLedgerSink : ILedgerSink
{
    private readonly Lock _gate = new();
    private readonly FakeLedgerSource _ledger;
    private readonly Dictionary<string, uint> _validated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _open = new(StringComparer.Ordinal);
    private readonly List<Pending> _openLedger = [];
    private readonly List<Pending> _held = [];
    private readonly List<Transmission> _transmissions = [];
    private readonly List<(string Account, string Kind, uint Count)> _foreign = [];
    private readonly Dictionary<string, Dictionary<uint, string>> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _copies = [];
    private int _submits;
    private int _inFlight;
    private int _peakInFlight;
    private int _closes;

    public FakeBatchLedgerSink(FakeLedgerSource ledger)
    {
        _ledger = ledger;
        _ledger.LedgerRead += () =>
        {
            if (AutoClose) CloseLedger();
        };
    }

    /// <summary>One submission as the fake saw it, and what it answered.</summary>
    public sealed record Transmission(
        int Ordinal, string Hash, string TransactionType, string Account, uint Sequence, uint LastLedger, int Inners,
        long OpenLedgerIndex, uint PayerValidatedSequence, string Answer);

    private sealed record Pending(string Hash, JsonObject Tx, string Type, string Account, uint Sequence, uint LastLedger);

    /// <summary>Closes a ledger at the start of every ledger read. On by default.</summary>
    public bool AutoClose { get; set; } = true;

    /// <summary>How long each submission is held before it is processed, so concurrent senders overlap.</summary>
    public TimeSpan SubmitDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Called for each processed submission, before its answer returns.</summary>
    public Action<Transmission>? Transmitting { get; set; }

    /// <summary>
    /// Submissions answered with this and not processed, keyed by the transaction's sequence and which
    /// transmission of that sequence it is (1-based, counted over every blob that carries it). Keyed by
    /// the candidate rather than by arrival order, so a script meant for one round never hits the next
    /// round's first send.
    /// </summary>
    public Dictionary<(uint Sequence, int Copy), string> AnswerAt { get; } = [];

    /// <summary>Submissions (by arrival ordinal) processed, then answered with a dropped connection.</summary>
    public HashSet<int> ApplyThenDropAt { get; } = [];

    /// <summary>Outers (by sequence) held this long before they are processed: a later one overtakes them.</summary>
    public Dictionary<uint, TimeSpan> DelayOuter { get; } = [];

    /// <summary>Outers (by sequence) answered tesSUCCESS and never applied; one-shot.</summary>
    public HashSet<uint> LoseOuter { get; } = [];

    /// <summary>Outers (by sequence) that apply with this tec at the close: fee and sequence taken, no inner applied; one-shot.</summary>
    public Dictionary<uint, string> OuterResult { get; } = [];

    /// <summary>Inners (by memo marker) that apply with this tec every time; the sequence is consumed.</summary>
    public Dictionary<string, string> InnerResult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inners (by memo marker) that neither apply nor consume a sequence; one-shot.</summary>
    public HashSet<string> InnerVanishes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Closes
    {
        get { lock (_gate) return _closes; }
    }

    public int PeakInFlight
    {
        get { lock (_gate) return _peakInFlight; }
    }

    public IReadOnlyList<Transmission> Transmissions
    {
        get { lock (_gate) return [.. _transmissions]; }
    }

    public void SetSequence(string account, uint next)
    {
        lock (_gate)
        {
            _validated[account] = next;
            _open[account] = next;
            _ledger.RecordSequences(_ledger.CurrentLedger, new Dictionary<string, uint> { [account] = next });
        }
    }

    /// <summary>Queues a transaction nobody in the test signed, applied first at the next close.</summary>
    public void Foreign(string account, string kind, uint ticketCount = 0)
    {
        lock (_gate) _foreign.Add((account, kind, ticketCount));
    }

    /// <summary>Gives the account a live ticket created by <paramref name="creatorHash"/>.</summary>
    public void AddLiveTicket(string account, uint ticket, string creatorHash)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(account, out Dictionary<uint, string>? live))
            {
                live = [];
                _tickets[account] = live;
            }

            live[ticket] = creatorHash;
            PublishTickets(account);
        }
    }

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult((validated ? _validated : _open).GetValueOrDefault(account, 1u));
    }

    public async Task<SubmitOutcome> SubmitAsync(
        Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string answer = await SubmitBlobAsync(signed.TxBlob, cancellationToken).ConfigureAwait(false);
        return new SubmitOutcome(answer, signed.Hash);
    }

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
        string hash = HashLedger.HashSignedTx(txBlob).ToUpperInvariant();
        string type = (string)tx["TransactionType"]!;
        string account = (string)tx["Account"]!;
        uint sequence = (uint)Number(tx["Sequence"]);
        uint lastLedger = (uint)Number(tx["LastLedgerSequence"]);
        int inners = tx["RawTransactions"] is JsonArray raw ? raw.Count : 0;
        int ordinal;
        int copy;

        lock (_gate)
        {
            ordinal = _submits++;
            copy = _copies[sequence] = _copies.GetValueOrDefault(sequence) + 1;
            _inFlight++;
            if (_inFlight > _peakInFlight) _peakInFlight = _inFlight;
        }

        try
        {
            await Task.Yield();

            if (SubmitDelay > TimeSpan.Zero) await Task.Delay(SubmitDelay, cancellationToken).ConfigureAwait(false);

            if (type == "Batch" && DelayOuter.TryGetValue(sequence, out TimeSpan delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            Transmission transmission;

            lock (_gate)
            {
                string answer = AnswerAt.TryGetValue((sequence, copy), out string? scripted)
                    ? scripted
                    : Apply(new Pending(hash, tx, type, account, sequence, lastLedger));

                transmission = new Transmission(
                    ordinal, hash, type, account, sequence, lastLedger, inners, _ledger.CurrentLedger + 1,
                    type == "Batch" ? _validated.GetValueOrDefault(account, 1u) : 0, answer);
                _transmissions.Add(transmission);
            }

            Transmitting?.Invoke(transmission);

            if (ApplyThenDropAt.Contains(ordinal))
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable, "fake: the connection dropped before the answer");
            }

            return transmission.Answer;
        }
        finally
        {
            lock (_gate) _inFlight--;
        }
    }

    /// <summary>Closes the open ledger: foreign transactions first, then what was accepted, in order.</summary>
    public void CloseLedger()
    {
        lock (_gate)
        {
            long index = _ledger.CurrentLedger + 1;
            int position = 0;

            foreach ((string account, string kind, uint count) in _foreign) WriteForeign(account, kind, count, index, position++);
            _foreign.Clear();

            foreach (Pending pending in _openLedger) Write(pending, index);
            _openLedger.Clear();

            // Nothing held can apply in a ledger past its deadline.
            _held.RemoveAll(held => held.LastLedger != 0 && held.LastLedger < index + 1);

            foreach (string account in _validated.Keys.ToList())
            {
                _open[account] = Math.Max(_open.GetValueOrDefault(account, 1u), _validated[account]);
            }

            _ledger.RecordSequences(index, new Dictionary<string, uint>(_validated, StringComparer.Ordinal));
            _closes++;
        }
    }

    /// <summary>Called under the gate.</summary>
    private string Apply(Pending pending)
    {
        long openLedger = _ledger.CurrentLedger + 1;

        if (pending.Tx["TicketSequence"] is { } ticketNode)
        {
            uint ticket = (uint)Number(ticketNode);
            bool taken = _openLedger.Any(open => open.Account == pending.Account
                && open.Tx["TicketSequence"] is { } other && (uint)Number(other) == ticket);

            if (taken || !_tickets.TryGetValue(pending.Account, out Dictionary<uint, string>? live) || !live.ContainsKey(ticket)) return "tefNO_TICKET";
            if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

            // The ticket stays listed until the close validates its spend, as a validated listing does.
            _openLedger.Add(pending);
            return "tesSUCCESS";
        }

        uint next = _open.GetValueOrDefault(pending.Account, 1u);

        if (pending.Sequence < next) return "tefPAST_SEQ";
        if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

        if (pending.Sequence > next)
        {
            if (!_held.Any(held => held.Hash == pending.Hash)) _held.Add(pending);
            return "terPRE_SEQ";
        }

        if (pending.Type == "Batch" && LoseOuter.Remove(pending.Sequence)) return "tesSUCCESS";

        Accept(pending);
        return "tesSUCCESS";
    }

    /// <summary>Called under the gate.</summary>
    private void Accept(Pending pending)
    {
        _openLedger.Add(pending);
        _open[pending.Account] = pending.Sequence + 1;

        while (_held.FirstOrDefault(held => held.Account == pending.Account && held.Sequence == _open[pending.Account]) is { } ready)
        {
            _held.Remove(ready);
            _openLedger.Add(ready);
            _open[ready.Account] = ready.Sequence + 1;
        }
    }

    /// <summary>Called under the gate.</summary>
    private void Write(Pending pending, long index)
    {
        if (pending.Tx["TicketSequence"] is { } ticketNode)
        {
            if (_tickets.TryGetValue(pending.Account, out Dictionary<uint, string>? live) && live.Remove((uint)Number(ticketNode)))
            {
                PublishTickets(pending.Account);
            }

            (string? type, string? data, string? format) = MemoOf(pending.Tx);
            _ledger.Add(new LedgerTransaction(pending.Hash, index, pending.Account, true, true, "tesSUCCESS", type, data, format)
            {
                TransactionType = pending.Type,
                Sequence = 0,
                TicketSequence = (uint)Number(ticketNode),
                Fee = Number(pending.Tx["Fee"]),
            });
            return;
        }

        // Applied in a ledger whose foreign transactions already took this sequence: no record.
        if (pending.Sequence != _validated.GetValueOrDefault(pending.Account, 1u)) return;

        _validated[pending.Account] = pending.Sequence + 1;

        if (pending.Type != "Batch")
        {
            (string? type, string? data, string? format) = MemoOf(pending.Tx);
            _ledger.Add(new LedgerTransaction(pending.Hash, index, pending.Account, true, true, "tesSUCCESS", type, data, format)
            {
                TransactionType = pending.Type,
                Sequence = pending.Sequence,
                Fee = Number(pending.Tx["Fee"]),
            });
            return;
        }

        string outcome = OuterResult.Remove(pending.Sequence, out string? scripted) ? scripted : "tesSUCCESS";
        _ledger.Add(new LedgerTransaction(pending.Hash, index, pending.Account, outcome == "tesSUCCESS", true, outcome, null, null, null)
        {
            TransactionType = "Batch",
            Sequence = pending.Sequence,
            Fee = Number(pending.Tx["Fee"]),
        });

        if (outcome != "tesSUCCESS") return;

        int position = 0;

        foreach (JsonNode? wrapper in (JsonArray)pending.Tx["RawTransactions"]!)
        {
            JsonObject inner = wrapper!["RawTransaction"]!.AsObject();
            string innerAccount = (string)inner["Account"]!;
            uint innerSequence = (uint)Number(inner["Sequence"]);
            (string? type, string? data, string? format) = MemoOf(inner);
            string marker = type ?? string.Empty;

            if (InnerVanishes.Remove(marker)) continue;
            if (innerSequence != _validated.GetValueOrDefault(innerAccount, 1u)) continue;

            string result = InnerResult.TryGetValue(marker, out string? failure) ? failure : "tesSUCCESS";
            _validated[innerAccount] = innerSequence + 1;

            _ledger.Add(new LedgerTransaction(Sha($"inner:{pending.Hash}:{position++}"), index, innerAccount,
                result == "tesSUCCESS", true, result, type, data, format)
            {
                TransactionType = (string?)inner["TransactionType"],
                Sequence = innerSequence,
                Fee = 0,
                ParentBatchID = pending.Hash,
            });
        }
    }

    /// <summary>Called under the gate.</summary>
    private void WriteForeign(string account, string kind, uint count, long index, int position)
    {
        uint next = _validated.GetValueOrDefault(account, 1u);
        string hash = Sha($"foreign:{account}:{index}:{position}");

        LedgerTransaction record = kind switch
        {
            "AccountSet" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "AccountSet", Sequence = next, Fee = 12,
            },
            "TicketCreate" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "TicketCreate", Sequence = next, TicketCount = count, Fee = 12,
            },
            "TicketedAccountSet" => new LedgerTransaction(hash, index, account, true, true, "tesSUCCESS", null, null, null)
            {
                TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "AccountSet, TicketCreate or TicketedAccountSet"),
        };

        _ledger.Add(record);

        if (kind == "AccountSet") _validated[account] = next + 1;
        if (kind == "TicketCreate") _validated[account] = next + 1 + count;
    }

    /// <summary>Called under the gate.</summary>
    private void PublishTickets(string account)
    {
        Dictionary<uint, string> live = _tickets.GetValueOrDefault(account) ?? [];
        _ledger.SetTicketCount(account, live.Count);
        _ledger.SetLiveTickets(account, [.. live.OrderBy(pair => pair.Key).Select(pair => new LiveTicket(pair.Key, pair.Value))]);
    }

    private static (string? Type, string? Data, string? Format) MemoOf(JsonObject tx) =>
        tx["Memos"] is JsonArray memos && memos.Count > 0 && memos[0]?["Memo"] is JsonObject memo
            ? ((string?)memo["MemoType"], (string?)memo["MemoData"], (string?)memo["MemoFormat"])
            : (null, null, null);

    private static long Number(JsonNode? node) =>
        node is null ? 0 : long.Parse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FakeBatchLedgerSinkTests`
Expected: PASS (7 tests).

- [ ] **Step 6: Run every unit test that uses the fake source and commit**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS (the new `LedgerRead` event has no subscriber outside the new fake).

```bash
git add tests/XrplFile.TestSupport/FakeBatchLedgerSink.cs tests/XrplFile.TestSupport/FakeLedgerSource.cs tests/XrplFile.Core.Tests/FakeBatchLedgerSinkTests.cs
git commit -m "test: a fake node that applies Batch outers at submit and inners at close"
```

---

### Task 6: `BatchStrategy` slice 1 — options, budget, fees, grouping and sequence assignment

Spec 4.1 (record and constructor shapes), B11, B15 (formula), B26 (budget shape), step 2 of "One round".

**Files:**
- Create: `src/XrplFile.Core/BatchStrategyOptions.cs`, `src/XrplFile.Core/BatchBudget.cs`, `src/XrplFile.Core/IBatchFenceRecorder.cs`, `src/XrplFile.Core/BatchFees.cs`, `src/XrplFile.Core/BatchGrouping.cs`
- Test: `tests/XrplFile.Core.Tests/BatchGroupingTests.cs`, `tests/XrplFile.Core.Tests/BatchFeesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record BatchStrategyOptions` with the spec's eleven properties and defaults, plus `TransportRetryStep` (400 ms, the table's step; Task 11 adds `NodeOutageLimit`).
  - `public sealed record BatchBudget(long FeeUnitDrops, long FeeCeilingDrops, long PayerBudgetDrops, long WriterBudgetDrops)`
  - `public interface IBatchFenceRecorder { Task RecordFenceAsync(long deadline, CancellationToken cancellationToken); }`
  - `public static class BatchFees`: `const long MaxOpenLedgerFeeDrops = 200`; `long Unit(OpenLedgerFee fee)`; `long Ceiling(long approvedUnitDrops)`; `long BatchFee(int inners, long unitDrops)`
  - internal: `record PlannedInner(int MemoIndex, uint Sequence)`, `record PlannedBatch(int Ordinal, uint OuterSequence, IReadOnlyList<PlannedInner> Inners)`, `record RoundPlan(IReadOnlyList<PlannedBatch> Batches, int? SoloMemo)`; `static class BatchGrouping` with `const int MaxInners = 8`, `const int MinInners = 2`, `IReadOnlyList<int> Sizes(int memos)`, `RoundPlan Plan(IReadOnlyList<int> memoIndices, uint writerSequence, uint payerSequence)`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchGroupingTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchGroupingTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new int[0])]
    [InlineData(2, new[] { 2 })]
    [InlineData(8, new[] { 8 })]
    [InlineData(9, new[] { 7, 2 })]
    [InlineData(16, new[] { 8, 8 })]
    [InlineData(17, new[] { 8, 7, 2 })]
    [InlineData(20, new[] { 8, 8, 4 })]
    public void Memos_go_eight_to_a_batch_and_a_trailing_one_borrows_a_memo(int memos, int[] expected)
    {
        Assert.Equal(expected, BatchGrouping.Sizes(memos).ToArray());
    }

    [Fact]
    public void No_batch_ever_holds_one_memo_or_more_than_eight()
    {
        for (int memos = 2; memos <= 300; memos++)
        {
            Assert.All(BatchGrouping.Sizes(memos), size => Assert.InRange(size, BatchGrouping.MinInners, BatchGrouping.MaxInners));
            Assert.Equal(memos, BatchGrouping.Sizes(memos).Sum());
        }
    }

    [Fact]
    public void A_round_of_one_memo_is_a_solo_and_no_batch()
    {
        RoundPlan plan = BatchGrouping.Plan([42], writerSequence: 10, payerSequence: 70);

        Assert.Empty(plan.Batches);
        Assert.Equal(42, plan.SoloMemo);
    }

    [Fact]
    public void Outers_take_the_payers_sequences_in_order_and_inners_the_writers_after_every_batch_before_them()
    {
        int[] memos = [.. Enumerable.Range(100, 17)];

        RoundPlan plan = BatchGrouping.Plan(memos, writerSequence: 10, payerSequence: 70);

        Assert.Null(plan.SoloMemo);
        Assert.Equal([70u, 71u, 72u], plan.Batches.Select(batch => batch.OuterSequence).ToArray());
        Assert.Equal([0, 1, 2], plan.Batches.Select(batch => batch.Ordinal).ToArray());
        Assert.Equal(
            Enumerable.Range(10, 17).Select(sequence => (uint)sequence).ToArray(),
            plan.Batches.SelectMany(batch => batch.Inners).Select(inner => inner.Sequence).ToArray());
        Assert.Equal(memos, plan.Batches.SelectMany(batch => batch.Inners).Select(inner => inner.MemoIndex).ToArray());
    }
}
```

`tests/XrplFile.Core.Tests/BatchFeesTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchFeesTests
{
    [Theory]
    [InlineData(10, 10, 15)]
    [InlineData(11, 10, 17)]
    [InlineData(6, 10, 10)]
    [InlineData(200, 10, 300)]
    public void The_unit_is_one_and_a_half_open_ledger_fees_rounded_up_and_never_below_base(long open, long baseFee, long unit)
    {
        Assert.Equal(unit, BatchFees.Unit(new OpenLedgerFee(open, baseFee)));
    }

    [Fact]
    public void The_ceiling_is_twice_the_approved_unit()
    {
        Assert.Equal(30, BatchFees.Ceiling(15));
    }

    [Theory]
    [InlineData(8, 15, 165)]
    [InlineData(2, 15, 75)]
    [InlineData(7, 10, 100)]
    public void A_batch_pays_its_inners_plus_three_units(int inners, long unit, long fee)
    {
        Assert.Equal(fee, BatchFees.BatchFee(inners, unit));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchGroupingTests -class XrplFile.Tests.BatchFeesTests`
Expected: build FAILS.

- [ ] **Step 3: Write the types**

`src/XrplFile.Core/BatchStrategyOptions.cs`:

```csharp
namespace XrplFile;

/// <summary>The Batch strategy's knobs, with the spec's values as defaults (spec 4.1).</summary>
public sealed record BatchStrategyOptions
{
    /// <summary>Transmissions in flight at the start (spec B10).</summary>
    public int InitialWindow { get; init; } = 16;

    /// <summary>The most batches sent past the payer's sequence in the latest checked snapshot (spec B10).</summary>
    public int MaxAhead { get; init; } = 256;

    /// <summary><c>LastLedgerSequence</c> = the snapshot's index plus this (spec B7).</summary>
    public int DeadlineLedgers { get; init; } = 20;

    /// <summary>Ledgers waited after every candidate is resolved, before the sweep (spec B9).</summary>
    public int SettleLedgers { get; init; } = 2;

    /// <summary>How often a sending round checks the chain, and how often settlement polls (spec B8).</summary>
    public TimeSpan ChainCheck { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a round reads the writer's history for progress (spec B20).</summary>
    public TimeSpan ProgressSweep { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The oldest a snapshot may be when a deadline is taken from it (spec B7).</summary>
    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the final sweep waits for history to cover the round (spec B30).</summary>
    public TimeSpan HistoryLagLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a round waits for fees above the ceiling to fall (spec B15).</summary>
    public TimeSpan FeeWait { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Candidates one memo may be put into in one session (spec B13).</summary>
    public int MaxAttemptsPerMemo { get; init; } = 3;

    /// <summary>The step between transport retries of one blob: step × (retry + 1) (spec submit-answer table). Tests shorten it.</summary>
    public TimeSpan TransportRetryStep { get; init; } = TimeSpan.FromMilliseconds(400);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
```

`src/XrplFile.Core/BatchBudget.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What an approved estimate lets a Batch run spend (spec B15, B26): the unit it was priced at, the
/// ceiling no round's unit may pass, and each account's worst case, which the strategy refuses to
/// sign past.
/// </summary>
public sealed record BatchBudget(long FeeUnitDrops, long FeeCeilingDrops, long PayerBudgetDrops, long WriterBudgetDrops);
```

`src/XrplFile.Core/IBatchFenceRecorder.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// Persists the fence (spec B23): the highest deadline any transmission of the run has registered.
/// Returns once it is saved; a save that fails throws, and the transmission it was for is never sent.
/// </summary>
public interface IBatchFenceRecorder
{
    Task RecordFenceAsync(long deadline, CancellationToken cancellationToken);
}
```

`src/XrplFile.Core/BatchFees.cs`:

```csharp
namespace XrplFile;

/// <summary>What a batch costs and how far a round's price may move (spec B15, 4.5).</summary>
public static class BatchFees
{
    /// <summary>Above this open-ledger fee, in drops, an estimate refuses (spec B15).</summary>
    public const long MaxOpenLedgerFeeDrops = 200;

    /// <summary>ceil(<c>open_ledger_fee</c> × 1.5), never below the base fee.</summary>
    public static long Unit(OpenLedgerFee fee)
    {
        ArgumentNullException.ThrowIfNull(fee);

        return Math.Max(fee.BaseFeeDrops, (fee.OpenLedgerFeeDrops * 3 + 1) / 2);
    }

    /// <summary>The most a round may pay per unit: twice the unit the estimate approved.</summary>
    public static long Ceiling(long approvedUnitDrops) => approvedUnitDrops * 2;

    /// <summary>Two units for the batch, one per inner and one for its one BatchSigner.</summary>
    public static long BatchFee(int inners, long unitDrops) => (inners + 3L) * unitDrops;
}
```

`src/XrplFile.Core/BatchGrouping.cs`:

```csharp
namespace XrplFile;

/// <summary>One memo inside a planned batch, and the writer's sequence it rides.</summary>
internal sealed record PlannedInner(int MemoIndex, uint Sequence);

/// <summary>A batch of a round: its place in the round, the payer's sequence it takes, and its inners.</summary>
internal sealed record PlannedBatch(int Ordinal, uint OuterSequence, IReadOnlyList<PlannedInner> Inners);

/// <summary>What one round sends: its batches, or the one memo it sends on its own (spec B11).</summary>
internal sealed record RoundPlan(IReadOnlyList<PlannedBatch> Batches, int? SoloMemo);

internal static class BatchGrouping
{
    public const int MaxInners = 8;

    public const int MinInners = 2;

    /// <summary>
    /// Batch sizes for this many memos: eight to a batch, and a trailing batch of one borrows a memo
    /// from the batch before it, so 8 + 1 becomes 7 + 2. Fewer than two memos make no batch.
    /// </summary>
    public static IReadOnlyList<int> Sizes(int memos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memos);

        if (memos < MinInners) return [];

        List<int> sizes = [];

        for (int left = memos; left > 0; left -= MaxInners) sizes.Add(Math.Min(MaxInners, left));

        if (sizes[^1] == 1)
        {
            sizes[^2] -= 1;
            sizes[^1] = 2;
        }

        return sizes;
    }

    /// <summary>
    /// Assigns sequences from one snapshot (spec 4.1 step 2): batch k takes the payer's sequence plus
    /// k, and its inners take the writer's sequences in order, after the inners of every batch before
    /// it. A round of exactly one memo is a solo and no batch.
    /// </summary>
    public static RoundPlan Plan(IReadOnlyList<int> memoIndices, uint writerSequence, uint payerSequence)
    {
        ArgumentNullException.ThrowIfNull(memoIndices);

        if (memoIndices.Count == 1) return new RoundPlan([], memoIndices[0]);

        IReadOnlyList<int> sizes = Sizes(memoIndices.Count);
        List<PlannedBatch> batches = new(sizes.Count);
        int position = 0;
        uint inner = writerSequence;

        for (int k = 0; k < sizes.Count; k++)
        {
            List<PlannedInner> inners = new(sizes[k]);
            for (int i = 0; i < sizes[k]; i++) inners.Add(new PlannedInner(memoIndices[position++], inner++));

            batches.Add(new PlannedBatch(k, payerSequence + (uint)k, inners));
        }

        return new RoundPlan(batches, null);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchGroupingTests -class XrplFile.Tests.BatchFeesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/BatchStrategyOptions.cs src/XrplFile.Core/BatchBudget.cs src/XrplFile.Core/IBatchFenceRecorder.cs src/XrplFile.Core/BatchFees.cs src/XrplFile.Core/BatchGrouping.cs tests/XrplFile.Core.Tests/BatchGroupingTests.cs tests/XrplFile.Core.Tests/BatchFeesTests.cs
git commit -m "feat(core): Batch fees, grouping and sequence assignment"
```

---
### Task 7: `BatchStrategy` slice 2 — the window, the fence and registration

Spec B10 (window), B7 and B23 (deadline persisted before first transmission), B13 (a candidate is reserved when registered), 4.1 step 3–4 (registration and halt under one lock).

**Files:**
- Create: `src/XrplFile.Core/BatchWindow.cs`, `src/XrplFile.Core/BatchFence.cs`, `src/XrplFile.Core/BatchCandidate.cs`, `src/XrplFile.Core/BatchRegistry.cs`, `src/XrplFile.Core/BatchTrace.cs`
- Test: `tests/XrplFile.Core.Tests/BatchWindowTests.cs`, `tests/XrplFile.Core.Tests/BatchRegistryTests.cs`, `tests/XrplFile.Core.Tests/RecordingFence.cs`

**Interfaces:**
- Consumes: `IBatchFenceRecorder`, `LedgerSnapshot`, `SignedTransaction`.
- Produces (all internal):
  - `BatchWindow(int initial)`: `int Size`, `int InFlight`, `Task AcquireAsync(CancellationToken)`, `void Release(bool busy)`, `void Return()` (a slot no transmission used: neither busy nor clean)
  - `BatchFence(IBatchFenceRecorder recorder)`: `long Recorded`, `Task CoverAsync(long deadline, CancellationToken)`
  - `BatchCandidate(int ordinal, bool isOuter, string account, uint sequence, uint lastLedger, long feeDrops, IReadOnlyList<int> memos, SignedTransaction signed)`: `const int SoloOrdinal = -1`, properties of the same names (`Ordinal`, `IsOuter`, `Account`, `Sequence`, `LastLedger`, `FeeDrops`, `Memos`, `Signed`), `string Hash`, `bool IsResolved`, `void MarkResolved()`, `bool IsResolvedBy(LedgerSnapshot snapshot)`
  - `BatchRegistry(BatchFence fence, IDictionary<int, int> candidatesPerMemo, BatchTrace trace)`: `bool IsHalted`, `string? HaltReason`, `IReadOnlyList<BatchCandidate> Candidates`, `IReadOnlyList<BatchCandidate> Outers` (by ordinal), `Task<bool> TryRegisterAsync(BatchCandidate, CancellationToken)`, `Task HaltAsync(string reason, CancellationToken)`
  - `BatchTrace`: `void Add(string entry)`, `IReadOnlyList<string> Events`, `Task<T> TimeSigningAsync<T>(Func<Task<T>> signing)`, `TimeSpan SigningTime`. Trace entries used by tests: `round:<ledger>`, `register:<ordinal>`, `discard:<ordinal>`, `halt:<reason>`, `transmit:<ordinal>:<answer>`, `settled:<ledger>`.
  - Test helper `RecordingFence : IBatchFenceRecorder` with `List<long> Recorded`, `bool Fail`, `Action<long>? OnRecord`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/RecordingFence.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>A fence recorder that remembers every fence it was asked to persist, and can fail as a full disk does.</summary>
internal sealed class RecordingFence : IBatchFenceRecorder
{
    private readonly Lock _gate = new();
    private readonly List<long> _recorded = [];

    public bool Fail { get; set; }

    public Action<long>? OnRecord { get; set; }

    public IReadOnlyList<long> Recorded
    {
        get { lock (_gate) return [.. _recorded]; }
    }

    public long Highest
    {
        get { lock (_gate) return _recorded.Count == 0 ? 0 : _recorded.Max(); }
    }

    public Task RecordFenceAsync(long deadline, CancellationToken cancellationToken)
    {
        if (Fail) throw new IOException("fake: the state file could not be written");

        lock (_gate) _recorded.Add(deadline);
        OnRecord?.Invoke(deadline);
        return Task.CompletedTask;
    }
}
```

`tests/XrplFile.Core.Tests/BatchWindowTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchWindowTests
{
    [Fact]
    public async Task The_window_admits_its_size_and_holds_the_next_until_a_release()
    {
        BatchWindow window = new(2);
        await window.AcquireAsync(TestContext.Current.CancellationToken);
        await window.AcquireAsync(TestContext.Current.CancellationToken);

        Task third = window.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);

        window.Release(busy: false);
        await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, window.InFlight);
    }

    [Fact]
    public async Task Three_busy_answers_in_a_row_halve_it()
    {
        BatchWindow window = new(16);

        for (int i = 0; i < 3; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: true);
        }

        Assert.Equal(8, window.Size);
    }

    [Fact]
    public async Task A_clean_answer_between_busy_ones_starts_the_count_again()
    {
        BatchWindow window = new(16);

        foreach (bool busy in new[] { true, true, false, true, true })
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy);
        }

        Assert.Equal(16, window.Size);
    }

    [Fact]
    public async Task Ten_clean_sends_widen_it_by_one_and_it_never_goes_below_one()
    {
        BatchWindow window = new(1);

        for (int i = 0; i < 6; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: true);
        }

        Assert.Equal(1, window.Size);

        for (int i = 0; i < 10; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Release(busy: false);
        }

        Assert.Equal(2, window.Size);
    }

    [Fact]
    public async Task A_returned_slot_admits_the_next_and_counts_as_no_answer()
    {
        BatchWindow window = new(1);

        for (int i = 0; i < 12; i++)
        {
            await window.AcquireAsync(TestContext.Current.CancellationToken);
            window.Return();
        }

        Assert.Equal(1, window.Size);
        Assert.Equal(0, window.InFlight);
    }

    [Fact]
    public async Task A_cancelled_wait_does_not_take_a_slot()
    {
        BatchWindow window = new(1);
        await window.AcquireAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource cancel = new();
        Task waiting = window.AcquireAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        window.Release(busy: false);
        Assert.Equal(0, window.InFlight);
    }
}
```

`tests/XrplFile.Core.Tests/BatchRegistryTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchRegistryTests
{
    private static BatchCandidate Candidate(int ordinal, uint lastLedger, params int[] memos) =>
        new(ordinal, isOuter: true, "rPayer", (uint)(100 + ordinal), lastLedger, 165, memos, new SignedTransaction("00", $"HASH{ordinal}"));

    [Fact]
    public async Task A_candidate_is_registered_only_after_its_deadline_is_persisted()
    {
        RecordingFence recorder = new();
        BatchRegistry registry = new(new BatchFence(recorder), new Dictionary<int, int>(), new BatchTrace());

        Assert.True(await registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken));
        Assert.True(await registry.TryRegisterAsync(Candidate(1, 520, 2, 3), TestContext.Current.CancellationToken));
        Assert.True(await registry.TryRegisterAsync(Candidate(2, 521, 4, 5), TestContext.Current.CancellationToken));

        // Persisted when it rises, not once per candidate.
        Assert.Equal([520L, 521L], recorder.Recorded);
        Assert.Equal(3, registry.Candidates.Count);
    }

    [Fact]
    public async Task Registering_reserves_a_candidate_for_every_memo_it_carries()
    {
        Dictionary<int, int> candidates = new() { [1] = 2 };
        BatchRegistry registry = new(new BatchFence(new RecordingFence()), candidates, new BatchTrace());

        await registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, candidates[0]);
        Assert.Equal(3, candidates[1]);
    }

    [Fact]
    public async Task After_a_halt_nothing_registers_and_nothing_is_reserved()
    {
        Dictionary<int, int> candidates = [];
        BatchTrace trace = new();
        BatchRegistry registry = new(new BatchFence(new RecordingFence()), candidates, trace);

        await registry.HaltAsync("diverged", TestContext.Current.CancellationToken);

        Assert.False(await registry.TryRegisterAsync(Candidate(4, 520, 0, 1), TestContext.Current.CancellationToken));
        Assert.Empty(candidates);
        Assert.Empty(registry.Candidates);
        Assert.Equal(["halt:diverged", "discard:4"], trace.Events);
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_registers_nothing()
    {
        Dictionary<int, int> candidates = [];
        BatchRegistry registry = new(new BatchFence(new RecordingFence { Fail = true }), candidates, new BatchTrace());

        await Assert.ThrowsAsync<IOException>(() => registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken));

        Assert.Empty(candidates);
        Assert.Empty(registry.Candidates);
    }

    [Fact]
    public void A_candidate_is_resolved_by_a_ledger_past_its_deadline_or_by_its_sequence_being_used()
    {
        BatchCandidate candidate = Candidate(0, 520, 0, 1);

        Assert.False(candidate.IsResolvedBy(new LedgerSnapshot(520, new Dictionary<string, uint> { ["rPayer"] = 100 })));
        Assert.True(candidate.IsResolvedBy(new LedgerSnapshot(521, new Dictionary<string, uint> { ["rPayer"] = 100 })));
        Assert.True(candidate.IsResolvedBy(new LedgerSnapshot(510, new Dictionary<string, uint> { ["rPayer"] = 101 })));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchWindowTests -class XrplFile.Tests.BatchRegistryTests`
Expected: build FAILS.

- [ ] **Step 3: Write the types**

`src/XrplFile.Core/BatchWindow.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// How many transmissions may be in flight on the connection (spec B10): it starts at the given size,
/// halves after three busy answers in a row and grows by one after ten clean ones. A public node
/// closes a client at about a hundred unsent answers; this is what keeps the client well below that.
/// </summary>
internal sealed class BatchWindow(int initial)
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private int _size = Math.Max(1, initial);
    private int _inFlight;
    private int _busyInRow;
    private int _cleanInRow;

    public int Size
    {
        get { lock (_gate) return _size; }
    }

    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    public Task AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight < _size && _waiting.Count == 0)
            {
                _inFlight++;
                return Task.CompletedTask;
            }

            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _waiting.Enqueue(waiter);

            return waiter.Task;
        }
    }

    public void Release(bool busy)
    {
        lock (_gate)
        {
            _inFlight--;

            if (busy)
            {
                _cleanInRow = 0;

                if (++_busyInRow >= 3)
                {
                    _size = Math.Max(1, _size / 2);
                    _busyInRow = 0;
                }
            }
            else
            {
                _busyInRow = 0;

                if (++_cleanInRow >= 10)
                {
                    _size++;
                    _cleanInRow = 0;
                }
            }

            WakeUnderLock();
        }
    }

    /// <summary>Gives back a slot that no transmission used (a batch discarded unsent): neither busy nor clean.</summary>
    public void Return()
    {
        lock (_gate)
        {
            _inFlight--;
            WakeUnderLock();
        }
    }

    /// <summary>Called under the gate. A cancelled waiter refuses the slot (TrySetResult fails) and is skipped.</summary>
    private void WakeUnderLock()
    {
        while (_inFlight < _size && _waiting.TryDequeue(out TaskCompletionSource? next))
        {
            if (next.TrySetResult()) _inFlight++;
        }
    }
}
```

`src/XrplFile.Core/BatchFence.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// The strategy's side of the fence (spec B23): asks the recorder to persist a deadline only when it
/// rises past the one already saved. Called under a round's registration lock, so never concurrently.
/// </summary>
internal sealed class BatchFence(IBatchFenceRecorder recorder)
{
    private long _recorded;

    public long Recorded => Interlocked.Read(ref _recorded);

    public async Task CoverAsync(long deadline, CancellationToken cancellationToken)
    {
        if (deadline <= Recorded) return;

        await recorder.RecordFenceAsync(deadline, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _recorded, deadline);
    }
}
```

`src/XrplFile.Core/BatchCandidate.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// A signed transaction that carries memos (spec B13): an outer batch, or the writer's solo. However
/// often its bytes are sent, it is one candidate, and it can charge one fee.
/// </summary>
internal sealed class BatchCandidate(
    int ordinal, bool isOuter, string account, uint sequence, uint lastLedger, long feeDrops,
    IReadOnlyList<int> memos, SignedTransaction signed)
{
    /// <summary>The ordinal of a round's solo memo; batches count from zero.</summary>
    public const int SoloOrdinal = -1;

    private volatile bool _resolved;

    public int Ordinal { get; } = ordinal;

    public bool IsOuter { get; } = isOuter;

    /// <summary>Whose sequence it rides: the payer for an outer, the writer for a solo.</summary>
    public string Account { get; } = account;

    public uint Sequence { get; } = sequence;

    public uint LastLedger { get; } = lastLedger;

    public long FeeDrops { get; } = feeDrops;

    /// <summary>The call's memo indices it carries.</summary>
    public IReadOnlyList<int> Memos { get; } = memos;

    public SignedTransaction Signed { get; } = signed;

    public string Hash => Signed.Hash;

    /// <summary>Set by settlement: nothing this candidate is can apply any more, so it is not sent again.</summary>
    public bool IsResolved => _resolved;

    public void MarkResolved() => _resolved = true;

    /// <summary>
    /// Resolved once the snapshot's ledger is past its deadline, or its account's sequence is past its
    /// own (spec B9). Either way it can no longer apply; whether its memos landed is the sweep's call.
    /// </summary>
    public bool IsResolvedBy(LedgerSnapshot snapshot) =>
        snapshot.LedgerIndex > LastLedger || snapshot.SequenceOf(Account) > Sequence;
}
```

`src/XrplFile.Core/BatchTrace.cs`:

```csharp
using System.Diagnostics;

namespace XrplFile;

/// <summary>What a Batch run did, in order, and how long it spent signing. Read by tests and by the 1 MB stand measurement.</summary>
internal sealed class BatchTrace
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];
    private long _signingTicks;

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    public TimeSpan SigningTime => TimeSpan.FromTicks(Interlocked.Read(ref _signingTicks));

    public void Add(string entry)
    {
        lock (_gate) _events.Add(entry);
    }

    public async Task<T> TimeSigningAsync<T>(Func<Task<T>> signing)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            return await signing().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(ref _signingTicks, Stopwatch.GetElapsedTime(started).Ticks);
        }
    }
}
```

`src/XrplFile.Core/BatchRegistry.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// A round's candidates, registered under the round's lock (spec 4.1 steps 3–4). Registration
/// persists the deadline first and reserves the candidate for every memo it carries; a halt is set
/// under the same lock, so once it is set nothing more registers and a signed blob is discarded unsent.
/// </summary>
internal sealed class BatchRegistry(BatchFence fence, IDictionary<int, int> candidatesPerMemo, BatchTrace trace)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _listGate = new();
    private readonly List<BatchCandidate> _candidates = [];
    private volatile string? _halt;

    public bool IsHalted => _halt is not null;

    public string? HaltReason => _halt;

    public IReadOnlyList<BatchCandidate> Candidates
    {
        get { lock (_listGate) return [.. _candidates]; }
    }

    /// <summary>The registered outers, by ordinal. A single sender registers them in order.</summary>
    public IReadOnlyList<BatchCandidate> Outers
    {
        get { lock (_listGate) return [.. _candidates.Where(candidate => candidate.IsOuter).OrderBy(candidate => candidate.Ordinal)]; }
    }

    public async Task<bool> TryRegisterAsync(BatchCandidate candidate, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_halt is not null)
            {
                trace.Add($"discard:{candidate.Ordinal}");
                return false;
            }

            await fence.CoverAsync(candidate.LastLedger, cancellationToken).ConfigureAwait(false);

            foreach (int memo in candidate.Memos)
            {
                candidatesPerMemo[memo] = candidatesPerMemo.TryGetValue(memo, out int count) ? count + 1 : 1;
            }

            lock (_listGate) _candidates.Add(candidate);
            trace.Add($"register:{candidate.Ordinal}");
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task HaltAsync(string reason, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_halt is not null) return;

            _halt = reason;
            trace.Add($"halt:{reason}");
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchWindowTests -class XrplFile.Tests.BatchRegistryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/BatchWindow.cs src/XrplFile.Core/BatchFence.cs src/XrplFile.Core/BatchCandidate.cs src/XrplFile.Core/BatchRegistry.cs src/XrplFile.Core/BatchTrace.cs tests/XrplFile.Core.Tests/BatchWindowTests.cs tests/XrplFile.Core.Tests/BatchRegistryTests.cs tests/XrplFile.Core.Tests/RecordingFence.cs
git commit -m "feat(core): Batch send window and registration behind a persisted fence"
```

---

### Task 8: `BatchStrategy` slice 3 — rounds, chain check, settlement and the solo memo

Spec B5–B11, 4.1 "One round" steps 1–7, the submit-answer table, B28. The final sweep here finds the memos that landed; Task 9 adds completeness and identity.

**Files:**
- Create: `src/XrplFile.Core/BatchContext.cs`, `src/XrplFile.Core/BatchChain.cs`, `src/XrplFile.Core/BatchRound.cs`, `src/XrplFile.Core/BatchSweep.cs`, `src/XrplFile.Core/BatchStrategy.cs`
- Modify: `src/XrplFile.Core/ISubmissionStrategy.cs` (`SubmittedMemo.LedgerIndex`, Spec question 23)
- Test: `tests/XrplFile.Core.Tests/BatchChainTests.cs`, `tests/XrplFile.Core.Tests/BatchStrategyTests.cs`, `tests/XrplFile.Core.Tests/BatchTestNode.cs`, `tests/XrplFile.Core.Tests/GatedSigner.cs`, `tests/XrplFile.Core.Tests/GatedBlobSink.cs`

**Interfaces:**
- Consumes: everything from Tasks 1, 2, 3, 5, 6, 7.
- Produces:
  - `public sealed class BatchStrategy : ISubmissionStrategy` — ctor `(ILedgerSink sink, ILedgerSource source, ITransactionSigner writer, ITransactionSigner payer, BatchBudget budget, IBatchFenceRecorder fence, BatchStrategyOptions? options = null)`; `string Account` (writer), `string Payer`, `long FeeDropsPerTransaction` (= `budget.FeeUnitDrops`), `int TicketMints` (0), `long? PayerFeeDrops`, `TimeSpan SigningTime`; `internal IReadOnlyList<string> Trace`.
  - internal `record BatchContext(ILedgerSink Sink, ILedgerSource Source, ITransactionSigner Writer, ITransactionSigner Payer, BatchBudget Budget, BatchStrategyOptions Options, BatchFence Fence, BatchWindow Window, BatchTrace Trace)`
  - `SubmittedMemo` gains `long? LedgerIndex { get; init; }`: the ledger the memo landed in, when the strategy knows it (the writer uses it in Task 15)
  - internal `record LandedInner(string Hash, long LedgerIndex)`; internal `record RoundResult(IReadOnlyDictionary<int, LandedInner> Landed, long PayerFeeDrops, bool PayerFeeKnown)` with `static RoundResult Empty`
  - internal `static class BatchChain` with `const string Diverged = "diverged"`, `const string Stalled = "stalled"`, `string? Verdict(RoundPlan plan, LedgerSnapshot start, IReadOnlyList<BatchCandidate> outers, LedgerSnapshot snapshot, string writer, string payer)`
  - internal `sealed class BatchRound(BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IDictionary<int, int> candidatesPerMemo)` with `Task<RoundResult> RunAsync(CancellationToken)`
  - internal `static class BatchSweep` with `Task<RoundResult> FinalAsync(BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyList<BatchCandidate> candidates, LedgerSnapshot start, LedgerSnapshot settled, CancellationToken)`

- [ ] **Step 1: Write the chain-check tests**

`tests/XrplFile.Core.Tests/BatchChainTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchChainTests
{
    private static readonly RoundPlan Plan = BatchGrouping.Plan([.. Enumerable.Range(0, 20)], writerSequence: 100, payerSequence: 500);

    private static readonly LedgerSnapshot Start = Snapshot(1000, writer: 100, payer: 500);

    private static LedgerSnapshot Snapshot(long ledger, uint writer, uint payer) =>
        new(ledger, new Dictionary<string, uint> { ["rW"] = writer, ["rP"] = payer });

    private static IReadOnlyList<BatchCandidate> Registered(int count) =>
        [.. Enumerable.Range(0, count).Select(k => new BatchCandidate(
            k, true, "rP", (uint)(500 + k), 1020, 165, [.. Plan.Batches[k].Inners.Select(inner => inner.MemoIndex)],
            new SignedTransaction("00", $"H{k}")))];

    [Fact]
    public void A_writer_that_kept_up_with_the_outers_the_payer_consumed_is_fine()
    {
        Assert.Null(BatchChain.Verdict(Plan, Start, Registered(3), Snapshot(1003, writer: 116, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_writer_behind_what_the_consumed_outers_imply_has_diverged()
    {
        Assert.Equal(BatchChain.Diverged, BatchChain.Verdict(Plan, Start, Registered(3), Snapshot(1003, writer: 108, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_payer_past_everything_registered_has_diverged()
    {
        Assert.Equal(BatchChain.Diverged, BatchChain.Verdict(Plan, Start, Registered(1), Snapshot(1003, writer: 108, payer: 502), "rW", "rP"));
    }

    [Fact]
    public void A_signed_outer_past_its_deadline_and_not_consumed_has_stalled_the_round()
    {
        Assert.Equal(BatchChain.Stalled, BatchChain.Verdict(Plan, Start, Registered(2), Snapshot(1021, writer: 108, payer: 501), "rW", "rP"));
    }

    [Fact]
    public void An_unsigned_next_batch_never_stalls_the_round()
    {
        Assert.Null(BatchChain.Verdict(Plan, Start, Registered(1), Snapshot(1021, writer: 108, payer: 501), "rW", "rP"));
    }
}
```

- [ ] **Step 2: Write the strategy tests and their helpers**

`tests/XrplFile.Core.Tests/BatchTestNode.cs`:

```csharp
using Xrpl.Wallet;

namespace XrplFile.Tests;

/// <summary>A writer, a payer, a fake ledger and a fast strategy over them, shared by the Batch strategy tests.</summary>
internal sealed class BatchTestNode
{
    public const uint WriterStart = 100;
    public const uint PayerStart = 500;

    public BatchTestNode(Action<FakeBatchLedgerSink>? script = null)
    {
        Ledger = new FakeLedgerSource();
        Sink = new FakeBatchLedgerSink(Ledger);
        Sink.SetSequence(Writer.Account, WriterStart);
        Sink.SetSequence(Payer.Account, PayerStart);
        script?.Invoke(Sink);
    }

    public ITransactionSigner Writer { get; } = new WalletSigner(XrplWallet.Generate());

    public ITransactionSigner Payer { get; } = new WalletSigner(XrplWallet.Generate());

    public FakeLedgerSource Ledger { get; }

    public FakeBatchLedgerSink Sink { get; }

    public RecordingFence Fence { get; } = new();

    public static BatchStrategyOptions FastOptions { get; } = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(5),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds: a 400 ms
        // retry step would meet a resolved candidate instead of its resend.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
    };

    public static BatchBudget Budget { get; } = new(15, 30, 10_000_000, 10_000_000);

    public BatchStrategy Strategy(
        BatchStrategyOptions? options = null, BatchBudget? budget = null, ITransactionSigner? writer = null,
        ITransactionSigner? payer = null, ILedgerSink? sink = null) =>
        new(sink ?? Sink, Ledger, writer ?? Writer, payer ?? Payer, budget ?? Budget, Fence, options ?? FastOptions);

    public static IReadOnlyList<MemoSubmission> Memos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i, 7]), null))];

    /// <summary>The writer's successful records carrying each memo marker, in ledger order.</summary>
    public async Task<IReadOnlyDictionary<string, List<LedgerTransaction>>> LandedByMarkerAsync()
    {
        Dictionary<string, List<LedgerTransaction>> byMarker = new(StringComparer.OrdinalIgnoreCase);

        await foreach (LedgerTransaction tx in Ledger.AccountTransactionsAsync(Writer.Account, 0, Ledger.CurrentLedger, true, CancellationToken.None))
        {
            if (!tx.Succeeded || tx.MemoTypeHex is null) continue;
            if (!byMarker.TryGetValue(tx.MemoTypeHex, out List<LedgerTransaction>? list)) byMarker[tx.MemoTypeHex] = list = [];
            list.Add(tx);
        }

        return byMarker;
    }

    public static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("the condition did not become true in 20 s");
            await Task.Delay(5);
        }
    }
}
```

`tests/XrplFile.Core.Tests/GatedSigner.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>Holds its <paramref name="gateAt"/>-th signature, signed and not yet returned, until released.</summary>
internal sealed class GatedSigner(ITransactionSigner inner, int gateAt) : ITransactionSigner
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Reached => _reached.Task;

    /// <summary>The hash of the signature held at the gate.</summary>
    public string? GatedHash { get; private set; }

    public string Account => inner.Account;

    public string PublicKey => inner.PublicKey;

    public void Release() => _release.TrySetResult();

    public async Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await inner.SignAsync(transaction, cancellationToken);

        if (Interlocked.Increment(ref _count) == gateAt)
        {
            GatedHash = signed.Hash;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return signed;
    }
}
```

`tests/XrplFile.Core.Tests/GatedBlobSink.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>Holds its <paramref name="gateAt"/>-th blob submission in flight until released: registered, sent, not yet seen by the node.</summary>
internal sealed class GatedBlobSink(ILedgerSink inner, int gateAt) : ILedgerSink
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;

    public Task Reached => _reached.Task;

    /// <summary>The blob held at the gate.</summary>
    public string? Gated { get; private set; }

    public void Release() => _release.TrySetResult();

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
        inner.SequenceAsync(account, validated, cancellationToken);

    public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
        inner.SubmitAsync(transaction, signer, cancellationToken);

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _count) == gateAt)
        {
            Gated = txBlob;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await inner.SubmitBlobAsync(txBlob, cancellationToken);
    }
}
```

`tests/XrplFile.Core.Tests/BatchStrategyTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class BatchStrategyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_memo_lands_and_reports_the_inner_that_carried_it()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(20), null, Token);

        Assert.Equal(Enumerable.Range(0, 20).ToArray(), landed.Select(memo => memo.Index).ToArray());
        foreach (SubmittedMemo memo in landed)
        {
            LedgerTransaction tx = (await node.Ledger.TransactionAsync(memo.TxHash, Token))!;
            Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx.MemoTypeHex);
            Assert.Equal(node.Writer.Account, tx.Account);
            Assert.NotNull(tx.ParentBatchID);
        }

        Assert.All(node.Sink.Transmissions, transmission => Assert.Equal(node.Payer.Account, transmission.Account));
    }

    [Fact]
    public async Task Nine_memos_go_as_seven_and_two()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(9), null, Token);

        Assert.Equal([7, 2], node.Sink.Transmissions.Where(t => t.TransactionType == "Batch").Select(t => t.Inners).ToArray());
    }

    [Fact]
    public async Task Each_outer_pays_its_inners_plus_three_units_at_the_rounds_unit()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(12, 10);
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(18, 36, 10_000_000, 10_000_000));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(10), null, Token);

        List<LedgerTransaction> outers = [];
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            outers.Add(tx);
        }

        Assert.Equal([(8 + 3) * 18L, (2 + 3) * 18L], outers.Select(outer => outer.Fee!.Value).ToArray());
    }

    [Fact]
    public async Task Every_transmission_carries_a_deadline_twenty_ledgers_past_a_fresh_snapshot_persisted_before_it_is_sent()
    {
        BatchTestNode node = new();
        List<string> order = [];
        node.Fence.OnRecord = deadline => { lock (order) order.Add($"fence:{deadline}"); };
        node.Sink.Transmitting = transmission => { lock (order) order.Add($"send:{transmission.LastLedger}"); };
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);

        foreach (FakeBatchLedgerSink.Transmission transmission in node.Sink.Transmissions)
        {
            Assert.InRange(transmission.LastLedger - transmission.OpenLedgerIndex, 0L, (long)new BatchStrategyOptions().DeadlineLedgers);
            int sent = order.IndexOf($"send:{transmission.LastLedger}");
            int fenced = order.FindIndex(entry => entry.StartsWith("fence:", StringComparison.Ordinal)
                && long.Parse(entry["fence:".Length..]) >= transmission.LastLedger);
            Assert.InRange(fenced, 0, sent);
        }
    }

    [Fact]
    public async Task A_round_that_landed_everything_settles_long_before_its_deadlines()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        long settled = long.Parse(strategy.Trace.First(entry => entry.StartsWith("settled:", StringComparison.Ordinal))["settled:".Length..]);
        Assert.True(settled < node.Sink.Transmissions.Min(transmission => transmission.LastLedger));
    }

    [Fact]
    public async Task An_outer_that_never_lands_stalls_the_round_and_its_memos_go_in_the_next()
    {
        BatchTestNode node = new(sink => sink.LoseOuter.Add(BatchTestNode.PayerStart));
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:stalled", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_tec_outer_is_seen_as_divergence_and_its_memos_are_sent_again()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:diverged", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_tec_inner_costs_its_chunk_only_and_the_next_round_resends_it()
    {
        // InnerResult applies to inners only: the second round sends the one missing memo as a solo.
        BatchTestNode node = new(sink => sink.InnerResult[MemoLayout.ChunkMarkerHex(3)] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        Assert.Equal(16, landed.Count);
        Assert.DoesNotContain(strategy.Trace, entry => entry.StartsWith("halt:", StringComparison.Ordinal));
        Assert.Equal(2, strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)));
        Assert.Equal("AccountSet", node.Sink.Transmissions[^1].TransactionType);
    }

    [Fact]
    public async Task An_inner_that_does_not_consume_its_sequence_halts_the_round_and_nothing_is_lost()
    {
        BatchTestNode node = new(sink => sink.InnerVanishes.Add(MemoLayout.ChunkMarkerHex(2)));
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:diverged", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_later_outer_that_overtakes_an_earlier_one_is_held_and_still_lands()
    {
        BatchTestNode node = new(sink => sink.DelayOuter[BatchTestNode.PayerStart] = TimeSpan.FromMilliseconds(80));
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains(node.Sink.Transmissions, transmission => transmission.Answer == "terPRE_SEQ");
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_answers_resend_the_same_blob_and_need_no_new_round()
    {
        BatchTestNode node = new(sink =>
        {
            sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telCAN_NOT_QUEUE";
            sink.AnswerAt[(BatchTestNode.PayerStart, 2)] = "error:tooBusy";
            sink.AnswerAt[(BatchTestNode.PayerStart, 3)] = "error:slowDown";
        });
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token);

        Assert.Equal(8, landed.Count);
        Assert.Single(node.Sink.Transmissions.Select(transmission => transmission.Hash).Distinct());
        Assert.Equal(4, node.Sink.Transmissions.Count);
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fee_refusal_is_not_resent_and_the_next_round_reads_the_unit_again()
    {
        BatchTestNode node = new(sink => sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telINSUF_FEE_P");
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token);

        Assert.Equal(8, landed.Count);
        Assert.Single(node.Sink.Transmissions, transmission => transmission.Answer == "telINSUF_FEE_P");
        Assert.Equal(2, strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_malformed_answer_fails_the_run_quoting_it()
    {
        BatchTestNode node = new(sink => sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "temBAD_SIGNATURE");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Equal("temBAD_SIGNATURE", error.EngineResult);
    }

    [Fact]
    public async Task A_round_of_one_memo_sends_a_plain_AccountSet_of_the_writer_and_no_batch()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        SubmittedMemo landed = Assert.Single(await strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        FakeBatchLedgerSink.Transmission solo = Assert.Single(node.Sink.Transmissions);
        Assert.Equal("AccountSet", solo.TransactionType);
        Assert.Equal(node.Writer.Account, solo.Account);
        Assert.Null((await node.Ledger.TransactionAsync(landed.TxHash, Token))!.ParentBatchID);
    }

    [Fact]
    public async Task The_window_keeps_no_more_than_sixteen_transmissions_in_flight()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(10);
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        Assert.InRange(node.Sink.PeakInFlight, 1, 16);
    }

    [Fact]
    public async Task No_outer_is_sent_more_than_the_allowance_past_the_payers_checked_sequence()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 2 });

        await strategy.SubmitAllAsync(BatchTestNode.Memos(80), null, Token);

        Assert.All(
            node.Sink.Transmissions.Where(transmission => transmission.TransactionType == "Batch"),
            transmission => Assert.True(transmission.Sequence < transmission.PayerValidatedSequence + 2));
    }

    [Fact]
    public async Task A_batch_signed_while_the_round_halts_is_discarded_unsent()
    {
        BatchTestNode node = new();
        GatedSigner payer = new(node.Payer, gateAt: 3);
        await using BatchStrategy strategy = node.Strategy(payer: payer);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await payer.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        node.Sink.Foreign(node.Writer.Account, "AccountSet");
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:diverged"));
        payer.Release();

        // Whether the run then completes or fails is Task 9's business (identity); here the
        // discarded blob must never have reached the node.
        Exception? outcome = await Record.ExceptionAsync(() => run);
        Assert.True(outcome is null or XrplFileException, outcome?.ToString());
        Assert.Contains("discard:2", strategy.Trace);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.Hash == payer.GatedHash);
    }

    [Fact]
    public async Task A_batch_registered_but_not_yet_seen_by_the_node_is_waited_for_before_the_sweep()
    {
        BatchTestNode node = new(sink => sink.LoseOuter.Add(BatchTestNode.PayerStart));
        GatedBlobSink gate = new(node.Sink, gateAt: 2);
        await using BatchStrategy strategy = node.Strategy(sink: gate);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:stalled"));
        gate.Release();

        IReadOnlyList<SubmittedMemo> landed = await run;

        // Registered, so not discarded: it reached the node after the halt, and the round settled
        // only past its deadline, when it could no longer apply (spec B9).
        string gatedHash = Xrpl.Utils.Hashes.HashLedger.HashSignedTx(gate.Gated!).ToUpperInvariant();
        FakeBatchLedgerSink.Transmission gated = Assert.Single(node.Sink.Transmissions, transmission => transmission.Hash == gatedHash);
        long settled = long.Parse(strategy.Trace.First(entry => entry.StartsWith("settled:", StringComparison.Ordinal))["settled:".Length..]);

        Assert.Equal(24, landed.Count);
        Assert.True(settled > gated.LastLedger);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_sends_nothing()
    {
        BatchTestNode node = new();
        node.Fence.Fail = true;
        await using BatchStrategy strategy = node.Strategy();

        await Assert.ThrowsAsync<IOException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public void A_payer_who_is_also_the_writer_is_refused()
    {
        BatchTestNode node = new();

        Assert.Throws<ArgumentException>(() => node.Strategy(payer: node.Writer));
    }

    [Fact]
    public void The_strategy_reports_the_approved_unit_the_writer_and_no_mints()
    {
        BatchTestNode node = new();
        BatchStrategy strategy = node.Strategy();

        Assert.Equal(15, strategy.FeeDropsPerTransaction);
        Assert.Equal(node.Writer.Account, strategy.Account);
        Assert.Equal(node.Payer.Account, strategy.Payer);
        Assert.Equal(0, strategy.TicketMints);
    }

    [Fact]
    public async Task Every_landed_memo_says_which_ledger_it_is_in()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        foreach (SubmittedMemo memo in await strategy.SubmitAllAsync(BatchTestNode.Memos(10), null, Token))
        {
            Assert.Equal((await node.Ledger.TransactionAsync(memo.TxHash, Token))!.LedgerIndex, memo.LedgerIndex);
        }
    }

    [Fact]
    public async Task No_batch_is_signed_until_the_window_has_a_slot_for_it()
    {
        BatchTestNode node = new();
        GatedBlobSink gate = new(node.Sink, gateAt: 1);
        CountingSigner payer = new(node.Payer);
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { InitialWindow = 1 }, payer: payer, sink: gate);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await Task.Delay(200, Token);

        // The one slot is held by the first batch's transmission: nothing else may be signed meanwhile.
        Assert.Equal(1, payer.Count);

        gate.Release();
        Assert.Equal(40, (await run).Count);
    }

    /// <summary>Counts the signatures it makes.</summary>
    private sealed class CountingSigner(ITransactionSigner inner) : ITransactionSigner
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public string Account => inner.Account;

        public string PublicKey => inner.PublicKey;

        public Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return inner.SignAsync(transaction, cancellationToken);
        }
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchChainTests -class XrplFile.Tests.BatchStrategyTests`
Expected: build FAILS (`BatchChain`, `BatchStrategy` do not exist).

- [ ] **Step 4: Write `BatchContext`, `BatchChain` and the basic `BatchSweep`**

`src/XrplFile.Core/BatchContext.cs`:

```csharp
namespace XrplFile;

/// <summary>Everything a round shares with the strategy that runs it, for the strategy's lifetime.</summary>
internal sealed record BatchContext(
    ILedgerSink Sink,
    ILedgerSource Source,
    ITransactionSigner Writer,
    ITransactionSigner Payer,
    BatchBudget Budget,
    BatchStrategyOptions Options,
    BatchFence Fence,
    BatchWindow Window,
    BatchTrace Trace);

/// <summary>An inner that landed with tesSUCCESS: its hash and the ledger it is in.</summary>
internal sealed record LandedInner(string Hash, long LedgerIndex);

/// <summary>What one round found: the memos it landed, by call index, and what the payer paid for the outers that applied (spec B29).</summary>
internal sealed record RoundResult(IReadOnlyDictionary<int, LandedInner> Landed, long PayerFeeDrops, bool PayerFeeKnown)
{
    public static RoundResult Empty { get; } = new(new Dictionary<int, LandedInner>(), 0, true);
}
```

`src/XrplFile.Core/BatchChain.cs`:

```csharp
namespace XrplFile;

/// <summary>The chain check of spec B8, on one snapshot.</summary>
internal static class BatchChain
{
    public const string Diverged = "diverged";

    public const string Stalled = "stalled";

    /// <summary>
    /// <see cref="Diverged"/> when the writer's sequence differs from what the outers the payer
    /// consumed imply, or the payer consumed more than was registered; <see cref="Stalled"/> when the
    /// snapshot is past the deadline of the first registered outer the payer has not consumed; null
    /// otherwise. It does not try to say why: identity is settlement's question (spec B12).
    /// </summary>
    public static string? Verdict(
        RoundPlan plan, LedgerSnapshot start, IReadOnlyList<BatchCandidate> outers, LedgerSnapshot snapshot, string writer, string payer)
    {
        long consumed = (long)snapshot.SequenceOf(payer) - start.SequenceOf(payer);

        if (consumed < 0 || consumed > outers.Count) return Diverged;

        long expected = start.SequenceOf(writer);
        for (int k = 0; k < consumed; k++) expected += plan.Batches[k].Inners.Count;

        if (snapshot.SequenceOf(writer) != expected) return Diverged;

        if (consumed < outers.Count && snapshot.LedgerIndex > outers[(int)consumed].LastLedger) return Stalled;

        return null;
    }
}
```

`src/XrplFile.Core/BatchSweep.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// A round's final sweep (spec 4.1 step 8, B28): the writer's and payer's history over the round's
/// ledgers decides which memos landed. Only a validated tesSUCCESS record of the writer, inside one
/// of the round's outers or the round's solo, matched on marker and data, confirms a memo.
/// </summary>
internal static class BatchSweep
{
    public static async Task<RoundResult> FinalAsync(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending,
        IReadOnlyList<BatchCandidate> candidates, LedgerSnapshot start, LedgerSnapshot settled, CancellationToken cancellationToken)
    {
        if (settled.LedgerIndex <= start.LedgerIndex) return RoundResult.Empty;

        IReadOnlyList<LedgerTransaction> writer = await HistoryAsync(
            context.Source, context.Writer.Account, start.LedgerIndex + 1, settled.LedgerIndex, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LedgerTransaction> payer = await HistoryAsync(
            context.Source, context.Payer.Account, start.LedgerIndex + 1, settled.LedgerIndex, cancellationToken).ConfigureAwait(false);

        return Outcome(context, memos, pending, candidates, writer, payer);
    }

    internal static RoundResult Outcome(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyList<BatchCandidate> candidates,
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords)
    {
        HashSet<string> outers = new(candidates.Where(c => c.IsOuter).Select(c => c.Hash), StringComparer.OrdinalIgnoreCase);
        string? solo = candidates.FirstOrDefault(c => !c.IsOuter)?.Hash;

        Dictionary<string, int> byMarker = new(StringComparer.OrdinalIgnoreCase);
        foreach (int index in pending) byMarker[memos[index].MemoTypeHex] = index;

        Dictionary<int, LandedInner> landed = [];

        foreach (LedgerTransaction tx in writerRecords)
        {
            if (!tx.Succeeded || !tx.Validated || !string.Equals(tx.Account, context.Writer.Account, StringComparison.Ordinal)) continue;

            bool ours = tx.ParentBatchID is { } parent
                ? outers.Contains(parent)
                : solo is not null && string.Equals(tx.Hash, solo, StringComparison.OrdinalIgnoreCase);

            if (!ours || tx.MemoTypeHex is null || !byMarker.TryGetValue(tx.MemoTypeHex, out int index)) continue;
            if (!string.Equals(tx.MemoDataHex, memos[index].MemoDataHex, StringComparison.OrdinalIgnoreCase)) continue;

            landed.TryAdd(index, new LandedInner(tx.Hash, tx.LedgerIndex));
        }

        long fee = 0;
        bool known = true;

        foreach (LedgerTransaction tx in payerRecords)
        {
            if (!outers.Contains(tx.Hash)) continue;

            if (tx.Fee is { } drops) fee += drops;
            else known = false;
        }

        return new RoundResult(landed, fee, known);
    }

    internal static async Task<IReadOnlyList<LedgerTransaction>> HistoryAsync(
        ILedgerSource source, string account, long from, long to, CancellationToken cancellationToken)
    {
        List<LedgerTransaction> records = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(account, from, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            records.Add(tx);
        }

        return records;
    }
}
```

- [ ] **Step 5: Write `BatchRound`**

`src/XrplFile.Core/BatchRound.cs`:

```csharp
using System.Runtime.ExceptionServices;

namespace XrplFile;

/// <summary>
/// One round of a Batch run (spec 4.1, B5): a snapshot, a plan from it, the batches sent through the
/// window while the chain is checked, settlement, the round's solo memo, and the final sweep. Once a
/// round has settled nothing it sent can still land, so the next round cannot collide with it.
/// </summary>
internal sealed class BatchRound(
    BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IDictionary<int, int> candidatesPerMemo)
{
    /// <summary>Transmissions of one blob, the first included (spec submit-answer table).</summary>
    private const int MaxTransmissions = 4;

    private readonly Lock _latestGate = new();
    private LedgerSnapshot? _latest;
    private DateTimeOffset _latestAt;
    private long _allowance;
    private Exception? _fatal;

    private string Writer => context.Writer.Account;

    private string Payer => context.Payer.Account;

    private BatchStrategyOptions Options => context.Options;

    public async Task<RoundResult> RunAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot start = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        long unit = await UnitAsync(cancellationToken).ConfigureAwait(false);
        RoundPlan plan = BatchGrouping.Plan(pending, start.SequenceOf(Writer), start.SequenceOf(Payer));
        BatchRegistry registry = new(context.Fence, candidatesPerMemo, context.Trace);

        context.Trace.Add($"round:{start.LedgerIndex}");

        LedgerSnapshot settled = plan.Batches.Count == 0
            ? start
            : await SendBatchesAsync(plan, start, unit, registry, cancellationToken).ConfigureAwait(false);

        // A's own transaction never shares a ledger window with batches in flight (spec B11), and a
        // halted round sends no more: the memo goes to the next round.
        if (plan.SoloMemo is int solo && !registry.IsHalted)
        {
            settled = await SendSoloAsync(solo, unit, registry, cancellationToken).ConfigureAwait(false);
        }

        return await BatchSweep.FinalAsync(context, memos, pending, registry.Candidates, start, settled, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LedgerSnapshot> SendBatchesAsync(
        RoundPlan plan, LedgerSnapshot start, long unit, BatchRegistry registry, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _allowance, start.SequenceOf(Payer) + (long)Options.MaxAhead);

        List<Task> transmissions = [];
        using CancellationTokenSource transmitting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource checking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task checks = ChainCheckLoopAsync(plan, start, registry, checking.Token);
        ExceptionDispatchInfo? failure = null;
        LedgerSnapshot? settled = null;

        try
        {
            try
            {
                foreach (PlannedBatch batch in plan.Batches)
                {
                    if (!await WaitForAllowanceAsync(batch.OuterSequence, registry, cancellationToken).ConfigureAwait(false)) break;

                    // A batch is built and signed only once the window has a slot for it (spec 4.1
                    // step 3, B10); the slot passes to its first transmission.
                    await context.Window.AcquireAsync(cancellationToken).ConfigureAwait(false);
                    bool handedOver = false;

                    try
                    {
                        if (registry.IsHalted || Volatile.Read(ref _fatal) is not null) break;

                        LedgerSnapshot basis = await FreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
                        long fee = BatchFees.BatchFee(batch.Inners.Count, unit);
                        uint lastLedger = checked((uint)(basis.LedgerIndex + Options.DeadlineLedgers));

                        Dictionary<string, object> outer = BatchTransactions.Outer(
                            Payer, context.Payer.PublicKey, batch.OuterSequence, fee, lastLedger,
                            [.. batch.Inners.Select(inner => BatchTransactions.Inner(Writer, inner.Sequence, memos[inner.MemoIndex]))]);

                        SignedTransaction signed = await context.Trace.TimeSigningAsync(
                            () => BatchTransactions.SignAsync(outer, context.Writer, context.Payer, cancellationToken)).ConfigureAwait(false);

                        BatchCandidate candidate = new(
                            batch.Ordinal, isOuter: true, Payer, batch.OuterSequence, lastLedger, fee,
                            [.. batch.Inners.Select(inner => inner.MemoIndex)], signed);

                        if (!await registry.TryRegisterAsync(candidate, cancellationToken).ConfigureAwait(false)) break;

                        transmissions.Add(TransmitAsync(candidate, registry, slotHeld: true, transmitting, transmitting.Token));
                        handedOver = true;
                    }
                    finally
                    {
                        if (!handedOver) context.Window.Return();
                    }
                }
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }

            await checking.CancelAsync().ConfigureAwait(false);

            try
            {
                await checks.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (checking.IsCancellationRequested)
            {
            }

            // A run that failed here fails unsettled; the fence already covers everything registered (spec B23).
            if (failure is null && Volatile.Read(ref _fatal) is null)
            {
                settled = await SettleAsync(registry.Candidates, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever ended the round -- settlement, a failure, the caller's cancellation -- no
            // transmission outlives it.
            await transmitting.CancelAsync().ConfigureAwait(false);
            await DrainAsync(transmissions).ConfigureAwait(false);
        }

        failure?.Throw();
        if (Volatile.Read(ref _fatal) is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();

        return settled!;
    }

    private async Task<LedgerSnapshot> SendSoloAsync(int memoIndex, long unit, BatchRegistry registry, CancellationToken cancellationToken)
    {
        LedgerSnapshot basis = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        uint sequence = basis.SequenceOf(Writer);
        uint lastLedger = checked((uint)(basis.LedgerIndex + Options.DeadlineLedgers));

        Dictionary<string, object> solo = BatchTransactions.Solo(Writer, context.Writer.PublicKey, sequence, unit, lastLedger, memos[memoIndex]);
        SignedTransaction signed = await context.Trace.TimeSigningAsync(
            () => context.Writer.SignAsync(solo, cancellationToken)).ConfigureAwait(false);

        BatchCandidate candidate = new(BatchCandidate.SoloOrdinal, isOuter: false, Writer, sequence, lastLedger, unit, [memoIndex], signed);

        if (!await registry.TryRegisterAsync(candidate, cancellationToken).ConfigureAwait(false)) return basis;

        using CancellationTokenSource transmitting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task transmission = TransmitAsync(candidate, registry, slotHeld: false, transmitting, transmitting.Token);
        LedgerSnapshot settled;

        try
        {
            settled = await SettleAsync([candidate], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transmitting.CancelAsync().ConfigureAwait(false);
            await DrainAsync([transmission]).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _fatal) is { } fatal) ExceptionDispatchInfo.Capture(fatal).Throw();

        return settled;
    }

    /// <summary>
    /// Transmits one registered candidate (spec submit-answer table): busy answers and lost answers
    /// are transport retries of the same blob, <see cref="BatchStrategyOptions.TransportRetryStep"/> ×
    /// (retry + 1) apart, up to four transmissions in all; <c>telINSUF_FEE_P</c> is not resent, since
    /// the next round reads the unit again; any other answer leaves it to settlement; a malformed one
    /// fails the run. A halted round sends no first copy of anything: the candidate stays registered
    /// and counted, its deadline is already in the fence, and settlement waits it out (Spec question 24).
    /// </summary>
    private async Task TransmitAsync(
        BatchCandidate candidate, BatchRegistry registry, bool slotHeld, CancellationTokenSource transmitting, CancellationToken cancellationToken)
    {
        try
        {
            for (int transmission = 1; transmission <= MaxTransmissions; transmission++)
            {
                bool first = transmission == 1;

                if (candidate.IsResolved || (first && registry.IsHalted))
                {
                    if (first && slotHeld) context.Window.Return();
                    if (first && !candidate.IsResolved) context.Trace.Add($"unsent:{candidate.Ordinal}");
                    return;
                }

                if (!(first && slotHeld)) await context.Window.AcquireAsync(cancellationToken).ConfigureAwait(false);
                string? answer = null;

                try
                {
                    answer = await context.Sink.SubmitBlobAsync(candidate.Signed.TxBlob, cancellationToken).ConfigureAwait(false);
                }
                catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
                {
                    // No answer: the next call reconnects (spec B24), and the blob is sent again.
                }
                finally
                {
                    context.Window.Release(busy: answer is null || IsBusy(answer));
                }

                context.Trace.Add($"transmit:{candidate.Ordinal}:{answer ?? "no answer"}");

                if (answer is not null && IsMalformed(answer))
                {
                    throw new XrplFileException(XrplFileError.SubmitRefused,
                        $"the node answered {answer} to {candidate.Hash}; a malformed transaction is a defect, not a condition",
                        engineResult: answer);
                }

                if (answer == "telINSUF_FEE_P") return;

                if (answer is not null && !IsBusy(answer)) return;

                if (transmission < MaxTransmissions)
                {
                    await Task.Delay(Options.TransportRetryStep * transmission, Options.Time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _fatal, e, null);
            await registry.HaltAsync("fatal", CancellationToken.None).ConfigureAwait(false);
            await transmitting.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A <c>tel</c> answer, <c>tooBusy</c>, <c>slowDown</c> (spec B10), or a node briefly out of step
    /// with its network -- <c>noNetwork</c>, <c>noCurrent</c>, <c>noClosed</c>, <c>notSynced</c>
    /// (Spec question 13). <c>amendmentBlocked</c> and any other error are left to settlement.
    /// </summary>
    private static bool IsBusy(string answer) =>
        answer.StartsWith("tel", StringComparison.Ordinal)
        || answer is "error:tooBusy" or "error:slowDown" or "error:noNetwork" or "error:noCurrent" or "error:noClosed" or "error:notSynced";

    /// <summary>A <c>tem</c> answer, or a blob the node could not read at all.</summary>
    private static bool IsMalformed(string answer) =>
        answer.StartsWith("tem", StringComparison.Ordinal) || answer == "error:invalidTransaction";

    /// <summary>
    /// The chain check of spec B8, every <see cref="BatchStrategyOptions.ChainCheck"/>. A check that
    /// cannot be made -- the node unreachable past its limit, a ledger that stopped -- is fatal: it
    /// halts the round, so a sender waiting for allowance is released rather than left waiting.
    /// </summary>
    private async Task ChainCheckLoopAsync(RoundPlan plan, LedgerSnapshot start, BatchRegistry registry, CancellationToken cancellationToken)
    {
        try
        {
            while (!registry.IsHalted)
            {
                await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);

                LedgerSnapshot snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);

                if (BatchChain.Verdict(plan, start, registry.Outers, snapshot, Writer, Payer) is { } verdict)
                {
                    await registry.HaltAsync(verdict, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Raised only by a snapshot that passed the check (spec B10).
                Interlocked.Exchange(ref _allowance, snapshot.SequenceOf(Payer) + (long)Options.MaxAhead);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _fatal, e, null);
            await registry.HaltAsync("fatal", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForAllowanceAsync(uint outerSequence, BatchRegistry registry, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (registry.IsHalted || Volatile.Read(ref _fatal) is not null) return false;
            if (outerSequence < Interlocked.Read(ref _allowance)) return true;

            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until every candidate is resolved (spec B9), then <see cref="BatchStrategyOptions.SettleLedgers"/>
    /// more ledgers, and returns the settling snapshot.
    /// </summary>
    private async Task<LedgerSnapshot> SettleAsync(IReadOnlyList<BatchCandidate> candidates, CancellationToken cancellationToken)
    {
        LedgerSnapshot snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);

        while (!candidates.All(candidate => candidate.IsResolvedBy(snapshot)))
        {
            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
            snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (BatchCandidate candidate in candidates) candidate.MarkResolved();

        long target = snapshot.LedgerIndex + Options.SettleLedgers;

        while (snapshot.LedgerIndex < target)
        {
            await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
            snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        context.Trace.Add($"settled:{snapshot.LedgerIndex}");
        return snapshot;
    }

    private async Task<LedgerSnapshot> FreshSnapshotAsync(CancellationToken cancellationToken)
    {
        lock (_latestGate)
        {
            if (_latest is { } latest && Options.Time.GetUtcNow() - _latestAt <= Options.SnapshotMaxAge) return latest;
        }

        return await SnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<LedgerSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot snapshot = await ReadAsync(
            token => context.Source.SnapshotAsync([Writer, Payer], token), cancellationToken).ConfigureAwait(false);

        lock (_latestGate)
        {
            _latest = snapshot;
            _latestAt = Options.Time.GetUtcNow();
        }

        return snapshot;
    }

    private async Task<long> UnitAsync(CancellationToken cancellationToken) =>
        BatchFees.Unit(await ReadAsync(context.Source.OpenLedgerFeeAsync, cancellationToken).ConfigureAwait(false));

    /// <summary>A read that failed is taken again (spec B6); three attempts.</summary>
    private async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable && attempt < 3)
            {
                await Task.Delay(Options.ChainCheck, Options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DrainAsync(IReadOnlyList<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A transmission cancelled with its round; nothing it was can be in doubt past the fence.
        }
    }
}
```

In `src/XrplFile.Core/ISubmissionStrategy.cs`, give `SubmittedMemo` a body:

```csharp
public sealed record SubmittedMemo(int Index, string TxHash)
{
    /// <summary>
    /// The ledger the memo landed in, when the strategy read it off the record it confirmed the memo
    /// by; null when it did not. The writer then needs no <c>tx</c> lookup per memo (Spec question 23).
    /// </summary>
    public long? LedgerIndex { get; init; }
}
```

- [ ] **Step 6: Write `BatchStrategy`**

`src/XrplFile.Core/BatchStrategy.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// Memos as inners of the writer, eight to a batch, in outers the payer signs and pays for (spec B1,
/// 4.1). The writer's sequence carries the chunks; the payer's sequence orders the batches; nothing
/// is queued, and nothing locks a reserve. Rounds assign sequences from one snapshot, send, settle
/// and sweep; whatever is still missing goes into the next round with fresh sequences.
///
/// One instance serves one session: the candidates per memo and the budget are counted over its
/// lifetime, and SubmitAllAsync is not re-entrant.
/// </summary>
public sealed class BatchStrategy : ISubmissionStrategy
{
    private readonly BatchContext _context;
    private readonly BatchStrategyOptions _options;
    private long _payerFeeDrops;
    private bool _payerFeeUnknown;

    public BatchStrategy(
        ILedgerSink sink, ILedgerSource source, ITransactionSigner writer, ITransactionSigner payer,
        BatchBudget budget, IBatchFenceRecorder fence, BatchStrategyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(payer);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(fence);

        if (string.Equals(writer.Account, payer.Account, StringComparison.Ordinal))
        {
            throw new ArgumentException("the payer of a Batch run is another account than its writer (spec B1)", nameof(payer));
        }

        _options = options ?? new BatchStrategyOptions();
        _context = new BatchContext(
            sink, source, writer, payer, budget, _options, new BatchFence(fence), new BatchWindow(_options.InitialWindow), new BatchTrace());
    }

    /// <summary>The estimate's approved unit, which the session checks the estimate against (spec 4.1).</summary>
    public long FeeDropsPerTransaction => _context.Budget.FeeUnitDrops;

    /// <summary>The writer: every memo is the writer's.</summary>
    public string Account => _context.Writer.Account;

    public string Payer => _context.Payer.Account;

    public int TicketMints => 0;

    /// <summary>
    /// What the payer paid this session: the <c>Fee</c> of every outer this instance signed that
    /// settlement found applied, matched by hash (spec B29). Null when a record did not say its fee.
    /// </summary>
    public long? PayerFeeDrops => _payerFeeUnknown ? null : Interlocked.Read(ref _payerFeeDrops);

    /// <summary>Time spent inside the two signers, over this instance's lifetime (spec 4.2's measurement).</summary>
    public TimeSpan SigningTime => _context.Trace.SigningTime;

    internal IReadOnlyList<string> Trace => _context.Trace.Events;

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memos);
        RequireDistinctMarkers(memos);

        Dictionary<int, LandedInner> landed = [];
        Dictionary<int, int> candidates = [];
        List<int> pending = [.. Enumerable.Range(0, memos.Count)];

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAnotherCandidate(memos, pending, candidates);

            RoundResult round = await new BatchRound(_context, memos, pending, candidates).RunAsync(cancellationToken).ConfigureAwait(false);

            foreach ((int index, LandedInner inner) in round.Landed) landed.TryAdd(index, inner);

            Interlocked.Add(ref _payerFeeDrops, round.PayerFeeDrops);
            if (!round.PayerFeeKnown) _payerFeeUnknown = true;

            submitted?.Report(landed.Count);
            pending = [.. pending.Where(index => !landed.ContainsKey(index))];
        }

        return [.. landed.OrderBy(pair => pair.Key)
            .Select(pair => new SubmittedMemo(pair.Key, pair.Value.Hash) { LedgerIndex = pair.Value.LedgerIndex })];
    }

    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TicketRelease.None);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A memo that would need a fourth candidate fails the run (spec B13).</summary>
    private void RequireAnotherCandidate(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyDictionary<int, int> candidates)
    {
        foreach (int index in pending)
        {
            if (candidates.GetValueOrDefault(index) >= _options.MaxAttemptsPerMemo)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {memos[index].MemoTypeHex} did not land in the {_options.MaxAttemptsPerMemo} candidates a session allows it");
            }
        }
    }

    private static void RequireDistinctMarkers(IReadOnlyList<MemoSubmission> memos)
    {
        HashSet<string> markers = new(memos.Count, StringComparer.OrdinalIgnoreCase);

        foreach (MemoSubmission memo in memos)
        {
            if (!markers.Add(memo.MemoTypeHex))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"two memos carry the marker {memo.MemoTypeHex}, which the sweep could not tell apart");
            }
        }
    }
}
```

(`ReleaseLeftoverTicketsAsync` becomes the real tidy-up in Task 9.)

- [ ] **Step 7: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchChainTests -class XrplFile.Tests.BatchStrategyTests`
Expected: PASS. In `A_batch_signed_while_the_round_halts…` the run completes here (the next round rebases on a fresh snapshot); from Task 9 on, the same script fails with `AccountInUse`, which Task 9 tests separately.

- [ ] **Step 8: Run all Core unit tests three times (timing-sensitive code) and commit**

Run three times: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS every time.

```bash
git add src/XrplFile.Core/ISubmissionStrategy.cs src/XrplFile.Core/BatchContext.cs src/XrplFile.Core/BatchChain.cs src/XrplFile.Core/BatchRound.cs src/XrplFile.Core/BatchSweep.cs src/XrplFile.Core/BatchStrategy.cs tests/XrplFile.Core.Tests/BatchChainTests.cs tests/XrplFile.Core.Tests/BatchStrategyTests.cs tests/XrplFile.Core.Tests/BatchTestNode.cs tests/XrplFile.Core.Tests/GatedSigner.cs tests/XrplFile.Core.Tests/GatedBlobSink.cs
git commit -m "feat(core): BatchStrategy rounds with chain check, settlement and solo memos"
```

---
### Task 9: `BatchStrategy` slice 4 — complete history, identity, the payer's fee and the tidy-up

Spec B12, B29, B30, 4.1 step 8 and "Tidy-up".

**Files:**
- Modify: `src/XrplFile.Core/BatchSweep.cs` (replace the file), `src/XrplFile.Core/BatchStrategy.cs` (`ReleaseLeftoverTicketsAsync`)
- Test: `tests/XrplFile.Core.Tests/BatchIdentityTests.cs`

**Interfaces:**
- Consumes: Task 8's `BatchSweep.FinalAsync` signature (unchanged), `LeftoverTickets.ReleaseAsync(ILedgerSink, ILedgerSource, ITransactionSigner, long, TimeSpan, TimeProvider, CancellationToken)`.
- Produces: `internal static bool BatchSweep.Covers(IEnumerable<LedgerTransaction> records, uint from, uint to)`; `AccountInUse` thrown with `Role`; `NodeLacksHistory` after `HistoryLagLimit`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchIdentityTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Queues a foreign transaction at the first transmission, so it lands inside the first round.</summary>
    private static BatchTestNode NodeWithForeign(Func<BatchTestNode, string> account, string kind, uint count = 0)
    {
        BatchTestNode node = new();
        bool queued = false;
        node.Sink.Transmitting = _ =>
        {
            if (queued) return;
            queued = true;
            node.Sink.Foreign(account(node), kind, count);
        };
        return node;
    }

    [Fact]
    public async Task A_transaction_of_the_writer_that_the_run_did_not_send_fails_it_naming_the_writer()
    {
        BatchTestNode node = NodeWithForeign(n => n.Writer.Account, "AccountSet");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
        Assert.Contains(node.Writer.Account, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transaction_of_the_payer_that_the_run_did_not_send_fails_it_naming_the_payer()
    {
        BatchTestNode node = NodeWithForeign(n => n.Payer.Account, "AccountSet");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Payer, error.Role);
    }

    [Fact]
    public async Task A_ticket_create_of_the_payer_is_caught_by_identity_not_by_sequence_arithmetic()
    {
        BatchTestNode node = NodeWithForeign(n => n.Payer.Account, "TicketCreate", 5);
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Payer, error.Role);
    }

    [Fact]
    public async Task A_ticket_create_and_a_ticketed_transaction_of_the_writer_complete_the_history_and_fail_identity()
    {
        BatchTestNode node = new();
        bool queued = false;
        node.Sink.Transmitting = _ =>
        {
            if (queued) return;
            queued = true;
            node.Sink.Foreign(node.Writer.Account, "TicketCreate", 3);
            node.Sink.Foreign(node.Writer.Account, "TicketedAccountSet");
        };
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        // Not NodeLacksHistory: the TicketCreate covers the sequences its tickets took (spec B30).
        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
    }

    [Fact]
    public async Task A_history_that_lags_the_snapshot_is_read_again_until_it_covers_the_round()
    {
        BatchTestNode node = new();
        node.Ledger.HistoryHides = tx => tx.ParentBatchID is not null;
        node.Ledger.HistoryHideReads = 2;
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { ProgressSweep = TimeSpan.FromHours(1) });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        Assert.Equal(16, landed.Count);
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_history_that_never_covers_the_round_fails_with_NodeLacksHistory()
    {
        BatchTestNode node = new();
        node.Ledger.HistoryHides = tx => tx.ParentBatchID is not null;
        node.Ledger.HistoryHideReads = int.MaxValue;
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with
        {
            ProgressSweep = TimeSpan.FromHours(1),
            HistoryLagLimit = TimeSpan.FromMilliseconds(200),
        });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(XrplFileError.NodeLacksHistory, error.Error);
    }

    [Fact]
    public async Task The_payer_fee_is_the_fee_of_every_outer_that_applied_a_tec_outer_included()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        long applied = 0;
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            applied += tx.Fee!.Value;
        }

        Assert.Equal(applied, strategy.PayerFeeDrops);
        Assert.True(applied > 2 * BatchFees.BatchFee(8, 15));
    }

    [Fact]
    public async Task A_batch_signed_while_a_foreign_transaction_halted_the_round_ends_in_AccountInUse()
    {
        BatchTestNode node = new();
        GatedSigner payer = new(node.Payer, gateAt: 3);
        await using BatchStrategy strategy = node.Strategy(payer: payer);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await payer.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        node.Sink.Foreign(node.Writer.Account, "AccountSet");
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:diverged"));
        payer.Release();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => run);
        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
    }

    [Fact]
    public void A_ticket_create_covers_its_own_sequence_and_its_tickets()
    {
        LedgerTransaction create = new("C", 5, "rW", true, true, "tesSUCCESS", null, null, null)
        {
            TransactionType = "TicketCreate", Sequence = 10, TicketCount = 3,
        };
        LedgerTransaction next = new("N", 6, "rW", false, true, "tecFAKE", null, null, null) { Sequence = 14 };

        Assert.True(BatchSweep.Covers([create, next], 10, 15));
        Assert.False(BatchSweep.Covers([create], 10, 15));
        Assert.True(BatchSweep.Covers([], 10, 10));
    }

    [Fact]
    public async Task The_tidy_up_spends_this_librarys_leftover_tickets_on_the_writer()
    {
        BatchTestNode node = new();
        node.Ledger.Add(new LedgerTransaction("CREATOR", 1, node.Writer.Account, true, true, "tesSUCCESS", MemoLayout.TicketMarkerHex, null, null));
        node.Sink.AddLiveTicket(node.Writer.Account, 77, "CREATOR");
        await using BatchStrategy strategy = node.Strategy();

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(Token);

        Assert.Equal(1, release.Released);
        FakeBatchLedgerSink.Transmission spend = Assert.Single(node.Sink.Transmissions);
        Assert.Equal(node.Writer.Account, spend.Account);
    }

    [Fact]
    public async Task With_no_leftover_tickets_the_tidy_up_sends_nothing()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        Assert.Equal(TicketRelease.None, await strategy.ReleaseLeftoverTicketsAsync(Token));
        Assert.Empty(node.Sink.Transmissions);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchIdentityTests`
Expected: build FAILS (`BatchSweep.Covers` does not exist); once stubbed, the identity tests complete instead of throwing.

- [ ] **Step 3: Replace `BatchSweep`**

`src/XrplFile.Core/BatchSweep.cs` (whole file):

```csharp
namespace XrplFile;

/// <summary>
/// A round's final sweep (spec 4.1 step 8). It reads the writer's and the payer's history over the
/// round's ledgers until both cover the account's sequence interval for the round (spec B30), checks
/// that nothing in it is foreign (spec B12), and only then decides which memos landed (spec B28) and
/// what the payer paid (spec B29).
/// </summary>
internal static class BatchSweep
{
    public static async Task<RoundResult> FinalAsync(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending,
        IReadOnlyList<BatchCandidate> candidates, LedgerSnapshot start, LedgerSnapshot settled, CancellationToken cancellationToken)
    {
        if (settled.LedgerIndex <= start.LedgerIndex) return RoundResult.Empty;

        string writer = context.Writer.Account;
        string payer = context.Payer.Account;
        DateTimeOffset giveUp = context.Options.Time.GetUtcNow() + context.Options.HistoryLagLimit;

        while (true)
        {
            List<LedgerTransaction> writerRecords = Own(await HistoryAsync(
                context.Source, writer, start.LedgerIndex + 1, settled.LedgerIndex, cancellationToken).ConfigureAwait(false), writer);
            List<LedgerTransaction> payerRecords = Own(await HistoryAsync(
                context.Source, payer, start.LedgerIndex + 1, settled.LedgerIndex, cancellationToken).ConfigureAwait(false), payer);

            if (Covers(writerRecords, start.SequenceOf(writer), settled.SequenceOf(writer))
                && Covers(payerRecords, start.SequenceOf(payer), settled.SequenceOf(payer)))
            {
                RequireIdentity(context, candidates, writerRecords, payerRecords);
                return Outcome(context, memos, pending, candidates, writerRecords, payerRecords);
            }

            if (context.Options.Time.GetUtcNow() >= giveUp)
            {
                throw new XrplFileException(XrplFileError.NodeLacksHistory,
                    $"account_tx did not cover ledgers {start.LedgerIndex + 1}–{settled.LedgerIndex} for the writer and the payer "
                    + $"within {context.Options.HistoryLagLimit}");
            }

            await Task.Delay(context.Options.ChainCheck, context.Options.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the records account for every sequence in [<paramref name="from"/>, <paramref name="to"/>):
    /// a record with a non-zero sequence covers it, and a TicketCreate also covers the sequences its
    /// tickets took. A ticketed record covers nothing. Records of any result count (spec B30).
    /// </summary>
    internal static bool Covers(IEnumerable<LedgerTransaction> records, uint from, uint to)
    {
        if (to <= from) return true;

        HashSet<uint> covered = [];

        foreach (LedgerTransaction tx in records)
        {
            if (tx.Sequence == 0) continue;

            covered.Add(tx.Sequence);

            if (string.Equals(tx.TransactionType, "TicketCreate", StringComparison.Ordinal) && tx.TicketCount is { } count)
            {
                for (uint ticket = 1; ticket <= count; ticket++) covered.Add(tx.Sequence + ticket);
            }
        }

        for (uint sequence = from; sequence < to; sequence++)
        {
            if (!covered.Contains(sequence)) return false;
        }

        return true;
    }

    /// <summary>
    /// A transaction of the payer that is not one of the round's outers, or of the writer that is
    /// neither an inner of those outers nor the round's solo, fails the run (spec B12).
    /// </summary>
    private static void RequireIdentity(
        BatchContext context, IReadOnlyList<BatchCandidate> candidates,
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords)
    {
        HashSet<string> outers = new(candidates.Where(c => c.IsOuter).Select(c => c.Hash), StringComparer.OrdinalIgnoreCase);
        string? solo = candidates.FirstOrDefault(c => !c.IsOuter)?.Hash;

        foreach (LedgerTransaction tx in payerRecords)
        {
            if (outers.Contains(tx.Hash)) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the payer {context.Payer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Payer);
        }

        foreach (LedgerTransaction tx in writerRecords)
        {
            bool ours = tx.ParentBatchID is { } parent
                ? outers.Contains(parent)
                : solo is not null && string.Equals(tx.Hash, solo, StringComparison.OrdinalIgnoreCase);

            if (ours) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the writer {context.Writer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Writer);
        }
    }

    internal static RoundResult Outcome(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyList<BatchCandidate> candidates,
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords)
    {
        HashSet<string> outers = new(candidates.Where(c => c.IsOuter).Select(c => c.Hash), StringComparer.OrdinalIgnoreCase);
        string? solo = candidates.FirstOrDefault(c => !c.IsOuter)?.Hash;

        Dictionary<string, int> byMarker = new(StringComparer.OrdinalIgnoreCase);
        foreach (int index in pending) byMarker[memos[index].MemoTypeHex] = index;

        Dictionary<int, LandedInner> landed = [];

        foreach (LedgerTransaction tx in writerRecords)
        {
            if (!tx.Succeeded || !tx.Validated) continue;

            bool ours = tx.ParentBatchID is { } parent
                ? outers.Contains(parent)
                : solo is not null && string.Equals(tx.Hash, solo, StringComparison.OrdinalIgnoreCase);

            if (!ours || tx.MemoTypeHex is null || !byMarker.TryGetValue(tx.MemoTypeHex, out int index)) continue;
            if (!string.Equals(tx.MemoDataHex, memos[index].MemoDataHex, StringComparison.OrdinalIgnoreCase)) continue;

            landed.TryAdd(index, new LandedInner(tx.Hash, tx.LedgerIndex));
        }

        long fee = 0;
        bool known = true;

        foreach (LedgerTransaction tx in payerRecords)
        {
            if (!outers.Contains(tx.Hash)) continue;

            if (tx.Fee is { } drops) fee += drops;
            else known = false;
        }

        return new RoundResult(landed, fee, known);
    }

    internal static async Task<IReadOnlyList<LedgerTransaction>> HistoryAsync(
        ILedgerSource source, string account, long from, long to, CancellationToken cancellationToken)
    {
        List<LedgerTransaction> records = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(account, from, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            records.Add(tx);
        }

        return records;
    }

    /// <summary>The records the account sent: account_tx also returns what others sent to it, which is not "of" it.</summary>
    private static List<LedgerTransaction> Own(IReadOnlyList<LedgerTransaction> records, string account) =>
        [.. records.Where(tx => string.Equals(tx.Account, account, StringComparison.Ordinal))];
}
```

- [ ] **Step 4: The tidy-up**

In `src/XrplFile.Core/BatchStrategy.cs` replace `ReleaseLeftoverTicketsAsync` with:

```csharp
    /// <summary>
    /// Spends this library's leftover tickets on the writer with plain AccountSets (spec 4.1 "Tidy-up"),
    /// with the behaviour and bounds of spec 2026-10-12 (I6–I8): the session calls it only after the
    /// last round settled and the run recorded "done". It runs at the approved unit.
    /// </summary>
    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) =>
        LeftoverTickets.ReleaseAsync(
            _context.Sink, _context.Source, _context.Writer, _context.Budget.FeeUnitDrops, _options.ChainCheck, _options.Time, cancellationToken);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchIdentityTests -class XrplFile.Tests.BatchStrategyTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/BatchSweep.cs src/XrplFile.Core/BatchStrategy.cs tests/XrplFile.Core.Tests/BatchIdentityTests.cs
git commit -m "feat(core): Batch settlement checks complete history and identity, and tidies up"
```

---

### Task 10: `BatchStrategy` slice 5 — candidates per memo, budget, fee ceiling, progress

Spec B13, B15 (ceiling and wait), B20 (Batch path), B26 (runtime budget).

**Files:**
- Create: `src/XrplFile.Core/BatchSpending.cs`, `src/XrplFile.Core/BatchProgress.cs`
- Modify: `src/XrplFile.Core/BatchContext.cs`, `src/XrplFile.Core/BatchRound.cs`, `src/XrplFile.Core/BatchStrategy.cs`
- Test: `tests/XrplFile.Core.Tests/BatchLimitsTests.cs`

**Interfaces:**
- Produces: internal `BatchSpending(BatchBudget budget)` with `void CommitPayer(long drops)`, `void CommitWriter(long drops)` (throw `BudgetExceeded`); internal `BatchProgress(IProgress<int>? inner)` with `void Found(IEnumerable<int> indices)`; `BatchContext` gains a last positional parameter `BatchSpending Spending`; `BatchRound` gains a last constructor parameter `BatchProgress progress`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchLimitsTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchLimitsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transport_retries_of_one_blob_are_one_candidate()
    {
        BatchTestNode node = new(sink =>
        {
            sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telCAN_NOT_QUEUE";
            sink.AnswerAt[(BatchTestNode.PayerStart, 2)] = "error:tooBusy";
            sink.AnswerAt[(BatchTestNode.PayerStart, 3)] = "telCAN_NOT_QUEUE";
        });
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAttemptsPerMemo = 1 });

        Assert.Equal(8, (await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token)).Count);
    }

    [Fact]
    public async Task A_tec_outer_is_a_candidate_however_little_it_wrote()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAttemptsPerMemo = 1 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
    }

    [Fact]
    public async Task A_memo_that_would_need_a_fourth_candidate_fails_the_run_and_progress_never_counted_it()
    {
        BatchTestNode node = new(sink =>
        {
            sink.InnerResult[MemoLayout.ChunkMarkerHex(0)] = "tecFAKE";
            sink.InnerResult[MemoLayout.ChunkMarkerHex(1)] = "tecFAKE";
        });
        List<int> reports = [];
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(
            BatchTestNode.Memos(16), new SynchronousProgress<int>(value => { lock (reports) reports.Add(value); }), Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.Answer == "tesSUCCESS" && t.Inners == 2));
        Assert.Equal(14, reports.Max());
        Assert.Equal(reports.OrderBy(value => value), reports);
    }

    [Fact]
    public async Task Progress_reports_memos_as_sweeps_find_them_before_the_round_settles()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(5);
        List<(int Value, bool Settled)> reports = [];
        BatchStrategy strategy = node.Strategy();
        await using (strategy)
        {
            await strategy.SubmitAllAsync(BatchTestNode.Memos(200), new SynchronousProgress<int>(value =>
            {
                lock (reports) reports.Add((value, strategy.Trace.Any(entry => entry.StartsWith("settled:", StringComparison.Ordinal))));
            }), Token);
        }

        Assert.Contains(reports, report => !report.Settled && report.Value > 0);
        Assert.Equal(200, reports[^1].Value);
        Assert.Equal(reports.Select(r => r.Value).OrderBy(v => v), reports.Select(r => r.Value));
    }

    [Fact]
    public async Task No_batch_is_signed_past_the_payers_budget()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 100, WriterBudgetDrops: 10_000));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task No_solo_is_signed_past_the_writers_budget()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 10_000, WriterBudgetDrops: 10));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task A_unit_above_the_ceiling_waits_for_fees_to_fall_and_proceeds_when_they_do()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFeeScript.Enqueue(new OpenLedgerFee(100, 10));
        await using BatchStrategy strategy = node.Strategy();

        Assert.Equal(8, (await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token)).Count);
        Assert.Contains("fee-wait:150", strategy.Trace);
    }

    [Fact]
    public async Task A_unit_still_above_the_ceiling_after_the_wait_fails_with_FeeTooHigh_and_sends_nothing()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(100, 10);
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.FeeTooHigh, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchLimitsTests`
Expected: FAIL — the budget, ceiling and progress tests fail (no budget check, no ceiling, one report per round); the two candidate tests pass already (the guard is Task 8's).

- [ ] **Step 3: Write `BatchSpending` and `BatchProgress`**

`src/XrplFile.Core/BatchSpending.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// The fees a session has committed to, per account, as it signs (spec B26). Signing past the
/// budget the estimate approved is refused: that is the safety net if an assumption behind the bound breaks.
/// </summary>
internal sealed class BatchSpending(BatchBudget budget)
{
    private readonly Lock _gate = new();
    private long _payer;
    private long _writer;

    public void CommitPayer(long drops)
    {
        lock (_gate)
        {
            if (_payer + drops > budget.PayerBudgetDrops)
            {
                throw new XrplFileException(XrplFileError.BudgetExceeded,
                    $"signing this batch would commit the payer to {_payer + drops} drops, past the {budget.PayerBudgetDrops} the estimate approved");
            }

            _payer += drops;
        }
    }

    public void CommitWriter(long drops)
    {
        lock (_gate)
        {
            if (_writer + drops > budget.WriterBudgetDrops)
            {
                throw new XrplFileException(XrplFileError.BudgetExceeded,
                    $"signing this transaction would commit the writer to {_writer + drops} drops, past the {budget.WriterBudgetDrops} the estimate approved");
            }

            _writer += drops;
        }
    }
}
```

`src/XrplFile.Core/BatchProgress.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// The call's progress (spec B20, 4.4): the number of its memos a sweep found landed with tesSUCCESS.
/// It counts memos found, never sequence movement, so it only grows.
/// </summary>
internal sealed class BatchProgress(IProgress<int>? inner)
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _found = [];
    private int _reported;

    public void Found(IEnumerable<int> indices)
    {
        lock (_gate)
        {
            foreach (int index in indices) _found.Add(index);

            if (_found.Count <= _reported) return;

            _reported = _found.Count;
            inner?.Report(_reported);
        }
    }
}
```

- [ ] **Step 4: Wire them in**

`src/XrplFile.Core/BatchContext.cs`: add `BatchSpending Spending` as the last positional parameter of `BatchContext` (after `BatchTrace Trace`).

`src/XrplFile.Core/BatchStrategy.cs`:
1. In the constructor, pass `new BatchSpending(budget)` as the last argument of `new BatchContext(...)`.
2. In `SubmitAllAsync`, after `List<int> pending = …` add `BatchProgress progress = new(submitted);`, construct rounds as `new BatchRound(_context, memos, pending, candidates, progress)`, and replace `submitted?.Report(landed.Count);` with `progress.Found(landed.Keys);`.

`src/XrplFile.Core/BatchRound.cs`:
1. Change the primary constructor to `BatchRound(BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IDictionary<int, int> candidatesPerMemo, BatchProgress progress)`.
2. Replace `RunAsync` with:

```csharp
    public async Task<RoundResult> RunAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot start = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        long unit = await UnitAsync(cancellationToken).ConfigureAwait(false);
        RoundPlan plan = BatchGrouping.Plan(pending, start.SequenceOf(Writer), start.SequenceOf(Payer));
        BatchRegistry registry = new(context.Fence, candidatesPerMemo, context.Trace);

        context.Trace.Add($"round:{start.LedgerIndex}");

        using CancellationTokenSource sweeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sweeps = ProgressLoopAsync(start, sweeping.Token);

        try
        {
            LedgerSnapshot settled = plan.Batches.Count == 0
                ? start
                : await SendBatchesAsync(plan, start, unit, registry, cancellationToken).ConfigureAwait(false);

            if (plan.SoloMemo is int solo && !registry.IsHalted)
            {
                settled = await SendSoloAsync(solo, unit, registry, cancellationToken).ConfigureAwait(false);
            }

            RoundResult result = await BatchSweep.FinalAsync(context, memos, pending, registry.Candidates, start, settled, cancellationToken)
                .ConfigureAwait(false);
            progress.Found(result.Landed.Keys);

            return result;
        }
        finally
        {
            await sweeping.CancelAsync().ConfigureAwait(false);

            try
            {
                await sweeps.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the writer's history forward from its last position every
    /// <see cref="BatchStrategyOptions.ProgressSweep"/> while the round runs, and reports the memos it
    /// finds with tesSUCCESS (spec B20). A read that fails is skipped; the final sweep decides anyway.
    /// </summary>
    private async Task ProgressLoopAsync(LedgerSnapshot start, CancellationToken cancellationToken)
    {
        Dictionary<string, int> byMarker = new(StringComparer.OrdinalIgnoreCase);
        foreach (int index in pending) byMarker[memos[index].MemoTypeHex] = index;

        long from = start.LedgerIndex + 1;

        while (true)
        {
            await Task.Delay(Options.ProgressSweep, Options.Time, cancellationToken).ConfigureAwait(false);

            try
            {
                long to = await context.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
                if (to < from) continue;

                long highest = from;
                List<int> found = [];

                await foreach (LedgerTransaction tx in context.Source
                    .AccountTransactionsAsync(Writer, from, to, forward: true, cancellationToken).ConfigureAwait(false))
                {
                    highest = Math.Max(highest, tx.LedgerIndex);

                    if (!tx.Succeeded || !tx.Validated || tx.MemoTypeHex is null) continue;
                    if (!string.Equals(tx.Account, Writer, StringComparison.Ordinal)) continue;

                    if (byMarker.TryGetValue(tx.MemoTypeHex, out int index)
                        && string.Equals(tx.MemoDataHex, memos[index].MemoDataHex, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(index);
                    }
                }

                progress.Found(found);
                from = highest;
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
            {
                // The next sweep tries again.
            }
        }
    }
```

3. In `SendBatchesAsync`, insert immediately before the `SignedTransaction signed = …` line:

```csharp
                context.Spending.CommitPayer(fee);
```

4. In `SendSoloAsync`, insert immediately before the `SignedTransaction signed = …` line:

```csharp
        context.Spending.CommitWriter(unit);
```

5. Replace `UnitAsync` with:

```csharp
    /// <summary>
    /// The round's unit (spec B15). Above the approved ceiling the round waits
    /// <see cref="BatchStrategyOptions.FeeWait"/> for fees to fall; if they have not, the run fails
    /// with <see cref="XrplFileError.FeeTooHigh"/> and can be resumed later.
    /// </summary>
    private async Task<long> UnitAsync(CancellationToken cancellationToken)
    {
        long ceiling = context.Budget.FeeCeilingDrops;
        long unit = BatchFees.Unit(await ReadAsync(context.Source.OpenLedgerFeeAsync, cancellationToken).ConfigureAwait(false));

        if (unit <= ceiling) return unit;

        context.Trace.Add($"fee-wait:{unit}");
        await Task.Delay(Options.FeeWait, Options.Time, cancellationToken).ConfigureAwait(false);

        unit = BatchFees.Unit(await ReadAsync(context.Source.OpenLedgerFeeAsync, cancellationToken).ConfigureAwait(false));

        if (unit <= ceiling) return unit;

        throw new XrplFileException(XrplFileError.FeeTooHigh,
            $"the fee unit is {unit} drops and the approved ceiling is {ceiling}; fees did not fall within {Options.FeeWait}");
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchLimitsTests -class XrplFile.Tests.BatchStrategyTests -class XrplFile.Tests.BatchIdentityTests`
Expected: PASS. In `A_memo_that_would_need_a_fourth_candidate…`, memos 0 and 1 had three candidates each (one 8-inner batch, then two 2-inner batches) and the run fails before a fourth.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/BatchSpending.cs src/XrplFile.Core/BatchProgress.cs src/XrplFile.Core/BatchContext.cs src/XrplFile.Core/BatchRound.cs src/XrplFile.Core/BatchStrategy.cs tests/XrplFile.Core.Tests/BatchLimitsTests.cs
git commit -m "feat(core): Batch budget, fee ceiling and progress from sweeps"
```

---

### Task 11: `BatchStrategy` slice 6 — dropped connections and a node that stops

Spec B24 (the strategy owns every resend), the `NodeUnreachable` row of the submit-answer table, Review Focus 1.

**Files:**
- Create: `src/XrplFile.Core/BatchReads.cs`
- Modify: `src/XrplFile.Core/BatchStrategyOptions.cs`, `src/XrplFile.Core/BatchRound.cs` (`ReadAsync`, `SnapshotAsync`, two fields), `src/XrplFile.Core/BatchSweep.cs` (`HistoryAsync` calls)
- Test: `tests/XrplFile.Core.Tests/BatchOutageTests.cs`

**Interfaces:**
- Produces: `BatchStrategyOptions.NodeOutageLimit` (`TimeSpan`, default 2 min; Spec question 4); internal `static class BatchReads` with `Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> read, BatchStrategyOptions options, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchOutageTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchOutageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BatchStrategyOptions Options => BatchTestNode.FastOptions with { NodeOutageLimit = TimeSpan.FromMilliseconds(300) };

    [Fact]
    public async Task A_blob_whose_answer_was_lost_is_resent_and_nothing_lands_twice()
    {
        BatchTestNode node = new(sink =>
        {
            // No ledger closes until every lost answer has been resent: otherwise settlement resolves
            // the three batches by the payer's sequence before their resends go out.
            sink.AutoClose = false;
            sink.ApplyThenDropAt.Add(0);
            sink.ApplyThenDropAt.Add(1);
            sink.ApplyThenDropAt.Add(2);
        });
        await using BatchStrategy strategy = node.Strategy(Options with { NodeOutageLimit = TimeSpan.FromSeconds(10) });

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);
        await BatchTestNode.EventuallyAsync(() => node.Sink.Transmissions.Count(t => t.Answer == "tefPAST_SEQ") >= 3);
        node.Sink.AutoClose = true;

        IReadOnlyList<SubmittedMemo> landed = await run;

        Assert.Equal(24, landed.Count);
        Assert.Equal(3, strategy.Trace.Count(entry => entry.EndsWith(":no answer", StringComparison.Ordinal)));
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshots_that_fail_for_a_moment_are_taken_again()
    {
        BatchTestNode node = new();
        node.Ledger.SnapshotFailures = 4;
        await using BatchStrategy strategy = node.Strategy(Options with { NodeOutageLimit = TimeSpan.FromSeconds(10) });

        Assert.Equal(16, (await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token)).Count);
    }

    [Fact]
    public async Task A_node_that_stays_unreachable_past_the_limit_fails_the_run()
    {
        BatchTestNode node = new();
        node.Ledger.SnapshotFailures = int.MaxValue;
        await using BatchStrategy strategy = node.Strategy(Options);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task A_validated_ledger_that_stops_advancing_fails_the_run_instead_of_hanging()
    {
        BatchTestNode node = new();
        node.Sink.AutoClose = false;
        await using BatchStrategy strategy = node.Strategy(Options);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
        Assert.Contains("stayed at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sender_waiting_for_allowance_still_fails_when_the_node_stops()
    {
        BatchTestNode node = new();
        node.Sink.AutoClose = false;

        // Two batches ahead at most: the sender is parked in its allowance wait when the chain check dies.
        await using BatchStrategy strategy = node.Strategy(Options with { MaxAhead = 2 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(80), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchOutageTests`
Expected: build FAILS (`NodeOutageLimit` does not exist).

- [ ] **Step 3: Add the option and the shared retry**

In `src/XrplFile.Core/BatchStrategyOptions.cs` add before `Time`:

```csharp
    /// <summary>
    /// How long reads may keep failing, or the validated ledger stay where it is, before the run fails
    /// with <see cref="XrplFileError.NodeUnreachable"/>. The spec bounds sends, not a node that stops.
    /// </summary>
    public TimeSpan NodeOutageLimit { get; init; } = TimeSpan.FromMinutes(2);
```

`src/XrplFile.Core/BatchReads.cs`:

```csharp
namespace XrplFile;

/// <summary>Reads of the Batch path: one that fails is taken again (spec B6), until the node has been unreachable for <see cref="BatchStrategyOptions.NodeOutageLimit"/>.</summary>
internal static class BatchReads
{
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> read, BatchStrategyOptions options, CancellationToken cancellationToken)
    {
        DateTimeOffset? firstFailure = null;

        while (true)
        {
            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.NodeUnreachable)
            {
                DateTimeOffset now = options.Time.GetUtcNow();
                firstFailure ??= now;

                if (now - firstFailure.Value >= options.NodeOutageLimit) throw;

                await Task.Delay(options.ChainCheck, options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
```

- [ ] **Step 4: Use it in the round and the sweep, and watch the ledger advance**

In `src/XrplFile.Core/BatchRound.cs`:
1. Add fields after `_latestAt`:

```csharp
    private long _highestLedger;
    private DateTimeOffset _advancedAt;
```

2. Replace `ReadAsync` with:

```csharp
    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        BatchReads.RetryAsync(read, Options, cancellationToken);
```

3. Replace `SnapshotAsync` with:

```csharp
    private async Task<LedgerSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        LedgerSnapshot snapshot = await ReadAsync(
            token => context.Source.SnapshotAsync([Writer, Payer], token), cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = Options.Time.GetUtcNow();
        bool stuck = false;

        lock (_latestGate)
        {
            if (snapshot.LedgerIndex > _highestLedger)
            {
                _highestLedger = snapshot.LedgerIndex;
                _advancedAt = now;
            }
            else if (now - _advancedAt >= Options.NodeOutageLimit)
            {
                stuck = true;
            }

            _latest = snapshot;
            _latestAt = now;
        }

        if (stuck)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable,
                $"the node's validated ledger has stayed at {snapshot.LedgerIndex} for {Options.NodeOutageLimit}");
        }

        return snapshot;
    }
```

In `src/XrplFile.Core/BatchSweep.cs`, in `FinalAsync`, wrap both `HistoryAsync(...)` calls in `BatchReads.RetryAsync(token => HistoryAsync(context.Source, <account>, start.LedgerIndex + 1, settled.LedgerIndex, token), context.Options, cancellationToken)`.

- [ ] **Step 5: Run the Batch tests three times**

Run three times: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchOutageTests -class XrplFile.Tests.BatchStrategyTests -class XrplFile.Tests.BatchIdentityTests -class XrplFile.Tests.BatchLimitsTests`
Expected: PASS every time.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/BatchReads.cs src/XrplFile.Core/BatchStrategyOptions.cs src/XrplFile.Core/BatchRound.cs src/XrplFile.Core/BatchSweep.cs tests/XrplFile.Core.Tests/BatchOutageTests.cs
git commit -m "feat(core): Batch rounds resend lost answers and stop on a node that stops"
```

---

### Task 12: The two-account estimate

Spec 4.5 (record, formulas), B26 (worst case), Review Focus 5.

**Files:**
- Create: `src/XrplFile.Core/BatchPublicationEstimate.cs`
- Modify: `src/XrplFile.Core/PublicationEstimate.cs` (`PublicationEstimator`)
- Test: `tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record BatchPublicationEstimate(long Chunks, int Blocks, int Lists, long Memos, int Batches, int SoloTransactions, long OpenLedgerFeeDrops, long FeeUnitDrops, long FeeCeilingDrops, long PayerFeeDrops, long WriterFeeDrops, long PayerFeeDropsWorstCase, long WriterFeeDropsWorstCase, long BaseReserveDrops, long OwnerReserveDrops, long WriterBalanceDrops, int WriterOwnerCount, long PayerBalanceDrops, int PayerOwnerCount)` with `WriterRequiredDrops`, `PayerRequiredDrops`, `WriterSpendableDrops`, `PayerSpendableDrops`, `WriterCovers`, `PayerCovers` exactly as spec 4.5.
  - `public static Task<BatchPublicationEstimate> PublicationEstimator.EstimateBatchAsync(ILedgerSource source, string writer, string payer, long fileBytes, CancellationToken cancellationToken)`
  - `internal static Task<BatchPublicationEstimate> PublicationEstimator.EstimateBatchAsync(ILedgerSource source, string writer, string payer, long chunks, int blocks, int lists, CancellationToken cancellationToken)` — prices `chunks` chunk memos, `lists` list memos and one manifest (used by a resume, Task 16).
  - `internal static int PublicationEstimator.BlockListMemoCount(int blocks)` (the existing private `BlockListMemos`, renamed and made internal).
  - `internal static void PublicationEstimator.RequireBatch(BatchPublicationEstimate estimate, long? budgetDrops)` — refusals 3–5 of spec 4.5.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchPublicationEstimateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FakeLedgerSource Node(long writerBalance = 100_000_000, long payerBalance = 100_000_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), OpenLedgerFee = new OpenLedgerFee(10, 10) };
        source.Balances["rWriter"] = writerBalance;
        source.Balances["rPayer"] = payerBalance;
        return source;
    }

    [Fact]
    public async Task The_seven_megabyte_file_of_the_testnet_run_is_priced_as_the_spec_says()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 7_123_692, Token);

        Assert.Equal(7124, estimate.Chunks);
        Assert.Equal(7, estimate.Blocks);
        Assert.Equal(1, estimate.Lists);
        Assert.Equal(7126, estimate.Memos);
        Assert.Equal(891, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal(15, estimate.FeeUnitDrops);
        Assert.Equal(30, estimate.FeeCeilingDrops);
        Assert.Equal((7124 + 3 * 891) * 15L, estimate.PayerFeeDrops);
        Assert.Equal(2 * 15L, estimate.WriterFeeDrops);
        Assert.Equal(53_445 * 30L, estimate.PayerFeeDropsWorstCase);
        Assert.Equal((7126 * 3 + 250) * 30L, estimate.WriterFeeDropsWorstCase);
        Assert.Equal(1_000_000 + 53_445 * 30L, estimate.PayerRequiredDrops);
    }

    [Fact]
    public async Task A_one_chunk_file_is_two_solo_transactions_and_costs_the_payer_nothing()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 500, Token);

        Assert.Equal(0, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal(0, estimate.PayerFeeDrops);
        Assert.Equal(30, estimate.WriterFeeDrops);
    }

    [Fact]
    public async Task Nine_chunks_are_two_batches_and_the_manifest_a_solo()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(Node(), "rWriter", "rPayer", 9_000, Token);

        Assert.Equal(2, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
        Assert.Equal((9 + 3 * 2) * 15L, estimate.PayerFeeDrops);
    }

    [Fact]
    public async Task Owned_objects_lock_reserve_out_of_each_accounts_spendable_balance()
    {
        FakeLedgerSource node = Node(writerBalance: 2_000_000, payerBalance: 3_000_000);
        node.OwnerCounts["rPayer"] = 5;

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(node, "rWriter", "rPayer", 10_000, Token);

        Assert.Equal(2_000_000, estimate.WriterSpendableDrops);
        Assert.Equal(3_000_000 - 5 * 200_000, estimate.PayerSpendableDrops);
        Assert.True(estimate.WriterCovers);
        Assert.True(estimate.PayerCovers);
    }

    [Fact]
    public void Refusals_come_writer_first_then_payer_then_budget()
    {
        BatchPublicationEstimate broke = new(10, 1, 0, 11, 2, 1, 10, 15, 30, 300, 15, 3000, 1000, 1_000_000, 200_000, 0, 0, 0, 0);
        BatchPublicationEstimate payerBroke = broke with { WriterBalanceDrops = 10_000_000 };
        BatchPublicationEstimate rich = payerBroke with { PayerBalanceDrops = 10_000_000 };

        Assert.Equal(XrplFileError.InsufficientBalance, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(broke, 1)).Error);
        Assert.Equal(XrplFileError.PayerInsufficientBalance, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(payerBroke, 1)).Error);
        Assert.Equal(XrplFileError.BudgetExceeded, Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireBatch(rich, 1)).Error);
        PublicationEstimator.RequireBatch(rich, null);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchPublicationEstimateTests`
Expected: build FAILS.

- [ ] **Step 3: Write the estimate**

`src/XrplFile.Core/BatchPublicationEstimate.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What a Batch publication will cost each of its two accounts (spec 4.5). The worst cases are true
/// bounds for one session under spec B13 and B15 (spec B26); a resume is a new session and is priced again.
/// </summary>
/// <param name="PayerFeeDrops">Σ (inners + 3) × unit over the batches, nothing retried.</param>
/// <param name="WriterFeeDrops">Solo transactions × unit, nothing retried.</param>
/// <param name="PayerFeeDropsWorstCase">ceil(Memos × 3 × 2.5) × ceiling: every memo in three candidates, in batches of two.</param>
/// <param name="WriterFeeDropsWorstCase">(Memos × 3 + 250) × ceiling: every memo three times as a solo, and a full tidy-up.</param>
public sealed record BatchPublicationEstimate(
    long Chunks, int Blocks, int Lists, long Memos,
    int Batches, int SoloTransactions,
    long OpenLedgerFeeDrops, long FeeUnitDrops, long FeeCeilingDrops,
    long PayerFeeDrops,
    long WriterFeeDrops,
    long PayerFeeDropsWorstCase,
    long WriterFeeDropsWorstCase,
    long BaseReserveDrops, long OwnerReserveDrops,
    long WriterBalanceDrops, int WriterOwnerCount,
    long PayerBalanceDrops, int PayerOwnerCount)
{
    public long WriterRequiredDrops => BaseReserveDrops + WriterFeeDropsWorstCase;

    public long PayerRequiredDrops => BaseReserveDrops + PayerFeeDropsWorstCase;

    public long WriterSpendableDrops => WriterBalanceDrops - WriterOwnerCount * OwnerReserveDrops;

    public long PayerSpendableDrops => PayerBalanceDrops - PayerOwnerCount * OwnerReserveDrops;

    public bool WriterCovers => WriterSpendableDrops >= WriterRequiredDrops;

    public bool PayerCovers => PayerSpendableDrops >= PayerRequiredDrops;
}
```

In `src/XrplFile.Core/PublicationEstimate.cs`, inside `PublicationEstimator`:
1. Rename `private static int BlockListMemos(int blocks)` to `internal static int BlockListMemoCount(int blocks)` and update its one caller in `EstimateAsync`.
2. Add:

```csharp
    public static Task<BatchPublicationEstimate> EstimateBatchAsync(
        ILedgerSource source, string writer, string payer, long fileBytes, CancellationToken cancellationToken)
    {
        if (fileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, "an empty file has no chunks and no root");
        }

        long chunks = FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes);
        int blocks = FileTree.BlockCount(chunks);
        int lists = blocks > 1 ? BlockListMemoCount(blocks) : 0;

        return EstimateBatchAsync(source, writer, payer, chunks, blocks, lists, cancellationToken);
    }

    /// <summary>
    /// Prices <paramref name="chunks"/> chunk memos, <paramref name="lists"/> list memos and the
    /// manifest, over the three passes the writer makes, each grouped by spec B11 (spec 4.5).
    /// </summary>
    internal static async Task<BatchPublicationEstimate> EstimateBatchAsync(
        ILedgerSource source, string writer, string payer, long chunks, int blocks, int lists, CancellationToken cancellationToken)
    {
        int batches = 0;
        int solos = 0;
        long batchedMemos = 0;

        foreach (long pass in new[] { chunks, lists, 1L })
        {
            if (pass == 0) continue;

            if (pass == 1)
            {
                solos++;
                continue;
            }

            batches += BatchGrouping.Sizes(checked((int)pass)).Count;
            batchedMemos += pass;
        }

        long memos = chunks + lists + 1;

        OpenLedgerFee open = await source.OpenLedgerFeeAsync(cancellationToken).ConfigureAwait(false);
        long unit = BatchFees.Unit(open);
        long ceiling = BatchFees.Ceiling(unit);

        NetworkFees fees = await source.FeesAsync(cancellationToken).ConfigureAwait(false);
        AccountState writerState = await source.AccountStateAsync(writer, cancellationToken).ConfigureAwait(false);
        AccountState payerState = await source.AccountStateAsync(payer, cancellationToken).ConfigureAwait(false);

        long payerFee = (batchedMemos + 3L * batches) * unit;
        long writerFee = solos * unit;

        // ceil(memos × 3 × 2.5) = ceil(memos × 7.5) = (15 × memos + 1) / 2 for a whole number of memos.
        long payerWorst = (memos * 15 + 1) / 2 * ceiling;
        long writerWorst = (memos * 3 + TicketStrategy.MaxLiveTickets) * ceiling;

        return new BatchPublicationEstimate(
            chunks, blocks, lists, memos, batches, solos,
            open.OpenLedgerFeeDrops, unit, ceiling,
            payerFee, writerFee, payerWorst, writerWorst,
            fees.BaseReserveDrops, fees.OwnerReserveDrops,
            writerState.BalanceDrops, writerState.OwnerCount,
            payerState.BalanceDrops, payerState.OwnerCount);
    }

    /// <summary>Spec 4.5's refusals 3–5: the writer's balance, the payer's, then the caller's budget against both requirements.</summary>
    internal static void RequireBatch(BatchPublicationEstimate estimate, long? budgetDrops)
    {
        if (!estimate.WriterCovers)
        {
            throw new XrplFileException(XrplFileError.InsufficientBalance,
                $"the writer needs {estimate.WriterRequiredDrops} drops and can spend {estimate.WriterSpendableDrops}");
        }

        if (!estimate.PayerCovers)
        {
            throw new XrplFileException(XrplFileError.PayerInsufficientBalance,
                $"the payer needs {estimate.PayerRequiredDrops} drops and can spend {estimate.PayerSpendableDrops}");
        }

        long required = estimate.WriterRequiredDrops + estimate.PayerRequiredDrops;

        if (budgetDrops is { } ceiling && required > ceiling)
        {
            throw new XrplFileException(XrplFileError.BudgetExceeded,
                $"the run needs {required} drops across both accounts and the budget allows {ceiling}");
        }
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchPublicationEstimateTests -class XrplFile.Tests.PublicationEstimateTests`
Expected: PASS. If `Lists` comes out other than 1 for 7 blocks, the list packer changed: recompute the expected figures in the first test from `BlockListMemoCount(7)` rather than changing the formulas.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/BatchPublicationEstimate.cs src/XrplFile.Core/PublicationEstimate.cs tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs
git commit -m "feat(core): the two-account Batch estimate and its worst case"
```

---
### Task 13: State version 3

Spec B16, 5.1.

**Files:**
- Modify: `src/XrplFile.Core/PublicationState.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationStateTests.cs`

**Interfaces:**
- Produces: `PublicationState` gains trailing optional positional parameters `string? Strategy = null`, `string? SourcePath = null`, `string? ManifestHash = null`, `string? Payer = null`, `string? PayerKeyReference = null`, `long? Fence = null`, each written only when not null; constants `CurrentVersion = 2`, `BatchVersion = 3`, `SequenceStrategyName = "sequence"`, `TicketStrategyName = "tickets"`, `BatchStrategyName = "batch"`. `Load` accepts 2 and 3 with the checks of spec 5.1.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationStateTests.cs`:

```csharp
using System.Text.Json;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationStateTests : IDisposable
{
    private static readonly string PayerAddress = XrplWallet.Generate().ClassicAddress;

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-state-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static PublicationState Version2(string? strategy) => new(
        PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "a.txt", 2500, 1000, "AB",
        42, "chunks", new Dictionary<int, string>(), Strategy: strategy);

    private static PublicationState Version3() => new(
        PublicationState.BatchVersion, "testnet", "rWriter", "key-1", "text/plain", "a.txt", 2500, 1000, "AB",
        42, "chunks", new Dictionary<int, string>(),
        Strategy: PublicationState.BatchStrategyName, SourcePath: @"C:\files\a.txt", Payer: PayerAddress, PayerKeyReference: "key-2", Fence: 0);

    private void WriteJson(string name, string json) => File.WriteAllText(PathOf(name), json);

    [Fact]
    public void A_version_2_state_keeps_its_strategy_and_writes_no_empty_new_fields()
    {
        Version2(PublicationState.TicketStrategyName).Save(PathOf("v2.json"));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PathOf("v2.json")));
        string[] properties = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        Assert.Contains("Strategy", properties);
        Assert.DoesNotContain("Payer", properties);
        Assert.DoesNotContain("PayerKeyReference", properties);
        Assert.DoesNotContain("Fence", properties);
        Assert.DoesNotContain("SourcePath", properties);
        Assert.DoesNotContain("ManifestHash", properties);
        Assert.Equal("tickets", PublicationState.Load(PathOf("v2.json")).Strategy);
    }

    [Fact]
    public void A_version_2_state_written_before_strategies_were_recorded_loads_with_none()
    {
        WriteJson("old.json", """
            {"Version":2,"Network":"testnet","Writer":"rWriter","KeyReference":"key-1","Type":"text/plain","Name":null,
             "Size":2500,"Chunk":1000,"Root":"AB","StartedInLedger":42,"Phase":"chunks","ChunkHashes":{}}
            """);

        Assert.Null(PublicationState.Load(PathOf("old.json")).Strategy);
    }

    [Fact]
    public void A_version_2_state_that_claims_to_be_a_batch_run_is_unreadable()
    {
        Version2("batch").Save(PathOf("v2batch.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("v2batch.json")));
    }

    [Fact]
    public void A_version_3_state_round_trips()
    {
        Version3().Save(PathOf("v3.json"));

        PublicationState loaded = PublicationState.Load(PathOf("v3.json"));

        Assert.Equal(PublicationState.BatchVersion, loaded.Version);
        Assert.Equal(PayerAddress, loaded.Payer);
        Assert.Equal("key-2", loaded.PayerKeyReference);
        Assert.Equal(0, loaded.Fence);
        Assert.Equal(@"C:\files\a.txt", loaded.SourcePath);
    }

    [Theory]
    [InlineData("no fence")]
    [InlineData("no payer")]
    [InlineData("no payer key")]
    [InlineData("payer is not an address")]
    [InlineData("not batch")]
    public void A_version_3_state_missing_or_misstating_a_required_field_is_unreadable(string defect)
    {
        PublicationState broken = defect switch
        {
            "no fence" => Version3() with { Fence = null },
            "no payer" => Version3() with { Payer = null },
            "no payer key" => Version3() with { PayerKeyReference = null },
            "payer is not an address" => Version3() with { Payer = "../../etc" },
            _ => Version3() with { Strategy = PublicationState.TicketStrategyName },
        };

        broken.Save(PathOf("broken.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("broken.json")));
    }

    [Fact]
    public void A_version_this_library_does_not_know_is_unreadable()
    {
        (Version3() with { Version = 4 }).Save(PathOf("v4.json"));

        Assert.Throws<FormatException>(() => PublicationState.Load(PathOf("v4.json")));
    }

    [Fact]
    public void Unfinished_lists_a_batch_run_as_readable_and_a_broken_one_as_unreadable()
    {
        Version3().Save(PathOf("good.json"));
        (Version3() with { Fence = null }).Save(PathOf("bad.json"));

        IReadOnlyList<UnfinishedRun> runs = PublicationSession.Unfinished(_directory);

        Assert.NotNull(Assert.Single(runs, run => run.Path.EndsWith("good.json", StringComparison.Ordinal)).State);
        Assert.NotNull(Assert.Single(runs, run => run.Path.EndsWith("bad.json", StringComparison.Ordinal)).Unreadable);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationStateTests`
Expected: build FAILS (`PublicationState` has no parameter `Strategy`).

- [ ] **Step 3: Change `PublicationState`**

In `src/XrplFile.Core/PublicationState.cs`:
1. Add `using Xrpl.AddressCodec;`.
2. Extend the record's parameter list after `IReadOnlyDictionary<int, string> ChunkHashes`:

```csharp
    IReadOnlyDictionary<int, string> ChunkHashes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Strategy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourcePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManifestHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PayerKeyReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Fence = null)
```

and add to the `<param>` documentation above the record:

```csharp
/// <param name="Strategy">
/// How the run submits (spec B16): "sequence" or "tickets" in version 2, absent in a version-2 file
/// written before this was recorded; "batch" in version 3. A run resumes on the strategy it recorded.
/// </param>
/// <param name="SourcePath">The absolute path the file was read from, so a resume need not ask for it (spec B17).</param>
/// <param name="ManifestHash">The manifest's hash once the run is done (spec B27).</param>
/// <param name="Payer">Version 3: the classic address that signs and pays for the outers.</param>
/// <param name="PayerKeyReference">Version 3: the payer's key, as the caller references it.</param>
/// <param name="Fence">
/// Version 3: the highest deadline any transmission of the run registered, written before that
/// transmission is first sent (spec B23); 0 before the first one.
/// </param>
```

3. Replace the body's constants and `Load`:

```csharp
    /// <summary>Sequence and ticket runs. See the remarks on why 2 (the root recorded at the start).</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Batch runs (spec B16). An application that predates them refuses this version, which is the
    /// point: it cannot resume a Batch run as a one-account run and erase its payer on save.
    /// </summary>
    public const int BatchVersion = 3;

    public const string SequenceStrategyName = "sequence";

    public const string TicketStrategyName = "tickets";

    public const string BatchStrategyName = "batch";
```

```csharp
    public static PublicationState Load(string path)
    {
        PublicationState? state = JsonSerializer.Deserialize<PublicationState>(File.ReadAllText(path), Options);

        if (state is null) throw new FormatException($"{path} did not deserialize as a publication state");

        if (state.Version == CurrentVersion)
        {
            if (state.Strategy is not (null or SequenceStrategyName or TicketStrategyName))
            {
                throw new FormatException($"a version {CurrentVersion} state records the strategy '{state.Strategy}', which is not one it can carry");
            }

            return state;
        }

        if (state.Version == BatchVersion)
        {
            if (!string.Equals(state.Strategy, BatchStrategyName, StringComparison.Ordinal))
            {
                throw new FormatException($"a version {BatchVersion} state must record the strategy '{BatchStrategyName}'");
            }

            if (state.Payer is null || !XrplCodec.IsValidClassicAddress(state.Payer))
            {
                throw new FormatException($"a version {BatchVersion} state must name its payer by classic address");
            }

            if (string.IsNullOrWhiteSpace(state.PayerKeyReference))
            {
                throw new FormatException($"a version {BatchVersion} state must name its payer's key");
            }

            if (state.Fence is null)
            {
                throw new FormatException($"a version {BatchVersion} state must record its fence");
            }

            return state;
        }

        throw new FormatException($"state version {state.Version} is not one this library reads");
    }
```

- [ ] **Step 4: Run the tests and the session suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationStateTests -class XrplFile.Tests.PublicationSessionTests`
Expected: PASS (nothing writes the new fields yet, so the pinned-fields test still holds).

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/PublicationState.cs tests/XrplFile.Core.Tests/PublicationStateTests.cs
git commit -m "feat(core): state version 3 for Batch runs, and strategy, source path and manifest on version 2"
```

---

### Task 14: The session — Batch estimate and start, the fence recorder, the report

Spec 4.1 (`IBatchFenceRecorder` is the session), 4.5 (session overloads and refusal order), 4.6, B16, B17, B23, B27 (`ManifestHash` written at "done"), B29.

**Files:**
- Create: `src/XrplFile.Core/BatchStrategyFactory.cs`
- Modify: `src/XrplFile.Core/PublicationSession.cs`, `src/XrplFile.Core/PublicationReport.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationSessionBatchTests.cs`; modify `tests/XrplFile.Core.Tests/PublicationSessionTests.cs` (the pinned-fields test, two new tests)

**Interfaces:**
- Consumes: `BatchStrategy`, `BatchBudget`, `IBatchFenceRecorder`, `BatchPublicationEstimate`, `PublicationEstimator.EstimateBatchAsync`/`RequireBatch`, `PublicationState` v3.
- Produces:
  - `public delegate BatchStrategy BatchStrategyFactory(BatchBudget budget, IBatchFenceRecorder fence);`
  - `SessionOptions.SourcePath` (`string?`), `SessionOptions.FencePoll` (`TimeSpan`, default 1 s), `SessionOptions.NodeOutageLimit` (`TimeSpan`, default 2 min)
  - `PublicationSession : IBatchFenceRecorder`
  - `static Task<BatchPublicationEstimate> PublicationSession.EstimateAsync(ILedgerSource source, string writer, string payer, long fileBytes, SessionOptions options, CancellationToken cancellationToken)` — refusals 1 (`BatchNotEnabled`) and 2 (`FeeTooHigh`)
  - `static Task<PublicationSession> PublicationSession.StartAsync(BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name, string writer, string keyReference, string payer, string payerKeyReference, string network, SessionOptions options, CancellationToken cancellationToken)`
  - `static string PublicationSession.RootOf(ReadOnlyMemory<byte> file)`
  - `PublicationReport.Payer` (`string?`), `PublicationReport.PayerFeeDrops` (`long?`)

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationSessionBatchTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionBatchTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-batch-session-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options(long? budget = null) =>
        new() { StateDirectory = _stateDirectory, BudgetDrops = budget, SourcePath = @"C:\files\s.txt", FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded(long writer = 100_000_000, long payer = 100_000_000)
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = writer;
        node.Ledger.Balances[node.Payer.Account] = payer;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, BatchStrategyOptions? options = null) =>
        (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, options ?? BatchTestNode.FastOptions);

    private Task<PublicationSession> StartAsync(BatchTestNode node, byte[] file, SessionOptions? options = null) =>
        PublicationSession.StartAsync(
            Factory(node), node.Ledger, file, "text/plain", "s.txt",
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", options ?? Options(), Token);

    [Fact]
    public async Task A_network_without_BatchV1_1_is_refused_first()
    {
        BatchTestNode node = Funded(writer: 0, payer: 0);
        node.Ledger.EnabledAmendments.Clear();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => StartAsync(node, VectorStream.Bytes("b", 9000)));

        Assert.Equal(XrplFileError.BatchNotEnabled, error.Error);
    }

    [Fact]
    public async Task An_open_ledger_fee_above_200_drops_is_refused_at_estimate_time()
    {
        BatchTestNode node = Funded(writer: 0, payer: 0);
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(201, 10);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.EstimateAsync(
            node.Ledger, node.Writer.Account, node.Payer.Account, 9000, Options(), Token));

        Assert.Equal(XrplFileError.FeeTooHigh, error.Error);
    }

    [Theory]
    [InlineData(0L, 100_000_000L, 0L, "InsufficientBalance")]
    [InlineData(100_000_000L, 0L, 0L, "PayerInsufficientBalance")]
    [InlineData(100_000_000L, 100_000_000L, 1L, "BudgetExceeded")]
    public async Task Balances_then_the_budget_are_refused_in_the_specs_order_before_any_state_exists(
        long writer, long payer, long budget, string expected)
    {
        BatchTestNode node = Funded(writer, payer);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => StartAsync(node, VectorStream.Bytes("b", 9000), Options(budget == 0 ? null : budget)));

        Assert.Equal(Enum.Parse<XrplFileError>(expected), error.Error);
        Assert.Empty(Directory.EnumerateFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_batch_run_writes_a_version_3_state_with_its_payer_source_and_a_zero_fence()
    {
        BatchTestNode node = Funded();

        PublicationSession session = await StartAsync(node, VectorStream.Bytes("b", 9000));

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.BatchVersion, state.Version);
        Assert.Equal(PublicationState.BatchStrategyName, state.Strategy);
        Assert.Equal(node.Payer.Account, state.Payer);
        Assert.Equal("key-p", state.PayerKeyReference);
        Assert.Equal(0, state.Fence);
        Assert.Equal(@"C:\files\s.txt", state.SourcePath);
    }

    [Fact]
    public async Task A_batch_run_publishes_a_file_that_reads_back_and_reports_what_the_payer_paid()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("batch", 20_000);
        PublicationSession session = await StartAsync(node, file);

        PublicationReport report = await session.RunAsync(null, Token);

        ReadResult read = await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token);
        Assert.Equal(file, read.Bytes);
        Assert.Equal(node.Writer.Account, read.Writer);

        long paid = 0;
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            paid += tx.Fee!.Value;
        }

        Assert.Equal(node.Payer.Account, report.Payer);
        Assert.Equal(paid, report.PayerFeeDrops);

        PublicationState done = PublicationState.Load(session.StatePath);
        Assert.Equal("done", done.Phase);
        Assert.Equal(report.ManifestHash, done.ManifestHash);
    }

    [Fact]
    public async Task The_state_file_holds_a_fence_past_every_deadline_before_that_deadline_is_sent()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("fence", 20_000));
        List<(long Fence, uint LastLedger)> seen = [];
        node.Sink.Transmitting = transmission =>
        {
            // The session's own view, set only after a save succeeded: opening the file here could make
            // the next fence save's move fail on Windows, and with it the run (spec B23).
            lock (seen) seen.Add((session.State.Fence ?? 0, transmission.LastLedger));
        };

        await session.RunAsync(null, Token);

        Assert.NotEmpty(seen);
        Assert.All(seen, pair => Assert.True(pair.Fence >= pair.LastLedger, $"fence {pair.Fence} behind {pair.LastLedger}"));
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_fails_the_run_before_anything_is_sent()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("blocked", 9000));
        Directory.CreateDirectory(session.StatePath + ".writing");

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => session.RunAsync(null, Token));

        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task A_factory_that_builds_a_strategy_for_another_payer_is_refused()
    {
        BatchTestNode node = Funded();
        ITransactionSigner stranger = new WalletSigner(XrplWallet.Generate());

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, stranger, budget, fence),
            node.Ledger, VectorStream.Bytes("b", 9000), "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options(), Token));
    }

    [Fact]
    public async Task A_one_chunk_file_costs_the_payer_nothing_and_the_writer_two_solos()
    {
        BatchTestNode node = Funded();
        PublicationSession session = await StartAsync(node, VectorStream.Bytes("one", 500));

        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(0L, report.PayerFeeDrops);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.TransactionType == "Batch");
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.TransactionType == "AccountSet" && t.Account == node.Writer.Account));
        Assert.Equal(2 * 15L, report.FeeDropsEstimated);
    }
}
```

In `tests/XrplFile.Core.Tests/PublicationSessionTests.cs`:
1. In `The_state_file_carries_exactly_the_fields_it_is_documented_to_carry`, change the expected list to end with `"ChunkHashes", "Strategy",` (a sequence run now records its strategy).
2. Add:

```csharp
    [Fact]
    public async Task A_run_records_its_strategy_and_where_its_bytes_came_from()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { FeeDrops = NodeFeeDrops });

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            new SessionOptions { StateDirectory = _stateDirectory, TicketsPerBatch = 100, SourcePath = @"D:\in\s.txt" },
            TestContext.Current.CancellationToken);

        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.CurrentVersion, state.Version);
        Assert.Equal(PublicationState.TicketStrategyName, state.Strategy);
        Assert.Equal(@"D:\in\s.txt", state.SourcePath);
    }

    [Fact]
    public async Task A_finished_run_records_its_manifest_hash()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, StrategyOptions);

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, VectorStream.Bytes("s", 2500), "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet",
            Options(), TestContext.Current.CancellationToken);
        PublicationReport report = await session.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(report.ManifestHash, PublicationState.Load(session.StatePath).ManifestHash);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionBatchTests -class XrplFile.Tests.PublicationSessionTests`
Expected: build FAILS (`SessionOptions.SourcePath`, the Batch overloads and `BatchStrategyFactory` do not exist).

- [ ] **Step 3: The factory, the options and the report**

`src/XrplFile.Core/BatchStrategyFactory.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// Builds a run's <see cref="BatchStrategy"/> once the session knows its budget, with the session as
/// its fence recorder (spec 4.1, 6.1). The session is what persists the fence, and it exists only
/// once the estimate has approved the run, so it builds the strategy rather than being handed one.
/// </summary>
public delegate BatchStrategy BatchStrategyFactory(BatchBudget budget, IBatchFenceRecorder fence);
```

In `SessionOptions` add:

```csharp
    /// <summary>The absolute path the bytes were read from, recorded so a resume need not ask for them (spec B17).</summary>
    public string? SourcePath { get; init; }

    /// <summary>How often a resume reads the ledger while it waits for the fence to pass (spec B23).</summary>
    public TimeSpan FencePoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long the fence wait tolerates a node that fails or a ledger that does not advance (Spec question 4).</summary>
    public TimeSpan NodeOutageLimit { get; init; } = TimeSpan.FromMinutes(2);
```

In `PublicationReport` add after `TicketReleaseFeeDropsInDoubt`:

```csharp
    /// <summary>The Batch run's payer, or null for a run with one account.</summary>
    public string? Payer { get; init; }

    /// <summary>
    /// Session scope: what the payer paid in this session — the <c>Fee</c> of every outer this session
    /// signed that settlement found applied, matched by hash (spec B29). Null when there is no payer,
    /// or when settlement could not attribute it; never a guess.
    /// </summary>
    public long? PayerFeeDrops { get; init; }
```

and add to the record's `<param name="FeeDropsEstimated">` documentation: "For a Batch run this is the writer's part only — its solo transactions at the approved unit — and <see cref="PayerFeeDrops"/> is what the payer paid; <see cref="Transactions"/> then counts the memos (chunks, lists, manifest), inners and solos alike."

- [ ] **Step 4: The session**

In `src/XrplFile.Core/PublicationSession.cs`:

1. Declare `public sealed class PublicationSession : IBatchFenceRecorder`.
2. Make these fields mutable and nullable where marked: `private ISubmissionStrategy? _strategy;`, `private long _feeDropsPerTransaction;`, and add:

```csharp
    /// <summary>The Batch estimate the run was approved against, or null for a one-account run.</summary>
    private BatchPublicationEstimate? _batchEstimate;

    /// <summary>The strategy; a session resumed onto a publication that already finished has none (spec B27).</summary>
    private ISubmissionStrategy Strategy =>
        _strategy ?? throw new InvalidOperationException("this session finished a publication without submitting and has no strategy");
```

The private constructor's first parameter becomes `ISubmissionStrategy? strategy`. Replace every read of `_strategy` in `RunAsync` and `ReleaseLeftoverTicketsAsync` with `Strategy` — the member accesses (`_strategy.TicketMints`, `_strategy.ReleaseLeftoverTicketsAsync`) and the field passed whole (`new XrplFileWriter(_strategy, …)`), or the nullable field breaks the build (CS8604 under `TreatWarningsAsErrors`).

3. Add the Batch estimate and start, and the root helper, after the existing `EstimateAsync`:

```csharp
    /// <summary>
    /// What a Batch run would cost each account (spec 4.5), refusing first a network that does not
    /// enable <c>BatchV1_1</c>, then an open-ledger fee above 200 drops. The amendment is read again
    /// here, before every run: this read decides, not a cached one (spec B14).
    /// </summary>
    public static async Task<BatchPublicationEstimate> EstimateAsync(
        ILedgerSource source, string writer, string payer, long fileBytes, SessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        await RequireBatchEnabledAsync(source, cancellationToken).ConfigureAwait(false);

        BatchPublicationEstimate estimate = await PublicationEstimator
            .EstimateBatchAsync(source, writer, payer, fileBytes, cancellationToken).ConfigureAwait(false);

        RequireFeeWithinLimit(estimate);

        return estimate;
    }

    /// <summary>
    /// Starts a Batch run (spec 4.5): estimates, refuses in the spec's order, builds the strategy
    /// through <paramref name="strategyFactory"/> with this session as its fence recorder, and writes
    /// a version-3 state file -- all before a single transaction is submitted.
    /// </summary>
    public static async Task<PublicationSession> StartAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string payer, string payerKeyReference, string network, SessionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategyFactory);
        ArgumentNullException.ThrowIfNull(options);
        RequireNameableAccount(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerKeyReference);

        if (string.Equals(payer, writer, StringComparison.Ordinal))
        {
            throw new ArgumentException("a Batch run's payer is another account than its writer (spec B1)", nameof(payer));
        }

        BatchPublicationEstimate estimate = await EstimateAsync(source, writer, payer, file.Length, options, cancellationToken).ConfigureAwait(false);
        PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);

        long startedIn = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);

        PublicationState state = new(
            PublicationState.BatchVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, RootOf(chunks, startedIn), startedIn, Phase: Stages[0],
            ChunkHashes: new Dictionary<int, string>(),
            Strategy: PublicationState.BatchStrategyName, SourcePath: options.SourcePath,
            Payer: payer, PayerKeyReference: payerKeyReference, Fence: 0);

        string path = NewStatePath(options, startedIn, writer);
        PublicationSession session = new(null, source, file, state, path, options, 0, 0, mintsAtStart: 0);

        session.AttachBatch(strategyFactory, estimate, resuming: false);
        state.Save(path);

        return session;
    }

    /// <summary>The root a file will publish under: over block hashes alone, as the writer computes it.</summary>
    public static string RootOf(ReadOnlyMemory<byte> file) => RootOf(FileTree.Split(file, MemoLayout.ChunkBytes), 0);

    private static async Task RequireBatchEnabledAsync(ILedgerSource source, CancellationToken cancellationToken)
    {
        if (!await source.IsAmendmentEnabledAsync(Amendments.Batch, cancellationToken).ConfigureAwait(false))
        {
            throw new XrplFileException(XrplFileError.BatchNotEnabled,
                $"this network's validated ledger does not enable {Amendments.Batch}");
        }
    }

    private static void RequireFeeWithinLimit(BatchPublicationEstimate estimate)
    {
        if (estimate.OpenLedgerFeeDrops > BatchFees.MaxOpenLedgerFeeDrops)
        {
            throw new XrplFileException(XrplFileError.FeeTooHigh,
                $"the open-ledger fee is {estimate.OpenLedgerFeeDrops} drops, above the {BatchFees.MaxOpenLedgerFeeDrops} a run starts at");
        }
    }

    /// <summary>
    /// Builds the run's strategy with this session as its fence recorder, and holds it to the state:
    /// the same writer, the same payer, and the estimate's unit (spec 4.1).
    /// </summary>
    private void AttachBatch(BatchStrategyFactory strategyFactory, BatchPublicationEstimate estimate, bool resuming)
    {
        BatchBudget budget = new(
            estimate.FeeUnitDrops, estimate.FeeCeilingDrops, estimate.PayerFeeDropsWorstCase, estimate.WriterFeeDropsWorstCase);

        BatchStrategy strategy = strategyFactory(budget, this);

        if (!string.Equals(strategy.Account, _state.Writer, StringComparison.Ordinal)
            || !string.Equals(strategy.Payer, _state.Payer, StringComparison.Ordinal))
        {
            string message = $"the run is recorded for writer {_state.Writer} and payer {_state.Payer}, and the strategy signs as "
                + $"{strategy.Account} and {strategy.Payer}";

            if (resuming) throw new XrplFileException(XrplFileError.ResumeMismatch, message);

            throw new ArgumentException(message, nameof(strategyFactory));
        }

        if (strategy.FeeDropsPerTransaction != estimate.FeeUnitDrops)
        {
            throw new XrplFileException(XrplFileError.FeeMismatch,
                $"the estimate approved a unit of {estimate.FeeUnitDrops} drops and the strategy charges {strategy.FeeDropsPerTransaction}");
        }

        _strategy = strategy;
        _batchEstimate = estimate;
        _feeDropsPerTransaction = estimate.FeeUnitDrops;
    }

    /// <summary>
    /// Persists the fence (spec B23) before the transmission it covers is first sent. A save that
    /// fails throws, which stops that transmission and fails the run.
    /// </summary>
    Task IBatchFenceRecorder.RecordFenceAsync(long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_state.Fence is { } recorded && recorded >= deadline) return Task.CompletedTask;

            PublicationState next = _state with { Fence = deadline };
            next.Save(StatePath);
            _state = next;
        }

        return Task.CompletedTask;
    }

    /// <summary>One run, one file: a ledger-and-writer name with a random suffix (see StartAsync's remarks).</summary>
    private static string NewStatePath(SessionOptions options, long startedIn, string writer)
    {
        string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return Path.Combine(options.StateDirectory, $"{startedIn}-{writer}-{suffix}.json");
    }

    /// <summary>The name a version-2 state records for the strategies this library ships; null for anything else.</summary>
    private static string? StrategyName(ISubmissionStrategy strategy) => strategy switch
    {
        TicketStrategy => PublicationState.TicketStrategyName,
        SequenceStrategy => PublicationState.SequenceStrategyName,
        _ => null,
    };
```

4. In the existing `StartAsync`, build the state with the two new fields, and use `NewStatePath`:

```csharp
        PublicationState state = new(
            PublicationState.CurrentVersion, network, writer, keyReference, type, name,
            file.Length, MemoLayout.ChunkBytes, root, startedIn, Phase: Stages[0],
            ChunkHashes: new Dictionary<int, string>(),
            Strategy: StrategyName(strategy), SourcePath: options.SourcePath);
```

and replace the two lines computing `suffix` and `path` with `string path = NewStatePath(options, startedIn, writer);` (keep the comment above them).

5. `Advance` gains a fourth parameter and records the hash:

```csharp
    private void Advance(string phase, string? root, IReadOnlyList<string>? chunkHashes, string? manifestHash = null)
```

with `ManifestHash = manifestHash ?? _state.ManifestHash,` added to its `with` expression.

6. In `RunAsync`, call `Advance("done", result.Manifest.Root, result.ChunkHashes, result.ManifestHash);`, and replace the report construction with:

```csharp
            int mints = _mintsAtStart is { } before ? Strategy.TicketMints - before : _mints;
            long transactions = result.ChunkHashes.Count + result.ListHashes.Count + 1 + mints;

            // The writer's fees, counted as today (spec 4.6): on the Batch path its solo transactions at
            // the approved unit, nothing retried; the payer's measured spend is PayerFeeDrops (Spec question 9).
            long feeEstimated = _batchEstimate is { } batch
                ? batch.WriterFeeDrops
                : transactions * _feeDropsPerTransaction;

            return new PublicationReport(
                result.Link, result.Manifest, result.Writer,
                transactions, feeEstimated,
                _startedAt, DateTimeOffset.UtcNow,
                result.ChunkHashes, result.ListHashes, result.ManifestHash)
            {
                StateSaveFailure = StateSaveFailure,
                TicketsReleased = counts.Released,
                TicketsAlreadyGone = counts.AlreadyGone,
                TicketsNotReleased = counts.NotReleased,
                TicketReleaseFailure = releaseFailure,
                TicketReleaseFeeDrops = release is null ? null : release.Charged * _feeDropsPerTransaction,
                TicketReleaseFeeDropsInDoubt = release is null ? null : release.InDoubt * _feeDropsPerTransaction,
                Payer = started.Payer,
                PayerFeeDrops = (Strategy as BatchStrategy)?.PayerFeeDrops,
            };
```

7. Update the `<summary>` of `ResumeAsync` (one-account overload) only where it says the strategy is handed in; nothing else changes in it in this task.

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionBatchTests -class XrplFile.Tests.PublicationSessionTests -class XrplFile.Tests.PublicationSessionStandaloneTests -trait- "Category=Integration"`
Expected: PASS.

- [ ] **Step 6: Run every Core unit test and commit**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` — Expected: PASS.

```bash
git add src/XrplFile.Core/BatchStrategyFactory.cs src/XrplFile.Core/PublicationSession.cs src/XrplFile.Core/PublicationReport.cs tests/XrplFile.Core.Tests/PublicationSessionBatchTests.cs tests/XrplFile.Core.Tests/PublicationSessionTests.cs
git commit -m "feat(core): start a Batch publication with the session as its fence"
```

---

### Task 15: Manifest reconciliation on every resume

Spec B27, 5.2 steps 3–4, 7.4's B27 cases (unit half). One manifest builder is shared by the writer and the session, so the session looks for exactly the bytes the writer would send.

**Files:**
- Create: `src/XrplFile.Core/ManifestPlan.cs`
- Modify: `src/XrplFile.Core/XrplFileWriter.cs`, `src/XrplFile.Core/PublicationSession.cs` (`ResumeAsync`, `RunAsync`, new `FindManifestAsync`, `RequireResumable`)
- Test: `tests/XrplFile.Core.Tests/ManifestReconciliationTests.cs`, `tests/XrplFile.Core.Tests/ScriptedStrategy.cs`, `tests/XrplFile.Core.Tests/WriterLedgerTests.cs`

**Interfaces:**
- Produces:
  - internal `static class ManifestPlan`: `IReadOnlyList<BlockEntry> Entries(IReadOnlyList<byte[]> chunks, IReadOnlyDictionary<int, long> chunkLedgers)`, `IReadOnlyList<MemoSubmission> ListMemos(IReadOnlyList<BlockEntry> entries)`, `Manifest Build(string type, string? name, long size, IReadOnlyList<BlockEntry> entries, long firstLedger, long lastLedger, int lists)`, `MemoSubmission Memo(Manifest manifest)`
  - `PublicationSession` private: `PublishResult? _alreadyPublished`; `Task<PublishResult?> FindManifestAsync(IReadOnlyList<byte[]> chunks, LandedMemos landed, CancellationToken)`; `static IReadOnlyList<byte[]> RequireResumable(PublicationState state, string statePath, ReadOnlyMemory<byte> file)`
  - Test helper `ScriptedStrategy(ISubmissionStrategy inner)` with `int PauseAtCall`, `int FailAtCall`, `Task Paused`, `void Resume()`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/ScriptedStrategy.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>A strategy that can stop at one of its SubmitAllAsync calls (1-based): hold there until resumed, or die there.</summary>
internal sealed class ScriptedStrategy(ISubmissionStrategy inner) : ISubmissionStrategy
{
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public int PauseAtCall { get; init; }

    public int FailAtCall { get; init; }

    public Task Paused => _paused.Task;

    public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

    public string Account => inner.Account;

    public int TicketMints => inner.TicketMints;

    public void Resume() => _resume.TrySetResult();

    public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);

        if (call == FailAtCall) throw new XrplFileException(XrplFileError.NodeUnreachable, "scripted: the process died here");

        if (call == PauseAtCall)
        {
            _paused.TrySetResult();
            await _resume.Task.WaitAsync(cancellationToken);
        }

        return await inner.SubmitAllAsync(memos, submitted, cancellationToken);
    }

    public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) => inner.ReleaseLeftoverTicketsAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
```

`tests/XrplFile.Core.Tests/ManifestReconciliationTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec B27: a resume finds the manifest a dead run already sent, and only that one.</summary>
public class ManifestReconciliationTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-b27-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    /// <summary>What a crash between the manifest validating and "done" being written leaves on disk.</summary>
    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null, ChunkHashes = new Dictionary<int, string>() }).Save(statePath);
    }

    [Fact]
    public async Task A_run_that_died_after_its_manifest_validated_is_finished_by_the_resume_without_sending()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27", 2500);

        await using SequenceStrategy first = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession run = await PublicationSession.StartAsync(
            first, ledger, file, "text/plain", "f.txt", Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);
        int submitted = sink.Submitted.Count;

        // Drained: the resume needs no money when there is nothing to send.
        ledger.Balances[Wallet.ClassicAddress] = 0;

        await using SequenceStrategy second = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession resumed = await PublicationSession.ResumeAsync(second, ledger, file, run.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal("done", PublicationState.Load(run.StatePath).Phase);
        Assert.Equal(original.ManifestHash, PublicationState.Load(run.StatePath).ManifestHash);
    }

    [Fact]
    public async Task The_ticket_path_finds_the_manifest_too()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27-tickets", 30_000);
        TicketStrategyOptions options = new() { FeeDrops = Fee, SweepPoll = TimeSpan.FromMilliseconds(1), ValidationPoll = TimeSpan.FromMilliseconds(1) };

        await using TicketStrategy first = new(sink, ledger, Signer, options);
        PublicationSession run = await PublicationSession.StartAsync(
            first, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);
        int submitted = sink.Submitted.Count;

        await using TicketStrategy second = new(sink, ledger, Signer, options);
        PublicationSession resumed = await PublicationSession.ResumeAsync(second, ledger, file, run.StatePath, Options, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
    }

    [Fact]
    public async Task Another_publication_of_the_same_bytes_with_the_same_root_is_not_adopted()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("b27-twins", 2500);

        // Run one sends its chunks and stops before its manifest.
        ScriptedStrategy one = new(new SequenceStrategy(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee })) { PauseAtCall = 2 };
        PublicationSession first = await PublicationSession.StartAsync(
            one, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);
        Task<PublicationReport> firstRun = first.RunAsync(null, Token);
        await one.Paused.WaitAsync(TimeSpan.FromSeconds(20), Token);

        // Run two starts after run one's chunks, so its window holds run one's manifest but not its chunks.
        sink.CloseLedger();
        ledger.CurrentLedger += 1;
        ScriptedStrategy two = new(new SequenceStrategy(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee })) { FailAtCall = 2 };
        PublicationSession second = await PublicationSession.StartAsync(
            two, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key-1", "testnet", Options, Token);

        one.Resume();
        PublicationReport firstReport = await firstRun;
        Assert.True(firstReport.Manifest.FirstLedger < second.State.StartedInLedger);

        // Run two sends its own chunks and dies before its manifest.
        await Assert.ThrowsAsync<XrplFileException>(() => second.RunAsync(null, Token));

        await using SequenceStrategy again = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        PublicationSession resumed = await PublicationSession.ResumeAsync(again, ledger, file, second.StatePath, Options, Token);
        PublicationReport secondReport = await resumed.RunAsync(null, Token);

        Assert.Equal(firstReport.Manifest.Root, secondReport.Manifest.Root);
        Assert.NotEqual(firstReport.Link, secondReport.Link);
        Assert.Equal(file, (await new XrplFileReader(ledger).ReadAsync(secondReport.Link, Token)).Bytes);
    }
}
```

`tests/XrplFile.Core.Tests/WriterLedgerTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec question 23: a strategy that says where each memo landed spares the writer a lookup per memo.</summary>
public class WriterLedgerTests
{
    [Fact]
    public async Task A_strategy_that_reports_ledgers_spares_the_writer_a_lookup_per_chunk()
    {
        BatchTestNode node = new();
        LookupRecordingSource source = new(node.Ledger);
        await using BatchStrategy strategy = node.Strategy();
        XrplFileWriter writer = new(strategy, source, new WriterOptions { ReadBack = false });

        PublishResult result = await writer.PublishAsync(
            VectorStream.Bytes("ledgers", 20_000), "text/plain", null, node.Writer.Account, "testnet", null, null,
            TestContext.Current.CancellationToken);

        Assert.Empty(source.LookedUp.Intersect(result.ChunkHashes, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Forwards every read and remembers which hashes were looked up one by one.</summary>
    private sealed class LookupRecordingSource(ILedgerSource inner) : ILedgerSource
    {
        private readonly List<string> _lookedUp = [];

        public IReadOnlyList<string> LookedUp
        {
            get { lock (_lookedUp) return [.. _lookedUp]; }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
        {
            lock (_lookedUp) _lookedUp.Add(hash);
            return inner.TransactionAsync(hash, cancellationToken);
        }

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
            inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);

        public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) => inner.SnapshotAsync(accounts, cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) => inner.IsAmendmentEnabledAsync(name, cancellationToken);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ManifestReconciliationTests -class XrplFile.Tests.WriterLedgerTests`
Expected: the first two FAIL (`ResumeAsync` throws `InsufficientBalance` in the first; the second sends a second manifest, so `Submitted.Count` grows); the third passes or fails on ordering — it must pass after Step 5; `WriterLedgerTests` FAILS (the writer looks every chunk up).

- [ ] **Step 3: Extract `ManifestPlan` and use it in the writer**

`src/XrplFile.Core/ManifestPlan.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// How a manifest is built from what landed: one place, so a resume looks for exactly the bytes the
/// writer would send (spec B27).
/// </summary>
internal static class ManifestPlan
{
    /// <summary>Block entries: hash over the chunks in memory, first ledger the earliest of the block's own chunks.</summary>
    public static IReadOnlyList<BlockEntry> Entries(IReadOnlyList<byte[]> chunks, IReadOnlyDictionary<int, long> chunkLedgers)
    {
        int blocks = FileTree.BlockCount(chunks.Count);
        List<BlockEntry> entries = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            int firstChunk = block * FileTree.BlockChunks;
            List<byte[]> members = [.. chunks.Skip(firstChunk).Take(FileTree.BlockChunks)];
            long ledger = Enumerable.Range(firstChunk, members.Count).Min(i => chunkLedgers[i]);
            entries.Add(new BlockEntry(block, Convert.ToHexString(FileTree.BlockHash(members)), ledger));
        }

        return entries;
    }

    /// <summary>The block-list memos, only when there is more than one block.</summary>
    public static IReadOnlyList<MemoSubmission> ListMemos(IReadOnlyList<BlockEntry> entries) =>
        entries.Count <= 1
            ? []
            : [.. FileTree.EncodeEntries(entries).Select((payload, j) =>
                new MemoSubmission(MemoLayout.ListMarkerHex(j), MemoLayout.Utf8Hex(payload), MemoLayout.JsonFormatHex))];

    public static Manifest Build(
        string type, string? name, long size, IReadOnlyList<BlockEntry> entries, long firstLedger, long lastLedger, int lists) =>
        new(type, size, MemoLayout.ChunkBytes, FileTree.RootHex(entries), firstLedger, lastLedger, entries.Count, lists, name);

    public static MemoSubmission Memo(Manifest manifest) =>
        new(MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex);
}
```

In `src/XrplFile.Core/XrplFileWriter.cs`, `PublishCoreAsync`:
1. Replace the block-entries loop (from `List<BlockEntry> entries = new(blocks);` through the closing brace of its `for`) and `string root = FileTree.RootHex(entries);` with:

```csharp
        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(chunks, chunkLedgers);
```

2. Inside `if (blocks > 1)`, replace the two lines building `payloads` and `listMemos` with:

```csharp
            IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);
```

3. Replace `Manifest manifest = new(type, file.Length, MemoLayout.ChunkBytes, root, firstLedger, lastLedger, blocks, listHashes.Count, name);` with:

```csharp
        Manifest manifest = ManifestPlan.Build(type, name, file.Length, entries, firstLedger, lastLedger, listHashes.Count);
```

4. Replace the manifest submission's memo `new MemoSubmission(MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex)` with `ManifestPlan.Memo(manifest)`.

5. Use the ledger a strategy reports instead of one `tx` lookup per memo (Spec question 23). In the chunk loop replace `chunkLedgers[index] = await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);` with:

```csharp
                chunkLedgers[index] = memo.LedgerIndex ?? await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);
```

and in the list loop replace `listLedgers[index] = await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);` with:

```csharp
                    listLedgers[index] = memo.LedgerIndex ?? await LedgerOfAsync(memo.TxHash, cancellationToken).ConfigureAwait(false);
```

A strategy reports a ledger only from a validated tesSUCCESS record it matched (B28), which is what the lookup would have checked.

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.XrplFileWriterTests -class XrplFile.Tests.VectorTests -class XrplFile.Tests.WriterLedgerTests`
Expected: PASS (items 1–4 are a pure refactor; item 5 makes `WriterLedgerTests` pass).

- [ ] **Step 4: Look for the manifest, and finish without sending when it is found**

In `src/XrplFile.Core/PublicationSession.cs`:

1. Add the field `private PublishResult? _alreadyPublished;`.

2. Add:

```csharp
    /// <summary>
    /// The manifest this run would send, if the ledger already holds it (spec B27): when every chunk
    /// and list has landed, the manifest is built from the reconciled hashes and ledgers, and a
    /// validated tesSUCCESS transaction of the writer from <see cref="PublicationState.StartedInLedger"/>
    /// on carrying exactly those bytes is the one. A manifest with the same root and other bytes is
    /// another publication's and is not adopted.
    /// </summary>
    private async Task<PublishResult?> FindManifestAsync(IReadOnlyList<byte[]> chunks, LandedMemos landed, CancellationToken cancellationToken)
    {
        if (landed.Chunks.Count != chunks.Count) return null;

        Dictionary<int, long> chunkLedgers = landed.Chunks.ToDictionary(pair => pair.Key, pair => pair.Value.LedgerIndex);
        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(chunks, chunkLedgers);
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);
        List<string> listHashes = [];
        List<long> listLedgers = [];

        for (int j = 0; j < listMemos.Count; j++)
        {
            if (!landed.Lists.TryGetValue(j, out LandedMemo? list)
                || !string.Equals(list.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            listHashes.Add(list.TxHash);
            listLedgers.Add(list.LedgerIndex);
        }

        long firstLedger = chunkLedgers.Values.Min();
        long lastLedger = listLedgers.Count == 0 ? chunkLedgers.Values.Max() : Math.Max(chunkLedgers.Values.Max(), listLedgers.Max());
        PublicationState state = State;
        Manifest manifest = ManifestPlan.Build(state.Type, state.Name, state.Size, entries, firstLedger, lastLedger, listMemos.Count);
        string data = ManifestPlan.Memo(manifest).MemoDataHex;

        long to = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in _source
            .AccountTransactionsAsync(state.Writer, state.StartedInLedger, to, forward: true, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!tx.Validated || !tx.Succeeded || !string.Equals(tx.Account, state.Writer, StringComparison.Ordinal)) continue;
            if (!MemoLayout.IsManifest(tx.MemoTypeHex) || !string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;

            List<string> chunkHashes = [.. Enumerable.Range(0, chunks.Count).Select(i => landed.Chunks[i].TxHash)];

            return new PublishResult(new XrplFileLink(state.Network, tx.Hash), manifest, state.Writer, chunkHashes, listHashes, tx.Hash);
        }

        return null;
    }

    /// <summary>The checks every resume makes before it reads the ledger; returns the file's chunks.</summary>
    private static IReadOnlyList<byte[]> RequireResumable(PublicationState state, string statePath, ReadOnlyMemory<byte> file)
    {
        if (string.Equals(state.Phase, "done", StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} describes a run that finished; publishing it again would put a second copy in the ledger");
        }

        if (state.Size != file.Length)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for {state.Size} bytes and this file is {file.Length}");
        }

        if (state.Chunk != MemoLayout.ChunkBytes)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started at {state.Chunk} bytes per chunk and this library writes {MemoLayout.ChunkBytes}");
        }

        IReadOnlyList<byte[]> chunks = FileTree.Split(file, state.Chunk);

        if (state.Root is not { } recorded)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records no root, so there is nothing to check this file against");
        }

        string root = RootOf(chunks, state.StartedInLedger);

        if (!string.Equals(recorded, root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for a file rooted at {recorded} and this one roots at {root}");
        }

        return chunks;
    }

    /// <summary>A picked file at a new place is where the next resume reads from (spec B17).</summary>
    private void RecordSourcePath(string? sourcePath)
    {
        if (sourcePath is null) return;

        lock (_stateLock)
        {
            if (string.Equals(_state.SourcePath, sourcePath, StringComparison.Ordinal)) return;

            _state = _state with { SourcePath = sourcePath };
            _state.Save(StatePath);
        }
    }
```

3. Replace the body of the one-account `ResumeAsync` after the writer check with (spec 5.2 order: the file, reconciliation, done already?, then the estimate):

```csharp
        // A Batch run resumes with its payer, on its own overload (spec B16).
        if (state.Version == PublicationState.BatchVersion)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records a Batch run, which resumes with its payer rather than on one account");
        }

        IReadOnlyList<byte[]> chunks = RequireResumable(state, statePath, file);

        PublicationSession session = new(
            strategy, source, file, state, statePath, options, 0, 0, mintsAtStart: null);
        session.RecordSourcePath(options.SourcePath);
        session._landed = await session.ReconcileAsync(chunks, cancellationToken).ConfigureAwait(false);

        // Nothing is sent, so nothing has to be affordable (spec B27).
        if (await session.FindManifestAsync(chunks, session._landed, cancellationToken).ConfigureAwait(false) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }

        PublicationEstimate estimate = await PublicationEstimator
            .EstimateAsync(source, state.Writer, file.Length, options.TicketsPerBatch, cancellationToken).ConfigureAwait(false);

        if (strategy.FeeDropsPerTransaction != estimate.FeeDropsPerTransaction)
        {
            throw new XrplFileException(XrplFileError.FeeMismatch,
                $"the estimate prices every transaction at {estimate.FeeDropsPerTransaction} drops and the strategy "
                + $"charges {strategy.FeeDropsPerTransaction}; build the strategy from the estimate's fee, or the "
                + "budget and balance checks describe a run nobody is going to submit");
        }

        if (strategy is TicketStrategy) PublicationEstimator.RequireTicketSlot(estimate);
        PublicationEstimator.Require(estimate, options.BudgetDrops);

        session._feeDropsPerTransaction = estimate.FeeDropsPerTransaction;
        session._mints = estimate.Mints;

        return session;
```

(`_mints` becomes a mutable field too: drop its `readonly`.) Update the method's `<summary>`: the ledger is reconciled and a finished manifest looked for before the run is priced; the price covers the whole file as before.

4. At the start of `RunAsync`'s `try` block, before the writer is built, add:

```csharp
            if (_alreadyPublished is { } done)
            {
                // The dead run's manifest is in the ledger: record "done" and send nothing, the
                // tidy-up included (spec B27). The fees were spent by the sessions before this one.
                Advance("done", done.Manifest.Root, done.ChunkHashes, done.ManifestHash);

                return new PublicationReport(
                    done.Link, done.Manifest, done.Writer,
                    done.ChunkHashes.Count + done.ListHashes.Count + 1, 0,
                    _startedAt, DateTimeOffset.UtcNow, done.ChunkHashes, done.ListHashes, done.ManifestHash)
                {
                    StateSaveFailure = StateSaveFailure,
                    Payer = State.Payer,
                };
            }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ManifestReconciliationTests -class XrplFile.Tests.PublicationSessionTests -class XrplFile.Tests.XrplFileWriterTests`
Expected: PASS. `A_resumed_ticketed_run_with_no_free_slot_never_starts` still passes: its run never sent a manifest, so the slot check is reached.

- [ ] **Step 6: Commit**

```bash
git add src/XrplFile.Core/ManifestPlan.cs src/XrplFile.Core/XrplFileWriter.cs src/XrplFile.Core/PublicationSession.cs tests/XrplFile.Core.Tests/ManifestReconciliationTests.cs tests/XrplFile.Core.Tests/ScriptedStrategy.cs tests/XrplFile.Core.Tests/WriterLedgerTests.cs
git commit -m "feat(core): a resume finds the manifest a dead run already sent"
```

---

### Task 16: Resuming a Batch run — the fence, the payer, the missing part

Spec B18, B23, B26 (a resume is a new session), 5.2, Review Focus 2.

**Files:**
- Modify: `src/XrplFile.Core/PublicationSession.cs`
- Test: `tests/XrplFile.Core.Tests/PublicationSessionBatchResumeTests.cs`

**Interfaces:**
- Consumes: Tasks 14 and 15.
- Produces: `static Task<PublicationSession> PublicationSession.ResumeAsync(BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string statePath, string payer, string payerKeyReference, SessionOptions options, IProgress<PublishProgress>? fenceProgress, CancellationToken cancellationToken)`. Fence progress is reported as `PublishProgress("fence", ledgersPassed, ledgersToWait)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationSessionBatchResumeTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class PublicationSessionBatchResumeTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-batch-resume-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, ITransactionSigner payer, List<BatchBudget>? budgets = null) =>
        (budget, fence) =>
        {
            budgets?.Add(budget);
            return new BatchStrategy(node.Sink, node.Ledger, node.Writer, payer, budget, fence, BatchTestNode.FastOptions);
        };

    /// <summary>A Batch run stopped after a few transmissions, as closing the window stops it.</summary>
    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node, int afterTransmissions = 2)
    {
        byte[] file = VectorStream.Bytes("resume", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal + 1 >= afterTransmissions) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;

        return (session, file);
    }

    [Fact]
    public async Task A_resume_reads_nothing_until_a_snapshot_is_past_the_fence_and_two_ledgers()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        long fence = PublicationState.Load(first.StatePath).Fence!.Value;
        Assert.True(fence > 0);
        int callsBefore = node.Ledger.AccountTransactionsCalls;
        List<PublishProgress> waited = [];

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(waited.Add), Token);

        AccountTransactionsCall firstRead = node.Ledger.Calls[callsBefore];
        Assert.True(firstRead.ToLedger > fence + 2, $"read at {firstRead.ToLedger} with the fence at {fence}");
        Assert.NotEmpty(waited);
        Assert.All(waited, progress => Assert.Equal("fence", progress.Stage));

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_new_payer_is_written_to_the_state_only_after_the_fence_and_pays_for_everything_after()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        ITransactionSigner newPayer = new WalletSigner(XrplWallet.Generate());
        node.Ledger.Balances[newPayer.Account] = 100_000_000;
        node.Sink.SetSequence(newPayer.Account, 900);
        string oldPayer = node.Payer.Account;
        List<string?> payerDuringFence = [];
        int transmittedBefore = node.Sink.Transmissions.Count;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, newPayer), node.Ledger, file, first.StatePath, newPayer.Account, "key-new", Options,
            new SynchronousProgress<PublishProgress>(_ => payerDuringFence.Add(PublicationState.Load(first.StatePath).Payer)), Token);

        Assert.All(payerDuringFence, payer => Assert.Equal(oldPayer, payer));
        PublicationState changed = PublicationState.Load(first.StatePath);
        Assert.Equal(newPayer.Account, changed.Payer);
        Assert.Equal("key-new", changed.PayerKeyReference);

        await resumed.RunAsync(null, Token);

        Assert.All(node.Sink.Transmissions.Skip(transmittedBefore).Where(t => t.TransactionType == "Batch"), t => Assert.Equal(newPayer.Account, t.Account));
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }

    [Fact]
    public async Task A_resume_prices_only_what_is_still_missing()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("resume", 40_000);
        BatchPublicationEstimate whole = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options, Token);
        (PublicationSession first, _) = await InterruptedAsync(node, afterTransmissions: 3);

        await PublicationSession.ResumeAsync(
            Factory(node, node.Payer, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);

        Assert.True(Assert.Single(budgets).PayerBudgetDrops < whole.PayerFeeDropsWorstCase);
    }

    [Fact]
    public async Task A_run_that_never_transmitted_has_no_fence_to_wait_for()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("resume", 9_000);
        PublicationSession first = await PublicationSession.StartAsync(
            Factory(node, node.Payer), node.Ledger, file, "text/plain", null,
            node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        List<PublishProgress> waited = [];

        await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(waited.Add), Token);

        Assert.Empty(waited);
    }

    [Fact]
    public async Task A_fence_wait_on_a_node_that_stopped_fails_instead_of_waiting_forever()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        node.Sink.AutoClose = false;
        SessionOptions stuck = new()
        {
            StateDirectory = _stateDirectory,
            FencePoll = TimeSpan.FromMilliseconds(1),
            NodeOutageLimit = TimeSpan.FromMilliseconds(200),
        };

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", stuck, null, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task Closing_during_the_fence_wait_leaves_the_state_as_it_was_and_the_run_resumable()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        byte[] before = await File.ReadAllBytesAsync(first.StatePath, Token);
        using CancellationTokenSource close = CancellationTokenSource.CreateLinkedTokenSource(Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options,
            new SynchronousProgress<PublishProgress>(_ => close.Cancel()), close.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(first.StatePath, Token));

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_one_account_run_is_not_resumed_as_a_Batch_run()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("v2", 2500);
        await using SequenceStrategy strategy = new(new FakeLedgerSink(node.Ledger), node.Writer, new SequenceStrategyOptions { FeeDrops = 10 });
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", "testnet", Options, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node, node.Payer), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionBatchResumeTests`
Expected: build FAILS (no Batch `ResumeAsync`).

- [ ] **Step 3: Write the Batch resume**

In `src/XrplFile.Core/PublicationSession.cs` add:

```csharp
    /// <summary>
    /// Picks a Batch run back up (spec 5.2): checks the file, waits until a snapshot is past the
    /// recorded fence plus the settle ledgers, reconciles, looks for a manifest already sent, writes
    /// a new payer to the state if one is given, prices what is still missing as a new session, and
    /// only then builds the strategy. Nothing the interrupted run sent can apply once the fence has
    /// passed, so the ledger's answer read after it is final.
    /// </summary>
    public static async Task<PublicationSession> ResumeAsync(
        BatchStrategyFactory strategyFactory, ILedgerSource source, ReadOnlyMemory<byte> file, string statePath,
        string payer, string payerKeyReference, SessionOptions options, IProgress<PublishProgress>? fenceProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategyFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(payerKeyReference);

        PublicationState state = PublicationState.Load(statePath);

        if (state.Version != PublicationState.BatchVersion)
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} records a one-account run, which resumes on the strategy it recorded rather than as a Batch run");
        }

        if (string.Equals(payer, state.Writer, StringComparison.Ordinal))
        {
            throw new ArgumentException("a Batch run's payer is another account than its writer (spec B1)", nameof(payer));
        }

        IReadOnlyList<byte[]> chunks = RequireResumable(state, statePath, file);

        await WaitForFenceAsync(source, state, options, fenceProgress, cancellationToken).ConfigureAwait(false);

        PublicationSession session = new(null, source, file, state, statePath, options, 0, 0, mintsAtStart: 0);
        session.RecordSourcePath(options.SourcePath);
        session._landed = await session.ReconcileAsync(chunks, cancellationToken).ConfigureAwait(false);

        if (await session.FindManifestAsync(chunks, session._landed, cancellationToken).ConfigureAwait(false) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }

        // Behind the fence no outer of the old payer can still apply, which is what makes changing it safe (spec B18).
        session.ChangePayer(payer, payerKeyReference);

        await RequireBatchEnabledAsync(source, cancellationToken).ConfigureAwait(false);

        int blocks = FileTree.BlockCount(chunks.Count);
        int lists = blocks > 1 ? PublicationEstimator.BlockListMemoCount(blocks) : 0;
        long missingChunks = chunks.Count - session._landed.Chunks.Count;

        // Lists are priced in full: their bytes depend on ledgers only the writer knows by now, and
        // there are few of them.
        BatchPublicationEstimate estimate = await PublicationEstimator
            .EstimateBatchAsync(source, state.Writer, payer, missingChunks, blocks, lists, cancellationToken).ConfigureAwait(false);

        RequireFeeWithinLimit(estimate);
        PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);

        session.AttachBatch(strategyFactory, estimate, resuming: true);

        return session;
    }

    /// <summary>
    /// Waits until a snapshot is past the fence plus the settle ledgers (spec B23), reporting the wait
    /// as the "fence" stage. A run that never transmitted has a fence of zero and waits for nothing.
    /// Reads are retried, and a node that stays unreachable, or whose validated ledger stays where it
    /// is, for <see cref="SessionOptions.NodeOutageLimit"/> fails the resume (Spec question 4).
    /// </summary>
    private static async Task WaitForFenceAsync(
        ILedgerSource source, PublicationState state, SessionOptions options, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        if (state.Fence is not { } fence || fence <= 0) return;

        BatchStrategyOptions reads = new() { ChainCheck = options.FencePoll, NodeOutageLimit = options.NodeOutageLimit };
        long target = fence + reads.SettleLedgers;
        long? first = null;
        long highest = 0;
        DateTimeOffset advancedAt = DateTimeOffset.UtcNow;

        while (true)
        {
            LedgerSnapshot snapshot = await BatchReads.RetryAsync(
                token => source.SnapshotAsync([state.Writer], token), reads, cancellationToken).ConfigureAwait(false);

            if (snapshot.LedgerIndex > target) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (snapshot.LedgerIndex > highest)
            {
                highest = snapshot.LedgerIndex;
                advancedAt = now;
            }
            else if (now - advancedAt >= options.NodeOutageLimit)
            {
                throw new XrplFileException(XrplFileError.NodeUnreachable,
                    $"the node's validated ledger has stayed at {snapshot.LedgerIndex} for {options.NodeOutageLimit}, short of the fence at {fence}");
            }

            first ??= snapshot.LedgerIndex;
            progress?.Report(new PublishProgress("fence", snapshot.LedgerIndex - first.Value, target + 1 - first.Value));

            await Task.Delay(options.FencePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the payer a resume runs with to the state before anything is sent (spec 5.2 step 5).</summary>
    private void ChangePayer(string payer, string payerKeyReference)
    {
        lock (_stateLock)
        {
            if (string.Equals(_state.Payer, payer, StringComparison.Ordinal)
                && string.Equals(_state.PayerKeyReference, payerKeyReference, StringComparison.Ordinal))
            {
                return;
            }

            _state = _state with { Payer = payer, PayerKeyReference = payerKeyReference };
            _state.Save(StatePath);
        }
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationSessionBatchResumeTests -class XrplFile.Tests.PublicationSessionBatchTests`
Expected: PASS.

- [ ] **Step 5: Run every Core unit test and commit**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` — Expected: PASS.

```bash
git add src/XrplFile.Core/PublicationSession.cs tests/XrplFile.Core.Tests/PublicationSessionBatchResumeTests.cs
git commit -m "feat(core): resume a Batch run behind its fence, with a new payer if needed"
```

---

### Task 17: Progress on the ticket path as every sweep poll finds memos

Spec B20 (ticket half), 4.4.

**Files:**
- Modify: `src/XrplFile.Core/TicketStrategy.cs` (`SubmitAllAsync`, `SweepAsync`)
- Test: `tests/XrplFile.Core.Tests/TicketStrategyTests.cs` (append)

**Interfaces:**
- Produces: `TicketStrategy.SweepAsync` gains an `Action<int>? found` parameter, called after every poll with the number of the batch's memos found successful so far; its `SweepResult.Landed` carries the records themselves, so every `SubmittedMemo` the ticket path returns has its `LedgerIndex` (Spec question 23).

- [ ] **Step 1: Write the failing tests**

Append inside `TicketStrategyTests`:

```csharp
    [Fact]
    public async Task Progress_moves_as_each_sweep_poll_finds_memos_not_once_per_batch()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint; the third memo shows up only two sweeps later.
        sink.QueuedUntilSweep[3] = 2;
        List<int> reports = [];
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(3), new SynchronousProgress<int>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(2, reports[0]);
        Assert.Equal(3, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    [Fact]
    public async Task Progress_on_tickets_never_counts_a_memo_that_failed()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.FailMarkers.Add(MemoLayout.ChunkMarkerHex(1));
        List<int> reports = [];

        // A short batch timeout: the failing memo would otherwise wait the full five seconds per attempt.
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 100,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromMilliseconds(200),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(
            Memos(3), new SynchronousProgress<int>(reports.Add), TestContext.Current.CancellationToken));

        Assert.Equal(2, reports.Max());
    }

    [Fact]
    public async Task Every_ticketed_memo_says_which_ledger_it_is_in()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        foreach (SubmittedMemo memo in await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal((await ledger.TransactionAsync(memo.TxHash, TestContext.Current.CancellationToken))!.LedgerIndex, memo.LedgerIndex);
        }
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TicketStrategyTests`
Expected: the first new test FAILS (`reports[0]` is 3: one report after the batch's sweep).

- [ ] **Step 3: Report from every poll**

1. `SweepResult` carries the records the sweep matched, not only their hashes:

```csharp
    private sealed record SweepResult(IReadOnlyDictionary<string, LedgerTransaction> Landed, IReadOnlySet<string> Seen)
    {
        public static SweepResult Empty { get; } = new(new Dictionary<string, LedgerTransaction>(), new HashSet<string>());
    }
```

2. In `SweepAsync`, add the parameter `Action<int>? found` before `CancellationToken cancellationToken`, documented `/// <param name="found">Told after every poll how many of these markers have landed successfully so far (spec B20).</param>`. Rename its local dictionary from `found` to `landedHere` and make it `Dictionary<string, LedgerTransaction> landedHere = new(StringComparer.Ordinal);`; the one `TryAdd` becomes `landedHere.TryAdd(tx.MemoTypeHex, tx);`; the completeness check becomes `if (landedHere.Count == markers.Count) return new SweepResult(landedHere, seen);` and the deadline line `if (DateTimeOffset.UtcNow >= deadline) return new SweepResult(landedHere, seen);`. Immediately after the `await foreach` loop, before those two lines, add:

```csharp
            found?.Invoke(landedHere.Count);
```

3. In `SubmitAllAsync`, the landed map holds records: `Dictionary<int, LedgerTransaction> landed = [];`; the copy loop becomes

```csharp
            foreach ((string marker, LedgerTransaction tx) in swept.Landed)
            {
                landed[wanted[marker]] = tx;
            }
```

the `SweepAsync` call becomes

```csharp
            int landedBefore = landed.Count;

            SweepResult swept = wanted.Count == 0
                ? SweepResult.Empty
                : await SweepAsync(
                    wanted.ToDictionary(pair => pair.Key, pair => memos[pair.Value].MemoDataHex, StringComparer.Ordinal),
                    sweepFrom.Value, batchFrom, found => submitted?.Report(landedBefore + found), cancellationToken).ConfigureAwait(false);
```

and the final `return` becomes

```csharp
        return [.. landed.OrderBy(pair => pair.Key)
            .Select(pair => new SubmittedMemo(pair.Key, pair.Value.Hash) { LedgerIndex = pair.Value.LedgerIndex })];
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.TicketStrategyTests -class XrplFile.Tests.XrplFileWriterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core/TicketStrategy.cs tests/XrplFile.Core.Tests/TicketStrategyTests.cs
git commit -m "feat(core): ticket progress moves with every sweep poll, and says where each memo landed"
```

---
### Task 18: App — "Paid by", payer memory, the amendment cache and the policy

Spec 6.1, B3, B4, B14 (screen cache), Review Focus 3.

**Files:**
- Create: `app/XrplFile.App.Core/IPayerSettingsStore.cs`, `app/XrplFile.App.Core/FilePayerSettingsStore.cs`, `app/XrplFile.App.Core/BatchAvailability.cs`
- Modify: `app/XrplFile.App.Core/PublishRequest.cs`, `app/XrplFile.App.Core/PublishModel.cs`, `app/XrplFile.App.Core/PublishPolicy.cs`, `app/XrplFile.App/MauiProgram.cs`, `app/XrplFile.App/Components/Publish.razor`
- Test: `tests/XrplFile.App.Core.Tests/PublishModelPayerTests.cs`, `tests/XrplFile.App.Core.Tests/BatchAvailabilityTests.cs`, `tests/XrplFile.App.Core.Tests/FilePayerSettingsStoreTests.cs`, `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs` (append)

**Interfaces:**
- Consumes: `Amendments.Batch`, `ILedgerSource.IsAmendmentEnabledAsync`, `BatchStrategy`, `BatchBudget`, `IBatchFenceRecorder`, `BatchStrategyOptions`, `PublicationState` strategy names.
- Produces:
  - `PublishRequest(ReadOnlyMemory<byte> File, string Type, string Name, string KeyReference, string Account, string Network, string? PayerReference = null, string? PayerAccount = null, string? SourcePath = null)` with init properties `string? WriterLabel`, `string? PayerLabel`
  - `public interface IPayerSettingsStore { string? LastPayer(string network); void Remember(string network, string payerReference); }`; `FilePayerSettingsStore(string? settingPath = null)` — `payers.json` beside `node-urls.json`
  - `public sealed class BatchAvailability(IConnectionFactory connections, TimeProvider time)`: `static readonly TimeSpan CacheFor` (5 min), `Task<bool?> IsEnabledAsync(string network, CancellationToken)` — null when the read failed (not cached)
  - `PublishModel` ctor gains `BatchAvailability? batch = null, IPayerSettingsStore? payers = null`; members `NobodyLabel`, `NobodyNote`, `BatchUnavailableNote` (const string), `PayerChoices`, `SelectedPayerReference`, `SelectedPayer`, `BatchEnabled` (`bool?`), `PayerLocked`, `PayerNote`, `Task RefreshPayerAsync(CancellationToken)`, `void SelectPayer(string? reference)`, `void RecordStarted(PublishRequest request)`
  - `PublishPolicy.CreateBatch(BatchBudget budget, IBatchFenceRecorder fence, ILedgerConnection connection, ITransactionSigner writer, ITransactionSigner payer, BatchStrategyOptions? options = null)` → `BatchStrategy`; `PublishPolicy.CreateFor(string? recordedStrategy, PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer)` → `ISubmissionStrategy`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/FilePayerSettingsStoreTests.cs`:

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class FilePayerSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-payers-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_last_payer_is_remembered_per_network()
    {
        string path = Path.Combine(_directory, "payers.json");
        new FilePayerSettingsStore(path).Remember("testnet", "ref-a");
        new FilePayerSettingsStore(path).Remember("devnet", "ref-b");
        new FilePayerSettingsStore(path).Remember("testnet", "ref-c");

        FilePayerSettingsStore store = new(path);
        Assert.Equal("ref-c", store.LastPayer("testnet"));
        Assert.Equal("ref-b", store.LastPayer("devnet"));
        Assert.Null(store.LastPayer("mainnet"));
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_remembers_nothing()
    {
        string path = Path.Combine(_directory, "payers.json");
        File.WriteAllText(path, "not json");

        Assert.Null(new FilePayerSettingsStore(path).LastPayer("testnet"));
    }
}
```

`tests/XrplFile.App.Core.Tests/BatchAvailabilityTests.cs`:

```csharp
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class BatchAvailabilityTests
{
    [Fact]
    public async Task A_read_is_cached_per_network_for_five_minutes_then_read_again()
    {
        FakeConnectionFactory connections = new();
        ManualTimeProvider time = new();
        BatchAvailability availability = new(connections, time);

        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        connections.Ledger.EnabledAmendments.Clear();
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));
        Assert.Equal(2, connections.Connections);
    }

    [Fact]
    public async Task A_failed_read_is_unknown_and_not_cached()
    {
        FakeConnectionFactory connections = new() { ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "down") };
        BatchAvailability availability = new(connections, new ManualTimeProvider());

        Assert.Null(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));

        connections.ConnectThrows = null;
        Assert.True(await availability.IsEnabledAsync("testnet", TestContext.Current.CancellationToken));
    }
}
```

`tests/XrplFile.App.Core.Tests/PublishModelPayerTests.cs`:

```csharp
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishModelPayerTests : VaultHarness
{
    private readonly FakeFilePicker _picker = new();
    private readonly FakeConnectionFactory _connections = new();
    private readonly string _files = Directory.CreateTempSubdirectory("xrpl-file-paid-by-").FullName;

    public override async ValueTask DisposeAsync()
    {
        Directory.Delete(_files, recursive: true);
        await base.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private FilePayerSettingsStore Payers => new(Path.Combine(_files, "payers.json"));

    private async Task<(PublishModel Model, Dictionary<string, string> References)> OpenAsync(params (string Label, string Network, string? Address)[] keys)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        Dictionary<string, string> references = [];

        foreach ((string label, string network, string? address) in keys)
        {
            NewVaultEntry entry = Entry(label) with { Network = network, Address = address ?? "rAddressOf" + label };
            references[label] = await vault.StoreAsync(entry, Token);
        }

        PublishModel model = new(
            vault, _picker, new FileNodeSettingsStore(Path.Combine(_files, "nodes.json")),
            new BatchAvailability(_connections, new ManualTimeProvider()), Payers);
        await model.RefreshKeysAsync(Token);

        return (model, references);
    }

    [Fact]
    public async Task Paid_by_lists_the_other_keys_on_the_writers_network_and_never_the_writer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(
            ("writer", "testnet", "rWriter"), ("upper", "TESTNET", null), ("twin", "testnet", "rWriter"), ("elsewhere", "devnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.Equal(["upper"], model.PayerChoices.Select(key => key.Label).ToArray());
    }

    [Fact]
    public async Task The_default_payer_is_the_last_one_used_on_that_network_then_the_first_other_key_then_nobody()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(
            ("writer", "testnet", null), ("first", "testnet", null), ("second", "testnet", null), ("alone", "devnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        Assert.Equal(refs["first"], model.SelectedPayerReference);

        Payers.Remember("testnet", refs["second"]);
        model.SelectKey(refs["first"]);
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        Assert.Equal(refs["second"], model.SelectedPayerReference);

        model.SelectKey(refs["alone"]);
        await model.RefreshPayerAsync(Token);
        Assert.Null(model.SelectedPayerReference);
        Assert.Equal(PublishModel.NobodyNote, model.PayerNote);
    }

    [Fact]
    public async Task A_network_without_Batch_fixes_Paid_by_on_Nobody_and_says_why()
    {
        _connections.Ledger.EnabledAmendments.Clear();
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.True(model.PayerLocked);
        Assert.Null(model.SelectedPayer);
        Assert.Equal("This network does not support Batch, so publishing uses tickets.", model.PayerNote);
    }

    [Fact]
    public async Task A_failed_amendment_read_leaves_Paid_by_usable()
    {
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "down");
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        Assert.Null(model.BatchEnabled);
        Assert.False(model.PayerLocked);
        Assert.Equal(refs["payer"], model.SelectedPayerReference);
    }

    [Fact]
    public async Task Choosing_Nobody_shows_its_muted_note_and_the_request_carries_no_payer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        string path = Path.Combine(_files, "f.bin");
        File.WriteAllBytes(path, new byte[2000]);
        _picker.PathToReturn = path;
        await model.PickFileAsync(Token);

        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);
        model.SelectPayer(null);

        Assert.Equal("Slower, and holds up to 50 XRP of reserve while publishing.", model.PayerNote);
        PublishRequest request = (await model.BuildRequestAsync(Token))!;
        Assert.Null(request.PayerReference);
        Assert.Equal(path, request.SourcePath);
    }

    [Fact]
    public async Task A_request_with_a_payer_names_both_accounts_and_starting_it_remembers_the_payer()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        string path = Path.Combine(_files, "f.bin");
        File.WriteAllBytes(path, new byte[2000]);
        _picker.PathToReturn = path;
        await model.PickFileAsync(Token);
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        PublishRequest request = (await model.BuildRequestAsync(Token))!;
        model.RecordStarted(request);

        Assert.Equal(refs["payer"], request.PayerReference);
        Assert.Equal("rAddressOfpayer", request.PayerAccount);
        Assert.Equal("writer", request.WriterLabel);
        Assert.Equal("payer", request.PayerLabel);
        Assert.Equal(refs["payer"], Payers.LastPayer("testnet"));
    }
}
```

Append to `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs` inside the class (add `using XrplFile.Tests;` and `using Xrpl.Wallet;` if absent):

```csharp
    [Fact]
    public async Task A_batch_strategy_is_built_from_the_sessions_budget_with_both_signers()
    {
        await using ILedgerConnection connection = await new FakeConnectionFactory().ConnectAsync("testnet", TestContext.Current.CancellationToken);
        ITransactionSigner writer = new WalletSigner(XrplWallet.Generate());
        ITransactionSigner payer = new WalletSigner(XrplWallet.Generate());

        BatchStrategy strategy = PublishPolicy.CreateBatch(new BatchBudget(15, 30, 1000, 1000), new NoFence(), connection, writer, payer);

        Assert.Equal(15, strategy.FeeDropsPerTransaction);
        Assert.Equal(writer.Account, strategy.Account);
        Assert.Equal(payer.Account, strategy.Payer);
    }

    [Theory]
    [InlineData("tickets", typeof(TicketStrategy))]
    [InlineData("sequence", typeof(SequenceStrategy))]
    public async Task A_run_resumes_on_the_strategy_it_recorded_whatever_its_size(string recorded, Type expected)
    {
        FakeConnectionFactory connections = new();
        await using ILedgerConnection connection = await connections.ConnectAsync("testnet", TestContext.Current.CancellationToken);
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            connections.Ledger, "rWriter", 2000, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        ISubmissionStrategy strategy = PublishPolicy.CreateFor(recorded, estimate, connection, new WalletSigner(XrplWallet.Generate()));

        Assert.IsType(expected, strategy);
    }

    [Fact]
    public async Task A_run_that_recorded_no_strategy_resumes_on_the_existing_policy()
    {
        FakeConnectionFactory connections = new();
        await using ILedgerConnection connection = await connections.ConnectAsync("testnet", TestContext.Current.CancellationToken);
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            connections.Ledger, "rWriter", 2000, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        Assert.IsType<SequenceStrategy>(PublishPolicy.CreateFor(null, estimate, connection, new WalletSigner(XrplWallet.Generate())));
    }

    private sealed class NoFence : IBatchFenceRecorder
    {
        public Task RecordFenceAsync(long deadline, CancellationToken cancellationToken) => Task.CompletedTask;
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishModelPayerTests -class XrplFile.App.Core.Tests.BatchAvailabilityTests -class XrplFile.App.Core.Tests.FilePayerSettingsStoreTests -class XrplFile.App.Core.Tests.PublishPolicyTests`
Expected: build FAILS.

- [ ] **Step 3: The request, the settings store and the availability cache**

`app/XrplFile.App.Core/PublishRequest.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. The bytes travel with it because
/// <see cref="PublicationSession"/> takes the whole file as a <see cref="ReadOnlyMemory{T}"/> — there
/// is no streaming path into it, which is also why <see cref="PublishModel.MaxFileBytes"/> exists.
/// </summary>
/// <param name="PayerReference">The vault key that pays for a Batch run, or null for a one-account run (spec 6.1).</param>
/// <param name="PayerAccount">That key's classic address.</param>
/// <param name="SourcePath">Where the bytes were read from, recorded so a resume need not ask for them (spec B17).</param>
public sealed record PublishRequest(
    ReadOnlyMemory<byte> File,
    string Type,
    string Name,
    string KeyReference,
    string Account,
    string Network,
    string? PayerReference = null,
    string? PayerAccount = null,
    string? SourcePath = null)
{
    /// <summary>The writer key's label, for the estimate card; null when not known.</summary>
    public string? WriterLabel { get; init; }

    /// <summary>The payer key's label, for the estimate card and the method line; null when not known.</summary>
    public string? PayerLabel { get; init; }
}
```

`app/XrplFile.App.Core/IPayerSettingsStore.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>The payer used last on each network: the default of "Paid by" (spec 6.1).</summary>
public interface IPayerSettingsStore
{
    string? LastPayer(string network);

    void Remember(string network, string payerReference);
}
```

`app/XrplFile.App.Core/FilePayerSettingsStore.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The payer memory as a small JSON object of network name to vault reference, in
/// <c>payers.json</c> beside <c>node-urls.json</c>. Read the defensive way the node settings are: a
/// file that cannot be read remembers nothing rather than stopping the Publish screen from opening.
/// </summary>
public sealed class FilePayerSettingsStore(string? settingPath = null) : IPayerSettingsStore
{
    private const string SettingFileName = "payers.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _settingPath = settingPath ?? Path.Combine(Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    public string? LastPayer(string network) => Read().TryGetValue(network, out string? reference) ? reference : null;

    public void Remember(string network, string payerReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);
        ArgumentException.ThrowIfNullOrEmpty(payerReference);

        Dictionary<string, string> payers = Read();
        payers[network] = payerReference;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
            File.WriteAllBytes(_settingPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payers, SerializerOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A default that is not remembered costs one click next time; it must not cost the run.
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_settingPath)) return new Dictionary<string, string>(StringComparer.Ordinal);

            Dictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_settingPath));
            return read is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
```

`app/XrplFile.App.Core/BatchAvailability.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Whether a network enables <c>BatchV1_1</c>, as the Publish screen needs it (spec B14): read once
/// per network and kept for five minutes. A failed read is "unknown" and is not kept, so the screen
/// stays usable and asks again next time. The estimate reads again before every run, and that read decides.
/// </summary>
public sealed class BatchAvailability(IConnectionFactory connections, TimeProvider time)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (bool Enabled, DateTimeOffset ReadAt)> _cache = new(StringComparer.Ordinal);

    public async Task<bool?> IsEnabledAsync(string network, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);

        lock (_gate)
        {
            if (_cache.TryGetValue(network, out (bool Enabled, DateTimeOffset ReadAt) cached) && time.GetUtcNow() - cached.ReadAt < CacheFor)
            {
                return cached.Enabled;
            }
        }

        try
        {
            await using ILedgerConnection connection = await connections.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            bool enabled = await connection.Source.IsAmendmentEnabledAsync(Amendments.Batch, cancellationToken).ConfigureAwait(false);

            lock (_gate) _cache[network] = (enabled, time.GetUtcNow());

            return enabled;
        }
        catch (XrplFileException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: The model**

In `app/XrplFile.App.Core/PublishModel.cs`:

1. Change the class header to `public sealed class PublishModel(IKeyVault vault, IFilePickerService picker, INodeSettingsStore nodes, BatchAvailability? batch = null, IPayerSettingsStore? payers = null)`.

2. Add after `MaxFileBytes`:

```csharp
    /// <summary>The last entry of "Paid by" (spec 6.1).</summary>
    public const string NobodyLabel = "Nobody — publish with tickets";

    /// <summary>The muted note under "Nobody" (spec 6.1).</summary>
    public const string NobodyNote = "Slower, and holds up to 50 XRP of reserve while publishing.";

    /// <summary>Under "Paid by" when the network's ledger does not enable Batch (spec 6.1).</summary>
    public const string BatchUnavailableNote = "This network does not support Batch, so publishing uses tickets.";

    /// <summary>The writer the current payer was chosen for.</summary>
    private string? _payerChosenFor;

    /// <summary>Whether the person chose the current payer; a default is worked out again on every refresh.</summary>
    private bool _payerChosenByPerson;

    /// <summary>The chosen payer's reference, or null for Nobody.</summary>
    public string? SelectedPayerReference { get; private set; }

    /// <summary>The cached amendment read for the writer's network (spec B14): null when it could not be read.</summary>
    public bool? BatchEnabled { get; private set; }

    /// <summary>Fixed on Nobody: the network does not enable Batch.</summary>
    public bool PayerLocked => BatchEnabled == false;

    /// <summary>
    /// Every other key on the writer's network, compared in canonical form, never the writer's own
    /// account under another reference (spec 6.1, B3).
    /// </summary>
    public IReadOnlyList<VaultEntryInfo> PayerChoices =>
        SelectedKey is { } writer && XrplFileLink.TryNormalizeNetwork(writer.Network, out string? network)
            ? [.. Keys.Where(key =>
                !string.Equals(key.Reference, writer.Reference, StringComparison.Ordinal)
                && !string.Equals(key.Address, writer.Address, StringComparison.Ordinal)
                && XrplFileLink.TryNormalizeNetwork(key.Network, out string? other)
                && string.Equals(other, network, StringComparison.Ordinal))]
            : [];

    /// <summary>The payer the run will use, or null for Nobody (a locked drop-down is always Nobody).</summary>
    public VaultEntryInfo? SelectedPayer =>
        PayerLocked || SelectedPayerReference is null
            ? null
            : PayerChoices.FirstOrDefault(key => string.Equals(key.Reference, SelectedPayerReference, StringComparison.Ordinal));

    /// <summary>The muted line under "Paid by", or null.</summary>
    public string? PayerNote => PayerLocked ? BatchUnavailableNote : SelectedPayer is null ? NobodyNote : null;

    /// <summary>
    /// Reads the amendment for the writer's network (cached, spec B14) and picks the default payer
    /// for the selected writer: the payer used last on that network, else the first other key
    /// there, else Nobody (spec 6.1). A payer the person chose for this writer stands.
    /// </summary>
    public async Task RefreshPayerAsync(CancellationToken cancellationToken)
    {
        if (SelectedKey is not { } writer || !XrplFileLink.TryNormalizeNetwork(writer.Network, out string? network))
        {
            SelectedPayerReference = null;
            BatchEnabled = null;
            _payerChosenFor = null;
            _payerChosenByPerson = false;
            return;
        }

        BatchEnabled = batch is null ? null : await batch.IsEnabledAsync(network, cancellationToken).ConfigureAwait(false);

        if (_payerChosenByPerson
            && string.Equals(_payerChosenFor, writer.Reference, StringComparison.Ordinal)
            && (SelectedPayerReference is null || PayerChoices.Any(key => string.Equals(key.Reference, SelectedPayerReference, StringComparison.Ordinal))))
        {
            return;
        }

        IReadOnlyList<VaultEntryInfo> choices = PayerChoices;
        string? last = payers?.LastPayer(network);

        SelectedPayerReference =
            last is not null && choices.Any(key => string.Equals(key.Reference, last, StringComparison.Ordinal)) ? last
            : choices.Count > 0 ? choices[0].Reference
            : null;
        _payerChosenFor = writer.Reference;
        _payerChosenByPerson = false;
    }

    /// <summary>The person's choice in "Paid by"; null or empty is Nobody.</summary>
    public void SelectPayer(string? reference)
    {
        SelectedPayerReference = string.IsNullOrEmpty(reference) ? null : reference;
        _payerChosenFor = SelectedReference;
        _payerChosenByPerson = true;
    }

    /// <summary>A run with this request has started: its payer becomes the network's default (spec 6.1).</summary>
    public void RecordStarted(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PayerReference is { } payer) payers?.Remember(request.Network, payer);
    }
```

3. In `BuildRequestAsync`, extend the vault check so a chosen payer is still listed — replace the `if (!listed.Any(...))` block with:

```csharp
            if (!listed.Any(key => string.Equals(key.Reference, SelectedKey.Reference, StringComparison.Ordinal)))
            {
                Message = "That key is no longer in the vault.";
                return null;
            }

            if (SelectedPayer is { } chosen && !listed.Any(key => string.Equals(key.Reference, chosen.Reference, StringComparison.Ordinal)))
            {
                Message = "The key chosen to pay is no longer in the vault.";
                return null;
            }
```

and replace the final `return new PublishRequest(...)` with:

```csharp
        VaultEntryInfo? payer = SelectedPayer;

        return new PublishRequest(
            bytes,
            TypeOf(FileName!),
            FileName!,
            SelectedKey.Reference,
            SelectedKey.Address,
            canonicalNetwork!,
            payer?.Reference,
            payer?.Address,
            FilePath)
        {
            WriterLabel = SelectedKey.Label,
            PayerLabel = payer?.Label,
        };
```

- [ ] **Step 5: The policy**

In `app/XrplFile.App.Core/PublishPolicy.cs` add:

```csharp
    /// <summary>
    /// The Batch strategy for a run with a payer (spec 6.1, B4): built by the session, which hands in
    /// the budget from its estimate and itself as the fence recorder.
    /// </summary>
    public static BatchStrategy CreateBatch(
        BatchBudget budget, IBatchFenceRecorder fence, ILedgerConnection connection, ITransactionSigner writer, ITransactionSigner payer,
        BatchStrategyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new BatchStrategy(connection.Sink, connection.Source, writer, payer, budget, fence, options);
    }

    /// <summary>
    /// The one-account strategy a run resumes on (spec B16): the one it recorded, whatever its size;
    /// a run recorded before strategies were, on the existing policy.
    /// </summary>
    public static ISubmissionStrategy CreateFor(
        string? recordedStrategy, PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(estimate.FeeDropsPerTransaction, int.MaxValue);

        int fee = (int)estimate.FeeDropsPerTransaction;

        return recordedStrategy switch
        {
            null => Create(estimate, connection, signer),
            PublicationState.TicketStrategyName => Tickets(connection, signer, fee),
            PublicationState.SequenceStrategyName => Sequence(connection, signer, fee),
            _ => throw new ArgumentOutOfRangeException(nameof(recordedStrategy), recordedStrategy, "not a one-account strategy"),
        };
    }

    private static TicketStrategy Tickets(ILedgerConnection connection, ITransactionSigner signer, int fee) =>
        new(connection.Sink, connection.Source, signer, new TicketStrategyOptions { FeeDrops = fee, TicketsPerBatch = TicketsPerBatch });

    private static SequenceStrategy Sequence(ILedgerConnection connection, ITransactionSigner signer, int fee) =>
        new(connection.Sink, signer, new SequenceStrategyOptions { FeeDrops = fee }, connection.Source);
```

and change the body of `Create`'s `return` to `return UsesTickets(estimate) ? Tickets(connection, signer, fee) : Sequence(connection, signer, fee);`.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishModelPayerTests -class XrplFile.App.Core.Tests.BatchAvailabilityTests -class XrplFile.App.Core.Tests.FilePayerSettingsStoreTests -class XrplFile.App.Core.Tests.PublishPolicyTests -class XrplFile.App.Core.Tests.PublishModelTests`
Expected: PASS.

- [ ] **Step 7: Wire the screen and the container**

`app/XrplFile.App/MauiProgram.cs`, after the `NetworkChoices` registration:

```csharp
        // "Paid by" (spec 6.1): the payer remembered per network, beside node-urls.json, and the
        // network's Batch amendment, cached for five minutes. PublishModel takes both as optional
        // constructor parameters, which the container fills from these registrations.
        services.AddSingleton<IPayerSettingsStore>(new FilePayerSettingsStore());
        services.AddSingleton(provider => new BatchAvailability(
            provider.GetRequiredService<IConnectionFactory>(), provider.GetRequiredService<TimeProvider>()));
```

`app/XrplFile.App/Components/Publish.razor`: after the key `list-group` `</div>`, inside the same `xf-card` section, add:

```razor
            @if (Model.SelectedKey is not null)
            {
                <label class="form-label mt-3" for="xf-payer">Paid by</label>
                <select id="xf-payer" class="form-select" disabled="@Model.PayerLocked"
                        value="@(Model.SelectedPayer?.Reference ?? string.Empty)" @onchange="OnPayerChanged">
                    @foreach (VaultEntryInfo payer in Model.PayerChoices)
                    {
                        <option value="@payer.Reference">@payer.Label &mdash; @payer.Address</option>
                    }
                    <option value="">@PublishModel.NobodyLabel</option>
                </select>
                @if (Model.PayerNote is { } note)
                {
                    <p class="xf-muted small mt-1 mb-0">@note</p>
                }
            }
```

Change the key button's handler from `@onclick="() => Model.SelectKey(key.Reference)"` to `@onclick="() => SelectKeyAsync(key.Reference)"`, and in `@code` add:

```csharp
    private async Task SelectKeyAsync(string reference)
    {
        Model.SelectKey(reference);
        await Model.RefreshPayerAsync(_cts.Token);
    }

    private void OnPayerChanged(ChangeEventArgs change) => Model.SelectPayer(change.Value as string);
```

and in `OnInitializedAsync`, after `await Model.RefreshKeysAsync(_cts.Token);`, add `await Model.RefreshPayerAsync(_cts.Token);`. (The select binds to a string value; a `bool` binding does not work on a Blazor `<select>`.)

- [ ] **Step 8: Build and commit**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — Expected: 0 errors, 0 warnings.

```bash
git add app/XrplFile.App.Core/IPayerSettingsStore.cs app/XrplFile.App.Core/FilePayerSettingsStore.cs app/XrplFile.App.Core/BatchAvailability.cs app/XrplFile.App.Core/PublishRequest.cs app/XrplFile.App.Core/PublishModel.cs app/XrplFile.App.Core/PublishPolicy.cs app/XrplFile.App/MauiProgram.cs app/XrplFile.App/Components/Publish.razor tests/XrplFile.App.Core.Tests/PublishModelPayerTests.cs tests/XrplFile.App.Core.Tests/BatchAvailabilityTests.cs tests/XrplFile.App.Core.Tests/FilePayerSettingsStoreTests.cs tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs
git commit -m "feat(app): choose who pays for a publication under Paid by"
```

---

### Task 19: App — the two-account estimate

Spec 6.2, 4.5 (refusals as sentences).

**Files:**
- Modify: `app/XrplFile.App.Core/EstimateModel.cs`, `app/XrplFile.App/Components/Estimate.razor`
- Test: `tests/XrplFile.App.Core.Tests/EstimateModelTests.cs` (append)

**Interfaces:**
- Consumes: `PublicationSession.EstimateAsync(source, writer, payer, …)`, `BatchPublicationEstimate`, `PublishRequest` payer fields and labels.
- Produces: `EstimateModel.BatchEstimate` (`BatchPublicationEstimate?`), `EstimateModel.MethodLine` (`string?`), `static string? EstimateModel.ShortfallOf(BatchPublicationEstimate estimate, PublishRequest request)`.

- [ ] **Step 1: Write the failing tests**

Append inside `EstimateModelTests`:

```csharp
    private static PublishRequest BatchRequest(int size = 4096) =>
        new(new byte[size], "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone", "ref-2", "rPayer")
        {
            WriterLabel = "Writer key",
            PayerLabel = "Payer key",
        };

    [Fact]
    public async Task A_request_with_a_payer_gets_the_two_account_estimate_and_names_the_method()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        _connections.Ledger.Balances["rPayer"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(BatchRequest(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.BatchEstimate);
        Assert.Null(model.Estimate);
        Assert.True(model.CanPublish);
        Assert.Equal("Batch, paid by Payer key.", model.MethodLine);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task A_short_payer_is_named_with_what_it_needs_and_can_spend()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        _connections.Ledger.Balances["rPayer"] = 1_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(BatchRequest(), TestContext.Current.CancellationToken);

        BatchPublicationEstimate estimate = model.BatchEstimate!;
        Assert.False(model.CanPublish);
        Assert.Equal(
            $"The payer Payer key needs {XrpAmount.Format(estimate.PayerRequiredDrops)} and can spend {XrpAmount.Format(estimate.PayerSpendableDrops)}.",
            model.Message);
    }

    [Fact]
    public async Task A_short_writer_is_named_the_same_way()
    {
        _connections.Ledger.Balances["rWriter"] = 0;
        _connections.Ledger.Balances["rPayer"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(BatchRequest(), TestContext.Current.CancellationToken);

        BatchPublicationEstimate estimate = model.BatchEstimate!;
        Assert.Equal(
            $"The writer Writer key needs {XrpAmount.Format(estimate.WriterRequiredDrops)} and can spend {XrpAmount.Format(estimate.WriterSpendableDrops)}.",
            model.Message);
    }

    [Fact]
    public async Task A_network_without_Batch_is_told_to_choose_Nobody()
    {
        _connections.Ledger.EnabledAmendments.Clear();

        EstimateModel model = NewModel();
        await model.EstimateAsync(BatchRequest(), TestContext.Current.CancellationToken);

        Assert.False(model.CanPublish);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.BatchNotEnabled), model.Message);
    }

    [Fact]
    public async Task Fees_too_high_at_estimate_time_do_not_mention_an_unfinished_run()
    {
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(250, 10);

        EstimateModel model = NewModel();
        await model.EstimateAsync(BatchRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(PublishErrorMessages.FeeTooHighNow, model.Message);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.EstimateModelTests`
Expected: build FAILS (`BatchEstimate` does not exist).

- [ ] **Step 3: The model**

In `app/XrplFile.App.Core/EstimateModel.cs`:

1. Add properties:

```csharp
    /// <summary>The two-account estimate, when the request has a payer (spec 6.2); otherwise null.</summary>
    public BatchPublicationEstimate? BatchEstimate { get; private set; }

    /// <summary>"Batch, paid by &lt;label&gt;." for a Batch request, otherwise null (spec 6.2).</summary>
    public string? MethodLine { get; private set; }
```

2. Replace `CanPublish` with:

```csharp
    public bool CanPublish =>
        BatchEstimate is { } batch
            ? batch.WriterCovers && batch.PayerCovers
            : Estimate is not null
                && Estimate.BalanceCovers
                && Estimate.FeeDropsPerTransaction <= int.MaxValue
                && !PublishPolicy.HasNoTicketRoom(Estimate);
```

3. In `EstimateAsync`, after `Message = null;` add `BatchEstimate = null; MethodLine = null;` and then:

```csharp
        if (request.PayerAccount is { } payer)
        {
            await EstimateBatchAsync(request, payer, cancellationToken).ConfigureAwait(false);
            return;
        }
```

4. Add:

```csharp
    private async Task EstimateBatchAsync(PublishRequest request, string payer, CancellationToken cancellationToken)
    {
        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            NodeUrl = connection.Url;

            SessionOptions options = new() { StateDirectory = paths.StateDirectory };

            BatchEstimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, payer, request.File.Length, options, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            // Nothing has started, so "listed under unfinished runs" would be false (Spec question 6).
            Message = ex.Error == XrplFileError.FeeTooHigh ? PublishErrorMessages.FeeTooHighNow : PublishErrorMessages.For(ex);
            return;
        }

        MethodLine = $"Batch, paid by {request.PayerLabel ?? payer}.";
        Message = ShortfallOf(BatchEstimate, request);
    }

    /// <summary>The account that is short, by label, with what it needs and what it can spend (spec 6.2); null when both cover.</summary>
    public static string? ShortfallOf(BatchPublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        List<string> lines = [];

        if (!estimate.WriterCovers)
        {
            lines.Add($"The writer {request.WriterLabel ?? request.Account} needs {XrpAmount.Format(estimate.WriterRequiredDrops)} "
                + $"and can spend {XrpAmount.Format(estimate.WriterSpendableDrops)}.");
        }

        if (!estimate.PayerCovers)
        {
            lines.Add($"The payer {request.PayerLabel ?? request.PayerAccount} needs {XrpAmount.Format(estimate.PayerRequiredDrops)} "
                + $"and can spend {XrpAmount.Format(estimate.PayerSpendableDrops)}.");
        }

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.EstimateModelTests`
Expected: PASS, the existing ticket-estimate tests included.

- [ ] **Step 5: The card**

In `app/XrplFile.App/Components/Estimate.razor`, before `@if (Model.Estimate is { } estimate)`, add:

```razor
    @if (Model.BatchEstimate is { } batch)
    {
        <section class="xf-card">
            <table class="table xf-kv-table">
                <tbody>
                    <tr><th>Chunks</th><td>@batch.Chunks.ToString("N0")</td></tr>
                    <tr><th>Batches</th><td>@batch.Batches.ToString("N0")</td></tr>
                    <tr><th>Solo transactions</th><td>@batch.SoloTransactions.ToString("N0")</td></tr>
                    <tr><th>Fee unit</th><td>@batch.FeeUnitDrops.ToString("N0") drops</td></tr>
                </tbody>
            </table>

            <h2 class="xf-section-title mt-4">Payer &mdash; @(Request.PayerLabel) <code class="xf-mono">@Request.PayerAccount</code></h2>
            <table class="table xf-kv-table">
                <tbody>
                    <tr><th>Fees</th><td>@XrpAmount.Format(batch.PayerFeeDrops)</td></tr>
                    <tr><th>Fees in the worst case</th><td>@XrpAmount.Format(batch.PayerFeeDropsWorstCase)</td></tr>
                    <tr><th>Account reserve</th><td>@XrpAmount.Format(batch.BaseReserveDrops)</td></tr>
                    <tr><th>Required</th><td>@XrpAmount.Format(batch.PayerRequiredDrops)</td></tr>
                    <tr><th>Balance</th><td>@XrpAmount.Format(batch.PayerBalanceDrops)</td></tr>
                    <tr><th>Spendable</th><td>@XrpAmount.Format(batch.PayerSpendableDrops)</td></tr>
                </tbody>
            </table>

            <h2 class="xf-section-title mt-4">Writer &mdash; @(Request.WriterLabel) <code class="xf-mono">@Request.Account</code></h2>
            <table class="table xf-kv-table">
                <tbody>
                    <tr><th>Fees</th><td>@XrpAmount.Format(batch.WriterFeeDrops)</td></tr>
                    <tr><th>Fees in the worst case</th><td>@XrpAmount.Format(batch.WriterFeeDropsWorstCase)</td></tr>
                    <tr><th>Account reserve</th><td>@XrpAmount.Format(batch.BaseReserveDrops)</td></tr>
                    <tr><th>Required</th><td>@XrpAmount.Format(batch.WriterRequiredDrops)</td></tr>
                    <tr><th>Balance</th><td>@XrpAmount.Format(batch.WriterBalanceDrops)</td></tr>
                    <tr><th>Spendable</th><td>@XrpAmount.Format(batch.WriterSpendableDrops)</td></tr>
                </tbody>
            </table>

            <p class="mt-3 mb-0">@Model.MethodLine</p>
            <p class="mt-3 mb-0 xf-muted">Node: <code class="xf-mono">@Model.NodeUrl</code> (@Request.Network)</p>
        </section>
    }
```

and change the `@if (Model.Estimate is { } estimate)` line to `else if (Model.Estimate is { } estimate)`. Add `@inject PublishModel Picker` at the top of the file, and in `PublishAsync` replace `Run.Start(Request);` with:

```csharp
            if (Run.Start(Request)) Picker.RecordStarted(Request);
```

- [ ] **Step 6: Build and commit**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — Expected: 0 errors, 0 warnings.

```bash
git add app/XrplFile.App.Core/EstimateModel.cs app/XrplFile.App/Components/Estimate.razor tests/XrplFile.App.Core.Tests/EstimateModelTests.cs
git commit -m "feat(app): the estimate shows what the payer and the writer each need"
```

---

### Task 20: App — running and resuming a Batch publication, and History from the state

Spec 6.1 (wiring), 5.1 (History from the state), 5.2 step 7, B16, B18.

**Files:**
- Modify: `app/XrplFile.App.Core/PublicationRun.cs`, `app/XrplFile.App.Core/PublicationRecord.cs`, `app/XrplFile.App/MauiProgram.cs`
- Modify: `tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs`

**Interfaces:**
- Consumes: Tasks 14, 16, 18.
- Produces:
  - `PublicationRun` ctor gains `TimeProvider? time = null, BatchStrategyOptions? batchOptions = null` after `autoLock`.
  - `RunSnapshot` init properties: `bool UsesBatch`, `string? StatePath`, `DateTimeOffset? StartedAt`, `long FileBytes`.
  - `PublicationRecord` gains `string? Payer = null` as its last parameter, written only when set.
  - After History holds a run's link, the run's state file is deleted (Spec question 22).
  - `FakeConnectionFactory.EnableBatch()` → `FakeBatchLedgerSink`; while enabled, connections hand out that sink.

- [ ] **Step 1: Let the fake factory hand out the Batch sink**

In `tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs` add:

```csharp
    /// <summary>The Batch node, once <see cref="EnableBatch"/> created it; connections then hand it out as their sink.</summary>
    public FakeBatchLedgerSink? BatchSink { get; private set; }

    /// <summary>
    /// Switches every later connection to a <see cref="FakeBatchLedgerSink"/> over the same ledger.
    /// Not the default: that sink closes a ledger on every read, which the one-account tests do not expect.
    /// </summary>
    public FakeBatchLedgerSink EnableBatch() => BatchSink ??= new FakeBatchLedgerSink(Ledger);
```

and change `Connection.Sink` to `public ILedgerSink Sink => (ILedgerSink?)owner.BatchSink ?? owner.Sink;`.

- [ ] **Step 2: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs`:

```csharp
using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationRunBatchTests : VaultHarness
{
    private static readonly BatchStrategyOptions Fast = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(5),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds: a 400 ms
        // retry step would meet a resolved candidate instead of its resend.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-run-batch-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)> ReadyAsync(int size = 20_000)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);

        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        string writerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "testnet", writer.ClassicAddress, writer.Seed), Token);
        string payerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "payer", "testnet", payer.ClassicAddress, payer.Seed), Token);

        FakeBatchLedgerSink sink = _connections.EnableBatch();
        sink.SetSequence(writer.ClassicAddress, 100);
        sink.SetSequence(payer.ClassicAddress, 500);
        _connections.Ledger.Balances[writer.ClassicAddress] = 100_000_000;
        _connections.Ledger.Balances[payer.ClassicAddress] = 100_000_000;

        PublicationHistory history = new(Paths.HistoryPath);
        PublicationRun run = new(_connections, vault, Paths, history, batchOptions: Fast);
        PublishRequest request = new(
            VectorStream.Bytes("app-batch", size), "application/octet-stream", "file.bin", writerRef, writer.ClassicAddress, "testnet",
            payerRef, payer.ClassicAddress, Path.Combine(_directory, "file.bin"));

        return (run, request, history, vault);
    }

    [Fact]
    public async Task A_batch_publication_succeeds_and_History_records_its_payer_from_the_state()
    {
        (PublicationRun run, PublishRequest request, PublicationHistory history, _) = await ReadyAsync();

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.True(snapshot.UsesBatch);
        Assert.NotNull(snapshot.StatePath);
        Assert.Equal(request.File.Length, snapshot.FileBytes);

        PublicationRecord record = Assert.Single(history.Read());
        Assert.Equal(request.PayerAccount, record.Payer);
        Assert.Equal(snapshot.Report!.Link.ToString(), record.Link);
        Assert.False(File.Exists(snapshot.StatePath), "the finished run's state file outlived its History record");
    }

    [Fact]
    public async Task A_cancelled_batch_run_resumes_from_its_state_with_its_payer()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(40_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        string statePath = run.Snapshot.StatePath!;
        _connections.BatchSink.Transmitting = null;

        Assert.True(run.Resume(statePath, request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public async Task A_one_account_run_resumes_on_the_ticket_strategy_it_recorded_even_when_small()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "w", "testnet", wallet.ClassicAddress, wallet.Seed), Token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;
        byte[] file = new byte[4096];

        Directory.CreateDirectory(Paths.StateDirectory);
        string statePath = Path.Combine(Paths.StateDirectory, "1-run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", wallet.ClassicAddress, reference, "application/octet-stream", "f.bin",
            file.Length, MemoLayout.ChunkBytes, PublicationSession.RootOf(file), 1, "chunks", new Dictionary<int, string>(),
            Strategy: PublicationState.TicketStrategyName).Save(statePath);

        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath));
        Assert.True(run.Resume(statePath, new PublishRequest(file, "application/octet-stream", "f.bin", reference, wallet.ClassicAddress, "testnet")));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Contains("mint", _connections.Sink.Events);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublicationRunBatchTests`
Expected: build FAILS (`batchOptions`, `UsesBatch`, `PublicationRecord.Payer` do not exist).

- [ ] **Step 4: The record and the snapshot**

`app/XrplFile.App.Core/PublicationRecord.cs`: add `using System.Text.Json.Serialization;` and, as the last positional parameter, `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payer = null`, documented `/// <param name="Payer">The account that paid for a Batch publication, or null. Omitted from the file when null; an older application ignores it.</param>`.

In `PublicationRun.cs`, add to `RunSnapshot`:

```csharp
    /// <summary>Whether the run publishes through Batch, with a payer (spec 6.1).</summary>
    public bool UsesBatch { get; init; }

    /// <summary>The run's state file, once it has one: what Resume on the failed-run screen resumes (spec B19).</summary>
    public string? StatePath { get; init; }

    /// <summary>When this run was started, on the run's clock (spec 6.3: the elapsed time).</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The file's size, which says how many passes are still to come (spec B21).</summary>
    public long FileBytes { get; init; }
```

- [ ] **Step 5: The run**

In `app/XrplFile.App.Core/PublicationRun.cs`:

1. Change the primary constructor to add `TimeProvider? time = null, BatchStrategyOptions? batchOptions = null` after `VaultAutoLock? autoLock = null`, and add the fields:

```csharp
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _usesBatch;
    private string? _statePath;
    private DateTimeOffset? _startedAt;
    private long _fileBytes;
```

2. In `Snapshot`, add `UsesBatch = _usesBatch, StatePath = _statePath, StartedAt = _startedAt, FileBytes = _fileBytes,` to the initializer. In `Reset`, add `_usesBatch = false; _statePath = null; _startedAt = null; _fileBytes = 0;`. In `Begin`, after `_network = request.Network;`, add:

```csharp
            _usesBatch = request.PayerAccount is not null;
            _statePath = statePath;
            _startedAt = _time.GetUtcNow();
            _fileBytes = request.File.Length;
```

3. Add the holder for what the failure sentence may say:

```csharp
    /// <summary>What <see cref="PublishErrorMessages.ForRun"/> may say about money, as the run learns it.</summary>
    private sealed class RunAttempt
    {
        public RunFailurePoint Point { get; set; }

        public bool UsesTickets { get; set; }

        public long OwnerReserveDrops { get; set; }
    }
```

4. Replace `RunAsync` from its first line down to (not including) `// Every connection and strategy acquired above has already been disposed` with:

```csharp
    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken, int generation)
    {
        PublicationRunState finalState;
        PublicationReport? report = null;
        PublicationState? finished = null;
        string? finishedPath = null;
        string? failure = null;
        RunAttempt attempt = new() { Point = statePath is null ? RunFailurePoint.BeforeSubmitting : RunFailurePoint.BeforeResuming };

        try
        {
            paths.EnsureCreated();

            ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            try
            {
                // A run resumes on the strategy it recorded (spec B16); a fresh one is Batch when it has a payer (spec B4).
                PublicationState? recorded = statePath is null ? null : PublicationState.Load(statePath);
                bool batch = recorded is null ? request.PayerAccount is not null : recorded.Version == PublicationState.BatchVersion;

                (report, finished, finishedPath) = batch
                    ? await RunBatchAsync(request, statePath, connection, attempt, generation, cancellationToken).ConfigureAwait(false)
                    : await RunOneAccountAsync(request, statePath, recorded, connection, attempt, generation, cancellationToken).ConfigureAwait(false);

                finalState = PublicationRunState.Succeeded;
            }
            finally
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            finalState = PublicationRunState.Cancelled;
        }
        catch (FeeTooLargeForApplicationException)
        {
            failure = PublishErrorMessages.FeeTooLargeForApplication;
            finalState = PublicationRunState.Failed;
        }
        catch (XrplFileException ex)
        {
            failure = PublishErrorMessages.ForRun(ex, attempt.Point, attempt.UsesTickets, attempt.OwnerReserveDrops);
            finalState = PublicationRunState.Failed;
        }
        catch (VaultException ex)
        {
            failure = VaultErrorMessages.For(ex.Error);
            finalState = PublicationRunState.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "The publication's state could not be written to disk, so the run was stopped.";
            finalState = PublicationRunState.Failed;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            failure = "This run's state file could not be read, so it cannot be resumed.";
            finalState = PublicationRunState.Failed;
        }
        catch (Exception)
        {
            failure = "Something went wrong that this application does not have a specific message for. The run was stopped.";
            finalState = PublicationRunState.Failed;
        }

```

5. In the success branch further down, replace the `history.Append(new PublicationRecord(...))` argument with a record written from the state (spec 5.1):

```csharp
                HistoryAppendResult appended = history.Append(new PublicationRecord(
                    report!.Link.ToString(),
                    finished!.Name ?? request.Name,
                    finished.Size,
                    finished.Type,
                    finished.Network,
                    finished.Writer,
                    report.Transactions,
                    report.FeeDropsEstimated,
                    report.FinishedAt,
                    finished.Payer));
```

and, inside the same `try`, after the `if (!appended.Recorded) { … } else if (appended.Note is not null) { … }` block, add:

```csharp
                // History holds the link now, so the run's state has nothing left to protect, and a
                // record the person later forgets cannot come back from it (Spec question 22).
                if (appended.Recorded) DeleteState(finishedPath!);
```

with, beside `DisposeQuietlyAsync`:

```csharp
    /// <summary>
    /// Removes a finished run's state file. One that cannot be removed now is removed at the next
    /// start-up, which finds its link already listed (<c>PublicationHistory.RecordFinishedRuns</c>, Task 22).
    /// </summary>
    private static void DeleteState(string statePath)
    {
        try
        {
            File.Delete(statePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next start-up.
        }
    }
```

6. Add the two paths and the state-path pin:

```csharp
    /// <summary>A one-account run: the code this method held before Batch, resuming on the recorded strategy.</summary>
    private async Task<(PublicationReport Report, PublicationState State, string StatePath)> RunOneAccountAsync(
        PublishRequest request, string? statePath, PublicationState? recorded, ILedgerConnection connection, RunAttempt attempt,
        int generation, CancellationToken cancellationToken)
    {
        SessionOptions estimateOptions = new()
        {
            StateDirectory = paths.StateDirectory,
            TicketsPerBatch = PublishPolicy.TicketsPerBatch,
        };

        PublicationEstimate estimate = await PublicationSession.EstimateAsync(
            connection.Source, request.Account, request.File.Length, estimateOptions, cancellationToken).ConfigureAwait(false);

        if (estimate.FeeDropsPerTransaction > int.MaxValue) throw new FeeTooLargeForApplicationException();

        attempt.UsesTickets = recorded?.Strategy is { } name ? name == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(estimate);
        attempt.OwnerReserveDrops = estimate.OwnerReserveDrops;

        lock (_gate)
        {
            if (_generation == generation)
            {
                _usesTickets = attempt.UsesTickets;
                _ownerReserveDrops = attempt.OwnerReserveDrops;
            }
        }

        SessionOptions options = new()
        {
            StateDirectory = paths.StateDirectory,
            TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            BudgetDrops = estimate.RequiredDrops,
            SourcePath = request.SourcePath,
        };

        VaultSigner signer = await VaultSigner.CreateAsync(vault, request.KeyReference, cancellationToken, autoLock).ConfigureAwait(false);
        ISubmissionStrategy probe = PublishPolicy.CreateFor(recorded?.Strategy, estimate, connection, signer);

        try
        {
            PublicationSession session = statePath is null
                ? await PublicationSession.StartAsync(
                    probe, connection.Source, request.File, request.Type, request.Name, request.Account,
                    request.KeyReference, request.Network, options, cancellationToken).ConfigureAwait(false)
                : await PublicationSession.ResumeAsync(probe, connection.Source, request.File, statePath, options, cancellationToken)
                    .ConfigureAwait(false);

            RecordStatePath(generation, session.StatePath);
            attempt.Point = RunFailurePoint.WhileSubmitting;

            PublicationReport report = await session.RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            return (report, session.State, session.StatePath);
        }
        finally
        {
            await DisposeQuietlyAsync(probe).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A Batch run (spec 6.1): both keys from the vault, the strategy built by the session through
    /// <see cref="PublishPolicy.CreateBatch"/>. A resume passes the payer the request names -- the
    /// recorded one, or the replacement the person chose (spec B18) -- and reports the fence wait.
    /// </summary>
    private async Task<(PublicationReport Report, PublicationState State, string StatePath)> RunBatchAsync(
        PublishRequest request, string? statePath, ILedgerConnection connection, RunAttempt attempt, int generation,
        CancellationToken cancellationToken)
    {
        string payer = request.PayerAccount ?? throw new InvalidOperationException("a Batch run's request names its payer");
        string payerReference = request.PayerReference ?? throw new InvalidOperationException("a Batch run's request names its payer's key");

        VaultSigner writerSigner = await VaultSigner.CreateAsync(vault, request.KeyReference, cancellationToken, autoLock).ConfigureAwait(false);
        VaultSigner payerSigner = await VaultSigner.CreateAsync(vault, payerReference, cancellationToken, autoLock).ConfigureAwait(false);
        BatchStrategy? built = null;

        BatchStrategy Build(BatchBudget budget, IBatchFenceRecorder fence) =>
            built = PublishPolicy.CreateBatch(budget, fence, connection, writerSigner, payerSigner, batchOptions);

        TimeSpan fencePoll = batchOptions?.ChainCheck ?? TimeSpan.FromSeconds(1);

        try
        {
            PublicationSession session;

            if (statePath is null)
            {
                SessionOptions estimateOptions = new() { StateDirectory = paths.StateDirectory };
                BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(
                    connection.Source, request.Account, payer, request.File.Length, estimateOptions, cancellationToken).ConfigureAwait(false);

                SessionOptions options = new()
                {
                    StateDirectory = paths.StateDirectory,
                    BudgetDrops = estimate.WriterRequiredDrops + estimate.PayerRequiredDrops,
                    SourcePath = request.SourcePath,
                    FencePoll = fencePoll,
                };

                session = await PublicationSession.StartAsync(
                    Build, connection.Source, request.File, request.Type, request.Name, request.Account, request.KeyReference,
                    payer, payerReference, request.Network, options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SessionOptions options = new() { StateDirectory = paths.StateDirectory, SourcePath = request.SourcePath, FencePoll = fencePoll };

                session = await PublicationSession.ResumeAsync(
                    Build, connection.Source, request.File, statePath, payer, payerReference, options,
                    new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            }

            RecordStatePath(generation, session.StatePath);
            attempt.Point = RunFailurePoint.WhileSubmitting;

            PublicationReport report = await session.RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);
            return (report, session.State, session.StatePath);
        }
        finally
        {
            if (built is not null) await DisposeQuietlyAsync(built).ConfigureAwait(false);
        }
    }

    /// <summary>Pins the state file onto this run's snapshot, as every other write after Begin is pinned.</summary>
    private void RecordStatePath(int generation, string statePath)
    {
        lock (_gate)
        {
            if (_generation == generation) _statePath = statePath;
        }
    }
```

(If Task 1 Step F3 fired, create both Batch signers with `cacheWallet: true` and call `writerSigner.ClearCache(); payerSigner.ClearCache();` at the start of the `finally`.)

7. `app/XrplFile.App/MauiProgram.cs`: in the `PublicationRun` registration add `provider.GetRequiredService<TimeProvider>()` after `autoLock`.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublicationRunBatchTests -class XrplFile.App.Core.Tests.PublicationRunTests -class XrplFile.App.Core.Tests.UnfinishedModelTests -class XrplFile.App.Core.Tests.RunModelTests -class XrplFile.App.Core.Tests.PublicationHistoryTests`
Expected: PASS.

- [ ] **Step 7: Build and commit**

Run: `dotnet build XrplFile.sln` — Expected: 0 errors, 0 warnings.

```bash
git add app/XrplFile.App.Core/PublicationRun.cs app/XrplFile.App.Core/PublicationRecord.cs app/XrplFile.App/MauiProgram.cs tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs
git commit -m "feat(app): run and resume Batch publications, and record History from the state"
```

---
### Task 21: App — time left, the fence wait, and Resume on the failed-run screen

Spec 6.3, B19, B21, Review Focus 4.

**Files:**
- Modify: `app/XrplFile.App.Core/RunModel.cs`, `app/XrplFile.App.Core/UnfinishedModel.cs` (`ResumeByPathAsync`), `app/XrplFile.App/Components/Run.razor`, `app/XrplFile.App/MauiProgram.cs` (nothing to add if `RunModel` resolves `TimeProvider` itself — see Step 4)
- Test: `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs`

**Interfaces:**
- Consumes: `RunSnapshot.StartedAt`, `FileBytes`, `UsesBatch`, `StatePath` (Task 20); the `"fence"` progress stage (Task 16).
- Produces:
  - `RunModel(PublicationRun run, TimeProvider? time = null)`; `static readonly TimeSpan RateWindow` (30 s); `const double BatchPriorMemosPerSecond = 70`; `static readonly TimeSpan SettlePerPass` (3 × `LedgerTiming.ApproximateLedgerInterval`); `static string StageTextOf(RunSnapshot snapshot, DateTimeOffset now, double? rate)`; `internal string Describe(RunSnapshot snapshot)`; `static bool IsResumable(RunSnapshot snapshot)`
  - `RunView` gains trailing fields `bool CanResume, string? StatePath`
  - `UnfinishedModel.ResumeByPathAsync(string statePath, CancellationToken)` → `Task<bool>`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs`:

```csharp
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class RunModelEtaTests : IDisposable
{
    private const long SevenMegabytes = 7_123_692;

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-eta-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static RunSnapshot Running(string stage, long done, long total, bool batch, TimeSpan elapsed, long fileBytes = SevenMegabytes) =>
        new(PublicationRunState.Running, new PublishProgress(stage, done, total), null, null, "f.bin", "testnet", null)
        {
            UsesBatch = batch,
            StartedAt = Now - elapsed,
            FileBytes = fileBytes,
        };

    private PublicationRun NewRun() =>
        new(new FakeConnectionFactory(), new NeverVault(), new RunPaths(_directory), new PublicationHistory(new RunPaths(_directory).HistoryPath));

    [Fact]
    public void A_measured_rate_gives_the_rate_the_time_left_with_a_settle_per_pass_to_come_and_the_elapsed_time()
    {
        string text = RunModel.StageTextOf(Running("chunks", 1240, 7124, batch: true, TimeSpan.FromSeconds(24)), Now, rate: 52);

        // 5,884 left at 52/s is 113 s, plus a settle for the list pass and one for the manifest: 137 s.
        Assert.Equal($"Writing the file — {1240:N0} of {7124:N0} · 52 chunks/s · about 3 min left · 0:24 elapsed", text);
    }

    [Fact]
    public void Before_thirty_seconds_the_Batch_path_assumes_seventy_memos_a_second_and_shows_no_rate()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(5)), Now, rate: null);

        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · about 3 min left · 0:05 elapsed", text);
    }

    [Fact]
    public void Before_thirty_seconds_the_ticket_path_says_it_is_estimating()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: false, TimeSpan.FromSeconds(5)), Now, rate: null);

        Assert.Equal($"Writing the file — {120:N0} of {7124:N0} · estimating… · 0:05 elapsed", text);
    }

    [Fact]
    public void Nothing_landing_for_thirty_seconds_reads_as_estimating_never_as_an_endless_wait()
    {
        string text = RunModel.StageTextOf(Running("chunks", 120, 7124, batch: true, TimeSpan.FromSeconds(90)), Now, rate: 0);

        Assert.Contains("estimating…", text, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_whose_memos_have_all_landed_claims_no_seconds_left()
    {
        string text = RunModel.StageTextOf(Running("chunks", 7124, 7124, batch: true, TimeSpan.FromSeconds(40)), Now, rate: 300);

        Assert.Equal($"Writing the file — {7124:N0} of {7124:N0} · finishing this pass · 0:40 elapsed", text);
    }

    [Fact]
    public async Task Resume_by_path_of_a_run_no_longer_listed_says_so()
    {
        UnfinishedModel model = new(new RunPaths(_directory), NewRun(), new FakeFilePicker());

        Assert.False(await model.ResumeByPathAsync(Path.Combine(_directory, "gone.json"), TestContext.Current.CancellationToken));
        Assert.Equal("This run is no longer listed under unfinished runs, so there is nothing to resume.", model.Message);
    }

    [Fact]
    public void The_fence_wait_says_what_it_waits_for_and_roughly_how_long()
    {
        Assert.Equal(
            "Waiting for earlier transactions to expire — about 40 s",
            RunModel.StageTextOf(Running("fence", 0, 10, batch: true, TimeSpan.FromSeconds(3)), Now, rate: null));
    }

    [Fact]
    public void A_fence_already_behind_the_ledger_never_shows_a_negative_wait()
    {
        string text = RunModel.StageTextOf(Running("fence", 12, 10, batch: true, TimeSpan.FromSeconds(3)), Now, rate: null);

        Assert.DoesNotContain("-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rate_is_measured_over_the_last_thirty_seconds_on_the_models_clock()
    {
        ManualTimeProvider time = new();
        RunModel model = new(NewRun(), time);
        DateTimeOffset start = time.GetUtcNow();

        RunSnapshot first = Running("chunks", 0, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };
        Assert.DoesNotContain("chunks/s", model.Describe(first), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(30));
        RunSnapshot later = Running("chunks", 1500, 7124, batch: true, TimeSpan.Zero) with { StartedAt = start };

        Assert.Contains("50 chunks/s", model.Describe(later), StringComparison.Ordinal);
        Assert.Contains("0:30 elapsed", model.Describe(later), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_run_whose_state_is_still_unfinished_can_be_resumed_from_its_screen()
    {
        string path = Path.Combine(_directory, "run.json");
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "f.bin", 2500, MemoLayout.ChunkBytes, "AB",
            42, "chunks", new Dictionary<int, string>()).Save(path);

        RunSnapshot failed = new(PublicationRunState.Failed, null, null, "it failed", "f.bin", "testnet", null) { StatePath = path };

        Assert.True(RunModel.IsResumable(failed));
        Assert.False(RunModel.IsResumable(failed with { State = PublicationRunState.Succeeded }));
        Assert.False(RunModel.IsResumable(failed with { StatePath = Path.Combine(_directory, "gone.json") }));

        (PublicationState.Load(path) with { Phase = "done" }).Save(path);
        Assert.False(RunModel.IsResumable(failed));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunModelEtaTests`
Expected: build FAILS (`StageTextOf`, `Describe`, `IsResumable` do not exist).

- [ ] **Step 3: The model**

In `app/XrplFile.App.Core/RunModel.cs`:

1. Add `bool CanResume, string? StatePath` as the last two fields of `RunView`.

2. Change the class header to `public sealed class RunModel(PublicationRun run, TimeProvider? time = null)` and add:

```csharp
    /// <summary>The window the rate is measured over (spec B21).</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(30);

    /// <summary>The Batch path's rate before thirty seconds of data: the platform's conservative figure (spec B21).</summary>
    public const double BatchPriorMemosPerSecond = 70;

    /// <summary>One settle per pass still to come: about three ledgers, the landing and the two settle ledgers (spec B21).</summary>
    public static readonly TimeSpan SettlePerPass = LedgerTiming.ApproximateLedgerInterval * 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _samplesGate = new();
    private readonly List<ProgressSample> _samples = [];

    private readonly record struct ProgressSample(DateTimeOffset At, string Stage, long Done);

    /// <summary>The progress line for this snapshot now, the rate measured from what this model has seen.</summary>
    internal string Describe(RunSnapshot snapshot)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return StageTextOf(snapshot, now, RateOf(snapshot, now));
    }

    /// <summary>
    /// Memos per second over the last <see cref="RateWindow"/> of this stage, or null until the stage
    /// has been watched that long. Samples are taken as the screen renders, once a second.
    /// </summary>
    private double? RateOf(RunSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Progress is not { } progress) return null;

        lock (_samplesGate)
        {
            if (_samples.Count > 0 && !string.Equals(_samples[^1].Stage, progress.Stage, StringComparison.Ordinal)) _samples.Clear();

            _samples.Add(new ProgressSample(now, progress.Stage, progress.Done));
            _samples.RemoveAll(sample => now - sample.At > RateWindow * 2);

            if (now - _samples[0].At < RateWindow) return null;

            ProgressSample from = _samples.Last(sample => now - sample.At >= RateWindow);
            double seconds = (now - from.At).TotalSeconds;

            return seconds <= 0 ? null : (progress.Done - from.Done) / seconds;
        }
    }

    /// <summary>
    /// The progress line (spec 6.3): "Writing the file — 1,240 of 7,124 · 52 chunks/s · about 2 min left ·
    /// 0:24 elapsed". Time left is what remains at the measured rate plus one settle per pass still to
    /// come (spec B21); before a rate is measured the Batch path uses its prior and shows no rate, the
    /// ticket path says "estimating…", and so does any stage whose measured rate is zero. A resume's
    /// fence wait has its own line.
    /// </summary>
    public static string StageTextOf(RunSnapshot snapshot, DateTimeOffset now, double? rate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Progress is not { } progress) return "Starting";

        if (progress.Stage == "fence")
        {
            long ledgers = Math.Max(0, progress.Total - progress.Done);
            return $"Waiting for earlier transactions to expire — about {Duration(LedgerTiming.ApproximateLedgerInterval * ledgers)}";
        }

        string elapsed = snapshot.StartedAt is { } started ? $" · {Clock(now - started)} elapsed" : string.Empty;
        string head = $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}";

        if (progress.Stage is not ("chunks" or "lists")) return head + elapsed;

        string unit = progress.Stage == "chunks" ? "chunks/s" : "lists/s";
        long remaining = Math.Max(0, progress.Total - progress.Done);

        // Every memo of the pass is in; the pass still settles and the writer still has work, so no
        // figure of seconds is claimed for it (Spec question 23).
        if (remaining == 0) return $"{head} · finishing this pass{elapsed}";
        TimeSpan settles = SettlePerPass * PassesAfter(progress.Stage, snapshot.FileBytes);

        if (rate is { } measured && measured > 0)
        {
            return $"{head} · {measured:N0} {unit} · about {Duration(TimeSpan.FromSeconds(remaining / measured) + settles)} left{elapsed}";
        }

        if (rate is null && snapshot.UsesBatch)
        {
            return $"{head} · about {Duration(TimeSpan.FromSeconds(remaining / BatchPriorMemosPerSecond) + settles)} left{elapsed}";
        }

        return $"{head} · estimating…{elapsed}";
    }

    /// <summary>
    /// Whether the failed-run screen offers Resume (spec B19): the run failed and its state file is
    /// still there and unfinished, which is when the unfinished-runs list shows it.
    /// </summary>
    public static bool IsResumable(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.State != PublicationRunState.Failed || snapshot.StatePath is not { } path || !File.Exists(path)) return false;

        try
        {
            return !string.Equals(PublicationState.Load(path).Phase, "done", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The passes still to come after this stage: a list pass when the file has more than one block, and the manifest.</summary>
    private static int PassesAfter(string stage, long fileBytes)
    {
        bool lists = fileBytes > 0 && FileTree.BlockCount(FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes)) > 1;
        return stage == "chunks" ? (lists ? 2 : 1) : 1;
    }

    private static string Duration(TimeSpan span)
    {
        double seconds = Math.Max(1, Math.Ceiling(span.TotalSeconds));
        return seconds < 60 ? $"{seconds:0} s" : $"{Math.Ceiling(seconds / 60):0} min";
    }

    private static string Clock(TimeSpan span)
    {
        TimeSpan whole = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(span.TotalSeconds)));
        return whole.TotalHours >= 1
            ? $"{(int)whole.TotalHours}:{whole.Minutes:00}:{whole.Seconds:00}"
            : $"{(int)whole.TotalMinutes}:{whole.Seconds:00}";
    }
```

3. Replace `StageText` with `public string StageText => Describe(run.Snapshot);`, and in `Capture` replace the `StageText:` argument with `StageText: Describe(snapshot),` and add `CanResume: IsResumable(snapshot), StatePath: snapshot.StatePath` as the last two arguments.

4. In `tests/XrplFile.App.Core.Tests/RunModelTests.cs`, build every `RunModel` with a frozen clock — `new(NewRun(), new ManualTimeProvider())` and likewise for the other constructions — so `AssertViewMatchesProperties` cannot see the elapsed time tick between its two reads.

- [ ] **Step 4: Resume by path**

In `app/XrplFile.App.Core/UnfinishedModel.cs` add:

```csharp
    /// <summary>
    /// Resume from the failed-run screen (spec B19): the same path as the list, found by the run's
    /// state file. <see langword="false"/> with <see cref="Message"/> set when the run is no longer listed.
    /// </summary>
    public async Task<bool> ResumeByPathAsync(string statePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);

        Refresh();

        UnfinishedRow? row = Rows.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.Path), Path.GetFullPath(statePath), StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            Message = "This run is no longer listed under unfinished runs, so there is nothing to resume.";
            return false;
        }

        return await ResumeAsync(row, cancellationToken).ConfigureAwait(false);
    }
```

`RunModel` is registered with `services.AddSingleton<RunModel>()`; the container fills its optional `TimeProvider` from the registered `TimeProvider.System`, so `MauiProgram.cs` needs no change.

- [ ] **Step 5: The screen**

In `app/XrplFile.App/Components/Run.razor`: add `@inject UnfinishedModel Unfinished`; in the action bar, inside `@if (view.IsFinished)`, before the Dismiss button add:

```razor
            @if (view.CanResume)
            {
                <button type="button" class="btn btn-outline-primary" disabled="@_resuming" @onclick="() => ResumeAsync(view.StatePath!)">Resume</button>
            }
```

after the failure alert add:

```razor
        @if (_resumeMessage is { } resumeMessage)
        {
            <div class="alert alert-warning mt-3">@resumeMessage</div>
        }
```

and in `@code`:

```csharp
    private bool _resuming;
    private string? _resumeMessage;

    private async Task ResumeAsync(string statePath)
    {
        if (_resuming) return;

        _resuming = true;
        _resumeMessage = null;

        try
        {
            if (!await Unfinished.ResumeByPathAsync(statePath, _cts.Token)) _resumeMessage = Unfinished.Message;
        }
        finally
        {
            _resuming = false;
        }
    }
```

(When the payer key has left the vault, `Unfinished.Message` asks for another payer and the unfinished-runs list, under the Publish tab after Dismiss, offers the choice — Task 22.)

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunModelEtaTests -class XrplFile.App.Core.Tests.RunModelTests -class XrplFile.App.Core.Tests.UnfinishedModelTests`
Expected: PASS.

- [ ] **Step 7: Build and commit**

Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — Expected: 0 errors, 0 warnings.

```bash
git add app/XrplFile.App.Core/RunModel.cs app/XrplFile.App.Core/UnfinishedModel.cs app/XrplFile.App/Components/Run.razor tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs tests/XrplFile.App.Core.Tests/RunModelTests.cs
git commit -m "feat(app): time left, the fence wait and Resume on the run screen"
```

---

### Task 22: App — unfinished runs and History

Spec 6.4, 5.1 (History recovery), 5.2 steps 1 and 5, B17, B18.

**Files:**
- Modify: `app/XrplFile.App.Core/UnfinishedModel.cs`, `app/XrplFile.App.Core/PublicationRecord.cs`, `app/XrplFile.App.Core/PublicationHistory.cs`, `app/XrplFile.App/MauiProgram.cs`, `app/XrplFile.App/Components/Unfinished.razor`, `app/XrplFile.App/Components/History.razor`
- Test: `tests/XrplFile.App.Core.Tests/UnfinishedModelBatchTests.cs`, `tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs` (append)

**Interfaces:**
- Produces:
  - `UnfinishedRow` init properties: `string? Root`, `string? SourcePath`, `string? Payer`, `string? PayerKeyReference`, `bool IsBatch`, `string? Strategy`, `string? Method`
  - `UnfinishedModel` ctor gains `IKeyVault? vault = null`; `Task RefreshAsync(CancellationToken)`; `UnfinishedRow? NeedsPayer`; `IReadOnlyList<VaultEntryInfo> PayerChoices`; `Task<bool> ResumeWithPayerAsync(UnfinishedRow row, string payerReference, CancellationToken)`; `static string? MethodOf(UnfinishedRow row, IReadOnlyDictionary<string, string> labels)`
  - `PublicationRecord` gains `bool? Recovered = null` as its last parameter, written only when set (an older application ignores it); a recovered record carries `0` transactions and `0` fees, its numbers staying non-nullable (Spec question 17)
  - `int PublicationHistory.RecordFinishedRuns(string stateDirectory)` — records each finished run whose state file still exists, which means its History write never happened, then deletes that state file (Spec question 22)
  - `UnfinishedModel.ShowsTicketNoteFor` decides by `IsBatch` and the recorded `Strategy` before the size

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/UnfinishedModelBatchTests.cs`:

```csharp
using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class UnfinishedModelBatchTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-unfinished-batch-").FullName;
    private readonly FakeFilePicker _picker = new();

    /// <summary>The run a test may have resumed: awaited before the vault and directory go away.</summary>
    private PublicationRun? _run;

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        if (_run is not null) await _run.Completion;

        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A vault holding a writer and a payer, and a Batch run's state file naming them, for a file on disk.</summary>
    private async Task<(UnfinishedModel Model, KeyVault Vault, string StatePath, string FilePath, string PayerReference)> BatchRunAsync(byte[] bytes)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        string writerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "testnet", writer.ClassicAddress, writer.Seed), Token);
        string payerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "Payer key", "testnet", payer.ClassicAddress, payer.Seed), Token);

        string filePath = Path.Combine(_directory, "source.bin");
        await File.WriteAllBytesAsync(filePath, bytes, Token);

        Directory.CreateDirectory(Paths.StateDirectory);
        string statePath = Path.Combine(Paths.StateDirectory, "1-batch.json");
        new PublicationState(
            PublicationState.BatchVersion, "testnet", writer.ClassicAddress, writerRef, "application/octet-stream", "source.bin",
            bytes.Length, MemoLayout.ChunkBytes, PublicationSession.RootOf(bytes), 1, "chunks", new Dictionary<int, string>(),
            Strategy: PublicationState.BatchStrategyName, SourcePath: filePath, Payer: payer.ClassicAddress, PayerKeyReference: payerRef, Fence: 0)
            .Save(statePath);

        PublicationRun run = new(new FakeConnectionFactory(), vault, Paths, new PublicationHistory(Paths.HistoryPath));
        _run = run;
        UnfinishedModel model = new(Paths, run, _picker, vault);
        await model.RefreshAsync(Token);

        return (model, vault, statePath, filePath, payerRef);
    }

    [Fact]
    public async Task A_batch_run_is_listed_with_its_method_and_payer_label()
    {
        (UnfinishedModel model, _, _, _, _) = await BatchRunAsync(new byte[3000]);

        Assert.Equal("Batch, paid by Payer key", Assert.Single(model.Rows).Method);
    }

    [Theory]
    [InlineData("tickets", 3000L, "Tickets")]
    [InlineData("sequence", 3000L, "Sequence")]
    [InlineData(null, 3000L, "Sequence")]
    [InlineData(null, 500_000L, "Tickets")]
    public void A_one_account_run_is_listed_by_its_recorded_or_implied_method(string? strategy, long size, string expected)
    {
        UnfinishedRow row = new("p", "testnet", "rW", "k", "t", "n", size, "Writing the file", 3, null) { Strategy = strategy };

        Assert.Equal(expected, UnfinishedModel.MethodOf(row, new Dictionary<string, string>()));
    }

    [Fact]
    public void Batch_runs_and_recorded_sequence_runs_never_show_the_ticket_note()
    {
        UnfinishedRow big = new("p", "testnet", "rW", "k", "t", "n", 3_145_728, "Writing the file", 3146, null);

        Assert.False(UnfinishedModel.ShowsTicketNoteFor([big with { IsBatch = true }]));
        Assert.False(UnfinishedModel.ShowsTicketNoteFor([big with { Strategy = PublicationState.SequenceStrategyName }]));
        Assert.True(UnfinishedModel.ShowsTicketNoteFor([big]));
    }

    [Fact]
    public async Task Resume_reads_the_recorded_file_and_never_opens_the_picker()
    {
        (UnfinishedModel model, _, _, _, _) = await BatchRunAsync(new byte[3000]);
        _picker.PickThrows = new InvalidOperationException("the picker must not open");

        Assert.True(await model.ResumeAsync(model.Rows[0], Token));
        Assert.Null(model.Message);
    }

    [Theory]
    [InlineData("moved")]
    [InlineData("changed")]
    public async Task A_moved_or_changed_file_falls_back_to_the_picker_with_the_reason(string what)
    {
        byte[] bytes = new byte[3000];
        (UnfinishedModel model, _, _, string filePath, _) = await BatchRunAsync(bytes);

        if (what == "moved") File.Delete(filePath);
        else await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)1, 3000).ToArray(), Token);

        _picker.PathToReturn = null;

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));
        Assert.Equal(
            what == "moved"
                ? $"The file this run was publishing is no longer at {filePath}. Choose it again."
                : $"The file at {filePath} has changed since the run started. Choose the original file.",
            model.Message);
    }

    [Fact]
    public async Task A_picked_file_that_is_not_the_runs_is_refused_by_its_root()
    {
        (UnfinishedModel model, _, _, string filePath, _) = await BatchRunAsync(new byte[3000]);
        File.Delete(filePath);
        string other = Path.Combine(_directory, "other.bin");
        await File.WriteAllBytesAsync(other, Enumerable.Repeat((byte)7, 3000).ToArray(), Token);
        _picker.PathToReturn = other;

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));
        Assert.Equal("That file is not the one this run was publishing.", model.Message);
    }

    [Fact]
    public async Task With_the_payer_key_gone_the_person_chooses_another_payer_on_the_runs_network()
    {
        (UnfinishedModel model, KeyVault vault, string statePath, _, string payerRef) = await BatchRunAsync(new byte[3000]);
        XrplWallet replacement = XrplWallet.Generate();
        string replacementRef = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "new payer", "TESTNET", replacement.ClassicAddress, replacement.Seed), Token);
        await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "devnet key", "devnet", "rDevnetOnly", XrplWallet.Generate().Seed), Token);
        await vault.DeleteAsync(payerRef, Token);

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));
        Assert.NotNull(model.NeedsPayer);
        Assert.Equal(["new payer"], model.PayerChoices.Select(key => key.Label).ToArray());

        Assert.True(await model.ResumeWithPayerAsync(model.NeedsPayer!, replacementRef, Token));
        Assert.Null(model.NeedsPayer);
    }
}
```

Append to `tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs`, inside the class (it has `_directory` and `HistoryPath`):

```csharp
    private string StateDirectory => Path.Combine(_directory, "runs");

    private string FinishedState(string name, string manifestHash)
    {
        Directory.CreateDirectory(StateDirectory);
        string path = Path.Combine(StateDirectory, name);
        new PublicationState(
            PublicationState.CurrentVersion, "testnet", "rWriter", "key-1", "text/plain", "done.txt", 2500, MemoLayout.ChunkBytes, "AB",
            42, "done", new Dictionary<int, string>(), Strategy: "sequence", ManifestHash: manifestHash).Save(path);
        return path;
    }

    [Fact]
    public void A_finished_run_missing_from_History_is_recorded_from_its_state_and_its_state_removed()
    {
        string statePath = FinishedState("1-done.json", new string('A', 64));
        PublicationHistory history = new(HistoryPath);

        Assert.Equal(1, history.RecordFinishedRuns(StateDirectory));

        PublicationRecord record = Assert.Single(history.Read());
        Assert.Equal(new XrplFileLink("testnet", new string('A', 64)).ToString(), record.Link);
        Assert.Equal("done.txt", record.Name);
        Assert.Equal(0, record.Transactions);
        Assert.True(record.Recovered);
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void A_record_the_person_forgot_does_not_come_back_at_start_up()
    {
        FinishedState("1-done.json", new string('B', 64));
        PublicationHistory history = new(HistoryPath);
        history.RecordFinishedRuns(StateDirectory);

        history.Forget(new XrplFileLink("testnet", new string('B', 64)).ToString());

        Assert.Equal(0, history.RecordFinishedRuns(StateDirectory));
        Assert.Empty(history.Read());
    }

    [Fact]
    public void A_state_whose_link_is_already_listed_is_only_removed()
    {
        string link = new XrplFileLink("testnet", new string('C', 64)).ToString();
        PublicationHistory history = new(HistoryPath);
        history.Append(new PublicationRecord(link, "done.txt", 2500, "text/plain", "testnet", "rWriter", 4, 40, DateTimeOffset.UtcNow));
        string statePath = FinishedState("1-done.json", new string('C', 64));

        Assert.Equal(0, history.RecordFinishedRuns(StateDirectory));
        Assert.Single(history.Read());
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public void A_recovered_record_is_written_so_an_older_application_still_reads_the_list()
    {
        FinishedState("1-done.json", new string('D', 64));
        new PublicationHistory(HistoryPath).RecordFinishedRuns(StateDirectory);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(HistoryPath));
        System.Text.Json.JsonElement written = document.RootElement[0];

        Assert.Equal(System.Text.Json.JsonValueKind.Number, written.GetProperty("Transactions").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, written.GetProperty("FeeDropsEstimated").ValueKind);
        Assert.False(written.TryGetProperty("Payer", out _));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.UnfinishedModelBatchTests -class XrplFile.App.Core.Tests.PublicationHistoryTests`
Expected: build FAILS.

- [ ] **Step 3: The record and History recovery**

`app/XrplFile.App.Core/PublicationRecord.cs`: add as the last positional parameter `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Recovered = null`, documented `/// <param name="Recovered">True for a record written at start-up from a finished run's state, which has no report: its transaction and fee figures are 0, not measured. Omitted otherwise; an older application ignores it.</param>`. `Transactions` and `FeeDropsEstimated` stay `long`: an older application reads a null there as a broken file and replaces the whole list.

In `app/XrplFile.App.Core/PublicationHistory.cs` add:

```csharp
    /// <summary>
    /// Records every finished run whose state file still exists (spec 5.1): the application deletes a
    /// run's state once History holds its link, so a "done" state still on disk is a run whose History
    /// write never happened. Its link is recorded unless already listed, and the state file is deleted
    /// either way -- which is also why a link the person forgot never comes back (Spec question 22).
    /// Called once at start-up. A state that cannot be read or named as a link is left alone. Returns
    /// how many records were added.
    /// </summary>
    public int RecordFinishedRuns(string stateDirectory)
    {
        if (!Directory.Exists(stateDirectory)) return 0;

        HashSet<string> known = new(Read().Select(record => record.Link), StringComparer.Ordinal);
        int recorded = 0;

        foreach (string path in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            PublicationState state;
            string link;

            try
            {
                state = PublicationState.Load(path);

                if (!string.Equals(state.Phase, "done", StringComparison.Ordinal) || state.ManifestHash is not { } manifest) continue;

                link = new XrplFileLink(state.Network, manifest).ToString();
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException
                or UnauthorizedAccessException or ArgumentException or XrplFileException)
            {
                continue;
            }

            if (!known.Contains(link))
            {
                HistoryAppendResult appended = Append(new PublicationRecord(
                    link, state.Name ?? Path.GetFileName(path), state.Size, state.Type, state.Network, state.Writer,
                    0, 0, File.GetLastWriteTimeUtc(path), state.Payer, Recovered: true));

                if (!appended.Recorded) continue;

                known.Add(link);
                recorded++;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start-up, which finds the link already listed.
            }
        }

        return recorded;
    }
```

In `app/XrplFile.App/MauiProgram.cs`, replace the `PublicationHistory` registration with:

```csharp
        services.AddSingleton(provider =>
        {
            RunPaths runPaths = provider.GetRequiredService<RunPaths>();
            PublicationHistory history = new(runPaths.HistoryPath);

            // A run that finished and crashed before its link was listed (spec 5.1).
            history.RecordFinishedRuns(runPaths.StateDirectory);

            return history;
        });
```

- [ ] **Step 4: The unfinished-runs model**

In `app/XrplFile.App.Core/UnfinishedModel.cs`:

1. Give `UnfinishedRow` a body with the init properties:

```csharp
{
    public string? Root { get; init; }

    public string? SourcePath { get; init; }

    public string? Payer { get; init; }

    public string? PayerKeyReference { get; init; }

    public bool IsBatch { get; init; }

    /// <summary>The strategy the state recorded, or null for a version-2 file written before strategies were.</summary>
    public string? Strategy { get; init; }

    /// <summary>"Batch, paid by &lt;label&gt;", "Tickets" or "Sequence" (spec 6.4).</summary>
    public string? Method { get; init; }
}
```

2. Change the header to `public sealed class UnfinishedModel(RunPaths paths, PublicationRun run, IFilePickerService picker, IKeyVault? vault = null)` and add `using XrplFile.Keys;`. Replace the body of `ShowsTicketNoteFor` (keep its null check) so a Batch row, or a row recorded as a sequence run, never shows the ticket note:

```csharp
        return rows.Any(row => row.Unreadable is null && !row.IsBatch
            && (row.Strategy is { } strategy ? strategy == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(row.Size)));
```

3. In `Refresh`, build readable rows with the new properties: `with`-style initializer `{ Root = state.Root, SourcePath = state.SourcePath, Payer = state.Payer, PayerKeyReference = state.PayerKeyReference, IsBatch = state.Version == PublicationState.BatchVersion, Strategy = state.Strategy }`, then set `Method` for every readable row with `MethodOf(row, new Dictionary<string, string>())` (addresses stand in for labels until `RefreshAsync` reads the vault). Also set `NeedsPayer = null;` at the start.

4. Add:

```csharp
    /// <summary>The run waiting for a payer because its payer key left the vault (spec B18), or null.</summary>
    public UnfinishedRow? NeedsPayer { get; private set; }

    /// <summary>The keys on that run's network, the writer's own account left out.</summary>
    public IReadOnlyList<VaultEntryInfo> PayerChoices { get; private set; } = [];

    /// <summary>Refresh, then names Batch payers by their vault labels when the vault can be read.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Refresh();

        IReadOnlyList<VaultEntryInfo> keys = await KeysAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        foreach (VaultEntryInfo key in keys) labels[key.Reference] = key.Label;

        Rows = [.. Rows.Select(row => row.Unreadable is null ? row with { Method = MethodOf(row, labels) } : row)];
    }

    public static string? MethodOf(UnfinishedRow row, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(labels);

        if (row.Unreadable is not null) return null;

        if (row.IsBatch)
        {
            string payer = row.PayerKeyReference is { } reference && labels.TryGetValue(reference, out string? label) ? label : row.Payer ?? "an unknown account";
            return $"Batch, paid by {payer}";
        }

        bool tickets = row.Strategy is { } strategy ? strategy == PublicationState.TicketStrategyName : PublishPolicy.UsesTickets(row.Size);
        return tickets ? "Tickets" : "Sequence";
    }

    /// <summary>Resumes a Batch run whose payer key left the vault, with the key the person chose (spec 5.2 step 5).</summary>
    public async Task<bool> ResumeWithPayerAsync(UnfinishedRow row, string payerReference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(payerReference);

        Message = null;
        VaultEntryInfo? payer = PayerChoices.FirstOrDefault(key => string.Equals(key.Reference, payerReference, StringComparison.Ordinal));

        if (payer is null)
        {
            Message = "Choose a payer on this run's network.";
            return false;
        }

        NeedsPayer = null;
        PayerChoices = [];

        return await ResumeWithAsync(row, payer.Reference, payer.Address, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VaultEntryInfo>> KeysAsync(CancellationToken cancellationToken)
    {
        if (vault is null) return [];

        try
        {
            return await vault.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException)
        {
            return [];
        }
    }

    /// <summary>
    /// The bytes at the path the run recorded, checked against its size and root (spec B17); null, with
    /// <see cref="Message"/> saying why, when the file has moved, cannot be read, or has changed.
    /// </summary>
    private async Task<byte[]?> RecordedSourceAsync(UnfinishedRow row, CancellationToken cancellationToken)
    {
        if (row.SourcePath is not { } source) return null;

        if (!File.Exists(source))
        {
            Message = $"The file this run was publishing is no longer at {source}. Choose it again.";
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"The file at {source} could not be read. Choose it again.";
            return null;
        }

        if (!IsTheRunsFile(row, bytes))
        {
            Message = $"The file at {source} has changed since the run started. Choose the original file.";
            return null;
        }

        return bytes;
    }

    /// <summary>The same size and the same Merkle root as the run recorded: the same bytes are hashed and published.</summary>
    private static bool IsTheRunsFile(UnfinishedRow row, byte[] bytes) =>
        bytes.LongLength == row.Size && row.Root is { } root && bytes.Length > 0
        && string.Equals(PublicationSession.RootOf(bytes), root, StringComparison.Ordinal);
```

5. Replace `ResumeAsync` with:

```csharp
    /// <summary>
    /// Resumes a run (spec 5.2): from the file it recorded, without a picker, unless that fails; a
    /// Batch run whose payer key left the vault first asks for another payer. <see langword="false"/>
    /// when nothing was started, with <see cref="Message"/> saying why -- except when the person
    /// cancelled the picker, which only leaves the reason the picker was opened for.
    /// </summary>
    public async Task<bool> ResumeAsync(UnfinishedRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        Message = null;
        NeedsPayer = null;
        PayerChoices = [];

        if (row.Unreadable is not null)
        {
            Message = "This run's state file cannot be read, so there is nothing to resume from. It can be deleted.";
            return false;
        }

        if (row.IsBatch && vault is not null)
        {
            IReadOnlyList<VaultEntryInfo> keys = await KeysAsync(cancellationToken).ConfigureAwait(false);

            if (!keys.Any(key => string.Equals(key.Reference, row.PayerKeyReference, StringComparison.Ordinal)))
            {
                XrplFileLink.TryNormalizeNetwork(row.Network ?? string.Empty, out string? network);

                NeedsPayer = row;
                PayerChoices = [.. keys.Where(key =>
                    !string.Equals(key.Address, row.Writer, StringComparison.Ordinal)
                    && XrplFileLink.TryNormalizeNetwork(key.Network, out string? other)
                    && string.Equals(other, network, StringComparison.Ordinal))];
                Message = "The key that paid for this run is no longer in the vault. Choose another payer on the same network to resume it.";
                return false;
            }
        }

        return await ResumeWithAsync(row, row.PayerKeyReference, row.Payer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ResumeWithAsync(UnfinishedRow row, string? payerReference, string? payerAccount, CancellationToken cancellationToken)
    {
        byte[]? bytes = await RecordedSourceAsync(row, cancellationToken).ConfigureAwait(false);
        string? sourcePath = row.SourcePath;

        if (bytes is null)
        {
            string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
            if (path is null) return false;

            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Message = "That file could not be read.";
                return false;
            }

            if (bytes.LongLength != row.Size)
            {
                Message =
                    $"That file is {bytes.LongLength:N0} bytes and this run was publishing {row.Size:N0}. "
                    + "Resuming needs the same file the run started with.";
                return false;
            }

            if (!IsTheRunsFile(row, bytes))
            {
                Message = "That file is not the one this run was publishing.";
                return false;
            }

            sourcePath = path;
        }

        Message = null;

        PublishRequest request = new(
            bytes,
            row.Type ?? "application/octet-stream",
            row.Name ?? Path.GetFileName(sourcePath ?? row.Path),
            row.KeyReference!,
            row.Writer!,
            row.Network!,
            row.IsBatch ? payerReference : null,
            row.IsBatch ? payerAccount : null,
            sourcePath);

        if (!run.Resume(row.Path, request))
        {
            Message = "A publication is already running.";
            return false;
        }

        return true;
    }
```

The existing test `An_interrupted_readable_run_resumes_with_the_same_bytes` (and any other that resumes through the picker) still passes: its state has no `SourcePath`, so the picker opens, and the picked file is the run's own.

- [ ] **Step 5: The screens**

`app/XrplFile.App/Components/Unfinished.razor`:
- `OnInitialized` becomes `protected override async Task OnInitializedAsync() => await Model.RefreshAsync(_cts.Token);`
- add a `<th>Method</th>` header after Network and `<td>@row.Method</td>` after `<td>@row.Network</td>`; the unreadable row's `colspan` becomes 5.
- after the `Model.Message` alert add:

```razor
    @if (Model.NeedsPayer is { } waiting)
    {
        <section class="xf-card">
            <label class="form-label" for="xf-new-payer">Paid by</label>
            <select id="xf-new-payer" class="form-select" @bind="_payerChoice">
                <option value="">Choose a key&hellip;</option>
                @foreach (VaultEntryInfo key in Model.PayerChoices)
                {
                    <option value="@key.Reference">@key.Label &mdash; @key.Address</option>
                }
            </select>
            <button type="button" class="btn btn-primary mt-2" disabled="@(_busy || string.IsNullOrEmpty(_payerChoice))"
                    @onclick="() => ResumeWithPayerAsync(waiting)">
                Resume with this payer
            </button>
        </section>
    }
```

- in `@code` add `private string? _payerChoice;` and:

```csharp
    private async Task ResumeWithPayerAsync(UnfinishedRow row)
    {
        if (_busy || string.IsNullOrEmpty(_payerChoice)) return;

        _busy = true;
        try
        {
            await Model.ResumeWithPayerAsync(row, _payerChoice, _cts.Token);
        }
        finally
        {
            _busy = false;
        }
    }
```

`app/XrplFile.App/Components/History.razor`: change `<td>@record.Name</td>` to:

```razor
                            <td>
                                @record.Name
                                @if (record.Payer is { } payer)
                                {
                                    <div class="xf-muted small">Paid by <span class="xf-mono">@payer</span></div>
                                }
                            </td>
```

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests`
Expected: PASS (the whole App.Core suite: the record's nullable numbers and the model's new constructor parameter touch other tests).

- [ ] **Step 7: Build and commit**

Run: `dotnet build XrplFile.sln` — Expected: 0 errors, 0 warnings.

```bash
git add app/XrplFile.App.Core/UnfinishedModel.cs app/XrplFile.App.Core/PublicationRecord.cs app/XrplFile.App.Core/PublicationHistory.cs app/XrplFile.App/MauiProgram.cs app/XrplFile.App/Components/Unfinished.razor app/XrplFile.App/Components/History.razor tests/XrplFile.App.Core.Tests/UnfinishedModelBatchTests.cs tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs
git commit -m "feat(app): resume from the recorded file, choose a new payer, and recover finished links"
```

---

### Task 23: Stand integration tests

Spec 7.4, and the signing-time measurement on a real 1 MB run (spec 4.2).

**Files:**
- Create: `tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs`, `tests/XrplFile.Core.Tests/LedgerPump.cs`

**Interfaces:**
- Consumes: everything in Core; `StandaloneFixture`, `DroppingProxy` (Task 3), `ScriptedStrategy` (Task 15).
- Produces: `LedgerPump(string endpoint)` — closes a stand ledger every 500 ms until disposed, so deadlines and fences pass in seconds rather than minutes.

- [ ] **Step 1: Write the helper and the tests**

`tests/XrplFile.Core.Tests/LedgerPump.cs`:

```csharp
using Xrpl.Client;
using Xrpl.Models.Methods;

namespace XrplFile.Tests;

/// <summary>Closes a standalone ledger every half second on its own connection, until disposed.</summary>
internal sealed class LedgerPump : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumping;

    public LedgerPump(string endpoint) => _pumping = PumpAsync(endpoint, _stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        try
        {
            await _pumping;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static async Task PumpAsync(string endpoint, CancellationToken token)
    {
        IXrplClient client = new XrplClient(endpoint);
        await client.Connect(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                await client.AnyRequest(new BaseRequest { Command = "ledger_accept" }, token);
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }
        finally
        {
            await client.Disconnect();
        }
    }
}
```

`tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Client;
using Xrpl.Wallet;
using XrplFile.Keys;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec 7.4: the Batch path end to end on the stand, with keys that never leave a vault.</summary>
[Trait("Category", "Integration")]
[Collection("Standalone")]
public class BatchPublicationStandaloneTests : IDisposable
{
    private static readonly KdfParameters FastParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
    private static readonly ReadOnlyMemory<byte> Password = Encoding.UTF8.GetBytes("open sesame");
    private static readonly BatchStrategyOptions Options = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(500),
        ProgressSweep = TimeSpan.FromSeconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-batch-stand-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Endpoint =>
        Environment.GetEnvironmentVariable("XRPL_FILE_NODE_URL") is { Length: > 0 } url ? url : "ws://localhost:6006";

    private SessionOptions Session => new() { StateDirectory = Path.Combine(_directory, "state"), FencePoll = TimeSpan.FromMilliseconds(500) };

    private sealed record Keys(KeyVault Vault, VaultSigner Writer, VaultSigner Payer, VaultSigner OtherPayer, XrplWallet WriterWallet) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Vault.DisposeAsync();
    }

    private async Task<Keys> KeysAsync(IXrplClient client, XrplClientLedgerSink sink, CancellationToken token)
    {
        XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
        XrplWallet payer = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);
        XrplWallet other = await StandaloneFixture.FundedAccountAsync(client, sink, 1_000_000_000, token);

        KeyVault vault = new(Path.Combine(_directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), FastParameters);
        await vault.CreateAsync(Password, token);

        async Task<VaultSigner> StoreAsync(string label, XrplWallet wallet) =>
            await VaultSigner.CreateAsync(
                vault, await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, label, "1", wallet.ClassicAddress, wallet.Seed), token), token);

        return new Keys(vault, await StoreAsync("writer", writer), await StoreAsync("payer", payer), await StoreAsync("other", other), writer);
    }

    private static BatchStrategyFactory Factory(ILedgerSink sink, ILedgerSource source, Keys keys, VaultSigner payer, List<BatchStrategy>? built = null) =>
        (budget, fence) =>
        {
            BatchStrategy strategy = new(sink, source, keys.Writer, payer, budget, fence, Options);
            built?.Add(strategy);
            return strategy;
        };

    private Task<PublicationSession> StartAsync(ILedgerSink sink, ILedgerSource source, Keys keys, byte[] file, CancellationToken token, List<BatchStrategy>? built = null) =>
        PublicationSession.StartAsync(
            Factory(sink, source, keys, keys.Payer, built), source, file, "application/octet-stream", "batch.bin",
            keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, token);

    /// <summary>Every chunk index the writer's successful records carry from <paramref name="from"/>, with duplicates.</summary>
    private static async Task<List<(long Index, string? Parent)>> ChunkRecordsAsync(ILedgerSource source, string writer, long from, CancellationToken token)
    {
        List<(long, string?)> chunks = [];
        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, from, await source.CurrentLedgerAsync(token), true, token))
        {
            if (tx.Succeeded && MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index) chunks.Add((index, tx.ParentBatchID));
        }

        return chunks;
    }

    private static async Task<int> ManifestRecordsAsync(ILedgerSource source, string writer, long from, CancellationToken token)
    {
        int count = 0;
        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, from, await source.CurrentLedgerAsync(token), true, token))
        {
            if (tx.Succeeded && MemoLayout.IsManifest(tx.MemoTypeHex)) count++;
        }

        return count;
    }

    /// <summary>A crash between the manifest validating and "done" being written.</summary>
    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null, ChunkHashes = new Dictionary<int, string>() }).Save(statePath);
    }

    [Fact]
    public async Task A_megabyte_goes_through_two_vault_signers_and_reads_back_and_signing_is_measured()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-1mb", 1_000_000);
            List<BatchStrategy> built = [];

            PublicationSession session = await StartAsync(sink, source, keys, file, token, built);
            PublicationReport report = await session.RunAsync(null, token);

            ReadResult read = await new XrplFileReader(source).ReadAsync(report.Link, token);
            Assert.Equal(file, read.Bytes);
            Assert.Equal(keys.Writer.Account, read.Writer);

            double share = built[0].SigningTime / report.Took;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"1 MB: {report.Took.TotalSeconds:F1} s, signing {built[0].SigningTime.TotalSeconds:F2} s ({share:P0}); payer paid {report.PayerFeeDrops} drops");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_whose_connection_the_node_closes_in_the_middle_still_lands_every_chunk()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        await using DroppingProxy proxy = new(new Uri(Endpoint));
        IXrplClient client = ClientKeeper.CreateClient(proxy.Endpoint.ToString());
        await client.Connect(token);

        try
        {
            XrplClientLedgerSource source = new(client);
            DroppingSink sink = new(new XrplClientLedgerSink(client), proxy, dropAt: 20);
            await using Keys keys = await KeysAsync(client, new XrplClientLedgerSink(client), token);
            byte[] file = VectorStream.Bytes("batch-drop", 500_000);

            PublicationSession session = await StartAsync(sink, source, keys, file, token);
            PublicationReport report = await session.RunAsync(null, token);

            Assert.True(sink.Dropped);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_stopped_with_batches_in_flight_resumes_with_another_payer_behind_its_fence_and_pays_no_chunk_twice()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            RecordingSource source = new(new XrplClientLedgerSource(client));
            await using Keys keys = await KeysAsync(client, inner, token);
            byte[] file = VectorStream.Bytes("batch-stop", 500_000);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            StoppingSink stopping = new(inner, stop, stopAfter: 10);
            PublicationSession first = await StartAsync(stopping, source, keys, file, stop.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));

            long fence = PublicationState.Load(first.StatePath).Fence!.Value;
            Assert.True(fence > 0);
            source.StartRecording();

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(inner, source, keys, keys.OtherPayer), source, file, first.StatePath,
                keys.OtherPayer.Account, "other", Session, null, token);

            Assert.True(source.FirstHistoryReadTo > fence + 2, $"history read up to {source.FirstHistoryReadTo} with the fence at {fence}");

            PublicationReport report = await resumed.RunAsync(null, token);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);

            List<(long Index, string? Parent)> chunks = await ChunkRecordsAsync(source, keys.Writer.Account, first.State.StartedInLedger, token);
            Assert.Equal(chunks.Count, chunks.Select(chunk => chunk.Index).Distinct().Count());
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_batch_run_stopped_after_its_manifest_validated_is_finished_without_sending()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-b27", 50_000);

            PublicationSession run = await StartAsync(sink, source, keys, file, token);
            PublicationReport original = await run.RunAsync(null, token);
            ForgetTheEnding(run.StatePath);
            uint writerSequence = await sink.SequenceAsync(keys.Writer.Account, validated: true, token);

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(sink, source, keys, keys.Payer), source, file, run.StatePath, keys.Payer.Account, "payer", Session, null, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(original.Link, report.Link);
            Assert.Equal(writerSequence, await sink.SequenceAsync(keys.Writer.Account, validated: true, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_ticket_run_stopped_after_its_manifest_validated_is_finished_without_sending()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("ticket-b27", 120_000);
            SessionOptions options = Session;

            PublicationEstimate priced = await PublicationSession.EstimateAsync(source, keys.Writer.Account, file.Length, options, token);
            TicketStrategyOptions ticketOptions = new() { FeeDrops = (int)priced.FeeDropsPerTransaction };

            await using TicketStrategy first = new(sink, source, keys.Writer, ticketOptions);
            PublicationSession run = await PublicationSession.StartAsync(
                first, source, file, "application/octet-stream", null, keys.Writer.Account, "writer", "1", options, token);
            PublicationReport original = await run.RunAsync(null, token);
            ForgetTheEnding(run.StatePath);
            int manifests = await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token);

            await using TicketStrategy second = new(sink, source, keys.Writer, ticketOptions);
            PublicationSession resumed = await PublicationSession.ResumeAsync(second, source, file, run.StatePath, options, token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.Equal(original.Link, report.Link);
            Assert.Equal(manifests, await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_run_stopped_with_its_manifest_in_flight_waits_for_the_fence_then_finds_it()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, inner, token);
            byte[] file = VectorStream.Bytes("batch-manifest-flight", 50_000);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            ManifestSink manifestSink = new(inner, stop, transmitFirst: true);
            PublicationSession run = await StartAsync(manifestSink, source, keys, file, stop.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.RunAsync(null, stop.Token));
            Assert.True(manifestSink.SawManifest);

            List<PublishProgress> waited = [];
            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(inner, source, keys, keys.Payer), source, file, run.StatePath, keys.Payer.Account, "payer", Session,
                new SynchronousProgress<PublishProgress>(waited.Add), token);
            PublicationReport report = await resumed.RunAsync(null, token);

            Assert.NotEmpty(waited);
            Assert.Equal(1, await ManifestRecordsAsync(source, keys.Writer.Account, run.State.StartedInLedger, token));
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(report.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    public async Task A_second_publication_of_the_same_bytes_by_the_same_writer_does_not_adopt_the_firsts_manifest()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            byte[] file = VectorStream.Bytes("batch-twins", 20_000);
            SessionOptions options = Session;

            // One: a one-account publication that stops before its manifest.
            PublicationEstimate priced = await PublicationSession.EstimateAsync(source, keys.Writer.Account, file.Length, options, token);
            ScriptedStrategy one = new(new SequenceStrategy(sink, keys.Writer, new SequenceStrategyOptions { FeeDrops = (int)priced.FeeDropsPerTransaction }))
            {
                PauseAtCall = 2,
            };
            PublicationSession first = await PublicationSession.StartAsync(
                one, source, file, "application/octet-stream", "batch.bin", keys.Writer.Account, "writer", "1", options, token);
            Task<PublicationReport> firstRun = first.RunAsync(null, token);
            await one.Paused.WaitAsync(TimeSpan.FromMinutes(5), token);

            // Two: a Batch publication of the same bytes, started now, so one's manifest lands in its window.
            await StandaloneFixture.CloseLedgersUntilAsync(client, await source.CurrentLedgerAsync(token) + 2, token);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            ManifestSink beforeManifest = new(sink, stop, transmitFirst: false);
            PublicationSession second = await StartAsync(beforeManifest, source, keys, file, stop.Token);

            one.Resume();
            PublicationReport firstReport = await firstRun;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.RunAsync(null, stop.Token));

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                Factory(sink, source, keys, keys.Payer), source, file, second.StatePath, keys.Payer.Account, "payer", Session, null, token);
            PublicationReport secondReport = await resumed.RunAsync(null, token);

            Assert.Equal(firstReport.Manifest.Root, secondReport.Manifest.Root);
            Assert.NotEqual(firstReport.Link, secondReport.Link);
            Assert.Equal(file, (await new XrplFileReader(source).ReadAsync(secondReport.Link, token)).Bytes);
        }
        finally
        {
            await client.Disconnect();
        }
    }

    /// <summary>Forwards everything, and has the proxy drop every connection at its <paramref name="dropAt"/>-th blob.</summary>
    private sealed class DroppingSink(ILedgerSink inner, DroppingProxy proxy, int dropAt) : ILedgerSink
    {
        private int _count;

        public bool Dropped { get; private set; }

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == dropAt)
            {
                proxy.DropAll();
                Dropped = true;
            }

            return inner.SubmitBlobAsync(txBlob, cancellationToken);
        }
    }

    /// <summary>Forwards, and cancels the run once <paramref name="stopAfter"/> blobs have been transmitted — closing the window mid-run.</summary>
    private sealed class StoppingSink(ILedgerSink inner, CancellationTokenSource stop, int stopAfter) : ILedgerSink
    {
        private int _count;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            string answer = await inner.SubmitBlobAsync(txBlob, cancellationToken);
            if (Interlocked.Increment(ref _count) == stopAfter) await stop.CancelAsync();
            return answer;
        }
    }

    /// <summary>
    /// Stops the run at the manifest's blob: after transmitting it (<paramref name="transmitFirst"/>)
    /// — the manifest in flight — or instead of transmitting it.
    /// </summary>
    private sealed class ManifestSink(ILedgerSink inner, CancellationTokenSource stop, bool transmitFirst) : ILedgerSink
    {
        public bool SawManifest { get; private set; }

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) => inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
            bool manifest = tx["Memos"] is JsonArray memos && memos.Count > 0
                && string.Equals((string?)memos[0]?["Memo"]?["MemoType"], MemoLayout.ManifestMarkerHex, StringComparison.OrdinalIgnoreCase);

            if (!manifest) return await inner.SubmitBlobAsync(txBlob, cancellationToken);

            SawManifest = true;

            if (transmitFirst)
            {
                string answer = await inner.SubmitBlobAsync(txBlob, cancellationToken);
                await stop.CancelAsync();
                return answer;
            }

            await stop.CancelAsync();
            throw new OperationCanceledException(stop.Token);
        }
    }

    /// <summary>Forwards, and remembers how far the first history read after <see cref="StartRecording"/> reached.</summary>
    private sealed class RecordingSource(ILedgerSource inner) : ILedgerSource
    {
        private bool _recording;

        public long FirstHistoryReadTo { get; private set; }

        public void StartRecording() => _recording = true;

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => inner.TransactionAsync(hash, cancellationToken);

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken)
        {
            if (_recording && FirstHistoryReadTo == 0) FirstHistoryReadTo = toLedger;
            return inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);
        }

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);

        public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) => inner.SnapshotAsync(accounts, cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) => inner.IsAmendmentEnabledAsync(name, cancellationToken);
    }
}
```

- [ ] **Step 2: Run them**

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchPublicationStandaloneTests -showLiveOutput
```

Expected: PASS (7 tests) and one line `1 MB: … s, signing … s (…%); payer paid … drops`.

If the share printed is above 20 % and Task 1 Step F3 did not fire, apply Task 1 Step F3 now (the steps there are complete), have Task 20's Batch signers pass `cacheWallet: true`, give the stand test's `KeysAsync` signers `cacheWallet: true` too, and rerun this step; commit those changes with this task.

A failing test here is a defect in Tasks 8–16, not a reason to loosen the test: debug it with superpowers:systematic-debugging.

- [ ] **Step 3: Run the whole suite once with the stand up**

Run: `dotnet run --project tests/XrplFile.Core.Tests` and `dotnet run --project tests/XrplFile.App.Core.Tests` and `dotnet run --project tests/XrplFile.Keys.Tests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs tests/XrplFile.Core.Tests/LedgerPump.cs
git commit -m "test(core): Batch publishing end to end on the stand" -m "1 MB: <seconds> s, signing <share> of it."
```

---

### Task 24: Acceptance in the MauiLab VM against testnet (by hand)

Spec 7.6. Nothing here is automated; the deliverable is a report with screenshots and figures.

**Files:**
- Create: `docs/superpowers/reports/2026-10-15-batch-publishing/report.md` and screenshots in the same folder
- Scratch only (never committed): `vm-cdp.ps1` (the CDP helper of `docs/superpowers/plans/2026-10-13-dark-theme.md` Task 8 Step 2, copied verbatim into your scratchpad), `read-web.mjs`

**Interfaces:**
- Consumes: the finished application; the `maui-vm-lab` skill and lab at `D:\Lab\maui-vm-lab`; testnet `wss://s.altnet.rippletest.net:51233`.

- [ ] **Step 1: Deploy and launch non-elevated with DevTools**

Read the `maui-vm-lab` skill first. On the host:

```powershell
cd D:\Lab\maui-vm-lab
.\06-deploy-and-run.ps1 -ProjectPath E:\GIT\XRPL\xrpl-file\app\XrplFile.App\XrplFile.App.csproj -ExeName XrplFile.App.exe -Action Deploy
```

In the guest, through PowerShell Direct, register and start the non-elevated task exactly as dark-theme Task 8 Step 1 does (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, scheduled task `XrplPlain`, `-RunLevel Limited`). Save the CDP helper as `vm-cdp.ps1` in your scratchpad and check `.\vm-cdp.ps1 -Eval 'document.title'`. Unlock the vault (`labtestvault1`).

- [ ] **Step 2: Two testnet keys**

In Accounts, create a writer key and a payer key on network `testnet` and fund each from the Funding screen's testnet faucet (100 XRP each is plenty: the estimate of B26 is about 1.6 XRP for the payer and 0.65 XRP for the writer). Screenshot Accounts (`01-accounts.png`).

- [ ] **Step 3: The file**

Use the 7,123,692-byte file of the 2026-10-13 testnet ticket run if it is still on the host (it is the run spec section 1 describes); otherwise create one of exactly that size on the host and record its SHA-256 in the report:

```powershell
$bytes = New-Object byte[] 7123692; [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
[IO.File]::WriteAllBytes("$env:TEMP\accept-7mb.bin", $bytes)
(Get-FileHash "$env:TEMP\accept-7mb.bin" -Algorithm SHA256).Hash
```

Copy it into the guest (`Copy-VMFile` as the skill shows) to `C:\Lab\accept-7mb.bin`.

- [ ] **Step 4: Publish with Batch and record the wall time**

Publish › choose the file › select the writer key › check "Paid by" shows the payer key (screenshot `02-paid-by.png`) › Estimate (screenshot `03-estimate.png`: both accounts, the method line "Batch, paid by <label>.") › Publish. Note the time Publish is pressed; screenshot the run line twice, before and after 30 s (`04-run-early.png`, `05-run-rate.png`: rate, time left, elapsed); note the time "Published." appears and screenshot it (`06-published.png`). Wall time = the difference.

- [ ] **Step 5: Read it back twice**

In the app's Read tab, paste the link and read; save the file and compare its SHA-256 with Step 3 (screenshot `07-read.png`). On the host, save as `read-web.mjs` in your scratchpad:

```javascript
import { createHash } from 'node:crypto';
import { LedgerClient, XrplFileReader, parseLink } from 'file:///E:/GIT/XRPL/xrpl-file/web/xrpl-file.js';

const client = new LedgerClient(['wss://s.altnet.rippletest.net:51233']);
try {
  const result = await new XrplFileReader(client, { maxBytes: 64_000_000 }).read(parseLink(process.argv[2]));
  console.log(result.bytes.length, createHash('sha256').update(result.bytes).digest('hex').toUpperCase());
} finally {
  client.close();
}
```

Run `node <scratchpad>\read-web.mjs "<link>"`. Expected: `7123692 <the SHA-256 of Step 3>`.

- [ ] **Step 6: Interrupt, then resume without picking the file**

Publish the same file again from the same two keys. After roughly 20 s of "Writing the file", close the window and confirm the close prompt. Start `XrplPlain` again and unlock. Under Publish › Unfinished runs the row shows "Batch, paid by <label>" (screenshot `08-unfinished.png`). Press Resume: no file dialog opens; the run screen first shows "Waiting for earlier transactions to expire — about … s" (screenshot `09-fence.png`), then writing, then "Published." (`10-resumed.png`). History lists both publications with "Paid by" (`11-history.png`). Read the resumed link back with `read-web.mjs` as in Step 5.

- [ ] **Step 7: Write the report and commit**

`docs/superpowers/reports/2026-10-15-batch-publishing/report.md`: date, app commit, node, the file (path or SHA-256), the estimate's figures, wall time of Step 4 against the 2026-10-13 ticket run (530 of 7,124 chunks in about seven minutes before it stopped), both read-backs with their hashes, the interrupt and resume (fence wait seen, no file dialog, same bytes), the payer's spend from the account's balance before and after, and every defect found, each with a screenshot. Defects are not fixed in this task; each becomes its own follow-up.

```bash
git add docs/superpowers/reports/2026-10-15-batch-publishing
git commit -m "docs(reports): Batch publishing acceptance on testnet"
```

---

## Spec questions

Defects or gaps found while planning, each with the ruling this plan follows.

1. **B25 omits `TicketCount`, which B30 needs.** B30 says a `TicketCreate` at s covers the sequences its tickets took; nothing in B25's field list carries the count. *Ruling:* `LedgerTransaction` also gains `TicketCount` (Task 2).
2. **4.2's outer shape cannot be handed to the writer's signer in Xrpl 11.2.0.** The outer listed in 4.2 already has `BatchSigners [ { BatchSigner: { Account A } } ]`; 11.2.0's `SignAsBatchPart` *appends* a signer, and its submitter check refuses an account that already signed (`UnauthorizedAccessException`; at the node a duplicate `BatchSigner` is `temBAD_SIGNER`). Its routing also keys on the inner `Account`s, not on `BatchSigners`. *Ruling:* the outer goes to the writer without `BatchSigners`; the writer's signature adds the entry; the payer signs the decoded result. The final blob has the spec's shape (Task 1).
3. **The tidy-up "under B7".** B7 asks for `LastLedgerSequence` = I + 20 and a persisted deadline; the tidy-up of spec 2026-10-12 uses I + 10 (I16), and it runs after the state says "done", when nothing reads the fence. *Ruling:* the Batch tidy-up is `LeftoverTickets.ReleaseAsync` unchanged (I6–I8, I16, offset 10), at the approved unit, polling at `ChainCheck`, without writing the fence (Task 9). Residual risk, accepted: a spend still in flight after the two-minute tidy-up deadline can apply during the first round of a Batch publication started at once from the same writer, which then fails with `AccountInUse` (B12) and is resumable. Its window is ten ledgers.
4. **No bound on a node that stops.** Snapshot and history reads that keep failing, or a validated ledger that stops advancing, would leave settlement waiting forever; the spec bounds sends only. *Ruling:* `BatchStrategyOptions.NodeOutageLimit` = 2 min, after which the run fails with `NodeUnreachable` and is resumable (Task 11, Review Focus 1). A chain check that fails this way is fatal to the round, so a sender parked in its allowance wait is released (Task 8, Review Focus 6). The resume's fence wait has the same bound, through `SessionOptions.NodeOutageLimit` (Task 16).
5. **`OpenLedgerFeeAsync` must also give the base fee.** B15's unit is "at least the base fee". *Ruling:* it returns `OpenLedgerFee(OpenLedgerFeeDrops, BaseFeeDrops)`, both from the `fee` method (Task 2).
6. **The `FeeTooHigh` sentence of 6.5 is false at estimate time.** It says the run "is listed under unfinished runs", but the estimate refusal (open-ledger fee above 200) happens before any state exists. *Ruling:* the spec's sentence is kept for a running run; before anything starts the application says "Network fees are above what this application publishes at right now. Try again when they are back down." (`PublishErrorMessages.FeeTooHighNow`, Task 4).
7. **`AccountInUse`'s sentence names a role that `For(XrplFileError)` cannot know.** *Ruling:* `XrplFileException.Role` (`AccountRole.Writer`/`Payer`) carries it and `For(XrplFileException)` uses "writer" or "payer"; `For(XrplFileError.AccountInUse)` alone says "writer or payer" (Task 4).
8. **Unfinished-row method for sequence runs.** 6.4 names "Batch, paid by `<label>`" and "Tickets" only, and a sequence run is neither. *Ruling:* such a row says "Sequence" (Task 22).
9. **The report's `FeeDropsEstimated` on the Batch path, and on a resume that sends nothing.** *Ruling (revised after review):* on the Batch path it is the writer's part counted as today (spec 4.6) — its solo transactions at the approved unit — and the payer's spend is the measured `PayerFeeDrops` (B29), so an estimate and a measurement are never added together; the record's documentation says so. A resume that finds the manifest (B27) reports 0 and sends nothing (Tasks 14, 15). The report's "unknown" payer fee (spec 4.6) is a null on the library object; the application shows no fees on the run screen today, so nothing prints it.
10. **"Nothing is sent" in B27 and the tidy-up.** *Ruling:* a resume that adopts the manifest also skips the tidy-up; leftover tickets wait for a later publication (spec 2026-10-12 I4).
11. **The one-account resume order.** 5.2 puts the estimate after reconciliation and the manifest check; the code priced first. *Ruling:* every resume reconciles and looks for the manifest before pricing, so a drained account can still finish a run whose manifest already landed (Task 15).
12. **Who builds the strategy.** 6.1 says `PublishPolicy.Create` returns a `BatchStrategy` wired with the session's budget and fence, but the session is created after its strategy. *Ruling:* the Batch overloads of `StartAsync`/`ResumeAsync` take a `BatchStrategyFactory(budget, fence)`; the application passes `PublishPolicy.CreateBatch` through it (Tasks 14, 18, 20).
13. **Submit answers the table does not name.** *Ruling (revised after review):* `error:invalidTransaction` is treated like `tem*` (the blob is malformed: fail with `SubmitRefused`); `noNetwork`, `noCurrent`, `noClosed` and `notSynced` — a node briefly out of step with its network — are busy, retried within the four transmissions; `amendmentBlocked` and any other code are left to settlement. `telINSUF_FEE_P` counts as busy for the window but is not resent: the same under-priced blob would be refused again, and the next round reads the unit again, as the table says (Task 8).
14. **A halted round's solo memo.** 4.1 step 7 sends it after settling; after a halt the round has stopped spending. *Ruling:* a halted round sends no solo; the memo goes into the next round (Task 8).
15. **Payer memory and "Nobody".** *Ruling:* only a real payer is remembered when a run starts; choosing Nobody is not remembered (Task 18).
16. **What `PublishRequest` carries.** 6.1 adds `PayerReference` and `PayerAccount`. *Ruling:* it also carries `SourcePath` (B17) and the two labels the estimate card and the shortfall sentence need (Task 18).
17. **History records recovered at start-up have no report.** *Ruling (revised after review):* the numbers stay `long` — an older application reads a null there as a broken file and replaces the whole list. A recovered record carries `0` for both and an optional `Recovered: true`, written only when set, which an older application ignores (Task 22).
18. **The Batch prior before 30 s.** B21 has the Batch path "assume 70 memos/s"; showing "70 chunks/s" would present an assumption as a measurement. *Ruling:* before 30 s the Batch line shows the time left from the prior and no rate (Task 21).
19. **The fence wait's poll and source.** *Ruling:* it reads `SnapshotAsync([writer])` every `SessionOptions.FencePoll` (1 s), reports the stage `"fence"` with ledgers passed and ledgers to wait, and the screen converts ledgers to seconds at `LedgerTiming.ApproximateLedgerInterval` (Tasks 16, 21).
20. **"One settle per pass" in B21.** *Ruling:* 3 × `LedgerTiming.ApproximateLedgerInterval` (12 s): the landing ledger and the two settle ledgers (Task 21).

The following were added after the plan review.

21. **When a batch is signed.** The first draft signed and registered batches up to `MaxAhead` ahead of the window, which on a slow node leaves registered batches waiting while their deadlines run. *Ruling:* as spec 4.1 step 3 says — a batch is built and signed only once the window has a slot for it and the budget allows it; the slot passes to its first transmission, and a batch discarded unsent gives it back without counting as an answer (Tasks 7, 8).
22. **History recovery (spec 5.1) against Forget and Clear.** Nothing deleted a finished run's state file, so recovering every "done" state at start-up would bring back links the person had forgotten or cleared. *Ruling:* once the application has written a run's History record it deletes that run's state file; start-up recovery records only "done" states that still exist — runs whose History write never happened — and deletes them, recording nothing when the link is already listed. No list of forgotten links is kept (Tasks 20, 22).
23. **The writer's per-memo `tx` lookup.** `XrplFileWriter` asks the node for each memo's ledger one transaction at a time after the strategy returns — about 50 minutes for 7,124 chunks at testnet's 420 ms round trip, while the run screen says seconds are left. *Ruling:* `SubmittedMemo` carries the ledger the memo landed in (`LedgerIndex`); `BatchStrategy` and `TicketStrategy` fill it from the records their sweeps confirmed, the writer skips the lookup when it is present and keeps it as the fallback (`SequenceStrategy`). A pass whose memos have all landed shows "finishing this pass" instead of a time left (Tasks 8, 15, 17, 21).
24. **Registered batches after a halt.** A halt stops signing, but a batch registered just before it could still be sent for the first time. *Ruling:* a halted round sends no first copy of anything; such a batch stays registered and counted (B13), its deadline is already in the fence, and settlement waits it out. Copies already sent are left alone (Task 8).
25. **Who reconnects.** `XrplClient` 11.2.0 reconnects on its own by default, and B24 allows only one owner of retries. *Ruling:* exactly one owner. By default `ClientKeeper` owns it: `ClientKeeper.CreateClient` builds every publishing client with the SDK's reconnection off, and the next request after a drop connects again. If the stand shows that a dead 11.2.0 client cannot connect again, the SDK owns it instead and the keeper only waits (Task 3 Step 8 has both branches). A connection its owner closed is retired and refused, never reopened.
26. **A history that lags a running publication.** B30 raises `NodeLacksHistory`, whose sentence is about reading a file. *Ruling:* while submitting, the application says the node's history fell behind and the run can be resumed (`PublishErrorMessages.HistoryLaggedWhilePublishing`, Task 4).
27. **B27 and duplicated chunks.** Reconciliation takes the first copy of a chunk in canonical order. A one-account run that sent a chunk twice can have used the other copy's ledger in its manifest; its resume then fails to adopt that manifest and sends a second one. Inherent to B27's "exactly those bytes"; accepted. On the Batch path a memo is in one candidate per round, so it cannot happen there.

## Self-review

- **Spec coverage.** B1 T1/T8; B2 T1; B3 T18; B4 T18/T20; B5 T8; B6 T2/T8; B7 T7/T8; B8 T8; B9 T8; B10 T7/T8; B11 T6/T8; B12 T9; B13 T7/T8/T10; B14 T2/T14/T18; B15 T6/T10/T14; B16 T13/T14/T18/T20; B17 T14/T15/T22; B18 T16/T22; B19 T21; B20 T10/T17; B21 T21; B22 T4; B23 T7/T14/T16; B24 T3/T8/T11; B25 T2; B26 T10/T12/T16; B27 T14/T15/T23; B28 T8/T9; B29 T9/T14; B30 T9. Spec 4.6 T14; 4.7 T4; 5.1 T13/T20/T22; 5.2 T15/T16/T22; 6.1–6.5 T18–T22 and T4; 7.1 T1; 7.2 T2/T3; 7.3 T5–T12; 7.4 T23 (and T15/T16 in unit form); 7.5 T18–T22; 7.6 T24.
- **Placeholder scan.** Conditional steps (Task 1 F1–F3, Task 3 Step 8's fallback, Task 23's cache step) carry their full code; every other step has its code or exact command.
- **Type consistency.** `BatchStrategy` ctor `(sink, source, writer, payer, budget, fence, options)` everywhere; `BatchRound` gains `progress` in Task 10 and `BatchContext` gains `Spending` in Task 10, both with their single construction sites updated there; `PublicationSession.ResumeAsync` Batch overload `(factory, source, file, statePath, payer, payerKeyReference, options, fenceProgress, ct)` in Tasks 16, 20, 23; `RunSnapshot` init properties defined in Task 20 and read in Task 21.
- **Review Focus.** Each line has its test in the owning task (11, 16, 18, 21, 8/12/14, 11, 11, 22).
- **Revised after the plan review.** Transport retries step on `TransportRetryStep` and the fake scripts answers by candidate, so the busy, fee and lost-answer tests can pass (M1); a failed chain check halts the round (M2); the Paid-by test stores its twin under another raw network (M3); finished state files are deleted once History holds the link (M4, Spec question 22); memos carry their ledger (M5, Spec question 23); `_strategy` is replaced as a whole (M6); and every should-fix item of the review is applied in its task, with Spec questions 9, 13 and 17 revised and 21–27 added. Rejected: splitting Task 8 (the round and the strategy are only testable together, and a half would leave an untestable intermediate); a stronger stand check of `SnapshotAsync` (the unit test already pins the request's ledger index); a test of the "could not be read" source-file fallback (an unreadable file cannot be produced the same way on every platform; the moved and changed cases are tested).
