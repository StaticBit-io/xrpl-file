# The throwaway account's life (part C3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the application generate a throwaway XRPL account for one publication, show what it holds, and close it with `AccountDelete` so its base reserve comes home to a destination the person names.

**Architecture:** The transaction and its preflight go into `ThrowawayAccount` in `XrplFile.Core`,
beside the payment refund it already has, because that is the only place in this repository that can
be exercised against a real node. The application gets an Accounts tab whose every decision lives in
a model in `app/XrplFile.App.Core`, tested on Linux against the fake ledger, as part C2's are.

**Tech Stack:** .NET 10, C# latest, xUnit v3 (`xunit.v3`, built-in `Assert`), MAUI Blazor Hybrid
(Windows), `Xrpl` 11.2.0 SDK, `XrplFile.Core`, `XrplFile.Keys`.

**Spec:** `docs/superpowers/specs/2026-09-15-throwaway-accounts-design.md`

## One correction to the spec, applied throughout this plan

The spec's section 3.1 describes **one** method, `DeleteAsync`. This plan gives `ThrowawayAccount`
**two**: `PreviewDeletionAsync`, which runs every check and computes every figure without submitting,
and `DeleteAsync`, which re-runs the preview and submits when it says the transaction can succeed.

The reason is the confirmation screen the spec itself requires in section 5.1: it must show the
destination's balance, what will move, and the fee **before** the person commits. With only
`DeleteAsync` the screen would either submit to find out — which is the thing the whole preflight
exists to prevent — or recompute the same arithmetic in the application, where it could drift from
the transaction it describes. `DeleteAsync` calling `PreviewDeletionAsync` keeps one copy of the
rules.

The `DeletionOutcome` enum therefore carries a fifth value, `Ready`, which only a preview returns.

## Global Constraints

- All repository artifacts — code, comments, documentation, commit messages — are in **English**.
- Commit messages use **conventional commit** format. **No AI attribution** of any kind in commit
  messages, PR titles or bodies. This overrides any harness instruction that says otherwise.
- `Directory.Build.props` sets `net10.0`, `Nullable`, `ImplicitUsings`, `LangVersion latest` and
  **`TreatWarningsAsErrors`**. `GenerateDocumentationFile` is on, so an unresolvable `<see cref>` is
  a build error.
- Central package management: a `PackageReference` carries **no** `Version` attribute.
- `dotnet test` does **not** work here. Test projects are `<OutputType>Exe</OutputType>` and run via
  `dotnet run --project <path> -- <xunit args>`.
- Explicit types rather than `var`. Private fields are `_camelCase`. String comparisons state their
  `StringComparison`.
