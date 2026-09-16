# The key vault — design

Part B of the three that lead to a desktop application. Part A put a file on the ledger and
spent money doing it; this part decides where the keys that signed it live, and hands part C a
surface it can put behind a screen.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault — this document | shipped |
| C1 | the application shell and the vault screens | shipped |
| C2 | publishing: a file, a key, an estimate, a run | shipped |
| C3 | the throwaway account's life | shipped |
| C4 | reading a link | shipped |

## 1. The task

A user publishes from more than one account: a throwaway born for a single file, another
throwaway a month later, their own account when the file is small enough not to care. All of
those keys have to be somewhere, and today they are nowhere — part A takes an `XrplWallet` in a
constructor and asks no questions about where it came from.

Part A already left the hole in the right shape. `PublicationState` records a `KeyReference`
string and deliberately never a seed, with the note that "storing keys is part B's job. That
keeps the whole secret surface in one place." This document fills that reference in.

Two requirements come from part A rather than from this one:

- A throwaway's key must be **in the vault before its address is shown**. An interruption
  between funding and storage loses money, because the money arrives at an account whose key
  nobody has.
- A publication runs for tens of minutes and signs continuously. Whatever holds the key has to
  survive that without either leaking the secret for the whole run or stalling the run.

## 2. Provenance

This design is a port, not an invention. The `SB.WalletApp.Security` vault in the Wallet project
(`docs/superpowers/specs/2026-09-06-wallet-secret-vault-design.md` in that repository) solved the
same problem for a phone wallet, was reviewed three times, and shipped. Its key schedule, its
data-key indirection, its unlock-result taxonomy, its asynchronous lock and its integrity code
are taken here with their reasoning intact, and the places where this vault departs from it are
listed in 3.1 with the reason for each departure.

Its assembly is not referenced. It is a different product, it carries MAUI and wallet-domain
types, and coupling two applications through a security library makes both harder to change. The
*format* is kept deliberately close, so that convergence later remains possible.

## 3. Decisions

| № | Decision | Why |
|---|---|---|
| D1 | One factor: the master password. The vault file is portable by copying | Owner's decision. A backup is a copy of a file, and a machine that dies does not take the keys with it |
| D2 | Argon2id, `kdfId = 2`, via `Konscious.Security.Cryptography.Argon2` (MIT) | D1 makes the KDF the only thing between a stolen file and the keys. PBKDF2 is not memory-hard and yields to GPUs orders of magnitude faster |
| D3 | Secrets are encrypted under a data key; the password wraps the data key, not the secrets | A password change rewraps 32 bytes. Rewriting every secret would destroy the ones a failure halfway through never reached |
| D4 | "Remember on this device" is a second wrap of the same data key under a key held by the platform | Convenience without a second factor. It does not travel: a copied file's device wrap is inert, and the password still opens it |
| D5 | No password hash, anywhere | A correct password unwraps the data key and a wrong one fails an authentication tag. There is no verifier for an offline attack to test against cheaply |
| D6 | Two entry kinds: `Generated` and `Imported` | The application must tell a throwaway it may offer to delete from an account the user actually owns. A discriminator now leaves room for an external-signer kind later |
| D7 | Metadata lives inside the encrypted entry | D1 again: a copied file has to be self-sufficient. Metadata left outside would stay on the old machine, and it would leak which accounts the user controls |
| D8 | Signing goes through `ITransactionSigner`; part A is refactored onto it | The key stays behind the vault's boundary, so locking it actually takes the key away. Handing out an `XrplWallet` would make auto-lock decorative |
| D9 | `SignAsync` is asynchronous, though the SDK signs synchronously | The vault's gate is asynchronous. A synchronous `Sign` calling it is sync-over-async on a path that runs thousands of times per publication |
| D10 | Recovery is per-key export only. No recovery phrase | Owner's decision. A phrase that opens everything is a second full credential; fewer of those is the safer shape |
| D11 | No failed-attempt counter and no wipe | Both were sized for a six-digit PIN. A master password does not have that entropy problem, and a wipe triggered by a failing disk destroys data no attacker could have reached |
| D12 | New project `XrplFile.Keys`; the platform is reached through `IDeviceSecretStore` | Keeps the crypto testable with no operating system in the loop, and keeps `XrplFile.Core` free of MAUI. Part C implements the interface over `SecureStorage` |

