# The application shell and the vault screens — design

Part C1 of the desktop application. Parts A and B built a library that can publish a file into the
XRP Ledger and keep the keys that sign it. Nobody can use either without an application, and this
is the first piece of one: the shell it all hangs from, and every screen the vault needs.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault | shipped |
| **C1** | **the shell and the vault screens — this document** | **designed** |
| C2 | publishing: a file, a key, an estimate, a run | not started |
| C3 | the throwaway account's life, including reclaiming its reserve | not started |
| C4 | reading a link back into a file | not started |

C1 spends no money and talks to no node. That is deliberate: it is the only part of the
application where a mistake costs nothing, which makes it the right place to stand up the shell,
the packaging and the test story before anything can go wrong expensively.

## 1. Scope

**In.** The MAUI Blazor Hybrid application, Windows first. Creating a vault, unlocking it by
password or by device, locking it, and the idle lock. Listing keys. Adding a key by import, by
generation, or from an opened backup container. Exporting a key to an encrypted container and
revealing a seed on screen. Changing the master password and turning device unlock on and off.
Deleting a key. Reporting damage. Settings: where the vault file is.

**Shipped narrower than planned.** Opening a different vault file was in scope above and is not
in this build: see the correction to E5 and to section 6's "Settings" below.

Also in, because the screens cannot be honest without it: replacing the export container with the
Wallet project's format (section 5), and giving an entry the algorithm its key was derived with
(section 5.4).

**Out.** Anything that reaches a node — C2 and C3. Reading a link — C4. Any platform other than
Windows: the code must not be gratuitously Windows-only, but nothing else is built or tested here.

## 2. Decisions

| № | Decision | Why |
|---|---|---|
| E1 | Three projects: `XrplFile.App.Core` (no MAUI), `XrplFile.App` (MAUI), and tests for the first | The same reasoning as B's D12. Logic that can only run on Windows can only be tested on Windows, which in this repository means not tested by CI at all |
| E2 | Razor components hold no decisions | A component with an `if` in it is a decision nothing can test. It moves to the model |
| E3 | CI builds a solution filter on Linux and the app on Windows | A `net10.0-windows` project cannot build on `ubuntu-latest`, and the current job builds the whole solution. The filter keeps the libraries and the app models tested cheaply on every push; a second job compiles the UI so it cannot rot unnoticed |
| E4 | C1 contacts no node | Addresses derive from a seed locally. A part that needs no network needs no connection settings, no reachability handling and no second moving piece while the shell is being stood up |
| E5 | The vault lives in the application's data directory, at a fixed path, and that path is shown | A backup is a copy of the file, so the file must be findable |
| E6 | The export container becomes the Wallet project's format, byte for byte | Owner's decision. A key that cannot move between the two products is a key locked in whichever one made it, and inventing a third format serves nobody |
| E7 | The container is therefore PBKDF2 (`kdfId = 1`), not Argon2id | The Wallet's header carries an iteration count and nothing else; Argon2id's three parameters do not fit. The vault file keeps Argon2id — see 5.2 |
| E8 | An entry records the algorithm its key uses | Not for correct derivation: an XRPL family seed carries its own curve in its prefix, and `XrplKeypairs.DeriveKeypair` (`Base/Xrpl.Keypairs/XrplKeypairs.cs`) reads it from there, ignoring the `algorithm` argument entirely — the vault already holds a secp256k1 key correctly with no field at all. It exists for interoperability and display: the shared container format carries an algorithm per key, a Wallet backup arrives with one, and dropping it on the way through would lose what that backup said. Absent still means ed25519 for anything already stored. An earlier revision of this decision claimed the vault could not hold a secp256k1 key; that was wrong — see 5.4 |
| E9 | No component-test framework | Components are thin by E2, and testing a binding tests Blazor. The obligation E2 creates is what replaces it |

**Correction to E5.** An earlier revision of this row said "another file can be opened." That did
not ship: `IKeyVault` is wired as a process-wide singleton over `VaultLocation.DefaultPath` in
`MauiProgram.RegisterVault` (3.1), and nothing built for this task can repoint it at a different
file without restarting the process. `Settings.razor` says so plainly rather than offering a
control that looks like it works and does not — see the correction to section 6 below. The intent
behind the original reasoning is not withdrawn, only deferred: "opening another is how it arrives
on a new machine" is still the eventual point of a findable, portable file, and section 10 now
carries this as a known limitation.

