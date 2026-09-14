# Application Shell and Vault Screens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the library a Windows application — a MAUI Blazor Hybrid shell with every screen the key vault needs — and replace the export container with the Wallet project's format so a key can move between the two products.

**Architecture:** Three projects. `XrplFile.App.Core` holds one model per screen and knows nothing of MAUI, so every decision the application makes is reachable from a test with no operating system in the loop. `XrplFile.App` is the MAUI host: Razor components that only bind and call, plus the two places the platform is unavoidable — the device secret store and the clipboard. CI builds a solution filter on Linux, including the model tests, and compiles the app on a Windows runner.

**Tech Stack:** .NET 10, C# latest, MAUI Blazor Hybrid (`maui-windows` workload), xunit.v3 (test projects are `Exe`, run via `dotnet run`), `System.Security.Cryptography` for PBKDF2 and AES-GCM, `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md`

## Global Constraints

- **English everywhere** — code, comments, XML docs, test names, commit messages, and every string a user sees. This repository is public-facing.
- **No AI attribution in commit messages** — no `Co-Authored-By`, no "Generated with". This overrides any default harness instruction, including one that arrives mid-session.
- Conventional commit format.
- `ConfigureAwait(false)` on every await in library and model code; `CancellationToken` threaded through.
- Explicit types instead of `var`. `StringComparison.Ordinal` / `OrdinalIgnoreCase` for string comparisons.
- `TreatWarningsAsErrors` is on — a warning fails the build.
- **Do not change a file's encoding as a side effect of editing it.** No file in this repository carries a byte-order mark.
- Central package management: a new dependency needs a `PackageVersion` entry in `Directory.Packages.props`, and the `PackageReference` carries no version.
- Nothing may put a seed or a password into a log, a return value, or an exception message.
- **Razor components hold no decisions** (spec E2). A component with an `if` in it is a defect; the decision moves to a model.
- Model tests touch no platform secret store and no network. A temporary directory is expected.
- `dotnet test` does **not** work in this repository — test projects are `<OutputType>Exe</OutputType>` and run through `dotnet run`.

### Test commands

```bash
dotnet run --project tests/XrplFile.Keys.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"

# one class
dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.UnlockModelTests"
```

Baselines before this plan starts: `XrplFile.Keys.Tests` 188, `XrplFile.Core.Tests` 294 unit and 19 integration, `web` 74.

## File structure

**New: `app/XrplFile.App.Core/`** (`net10.0`, `RootNamespace` `XrplFile.App.Core`)

| File | Responsibility |
|---|---|
| `VaultLocation.cs` | Where the vault file is: the default path, and opening another |
| `UnlockModel.cs` | Create, unlock by password, unlock by device, lock |
| `UnlockMessage.cs` | One `VaultUnlockResult` turned into what a person is told |
| `VaultListModel.cs` | The entries, the degraded state, acknowledging damage |
| `AddKeyModel.cs` | Import a seed or generate one; validation before anything is stored |
| `ExportModel.cs` | Export to a container, and the QR form of one |
| `RevealModel.cs` | A seed on screen: hidden, shown, hidden again, cleared on lock |
| `PasswordChangeModel.cs` | `VaultPasswordChange` turned into the sentences it stands for |
| `DeviceUnlockModel.cs` | Turning "remember on this device" on and off |
| `DeleteKeyModel.cs` | Deleting an entry, and what it warns when never exported |
| `IClipboardService.cs` | The clipboard seam, with the options a secret needs |
| `IFilePickerService.cs` | Choosing a file to write a container to, or to open a vault |

**New: `app/XrplFile.App/`** (`net10.0-windows10.0.19041.0`, MAUI Blazor Hybrid): `MauiProgram.cs`, `Components/` (Razor), `Platform/SecureStorageDeviceSecretStore.cs`, `Platform/MauiClipboardService.cs`, `Platform/MauiFilePickerService.cs`.

**New: `tests/XrplFile.App.Core.Tests/`** plus `VaultHarness.cs` — a real `KeyVault` over a temporary directory, an in-memory device store, small `KdfParameters`.

**Modified in `src/XrplFile.Keys/`:** `Pbkdf2Kdf.cs` and `Base45.cs` (new), `BackupContainer.cs` (new, replacing `ExportContainer.cs`), `VaultEntry.cs`, `VaultSigner.cs`, `KeyVault.cs`.

**Modified:** `XrplFile.sln`, new `XrplFile.Libraries.slnf`, `.github/workflows/test.yml`, `README.md`.

---

## Task 0: Prove the two things the spec refused to assume

**Files:**
- Create: `tools/XrplFile.KeyBench/Pbkdf2Bench.cs` (added to the existing bench tool)
- Create: a throwaway MAUI project outside the repository, deleted when the question is answered
- Modify: `docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md` section 9

**Interfaces:**
- Consumes: nothing.
- Produces: the PBKDF2 iteration constant Task 1 writes into `Pbkdf2Kdf`, and a recorded answer on whether a MAUI Blazor Hybrid app builds and starts here.

- [ ] **Step 1: Measure the PBKDF2 iteration count**

Append to `tools/XrplFile.KeyBench/Program.cs`:

```csharp
static void TimePbkdf2()
{
    byte[] password = System.Text.Encoding.UTF8.GetBytes("correct horse battery staple");
    byte[] salt = new byte[16];
    Random.Shared.NextBytes(salt);

    Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA256, 32);   // untimed, for JIT

    foreach (int iterations in new[] { 600_000, 700_000, 800_000, 900_000, 1_000_000 })
    {
        Stopwatch watch = Stopwatch.StartNew();
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        watch.Stop();
        Console.WriteLine($"PBKDF2-SHA256 t={iterations} -> {watch.ElapsedMilliseconds} ms");
    }
}

TimePbkdf2();
```

- [ ] **Step 2: Run it in Release and pick**

```bash
dotnet run -c Release --project tools/XrplFile.KeyBench
```

The Wallet bounds the range at 600,000 to 1,000,000 and a container written on a fast machine must not hang an import on a slow one. Pick the **largest** value in that range whose time is at or under **500 ms**; if none is, use the floor of 600,000. Record the whole table.

- [ ] **Step 3: Prove a MAUI Blazor Hybrid app builds and starts**

Outside the repository, in a scratch directory:

```bash
dotnet new maui-blazor -n ShellProbe -f net10.0
```

Trim its target frameworks to `net10.0-windows10.0.19041.0`, add a `ProjectReference` to `E:\GIT\XRPL\xrpl-file\src\XrplFile.Keys\XrplFile.Keys.csproj`, call `new KeyVault(...)` from `MauiProgram` so the reference is real rather than nominal, then:

```bash
dotnet build -c Debug
dotnet run -c Debug
```

Confirm a window appears. Record the template name, the exact target framework string that worked, and anything that had to be changed to make it build — that is what Task 5 will need.

Delete the scratch project.