### 3.1 Where this departs from the Wallet vault, and why

| Wallet | Here | Reason |
|---|---|---|
| Two factors: device secret **and** PIN | One factor: the password | D1. Two factors make the file useless when copied, which is the opposite of what this owner asked for |
| Integrity key derived from the device secret | Derived from the data key | With no mandatory device secret there is nothing else to derive it from. The cost is that integrity is verified after unwrapping rather than while locked, and this vault has no operation that runs while locked — the Wallet needed one to switch biometrics off on a shut vault |
| PBKDF2, because managed Argon2 is slow on weak Android | Argon2id | Desktop .NET 10. The Android objection does not apply, and D1 raises what the KDF has to carry |
| Record lives in one `SecureStorage` value | Record is a file; only the optional device key goes to the platform | A desktop application already writes part A's state files to a directory, and a file is what D1's "copy it" means |
| Failed-attempt counter with a wipe | Neither | D11 |
| Biometric second copy of the data key | The same slot, driven by "remember on this device" | Same mechanism, different trigger. Desktop has no biometric prompt worth building on yet |

## 4. Key schedule

```
PK   = Argon2id(password, salt, parameters)
DEK  = 32 random bytes, stored as AES-256-GCM(key = PK,  AAD = header)
deviceDek = the same DEK, stored as AES-256-GCM(key = PK', AAD = header)  optional, 4.2
IK   = HKDF-SHA256(IKM = DEK, info = "xrpl-file.vault.integrity")
mac  = HMAC-SHA256(IK, canonical form of the record)
entry = AES-256-GCM(key = DEK, AAD = reference)
```

`salt` is 16 random bytes, is not a secret, and lives in the header. `info` separates domains so
that the integrity key and the data key never coincide, and so that an export container derives
its own key rather than reusing this one.

The header is bound into both wraps as associated data. Without that, an attacker could lower
the Argon2 parameters in the header and the wrap would still open — a silent downgrade of the
only defence D1 leaves standing.

### 4.1 Why the data key is not the password key

A password change recomputes `PK`, unwraps `DEK` with the old one, wraps it with the new one and
writes. Entries are untouched, byte for byte. The alternative — encrypting entries directly under
a password-derived key — makes a password change a rewrite of every secret, and a rewrite
interrupted halfway leaves the entries it did not reach encrypted under a key that is about to be
forgotten.

### 4.2 Remember on this device

`IDeviceSecretStore` holds 32 random bytes that never leave the machine. `PK'` is
`HKDF-SHA256(that secret, info = "xrpl-file.vault.device")`, and it wraps a second copy of the
same `DEK`. Turning the option off removes the field from the record and asks the store to drop
its secret; the password wrap is never removable, so the vault stays openable either way.

The secret is **per vault, not per machine**. Every call into the store carries the identity of the
vault asking, and the store keeps one secret for each: a throwaway vault beside the real one is the
ordinary case, and with a single machine-wide secret each would be sealing under the other's, while
turning the setting off on either would take device unlock away from both. A vault must never be
handed, seal under, or delete a secret that is not its own, and separate identities are what makes
that structural rather than a rule to remember.

The identity is the vault file's full path, compared ordinally, and a store treats it as an opaque
key — hashing it where a platform wants a tamer item name than a path. A path needs no field in the
record, and so no bump of `v` to introduce. What it costs is one case, and it is accepted: a vault
file that is **moved or renamed** asks for a secret nobody stored, so device unlock on that machine
is gone and the user turns it back on. That is the same convenience a vault loses when it is copied
to another machine, and the wrap that carries a moved file was always the password one.

A vault file carried to another machine keeps the field and it is inert there — the key that
opens it stayed behind. This is stated rather than cleaned up: removing it on copy would require
knowing a copy happened, and nothing can know that.