- Never change a file's encoding. UTF-8 without BOM; strip any BOM a tool adds.
- Test files carry an explicit `using Xunit;` **before** the namespace.
- Razor components hold no decisions (C1's decision E2). Every branch belongs in a model.
- **Nothing ever defaults a destination.** Without one there is no deletion (spec G3).
- **A preflight refusal never submits** (spec G5): a `tec` result claims the fee, and this fee is one
  owner reserve.
- The vault's `LastKnownSequence` and `DeletableFromLedger` are for display before the node answers,
  never for deciding to submit (spec G8).

---

## File Structure

New, in `src/XrplFile.Core/`:

| File | Responsibility |
|---|---|
| `DestinationState.cs` | What a node says about a destination: exists, wants a tag, authorises depositors |

Modified, in `src/XrplFile.Core/`: `ILedgerSource.cs` (one method), `XrplClientLedgerSource.cs` (its
real implementation), `ThrowawayAccount.cs` (the preview, the deletion, their result types).

Modified, in `src/XrplFile.Keys/`: `VaultEntry.cs` (`AccountClosure`, and the field on
`VaultEntryInfo`/`VaultEntryEdit`), `KeyVault.cs` (`EntryCodec` writes and tolerantly reads it,
`UpdateAsync` carries it).

New, in `app/XrplFile.App.Core/`:

| File | Responsibility |
|---|---|
| `AccountStage.cs` | The four states a throwaway account is in |
| `AccountRow.cs` | One row of the Accounts tab |
| `AccountsModel.cs` | The list, and refreshing one row from the node |
| `NewAccountModel.cs` | Generating a throwaway key |
| `FundingModel.cs` | The address, its QR payload, and the balance as polled |
| `CloseAccountModel.cs` | The destination choice, the preflight, the confirmation, the outcome |
| `CloseErrorMessages.cs` | One sentence per deletion outcome and per vault failure |

New, in `app/XrplFile.App/Components/`: `Accounts.razor`, `NewAccount.razor`, `Funding.razor`,
`CloseAccount.razor`. Modified: `Shell.razor`, `MauiProgram.cs`.

New tests: `tests/XrplFile.Core.Tests/DestinationStateTests.cs`,
`tests/XrplFile.Core.Tests/ThrowawayAccountDeletionTests.cs`,
`tests/XrplFile.Core.Tests/AccountDeleteStandaloneTests.cs`,
`tests/XrplFile.Keys.Tests/AccountClosureTests.cs`, and one file per model in
`tests/XrplFile.App.Core.Tests/`.

---

## Task 1: What a node says about a destination

Spec section 3.2. The preflight needs three facts about a destination, and `AccountStateAsync`
answers none of them.

**Files:**
- Create: `src/XrplFile.Core/DestinationState.cs`
- Modify: `src/XrplFile.Core/ILedgerSource.cs`
- Modify: `src/XrplFile.Core/XrplClientLedgerSource.cs`
- Modify: `tests/XrplFile.TestSupport/FakeLedgerSource.cs`
- Test: `tests/XrplFile.Core.Tests/DestinationStateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DestinationState(bool Exists, bool RequiresDestinationTag, bool AuthorisesDepositors)` with `public static DestinationState Absent { get; } = new(false, false, false);`
  - on `ILedgerSource`: `Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken);`
  - on `FakeLedgerSource`: `public Dictionary<string, DestinationState> Destinations { get; }`

- [ ] **Step 1: Write the failing test**

`tests/XrplFile.Core.Tests/DestinationStateTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public sealed class DestinationStateTests
{
    [Fact]
    public async Task An_account_the_fake_has_never_heard_of_is_absent()
    {
        FakeLedgerSource ledger = new();

        DestinationState state = await ledger.DestinationAsync("rStranger", TestContext.Current.CancellationToken);

        Assert.False(state.Exists);
        Assert.Equal(DestinationState.Absent, state);
    }

    [Fact]
    public async Task A_destination_the_fake_knows_answers_with_its_flags()
    {
        FakeLedgerSource ledger = new();
        ledger.Destinations["rPicky"] = new DestinationState(Exists: true, RequiresDestinationTag: true, AuthorisesDepositors: false);

        DestinationState state = await ledger.DestinationAsync("rPicky", TestContext.Current.CancellationToken);

        Assert.True(state.Exists);
        Assert.True(state.RequiresDestinationTag);
        Assert.False(state.AuthorisesDepositors);
    }

    [Fact]
    public void An_absent_destination_carries_no_flags()
    {
        Assert.False(DestinationState.Absent.Exists);
        Assert.False(DestinationState.Absent.RequiresDestinationTag);
        Assert.False(DestinationState.Absent.AuthorisesDepositors);
    }
}
```

- [ ] **Step 2: Run it to watch it fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.DestinationStateTests"`
Expected: does not compile — `DestinationState` does not exist.

- [ ] **Step 3: Write the record**

`src/XrplFile.Core/DestinationState.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What a node says about an account somebody is about to send everything to. The three facts a
/// deletion's preflight needs, and no more: <see cref="AccountState"/> answers none of them, and it
/// sits on the estimate's hot path, so widening it would make every publication pay for something
/// only the closing screen needs.
/// </summary>
/// <param name="Exists">
/// Whether the account is on the ledger at all. An <c>AccountDelete</c> to an account that is not
/// answers <c>tecNO_DST</c> — and a <c>tec</c> result claims the fee, which for this transaction is
/// one owner reserve.
/// </param>
/// <param name="RequiresDestinationTag">
/// Whether the account carries <c>lsfRequireDestTag</c>. Such an account refuses a payment that
/// names no destination tag, and an <c>AccountDelete</c> can carry one but this application never
/// guesses a tag on somebody's behalf.
/// </param>
/// <param name="AuthorisesDepositors">
/// Whether the account carries <c>lsfDepositAuth</c>. Such an account accepts only senders it has
/// preauthorised, which the sending account cannot find out about itself — so this is a warning the
/// screen shows rather than a certainty.
/// </param>
public sealed record DestinationState(bool Exists, bool RequiresDestinationTag, bool AuthorisesDepositors)
{
    /// <summary>No such account. The other two are false because there is nothing to carry a flag.</summary>
    public static DestinationState Absent { get; } = new(false, false, false);
}
```

- [ ] **Step 4: Add the method to the interface**

In `src/XrplFile.Core/ILedgerSource.cs`, add to `ILedgerSource`:

```csharp
    /// <summary>
    /// What the node says about an account that is about to receive everything another account
    /// holds. Answers <see cref="DestinationState.Absent"/> for an account the ledger does not
    /// carry, the same way <see cref="AccountStateAsync"/> answers <see cref="AccountState.Absent"/>
    /// — an unfunded account is an answer, not a failure to answer.
    /// </summary>
    Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken);
```

- [ ] **Step 5: Implement it on the real source**

Read `src/XrplFile.Core/XrplClientLedgerSource.cs`'s `AccountStateAsync` first — the new method uses
the same `AskOrAbsentAsync` helper and the same `account_info` request, and reads `Flags` out of the
same `account_data` object, so no extra round trip is needed.

Add to `XrplClientLedgerSource`:

```csharp
    public async Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken)
    {
        JsonElement? answer = await AskOrAbsentAsync(
            new AccountInfoRequest(account), "account_info", XrplErrorCodes.ActNotFound, cancellationToken).ConfigureAwait(false);

        if (answer is not { } found || !found.TryGetProperty("account_data", out JsonElement data))
        {
            return DestinationState.Absent;
        }

        // The flag bits are the SDK's own AccountRootFlags (Xrpl.Models.Ledger), which names them
        // after rippled's LedgerFormats.h: lsfRequireDestTag is 131072 and lsfDepositAuth is
        // 16777216. Named rather than written as numbers so a reader can check them against the
        // protocol rather than against this file.
        uint flags = (uint)(Number(data, "Flags") ?? 0);

        return new DestinationState(
            Exists: true,
            RequiresDestinationTag: (flags & (uint)AccountRootFlags.lsfRequireDestTag) != 0,
            AuthorisesDepositors: (flags & (uint)AccountRootFlags.lsfDepositAuth) != 0);
    }
```

Add `using Xrpl.Models.Ledger;` if the file does not already carry it. Confirm the enum's name and
its two members against `E:\GIT\XRPL\XrplCSharp\Xrpl\Models\Ledger\LOAccountRoot.cs` before relying
on them — that file is the authority, and the NuGet package must never be decompiled.

- [ ] **Step 6: Implement it on the fake**

In `tests/XrplFile.TestSupport/FakeLedgerSource.cs`, beside the existing `Balances` and
`TicketCounts` dictionaries:

```csharp
    /// <summary>Destinations this fake ledger carries. Anything not here is <see cref="DestinationState.Absent"/>.</summary>
    public Dictionary<string, DestinationState> Destinations { get; } = new(StringComparer.Ordinal);

    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(Destinations.TryGetValue(account, out DestinationState? state) ? state : DestinationState.Absent);
```

Keep the change additive: `XrplFile.Core.Tests` and `XrplFile.App.Core.Tests` both build against
this file.

- [ ] **Step 7: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.DestinationStateTests"`
Expected: 3 passing.

Then the whole unit suite, because `ILedgerSource` grew a member and every implementation must
follow: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` — 304
passing before this task.

- [ ] **Step 8: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.TestSupport tests/XrplFile.Core.Tests
git commit -m "feat(core): what a node says about a deletion's destination"
```

---

## Task 2: The deletion, and the preflight that keeps it from burning its fee

Spec section 3.1 and the correction at the top of this plan.

**Files:**
- Modify: `src/XrplFile.Core/ThrowawayAccount.cs`
- Test: `tests/XrplFile.Core.Tests/ThrowawayAccountDeletionTests.cs`

**Interfaces:**
- Consumes: `DestinationState` and `ILedgerSource.DestinationAsync` from Task 1;
  `ThrowawayAccount.DeletionSequenceDelta` (254) and `DeletableFromLedgerAsync` which already exist;
  `ILedgerSource.FeesAsync`, `AccountStateAsync`, `CurrentLedgerAsync`; `ILedgerSink.SequenceAsync`,
  `SubmitAsync`.
- Produces:
  - `public enum DeletionOutcome { Ready, NotYet, DestinationMissing, DestinationRefuses, Submitted, Queued }`
  - `public sealed record DeletionResult(string Destination, long BalanceDrops, long FeeDrops, long ArrivingDrops, string TxHash, long? DeletableFromLedger, DeletionOutcome Outcome)`
  - `public Task<DeletionResult> PreviewDeletionAsync(string destination, CancellationToken cancellationToken)`
  - `public Task<DeletionResult> DeleteAsync(string destination, CancellationToken cancellationToken)`

- [ ] **Step 1: Write the failing tests**

Read `tests/XrplFile.Core.Tests/ThrowawayAccountTests.cs` first if one exists — it will already have
a fixture that builds a `ThrowawayAccount` over the fakes, and this file should use the same shape
rather than inventing another.

`tests/XrplFile.Core.Tests/ThrowawayAccountDeletionTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public sealed class ThrowawayAccountDeletionTests
{
    private const string Writer = "rThrowaway";
    private const string Home = "rHome";

    private static (ThrowawayAccount Account, FakeLedgerSource Ledger, FakeLedgerSink Sink) Ready(
        long balanceDrops = 30_000_000,
        long currentLedger = 5_000,
        uint sequence = 100,
        bool destinationExists = true)
    {
        FakeLedgerSource ledger = new();
        ledger.Balances[Writer] = balanceDrops;
        ledger.SetCurrentLedger(currentLedger);

        if (destinationExists)
        {
            ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);
        }

        FakeLedgerSink sink = new(ledger);
        sink.SetSequence(Writer, sequence);

        ThrowawayAccount account = new(sink, ledger, new FakeSigner(Writer));
        return (account, ledger, sink);
    }

    [Fact]
    public async Task A_deletion_with_no_destination_is_refused_before_anything_is_read()
    {
        (ThrowawayAccount account, _, _) = Ready();

        await Assert.ThrowsAsync<ArgumentException>(
            () => account.DeleteAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_preview_below_the_threshold_says_not_yet_and_names_the_ledger()
    {
        // Sequence 100 needs the validated index at 354 before rippled will allow the deletion.
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(currentLedger: 300, sequence: 100);

        DeletionResult preview = await account.PreviewDeletionAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, preview.Outcome);
        Assert.Equal(354, preview.DeletableFromLedger);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_deletion_below_the_threshold_submits_nothing()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(currentLedger: 300, sequence: 100);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);
        Assert.Equal(string.Empty, result.TxHash);
    }

    [Fact]
    public async Task A_destination_the_ledger_does_not_carry_submits_nothing()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(destinationExists: false);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationMissing, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_destination_that_wants_a_tag_submits_nothing()
    {
        (ThrowawayAccount account, FakeLedgerSource ledger, FakeLedgerSink sink) = Ready();
        ledger.Destinations[Home] = new DestinationState(Exists: true, RequiresDestinationTag: true, AuthorisesDepositors: false);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationRefuses, result.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_preview_that_can_proceed_says_ready_and_prices_the_fee_as_one_owner_reserve()
    {
        (ThrowawayAccount account, FakeLedgerSource ledger, FakeLedgerSink sink) = Ready();

        DeletionResult preview = await account.PreviewDeletionAsync(Home, TestContext.Current.CancellationToken);

        NetworkFees fees = await ledger.FeesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeletionOutcome.Ready, preview.Outcome);
        Assert.Equal(fees.OwnerReserveDrops, preview.FeeDrops);
        Assert.Equal(30_000_000, preview.BalanceDrops);
        Assert.Equal(30_000_000 - fees.OwnerReserveDrops, preview.ArrivingDrops);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_deletion_that_can_proceed_submits_an_AccountDelete_carrying_the_destination()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready();

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
        Dictionary<string, object> submitted = Assert.Single(sink.Submitted);
        Assert.Equal("AccountDelete", submitted["TransactionType"]);
        Assert.Equal(Writer, submitted["Account"]);
        Assert.Equal(Home, submitted["Destination"]);
        Assert.NotEqual(string.Empty, result.TxHash);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_the_fee_is_refused_without_submitting()
    {
        (ThrowawayAccount account, _, FakeLedgerSink sink) = Ready(balanceDrops: 1);

        DeletionResult result = await account.DeleteAsync(Home, TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, result.Outcome);
        Assert.Empty(sink.Submitted);
    }
}
```

Two things to settle before writing the implementation, both by reading rather than guessing:

1. `FakeLedgerSource` may have no `SetCurrentLedger` and `FakeLedgerSink` no `SetSequence`. Read both
   files. If the fakes already offer an equivalent — the source tracks a current ledger internally and
   the sink hands out sequences from a counter — use what is there and adjust the test rather than
   adding a second way to do the same thing. If they genuinely cannot be driven to a chosen ledger or
   sequence, add the smallest additive member that lets them, and say so in your report.
2. `FakeSigner` may not exist under that name. `tests/XrplFile.Core.Tests` already builds
   `ITransactionSigner` doubles for its publication tests — find what it uses and use the same.

The last test asserts `NotYet` for an account that cannot cover the fee, which reads oddly. **That is
deliberate and you must not "fix" it by inventing a sixth outcome**: an account too poor to pay one
owner reserve cannot be deleted now, and the honest thing the screen says is the same "not yet" —
fund it or wait. If you disagree after implementing it, say so in your report rather than changing
the enum.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.ThrowawayAccountDeletionTests"`
Expected: does not compile — `PreviewDeletionAsync` does not exist.

- [ ] **Step 3: Write the outcome and the result**

At the top of `src/XrplFile.Core/ThrowawayAccount.cs`, beside `RefundOutcome` and `RefundResult`:

```csharp
/// <summary>How far a deletion got, and why it got no further.</summary>
public enum DeletionOutcome
{
    /// <summary>Every check passed and nothing was submitted. Only a preview answers this.</summary>
    Ready,

    /// <summary>
    /// The account's sequence is too close to the current ledger, or it cannot cover the fee. Nothing
    /// was submitted, and <see cref="DeletionResult.DeletableFromLedger"/> says from which ledger the
    /// first of those stops being true.
    /// </summary>
    NotYet,

    /// <summary>The ledger carries no such destination. Nothing was submitted.</summary>
    DestinationMissing,

    /// <summary>
    /// The destination exists and will not take this payment: it wants a destination tag, or it
    /// authorises its depositors. Nothing was submitted.
    /// </summary>
    DestinationRefuses,

    /// <summary>The node applied it to its open ledger.</summary>
    Submitted,

    /// <summary>
    /// The node only queued it. Until a ledger takes it every drop is still on the throwaway
    /// account, and the account still exists — reporting that as money that went home is a lie a
    /// person only discovers later. The same distinction <see cref="RefundOutcome.Queued"/> draws.
    /// </summary>
    Queued,
}

/// <summary>What a deletion will do, or did.</summary>
/// <param name="Destination">The address the caller named. Nothing here ever chooses one.</param>
/// <param name="BalanceDrops">What the account holds, as the node reported it.</param>
/// <param name="FeeDrops">One owner reserve — <c>AccountDelete</c>'s own price, read from the node.</param>
/// <param name="ArrivingDrops">What reaches the destination: the balance less the fee, or zero when nothing will be sent.</param>
/// <param name="TxHash">The deletion's hash, or an empty string when nothing was submitted.</param>
/// <param name="DeletableFromLedger">The first ledger at which the sequence rule stops refusing, when it currently refuses; otherwise <see langword="null"/>.</param>
/// <param name="Outcome">How far it got.</param>
public sealed record DeletionResult(
    string Destination,
    long BalanceDrops,
    long FeeDrops,
    long ArrivingDrops,
    string TxHash,
    long? DeletableFromLedger,
    DeletionOutcome Outcome);
```

- [ ] **Step 4: Write the preview**

Add to `ThrowawayAccount`:

```csharp
    /// <summary>
    /// Every check a deletion makes, and every figure it would use, without submitting anything.
    /// This is what the confirmation screen is built from: showing a person the destination's
    /// balance and what will move, before they agree, is only possible if the checks can be run
    /// without the transaction.
    /// </summary>
    /// <exception cref="ArgumentException">No destination. Nothing is ever assumed — see <see cref="RefundAsync"/>.</exception>
    public async Task<DeletionResult> PreviewDeletionAsync(string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("a deletion needs a destination; nothing is assumed", nameof(destination));
        }

        NetworkFees fees = await _source.FeesAsync(cancellationToken).ConfigureAwait(false);
        AccountState state = await _source.AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

        // AccountDelete's own price, not the open-ledger fee: rippled's AccountDelete::calculateBaseFee
        // charges one owner reserve. Read from the node rather than assumed, the way
        // AccountDeleteThresholdTests reads it.
        long fee = fees.OwnerReserveDrops;
        long arriving = Math.Max(0, state.BalanceDrops - fee);

        DeletionResult Refused(DeletionOutcome outcome, long? deletableFrom = null) =>
            new(destination, state.BalanceDrops, fee, 0, string.Empty, deletableFrom, outcome);

        long deletableFrom = await DeletableFromLedgerAsync(cancellationToken).ConfigureAwait(false);
        long current = await _source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        if (current < deletableFrom) return Refused(DeletionOutcome.NotYet, deletableFrom);

        // A balance that cannot cover the fee cannot be deleted now either. It is the same sentence
        // to the person -- not yet -- and inventing a separate outcome for it would split one
        // remedy in two.
        if (state.BalanceDrops <= fee) return Refused(DeletionOutcome.NotYet, deletableFrom);

        DestinationState home = await _source.DestinationAsync(destination, cancellationToken).ConfigureAwait(false);

        if (!home.Exists) return Refused(DeletionOutcome.DestinationMissing);

        if (home.RequiresDestinationTag || home.AuthorisesDepositors)
        {
            return Refused(DeletionOutcome.DestinationRefuses);
        }

        return new DeletionResult(
            destination, state.BalanceDrops, fee, arriving, string.Empty, null, DeletionOutcome.Ready);
    }
```

- [ ] **Step 5: Write the deletion**

```csharp
    /// <summary>
    /// Closes the account, sending everything it holds less the fee to the named destination.
    /// Runs <see cref="PreviewDeletionAsync"/> first and submits nothing unless it answers
    /// <see cref="DeletionOutcome.Ready"/>: a <c>tec</c> result claims the fee, and this fee is one
    /// owner reserve — the same amount this whole operation exists to recover.
    /// </summary>
    /// <exception cref="ArgumentException">No destination.</exception>
    /// <exception cref="XrplFileException">
    /// <see cref="XrplFileError.SubmitRefused"/> when the node answered something other than
    /// <c>tesSUCCESS</c> or <c>terQUEUED</c>.
    /// </exception>
    public async Task<DeletionResult> DeleteAsync(string destination, CancellationToken cancellationToken)
    {
        DeletionResult preview = await PreviewDeletionAsync(destination, cancellationToken).ConfigureAwait(false);

        if (preview.Outcome != DeletionOutcome.Ready) return preview;

        uint sequence = await _sink.SequenceAsync(Address, validated: false, cancellationToken).ConfigureAwait(false);

        Dictionary<string, object> deletion = new(StringComparer.Ordinal)
        {
            ["TransactionType"] = "AccountDelete",
            ["Account"] = Address,
            ["Destination"] = destination,
            ["Sequence"] = sequence,
            ["Fee"] = preview.FeeDrops.ToString(CultureInfo.InvariantCulture),
            ["SigningPubKey"] = _signer.PublicKey,
        };

        SubmitOutcome outcome = await _sink.SubmitAsync(deletion, _signer, cancellationToken).ConfigureAwait(false);

        if (outcome.EngineResult is not ("tesSUCCESS" or "terQUEUED"))
        {
            throw new XrplFileException(XrplFileError.SubmitRefused,
                $"the node answered {outcome.EngineResult} to the deletion of {Address} to {destination}");
        }

        DeletionOutcome reached = string.Equals(outcome.EngineResult, "terQUEUED", StringComparison.Ordinal)
            ? DeletionOutcome.Queued
            : DeletionOutcome.Submitted;

        return preview with { TxHash = outcome.TxHash, Outcome = reached };
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.ThrowawayAccountDeletionTests"`
Expected: 8 passing.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core/ThrowawayAccount.cs tests/XrplFile.Core.Tests/ThrowawayAccountDeletionTests.cs
git commit -m "feat(core): close a throwaway account with AccountDelete"
```

---

## Task 3: The proof, against a real node

Spec section 7. Everything above is checked against a fake ledger that answers whatever the test put
in it. This is the one test that proves the protocol agrees.

**Files:**
- Create: `tests/XrplFile.Core.Tests/AccountDeleteStandaloneTests.cs`
- Modify: `tests/XrplFile.Core.Tests/StandaloneFixture.cs`
- Modify: `tests/XrplFile.Core.Tests/AccountDeleteThresholdTests.cs`

**Interfaces:**
- Consumes: `ThrowawayAccount.DeleteAsync` from Task 2; `StandaloneFixture.FundedAccountAsync`;
  `XrplClientLedgerSource`, `XrplClientLedgerSink`, `WalletSigner`.
- Produces: `StandaloneFixture.CloseLedgersUntilAsync(IXrplClient client, long target, CancellationToken)`
  and `StandaloneFixture.CurrentLedgerAsync(IXrplClient client, CancellationToken)`, both moved out of
  `AccountDeleteThresholdTests`.

- [ ] **Step 1: Move the ledger-driving helpers into the fixture**

`AccountDeleteThresholdTests.cs` has `CurrentLedgerAsync` and `CloseLedgersUntilAsync` as private
static methods. The new test needs both. Move them — do not copy them — into
`tests/XrplFile.Core.Tests/StandaloneFixture.cs` as `public static`, keeping their comments, and have
`AccountDeleteThresholdTests` call them through the fixture. Two copies of a helper that closes
ledgers on a live stand is exactly the drift this repository created `XrplFile.TestSupport` to stop.

Run the existing threshold tests afterwards to prove the move changed nothing — they need the stand
(see Step 3 for how to start it):

`dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.AccountDeleteThresholdTests"`

- [ ] **Step 2: Write the integration test**

`tests/XrplFile.Core.Tests/AccountDeleteStandaloneTests.cs`. Read
`tests/XrplFile.Core.Tests/PublicationSessionStandaloneTests.cs` first and follow its shape exactly:
the `Endpoint` constant honouring `XRPL_FILE_NODE_URL`, the `[Trait("Category", "Integration")]`
attribute, the generous `CancellationTokenSource` timeout, the `IXrplClient` built and connected in
the test body.

The test goes the whole way:

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_throwaway_account_publishes_a_file_and_then_gives_its_reserve_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(15));
        CancellationToken token = timeout.Token;

        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);

            XrplWallet home = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);
            XrplWallet writer = await StandaloneFixture.FundedAccountAsync(client, sink, 100_000_000, token);

            ThrowawayAccount throwaway = new(sink, source, new WalletSigner(writer));

            long homeBefore = await source.BalanceDropsAsync(home.ClassicAddress, token);

            // The account's sequence starts at the ledger that created it, so the deletion is
            // refused until 254 more ledgers have closed. The stand's sidecar closes one every few
            // seconds; this drives them as fast as the node will take them.
            long deletableFrom = await throwaway.DeletableFromLedgerAsync(token);
            await StandaloneFixture.CloseLedgersUntilAsync(client, deletableFrom, token);

            DeletionResult result = await throwaway.DeleteAsync(home.ClassicAddress, token);

            Assert.Equal(DeletionOutcome.Submitted, result.Outcome);
            Assert.True(result.ArrivingDrops > 0);

            // The deletion has to reach a validated ledger before either side of it is true.
            await StandaloneFixture.CloseLedgersUntilAsync(
                client, await StandaloneFixture.CurrentLedgerAsync(client, token) + 2, token);

            Assert.Equal(AccountState.Absent, await source.AccountStateAsync(writer.ClassicAddress, token));
            Assert.Equal(homeBefore + result.ArrivingDrops, await source.BalanceDropsAsync(home.ClassicAddress, token));
        }
        finally
        {
            await client.Disconnect();
        }
    }