- [ ] **Step 4: Amend the spec**

Replace section 9's two items with what was measured: the iteration count with its time, and the MAUI answer with the target framework string and any adjustment. If the probe failed, say exactly how — that changes Task 5 and possibly the whole approach, so it is reported, not worked around.

- [ ] **Step 5: Commit**

```bash
git add tools/XrplFile.KeyBench docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md
git commit -m "chore(app): measure the container's cost and prove the shell starts"
```

---

## Task 1: PBKDF2, pinned to the RFC

**Files:**
- Create: `src/XrplFile.Keys/Pbkdf2Kdf.cs`
- Create: `tests/XrplFile.Keys.Tests/Pbkdf2KdfTests.cs`

**Interfaces:**
- Consumes: Task 0's iteration count.
- Produces:
  ```csharp
  internal static class Pbkdf2Kdf
  {
      internal const int KdfId = 1;
      internal const int IterationsFloor = 600_000;
      internal const int IterationsCeiling = 1_000_000;
      internal static int DefaultIterations { get; }
      internal static byte[] Derive(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength);
  }
  ```

**A refinement of the spec, and why.** The spec says a `Pbkdf2Kdf` reporting `KdfId` 1 is added "beside" `Argon2idKdf`. It does **not** implement `IPasswordKdf`. That interface takes a `KdfParameters`, which since B's last round validates memory and parallelism bounds — values PBKDF2 has no use for, so a PBKDF2 parameter set would have to carry meaningless numbers that pass a validator, which is a trap rather than an abstraction. The container's `kdfId` already selects the algorithm; there is no polymorphism to preserve. `IPasswordKdf` stays the vault's, with Argon2id its only implementation.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Keys.Tests/Pbkdf2KdfTests.cs`. **Copy the vectors from RFC 6070 — do not write them from memory.** RFC 6070 publishes PBKDF2-HMAC-**SHA1** vectors; this code uses SHA-256, so take the SHA-256 vectors from RFC 7914 section 11, which restates the same inputs for SHA-256. Fetch the RFC; if you cannot reach it, say so in your report and stop rather than inventing a value.

```csharp
using Xunit;

namespace XrplFile.Keys.Tests;

public class Pbkdf2KdfTests
{
    [Fact]
    public void The_derivation_matches_the_published_vector()
    {
        // RFC 7914 section 11: PBKDF2-HMAC-SHA256, P = "passwd", S = "salt", c = 1, dkLen = 64.
        byte[] expected = Convert.FromHexString("<paste from RFC 7914 section 11>");

        byte[] actual = Pbkdf2Kdf.Derive("passwd"u8, "salt"u8, iterations: 1, outputLength: 64);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_same_inputs_derive_the_same_bytes()
    {
        byte[] password = "correct horse battery staple"u8.ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x07, 16).ToArray();

        Assert.Equal(
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32),
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32));
    }

    [Fact]
    public void A_different_iteration_count_derives_different_bytes()
    {
        byte[] password = "correct horse battery staple"u8.ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x07, 16).ToArray();

        Assert.NotEqual(
            Pbkdf2Kdf.Derive(password, salt, iterations: 1000, outputLength: 32),
            Pbkdf2Kdf.Derive(password, salt, iterations: 1001, outputLength: 32));
    }

    [Fact]
    public void The_default_iteration_count_is_the_measured_one_and_inside_the_shared_range()
    {
        // Task 0 measured this. It travels inside every container's header, and the Wallet refuses
        // a count outside the range before it runs the derivation.
        Assert.Equal(<measured iterations>, Pbkdf2Kdf.DefaultIterations);
        Assert.InRange(Pbkdf2Kdf.DefaultIterations, Pbkdf2Kdf.IterationsFloor, Pbkdf2Kdf.IterationsCeiling);
    }
}
```

Fill the RFC hex and the measured count before running.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet run --project tests/XrplFile.Keys.Tests -- -class "XrplFile.Keys.Tests.Pbkdf2KdfTests"
```

Expected: does not compile — `Pbkdf2Kdf` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;

namespace XrplFile.Keys;

/// <summary>
/// Password stretching for the export container, which is the Wallet project's format and fixes
/// PBKDF2-HMAC-SHA256 as <c>kdfId</c> 1.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="IPasswordKdf"/>. That interface carries a
/// <see cref="KdfParameters"/> whose memory and parallelism PBKDF2 has no use for and which now
/// validates them, so an implementation here would have to invent numbers that pass a validator.
/// The container's <c>kdfId</c> already chooses the algorithm; there is nothing to dispatch on.
///
/// The output must stay byte-identical to the Wallet's, or a container written by one product does
/// not open in the other. The published vector is what holds that.
/// </remarks>
internal static class Pbkdf2Kdf
{
    internal const int KdfId = 1;

    /// <summary>The lowest count the Wallet accepts in a container header.</summary>
    internal const int IterationsFloor = 600_000;

    /// <summary>The highest, so a container written on a fast machine cannot hang a slow import.</summary>
    internal const int IterationsCeiling = 1_000_000;

    /// <summary>Measured on a desktop machine against the slowest import a person will accept.</summary>
    internal static int DefaultIterations => 0;   // replace with Task 0's number

    internal static byte[] Derive(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, int iterations, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 4);

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputLength);
    }
}
```

- [ ] **Step 4: Run and watch it pass.**

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Keys/Pbkdf2Kdf.cs tests/XrplFile.Keys.Tests/Pbkdf2KdfTests.cs
git commit -m "feat(keys): the derivation the shared container format fixes"
```

---

## Task 2: Base45

**Files:**
- Create: `src/XrplFile.Keys/Base45.cs`
- Create: `tests/XrplFile.Keys.Tests/Base45Tests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal static class Base45
  {
      internal static string Encode(ReadOnlySpan<byte> data);
      internal static bool TryDecode(string text, out byte[] data);
      internal static int GetEncodedLength(int byteCount);
      internal static int GetMaxDecodedLength(int encodedLength);
  }
  ```

Lifted from `Encoding/Base45.cs` in the Wallet project, which has no `using` directives at all. Put its provenance in the file header: the project, the path, and that the encoding is RFC 9285.

**Why this exists at all.** A container becomes a QR code by way of Base45 rather than raw bytes. The reason is established from the Wallet's own scanner code, not assumed: the scanners on both mobile platforms hand back a string, so a byte with the high bit set does not survive the trip, the authentication tag fails, and the user is told the password is wrong when it was right. Base45's alphabet fits QR's alphanumeric mode.

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace XrplFile.Keys.Tests;

public class Base45Tests
{
    [Theory]
    // RFC 9285 section 4.2. Copy these from the RFC, not from memory.
    [InlineData("AB", "BB8")]
    [InlineData("Hello!!", "%69 VD92EX0")]
    [InlineData("base-45", "UJCLQE7W581")]
    public void The_encoding_matches_the_rfc(string text, string expected)
    {
        Assert.Equal(expected, Base45.Encode(System.Text.Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Every_byte_value_survives_a_round_trip()
    {
        byte[] all = new byte[256];
        for (int i = 0; i < all.Length; i++) all[i] = (byte)i;

        Assert.True(Base45.TryDecode(Base45.Encode(all), out byte[] back));
        Assert.Equal(all, back);
    }

    [Fact]
    public void A_string_outside_the_alphabet_is_refused_rather_than_throwing()
    {
        Assert.False(Base45.TryDecode("not base45!", out byte[] _));
    }

    [Fact]
    public void An_empty_input_round_trips()
    {
        Assert.True(Base45.TryDecode(Base45.Encode([]), out byte[] back));
        Assert.Empty(back);
    }

    [Fact]
    public void The_qr_budget_holds_at_its_boundary()
    {
        // The Wallet measured version 40 at correction level H as 1852 alphanumeric characters,
        // which is 1234 container bytes. A container at that size must still encode inside it.
        Assert.True(Base45.GetEncodedLength(1234) <= 1852);
        Assert.True(Base45.GetEncodedLength(1235) > 1852);
    }
}
```