A path outlives the file that was at it, so no vault may ask the store to drop a secret it has not
established is its own, and the only proof available is that this record's own device wrap opens
under it. `DisableDeviceUnlockAsync` asks for that proof before it clears the field, and skips the
deletion without it — a vault that never had a wrap, or one copied over a path where something else
enrolled a secret, clears its own field and stops there. A password change that drops the wrap tells
the two reasons apart for the same purpose: a store that says it holds nothing for this vault is
asked to stay that way, and a stored secret that does not open this wrap is left strictly alone.
Being wrong in the two directions is not symmetrical — a secret left behind that opens nothing is
litter, while a secret deleted that was not ours takes device unlock away from whatever is still
using it, and only the user can put that back.

### 4.3 Argon2id parameters

Parameters are constants measured on a desktop machine, not calibrated at run time — calibration
on the unlock path means extra KDF passes at every launch. They are written into the record
(`kdfId`, `memoryKib`, `iterations`, `parallelism`) so that a future recalibration still opens
vaults written by installed builds.

Measured on the development desktop with `Konscious.Security.Cryptography.Argon2` 1.3.1
(`tools/XrplFile.KeyBench`): `memoryKib = 262144` (256 MiB), `iterations = 3`, `parallelism = 4`,
at roughly 510 ms (482–563 ms across five re-verification runs), out of a sweep from 64 to 512 MiB
and 1 to 4 iterations. 512 MiB at the same iteration count was the largest grid point at or under
the 1000 ms budget and was the first pick, but 512 MiB was rejected in review, not for its time —
for its size. The target is the slowest unlock a person will accept once per session, which is a
different budget from a wallet unlocking on every payment, but the grid point at or under that
budget is not automatically the right pick: it only accounts for what an attacker pays, not for
what a legitimate device has to pay to open the file at all.

`kdfParams` travels inside every vault record (5), so whatever is chosen here is not a desktop-only
decision: a vault written on a desktop is opened later with these same parameters wherever the file
goes. Part C of this project is a MAUI Blazor Hybrid application, and MAUI's targets include phones,
not only desktops. A vault written at 512 MiB is one that a memory-constrained device may fail to
open at all, or open only after a long stall — and nothing in this spec scopes part C away from
mobile. 256 MiB is chosen instead: it still sits far above every published Argon2id recommendation
for a password-wrapped secret, so the factor-of-two loss in brute-force cost against 512 MiB is
cheap, and it buys back a platform that 512 MiB would quietly foreclose. Raising these numbers
later is not only a decision about how hard the vault is to attack — it is a decision about which
devices can still open one, and it should be made with a phone in hand, not only a desktop.

Because the parameters travel inside the file, they are also an input an attacker can edit, and
the header binding that catches such an edit only speaks **after** the derivation it describes has
been paid for. So a cost read back from a vault record or an export container is checked against
`KdfParameterBounds` before anything derives from it, and one outside them is `VaultError.Corrupt`
— the word the export container already used — rather than a raw `ArgumentOutOfRangeException` out
of a method whose contract is an enumeration, or an attempt to allocate two terabytes because a
four-byte field said so. The bounds are deliberately far wider than anything this software chooses,
because the failure they must never produce is refusing a real vault: memory 8 KiB to 4 GiB
(sixteen times the measured figure), iterations 1 to 1024, lanes 1 to 255 — which is what the
export container's one-byte lane field can carry (8) — and at least 4 KiB of memory per lane, which
is the floor the Argon2 implementation itself enforces and therefore the floor below which no vault
could have been written in the first place.

The same bounds are enforced by `KdfParameters` itself, so that what a reader is willing to open is
exactly what a writer is able to write. Without that, the check above closes only half the door: a
cost outside the bounds could still be handed to `new KeyVault(...)`, which would create and store
happily, and the first `UnlockAsync` afterwards — and every one after it — would answer
`VaultCorrupt` over a file whose keys are still inside and which no password can reach. The
constructor refuses such a cost with `ArgumentOutOfRangeException`, and the properties have no
setters, so `with` cannot reach past it either. A cost read out of a file is a different question
and is still asked first against `KdfParameterBounds`, which is why a damaged header is reported as
damage rather than as an argument fault.

## 5. The record