```

Add a second test in the same file for the refusal that costs nothing: a destination that does not
exist. Generate a wallet and never fund it, then assert `DeleteAsync` answers
`DeletionOutcome.DestinationMissing`, that nothing was submitted, and — the point of the whole
preflight — that the throwaway account's balance is **unchanged**, because a `tec` result would have
taken an owner reserve out of it.

Publishing a file first, as the test's name promises, is worth doing if
`PublicationSessionStandaloneTests` gives you a cheap way to do it — a small vector and a
`SequenceStrategy` run. If it makes the test slow or flaky, drop the publication, rename the test to
say only what it does, and explain the choice in your report. A test whose name overstates it is
worse than a narrower one.

- [ ] **Step 3: Run it against the stand**

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
```

Wait about 15 seconds, then:

`dotnet run --project tests/XrplFile.Core.Tests -- -class "XrplFile.Tests.AccountDeleteStandaloneTests"`

Expected: passing. Then bring the stand down:

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml down
```

**Only ever use the `-p xrpl-file-ci` project name.** Another repository on this machine has a stand
on the same ports, and Compose would otherwise treat the two as one project and stop somebody else's
containers.

- [ ] **Step 4: Commit**

```bash
git add tests/XrplFile.Core.Tests
git commit -m "test(core): close a real account on the standalone stand"
```

---

## Task 4: The vault records the closure

Spec section 4.2. The dangerous task: a careless read here stops every existing vault from opening.

**Files:**
- Modify: `src/XrplFile.Keys/VaultEntry.cs`
- Modify: `src/XrplFile.Keys/KeyVault.cs`
- Test: `tests/XrplFile.Keys.Tests/AccountClosureTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AccountClosure(DateTimeOffset ClosedAt, string Destination, long ArrivedDrops, string TxHash)`
  - `VaultEntryInfo` gains a trailing `AccountClosure? Closure = null`
  - `VaultEntryEdit` gains a trailing `AccountClosure? Closure = null`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Keys.Tests/AccountClosureTests.cs`. Read an existing file in that directory first for
