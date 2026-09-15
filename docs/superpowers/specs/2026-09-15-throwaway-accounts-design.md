# The throwaway account's life — design

Part C3 of the desktop application. C2 publishes a file from a key the vault already holds, whose
account somebody else already funded. This is the part that makes that account: generated for one
publication, funded by hand, and closed afterwards so its base reserve comes home instead of
sitting on the ledger forever.

| Part | What it is | State |
|---|---|---|
| A | tickets and the publication session | shipped |
| B | the key vault | shipped |
| C1 | the shell and the vault screens | shipped |
| C2 | publishing: a file, a key, an estimate, a run | shipped |
| **C3** | **the throwaway account's life, including reclaiming its reserve — this document** | **in progress** |
| C4 | reading a link back into a file | not started |

`XrplFile.Core` already knows most of this. `ThrowawayAccount` computes what an account still needs
(`FundingAsync`), waits for it (`AwaitFundingAsync`), reports who funded it (`FunderAsync`), sends
what it can spare back (`RefundAsync`), and says from which ledger deletion becomes possible
(`DeletableFromLedgerAsync`). One thing is missing, and it is the one that matters: nothing anywhere
submits an `AccountDelete`, so the base reserve — the largest single number in the whole flow —
never comes back.

## 1. Scope

**In.** An Accounts tab beside Keys, Publish and Settings. Generating a throwaway key. Showing its
address, a QR of that address, and its balance as the ledger reports it. Saying when the account
becomes deletable and counting down to it. Closing the account with `AccountDelete`, to a
destination the person names, with the preflight checks that stop a doomed submission from burning
its fee. Recording on the vault entry that the account was closed and where its remainder went.

Also in, because the closing screen cannot be honest without it:

- `ThrowawayAccount.DeleteAsync` in `XrplFile.Core` — the transaction itself, with its refusals
  (section 3);
- one method on `ILedgerSource` that answers what a destination is, because a destination that
  refuses the payment costs an owner reserve to find out from the node (section 3.2);
- one field on the vault entry recording the closure, and the backward-compatible read that keeps
  older vaults opening (section 4.2).

**Out.** Funding the account — the application has no money; it shows an address and waits.
Publishing, which is C2 and unchanged by this part. Reading a link back into a file, which is C4.
Choosing a destination on the person's behalf, ever.

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| G1 | The throwaway account lives on its own tab, not inside the publish flow | Publishing already works and is reviewed. A key created and funded here is simply a key the vault holds by the time C2 asks for one, so nothing in C2 changes |
| G2 | The way home is `AccountDelete`, not a payment | It returns the base reserve, which a payment cannot; one transaction and one fee instead of two of each. The payment `RefundAsync` stays for the case deletion cannot fix (section 5.3) |
| G3 | The destination is named by the person, from the vault's own keys or typed, and never defaulted | Owner's standing rule: without a destination there is no deletion. Offering only the vault's keys would forbid the commonest case, sending it to an exchange or another wallet |
| G4 | The transaction lives in `ThrowawayAccount`, in `XrplFile.Core` | It is protocol work — a fee sized as an owner reserve, the `Sequence + 255` rule, the destination's flags — and the only place it can be exercised against a real node is `XrplFile.Core.Tests`, which already drives a standalone stand |
| G5 | Preflight refusals never submit | A `tec*` result claims the fee, and this fee is an owner reserve. Every refusal we can see coming and do not is money burned for nothing |
| G6 | The confirmation shows the destination in full, with its balance | A truncated address hides exactly the characters a typo lives in, and the destination's balance is the one way a person can tell they reached the account they meant |
| G7 | The vault entry is marked closed, never deleted by this part | Owner's decision. A deleted address can be recreated by funding it again and the seed still controls it, so the key is not automatically worthless — and this application does not destroy a secret as a side effect of another action |
| G8 | `LastKnownSequence` and `DeletableFromLedger` are for display before the node answers, never for deciding to submit | They are a cache, and the thing they cache — the ledger — moves on its own. Deciding from them is the defect class parts B and C1 hit three times, with the added twist that here nobody has to touch anything for the cached value to go stale |
| G9 | "Create" generates a key and says so | On the XRP Ledger an account exists once it holds its reserve. A screen that says it created an account has lied to anyone who then waits for one |
| G10 | The Accounts tab computes no target amount | What a publication needs depends on the file, and C2's estimate screen already answers it exactly, against the real bytes. A second, vaguer answer beside it would be the one people trust |
| G11 | The countdown is not a reason to offer the payment refund | Waiting is not "deletion is impossible". Offering a payment while the clock runs talks a person into two fees and leaves the base reserve behind forever, which is the whole thing G2 exists to avoid |
| G12 | Every refusal gets its own sentence, and three are never conflated | "Not yet — N ledgers to go", "that destination does not exist" and "the node refused" have nothing in common in what the person does next |

