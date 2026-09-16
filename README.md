# xrpl-file

A link that points at a file stored inside the XRP Ledger, and the tools to write and read one.

```
xrpl-file://mainnet/9F2C0B7D1E4A6C8B3D5F7A9C1E3B5D7F9A1C3E5B7D9F1A3C5E7B9D1F3A5C7E9B
```

An NFT's `URI` usually points at IPFS, and IPFS content lasts only as long as somebody pins it.
The ledger entry is permanent; the thing it names is not. An xrpl-file link names a *manifest*
transaction; the manifest names the ledger range holding the file's bytes in memos, and a
Merkle root to verify them. Nothing to pin, nothing to expire, and every byte checked.

The format is specified in [docs/spec/xrpl-file-v1.md](docs/spec/xrpl-file-v1.md). The design
and its reasoning are in [docs/superpowers/specs](docs/superpowers/specs).

## What is here

| | |
|---|---|
| `src/XrplFile.Core` | .NET library: parse a link, read and verify a file, publish one. On top of the [`Xrpl`](https://www.nuget.org/packages/Xrpl) package |
| `web/xrpl-file.js` | the same reading in dependency-free browser JavaScript |
| `tests/vectors/v1.json` | vectors both implementations are held to |
| `tools/XrplFile.Vectors` | regenerates the vectors from the .NET code |
| `app/XrplFile.App.Core` | models for the desktop application's screens — no MAUI, no UI |
| `app/XrplFile.App` | the desktop application itself: MAUI Blazor Hybrid, Windows only so far |

A command-line uploader, an HTTP gateway, a demo page and an embeddable `<xrpl-file>` element are
planned next. The desktop application below already does the rest: publishing a file, reclaiming a
throwaway account's reserve, and reading a link back.

## Reading a file (.NET)

```csharp
using Xrpl.Client;
using XrplFile;

IXrplClient client = new XrplClient("wss://xrplcluster.com");
await client.Connect();

XrplFileReader reader = new(new XrplClientLedgerSource(client));
ReadResult file = await reader.ReadAsync(XrplFileLink.Parse("xrpl-file://mainnet/…"), CancellationToken.None);

Console.WriteLine($"{file.Manifest.Type}, {file.Bytes.Length} bytes, root {file.Manifest.Root}");
```

## Reading a file (browser)

```js
import { LedgerClient, ENDPOINTS, XrplFileReader, parseLink } from './web/xrpl-file.js';

const link = parseLink('xrpl-file://mainnet/…');
const reader = new XrplFileReader(new LedgerClient(ENDPOINTS[link.network]));
const { manifest, bytes } = await reader.read(link, ({ chunksDone, chunksTotal }) => console.log(chunksDone, chunksTotal));
```

## Publishing a file (.NET)

Publishing is a session, not a single call: it prices the run before spending anything, writes a
state file as it goes, and hands back a report once the manifest is in the ledger. Underneath,
the file still moves one `AccountSet` per 1000 bytes, then a block list, then a read-back against
the whole file's Merkle root, and only then the manifest — a run that breaks before the manifest
lands leaves no link behind, whichever strategy is carrying the chunks.

```csharp
XrplWallet wallet = XrplWallet.FromSeed(Environment.GetEnvironmentVariable("XRPL_SEED")!);
XrplClientLedgerSource source = new(client);
XrplClientLedgerSink sink = new(client);
SessionOptions options = new() { StateDirectory = "runs", BudgetDrops = 5_000_000 };

// What it will cost, before any of it is spent.
PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, wallet.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

Console.WriteLine($"{estimate.Transactions} transactions, {estimate.RequiredDrops} drops needed, {estimate.SpendableDrops} spendable");

// The strategy's fee and the estimate's must be the same number, or the run is approved at a
// price nobody submits. StartAsync refuses the pair rather than letting that through, so the
// estimate's fee goes into the strategy that will do the submitting.
await using TicketStrategy strategy = new(sink, source, new WalletSigner(wallet),
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    wallet.ClassicAddress, keyReference: "my-key", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(
    new Progress<PublishProgress>(p => Console.WriteLine($"{p.Stage} {p.Done}/{p.Total}")), CancellationToken.None);

Console.WriteLine(report.Link);
```

The estimate judges the account by what it may actually spend rather than by its balance: one
whose trust lines, offers or leftover tickets already lock an owner reserve is refused before the
run starts, instead of after the chunks are paid for and the first `TicketCreate` comes back
`tecINSUFFICIENT_RESERVE`. `RequiredDrops` is still what the run needs; `SpendableDrops` is what
the account has to meet it with.

`TicketStrategy` mints tickets and rides them, so chunks go to the ledger in parallel rather than
one after another — on a standalone stand, 200 chunk memos landed in about 22 seconds on tickets
against 50-plus seconds projected for one at a time. `SequenceStrategy` is the slower alternative,
one `AccountSet` at a time on the account's own sequence; it needs no reserve beyond what the
account already holds, which is what to reach for when the account cannot carry a batch of
tickets.

The session writes a state file before the first transaction. If the process dies mid-run,
`PublicationSession.ResumeAsync` picks it back up from that file — but the file is where to look,
not what to believe: a resume reconciles against the ledger itself (the same `account_tx` sweep a
strategy uses) rather than trusting what the file last recorded, because a crash can fall between
a submission and its record. The state file never holds a seed; it holds a `keyReference` string
the caller chose, meaningful only to whatever key store the caller uses to resolve it back to a
signer.

### Publishing without a funded account

An application can publish on behalf of somebody who never hands it a seed. A key is generated
for this one run, its address is shown for the person to fund from a wallet they already trust,
and the library never asks for, holds, or moves the account the money came from — the whole
surface is *show this address, watch this address, spend from this key*.

```csharp
XrplWallet throwaway = XrplWallet.Generate();
ThrowawayAccount account = new(sink, source, new WalletSigner(throwaway));
long startedIn = await source.CurrentLedgerAsync(CancellationToken.None);

PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, throwaway.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

// account.Address (== request.QrPayload) goes on screen next to estimate.RequiredDrops, as text
// to copy and as a QR code to scan. The money may arrive from a phone a minute from now or an
// hour from now, so the wait has no deadline of its own.
FundingRequest request = await account.AwaitFundingAsync(estimate, TimeSpan.FromSeconds(5), CancellationToken.None);

await using TicketStrategy strategy = new(sink, source, new WalletSigner(throwaway),
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    throwaway.ClassicAddress, keyReference: "throwaway", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(null, CancellationToken.None);

// A proposal, read from whoever's payment funded the account — never assumed, and RefundAsync
// takes only what the caller actually passes, so a person confirms it or types another address.
string funder = await account.FunderAsync(startedIn, CancellationToken.None)
    ?? throw new InvalidOperationException("nobody has paid this account yet");

RefundResult refund = await account.RefundAsync(funder, CancellationToken.None);
```

`RefundAsync` sends back everything the account may actually spend — the balance less the fee
and less every reserve the account is still under. The base reserve always stays behind: getting
it back needs `AccountDelete`, and that refuses with `tecTOO_SOON` until the account's sequence
plus 254 ledgers has passed, counted against the last *validated* ledger index (rippled's own
rule reads `+ 255` against the ledger it is still building, one ahead of what a client can read —
see the design document for the one-ledger difference and how it was measured). A ticketed
transaction — everything this library sends once tickets are minted — never moves the account's
sequence at all, so a publication that ran entirely on tickets leaves the boundary exactly where
its last mint left it: nothing a caller submits afterwards brings it closer, only ledgers closing
does. `ThrowawayAccount.DeletableFromLedgerAsync` reports the ledger index at which deletion (not
implemented by this library) would stop refusing.

## Keys and the vault (.NET)

Every sample above needs a wallet from somewhere, and `XrplFile.Keys` is where one actually lives
between publications. It is one file, encrypted under a single master password, holding as many
entries as an application cares to put in it. The file is deliberately portable: a backup is a
copy of it, and a machine that dies does not take the keys with it. That is the whole point of
the one-factor design — nothing about the file depends on the machine that wrote it, except
"remember on this device", which is convenience layered on top and simply inert on a copy.

The other side of that portability is that a forgotten master password loses every key that was
never taken out of the vault another way. `IKeyVault.ExportAsync` is that other way: it reveals
one entry and seals it into a password-protected container — bytes an application can write to a
file or turn into a QR code. It writes nothing itself, and deliberately does not mark the entry
`Exported`: that flag means the secret survives somewhere other than this vault, and only the
caller that actually put the bytes somewhere knows whether it does. Record it with
`UpdateAsync(reference, new VaultEntryEdit(Exported: true), ...)` once the write has succeeded,
and the vault can then warn "these keys exist nowhere else" before the day the password is
actually forgotten instead of after. Erring the other way — marking a container nobody kept —
silences that warning exactly where it was needed.

A vault that opens with `VaultUnlockResult.VaultDegraded` is open — listing, revealing and
exporting all keep working, which is how the keys that are still intact get rescued. What it will
not do is write. The record's integrity code disagreeing with its own contents is the only evidence
there is that an entry was removed from the file or that its bytes were edited under it, and every
write recomputes that code, so the first write is refused with `VaultError.Degraded` until the
application has told the user and called `AcknowledgeDamageAsync`. Nothing is written by the
acknowledgement itself, so a vault locked and opened again reports `VaultDegraded` once more.

Signing goes through the vault rather than through a wallet held in memory. `VaultSigner`
implements the same `ITransactionSigner` seam `WalletSigner` does in the samples above: it asks
the vault to reveal its entry for exactly one signature and lets the derived wallet go immediately
afterwards, so locking the vault genuinely takes the signing key away rather than only hiding a
reference to one still sitting in memory. A vault that locks mid-publication — an idle
`VaultAutoLock`, or an explicit `LockAsync` from elsewhere in the application — fails the very
next signature with `VaultError.Locked`, and that failure is recovered exactly the way any
interrupted publication is recovered (see "Publishing a file" above): unlock the vault and resume
the session from its state file, rather than starting the run over and paying for its chunks a
second time.

```csharp
using System.Text;
using XrplFile.Keys;

await using KeyVault vault = new("vault.json", new Argon2idKdf());
await vault.CreateAsync(Encoding.UTF8.GetBytes("a strong master password"), CancellationToken.None);

XrplWallet generated = XrplWallet.Generate();
string reference = await vault.StoreAsync(
    new NewVaultEntry(VaultEntryKind.Generated, "run one", "mainnet", generated.ClassicAddress, generated.Seed),
    CancellationToken.None);

// `reference` is what a publication's state file records as its KeyReference — never a seed.
// Nothing about `generated` itself has to survive past this call.
```

Publishing then looks exactly like "Publishing a file" above, with the wallet replaced by a
signer over the vault entry `reference` names:

```csharp
using XrplFile.Keys;

await using KeyVault vault = new("vault.json", new Argon2idKdf());
VaultUnlockResult unlocked = await vault.UnlockAsync(
    Encoding.UTF8.GetBytes("a strong master password"), CancellationToken.None);
if (unlocked != VaultUnlockResult.Success)
{
    throw new InvalidOperationException($"the vault did not open: {unlocked}");
}

await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(15), TimeProvider.System);
VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, CancellationToken.None, autoLock);

XrplClientLedgerSource source = new(client);
XrplClientLedgerSink sink = new(client);
SessionOptions options = new() { StateDirectory = "runs", BudgetDrops = 5_000_000 };

PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, signer.Account, new FileInfo("cover.png").Length, options, CancellationToken.None);

await using TicketStrategy strategy = new(sink, source, signer,
    new TicketStrategyOptions { FeeDrops = (int)estimate.FeeDropsPerTransaction });
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    signer.Account, keyReference: reference, "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(null, CancellationToken.None);
```

`autoLock.Touch()` is called after every signature `VaultSigner` makes, so a run signing
continuously for tens of minutes never locks itself out mid-run even while `VaultAutoLock`'s idle
window keeps a machine left alone from staying unlocked forever. It is optional: a `VaultSigner`
built with none touches nothing, and the vault only ever locks when something calls `LockAsync`.

## The desktop application

`app/XrplFile.App` is a MAUI Blazor Hybrid application, Windows only for now, built on
`XrplFile.Keys`. It is the first slice of a larger application (design in
[docs/superpowers/specs](docs/superpowers/specs)): today it creates and unlocks a vault, lists keys,
adds one by generating it, importing a typed seed, or opening a backup container and choosing a key
out of it, exports and reveals a seed, changes the master password, turns "remember on this device"
on and off, deletes a key, and reports damage. It also spends XRP: the Publish tab writes a file into
the ledger from a key held in the vault, with an estimate shown before anything is signed and a
publications list kept afterward; the Accounts tab reclaims a throwaway account's reserve once the
account is old enough for the ledger to allow deleting it; and the Read tab takes an `xrpl-file://`
link — pasted in, or reached with one click beside a row of that publications list — shows the name,
size and type its manifest describes before downloading anything, then reads it block by block with
progress and cancellation and saves the verified result wherever the person chooses. Nothing here
reimplements `XrplFile.Core`'s own reading or writing; the samples above still show what each tab
does under the hood, for anyone driving the library directly instead.

Every key the application manages lives in one vault file, encrypted under a single master password
chosen when the vault is created. The file is deliberately portable: a backup is a copy of it, and a
machine that dies does not take the keys with it. The other side of that is a forgotten master
password loses every key that was never taken out of the vault another way — which is exactly why
exporting exists, and why the key list marks a generated key that has never been exported: that flag
is how the application shows which throwaway accounts exist nowhere else, before the day the
password is the only thing standing between someone and them.

Signing goes through the vault rather than through a wallet held in memory, so locking it takes the
signing key away for real, not just a reference to one still resident. A vault that locks in the
middle of a publication fails the very next signature, and that failure is recovered the same way
any interrupted publication is: unlock the vault and resume the session from its state file, rather
than starting the run over and paying for its chunks a second time.

A container exported here can be imported into the Wallet application, and a container written there
can be imported here: the export format is the Wallet project's own — `"SBW1"`, PBKDF2-HMAC-SHA256
at up to 1,000,000 iterations — adopted so that a key is not locked into whichever of the two
products happened to create it. The vault file itself is untouched by that and stays Argon2id; the
container is a different artifact, the one that actually travels.

### Building and running it

.NET 10 SDK with the `maui-windows` workload:

```bash
dotnet workload install maui-windows
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
dotnet run --project app/XrplFile.App/XrplFile.App.csproj
```

Windows is the only target this repository builds or tests. `XrplFile.App.Core` carries no
Windows-specific code — every decision the screens make lives there and is covered by
`XrplFile.App.Core.Tests` with no operating system in the loop — but the two places the platform is
unavoidable, "remember on this device" and the clipboard and file-picker services, have only a
Windows implementation so far. CI mirrors this split: the Linux jobs build and test everything in
`XrplFile.Libraries.slnf`, and a separate `windows-latest` job builds `XrplFile.App.csproj` itself,
with no tests of its own to run — its job is proving the markup still compiles against the models.

## Building and testing

.NET 10 SDK and Node 22+.

```bash
dotnet build XrplFile.Libraries.slnf
dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.Keys.Tests -- -trait- "Category=Integration"
cd web && node --test test/*.test.mjs
```

`XrplFile.Libraries.slnf` is a solution filter over `XrplFile.sln` that excludes `XrplFile.App`
(`net10.0-windows10.0.19041.0`) — the only project in the solution that cannot build outside
Windows. `dotnet build XrplFile.sln` itself now needs Windows and the `maui-windows` workload for
that reason; see "The desktop application" above for building and running `XrplFile.App` itself.

`dotnet test` does not run xunit.v3 on the .NET 10 SDK: the test project is an executable and
is driven with `dotnet run`, taking xunit's own filters (`-class`, `-method`, `-trait`, `-trait-`)
after the `--` separator instead of `dotnet test`'s `--filter`.

Integration tests need a standalone rippled, brought up with the Compose file in `.ci-config`.
They read the node's WebSocket URL from `XRPL_FILE_NODE_URL`, defaulting to `ws://localhost:6006`
(the port the Compose file publishes):

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml up -d
dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml down
```

If ports 5005/5006/6006/6007 are already held by another stand on the same host, layer
`.ci-config/docker-compose.local-ports.yml` on top to move this repo's rippled to
15005/15006/16006/16007, and point the tests at the moved WebSocket port:

```bash
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
XRPL_FILE_NODE_URL=ws://localhost:16006 dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration"
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down
```

To regenerate the vectors after a deliberate change to the format:

```bash
dotnet run --project tools/XrplFile.Vectors -- tests/vectors/v1.json
```

## Licence

MIT.