Verify the three RFC rows against RFC 9285 section 4.2 before running; if a row differs, the RFC is right.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Lift the implementation** from the Wallet project, unchanged except for the namespace and the provenance header.

- [ ] **Step 4: Run and watch them pass.**

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Keys/Base45.cs tests/XrplFile.Keys.Tests/Base45Tests.cs
git commit -m "feat(keys): Base45, because a QR scanner hands back a string"
```

---

## Task 3: The container becomes the Wallet's

The largest library task. It replaces a shipped type, so read the spec's section 5 in full before starting.

**Files:**
- Create: `src/XrplFile.Keys/BackupContainer.cs`, `BackupPayload.cs`
- Delete: `src/XrplFile.Keys/ExportContainer.cs`
- Modify: `src/XrplFile.Keys/KeyVault.cs` (`ExportAsync`)
- Modify/replace: `tests/XrplFile.Keys.Tests/ExportContainerTests.cs`
- Create: `tests/XrplFile.Keys.Tests/fixtures/wallet-container.bin` and `WalletCompatibilityTests.cs`

**Interfaces:**
- Consumes: `Pbkdf2Kdf` (Task 1), `Base45` (Task 2).
- Produces:
  ```csharp
  public enum BackupUnpackError { None, NotOurContainer, WrongPassword, PasswordlessContainerNotAllowed, IterationsOutOfRange }

  public sealed record BackupWalletEntry(
      string KeyValue, string? MnemonicPassphrase, string ClassicAddress, int KeyType, string Algorithm,
      string? DerivationPath, string? MasterAddress, string? Description,
      string? Folder = null, string? DestinationTag = null,
      string? Network = null, string? Kind = null, uint? LastKnownSequence = null, long? DeletableFromLedger = null);

  public sealed class BackupPayload { public required IReadOnlyList<BackupWalletEntry> Wallets { get; init; } }

  public static class BackupContainer
  {
      public const int CurrentVersion = 1;
      public const int MaxInflatedSize;
      public static byte[] Pack(BackupPayload payload, string? password, int iterations);
      public static bool TryUnpack(byte[] data, string? password, bool fromFile, out BackupPayload? payload, out BackupUnpackError error);
      public static bool DeclaresNoPassword(ReadOnlySpan<byte> container);
  }
  ```

Two deliberate differences from the Wallet's signatures: no `IPasswordKdf` parameter, because `kdfId` fixes the algorithm (Task 1's note); and a `fromFile` flag on unpacking, because the refusal of a password-less container is about **where it came from**, and the container cannot know that for itself.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Keys.Tests/BackupContainerTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Keys.Tests;

public class BackupContainerTests
{
    private static BackupPayload OneEntry() => new()
    {
        Wallets =
        [
            new BackupWalletEntry(
                KeyValue: "sEdTM1uX8pu2do5XvTnutH6HsouMaM2",
                MnemonicPassphrase: null,
                ClassicAddress: "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
                KeyType: 0,
                Algorithm: "ed25519",
                DerivationPath: null,
                MasterAddress: null,
                Description: "a key",
                Network: "testnet",
                Kind: "Generated"),
        ],
    };

    [Fact]
    public void A_container_round_trips()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "container password", iterations: 1000);

        Assert.True(BackupContainer.TryUnpack(container, "container password", fromFile: true,
            out BackupPayload? opened, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.None, error);
        Assert.Equal("sEdTM1uX8pu2do5XvTnutH6HsouMaM2", opened!.Wallets[0].KeyValue);
        Assert.Equal("testnet", opened.Wallets[0].Network);
    }

    [Fact]
    public void The_header_is_the_wallets_layout()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "p", iterations: 1000);

        Assert.Equal("SBW1"u8.ToArray(), container[..4]);
        Assert.Equal(BackupContainer.CurrentVersion, container[4]);
        Assert.Equal(1, container[5]);                                             // kdfId = PBKDF2
        Assert.Equal(1000, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(6, 4)));
    }

    [Fact]
    public void A_wrong_password_is_told_apart_from_a_foreign_container()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);

        Assert.False(BackupContainer.TryUnpack(container, "wrong", fromFile: true, out _, out BackupUnpackError wrong));
        Assert.Equal(BackupUnpackError.WrongPassword, wrong);

        Assert.False(BackupContainer.TryUnpack("not a container at all"u8.ToArray(), "right", fromFile: true,
            out _, out BackupUnpackError foreign));
        Assert.Equal(BackupUnpackError.NotOurContainer, foreign);
    }

    [Fact]
    public void A_tampered_header_reads_as_a_wrong_password_on_purpose()
    {
        // The header is the associated data, so any edit fails the tag exactly as a wrong password
        // does. The two are deliberately not told apart: the only thing a user can reason about is
        // the password.
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        container[4] ^= 0xFF;

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.WrongPassword, error);
    }

    [Fact]
    public void An_iteration_count_outside_the_shared_range_is_refused_before_the_derivation()
    {
        byte[] container = BackupContainer.Pack(OneEntry(), "right", iterations: 1000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(6, 4), 50_000_000);

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.IterationsOutOfRange, error);
    }

    [Fact]
    public void A_password_less_container_is_refused_when_it_arrives_as_a_file()
    {
        // Nothing about a password-less container is signed, so anyone can write one. Accepting a
        // file would let a stranger's bytes arrive on the import screen as a list of keys to pick.
        byte[] container = BackupContainer.Pack(OneEntry(), password: null, iterations: 0);

        Assert.True(BackupContainer.DeclaresNoPassword(container));
        Assert.False(BackupContainer.TryUnpack(container, null, fromFile: true, out _, out BackupUnpackError refused));
        Assert.Equal(BackupUnpackError.PasswordlessContainerNotAllowed, refused);

        Assert.True(BackupContainer.TryUnpack(container, null, fromFile: false, out _, out BackupUnpackError allowed));
        Assert.Equal(BackupUnpackError.None, allowed);
    }

    [Fact]
    public void A_payload_that_expands_past_the_cap_is_not_a_wrong_password()
    {
        // The password was right; the container is not something this build will open.
        byte[] container = PackDeliberatelyOversizedPayload("right");

        Assert.False(BackupContainer.TryUnpack(container, "right", fromFile: true, out _, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.NotOurContainer, error);
    }
}
```