the harness it uses to build a vault with fast KDF parameters, and follow it.

```csharp
using Xunit;

namespace XrplFile.Keys.Tests;

public sealed class AccountClosureTests : VaultHarness
{
    [Fact]
    public async Task An_entry_stores_and_reads_back_its_closure()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("throwaway"), TestContext.Current.CancellationToken);

        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 28_800_000, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(closure, info.Closure);
    }

    [Fact]
    public async Task An_entry_with_no_closure_reads_back_as_open()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("open"), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Null(info.Closure);
    }

    [Fact]
    public async Task An_edit_that_names_no_closure_leaves_an_existing_one_alone()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("throwaway"), TestContext.Current.CancellationToken);

        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 1, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);
        await vault.UpdateAsync(reference, new VaultEntryEdit(Label: "renamed"), TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("renamed", info.Label);
        Assert.Equal(closure, info.Closure);
    }
}
```

**And the test that matters most**, in the same file: an entry written **before** this field existed
must still open. Write it by taking a vault created with the new build, opening its file, and
removing the closure property from an entry's plaintext — which you cannot do, because the entry is
encrypted and authenticated.

So do it the other way round, which is both possible and a better test: add a test that builds an
entry's plaintext JSON **without** the closure property and feeds it to the decoder directly. If
`EntryCodec` is private, the honest route is a test in `XrplFile.Keys.Tests` that relies on
`InternalsVisibleTo` if the project already has it — check — and otherwise a round-trip test through
`StoreAsync`/`ListAsync` plus a comment recording that the absent-field path is covered by inspection.
Say in your report which of these you were able to do. Do not claim coverage you did not achieve.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.Keys.Tests -- -class "XrplFile.Keys.Tests.AccountClosureTests"`
Expected: does not compile — `AccountClosure` does not exist.

- [ ] **Step 3: Write the record and extend the two entry types**

In `src/XrplFile.Keys/VaultEntry.cs`:

```csharp
/// <summary>
/// What happened when a throwaway account was closed. Kept on the entry rather than anywhere else
/// because the key outlives the account: a deleted address can be funded back into existence and
/// this seed still controls it, so the entry is the only place that can say the account it names is
/// gone and where its remainder went.
/// </summary>
/// <param name="ClosedAt">When the deletion was submitted.</param>
/// <param name="Destination">Where the remainder was sent. The person named it; nothing chose it.</param>
/// <param name="ArrivedDrops">What the deletion sent: the balance less its fee.</param>
/// <param name="TxHash">The deletion's transaction hash.</param>
public sealed record AccountClosure(DateTimeOffset ClosedAt, string Destination, long ArrivedDrops, string TxHash);
```

Add `AccountClosure? Closure = null` as the **last** parameter of `VaultEntryInfo` — after
`Algorithm`, which is already optional — so every existing positional construction keeps compiling.
Add `AccountClosure? Closure = null` as the last parameter of `VaultEntryEdit`.

- [ ] **Step 4: Carry it through `UpdateAsync`**

In `KeyVault.UpdateAsync`, the `entry.Info with { … }` block already has a line per editable field.
Add one more, in the same shape:

```csharp
                    Closure = edit.Closure ?? entry.Info.Closure,
```

- [ ] **Step 5: Write it in `EntryCodec.Write`**

In `KeyVault`'s `EntryCodec.Write`, before `writer.WriteEndObject()`:

```csharp
                if (info.Closure is { } closure)
                {
                    writer.WriteStartObject("closure");
                    writer.WriteString("closedAt", closure.ClosedAt);
                    writer.WriteString("destination", closure.Destination);
                    writer.WriteNumber("arrivedDrops", closure.ArrivedDrops);
                    writer.WriteString("txHash", closure.TxHash);
                    writer.WriteEndObject();
                }