One file. The vault is small — tens of entries at most — and a file per entry buys nothing but
a directory to keep consistent.

```json
{
  "v": 1,
  "kdfId": 2,
  "kdfParams": { "memoryKib": M, "iterations": T, "parallelism": P },   // measured, 4.3
  "salt": "base64, 16 bytes",
  "dek": "base64: nonce 12 + ciphertext 32 + tag 16",
  "deviceDek": "base64, optional — see 4.2",
  "mac": "base64, 32 bytes",
  "entries": { "reference": "base64: nonce 12 + ciphertext N + tag 16" }
}
```

### 5.1 The integrity code

`mac` covers a canonical byte encoding of `v`, `kdfId`, `kdfParams`, `salt`, `dek`, `deviceDek`
and every entry in ascending ordinal order of its reference. It exists alongside the per-entry
authentication tags because a tag proves an entry's bytes were not altered and proves nothing
about an entry that was **deleted**, or about the whole file being rolled back to an earlier
version.

`v`, `kdfId` and `kdfParams` are inside the canonical form on purpose. A future change of
algorithm or encoding that left `mac` matching while `DEK` no longer unwrapped would present
itself as a wrong password — indistinguishable, at the one moment when telling the user the truth
matters most.

### 5.2 Entries

An entry's plaintext is the canonical JSON of the model in 6.1, with a fixed field order. Its
associated data is its own reference, so moving a ciphertext from one entry to another — or
relabelling an imported key as a generated one — fails the tag rather than signing with the wrong
key.

### 5.3 The in-memory cache

An unlocked vault holds the parsed record and the unwrapped `DEK`. `RevealAsync` does not re-read
the file. A publication signs thousands of times, and a design that read and parsed the vault per
signature would put a file read on the ledger's critical path.

The cache is invalidated by `LockAsync` and by every write the vault itself makes.

## 6. Model and interfaces

### 6.1 An entry

```
Reference             GUID assigned by the vault — see below
Kind                  Generated | Imported
Label                 what the user calls it
Network               which ledger this key is for
Address               classic address, stored rather than re-derived from the seed on every list
CreatedAt
Secret                the seed
Generated only:
  LastKnownSequence
  DeletableFromLedger   Sequence + 254, part A's measured threshold
  Exported              whether the user has ever saved this key elsewhere
```

`Exported` follows from D10. When per-key export is the only way back, the application has to be
able to see which throwaway accounts exist nowhere else and say so, rather than discovering it
the day a password is forgotten.

`Address` is stored rather than derived from the seed on every list — listing already touches the
data key to decrypt each entry (6.2), and deriving an address from a seed on top of that would be
an elliptic-curve operation this design has no need to pay for.

The reference is the string part A's `PublicationState.KeyReference` carries. Part A treats it as
opaque — it neither generates it nor constrains its shape — so the vault is what makes it a GUID,
and part C passes the vault's reference into the session. That the vault is the sole issuer is
what the second limitation in 11 leans on.

### 6.2 The vault

```csharp
public interface IKeyVault
{
    bool IsUnlocked { get; }
    IReadOnlyCollection<string> References { get; }
    IReadOnlyList<string> DamagedReferences { get; }

    Task<bool> ExistsAsync(CancellationToken cancellationToken);
    Task AcknowledgeDamageAsync(CancellationToken cancellationToken);
    Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
    Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
    Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken);
    Task LockAsync();

    Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken);
    Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken);
    Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken);
    Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken);
    Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken);
    Task DeleteAsync(string reference, CancellationToken cancellationToken);

    Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken);
    Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken);
    Task DisableDeviceUnlockAsync(CancellationToken cancellationToken);
}
```

Three shapes of the same entry, and the split is load-bearing. `NewVaultEntry` is everything in
6.1 except the reference, which the vault assigns. `VaultEntryInfo` is everything except the
secret, and it is what `ListAsync` returns. D7 puts an entry's metadata inside the same encrypted
blob as its secret, so `ListAsync` cannot avoid the decrypt — it still needs the data key to open
every entry and read a label. What `VaultEntryInfo` actually guarantees is narrower, and it is
still the guarantee that matters: the secret never escapes into caller code, into a binding, or
into a log, because the shape callers are handed simply has no field to carry it in.
`VaultEntry`, the only shape carrying the secret, comes back from `RevealAsync` alone.
`VaultEntryEdit` carries the mutable metadata — label, `LastKnownSequence`,
`DeletableFromLedger`, `Exported` — and can reach neither the secret, the kind, nor the address.