`PackDeliberatelyOversizedPayload` builds a payload whose inflated JSON exceeds `MaxInflatedSize` — a few thousand entries with long descriptions compresses small and inflates large. Write it in the test file.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Lift `BackupContainer` and `BackupPayload`**

From the Wallet project, with provenance in the file headers. Changes on the way in, each of which must appear in your report:

- the `IPasswordKdf kdf` parameters become direct calls to `Pbkdf2Kdf.Derive`;
- `VaultParameters.*` constants become local constants with the same values: key 32, nonce 12, tag 16, salt 16;
- `TryUnpack` gains `bool fromFile` and refuses a password-less container when it is true;
- `BackupWalletEntry` gains `Network`, `Kind`, `LastKnownSequence` and `DeletableFromLedger` as optional trailing parameters — the same way that record already grew `Folder` and `DestinationTag`, which their own documentation says leaves the version byte alone;
- keep `MaxInflatedSize` and its derivation, keep the four outcomes, keep `DefaultIgnoreCondition = WhenWritingNull` so an unused field costs nothing against the QR budget.

- [ ] **Step 4: Point `KeyVault.ExportAsync` at it**

`ExportAsync` currently calls `ExportContainer.Protect`. It now builds a `BackupPayload` from the revealed entry and calls `BackupContainer.Pack` with `Pbkdf2Kdf.DefaultIterations`. Everything else about that method — revealing first, marking `Exported` only after the container exists, rolling back on failure, returning the container even when the mark fails — is unchanged and its tests must still pass untouched.

- [ ] **Step 5: Delete `ExportContainer.cs`** and rewrite `ExportContainerTests.cs` against the new type. Tests that pinned the old `XFK1` layout are replaced; tests that pinned behaviour — the rollback paths, `Exported` being set only on success — are kept and must still pass.

- [ ] **Step 6: Prove compatibility rather than assert it**

Generate one container with the Wallet's own implementation and check it in as `tests/XrplFile.Keys.Tests/fixtures/wallet-container.bin`. To generate it, write a throwaway program in the Wallet repository (`E:\Claude\src\release-image-correction-ec8f31`) that calls its `BackupContainer.Pack` with a known payload and a known password, run it, copy the bytes out, and delete the program. Record the payload and password in the test.

```csharp
[Fact]
public void A_container_the_wallet_wrote_opens_here()
{
    // The only test that can prove compatibility rather than assert it. Generated once by
    // SB.WalletApp.Security.Backup.BackupContainer.Pack and checked in.
    byte[] container = File.ReadAllBytes(Path.Combine("fixtures", "wallet-container.bin"));

    Assert.True(BackupContainer.TryUnpack(container, "<the password used>", fromFile: true,
        out BackupPayload? payload, out BackupUnpackError error));
    Assert.Equal(BackupUnpackError.None, error);
    Assert.Equal("<the address used>", payload!.Wallets[0].ClassicAddress);
    Assert.Null(payload.Wallets[0].Network);   // a wallet backup carries no network
}
```

Add the fixture to the test csproj with `CopyToOutputDirectory="PreserveNewest"`, as `tests/vectors/v1.json` already is in the Core test project.

If you cannot build the Wallet project to generate it, **stop and report that** — the fixture is the only thing standing between "we adopted their format" and "we believe we did".

- [ ] **Step 7: Run everything**

Keys twice, Core once. Then commit.

```bash
git add src/XrplFile.Keys tests/XrplFile.Keys.Tests
git commit -m "feat(keys)!: the export container is the wallet's format"
```

---

## Task 4: An entry knows its algorithm

**Files:**
- Modify: `src/XrplFile.Keys/VaultEntry.cs`, `VaultSigner.cs`, `KeyVault.cs`, `BackupContainer` payload mapping
- Modify: `tests/XrplFile.Keys.Tests/VaultTestHarness.cs` and the tests that construct entries
- Create: `tests/XrplFile.Keys.Tests/AlgorithmTests.cs`

**Interfaces:**
- Produces: `NewVaultEntry` and `VaultEntryInfo` gain `string? Algorithm`; `VaultSigner` derives with it.

**The defect this closes.** `VaultSigner` calls `XrplWallet.FromSeed(entry.Secret)` with no algorithm, and the SDK defaults to ed25519 (`XrplWallet.DEFAULT_ALGORITHM`). A secp256k1 key derived from the same seed is a **different account with a different address**, so the vault cannot hold one: the address check added in B's Task 8 refuses the signer. It fails safely and it fails.

- [ ] **Step 1: Write the failing test**

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Keys.Tests;

public class AlgorithmTests
{
    [Fact]
    public async Task A_secp256k1_key_can_be_stored_and_signed_with()
    {
        // Today this fails: the signer derives ed25519 from the seed, gets a different address,
        // and refuses itself with AddressMismatch.
        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        KeyVault vault = VaultTestHarness.NewVault();
        await vault.CreateAsync(VaultTestHarness.Password("p"), TestContext.Current.CancellationToken);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "imported", "testnet",
                wallet.ClassicAddress, wallet.Seed, XrplWallet.Secp256k1),
            TestContext.Current.CancellationToken);

        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, TestContext.Current.CancellationToken);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);
    }

    [Fact]
    public async Task An_entry_with_no_algorithm_is_ed25519()
    {
        // Everything stored before this field existed was derived that way, so absent must keep
        // meaning what it meant.
        XrplWallet wallet = XrplWallet.Generate();
        KeyVault vault = VaultTestHarness.NewVault();
        await vault.CreateAsync(VaultTestHarness.Password("p"), TestContext.Current.CancellationToken);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "imported", "testnet",
                wallet.ClassicAddress, wallet.Seed, Algorithm: null),
            TestContext.Current.CancellationToken);

        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, TestContext.Current.CancellationToken);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
    }
}
```

- [ ] **Step 2: Run and watch the first fail with `AddressMismatch`.**

- [ ] **Step 3: Implement**

`NewVaultEntry` and `VaultEntryInfo` gain `string? Algorithm` as a trailing optional parameter, so every existing construction site still compiles. `VaultSigner`'s two `XrplWallet.FromSeed(entry.Secret)` calls become `XrplWallet.FromSeed(entry.Secret, masterAddress: null, algorithm: entry.Info.Algorithm)` — the SDK's own parameter is `string? algorithm`, and null means its default. The entry's canonical JSON gains the field; `VaultEntryEdit` does **not**, because the algorithm is part of what the entry *is*, like its kind and address.

The container's mapping (Task 3) reads `algorithm` on import and writes it on export.

- [ ] **Step 4: Run both suites and watch them pass.**

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Keys tests/XrplFile.Keys.Tests
git commit -m "fix(keys): a seed does not say which curve derived it"
```