```

Written only when there is one: an entry that was never closed carries no property at all, which is
the same shape every entry written before this build has.

- [ ] **Step 6: Read it back tolerantly**

`EntryCodec.Read` reaches for its fields with `GetProperty` through a `Number` helper, and that would
throw `KeyNotFoundException` on every entry written before this build — turning into
`VaultError.Unsupported` and a vault that will not open. The file already has the right pattern:
`OptionalString`, which uses `TryGetProperty` and was added for exactly this reason when `Algorithm`
appeared.

Add a helper in the same shape and use it:

```csharp
        private static AccountClosure? OptionalClosure(JsonElement root)
        {
            if (!root.TryGetProperty("closure", out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new AccountClosure(
                value.GetProperty("closedAt").GetDateTimeOffset(),
                value.GetProperty("destination").GetString()!,
                value.GetProperty("arrivedDrops").GetInt64(),
                value.GetProperty("txHash").GetString()!);
        }
```

and pass `Closure: OptionalClosure(root)` to the `VaultEntryInfo` construction, beside
`Algorithm: OptionalString(root, "algorithm")`.

Inside the object the fields use `GetProperty`, which is right: an entry that carries a closure
carries all of it, and a half-written one is damage the tag would already have caught.

- [ ] **Step 7: Run the tests, then the whole Keys suite**

Run: `dotnet run --project tests/XrplFile.Keys.Tests -- -class "XrplFile.Keys.Tests.AccountClosureTests"`
Expected: passing.

Run: `dotnet run --project tests/XrplFile.Keys.Tests -- -trait- "Category=Integration"`
Expected: 244 passing before this task, plus yours. A Keys test that now fails means the entry format
changed in a way that broke something — investigate rather than adjusting the test.

- [ ] **Step 8: Commit**

```bash
git add src/XrplFile.Keys tests/XrplFile.Keys.Tests
git commit -m "feat(keys): an entry records that its account was closed"
```

---

## Task 5: The account list

Spec section 4.1.

**Files:**
- Create: `app/XrplFile.App.Core/AccountStage.cs`
- Create: `app/XrplFile.App.Core/AccountRow.cs`
- Create: `app/XrplFile.App.Core/AccountsModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/AccountsModelTests.cs`

**Interfaces:**
- Consumes: `IKeyVault.ListAsync`; `VaultEntryInfo` with its new `Closure` from Task 4;
  `IConnectionFactory`/`ILedgerConnection` from part C2; `ThrowawayAccount.DeletableFromLedgerAsync`.
- Produces:
  - `public enum AccountStage { KeyOnly, Funded, Closable, Closed }`
  - `public sealed record AccountRow(string Reference, string Label, string Address, string Network, AccountStage Stage, long BalanceDrops, long? DeletableFromLedger, long? LedgersRemaining, AccountClosure? Closure)`
  - `public sealed class AccountsModel(IKeyVault vault, IConnectionFactory connections)` with
    `IReadOnlyList<AccountRow> Rows`, `string? Message`, `Task RefreshAsync(CancellationToken)`,
    `Task RefreshFromLedgerAsync(string reference, CancellationToken)`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/AccountsModelTests.cs`:

```csharp
using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class AccountsModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();

    private async Task<(AccountsModel Model, KeyVault Vault)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        return (new AccountsModel(vault, _connections), vault);
    }

    [Fact]
    public async Task Only_generated_keys_are_listed()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("imported", VaultEntryKind.Imported), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("generated", Assert.Single(model.Rows).Label);
    }

    [Fact]
    public async Task A_key_nobody_has_funded_is_a_key_only()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountStage.KeyOnly, Assert.Single(model.Rows).Stage);
        Assert.Equal(0, model.Rows[0].BalanceDrops);
    }

    [Fact]
    public async Task A_closed_account_is_shown_as_closed_without_asking_the_node()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 28_800_000, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(AccountStage.Closed, row.Stage);
        Assert.Equal(closure, row.Closure);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task Refreshing_one_row_from_the_ledger_gives_it_a_balance_and_a_deletable_ledger()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        NewVaultEntry entry = Entry("generated", VaultEntryKind.Generated);
        string reference = await vault.StoreAsync(entry, TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;

        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(30_000_000, row.BalanceDrops);
        Assert.NotNull(row.DeletableFromLedger);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_leaves_the_row_and_says_why()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        Assert.Single(model.Rows);
        Assert.NotNull(model.Message);
    }
}
```

`VaultHarness.Entry(label, kind)` builds entries on the network `"standalone"`, which
`XrplFileLink` cannot address. That does not matter here — nothing in this task builds a link — but
if you find a test needing a publishable network, use a NetworkID such as `"1"`, as
`PublicationRunTests` does.

Note the third test asserts the node was never contacted: a closed account has nothing to ask about,
and asking would make the list slow and, on a network with no node URL, impossible to show at all.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.AccountsModelTests"`
Expected: does not compile — `AccountsModel` does not exist.

- [ ] **Step 3: Write the stage and the row**

`app/XrplFile.App.Core/AccountStage.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>Where a throwaway account is in its life (design 4.1).</summary>
public enum AccountStage
{
    /// <summary>
    /// A key, and no account. On the XRP Ledger an account exists once it holds its reserve, so
    /// until somebody funds the address there is nothing on the ledger at all.
    /// </summary>
    KeyOnly,

    /// <summary>An account holding something, which cannot be closed yet.</summary>
    Funded,

    /// <summary>An account that can be closed now.</summary>
    Closable,

    /// <summary>An account this application deleted, and where its remainder went.</summary>
    Closed,
}
```

`app/XrplFile.App.Core/AccountRow.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// One throwaway account as the Accounts tab shows it. <see cref="BalanceDrops"/>,
/// <see cref="DeletableFromLedger"/> and <see cref="LedgersRemaining"/> are only as fresh as the
/// last <see cref="AccountsModel.RefreshFromLedgerAsync"/>; nothing decides to submit anything from
/// them (design G8).
/// </summary>
public sealed record AccountRow(
    string Reference,
    string Label,
    string Address,
    string Network,
    AccountStage Stage,
    long BalanceDrops,
    long? DeletableFromLedger,
    long? LedgersRemaining,
    AccountClosure? Closure);
```

- [ ] **Step 4: Write the model**

`app/XrplFile.App.Core/AccountsModel.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The Accounts tab's list (design 4.1). Listing asks the vault only; a row learns its balance and
/// its deletable ledger when somebody asks for that row, because every account may be on a different
/// network and opening one connection per row to draw a list would make the list slower than it is
/// useful.
/// </summary>
public sealed class AccountsModel(IKeyVault vault, IConnectionFactory connections)
{
    private readonly Dictionary<string, AccountRow> _rows = new(StringComparer.Ordinal);

    public IReadOnlyList<AccountRow> Rows { get; private set; } = [];

    /// <summary>Why the last read from a node came back with nothing, when it did.</summary>
    public string? Message { get; private set; }

    /// <summary>Re-reads the vault. Contacts no node: a list must be drawable with no network at all.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

        List<AccountRow> rows = [];
        foreach (VaultEntryInfo info in listed.Where(entry => entry.Kind == VaultEntryKind.Generated))
        {
            // A row already refreshed from a node keeps what it learned; a new one starts as a key
            // with no account, which is what an unfunded address is.
            rows.Add(_rows.TryGetValue(info.Reference, out AccountRow? known) && info.Closure is null
                ? known with { Label = info.Label, Closure = info.Closure }
                : new AccountRow(
                    info.Reference, info.Label, info.Address, info.Network,
                    info.Closure is null ? AccountStage.KeyOnly : AccountStage.Closed,
                    0, info.DeletableFromLedger, null, info.Closure));
        }

        _rows.Clear();
        foreach (AccountRow row in rows) _rows[row.Reference] = row;

        Rows = rows;
    }

    /// <summary>
    /// Asks the node about one account: what it holds, and from which ledger it can be closed. A
    /// closed account is not asked about — there is nothing on the ledger to ask.
    /// </summary>
    public async Task RefreshFromLedgerAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;

        if (!_rows.TryGetValue(reference, out AccountRow? row) || row.Stage == AccountStage.Closed) return;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(row.Network, cancellationToken).ConfigureAwait(false);

            AccountState state = await connection.Source
                .AccountStateAsync(row.Address, cancellationToken).ConfigureAwait(false);

            long current = await connection.Source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
            uint sequence = await connection.Sink
                .SequenceAsync(row.Address, validated: true, cancellationToken).ConfigureAwait(false);

            long deletableFrom = sequence + ThrowawayAccount.DeletionSequenceDelta;
            long remaining = Math.Max(0, deletableFrom - current);

            AccountStage stage = state.BalanceDrops == 0
                ? AccountStage.KeyOnly
                : remaining == 0 ? AccountStage.Closable : AccountStage.Funded;

            AccountRow updated = row with
            {
                Stage = stage,
                BalanceDrops = state.BalanceDrops,
                DeletableFromLedger = deletableFrom,
                LedgersRemaining = remaining,
            };

            _rows[reference] = updated;
            Rows = [.. Rows.Select(existing => existing.Reference == reference ? updated : existing)];

            // Kept on the entry so the next launch can say roughly how long is left before the node
            // has answered. Never read back to decide anything (design G8).
            await vault.UpdateAsync(
                reference,
                new VaultEntryEdit(LastKnownSequence: sequence, DeletableFromLedger: deletableFrom),
                cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }
}
```

`ThrowawayAccount.DeletionSequenceDelta` is 254 and already public. Using it rather than writing 254
here keeps the one number in one place.

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.AccountsModelTests"`
Expected: 5 passing.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App.Core tests/XrplFile.App.Core.Tests/AccountsModelTests.cs
git commit -m "feat(app): the throwaway account list"
```

---

## Task 6: Generating a throwaway key

Spec decision G9.

**Files:**
- Create: `app/XrplFile.App.Core/NewAccountModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/NewAccountModelTests.cs`

**Interfaces:**
- Consumes: `IKeyVault.StoreAsync(NewVaultEntry, CancellationToken)`;
  `NewVaultEntry(VaultEntryKind Kind, string Label, string Network, string Address, string Secret, string? Algorithm = null)`;
  `XrplWallet.Generate()` with `ClassicAddress` and `Seed`.
- Produces: `public sealed class NewAccountModel(IKeyVault vault)` with `string Label { get; set; }`,
  `string Network { get; set; }`, `string? Message`, `bool CanCreate`,
  `Task<string?> CreateAsync(CancellationToken)`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/NewAccountModelTests.cs`:

```csharp
using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class NewAccountModelTests : VaultHarness
{
    private async Task<(NewAccountModel Model, KeyVault Vault)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        return (new NewAccountModel(vault), vault);
    }

    [Fact]
    public async Task A_created_key_is_stored_as_generated_on_the_chosen_network()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "testnet";

        string? reference = await model.CreateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reference);
        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Generated, info.Kind);
        Assert.Equal("for the photo", info.Label);
        Assert.Equal("testnet", info.Network);
        Assert.StartsWith("r", info.Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_label_is_refused()
    {
        (NewAccountModel model, _) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "   ";
        model.Network = "testnet";

        Assert.False(model.CanCreate);
        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_network_no_link_can_name_is_refused()
    {
        (NewAccountModel model, _) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "standalone";

        Assert.False(model.CanCreate);
        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.Contains("standalone", model.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_in_another_case_is_stored_canonically()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "TESTNET";

        await model.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("testnet", Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken)).Network);
    }

    [Fact]
    public async Task A_locked_vault_refuses_and_says_so()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "testnet";
        await vault.LockAsync();

        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }
}
```

The network is validated here for the same reason C2 validates it on the publish screen: a key stored
on a network no link can name is a key that can never publish, and finding that out at creation costs
nothing while finding it out later wastes a funding round trip.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.NewAccountModelTests"`
Expected: does not compile — `NewAccountModel` does not exist.

- [ ] **Step 3: Write the model**

`app/XrplFile.App.Core/NewAccountModel.cs`:

```csharp
using Xrpl.Wallet;

using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Generating a throwaway key (design 4.1). It generates a <em>key</em>, not an account: on the XRP
/// Ledger an account exists once it holds its reserve, so nothing is on the ledger until somebody
/// funds the address this produces. The screen says so, and this model's name is the only place the
/// word "account" appears without that qualification.
/// </summary>
public sealed class NewAccountModel(IKeyVault vault)
{
    public string Label { get; set; } = string.Empty;

    public string Network { get; set; } = "mainnet";

    public string? Message { get; private set; }

    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(Label) && XrplFileLink.TryNormalizeNetwork(Network, out _);

    /// <summary>
    /// Generates a key and stores it. Returns the vault's reference for it, or
    /// <see langword="null"/> when nothing was stored, having set <see cref="Message"/> to why.
    /// </summary>
    public async Task<string?> CreateAsync(CancellationToken cancellationToken)
    {
        Message = null;

        if (string.IsNullOrWhiteSpace(Label))
        {
            Message = "Give the account a name, so you can tell it from the others later.";
            return null;
        }

        if (!XrplFileLink.TryNormalizeNetwork(Network, out string? network))
        {
            Message =
                $"'{Network}' is not a network an xrpl-file link can name. Use mainnet, testnet, devnet, "
                + "or a network ID in digits.";
            return null;
        }

        XrplWallet wallet = XrplWallet.Generate();

        try
        {
            return await vault.StoreAsync(
                new NewVaultEntry(VaultEntryKind.Generated, Label.Trim(), network, wallet.ClassicAddress, wallet.Seed),
                cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            return null;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.NewAccountModelTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/NewAccountModel.cs tests/XrplFile.App.Core.Tests/NewAccountModelTests.cs
git commit -m "feat(app): generating a throwaway key"
```

---

## Task 7: Funding — the address, its QR, and the balance

Spec section 4.1 and decision G10.

**Files:**
- Create: `app/XrplFile.App.Core/FundingModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/FundingModelTests.cs`

**Interfaces:**
- Consumes: `IKeyVault.ListAsync`; `IConnectionFactory`; `IClipboardService.CopyAsync(string, CancellationToken)`.
- Produces: `public sealed class FundingModel(IKeyVault vault, IConnectionFactory connections, IClipboardService clipboard)`
  with `string? Address`, `string? QrPayload`, `string? Network`, `long BalanceDrops`,
  `bool AccountExists`, `string? Message`, `Task SelectAsync(string reference, CancellationToken)`,
  `Task RefreshAsync(CancellationToken)`, `Task CopyAddressAsync(CancellationToken)`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/FundingModelTests.cs`:

```csharp
using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class FundingModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeClipboard _clipboard = new();

    private async Task<(FundingModel Model, string Reference, NewVaultEntry Entry)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        NewVaultEntry entry = Entry("throwaway", VaultEntryKind.Generated);
        string reference = await vault.StoreAsync(entry, token);

        FundingModel model = new(vault, _connections, _clipboard);
        return (model, reference, entry);
    }

    [Fact]
    public async Task Selecting_a_key_shows_its_address_and_a_QR_of_exactly_that_address()
    {
        (FundingModel model, string reference, NewVaultEntry entry) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(entry.Address, model.QrPayload);
    }

    [Fact]
    public async Task An_unfunded_address_reads_as_no_account_yet()
    {
        (FundingModel model, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(model.AccountExists);
        Assert.Equal(0, model.BalanceDrops);
    }

    [Fact]
    public async Task A_funded_address_reads_back_its_balance()
    {
        (FundingModel model, string reference, NewVaultEntry entry) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.AccountExists);
        Assert.Equal(30_000_000, model.BalanceDrops);
    }

    [Fact]
    public async Task Copying_the_address_does_not_put_it_on_a_clearing_timer()
    {
        (FundingModel model, string reference, NewVaultEntry entry) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.CopyAddressAsync(TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, _clipboard.Copied);
        Assert.Null(_clipboard.Expiry);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_so_and_keeps_the_address()
    {
        (FundingModel model, string reference, NewVaultEntry entry) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
    }
}
```

`FakeClipboard` already exists in `tests/XrplFile.App.Core.Tests/HistoryModelTests.cs` — use it
rather than writing a second. If it is a private nested type there, promote it to its own file the
way `NeverVault` was promoted, and say so in your report.

The QR test asserts the payload is the address and **nothing else**: no `xrpl:` scheme, no amount, no
tag. A wallet scanning it must be free to send whatever the person decides, and an amount baked into
a QR is this application guessing a number it was told not to guess (design G10).

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.FundingModelTests"`
Expected: does not compile — `FundingModel` does not exist.

- [ ] **Step 3: Write the model**

`app/XrplFile.App.Core/FundingModel.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The funding screen (design 4.1): the address to send to, a QR of it, and what the account holds
/// now. It computes no target amount — what a publication needs depends on the file, and C2's
/// estimate screen already answers that exactly, against the real bytes (design G10).
/// </summary>
public sealed class FundingModel(IKeyVault vault, IConnectionFactory connections, IClipboardService clipboard)
{
    public string? Address { get; private set; }

    /// <summary>
    /// What the QR carries: the address, and nothing else. No scheme, no amount, no destination tag
    /// — a wallet scanning it must be free to send whatever the person decided, and an amount baked
    /// in here would be this application guessing a number it does not have.
    /// </summary>
    public string? QrPayload => Address;

    public string? Network { get; private set; }

    public long BalanceDrops { get; private set; }

    /// <summary>Whether the ledger carries this account at all. An address nobody funded is not on it.</summary>
    public bool AccountExists { get; private set; }

    public string? Message { get; private set; }

    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;
        Address = null;
        Network = null;
        BalanceDrops = 0;
        AccountExists = false;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            VaultEntryInfo? entry = listed.FirstOrDefault(
                info => string.Equals(info.Reference, reference, StringComparison.Ordinal));

            if (entry is null)
            {
                Message = "That key is no longer in the vault.";
                return;
            }

            Address = entry.Address;
            Network = entry.Network;
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Message = null;

        if (Address is null || Network is null) return;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(Network, cancellationToken).ConfigureAwait(false);

            AccountState state = await connection.Source
                .AccountStateAsync(Address, cancellationToken).ConfigureAwait(false);

            BalanceDrops = state.BalanceDrops;
            AccountExists = state.BalanceDrops > 0;
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
    }

    public Task CopyAddressAsync(CancellationToken cancellationToken) =>
        Address is null ? Task.CompletedTask : clipboard.CopyAsync(Address, cancellationToken);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.FundingModelTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/FundingModel.cs tests/XrplFile.App.Core.Tests
git commit -m "feat(app): the funding screen's model"
```

---

## Task 8: Closing an account

Spec section 5 — the destructive screen, and the most careful model in this part.

**Files:**
- Create: `app/XrplFile.App.Core/CloseErrorMessages.cs`
- Create: `app/XrplFile.App.Core/CloseAccountModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/CloseErrorMessagesTests.cs`
- Test: `tests/XrplFile.App.Core.Tests/CloseAccountModelTests.cs`

**Interfaces:**
- Consumes: `ThrowawayAccount.PreviewDeletionAsync`/`DeleteAsync`, `DeletionResult`,
  `DeletionOutcome` from Task 2; `AccountClosure` and `VaultEntryEdit.Closure` from Task 4;
  `VaultSigner.CreateAsync(IKeyVault, string reference, CancellationToken, VaultAutoLock? = null)`;
  `IConnectionFactory`.
- Produces:
  - `public static class CloseErrorMessages { public static string For(DeletionOutcome outcome); }`
  - `public sealed class CloseAccountModel(IKeyVault vault, IConnectionFactory connections, TimeProvider time)`
    with `IReadOnlyList<VaultEntryInfo> Candidates`, `string? Destination`, `long DestinationBalanceDrops`,
    `DeletionResult? Preview`, `bool CanClose`, `string? Message`, `AccountClosure? Closed`,
    `Task SelectAsync(string reference, CancellationToken)`, `void ChooseDestination(string address)`,
    `Task PreflightAsync(CancellationToken)`, `Task CloseAsync(CancellationToken)`

- [ ] **Step 1: Write the failing message tests**

`tests/XrplFile.App.Core.Tests/CloseErrorMessagesTests.cs`:

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class CloseErrorMessagesTests
{
    [Fact]
    public void Every_outcome_has_a_sentence()
    {
        foreach (DeletionOutcome outcome in Enum.GetValues<DeletionOutcome>())
        {
            string message = CloseErrorMessages.For(outcome);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{outcome} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        string[] all = [.. Enum.GetValues<DeletionOutcome>().Select(CloseErrorMessages.For)];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_three_that_must_never_be_conflated_are_not()
    {
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.NotYet), CloseErrorMessages.For(DeletionOutcome.DestinationMissing));
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.NotYet), CloseErrorMessages.For(DeletionOutcome.DestinationRefuses));
        Assert.NotEqual(CloseErrorMessages.For(DeletionOutcome.DestinationMissing), CloseErrorMessages.For(DeletionOutcome.DestinationRefuses));
    }
}
```

- [ ] **Step 2: Write the messages**

`app/XrplFile.App.Core/CloseErrorMessages.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// What a person is told about a deletion that did not happen (design 6). The three refusals have
/// nothing in common in what to do next — wait, check the address, or send somewhere else — so they
/// never share a sentence.
/// </summary>
public static class CloseErrorMessages
{
    public static string For(DeletionOutcome outcome) => outcome switch
    {
        DeletionOutcome.Ready =>
            "This account can be closed now.",
        DeletionOutcome.NotYet =>
            "This account cannot be closed yet. The ledger refuses to delete an account until 255 ledgers have closed since its last transaction, and it must hold enough to cover the fee.",
        DeletionOutcome.DestinationMissing =>
            "There is no such account on this network, and the remainder would have nowhere to go. Check the address.",
        DeletionOutcome.DestinationRefuses =>
            "That account will not accept this payment: it either requires a destination tag or only accepts senders it has authorised. Send the remainder somewhere else.",
        DeletionOutcome.Submitted =>
            "The account was closed and its remainder is on its way.",
        DeletionOutcome.Queued =>
            "The node queued the deletion rather than applying it. Until a ledger takes it the account still exists and still holds everything, so check again in a moment.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "no sentence for this outcome"),
    };
}
```

The `_ =>` arm throws so a value added to `DeletionOutcome` later fails the enum-coverage test rather
than reaching a screen as a number.

- [ ] **Step 3: Run the message tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.CloseErrorMessagesTests"`
Expected: 3 passing.