## 3. Projects

```
app/XrplFile.App.Core          net10.0                      models, no MAUI
app/XrplFile.App               net10.0-windows10.0.19041.0  MAUI Blazor Hybrid, Razor, platform
tests/XrplFile.App.Core.Tests  net10.0                      xunit.v3, as the other test projects
```

`XrplFile.App.Core` references `XrplFile.Core` and `XrplFile.Keys` and nothing else. It holds one
model per screen: what the screen can do, what it refuses, and what it says. Every branch in the
application lives here, and every one of them is reachable from a test with no operating system in
the loop.

`XrplFile.App` holds the Razor components, the MAUI host, and the two places the platform is
unavoidable:

- `SecureStorageDeviceSecretStore`, implementing `IDeviceSecretStore` over MAUI `SecureStorage`.
  The interface takes a vault identity, which B keys by the vault file's full path.
- The clipboard and file-picker services described in section 8, which are interfaces in
  `App.Core` and implementations here.

### 3.1 What a model looks like

A model is a plain class with a constructor, state, and methods. It does not know Razor exists.
`VaultListModel` holds the entries and whether the vault is degraded; `AddKeyModel` validates a
seed and a label before it will let anything be stored; `PasswordChangeModel` (named
`ChangePasswordModel` in an earlier revision of this document; the class as built is
`PasswordChangeModel`) turns a `VaultPasswordChange` into the set of sentences a person needs to
read.

The test for each is the same shape as B's: a real `KeyVault` over a temporary directory, an
in-memory device store, and small `KdfParameters` so the suite stays fast.

## 4. CI

`XrplFile.sln` gains both new projects. A new `XrplFile.Libraries.slnf` lists everything except
`XrplFile.App`, and the existing Linux job builds and tests that filter instead of the solution —
so `XrplFile.App.Core.Tests` runs on every push at no extra cost, because it is an ordinary
`net10.0` project.

A second job on `windows-latest` installs the `maui-windows` workload and builds `XrplFile.App`.
It runs no tests: there are none to run there, and its job is to prove the markup still compiles
against the models.

The expensive runner therefore pays only for compiling the user interface.

## 5. The export container becomes the Wallet's

Part B shipped `ExportContainer` with a magic of `XFK1`, an Argon2id header, and its own payload
schema. It is replaced. No user has ever run this software, so no container of the old shape
exists anywhere, and changing it now costs nothing — the same argument that settled the vault
record's wire shape during B's implementation.

### 5.1 The format

Taken from `SB.WalletApp.Security.Backup.BackupContainer` in the Wallet project
(`docs/superpowers/specs/2026-09-06-wallet-secret-vault-design.md` section 8.4 there):

```
"SBW1" (4) | version (1) | kdfId (1) | iterations (4, big-endian) | salt (16)
           | nonce (12) | ciphertext (N) | tag (16)
```

The header through `salt` is the associated data. The plaintext is deflate-compressed before
encryption. A container declaring `kdfId = 0` has no iterations, no salt, no nonce and no tag: it
is the Wallet's password-less on-screen QR, and section 5.5 says what we do about it.

`Base45.cs` and `BackupContainer.cs` are lifted into `XrplFile.Keys` with their provenance in the
file header. `Base45.cs` has no `using` directives at all; `BackupContainer.cs` needs the base
class library, five size constants, and `IPasswordKdf`, which B already defines.

B defines the interface but implements only Argon2id, so a `Pbkdf2Kdf` reporting `KdfId` 1 is
added beside it — the number B reserved for exactly this. It must be byte-compatible with the
Wallet's, which derives through `Rfc2898DeriveBytes.Pbkdf2` with SHA-256, and is pinned against
RFC 6070's vectors so a library change cannot move it.

The Wallet's assembly is not referenced. It carries MAUI and wallet-domain types, and coupling two
applications through a security library makes both harder to change: what is adopted here is a
format and two self-contained files, not a dependency.

### 5.2 What this costs

The container stops being Argon2id. The Wallet's header has room for an iteration count and
nothing else, and Argon2id needs memory, iterations and parallelism.

The vault file is untouched and stays Argon2id at the measured 256 MiB / 3 / 4. That decision was
about the master password being the only thing between a stolen vault and every key in it, and
nothing here changes it. The container is a different artifact: it is the one that travels, it
carries a password the user chose for it alone, and it follows the format that lets a key actually
arrive somewhere.