---

## Task 5: Three projects, a solution filter, and CI

Nothing user-visible. It ends with an application that builds, starts, shows nothing, and is compiled by CI.

**Files:**
- Create: `app/XrplFile.App.Core/XrplFile.App.Core.csproj`, `VaultLocation.cs`
- Create: `app/XrplFile.App/` from the MAUI Blazor template, trimmed to Windows
- Create: `tests/XrplFile.App.Core.Tests/XrplFile.App.Core.Tests.csproj`, `VaultHarness.cs`, `VaultLocationTests.cs`
- Create: `XrplFile.Libraries.slnf`
- Modify: `XrplFile.sln`, `.github/workflows/test.yml`

**Interfaces:**
- Consumes: Task 0's recorded template name and target framework.
- Produces:
  ```csharp
  public static class VaultLocation
  {
      public static string DefaultPath { get; }          // under the application's data directory
      public static string FileName => "vault.json";
  }
  ```
  and `VaultHarness` in the test project: `NewVault(string? path = null)`, `Password(string)`, and `InMemoryDeviceSecretStore`.

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public class VaultLocationTests
{
    [Fact]
    public void The_default_path_is_under_the_applications_data_directory()
    {
        string path = VaultLocation.DefaultPath;

        Assert.EndsWith(VaultLocation.FileName, path, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            path,
            StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(path), path);   // the device secret is keyed by this string
    }
}
```

The last assertion matters: B keys the device secret by the vault's full path, so a path that is not already normalised would key it differently depending on how it was built.

- [ ] **Step 2: Create `XrplFile.App.Core` and its test project**

```xml
<!-- app/XrplFile.App.Core/XrplFile.App.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>XrplFile.App.Core</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\XrplFile.Core\XrplFile.Core.csproj" />
    <ProjectReference Include="..\..\src\XrplFile.Keys\XrplFile.Keys.csproj" />
  </ItemGroup>
</Project>
```

```xml
<!-- tests/XrplFile.App.Core.Tests/XrplFile.App.Core.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <RootNamespace>XrplFile.App.Core.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\app\XrplFile.App.Core\XrplFile.App.Core.csproj" />
  </ItemGroup>
</Project>
```

Write `VaultLocation` and `VaultHarness`. `VaultHarness` mirrors `tests/XrplFile.Keys.Tests/VaultTestHarness.cs` — read that file and follow it rather than inventing a second shape.

- [ ] **Step 3: Run the test and watch it pass.**

- [ ] **Step 4: Create the MAUI project**

Use the template name and target framework Task 0 recorded. Single target framework, Windows only. It references `XrplFile.App.Core`. Strip the template's sample pages down to an empty shell that starts — the screens arrive in Tasks 11 and 12.

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug
```

Then run it and confirm a window appears.

- [ ] **Step 5: Add everything to the solution and write the filter**

```bash
dotnet sln XrplFile.sln add app/XrplFile.App.Core/XrplFile.App.Core.csproj
dotnet sln XrplFile.sln add app/XrplFile.App/XrplFile.App.csproj
dotnet sln XrplFile.sln add tests/XrplFile.App.Core.Tests/XrplFile.App.Core.Tests.csproj
```

`XrplFile.Libraries.slnf` lists every project in the solution **except** `app/XrplFile.App`. Verify:

```bash
dotnet build XrplFile.Libraries.slnf -c Release
```

- [ ] **Step 6: Change CI**

In `.github/workflows/test.yml`, both existing jobs change `dotnet build XrplFile.sln` to `dotnet build XrplFile.Libraries.slnf`. The `unit` job gains a step after the Core tests:

```yaml
      - run: dotnet run --project tests/XrplFile.App.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
```

And a new job:

```yaml
  windows-app:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet workload install maui-windows
      - run: dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
```

It runs no tests. Its job is to prove the markup still compiles against the models.

- [ ] **Step 7: Commit**

```bash
git add app tests/XrplFile.App.Core.Tests XrplFile.sln XrplFile.Libraries.slnf .github/workflows/test.yml
git commit -m "feat(app): a shell that starts, and a build that does not need Windows to test it"
```

---

## Task 6: Creating and unlocking

**Files:**
- Create: `app/XrplFile.App.Core/UnlockModel.cs`, `UnlockMessage.cs`
- Create: `tests/XrplFile.App.Core.Tests/UnlockModelTests.cs`, `UnlockMessageTests.cs`

**Interfaces:**
- Consumes: `VaultHarness` (Task 5).
- Produces:
  ```csharp
  public enum UnlockStage { NoVault, Locked, Unlocked }

  public sealed record UnlockMessage(string Headline, string Detail, bool CountsAsAFailedAttempt);

  public static class UnlockMessages
  {
      public static UnlockMessage For(VaultUnlockResult result);
  }

  public sealed class UnlockModel(IKeyVault vault)
  {
      public UnlockStage Stage { get; }
      public UnlockMessage? LastMessage { get; }
      public bool DeviceUnlockOffered { get; }
      public Task RefreshAsync(CancellationToken cancellationToken);
      public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
      public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
      public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken);
      public Task LockAsync();
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;
using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class UnlockMessageTests
{
    [Fact]
    public void Only_a_wrong_password_counts_against_an_attempt_limit()
    {
        // A failing disk reported as a user's mistake is how healthy data gets destroyed.
        Assert.True(UnlockMessages.For(VaultUnlockResult.WrongPassword).CountsAsAFailedAttempt);
        Assert.False(UnlockMessages.For(VaultUnlockResult.VaultCorrupt).CountsAsAFailedAttempt);
        Assert.False(UnlockMessages.For(VaultUnlockResult.VaultUnavailable).CountsAsAFailedAttempt);
    }

    [Fact]
    public void A_corrupt_vault_is_never_told_the_password_is_fine()
    {
        // WrongPassword and VaultCorrupt cannot be told apart when the record was already damaged,
        // so the message must not claim to know.
        UnlockMessage message = UnlockMessages.For(VaultUnlockResult.VaultCorrupt);

        Assert.DoesNotContain("password is correct", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not the password", message.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(VaultUnlockResult.Success)]
    [InlineData(VaultUnlockResult.WrongPassword)]
    [InlineData(VaultUnlockResult.VaultMissing)]
    [InlineData(VaultUnlockResult.VaultCorrupt)]
    [InlineData(VaultUnlockResult.VaultDegraded)]
    [InlineData(VaultUnlockResult.VaultUnsupported)]
    [InlineData(VaultUnlockResult.VaultUnavailable)]
    public void Every_outcome_has_its_own_words(VaultUnlockResult result)
    {
        UnlockMessage message = UnlockMessages.For(result);

        Assert.False(string.IsNullOrWhiteSpace(message.Headline));
        Assert.False(string.IsNullOrWhiteSpace(message.Detail));
    }

    [Fact]
    public void No_two_outcomes_share_a_headline()
    {
        VaultUnlockResult[] all = Enum.GetValues<VaultUnlockResult>();

        Assert.Equal(all.Length, all.Select(r => UnlockMessages.For(r).Headline).Distinct(StringComparer.Ordinal).Count());
    }
}

public class UnlockModelTests
{
    [Fact]
    public async Task A_fresh_directory_offers_to_create_a_vault()
    {
        UnlockModel model = new(VaultHarness.NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task Creating_leaves_the_vault_open()
    {
        UnlockModel model = new(VaultHarness.NewVault());
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        await model.CreateAsync(VaultHarness.Password("p"), TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }

    [Fact]
    public async Task A_wrong_password_reports_it_and_leaves_the_vault_shut()
    {
        KeyVault vault = VaultHarness.NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(VaultHarness.Password("right"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        VaultUnlockResult result = await model.UnlockAsync(VaultHarness.Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.True(model.LastMessage!.CountsAsAFailedAttempt);
    }

    [Fact]
    public async Task A_degraded_unlock_leaves_the_user_inside()
    {
        // VaultDegraded means the password worked and something inside is damaged.
        KeyVault vault = VaultHarness.NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(VaultHarness.Password("p"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(VaultHarness.Entry("one"), TestContext.Current.CancellationToken);
        await model.LockAsync();
        VaultHarness.FlipOneByteOfEntry(vault, reference);

        VaultUnlockResult result = await model.UnlockAsync(VaultHarness.Password("p"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }
}
```