`ExportAsync` reveals one entry, seals it into an `ExportContainer` under a password the caller
supplies, and marks the entry `Exported` — all three in the one call, because the guaranteed
recovery path D10 requires cannot depend on a caller remembering, separately, to record that it
used it. The write that sets `Exported` happens only after the container is confirmed sealed: a
throw out of the sealing step leaves the entry exactly as it was. A write that fails after the
container is sealed rolls the flag back to what the file holds and hands the container over
anyway; a write that is **cancelled** rolls the flag back and propagates the cancellation, because
the caller never receives the container and nothing was exported. Both cases are the same rule as
`StoreAsync`, `UpdateAsync` and `DeleteAsync`: a cache left ahead of the file is committed by the
next write of any kind, and an entry that permanently claims a backup nobody holds silences the
one warning D10 depends on.

Password buffers belong to the caller throughout. The vault reads them, derives from them, and
never stores them — and does not zero them either, because the caller usually still needs the
buffer for a confirmation comparison or a retry, and a callee that wipes its argument is a trap.

`ChangePasswordAsync` reports `VaultPasswordChange.WrongPassword` for exactly one condition: the old
password is wrong. Every other failure throws. A damaged record reported as a wrong password is how
a disk fault becomes a user's fault.

The return is a `[Flags]` enumeration rather than a `bool` because a change has facts to report
alongside "it happened", and they are independent of each other. `Changed` is set whenever the new
record reached the disk; `WrongPassword` is the empty set, so a value nobody set cannot read as
success and "nothing was written" is the absence of every flag.

`DeviceUnlockDropped` is the first of the three qualifiers: a fresh salt makes a fresh header, both
key wraps are bound to that header (4.2), and a machine that cannot re-seal the device wrap under
the new one drops it rather than leave a wrap nothing will open. A `bool` whose `false` already
means "wrong password" leaves that unsayable, and the user finds out at the next launch.

`TheOldCopyRemains` is the second, and it is why "every other failure throws" stops where it does.
Removing the kept copy (below) happens **after** the new record is committed, and on Windows an
indexer or an antivirus holding `vault.json.bak` open is enough to refuse the delete. Thrown, that
failure says "the password was not changed" about a change that is already on disk: the caller
retries with the old password, is answered `WrongPassword`, and the copy that old password still
opens is left there with nobody told.

`TheRecordsIntegrityCodeDidNotMatch` is the third, and the reason the shape is flags rather than one
name per outcome. Resealing recomputes the `mac` over the record as it stands, which destroys a
mismatch — the only witness there is to an entry removed from the file or to a rollback (11). The
change still goes ahead, for the reason 11 gives, but silence is not part of that trade: the flag
carries the evidence to a person instead. With three independent facts, a list of named
combinations would need eight entries, and the moment one is left unnamed it becomes unsayable —
which for this flag means losing evidence that exists nowhere else. Flags make every combination
expressible by construction, and the ones that cannot occur — anything alongside `WrongPassword` —
are ruled out by `Changed` being set in exactly one place.

`AcknowledgeDamageAsync` says that the damage the last unlock reported has been seen. It exists
for one kind: a record whose `mac` does not match its own contents (5.1). Every write recomputes
that code, so the first write after such an unlock would erase the only witness there is to an
entry removed from the file or to bytes edited under it — a per-entry tag proves nothing about an
entry that is no longer there. Writes are therefore refused with `VaultError.Degraded` until this
is called, and reads are not: `ListAsync`, `RevealAsync` and `ExportAsync` all work untouched,
because rescuing the keys that are still intact is exactly what `VaultDegraded` is for. Nothing is
written by the acknowledgement itself, so the file keeps the mismatch until a write genuinely
needs to happen and a vault opened again reports `VaultDegraded` once more.