The iteration count lives in the header, so each side writes its own measured value and the other
honours it. The Wallet bounds the range at 600,000 to 1,000,000; a count outside that is refused
before the derivation runs, so a container written on a fast machine cannot hang an import on a
slow one.

**Comparing the two approaches (measured on the same machine):**

| | Derivation | Measured |
|---|---|---|
| The vault file | Argon2id, 256 MiB, 3 passes, 4 lanes | ~510 ms |
| The export container | PBKDF2-HMAC-SHA256, 1,000,000 iterations | ~122–126 ms |

This is four times slower wall-clock time, but the smaller half of the story. Argon2id is
memory-hard by design — it resists parallel attack with graphics hardware because memory bandwidth
is the bottleneck. PBKDF2 is memory-independent and therefore trivial to accelerate on a GPU.
An attacker with specialized hardware gains far more against PBKDF2 than the four-fold time
difference alone suggests. The iteration count cannot simply be raised: 1,000,000 is the top of the
range the Wallet's header format accepts, so the container is bounded by format compatibility
rather than by what this machine could afford. See section 9 for the full measurements.

### 5.3 The payload

The Wallet's `BackupWalletEntry`, with our fields added as further optional properties:

| Their field | Ours |
|---|---|
| `keyValue` | `Secret` |
| `classicAddress` | `Address` |
| `description` | `Label` |
| `algorithm` | the entry's algorithm (5.4) |
| `mnemonicPassphrase`, `derivationPath`, `masterAddress`, `folder`, `destinationTag` | not ours; written as null, and therefore not written at all |
| `keyType` | not ours, and not nullable in their record — written as the value their own seed-derived wallets carry |
| `network`, `kind`, `lastKnownSequence`, `deletableFromLedger` | added by us, all optional |

This is how their format already evolves rather than a liberty we are taking: `folder` and
`destinationTag` were added exactly this way, and their own documentation says a container written
before a field existed simply deserialises with null and the version byte does not move. Verified
before adopting it: nothing in that project sets `JsonUnmappedMemberHandling.Disallow`, and
`System.Text.Json` ignores unknown members by default, so our extra properties are invisible to
their reader. `DefaultIgnoreCondition = WhenWritingNull` means an unused field is not written at
all and costs nothing against the QR budget.

A key imported from the Wallet arrives with no `network`. The import screen asks; it does not
guess, because a testnet key treated as a mainnet one is an address the user does not own.

### 5.4 An entry gains its algorithm

**Correction.** An earlier revision of this section said `VaultSigner` derives with no algorithm,
that the SDK therefore defaults to ed25519, and that a secp256k1 key derived from the same seed is
a different account the vault could not hold. That was wrong, and is corrected here rather than
silently rewritten, because a design document that gets this kind of claim wrong and is not seen
to be wrong stops being trustworthy about the claims next to it — the ones about money and secrets.

An XRPL family seed carries its own curve in its prefix: `XrplCodec` gives a secp256k1 seed the
version byte `33` and an ed25519 seed the version bytes `[0x01, 0xE1, 0x4B]` (the `sEd...` prefix).
`XrplKeypairs.DeriveKeypair(string seed, string? algorithm, …)` (`Base/Xrpl.Keypairs/XrplKeypairs.cs`
in the SDK) decodes that prefix and branches on the seed's own type — it never reads the
`algorithm` parameter at all. `XrplWallet.FromSeed(entry.Secret)` with no algorithm therefore
already derives a secp256k1 key correctly from a secp256k1 seed, at the right address; the vault
could hold one before this task, and the address check added during B's Task 8 was never the thing
standing in the way.

So the field is not what makes derivation correct — the seed already carries that. It exists for
**interoperability and display**: the shared container format (5.3) carries an algorithm per key,
a Wallet backup arrives with one recorded, and dropping it on the way through the vault would lose
what that backup said, even though re-deriving without it would still land on the right address.

`NewVaultEntry`, `VaultEntryInfo` and `VaultEntry` gain an algorithm, `VaultSigner` passes it to
`FromSeed` — a no-op against today's SDK, kept so the call states what it means rather than relying
on a default that happens to be harmless — and the container's `algorithm` field is read on import
and written on export. Existing entries have none; absent still means ed25519, which is what they
were derived as, and costs nothing to keep meaning.

### 5.5 A container with no password