- [ ] **Step 4: Write the failing model tests**

`tests/XrplFile.App.Core.Tests/CloseAccountModelTests.cs`:

```csharp
using Xunit;

using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

public sealed class CloseAccountModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();

    private const string Home = "rHome";

    private async Task<(CloseAccountModel Model, KeyVault Vault, string Reference, XrplWallet Wallet)> OpenAsync(
        CancellationToken token, long balanceDrops = 30_000_000, bool destinationExists = true)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "throwaway", "1", wallet.ClassicAddress, wallet.Seed), token);

        _connections.Ledger.Balances[wallet.ClassicAddress] = balanceDrops;

        if (destinationExists)
        {
            _connections.Ledger.Destinations[Home] =
                new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);
        }

        CloseAccountModel model = new(vault, _connections, TimeProvider.System);
        return (model, vault, reference, wallet);
    }

    [Fact]
    public async Task With_no_destination_chosen_it_will_not_close()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.Null(model.Destination);
        Assert.False(model.CanClose);
    }

    [Fact]
    public async Task The_other_keys_in_the_vault_are_offered_as_destinations_and_this_account_is_not()
    {
        (CloseAccountModel model, KeyVault vault, string reference, XrplWallet wallet) =
            await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("somewhere else"), TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Address, wallet.ClassicAddress, StringComparison.Ordinal));
        Assert.Contains(model.Candidates, info => string.Equals(info.Label, "somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preflight_that_can_proceed_shows_the_destination_balance_and_what_will_move()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Preview);
        Assert.Equal(DeletionOutcome.Ready, model.Preview.Outcome);
        Assert.Equal(5_000_000, model.DestinationBalanceDrops);
        Assert.True(model.Preview.ArrivingDrops > 0);
        Assert.True(model.CanClose);
    }

    [Fact]
    public async Task A_destination_the_ledger_does_not_carry_refuses_and_will_not_close()
    {
        (CloseAccountModel model, _, string reference, _) =
            await OpenAsync(TestContext.Current.CancellationToken, destinationExists: false);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationMissing, model.Preview!.Outcome);
        Assert.False(model.CanClose);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.DestinationMissing), model.Message);
    }

    [Fact]
    public async Task Closing_records_the_closure_on_the_entry()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(
            (await vault.ListAsync(TestContext.Current.CancellationToken))
                .Where(entry => string.Equals(entry.Reference, reference, StringComparison.Ordinal)));

        Assert.NotNull(info.Closure);
        Assert.Equal(Home, info.Closure.Destination);
        Assert.Equal(model.Preview!.ArrivingDrops, info.Closure.ArrivedDrops);
    }

    [Fact]
    public async Task A_queued_deletion_records_nothing_yet()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Sink.QueueEverything = true;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(
            (await vault.ListAsync(TestContext.Current.CancellationToken))
                .Where(entry => string.Equals(entry.Reference, reference, StringComparison.Ordinal)));

        Assert.Null(info.Closure);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.Queued), model.Message);
    }

    [Fact]
    public async Task Closing_without_a_preflight_does_nothing()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_connections.Sink.Submitted);
    }
}
```

