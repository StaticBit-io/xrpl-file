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

A command-line uploader, an HTTP gateway, a demo page and an embeddable `<xrpl-file>` element
are planned next.

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

Console.WriteLine($"{estimate.Transactions} transactions, {estimate.RequiredDrops} drops needed, {estimate.BalanceDrops} held");

await using TicketStrategy strategy = new(sink, source, wallet);
PublicationSession session = await PublicationSession.StartAsync(
    strategy, source, File.ReadAllBytes("cover.png"), "image/png", "cover.png",
    wallet.ClassicAddress, keyReference: "my-key", "mainnet", options, CancellationToken.None);

PublicationReport report = await session.RunAsync(
    new Progress<PublishProgress>(p => Console.WriteLine($"{p.Stage} {p.Done}/{p.Total}")), CancellationToken.None);

Console.WriteLine(report.Link);
```

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
ThrowawayAccount account = new(sink, source, throwaway);
long startedIn = await source.CurrentLedgerAsync(CancellationToken.None);

PublicationEstimate estimate = await PublicationSession.EstimateAsync(
    source, throwaway.ClassicAddress, new FileInfo("cover.png").Length, options, CancellationToken.None);

// account.Address (== request.QrPayload) goes on screen next to estimate.RequiredDrops, as text
// to copy and as a QR code to scan. The money may arrive from a phone a minute from now or an
// hour from now, so the wait has no deadline of its own.
FundingRequest request = await account.AwaitFundingAsync(estimate, TimeSpan.FromSeconds(5), CancellationToken.None);

await using TicketStrategy strategy = new(sink, source, throwaway);
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

## Building and testing

.NET 10 SDK and Node 22+.

```bash
dotnet build XrplFile.sln
dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"
cd web && node --test test/*.test.mjs
```

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