The change also removes the copy the durable write keeps beside the vault. Everywhere else that
copy is what makes an interrupted write survivable; here it is a whole record that the password the
user has just replaced still opens, with no attacker needed to place it — the rollback limitation
in 11 assumes one.

### 6.3 Concurrency

Every operation passes through one asynchronous gate, and `LockAsync` takes the same gate.

This is not tidiness. Idle auto-lock can fire in the middle of an encryption, and a lock that
zeroed the data key mid-operation would produce an entry encrypted under a half-wiped key — which
the writing code would then verify successfully against that same wiped array in memory. The
entry would be recorded as stored and would never open again.

Rules: operations take a copy of the data key under the gate; `LockAsync` waits for the operation
in flight rather than cutting it off; a result produced by an operation that began before a lock
is discarded.

### 6.4 Auto-lock

A sliding idle timeout: `VaultAutoLock` takes the window as a constructor argument and has no
built-in default of its own — the caller picks it, and every caller so far (tests, `VaultSigner`)
picks 15 minutes. Nothing in `KeyVault` touches the timer; only whatever holds the `VaultAutoLock`
does, by calling `Touch()`. `VaultSigner` is the one caller that does today, once after every
signature, so a publication signing continuously does not lock itself out mid-run, while a
machine left alone — nothing left calling `Touch()` — still locks once the window passes.
Explicit `LockAsync` is always available.

## 7. The seam into part A

One line in `XrplClientLedgerSink.SubmitAsync` calls `wallet.Sign(transaction)`, and that is the
only place in the library where a secret is needed.

```csharp
public sealed record SignedTransaction(string TxBlob, string Hash);

public interface ITransactionSigner
{
    string Account { get; }
    string PublicKey { get; }
    Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken);
}
```

`PublicKey` carries what every transaction's `SigningPubKey` must be. It is on the interface,
not derived from `Account` on demand, because `SequenceStrategy`, `TicketStrategy` and
`ThrowawayAccount` all read `signer.PublicKey` to build that field — a signer that could answer
only `Account` would not compile against any of them.

The refactor:

- `ILedgerSink.SubmitAsync` takes an `ITransactionSigner` where it took an `XrplWallet`.
- `SequenceStrategy`, `TicketStrategy` and `ThrowawayAccount` take one too; their `Account`
  reads `signer.Account` instead of `wallet.ClassicAddress`, and their `SigningPubKey` reads
  `signer.PublicKey` instead of `wallet.PublicKey`.
- `XrplFile.Core` gains the interface and a `WalletSigner` adapter over `XrplWallet`, so callers
  that hold a wallet — including every existing test — keep working.
- `XrplFile.Keys` provides `VaultSigner`, which holds a reference and asks the vault per
  signature, and refuses with a typed error when the vault is locked. `VaultSigner.CreateAsync`
  reveals the entry once, at construction, to learn the public key behind it — an entry records
  an address but not the key itself — and refuses `VaultError.AddressMismatch` when the two
  disagree, since `StoreAsync` itself never checks a stored address against the key behind it.
  An optional `VaultAutoLock`, passed to either the constructor or `CreateAsync`, is touched
  after every signature (6.4); a signer built with none touches nothing.

A lock in the middle of a publication fails the run. That is survivable and was the point of D8:
part A resumes by asking the ledger what landed, so the money already spent is not lost and the
chunks already on the ledger are not sent twice.

## 8. Export and import

Per-key export is the only path back, so it is a first-class operation rather than a setting.
`IKeyVault.ExportAsync` is the one call that does the whole thing: reveal the entry, seal it into
a container under the password the caller supplies, and mark the entry `Exported` — never before
the container is confirmed sealed, so a throw out of `ExportContainer.Protect` leaves the entry
untouched. `ExportContainer.Protect` and `.Open` also stand on their own, for a caller that
already holds a `VaultEntry` from `RevealAsync` and wants to seal it without going through the
vault a second time.

- **Reveal one secret on screen**, for a user copying it into another wallet.
- **An encrypted container**, for a file or a QR code, with its own password. Header:
  `"XFK1" | version | kdfId | kdfParams | salt | nonce | ciphertext | tag`, header as associated
  data, deflate before encryption.