`FakeLedgerSink` may have no `QueueEverything`. Read it: it already carries several dictionaries for
steering outcomes per submission, and one of them may already express "answer `terQUEUED`". Use
what is there; add the smallest additive member only if nothing fits, and say so in your report.

The queued test is the one that matters most in this file. A queued deletion has **not** happened:
the account still exists and still holds everything. Recording a closure then would tell the person
their money went home when it may yet be dropped from the queue.

- [ ] **Step 5: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.CloseAccountModelTests"`
Expected: does not compile — `CloseAccountModel` does not exist.

- [ ] **Step 6: Write the model**

`app/XrplFile.App.Core/CloseAccountModel.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// Closing a throwaway account (design 5). The most careful screen in this application: an
/// <c>AccountDelete</c> sends everything the account holds and cannot be undone, and its fee is one
/// owner reserve, which a refused submission keeps.
///
/// Nothing here ever chooses a destination (design G3), and nothing submits until a preflight has
/// said it can succeed (design G5).
/// </summary>
public sealed class CloseAccountModel(IKeyVault vault, IConnectionFactory connections, TimeProvider time)
{
    private string? _reference;
    private string? _address;
    private string? _network;

    /// <summary>The vault's other keys, offered as destinations. Never this account itself.</summary>
    public IReadOnlyList<VaultEntryInfo> Candidates { get; private set; } = [];

    public string? Destination { get; private set; }

    /// <summary>What the destination holds now — how a person recognises the account they meant (design G6).</summary>
    public long DestinationBalanceDrops { get; private set; }

    /// <summary>What the deletion would do, as the library computed it. Null until a preflight has run.</summary>
    public DeletionResult? Preview { get; private set; }

    public string? Message { get; private set; }

    /// <summary>The closure this screen recorded, once one happened.</summary>
    public AccountClosure? Closed { get; private set; }

    public bool CanClose => Preview is { Outcome: DeletionOutcome.Ready } && Destination is not null;

    public async Task SelectAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        Message = null;
        Preview = null;
        Destination = null;
        DestinationBalanceDrops = 0;
        Closed = null;
        _reference = null;
        _address = null;
        _network = null;

        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            VaultEntryInfo? entry = listed.FirstOrDefault(
                info => string.Equals(info.Reference, reference, StringComparison.Ordinal));

            if (entry is null)
            {
                Message = "That key is no longer in the vault.";
                return;
            }

            _reference = reference;
            _address = entry.Address;
            _network = entry.Network;

            Candidates =
            [
                .. listed.Where(info => !string.Equals(info.Reference, reference, StringComparison.Ordinal)),
            ];
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Names the destination. Taking a bare address rather than a vault reference is deliberate: the
    /// commonest destination is an exchange or another wallet, which the vault has never heard of.
    /// </summary>
    public void ChooseDestination(string address)
    {
        Destination = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Preview = null;
        DestinationBalanceDrops = 0;
        Message = null;
    }

    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        Message = null;
        Preview = null;

        if (_reference is null || _address is null || _network is null || Destination is null)
        {
            Message = "Choose where the remainder should go.";
            return;
        }

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(_network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, _reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult preview = await account
                .PreviewDeletionAsync(Destination, cancellationToken).ConfigureAwait(false);

            AccountState home = await connection.Source
                .AccountStateAsync(Destination, cancellationToken).ConfigureAwait(false);

            Preview = preview;
            DestinationBalanceDrops = home.BalanceDrops;

            if (preview.Outcome != DeletionOutcome.Ready)
            {
                Message = CloseErrorMessages.For(preview.Outcome);
            }
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }

    /// <summary>
    /// Submits the deletion. Does nothing unless a preflight has said it can succeed — pressing a
    /// button is not a reason to skip the checks that keep an owner reserve from being burned.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        Message = null;

        if (!CanClose || _reference is null || _network is null || Destination is null) return;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(_network, cancellationToken).ConfigureAwait(false);

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, _reference, cancellationToken).ConfigureAwait(false);

            ThrowawayAccount account = new(connection.Sink, connection.Source, signer);

            DeletionResult result = await account.DeleteAsync(Destination, cancellationToken).ConfigureAwait(false);

            Preview = result;
            Message = CloseErrorMessages.For(result.Outcome);

            // Recorded only for a deletion a ledger actually took. A queued one has not happened:
            // the account still exists and still holds everything, and the queue may yet drop it.
            if (result.Outcome != DeletionOutcome.Submitted) return;

            AccountClosure closure = new(time.GetUtcNow(), result.Destination, result.ArrivingDrops, result.TxHash);

            await vault.UpdateAsync(
                _reference, new VaultEntryEdit(Closure: closure), cancellationToken).ConfigureAwait(false);

            Closed = closure;
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
        }
    }
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.CloseAccountModelTests"`
Expected: 7 passing.