`kdfId = 0` is part of the format, so our reader must understand it. It is also a seed in the
clear, and the Wallet only ever produces one for a QR code shown on a screen for another device's
camera — never a file, never the gallery, never a share sheet.

We do not write one. C1 has no device-to-device transfer; a container here is a backup, and a
backup with no password is not one. Our reader accepts `kdfId = 0` only from a source that is not
a file, and refuses it otherwise with its own outcome — the Wallet's
`PasswordlessContainerNotAllowed`, kept because the refusal is about where the container came from
and the user is owed that difference.

### 5.6 What else comes with it

**A bound on inflation.** B's now-removed `ExportContainer.Decompress` copied the deflate stream
out with no limit. A container that decrypts — which needs only that the attacker chose the
password and told it to the user — can expand without bound and take the application down. The
Wallet caps it at `AssumedMaxWallets × AssumedMaxEntrySize` plus an envelope allowance, and reports
the overrun as "not our container" rather than as a wrong password, because the password was
right. `BackupContainer` (5.1) carries this bound forward as `MaxInflatedSize`.

**Four outcomes instead of one.** B's container reported everything as `Corrupt`, so "this is not
a container" and "the password is wrong" were indistinguishable and the user hunted for a typo
that was never the problem. The Wallet's distinguishes: not our container, wrong password,
password-less from a source that may not carry one, iterations out of range —
`BackupContainer.TryUnpack`'s four `BackupUnpackError` values.

One of their choices is subtler and is kept deliberately: **a tampered header byte is not told
apart from a wrong password.** The whole header is associated data, so any edit fails the tag the
same way; and the only thing a user can reason about is the password.

## 6. Screens

**Navigation.** One window. Locked, there is the unlock screen and nothing else. Unlocked, there
is the key list and settings. No placeholder entries for C2 to C4 — the menu grows when they do.

**First run.** No vault, so one is created. This is the one screen that must state the consequence
plainly: there is no recovery phrase, and a forgotten password loses every key that was not
exported. Not in small print under the field — it is the condition on which someone agrees to use
this.

**The key list.** Label, kind, network, address. For `Generated` entries, whether they have ever
been exported, prominently. That flag exists so the application can show which throwaway accounts
exist nowhere else; a list that hides it wastes both the flag and the decision it came from.

**Adding a key.** Three routes, on their own tabs: generate one, import a typed seed, or open a
backup container — the format shared with the Wallet application (section 5) — and choose one or
more keys out of it. All three need a label and a network; none of them ask for an algorithm,
because an XRPL seed carries its own curve in its prefix and every route decodes it from the seed
itself rather than asking (5.4's correction — an earlier revision of this paragraph said import
needed the algorithm when the address did not match ed25519 derivation, and that stopped being
true along with the claim in 5.4 that it corrects). The container route is not in the original
scope: it exists because a container that only this application can write and never read back is
half of interoperability, not all of it. Immediately after generating, the screen offers to
export — not a menu item elsewhere. Part A requires a throwaway's key to be in the vault before
its address is shown, and B makes export the only way back; the moment a key is born is the one
moment the user is certainly looking.

**Export and reveal** are two separate acts. Export writes a container with its own password
through `KeyVault.ExportAsync`, which also sets the flag. Reveal shows the seed on screen, under
section 8's rules.

**Changing the password.** `VaultPasswordChange` is a flag set, and the screen reads all of it:
the password changed; device unlock was dropped with it; the old copy of the file survived and
here is its path; the record was already damaged before the change. Each of those exists because
silence about it misled somebody, and a screen that says only "done" puts the silence back.

**Deleting a key.** Confirmation, and a distinct warning when the entry was never exported. This
is not removing a row; it is losing access to an account permanently.

**Damage.** `VaultDegraded` means the user is in and something inside is broken. The screen names
the damaged entries, the intact ones keep working, and writes are refused until
`AcknowledgeDamageAsync`. That acknowledgement is a human act with the damage on screen, never an
automatic "caught `Degraded`, acknowledged, retried" — the record's integrity code is the only
witness that an entry was deleted or the file rolled back, and a loop that acknowledges without
looking destroys the evidence exactly as the code did before it was fixed.

**Settings.** The vault file's path with a button to show it in the file manager, because a backup
is a copy and a copy must be findable. And the device-unlock switch, which says plainly that it is
tied to this path: rename the file and the password is needed once.