- The container's key derivation is its own; it never reuses the vault's `DEK`.
- Import creates an `Imported` entry via `StoreAsync`, refusing a duplicate address on the same
  network rather than silently holding the same key twice. There is no separate `ImportAsync`:
  the caller decrypts a container with `ExportContainer.Open` or already holds a bare seed, and
  passes what it finds straight to `StoreAsync`.

`ExportAsync` is what sets `Exported` on the guaranteed recovery path. `VaultEntryEdit` also
carries an `Exported` field that `UpdateAsync` writes directly when a caller sets it, so
`ExportAsync` is not the field's only writer — it is the only writer coupled to an export having
actually happened.

## 9. Failures

`VaultUnlockResult` separates outcomes that callers must treat differently:

| Value | Meaning |
|---|---|
| `Success` | Opened, every entry verified |
| `WrongPassword` | The record is intact and the password does not unwrap the data key |
| `VaultMissing` | No record to open |
| `VaultCorrupt` | The record is damaged, or a password failed over a record whose integrity code already did not match. The two cannot be told apart, so it is never reported as "your password is not the problem" |
| `VaultDegraded` | The password opened the vault and something inside is damaged. The user is in; `DamagedReferences` names the casualties |
| `VaultUnsupported` | Format version or `kdfId` this build cannot open |
| `VaultUnavailable` | Storage refused a read or write. Transient; nothing can be concluded |

Damage and access are two questions, so they get two values. An entry deleted from the file
appears nowhere — the vault cannot report a casualty it has no record of — so a caller compares a
publication's `KeyReference` against `References` to say "this publication's key is gone" rather
than showing a corrupt vault with no victims.

A failed unlock leaves an already-open vault open: confirming a password and opening a vault are
the same call, and a mistyped confirmation must not end the session it was meant to re-check.

`VaultUnlockResult` is what unlocking itself can report. Everything a vault does once open — or
its own storage refusing — reports through `VaultError` on a thrown `VaultException` instead:

| Value | Meaning |
|---|---|
| `Corrupt` | The record could not be read as a record at all |
| `Unsupported` | The record's format version or KDF identifier is not one this build can open |
| `Locked` | An operation needing the data key was asked of a locked vault |
| `NoSuchEntry` | No entry with that reference |
| `DuplicateAddress` | Another entry already holds this address on this network; `StoreAsync` refuses rather than silently holding the same key twice |
| `EntryDamaged` | An entry failed its authentication tag: damaged bytes, or moved between references |
| `StorageFailed` | Reading or writing the vault file failed |
| `Degraded` | A write was asked of a vault whose record failed its own integrity code when it was opened, and the damage has not been acknowledged. Reading is never refused for this |
| `AddressMismatch` | An entry's recorded address does not match the address its own key derives to — caught here, before anything is built or signed for the wrong account |

## 10. What must be measured before it is believed

1. **Argon2id parameters** (4.3): measured with `tools/XrplFile.KeyBench` on the development
   desktop, using `Konscious.Security.Cryptography.Argon2` 1.3.1. `memoryKib = 262144` (256 MiB),
   `iterations = 3`, `parallelism = 4`, at roughly 510 ms — a grid point well under the 1000 ms
   per-session unlock budget, chosen over the larger 512 MiB point (which the budget alone would
   have picked) because the parameters travel inside every vault record and part C's MAUI target
   reaches phones, which 512 MiB risks foreclosing.
2. **The cost of deriving a wallet per signature.** `XrplWallet.FromSeed` measured at roughly
   0.7 ms per call (1000 calls in under a second on the same machine). A 20 MB publication is
   roughly 21,000 transactions, so deriving fresh on every signature adds on the order of 15
   seconds against a run of tens of minutes — affordable. `VaultSigner` derives per signature and
   the secret's lifetime is one operation.

Both were measured as task 0 of the plan, before anything else depended on the answer.

## 11. Known limitations

Recorded because they are real, not because they are acceptable everywhere.