## 3. What `XrplFile.Core` gains

### 3.1 `ThrowawayAccount.DeleteAsync`

One method beside `RefundAsync`, with the same discipline about destinations: an empty one is an
`ArgumentException`, because nothing here ever chooses one.

```
Task<DeletionResult> DeleteAsync(string destination, CancellationToken cancellationToken)
```

It reads the network's fees, the account's state, the destination, and the validated ledger index;
decides whether the transaction can succeed; and submits only if it can.

`DeletionOutcome` distinguishes:

- `NotYet` — the threshold is not reached. Nothing is submitted, and the result carries the ledger
  from which deletion becomes possible, so the screen can count down to it.
- `DestinationMissing` — the destination does not exist on the ledger. Nothing is submitted.
- `DestinationRefuses` — the destination exists but will not accept this payment: it requires a
  destination tag, or it authorises its depositors and has not authorised this one. Nothing is
  submitted.
- `Submitted` — the node applied it to its open ledger.
- `Queued` — the node only queued it. Until a ledger takes it every drop is still on the throwaway
  account, and reporting that as money that went home is a lie a person discovers later. This is the
  same distinction `RefundOutcome` already draws, for the same reason.

A node refusal that is none of the above travels as `XrplFileException` with
`XrplFileError.SubmitRefused`, as `RefundAsync` already does.

`DeletionResult` carries what the screen has to show and what a log has to keep: the destination the
caller named, what the account held, what the fee took, what will therefore arrive, the transaction's
hash when there is one, the ledger deletion becomes possible from when there is not, and the outcome
above. `RefundResult` is the shape to follow — including its habit of naming the destination back, so
nothing downstream has to remember what it asked for.

The fee is one owner reserve, read from the node rather than assumed —
`tests/XrplFile.Core.Tests/AccountDeleteThresholdTests.cs` already reads it that way, citing
rippled's `AccountDelete::calculateBaseFee`, and that is the shape to follow.

### 3.2 One method on `ILedgerSource`

The preflight needs to know three things about a destination: whether it exists, whether it requires
a destination tag, and whether it authorises its depositors. `AccountStateAsync` answers none of
them — it returns a balance, an owner count and a ticket count, and it sits on the estimate's hot
path, so widening it makes every publication pay for something only this screen needs.

So `ILedgerSource` gains one method describing a destination. Two implementations follow it: the
real one over the SDK's client, and `FakeLedgerSource` in `XrplFile.TestSupport`, which every test in
this repository already shares.

The alternative — submit and let the node refuse — is not free. `tecDST_TAG_NEEDED` and
`tecNO_PERMISSION` are `tec` results, and a `tec` result claims the fee. Choosing not to check costs
an owner reserve every time someone pastes the wrong address, which is the same amount this whole
part exists to recover.

## 4. What the application gains

### 4.1 The Accounts tab

It lists the vault's `Generated` entries — the keys this application made — and for each shows the
address, a QR of it (`QrMatrix` already renders one for the seed screens), the balance the ledger
reports, and which of four states the account is in:

