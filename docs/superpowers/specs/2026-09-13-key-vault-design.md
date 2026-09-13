# The key vault — design

Part B of the three that lead to a desktop application. Part A put a file on the ledger and
spent money doing it; this part decides where the keys that signed it live, and hands part C a
surface it can put behind a screen.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault — this document | designed |
| C | the MAUI Blazor Hybrid application over A and B | not started |

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

A vault file carried to another machine keeps the field and it is inert there — the key that
opens it stayed behind. This is stated rather than cleaned up: removing it on copy would require
knowing a copy happened, and nothing can know that.

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
Address               classic address, stored rather than derived so listing costs nothing
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

`Address` is stored because every field here is encrypted anyway: deriving it would mean touching
key material to draw a list.

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
    Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
    Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);
    Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken);
    Task LockAsync();

    Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken);
    Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken);
    Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken);
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
secret, and it is what `ListAsync` returns — drawing a list of keys must not decrypt a single
seed, or the whole vault's plaintext would pass through memory every time a screen opened.
`VaultEntry`, the only shape carrying the secret, comes back from `RevealAsync` alone.
`VaultEntryEdit` carries the mutable metadata — label, `LastKnownSequence`,
`DeletableFromLedger`, `Exported` — and can reach neither the secret, the kind, nor the address.

Password buffers belong to the caller throughout. The vault reads them, derives from them, and
never stores them — and does not zero them either, because the caller usually still needs the
buffer for a confirmation comparison or a retry, and a callee that wipes its argument is a trap.

`ChangePasswordAsync` reports `VaultPasswordChange.WrongPassword` for exactly one condition: the old
password is wrong. Every other failure throws. A damaged record reported as a wrong password is how
a disk fault becomes a user's fault.

The return is an enumeration rather than a `bool` because a change has a second outcome a caller
must be able to show: a fresh salt makes a fresh header, both key wraps are bound to that header
(4.2), and a machine that cannot re-seal the device wrap under the new one drops it rather than
leave a wrap nothing will open. `ChangedAndDeviceUnlockDropped` says so; a `bool` whose `false`
already means "wrong password" leaves it unsayable, and the user finds out at the next launch.

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

A sliding idle timeout, 15 minutes by default, reset by every operation including a signature. A
publication therefore does not lock itself out while it is working, and a machine left alone
does lock. Explicit `LockAsync` is always available.

## 7. The seam into part A

One line in `XrplClientLedgerSink.SubmitAsync` calls `wallet.Sign(transaction)`, and that is the
only place in the library where a secret is needed.

```csharp
public sealed record SignedTransaction(string TxBlob, string Hash);

public interface ITransactionSigner
{
    string Account { get; }
    Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken);
}
```

The refactor:

- `ILedgerSink.SubmitAsync` takes an `ITransactionSigner` where it took an `XrplWallet`.
- `SequenceStrategy`, `TicketStrategy` and `ThrowawayAccount` take one too; their `Account`
  reads `signer.Account` instead of `wallet.ClassicAddress`.
- `XrplFile.Core` gains the interface and a `WalletSigner` adapter over `XrplWallet`, so callers
  that hold a wallet — including every existing test — keep working.
- `XrplFile.Keys` provides `VaultSigner`, which holds a reference and asks the vault per
  signature, and refuses with a typed error when the vault is locked.

A lock in the middle of a publication fails the run. That is survivable and was the point of D8:
part A resumes by asking the ledger what landed, so the money already spent is not lost and the
chunks already on the ledger are not sent twice.

## 8. Export and import

Per-key export is the only path back, so it is a first-class operation rather than a setting.

- **Reveal one secret on screen**, for a user copying it into another wallet.
- **An encrypted container**, for a file or a QR code, with its own password. Header:
  `"XFK1" | version | kdfId | kdfParams | salt | nonce | ciphertext | tag`, header as associated
  data, deflate before encryption.
- The container's key derivation is its own; it never reuses the vault's `DEK`.
- Import accepts a container or a bare seed and creates an `Imported` entry, refusing a duplicate
  address on the same network rather than silently holding the same key twice.

Export sets `Exported` on the entry. Nothing else in the system writes that field.

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
| Concurrency | Lock during an in-flight store: the gate serialises and no half-wiped key reaches a record |
| Password change | Entries byte-identical before and after; the device wrap still opens afterwards |
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