`VaultHarness` gains `Entry` and `FlipOneByteOfEntry`, ported from `tests/XrplFile.Keys.Tests/VaultTestHarness.cs`.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.** `UnlockMessages.For` is a `switch` over the enum with no default arm, so adding a value to `VaultUnlockResult` breaks the build rather than falling through to a wrong sentence.

- [ ] **Step 4: Run and watch them pass. Commit.**

```bash
git commit -m "feat(app): what a vault says when it will not open"
```

---

## Task 7: The key list

**Files:**
- Create: `app/XrplFile.App.Core/VaultListModel.cs`
- Create: `tests/XrplFile.App.Core.Tests/VaultListModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record KeyRow(VaultEntryInfo Info, bool IsDamaged, bool NeedsExporting);

  public sealed class VaultListModel(IKeyVault vault)
  {
      public IReadOnlyList<KeyRow> Rows { get; }
      public bool IsDegraded { get; }
      public bool WritesAreRefused { get; }
      public Task RefreshAsync(CancellationToken cancellationToken);
      public Task AcknowledgeDamageAsync(CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: Write the failing tests**

Facts to assert, one test each:

- rows come back for the entries in the vault, with label, kind, network and address;
- a `Generated` entry that was never exported has `NeedsExporting` true; an `Imported` one does not, whatever its flag — the warning is about throwaway accounts that exist nowhere else;
- an entry whose ciphertext was damaged has `IsDamaged` true and the others do not;
- **an entry deleted from the file is in no row and in no damaged list** — the vault cannot report a casualty it has no record of, so a caller compares its own reference against `References`;
- while degraded, `WritesAreRefused` is true, and it becomes false after `AcknowledgeDamageAsync`;
- `AcknowledgeDamageAsync` on a vault that is not degraded is harmless.

```csharp
[Fact]
public async Task A_generated_key_that_was_never_exported_is_flagged()
{
    // The flag exists so the application can show which throwaway accounts exist nowhere else.
    KeyVault vault = VaultHarness.NewVault();
    await vault.CreateAsync(VaultHarness.Password("p"), TestContext.Current.CancellationToken);
    await vault.StoreAsync(VaultHarness.Entry("throwaway", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
    await vault.StoreAsync(VaultHarness.Entry("mine", VaultEntryKind.Imported), TestContext.Current.CancellationToken);

    VaultListModel model = new(vault);
    await model.RefreshAsync(TestContext.Current.CancellationToken);

    Assert.True(model.Rows.Single(r => r.Info.Kind == VaultEntryKind.Generated).NeedsExporting);
    Assert.False(model.Rows.Single(r => r.Info.Kind == VaultEntryKind.Imported).NeedsExporting);
}
```

- [ ] **Step 2: Run, implement, run. Commit.**

```bash
git commit -m "feat(app): a list that says which keys exist nowhere else"
```

---

## Task 8: Adding a key

**Files:**
- Create: `app/XrplFile.App.Core/AddKeyModel.cs`
- Create: `tests/XrplFile.App.Core.Tests/AddKeyModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AddKeyDraft(string Label, string Network, string? Seed, string? Algorithm);
  public sealed record AddKeyPreview(string Address, string Algorithm);

  public sealed class AddKeyModel(IKeyVault vault)
  {
      public AddKeyPreview? Preview { get; }
      public string? Problem { get; }
      public bool Generate(string label, string network);
      public bool Import(AddKeyDraft draft);
      public Task<string> StoreAsync(CancellationToken cancellationToken);
  }
  ```

`Generate` and `Import` prepare and validate; `StoreAsync` writes. Nothing reaches the vault until the address has been derived and shown.

- [ ] **Step 1: Write the failing tests**

- a generated key previews an address and stores as `Generated`;
- an imported ed25519 seed previews the address that seed derives;
- **an imported secp256k1 seed with the algorithm given previews that curve's address**, which is a different address from the ed25519 one for the same seed;
- a malformed seed sets `Problem` and stores nothing;
- an empty label or network sets `Problem` and stores nothing;
- storing an address the vault already holds on the same network surfaces the vault's `DuplicateAddress` as a `Problem` rather than throwing out of the model;
- a locked vault surfaces `VaultError.Locked` as a `Problem`.

```csharp
[Fact]
public void The_same_seed_previews_two_different_addresses_on_two_curves()
{
    // This is why an entry has to record its algorithm: a seed alone does not say which account.
    XrplWallet ed = XrplWallet.Generate();
    AddKeyModel model = new(VaultHarness.NewVault());

    Assert.True(model.Import(new AddKeyDraft("a", "testnet", ed.Seed, XrplWallet.Ed25519)));
    string ed25519Address = model.Preview!.Address;

    Assert.True(model.Import(new AddKeyDraft("a", "testnet", ed.Seed, XrplWallet.Secp256k1)));

    Assert.NotEqual(ed25519Address, model.Preview!.Address);
}
```

- [ ] **Step 2: Run, implement, run. Commit.**

```bash
git commit -m "feat(app): a key arrives with its curve, not just its seed"
```

---

## Task 9: Export, reveal, and the QR

**Files:**
- Create: `app/XrplFile.App.Core/ExportModel.cs`, `RevealModel.cs`, `IClipboardService.cs`
- Create: `tests/XrplFile.App.Core.Tests/ExportModelTests.cs`, `RevealModelTests.cs`

**Interfaces:**
- Consumes: `KeyVault.ExportAsync`, `BackupContainer`, `Base45`.
- Produces:
  ```csharp
  public sealed record QrPayload(string Text, int ContainerBytes, bool FitsOneCode);

  public sealed class ExportModel(IKeyVault vault)
  {
      public Task<byte[]> ToContainerAsync(string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken);
      public QrPayload ToQr(byte[] container);
  }

  public sealed class RevealModel(IKeyVault vault, TimeProvider time)
  {
      public string? Shown { get; }
      public Task ShowAsync(string reference, CancellationToken cancellationToken);
      public void Hide();
      public Task CopyAsync(IClipboardService clipboard, CancellationToken cancellationToken);
  }

  public interface IClipboardService
  {
      Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken);
  }
  ```

`IClipboardService.CopySecretAsync` carries the expiry because a secret on a clipboard has to leave it. The implementation in `XrplFile.App` maps it to the platform's options; on Windows neither iOS's `localOnly` nor Android's sensitivity flag exists, and that implementation says so in its documentation rather than pretending otherwise.

- [ ] **Step 1: Write the failing tests**

- exporting produces a container that `BackupContainer.TryUnpack` opens with the same password, and the entry's `Exported` flag is set;
- the QR payload's `Text` decodes back to the container through `Base45.TryDecode`;
- `FitsOneCode` is true for one key and false for a container past 1234 bytes;
- a revealed seed is null before `ShowAsync`, is the entry's secret after it, and is null again after `Hide`;
- **a revealed seed hides itself once the idle window passes** — driven by a `TimeProvider` double, with no real waiting;
- `CopyAsync` passes a non-zero expiry to the clipboard;
- revealing from a locked vault surfaces `VaultError.Locked` rather than returning null silently.

```csharp
[Fact]
public async Task A_shown_seed_hides_itself()
{
    // The Wallet hides after 60 seconds; the value is ours, the discipline is theirs.
    ManualTimeProvider time = new();
    RevealModel model = new(vault, time);
    await model.ShowAsync(reference, TestContext.Current.CancellationToken);
    Assert.NotNull(model.Shown);

    time.Advance(TimeSpan.FromSeconds(61));

    Assert.Null(model.Shown);
}
```

`ManualTimeProvider` is written in the test project, as `tests/XrplFile.Keys.Tests/VaultAutoLockTests.cs` already does — read that one and follow it rather than inventing a second.

- [ ] **Step 2: Run, implement, run. Commit.**

```bash
git commit -m "feat(app): a seed leaves the vault under supervision"
```

---

## Task 10: Password, device unlock, and deletion

**Files:**
- Create: `app/XrplFile.App.Core/PasswordChangeModel.cs`, `DeviceUnlockModel.cs`, `DeleteKeyModel.cs`
- Create: the matching test files

**Interfaces:**
- Produces:
  ```csharp
  public sealed record PasswordChangeReport(bool Changed, IReadOnlyList<string> ThingsToTell, string? SurvivingCopyPath);

  public sealed class PasswordChangeModel(IKeyVault vault, string vaultPath)
  {
      public Task<PasswordChangeReport> ChangeAsync(ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken);
  }

  public sealed class DeviceUnlockModel(IKeyVault vault)
  {
      public bool Enabled { get; }
      public Task<bool> EnableAsync(CancellationToken cancellationToken);
      public Task DisableAsync(CancellationToken cancellationToken);
  }

  public sealed record DeleteWarning(bool NeverExported, string Address);

  public sealed class DeleteKeyModel(IKeyVault vault)
  {
      public Task<DeleteWarning> PrepareAsync(string reference, CancellationToken cancellationToken);
      public Task DeleteAsync(string reference, CancellationToken cancellationToken);
  }
  ```

- [ ] **Step 1: Write the failing tests**

The password model is the sharp one. `VaultPasswordChange` is a flag set, and every flag must reach the user:

```csharp
[Theory]
[InlineData(VaultPasswordChange.Changed, 1)]
[InlineData(VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped, 2)]
[InlineData(VaultPasswordChange.Changed | VaultPasswordChange.TheOldCopyRemains, 2)]
[InlineData(VaultPasswordChange.Changed | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch, 2)]
[InlineData(
    VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped
    | VaultPasswordChange.TheOldCopyRemains | VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch, 4)]
public void Every_flag_that_is_set_is_something_the_user_is_told(VaultPasswordChange outcome, int expected)
{
    // Each of these exists because silence about it misled somebody. A screen that says only
    // "done" puts the silence back.
    PasswordChangeReport report = PasswordChangeModel.Describe(outcome, vaultPath: @"C:\vault\vault.json");

    Assert.True(report.Changed);
    Assert.Equal(expected, report.ThingsToTell.Count);
}

[Fact]
public void A_wrong_old_password_is_not_a_change()
{
    PasswordChangeReport report = PasswordChangeModel.Describe(VaultPasswordChange.WrongPassword, @"C:\vault\vault.json");

    Assert.False(report.Changed);
    Assert.Null(report.SurvivingCopyPath);
}

[Fact]
public void The_surviving_copy_is_named_when_there_is_one()
{
    PasswordChangeReport report = PasswordChangeModel.Describe(
        VaultPasswordChange.Changed | VaultPasswordChange.TheOldCopyRemains, @"C:\vault\vault.json");

    Assert.Equal(@"C:\vault\vault.json.bak", report.SurvivingCopyPath);
}
```

`Describe` is a static so the mapping is testable without a vault. The model's `ChangeAsync` calls the vault and then `Describe`.

For deletion: `PrepareAsync` reports `NeverExported` for an entry whose flag is false, and a `DeleteKeyModel` never deletes without `PrepareAsync` having been called for that reference.

For device unlock: enabling on a vault with no device store available returns false and changes nothing; disabling is safe when it was never on.

- [ ] **Step 2: Run, implement, run. Commit.**

```bash
git commit -m "feat(app): three operations that must not be quiet"
```

---

## Task 11: The shell, the platform, and the way in

The first task with markup. Components bind and call; every decision is already in a model.

**Files:**
- Modify: `app/XrplFile.App/MauiProgram.cs`
- Create: `app/XrplFile.App/Platform/SecureStorageDeviceSecretStore.cs`, `MauiClipboardService.cs`, `MauiFilePickerService.cs`
- Create: `app/XrplFile.App/Components/Unlock.razor`, `CreateVault.razor`, `Shell.razor`
- Create: `tests/XrplFile.App.Core.Tests/` — nothing new; this task adds no decisions

**Interfaces:**
- Consumes: `UnlockModel`, `IClipboardService`, `IFilePickerService`, `VaultLocation`.
- Produces: `SecureStorageDeviceSecretStore : IDeviceSecretStore`, keyed by the vault's full path.

- [ ] **Step 1: The device secret store**

```csharp
/// <summary>
/// The platform's secret store, holding one 32-byte secret per vault.
/// </summary>
/// <remarks>
/// Keyed by the vault file's full path, which is what the vault passes: on Windows this is DPAPI
/// under the hood, on other platforms the Keychain or the Android keystore. A renamed or moved
/// vault therefore looks like a different vault and asks for its password once — the accepted cost
/// of not putting an identifier inside the record.
/// </remarks>
public sealed class SecureStorageDeviceSecretStore : IDeviceSecretStore
```

`GetAsync`/`CreateAsync`/`DeleteAsync` map onto `SecureStorage.GetAsync`/`SetAsync`/`Remove`, with the value base64-encoded because `SecureStorage` stores strings. A platform failure becomes the same exception shape the in-memory double produces, so the vault's handling is exercised by both.

- [ ] **Step 2: Dependency injection**

`MauiProgram` registers `KeyVault` over `VaultLocation.DefaultPath` with `Argon2idKdf` and `SecureStorageDeviceSecretStore`, `VaultAutoLock` around it, and one registration per model. Nothing constructs a vault anywhere else.

- [ ] **Step 3: The unlock and create screens**

`Unlock.razor` and `CreateVault.razor` bind to `UnlockModel` and display `UnlockMessage.Headline` and `.Detail`. No `if` decides what to say — the model already did.

`CreateVault.razor` carries the consequence as body text, not a footnote: there is no recovery phrase, and a forgotten password loses every key that was not exported.

- [ ] **Step 4: Build, run, and look**

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj -c Debug
dotnet run --project app/XrplFile.App/XrplFile.App.csproj -c Debug
```

Create a vault, close the application, reopen it, unlock it. Report what you saw — this is the first task whose deliverable a test cannot check.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(app): a window that opens a vault"
```

---

## Task 12: The remaining screens

**Files:**
- Create: `app/XrplFile.App/Components/KeyList.razor`, `AddKey.razor`, `ExportKey.razor`, `RevealSeed.razor`, `ChangePassword.razor`, `Settings.razor`, `Damage.razor`
- Create: `app/XrplFile.App/wwwroot/qr.js`

**Interfaces:**
- Consumes: every model from Tasks 7 to 10.

- [ ] **Step 1: The screens**

One component per model, binding only. The key list shows label, kind, network, address, and — prominently, for `Generated` rows — that the key exists nowhere else.

- [ ] **Step 2: The QR, and the trap**

A QR code is drawn into a `<canvas>`, **never** an `<img src="data:…">`, and its container carries `-webkit-touch-callout: none`.

```razor
@* A long press on an <img> in a WKWebView offers "Add to Photos", and an unencrypted code goes
   to the gallery and to iCloud around every restriction the model applies. Windows does not have
   this trap; the markup is shared, and the day it runs on iOS must not be the day it is found. *@
<canvas id="qr" style="-webkit-touch-callout: none;"></canvas>
```

`qr.js` renders `QrPayload.Text` at error correction level H and never silently lowers it. When `FitsOneCode` is false the screen says so and offers the file instead; multi-frame QR is not implemented.

- [ ] **Step 3: Damage on screen**

`Damage.razor` names the damaged entries and offers `AcknowledgeDamageAsync` as a button a person presses with the list in front of them. There is no automatic retry anywhere: the record's integrity code is the only witness that an entry was deleted or the file rolled back, and a loop that acknowledges without looking destroys the evidence.

- [ ] **Step 4: Settings**

The vault's path, a button that reveals it in the file manager, opening a different vault, and the device-unlock switch — which says that it is tied to this path.

- [ ] **Step 5: Build, run, and walk every screen**

Create a vault, add a generated key, export it, reveal it, change the password, turn device unlock on and off, delete a key. Report what you saw and anything that behaved differently from its model's tests.

- [ ] **Step 6: Commit**

```bash
git commit -m "feat(app): the screens a vault needs"
```

---

## Task 13: Say what it does

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md`

- [ ] **Step 1: Read what shipped**

Read the models and the components as they are, not as this plan predicted. Every sentence must be checked against the source.

- [ ] **Step 2: The README**

A section on the application: that it is Windows-first, how to build and run it, that keys live in a vault encrypted under a master password, that the file is portable and a backup is a copy of it, that a forgotten password loses everything not exported, and that a container written here opens in the Wallet application and the other way round.

Also correct the container's description wherever the README describes `XFK1` — that format is gone.

- [ ] **Step 3: Reconcile the spec**

Walk its sections against the code and correct what shipped differently. Where a limitation turned out otherwise, correct section 10. A spec that disagrees with the code is worse than no spec, because it is believed.

- [ ] **Step 4: Run every suite, then commit**

```bash
git commit -m "docs: the application, and the container it now shares"
```

---

## Self-review

**Spec coverage.** E1 Task 5; E2 Tasks 11 and 12 (and the constraint block); E3 Task 5 step 6; E4 nothing contacts a node in any task — correct, and visible in the absence of `ILedgerSource` from every model's constructor; E5 Tasks 5 and 12; E6 and E7 Task 3; E8 Task 4; E9 no test project for components exists. Spec section 5.1 Tasks 1 to 3; 5.3 Task 3 step 3; 5.4 Task 4; 5.5 Task 3's password-less tests; 5.6 Task 3's cap and outcome tests. Section 6 Tasks 6 to 12. Section 7 Task 6's `UnlockMessages`. Section 8 Task 9 and Task 12 step 2. Section 9 Task 0. Section 11 distributed.

**Gap found and closed.** Spec section 8 requires the clipboard's platform options, and nothing asserted the expiry reaches the implementation. Task 9's test list now includes `CopyAsync` passing a non-zero expiry.

**Second gap found and closed.** Spec section 5.1 says a `Pbkdf2Kdf` is added implementing nothing in particular; the plan's Task 1 now states explicitly that it does **not** implement `IPasswordKdf` and why, because `KdfParameters` validates memory and parallelism that PBKDF2 has no use for. Task 13 carries the spec correction.

**Type consistency.** `VaultHarness` is the test helper in `XrplFile.App.Core.Tests` (Task 5) and `VaultTestHarness` remains the one in `XrplFile.Keys.Tests`; the two are deliberately different names for different projects, and Task 5 says to follow the existing one rather than share it across a project boundary. `NewVaultEntry` gains `Algorithm` as a trailing optional parameter in Task 4 and is used with it in Task 8. `BackupContainer.TryUnpack` takes `fromFile` in Task 3 and is called with it in Task 9's tests. `UnlockMessage.CountsAsAFailedAttempt` is named the same in Task 6's model and its tests.