**Correction.** An earlier revision of this paragraph also listed "opening a different vault" as a
Settings capability. It is not one: `Settings.razor` names the vault's path and, under its own
heading, says plainly that opening one kept elsewhere is not available in this build and that the
application always opens the vault at the path shown — see the correction to E5 and section 10.

## 7. Failures on screen

`VaultUnlockResult`'s seven values map to seven messages. Three must never be conflated:

| Value | What the user is told |
|---|---|
| `WrongPassword` | The password is wrong. Only this may count against any attempt limit a screen keeps |
| `VaultCorrupt` | The vault could not be opened; the password may or may not be the problem. Never phrased as "your password is fine" |
| `VaultUnavailable` | Storage refused. Transient; nothing can be concluded |
| `VaultMissing` | There is nothing to open |
| `VaultUnsupported` | This build cannot read that vault's format |
| `VaultDegraded` | You are in; these entries are damaged |
| `Success` | — |

The library separated these because a failing disk reported as a user's mistake is how healthy
data gets destroyed. A screen that collapses them back undoes that.

## 8. Secrets on screen

Adopted from the Wallet's sections 8.3 and 8.5, because these are measured platform behaviours
rather than preferences.

**Revealing a seed.** It comes from `RevealAsync` with a reason, is hidden by default, appears on
a deliberate press, and hides itself after 60 seconds.

**Correction.** "Cleared immediately when the vault locks" is what this paragraph originally said
and is not what shipped. `RevealModel.Shown` is computed from `IKeyVault.IsUnlocked` on every
read rather than pushed to by a lock notification — `IKeyVault` raises none — so it does become
`null` on any read after the vault locks, from its own sixty-second timer or from anything else
that calls `LockAsync`, with no separate wiring needed. What that does not give is instant
clearing on screen: `RevealSeed.razor` learns of a lock (or the sixty-second window elapsing) only
by polling `Model.Shown` once a second and re-rendering, a gap its own file comment states
outright as "a gap against design section 8's wording, not a match for it." So the true bound is
about a second, not the instant, and it is a bound on the model's own field, not a guarantee about
the screen: nothing here reaches into the UI to blank a value already painted — a label, a copied
clipboard entry, a screenshot taken in that window — the screen only stops rendering the secret
once it next reads `Shown`. See section 10.

**The clipboard.** Copying a secret needs platform options, or it leaves the device before any
timer in the application can fire:

- iOS: `UIPasteboard.setItems(_:options:)` with `localOnly` and an expiry. Without `localOnly`,
  Universal Clipboard copies the content to the user's other devices at once, and no application
  timer undoes that — the process may even be suspended by then.
- Android 13 and later: `ClipDescription.EXTRA_IS_SENSITIVE`, or the secret appears in the system
  clipboard preview and in the cross-device clipboard.

`IClipboardService` is declared in `App.Core` with those options as parameters and implemented in
`XrplFile.App`. On Windows today neither applies, and the Windows implementation says so rather
than pretending the options were honoured.

**QR codes.** A container is encoded with Base45 (RFC 9285) before it becomes a QR code. The reason
is established from the Wallet's own scanner code rather than assumed: the scanners on both mobile
platforms hand back a string, so bytes with the high bit set do not survive the trip and the tag
fails — the user is told the password is wrong when it was right. Base45's alphabet fits QR's
alphanumeric mode, which costs about 3% against byte mode and keeps the saving over base64.

Capacity, measured by the Wallet against its own encoder: version 40 at correction level H holds
1852 alphanumeric characters, which is 1234 container bytes. One key always fits. The error
correction level is never quietly lowered, and multi-frame QR is not implemented.

**The rendering trap.** A QR code is drawn into a `<canvas>`, never an `<img src="data:…">`, and
its container carries `-webkit-touch-callout: none`. Otherwise a long press in a WKWebView offers
"Add to Photos" and an unencrypted code goes to the gallery and to iCloud Photos, around every
restriction the service layer applies. Windows does not have this trap; the rule is kept because
the markup is shared and the day it runs on iOS must not be the day it is discovered.

## 9. What was measured

