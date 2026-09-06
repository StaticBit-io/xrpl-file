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

```csharp
XrplWallet wallet = XrplWallet.FromSeed(Environment.GetEnvironmentVariable("XRPL_SEED")!);
XrplFileWriter writer = new(new XrplClientLedgerSink(client), new XrplClientLedgerSource(client));

PublishResult published = await writer.PublishAsync(
    File.ReadAllBytes("cover.png"), "image/png", "cover.png", wallet, "mainnet", progress: null, CancellationToken.None);

Console.WriteLine(published.Link);
```

The writer sends one `AccountSet` per 1000 bytes, then the block list, reads everything back,
and only then writes the manifest. A run that breaks half-way leaves no link behind.

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