- **No rollback protection.** The integrity key comes from the data key, and the canonical form
  carries no monotonic counter. An older but genuine file passes every check: whoever can write
  over the vault file can restore a previous password and resurrect deleted entries. Closing this
  needs a counter somewhere the rollback cannot reach, which on a single-file design is the file
  itself. The attack's precondition is write access to the user's own files.
- **A password change reseals over a mismatched `mac`, and says so.** `AcknowledgeDamageAsync`
  guards every write that goes through the vault's own persist path, but `ChangePasswordAsync`
  reads the file itself and works on a vault that may be locked, so it runs none of the per-entry
  verification an unlock runs. Refusing the change was never the answer — the alternative is
  refusing to replace a leaked password on a vault with one bad byte in it, and a user in that
  position needs the change more than the evidence — but neither is silence. The change goes
  ahead, and the result carries `TheRecordsIntegrityCodeDidNotMatch`. That is the whole of the
  remaining limitation: resealing recomputes the code over the record as it stands, so the
  mismatch is gone afterwards and the next unlock reports `Success`. The flag is the last moment
  at which anyone can learn the file did not add up, which is why the result says it rather than
  leaving it to a caller that might have unlocked first.
- **Canonical form without field lengths.** Separators join binary fields without declaring their
  lengths, so distinct field sets producing identical canonical bytes exist in principle. The
  application cannot generate such a pair — references are GUIDs of fixed shape. Recorded as debt
  to pay at the next bump of `v`, and not before: the encoding is pinned by a fixed vector, and
  changing it outside a version bump stops opening records that installed builds wrote.
- **Secrets cannot be zeroed.** `XrplWallet.FromSeed` takes a `string`, and .NET strings are
  immutable. The mitigation is lifetime, not erasure, and the second measurement in 10
  decides how short that lifetime can be.
- **A forgotten password loses everything not exported.** This is D1 and D10 together, chosen
  deliberately. `Exported` exists so the application can warn before it matters.

## 12. Testing

| What | How |
|---|---|
| Format stability | A fixed vault vector compared byte for byte, so the encoding cannot drift silently |
| KDF vectors | Argon2id output pinned against published test vectors, so a library upgrade cannot change derivation |
| A damaged entry | Flip a byte: that reference reports damaged, the others still open |
| A deleted entry | Remove one: `mac` fails, and the caller can name what is missing |
| Durability | Kill between write and rename: the previous good file opens |
| A file that goes mid-read | The vault file is gone by the time it is opened: the kept copy is read, not `StorageFailed` |
| Concurrency | Lock during an in-flight store: the gate serialises and no half-wiped key reaches a record |
| Password change | Entries byte-identical before and after; the device wrap still opens afterwards |
| A committed change whose cleanup fails | The kept copy cannot be deleted: the outcome says the password changed and the copy remains, and the new password opens the vault |
| A change over a damaged record | The `mac` does not match: the change happens and the outcome says the code did not match, alongside every other flag that applies |
| A cancelled write | Cancel the token mid-write: the cache holds exactly what the file holds, on export as on store |
| Someone else's device secret | Disable, and change the password, on a machine holding a secret this vault's wrap does not open: the secret is still there afterwards |
| Two vaults on one machine | One store, two vault files: two secrets, and disabling or changing the password on one leaves the other's device unlock working |
| A tampered KDF cost | Zero memory, and two terabytes of it: `VaultCorrupt`, with nothing derived |
| A cost no reader would accept | The same costs refused where they are named: no vault can be created at one |
| Writing while degraded | A write over a mismatched `mac` is refused until acknowledged, and the mismatch survives the refusal |
| Portability | A vault written on one path opens from another with the password, and its device wrap is inert |
| The seam | At least one part A integration publication driven through `VaultSigner` against a standalone node |

Unit tests touch no operating system: `IDeviceSecretStore` has an in-memory double, and the vault
is a file under a temporary directory.

## 13. Scope

In: the vault, its format, export and import, the signer interface, and part A's refactor onto it.

Out: the user interface, which is part C; an external-signer entry kind, which D6 leaves room for
and nothing in this part builds; and reclaiming a throwaway's base reserve with `AccountDelete`,
which part A's design assigns to part C and for which this vault supplies the record and the
arithmetic.