1. a key, with no account yet — nobody has funded it;
2. an account, holding some amount;
3. an account that can be closed from ledger N — with the countdown;
4. a closed account, with where its remainder went.

Creating one generates a key through the same vault call C1's key screen uses, with the kind and a
default label filled in. The screen says it generated a key and that the account appears once
somebody funds the address (G9).

Nothing here computes how much to send (G10).

### 4.2 The vault entry records the closure

`VaultEntryInfo` gains one field for the closure: when the account was deleted and where its
remainder went. `VaultEntryEdit` gains the same, so the application can write it.

**The read must tolerate its absence.** An entry's payload is a JSON object inside the ciphertext,
and `KeyVault`'s `EntryCodec.Read` reaches for its fields with `GetProperty`. A new field read that
way would throw on every entry written before this part — which is every entry in every vault that
exists today, and the failure mode is a vault that no longer opens at all. The new field is read with
`TryGetProperty`, and a test covers an entry written without it.

### 4.3 The models

In `app/XrplFile.App.Core`, each with its own tests, as C2's are:

- the account list and its four states;
- generating a throwaway key;
- funding: the address, its QR payload, and the balance as it is polled;
- closing: the destination choice, the preflight, the confirmation, and the outcome.

`LastKnownSequence` and `DeletableFromLedger` are written on the entry as the application learns
them, and read back only to show something before the node has answered (G8). Every decision to
submit is made from a fresh read.

## 5. Closing an account

### 5.1 The screen

The destination comes from the vault's other keys or from a typed address (G3). Then the preflight
runs, and only then is there anything to confirm.

The confirmation shows the destination address **in full**, its current balance, what will move, the
fee — one owner reserve — and, in words, that the account will cease to exist. Truncating the address
would hide the characters a typo lives in, and the destination's balance is how a person recognises
the account they meant (G6).

### 5.2 While the countdown runs

An account whose threshold is not reached shows how many ledgers remain and roughly how long that is.
It does not show a second button (G11).

### 5.3 When deletion cannot work

If deletion is refused for a reason waiting cannot fix, the payment refund `RefundAsync` already
implements is offered instead, saying plainly what it leaves behind: the base reserve and one owner
reserve per object the account still owns, none of which a payment can recover.

In practice a publish-only account owns nothing but tickets, which `AccountDelete` removes with it,
and the protocol caps live tickets far below the limit on deletable objects. So this path is rare —
which is exactly why it must say what it costs rather than looking like the normal way out.

## 6. Errors

Each gets its own sentence, in a model with a test that enumerates them, the way
`PublishErrorMessages` does. Three are never conflated (G12): the threshold is not reached and the
person waits; the destination does not exist and the person checks the address; the node refused and
neither of those helps.

The countdown's sentence carries a number — ledgers remaining, and about how long — because "not
yet" without a number is indistinguishable from "never" to someone watching it.

## 7. Testing

The models are tested on Linux in CI against the fake ledger, as C2's are: the only thing faked is
the node, so the real `ThrowawayAccount` and the real preflight stay inside the tested path.

The proof that any of it works is one integration test in `XrplFile.Core.Tests`, against the
standalone stand, that goes the whole way: generate a key, fund it from genesis, publish a file,
close the account, and check the remainder arrived and the account is gone.
`AccountDeleteThresholdTests` already stands up that stand and drives ledgers forward so a
255-ledger wait takes seconds rather than a quarter of an hour; the new test sits beside it and uses
the same machinery.

The Windows-only screens get what C1 and C2's got: a compile job, and every decision they might make
living in a model instead (C1's E2).

## 8. What this part does not do

It does not fund anything. It does not publish — C2 does that, unchanged. It does not read a link
back into a file, which is C4 and the last part. And it never picks a destination: without one there
is no deletion, which is the rule this whole part is built around.