1. **The container's iteration count for a desktop machine.** Measured with
   `tools/XrplFile.KeyBench` (`TimePbkdf2`, `dotnet run -c Release`), PBKDF2-HMAC-SHA256 over the
   Wallet's 600,000–1,000,000 range, two runs to rule out a fluke:

   | Iterations | Run 1 | Run 2 |
   |---|---|---|
   | 600,000 | 79 ms | 73 ms |
   | 700,000 | 89 ms | 86 ms |
   | 800,000 | 113 ms | 102 ms |
   | 900,000 | 109 ms | 109 ms |
   | 1,000,000 | 122 ms | 126 ms |

   Both runs are monotonic within noise and every point sits far under the 500 ms budget — even
   the floor of the range costs nowhere near a slow import. `Pbkdf2Kdf` is written at
   **1,000,000 iterations** (the largest value in the Wallet's range), measured at **122–126 ms**.
   For a comparison of this cost against the vault's Argon2id derivation (measured at ~510 ms on
   the same machine), see section 5.2.
2. **That a MAUI Blazor Hybrid application on `net10.0-windows` builds and starts in this
   repository**, with `XrplFile.Keys` referenced, before any screen is written. Confirmed outside
   the repository with `dotnet new maui-blazor -n ShellProbe -f net10.0`
   (`maui-windows` 10.0.20, SDK 10.0.303): trimming `<TargetFrameworks>` to only
   `net10.0-windows10.0.19041.0`, adding a `ProjectReference` to `XrplFile.Keys.csproj`, and
   constructing a `KeyVault` from `MauiProgram.CreateMauiApp` were the only changes needed —
   `dotnet build -c Debug` and `dotnet run -c Debug` produced a window (`MainWindowTitle`
   `ShellProbe`, responding) with no other adjustment to the template, no package-version
   conflict with the repository's central package management, and no warning from
   `TreatWarningsAsErrors`. Task 5 can start the real shell from this template and target string
   as recorded.

## 10. Known limitations

- **A password-less container is unauthenticated.** Nothing about it is signed, so anyone can
  write one. We never produce one, and we refuse one that arrives as a file — but the format
  admits it, and that is inherited rather than chosen.
- **The QR path is written but not exercised on a device.** Windows is the only target here, and
  the trap in section 8 is a mobile one. The rule is recorded; the proof waits for a mobile target.
- **Device unlock is tied to the vault file's path.** Moving or renaming the file asks for the
  password once. Inherited from B, where the alternative was a record-format change.
- **Existing vault entries have no algorithm.** Absent means ed25519, which is what they were
  derived as, so nothing already stored changes meaning.
- **Opening a different vault file is not available.** `IKeyVault` is wired as a process-wide
  singleton over the fixed default path (`MauiProgram.RegisterVault`, 3.1), and nothing built for
  this task can repoint it without restarting the process. This was in scope (section 1) and in an
  earlier revision of E5 and of section 6's "Settings" paragraph; `Settings.razor` says plainly
  that it is not available rather than offering a control that looks like it works. Moving a vault
  onto a new machine today means placing the file at that machine's own default path before the
  application first runs there, not opening it from within the application.
- **A revealed seed clears from the model within about a second of a lock, not instantly, and the
  screen does not erase what it already painted.** `RevealModel.Shown` reads `IKeyVault.IsUnlocked`
  on every access, so no read after a lock ever returns the secret, but `RevealSeed.razor` only
  learns to stop rendering it by polling once a second — an earlier revision of section 8 said the
  seed is "cleared immediately when the vault locks," which overstated the screen's own guarantee.
  Nothing here scrubs a value already on screen, copied to the clipboard, or captured in a
  screenshot during that window.

## 11. Testing

| What | How |
|---|---|
| Every model | Against a real `KeyVault` over a temporary directory, with an in-memory device store and small `KdfParameters` |
| The container's format | A fixed vector, byte for byte, plus a golden container generated once by the Wallet's own implementation, checked in, and opened by ours — the only test that can prove compatibility rather than assert it |
| Inflation bound | A payload that expands past the cap is refused as "not our container", not as a wrong password |
| The four unpack outcomes | One test each, including a password-less container arriving as a file |
| Base45 | Against RFC 9285's own vectors, and a round trip at the 1234-byte boundary |
| An algorithm that is not ed25519 | A secp256k1 key stored, listed, exported, imported and signed with |
| Unlock outcomes | Each of the seven mapped to its own message, with the three that must not be conflated asserted separately |
| The application itself | Built by the Windows CI job; started by hand on Windows at the end of each task that changes a screen |

Components have no tests, by E9. The obligation that replaces them is E2: a component with a
decision in it is a defect, and the decision moves to a model that is tested.