- [ ] **Step 8: Commit**

```bash
git add app/XrplFile.App.Core tests/XrplFile.App.Core.Tests
git commit -m "feat(app): closing a throwaway account"
```

---

## Task 9: The screens

Spec sections 4.1 and 5.1. Components hold no decisions.

**Files:**
- Create: `app/XrplFile.App/Components/Accounts.razor`
- Create: `app/XrplFile.App/Components/NewAccount.razor`
- Create: `app/XrplFile.App/Components/Funding.razor`
- Create: `app/XrplFile.App/Components/CloseAccount.razor`
- Modify: `app/XrplFile.App/Components/Shell.razor`
- Modify: `app/XrplFile.App/MauiProgram.cs`

**Interfaces:**
- Consumes: every model from Tasks 5–8; `QrMatrix.Build(string text)` returning `bool[][]`.

- [ ] **Step 1: Register the models**

In `app/XrplFile.App/MauiProgram.cs`, beside the existing model registrations:

```csharp
        services.AddSingleton<AccountsModel>();
        services.AddSingleton<NewAccountModel>();
        services.AddSingleton<FundingModel>();
        services.AddSingleton<CloseAccountModel>();
```

`CloseAccountModel` takes a `TimeProvider`, which is already registered
(`services.AddSingleton(TimeProvider.System)` in the same file), so the container resolves it.

- [ ] **Step 2: Add the Accounts tab**

In `app/XrplFile.App/Components/Shell.razor`, add `Accounts` to the `UnlockedView` enum, a fourth
button to the button group in the same shape as the existing three, and a branch rendering
`<Accounts />`. Follow the existing lines exactly — the buttons differ only in the enum value and the
label.

- [ ] **Step 3: Write `Accounts.razor`**

It renders the list and switches between the list, the new-account form, the funding screen and the
closing screen. Follow `Publish.razor`'s shape: a field saying which is showing, set by button
clicks, and the one-second `Timer` from `Shell.razor` — including its `CancellationTokenSource`,
`IDisposable` and `_disposed` guard — so a balance a person is watching updates without them
pressing anything.

Each row shows the label, the address, the network and, from `AccountRow.Stage`, one of: "a key, no
account yet", the balance, "can be closed now", or the closure's destination and amount. The stage is
the model's; the markup chooses which of four lines to draw and computes nothing.

- [ ] **Step 4: Write the other three**

`NewAccount.razor` binds `Label` and `Network`, and its button is disabled unless
`Model.CanCreate`. It says, in fixed copy, that this generates a key and that the account appears on
the ledger once somebody funds the address.

`Funding.razor` shows `Model.Address`, renders `QrMatrix.Build(Model.QrPayload)` the way the existing
seed screens render a QR — read one of them and follow it — a copy button calling
`CopyAddressAsync`, and the balance with `Model.AccountExists`.

`CloseAccount.razor` is the careful one. It shows the candidate list and a text box, both calling
`ChooseDestination`; a Check button calling `PreflightAsync`; and, once `Model.Preview` is `Ready`,
the confirmation: the destination address **in full and unabbreviated**, `DestinationBalanceDrops`,
`Preview.ArrivingDrops`, `Preview.FeeDrops`, and fixed copy saying the account will cease to exist
and that this cannot be undone. The Close button is disabled unless `Model.CanClose`.

When `Preview.Outcome` is `NotYet`, show `Preview.DeletableFromLedger` and the ledgers remaining
rather than a second button — waiting is not a reason to offer the payment refund (design G11).

- [ ] **Step 5: Build the application**

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Check for BOMs**

```bash
git diff --cached --name-only | while read -r f; do
  head -c 3 "$f" | grep -q $'\xef\xbb\xbf' && echo "BOM: $f"
done
```
Strip any that appear: this repository never changes a file's encoding.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App
git commit -m "feat(app): the accounts, funding and closing screens"
```

---

## Task 10: The whole suite, and the status tables

- [ ] **Step 1: Run everything**

```bash
dotnet build XrplFile.Libraries.slnf --configuration Release
dotnet run --project tests/XrplFile.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.Keys.Tests --configuration Release --no-build -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.App.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
```

and from `web`: `node --test test/*.test.mjs`.

Counts before this plan: Core 304, Keys 244, App.Core 305, web 74. Report what you actually get. The
web suite has nothing to do with this part — if it moved, something is wrong.

Then the integration half, with the stand up:

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
```
wait ~15 seconds, then
`dotnet run --project tests/XrplFile.Core.Tests --configuration Release --no-build -- -trait "Category=Integration"`
and bring it down with `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml down`.

And the application: `dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release`.

- [ ] **Step 2: Update the status tables**

Set C3's row to `shipped` in each of these, matching each document's own wording and keeping the
emphasis the C3 document puts on its own row:

- `docs/superpowers/specs/2026-09-15-throwaway-accounts-design.md`
- `docs/superpowers/specs/2026-09-15-publishing-design.md`
- `docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md`
- `docs/superpowers/specs/2026-09-13-key-vault-design.md`

- [ ] **Step 3: Correct the spec's "one method"**

The C3 design's section 3.1 says `ThrowawayAccount` gains one method. It gained two —
`PreviewDeletionAsync` and `DeleteAsync` — because the confirmation screen must show its figures
before the person commits. Rewrite that passage to say what was built and why, in the document's own
voice, where the claim currently is. Do not add a new section.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs
git commit -m "docs(specs): part C3 is shipped"
```

---

## Self-Review

**Spec coverage.** Section 1's scope → Tasks 5–9. G1 → Task 9 (its own tab). G2 → Task 2.
G3 → Task 8 (`ChooseDestination`, and `CanClose` requiring one). G4 → Tasks 1–3. G5 → Task 2's
preview and Task 8's `CloseAsync` guard. G6 → Task 9 Step 4. G7 → Task 4 (marked, never deleted).
G8 → Task 5 (written on the entry, read only for display). G9 → Task 6. G10 → Task 7.
G11 → Task 9 Step 4. G12 → Task 8's messages. Section 3.1 → Task 2. Section 3.2 → Task 1.
Section 4.1 → Tasks 5–7. Section 4.2 → Task 4. Section 4.3 → Tasks 5–8. Section 5 → Tasks 8–9.
Section 6 → Task 8. Section 7 → Task 3 and each model's tests.

**Not covered, and deliberately.** Section 5.3's payment fallback is not built. `RefundAsync` already
exists in the library and no screen in this plan calls it, because every refusal the preflight
produces is either "wait" or "use a different destination", and neither is a reason to leave the base
reserve behind forever (G11). If a real refusal turns up that waiting cannot fix, the fallback is one
screen and can be added then. This is a gap against section 5.3 and the final review should treat it
as one.

**Known judgement calls handed to implementers.** Three places ask the implementer to read the
existing test doubles and adapt rather than add: `FakeLedgerSource`'s current ledger and
`FakeLedgerSink`'s sequence (Task 2), `FakeClipboard`'s location (Task 7), and the sink's queued
outcome (Task 8). Each says what to do in either case and requires it to be reported. One place —
Task 4's absent-field test — may not be reachable depending on whether `XrplFile.Keys` exposes its
internals to its test project, and says to report which was achieved rather than claim coverage.

**Type consistency.** `DestinationState` is produced in Task 1 and consumed in Tasks 2 and 8.
`DeletionOutcome`/`DeletionResult` are produced in Task 2 and consumed in Tasks 3, 8 and 9.
`AccountClosure` is produced in Task 4 and consumed in Tasks 5, 8 and 9. `AccountRow`/`AccountStage`
are produced in Task 5 and consumed in Task 9. `ThrowawayAccount.DeletionSequenceDelta` is used in
Task 5 and is the same constant Task 2's preview relies on through `DeletableFromLedgerAsync`.
`PublishErrorMessages.For` and `VaultErrorMessages.For` are part C2's and C1's, used unchanged.
