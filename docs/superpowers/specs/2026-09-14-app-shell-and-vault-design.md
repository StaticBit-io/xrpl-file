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
password or by device, locking it, and the idle lock. Listing keys. Adding a key by import or by
generation. Exporting a key to an encrypted container and revealing a seed on screen. Changing
the master password and turning device unlock on and off. Deleting a key. Reporting damage.
Settings: where the vault file is, and opening a different one.

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
| E5 | The vault lives in the application's data directory by default, its path is shown, and another file can be opened | A backup is a copy of the file, so the file must be findable. Opening another is how it arrives on a new machine |
| E6 | The export container becomes the Wallet project's format, byte for byte | Owner's decision. A key that cannot move between the two products is a key locked in whichever one made it, and inventing a third format serves nobody |
| E7 | The container is therefore PBKDF2 (`kdfId = 1`), not Argon2id | The Wallet's header carries an iteration count and nothing else; Argon2id's three parameters do not fit. The vault file keeps Argon2id — see 5.2 |
| E8 | An entry records the algorithm its key uses | `VaultSigner` derives with `XrplWallet.FromSeed(secret)` and no algorithm, which is ed25519 by default. A secp256k1 key is a different account from the same seed, so today the vault cannot hold one |
| E9 | No component-test framework | Components are thin by E2, and testing a binding tests Blazor. The obligation E2 creates is what replaces it |

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
seed and a label before it will let anything be stored; `ChangePasswordModel` turns a
`VaultPasswordChange` into the set of sentences a person needs to read.

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
RFC 6070's vectors so a library change cannot move it. Their assembly is
not referenced — it carries MAUI and wallet-domain types, and coupling two applications through a
security library makes both harder to change.

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

`VaultSigner` calls `XrplWallet.FromSeed(entry.Secret)` with no algorithm, and the SDK defaults to
ed25519. A secp256k1 key derived from the same seed is a different account with a different
address.

So today the vault cannot hold a secp256k1 key. It fails safely rather than silently — the
address check added during B's Task 8 refuses a signer whose derived address does not match the
entry's — but it refuses rather than working.

`NewVaultEntry`, `VaultEntryInfo` and `VaultEntry` gain an algorithm, `VaultSigner` passes it to
`FromSeed`, and the container's `algorithm` field is read on import and written on export. Existing
entries have none; absent means ed25519, which is what they were derived as.

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

**A bound on inflation.** Our `ExportContainer.Decompress` copies the deflate stream out with no
limit. A container that decrypts — which needs only that the attacker chose the password and told
it to the user — can expand without bound and take the application down. The Wallet caps it at
`AssumedMaxWallets × AssumedMaxEntrySize` plus an envelope allowance, and reports the overrun as
"not our container" rather than as a wrong password, because the password was right.

**Four outcomes instead of one.** Ours reports everything as `Corrupt`, so "this is not a
container" and "the password is wrong" are indistinguishable and the user hunts for a typo that
was never the problem. Theirs distinguishes: not our container, wrong password, password-less from
a source that may not carry one, iterations out of range.

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

**Adding a key.** Import a seed or generate one; both need a label and a network, and import needs
the algorithm when the address does not match ed25519 derivation. Immediately after generating,
the screen offers to export — not a menu item elsewhere. Part A requires a throwaway's key to be
in the vault before its address is shown, and B makes export the only way back; the moment a key
is born is the one moment the user is certainly looking.

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
is a copy and a copy must be findable. Opening a different vault. And the device-unlock switch,
which says plainly that it is tied to this path: rename the file and the password is needed once.

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
a deliberate press, hides itself after 60 seconds, and is cleared immediately when the vault locks.

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

## 9. What must be measured

1. **The container's iteration count for a desktop machine**, within the Wallet's 600,000 to
   1,000,000 range, on the same basis as its own calibration: the slowest import a person will
   accept once.
2. **That a MAUI Blazor Hybrid application on `net10.0-windows` builds and starts in this
   repository**, with `XrplFile.Keys` referenced, before any screen is written. The workload is
   installed here (`maui-windows` 10.0.20, SDK 10.0.303), but a project that will not start is
   worth discovering on the first task rather than the fifth.

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
