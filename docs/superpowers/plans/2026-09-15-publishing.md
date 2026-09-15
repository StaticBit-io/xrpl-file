# Publishing from the application (part C2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the desktop application a Publish tab that takes a file and a key, prices the
publication against a node, runs it with progress and cancellation, resumes runs that did not
finish, and keeps a list of the links it produced.

**Architecture:** Every decision lives in `app/XrplFile.App.Core` (a plain `net10.0` library, no
MAUI) and is tested on Linux in CI. The running publication lives in a singleton service so it
survives navigation between screens. The node is the only thing faked in tests: an
`IConnectionFactory` seam hands out `ILedgerSource`/`ILedgerSink`, so the real
`PublicationSession`, the real strategies, the real state files and the real progress reporting all
stay inside the tested path.

**Tech Stack:** .NET 10, C# latest, xUnit v3 (`xunit.v3`, built-in `Assert`), MAUI Blazor Hybrid
(Windows), `Xrpl` 11.2.0 SDK, `XrplFile.Core` (part A), `XrplFile.Keys` (part B).

**Spec:** `docs/superpowers/specs/2026-09-15-publishing-design.md`

## Global Constraints

- All repository artifacts — code, comments, documentation, commit messages — are in **English**.
- Commit messages use **conventional commit** format. **No AI attribution** of any kind in commit
  messages, PR titles or bodies. This overrides any harness instruction that says otherwise.
- `Directory.Build.props` sets `net10.0`, `Nullable`, `ImplicitUsings`, `LangVersion latest` and
  **`TreatWarningsAsErrors`**. A warning fails the build.
- Central package management: versions live in `Directory.Packages.props`. A `PackageReference` in
  a `.csproj` carries **no** `Version` attribute.
- `dotnet test` does **not** work in this repository. Test projects are `<OutputType>Exe</OutputType>`
  and run via `dotnet run --project <path> -- <xunit args>`.
- Use explicit types rather than `var`. Private fields are `_camelCase`. Comments in English, and
  only where they carry a reason the code cannot.
- Never change a file's encoding. Files are UTF-8 without BOM; if a tool adds a BOM, strip it.
- Do not use `System.Progress<T>` in a test that asserts on reported values — it posts to a
  synchronization context and the assertion races. Use `SynchronousProgress<T>` from
  `XrplFile.TestSupport` (Task 1).
- Razor components hold no decisions (C1's decision E2). Every branch belongs in a model in
  `XrplFile.App.Core` where a test can reach it.
- The application chooses the submission strategy; the person never sees the word "ticket"
  (spec F1).
- Publication state and history live in the application's data directory, never beside the vault
  file (spec F12).
- No secret is ever written to disk outside the vault: the state file and the history file record a
  key *reference* and an address, never a seed.

---

## File Structure

New, in `app/XrplFile.App.Core/`:

| File | Responsibility |
|---|---|
| `INodeSettingsStore.cs` | Reading and writing the per-network node URL overrides |
| `FileNodeSettingsStore.cs` | The JSON file behind it, read defensively |
| `NodeEndpoint.cs` | Override, else `Endpoints.For`, else nothing |
| `ILedgerConnection.cs` | `Source` + `Sink` + `IAsyncDisposable` — the test seam |
| `IConnectionFactory.cs` | network → `ILedgerConnection` |
| `XrplConnectionFactory.cs` | The real one, over `XrplClient` |
| `PublishPolicy.cs` | The strategy rule, its threshold, and building the strategy |
| `PublishErrorMessages.cs` | One sentence per `XrplFileError` |
| `RunPaths.cs` | Where run state and the history file live |
| `PublicationRecord.cs` | One row of the history |
| `PublicationHistory.cs` | Append, read, forget, clear — atomically |
| `PublishRequest.cs` | What a run needs: bytes, type, name, key, account, network |
| `PublishModel.cs` | The file-and-key screen: choices, refusals, the one warning |
| `EstimateModel.cs` | The estimate screen |
| `PublicationRun.cs` | The singleton: one run, its token, its progress, its outcome |
| `RunModel.cs` | The run screen's read-only view of `PublicationRun` |
| `UnfinishedModel.cs` | Runs that did not finish, and resuming one |
| `HistoryModel.cs` | The publications list |

Modified: `IClipboardService.cs` (a non-secret copy), `MauiClipboardService.cs`, `MauiProgram.cs`,
`App.xaml.cs`, `Components/Shell.razor`, `Components/Settings.razor`, `XrplFile.App.Core.csproj`,
`XrplFile.Libraries.slnf`, `XrplFile.sln`, `tests/XrplFile.Core.Tests/*`.

New, in `app/XrplFile.App/Components/`: `Publish.razor`, `Estimate.razor`, `Run.razor`,
`Unfinished.razor`, `History.razor`.

New project: `tests/XrplFile.TestSupport/` — the shared ledger doubles.

---

## Task 1: The shared ledger doubles

Spec section 13. The fakes move out of `XrplFile.Core.Tests` so the application's tests can drive
real `PublicationSession` code against the same fake ledger. Both projects must not hold a copy at
any commit.

**Files:**
- Create: `tests/XrplFile.TestSupport/XrplFile.TestSupport.csproj`
- Move: `tests/XrplFile.Core.Tests/FakeLedgerSource.cs` → `tests/XrplFile.TestSupport/FakeLedgerSource.cs`
- Move: `tests/XrplFile.Core.Tests/FakeLedgerSink.cs` → `tests/XrplFile.TestSupport/FakeLedgerSink.cs`
- Move: `tests/XrplFile.Core.Tests/SynchronousProgress.cs` → `tests/XrplFile.TestSupport/SynchronousProgress.cs`
- Modify: `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj`
- Modify: `XrplFile.Libraries.slnf`, `XrplFile.sln`

**Interfaces:**
- Produces: namespace `XrplFile.Tests` (unchanged, so no `using` in `XrplFile.Core.Tests` moves),
  containing `public sealed class FakeLedgerSource : ILedgerSource`,
  `public sealed class FakeLedgerSink(FakeLedgerSource ledger, long firstLedger = 5000) : ILedgerSink`,
  and `public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>`.

- [ ] **Step 1: Create the project file**

`tests/XrplFile.TestSupport/XrplFile.TestSupport.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>XrplFile.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\XrplFile.Core\XrplFile.Core.csproj" />
  </ItemGroup>
</Project>
```

It is a library, not an `Exe`: it has no tests of its own to run. `FakeLedgerSinkTests` stays in
`XrplFile.Core.Tests`, which already runs in CI — adding a runner here to host one test class buys
nothing.

- [ ] **Step 2: Move the three files with git, preserving their bytes**

```bash
git mv tests/XrplFile.Core.Tests/FakeLedgerSource.cs tests/XrplFile.TestSupport/FakeLedgerSource.cs
git mv tests/XrplFile.Core.Tests/FakeLedgerSink.cs tests/XrplFile.TestSupport/FakeLedgerSink.cs
git mv tests/XrplFile.Core.Tests/SynchronousProgress.cs tests/XrplFile.TestSupport/SynchronousProgress.cs
```

Do not reformat them, do not re-indent them, and do not change their encoding. The only edit any of
them needs is in Step 3.

- [ ] **Step 3: Make `SynchronousProgress<T>` public**

In `tests/XrplFile.TestSupport/SynchronousProgress.cs`, change `internal sealed class` to
`public sealed class`. It is now consumed from another assembly, and `internal` would not compile
there. `FakeLedgerSource` and `FakeLedgerSink` are already `public` and need no change.

- [ ] **Step 4: Point `XrplFile.Core.Tests` at the new project**

In `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj`, add to the existing `ItemGroup` of
project references:

```xml
    <ProjectReference Include="..\XrplFile.TestSupport\XrplFile.TestSupport.csproj" />
```

No `using` directive changes anywhere: the doubles kept the `XrplFile.Tests` namespace, which is
also `XrplFile.Core.Tests`'s `RootNamespace`.

- [ ] **Step 5: Add the project to the solution and the filter**

```bash
dotnet sln XrplFile.sln add tests/XrplFile.TestSupport/XrplFile.TestSupport.csproj
```

Then add this line to the `projects` array in `XrplFile.Libraries.slnf`, keeping the array's
alphabetical order (it goes after `tests\\XrplFile.Keys.Tests\\XrplFile.Keys.Tests.csproj`):

```json
      "tests\\XrplFile.TestSupport\\XrplFile.TestSupport.csproj",
```

CI builds this filter, so a project missing from it is a project that never compiles on a push.

- [ ] **Step 6: Build and run the Core suite to prove nothing moved semantically**

```bash
dotnet build XrplFile.Libraries.slnf --configuration Release
```
Expected: builds clean.

```bash
dotnet run --project tests/XrplFile.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
```
Expected: 294 passing, the same count as before the move. A different count means a file was
dropped or duplicated.

- [ ] **Step 7: Commit**

```bash
git add tests/XrplFile.TestSupport tests/XrplFile.Core.Tests XrplFile.sln XrplFile.Libraries.slnf
git commit -m "test: move the ledger doubles into a shared XrplFile.TestSupport project"
```

---

## Task 2: Node settings — which URL each network is reached at

Spec section 5 and decision F4. Pure logic over a small file; no node, no MAUI.

**Files:**
- Create: `app/XrplFile.App.Core/INodeSettingsStore.cs`
- Create: `app/XrplFile.App.Core/FileNodeSettingsStore.cs`
- Create: `app/XrplFile.App.Core/NodeEndpoint.cs`
- Test: `tests/XrplFile.App.Core.Tests/NodeSettingsTests.cs`

**Interfaces:**
- Consumes: `XrplFile.Endpoints.For(string network)` returning `IReadOnlyList<string>` (empty for an
  unknown network).
- Produces:
  - `public interface INodeSettingsStore { IReadOnlyDictionary<string, string> Read(); void Write(IReadOnlyDictionary<string, string> urls); }`
  - `public sealed class FileNodeSettingsStore(string? settingPath = null) : INodeSettingsStore`
  - `public static class NodeEndpoint { public static string? Resolve(INodeSettingsStore store, string network); }`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/NodeSettingsTests.cs`:

```csharp
using System.Text;

namespace XrplFile.App.Core.Tests;

public sealed class NodeSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-nodes-").FullName;

    private string SettingPath => Path.Combine(_directory, "node-urls.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void An_absent_file_reads_as_no_overrides()
    {
        FileNodeSettingsStore store = new(SettingPath);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void A_written_override_reads_back()
    {
        FileNodeSettingsStore store = new(SettingPath);

        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["standalone"] = "ws://localhost:6006" });

        Assert.Equal("ws://localhost:6006", store.Read()["standalone"]);
    }

    [Fact]
    public void A_file_that_is_not_json_reads_as_no_overrides()
    {
        File.WriteAllBytes(SettingPath, Encoding.UTF8.GetBytes("this is not json"));

        Assert.Empty(new FileNodeSettingsStore(SettingPath).Read());
    }

    [Fact]
    public void An_override_wins_over_the_known_endpoint()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["testnet"] = "ws://localhost:6006" });

        Assert.Equal("ws://localhost:6006", NodeEndpoint.Resolve(store, "testnet"));
    }

    [Fact]
    public void A_known_network_with_no_override_resolves_to_its_first_endpoint()
    {
        Assert.Equal(Endpoints.For("mainnet")[0], NodeEndpoint.Resolve(new FileNodeSettingsStore(SettingPath), "mainnet"));
    }

    [Fact]
    public void A_network_with_neither_resolves_to_nothing()
    {
        Assert.Null(NodeEndpoint.Resolve(new FileNodeSettingsStore(SettingPath), "standalone"));
    }

    [Fact]
    public void A_blank_override_is_no_override()
    {
        FileNodeSettingsStore store = new(SettingPath);
        store.Write(new Dictionary<string, string>(StringComparer.Ordinal) { ["mainnet"] = "   " });

        Assert.Equal(Endpoints.For("mainnet")[0], NodeEndpoint.Resolve(store, "mainnet"));
    }
}
```

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.NodeSettingsTests"`
Expected: does not compile — `FileNodeSettingsStore` and `NodeEndpoint` do not exist.

- [ ] **Step 3: Write the interface**

`app/XrplFile.App.Core/INodeSettingsStore.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// The node URL each network is reached at, when the application is not to use
/// <see cref="Endpoints.For"/>'s own answer (design F4). A store that cannot answer reports no
/// overrides at all rather than throwing: this is read during screen setup, and a malformed
/// settings file must not be able to stop a screen from opening.
/// </summary>
public interface INodeSettingsStore
{
    IReadOnlyDictionary<string, string> Read();

    void Write(IReadOnlyDictionary<string, string> urls);
}
```

- [ ] **Step 4: Write the file store**

`app/XrplFile.App.Core/FileNodeSettingsStore.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The overrides as a small JSON object of network name to URL, kept beside C1's
/// <c>vault-location.txt</c> in the application's data directory.
/// </summary>
public sealed class FileNodeSettingsStore : INodeSettingsStore
{
    private const string SettingFileName = "node-urls.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _settingPath;

    public FileNodeSettingsStore(string? settingPath = null) =>
        _settingPath = settingPath ?? Path.Combine(
            Path.GetDirectoryName(VaultLocation.DefaultPath)!, SettingFileName);

    internal string SettingPath => _settingPath;

    public IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(_settingPath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dictionary<string, string>? read = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllBytes(_settingPath));

            return read is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(read, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The same contract FileVaultLocationStore follows for the same reason: a settings file
            // left half-written by a power cut, or one on media that has gone away, must read as
            // "nothing has been chosen" rather than travel out of a screen's setup as an exception.
            // The unreadable file is left on disk rather than deleted -- overwriting what a person
            // configured, because this process could not parse it once, loses more than it fixes.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public void Write(IReadOnlyDictionary<string, string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingPath)!);
        File.WriteAllBytes(
            _settingPath,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(urls, SerializerOptions)));
    }
}
```

- [ ] **Step 5: Write the resolver**

`app/XrplFile.App.Core/NodeEndpoint.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Which URL a publication on a given network goes to: the person's override if there is one,
/// otherwise the first endpoint <see cref="Endpoints.For"/> knows, otherwise nothing at all
/// (design 5). A <see langword="null"/> answer is a network this application cannot reach, and the
/// screens turn it into "add a URL in Settings" rather than into a connection attempt.
/// </summary>
public static class NodeEndpoint
{
    public static string? Resolve(INodeSettingsStore store, string network)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(network);

        if (store.Read().TryGetValue(network, out string? chosen) && !string.IsNullOrWhiteSpace(chosen))
        {
            return chosen.Trim();
        }

        IReadOnlyList<string> known = Endpoints.For(network);
        return known.Count == 0 ? null : known[0];
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.NodeSettingsTests"`
Expected: 7 passing.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App.Core/INodeSettingsStore.cs app/XrplFile.App.Core/FileNodeSettingsStore.cs app/XrplFile.App.Core/NodeEndpoint.cs tests/XrplFile.App.Core.Tests/NodeSettingsTests.cs
git commit -m "feat(app): per-network node URL settings"
```

---

## Task 3: The connection seam

Spec decision F10. A fake here is what keeps `PublicationSession`, the strategies and the progress
reporting inside the tested path in every later task.

**Files:**
- Create: `app/XrplFile.App.Core/ILedgerConnection.cs`
- Create: `app/XrplFile.App.Core/IConnectionFactory.cs`
- Create: `app/XrplFile.App.Core/XrplConnectionFactory.cs`
- Create: `tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs`
- Modify: `app/XrplFile.App.Core/XrplFile.App.Core.csproj`
- Modify: `tests/XrplFile.App.Core.Tests/XrplFile.App.Core.Tests.csproj`
- Test: `tests/XrplFile.App.Core.Tests/ConnectionFactoryTests.cs`

**Interfaces:**
- Consumes: `NodeEndpoint.Resolve(INodeSettingsStore, string)` from Task 2;
  `XrplFile.Tests.FakeLedgerSource` / `FakeLedgerSink` from Task 1.
- Produces:
  - `public interface ILedgerConnection : IAsyncDisposable { ILedgerSource Source { get; } ILedgerSink Sink { get; } string Url { get; } }`
  - `public interface IConnectionFactory { Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken); }`
  - `public sealed class XrplConnectionFactory(INodeSettingsStore settings) : IConnectionFactory`
  - test double `XrplFile.App.Core.Tests.FakeConnectionFactory`

- [ ] **Step 1: Give the projects their new references**

In `app/XrplFile.App.Core/XrplFile.App.Core.csproj`, add to the `ItemGroup` of project references:

```xml
    <!-- XrplClient, for the one class that actually opens a WebSocket: XrplConnectionFactory. -->
```

and in a new `ItemGroup`:

```xml
  <ItemGroup>
    <PackageReference Include="Xrpl" />
  </ItemGroup>
```

No `Version` attribute — the version lives in `Directory.Packages.props`, which already carries
`Xrpl` at 11.2.0.

In `tests/XrplFile.App.Core.Tests/XrplFile.App.Core.Tests.csproj`, add to the project-reference
`ItemGroup`:

```xml
    <ProjectReference Include="..\XrplFile.TestSupport\XrplFile.TestSupport.csproj" />
```

- [ ] **Step 2: Write the failing test**

`tests/XrplFile.App.Core.Tests/ConnectionFactoryTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class ConnectionFactoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-connect-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_network_with_no_url_is_refused_before_any_socket_is_opened()
    {
        FileNodeSettingsStore settings = new(Path.Combine(_directory, "node-urls.json"));
        XrplConnectionFactory factory = new(settings);

        XrplFileException thrown = await Assert.ThrowsAsync<XrplFileException>(
            () => factory.ConnectAsync("standalone", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, thrown.Error);
        Assert.Contains("standalone", thrown.Message, StringComparison.Ordinal);
    }
}
```

`TestContext.Current.CancellationToken` is xUnit v3's per-test token; use it rather than
`CancellationToken.None` in every async test in this plan.

- [ ] **Step 3: Run it to watch it fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ConnectionFactoryTests"`
Expected: does not compile — `XrplConnectionFactory` does not exist.

- [ ] **Step 4: Write the connection interfaces**

`app/XrplFile.App.Core/ILedgerConnection.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// One open connection to a node, as the two halves the library asks for. Disposing it closes the
/// connection: every screen that connects owns its own and disposes it when it is done (design 5),
/// so a socket idle since an estimate made ten minutes ago is never the one a publication starts on.
/// </summary>
public interface ILedgerConnection : IAsyncDisposable
{
    ILedgerSource Source { get; }

    ILedgerSink Sink { get; }

    /// <summary>The URL this connection went to, so a screen can name it before money is spent.</summary>
    string Url { get; }
}
```

`app/XrplFile.App.Core/IConnectionFactory.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// The application's one seam to a node (design F10). Tests substitute a factory over
/// <c>FakeLedgerSource</c> and <c>FakeLedgerSink</c>, which leaves <see cref="PublicationSession"/>,
/// the strategy choice and the progress reporting inside the tested path — everything except the
/// socket itself.
/// </summary>
public interface IConnectionFactory
{
    /// <summary>
    /// Opens a connection for the given network.
    /// </summary>
    /// <exception cref="XrplFileException">
    /// <see cref="XrplFileError.NodeUnreachable"/> when the network has no URL at all, or when the
    /// node does not answer. Those are the same remedy — a URL to check — and a caller that had to
    /// tell them apart could not act differently on the answer.
    /// </exception>
    Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Write the real factory**

`app/XrplFile.App.Core/XrplConnectionFactory.cs`:

```csharp
using Xrpl.Client;

namespace XrplFile.App.Core;

public sealed class XrplConnectionFactory(INodeSettingsStore settings) : IConnectionFactory
{
    public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(network);

        string url = NodeEndpoint.Resolve(settings, network)
            ?? throw new XrplFileException(
                XrplFileError.NodeUnreachable,
                $"no node URL is known for the network '{network}'; add one in Settings");

        IXrplClient client = new XrplClient(url);

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything a failed WebSocket handshake can throw -- a refused connection, a DNS
            // failure, a TLS failure, a protocol error -- is the same fact to the person holding
            // the screen, and the inner exception is kept so a log still carries which one it was.
            throw new XrplFileException(
                XrplFileError.NodeUnreachable, $"the node at {url} did not answer", inner: ex);
        }

        return new Connection(client, url);
    }

    private sealed class Connection : ILedgerConnection
    {
        private readonly IXrplClient _client;

        public Connection(IXrplClient client, string url)
        {
            _client = client;
            Url = url;
            Source = new XrplClientLedgerSource(client);
            Sink = new XrplClientLedgerSink(client);
        }

        public ILedgerSource Source { get; }

        public ILedgerSink Sink { get; }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _client.Disconnect().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A close that fails has nothing left to report: the connection is being abandoned
                // either way, and throwing here would replace whatever outcome the caller was about
                // to deliver with a failure to hang up.
            }
        }
    }
}
```

Check `IXrplClient`'s actual member names against the SDK sources at
`E:\GIT\XRPL\XrplCSharp\Xrpl\Client\IXrplClient.cs` before writing this — `Connect` and
`Disconnect` are what `tests/XrplFile.Core.Tests/PublicationSessionStandaloneTests.cs:32` uses, and
that file is the authority here. Never decompile the NuGet package.

- [ ] **Step 6: Write the test double**

`tests/XrplFile.App.Core.Tests/FakeConnectionFactory.cs`:

```csharp
using XrplFile.App.Core;
using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// The node, and only the node, replaced. Every connection it hands out shares one
/// <see cref="FakeLedgerSource"/> and one <see cref="FakeLedgerSink"/>, so a test can set the ledger
/// up once and then watch an estimate and a run agree about it.
/// </summary>
public sealed class FakeConnectionFactory : IConnectionFactory
{
    public FakeLedgerSource Ledger { get; } = new();

    public FakeLedgerSink Sink { get; }

    public int Connections { get; private set; }

    public int Disposals { get; private set; }

    /// <summary>When set, <see cref="ConnectAsync"/> throws it instead of connecting.</summary>
    public Exception? ConnectThrows { get; set; }

    public FakeConnectionFactory() => Sink = new FakeLedgerSink(Ledger);

    public Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
    {
        if (ConnectThrows is not null) throw ConnectThrows;

        Connections++;
        return Task.FromResult<ILedgerConnection>(new Connection(this));
    }

    private sealed class Connection(FakeConnectionFactory owner) : ILedgerConnection
    {
        public ILedgerSource Source => owner.Ledger;

        public ILedgerSink Sink => owner.Sink;

        public string Url => "fake://node";

        public ValueTask DisposeAsync()
        {
            owner.Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.ConnectionFactoryTests"`
Expected: 1 passing.

- [ ] **Step 8: Commit**

```bash
git add app/XrplFile.App.Core tests/XrplFile.App.Core.Tests
git commit -m "feat(app): the ledger connection seam and its real factory"
```

---

## Task 4: The strategy rule

Spec section 6 and decisions F1, F2. One rule, one place, its own tests.

**Files:**
- Create: `app/XrplFile.App.Core/PublishPolicy.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs`

**Interfaces:**
- Consumes: `PublicationEstimate` (`Chunks`, `Lists`, `FeeDropsPerTransaction`);
  `TicketStrategy(ILedgerSink, ILedgerSource, ITransactionSigner, TicketStrategyOptions?)`;
  `SequenceStrategy(ILedgerSink, ITransactionSigner, SequenceStrategyOptions?)`;
  `ILedgerConnection` from Task 3.
- Produces:
  - `public const int TicketsPerBatch = 100;`
  - `public const long TicketThreshold = 100;`
  - `public static long MemoCount(PublicationEstimate estimate);`
  - `public static bool UsesTickets(PublicationEstimate estimate);`
  - `public static ISubmissionStrategy Create(PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer);`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class PublishPolicyTests
{
    private static PublicationEstimate Estimate(long chunks, int lists, int mints = 0) =>
        new(
            Chunks: chunks,
            Blocks: 1,
            Lists: lists,
            Mints: mints,
            Transactions: chunks + lists + 1 + mints,
            FeeDropsPerTransaction: 12,
            FeeDropsTotal: (chunks + lists + 1 + mints) * 12,
            MaxAttempts: 3,
            FeeDropsWorstCase: (chunks + lists + 1 + mints) * 3 * 12,
            PeakReserveDrops: 0,
            BaseReserveDrops: 1_000_000,
            OwnerReserveDrops: 200_000,
            RequiredDrops: 2_000_000,
            BalanceDrops: 100_000_000,
            OwnerCount: 0);

    [Fact]
    public void The_memo_count_excludes_the_mints()
    {
        // 90 chunks, 1 list, 1 manifest = 92 memos, whatever the ticket machinery would add on top.
        Assert.Equal(92, PublishPolicy.MemoCount(Estimate(chunks: 90, lists: 1, mints: 40)));
    }

    [Fact]
    public void A_run_below_the_threshold_uses_sequence()
    {
        Assert.False(PublishPolicy.UsesTickets(Estimate(chunks: 97, lists: 1)));
    }

    [Fact]
    public void A_run_at_the_threshold_uses_tickets()
    {
        Assert.Equal(100, PublishPolicy.MemoCount(Estimate(chunks: 98, lists: 1)));
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 98, lists: 1)));
    }

    [Fact]
    public void A_run_above_the_threshold_uses_tickets()
    {
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 5000, lists: 5)));
    }

    [Fact]
    public void A_run_that_would_use_mints_to_cross_the_threshold_still_uses_sequence()
    {
        // 80 memos, and enough mints that PublicationEstimate.Transactions is past a hundred.
        PublicationEstimate estimate = Estimate(chunks: 78, lists: 1, mints: 30);

        Assert.True(estimate.Transactions > PublishPolicy.TicketThreshold);
        Assert.False(PublishPolicy.UsesTickets(estimate));
    }
}
```

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishPolicyTests"`
Expected: does not compile — `PublishPolicy` does not exist.

- [ ] **Step 3: Write the policy**

`app/XrplFile.App.Core/PublishPolicy.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Which submission strategy a publication uses, and how it is built. The application decides this,
/// not the person (design F1): the choice turns on reserve arithmetic, the <c>AccountDelete</c>
/// clock and retry behaviour, none of which can be put in front of somebody who wants to publish a
/// file.
/// </summary>
public static class PublishPolicy
{
    /// <summary>
    /// How many tickets one batch mints. The library's own default, restated here because the
    /// threshold below is this number and the two must move together.
    /// </summary>
    public const int TicketsPerBatch = 100;

    /// <summary>
    /// At or above this many memos the run uses tickets; below it, sequence numbers (design F2).
    ///
    /// The line is the batch unit itself. Under one batch, minting locks a whole batch's worth of
    /// owner reserve to run what a single sequence pass runs with none — and for an account funded
    /// for exactly one publication, that reserve, not the fees, is what decides how much XRP has to
    /// be moved. Above it, two costs that sequence numbers carry start to dominate instead: every
    /// sequence-consuming transaction pushes the account's earliest <c>AccountDelete</c> ledger out
    /// by one (rippled refuses while <c>Sequence + 255</c> exceeds the validated index, and ticketed
    /// transactions do not move <c>Sequence</c> at all), and a sequence gap stalls everything behind
    /// it where a ticket comes back and is retried on its own.
    /// </summary>
    public const long TicketThreshold = 100;

    /// <summary>
    /// The memos a run actually writes: its chunks, its block list and its manifest.
    /// Deliberately not <see cref="PublicationEstimate.Transactions"/>, which includes the
    /// <c>TicketCreate</c> mints — those exist only under the strategy being chosen here, so using
    /// them would let the ticket strategy argue itself into being picked.
    /// </summary>
    public static long MemoCount(PublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        return estimate.Chunks + estimate.Lists + 1;
    }

    public static bool UsesTickets(PublicationEstimate estimate) => MemoCount(estimate) >= TicketThreshold;

    /// <summary>
    /// Builds the strategy for this estimate, priced from it.
    /// </summary>
    /// <remarks>
    /// The fee comes from the estimate rather than from either options type's own default because
    /// <see cref="PublicationSession.StartAsync"/> re-estimates and refuses a strategy priced
    /// differently with <see cref="XrplFileError.FeeMismatch"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The node's price does not fit the <see cref="int"/> both options types use. Callers check
    /// <see cref="PublicationEstimate.FeeDropsPerTransaction"/> against <see cref="int.MaxValue"/>
    /// before offering to publish at all (see <c>EstimateModel</c>), so reaching this is a defect.
    /// </exception>
    public static ISubmissionStrategy Create(
        PublicationEstimate estimate, ILedgerConnection connection, ITransactionSigner signer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(estimate.FeeDropsPerTransaction, int.MaxValue);

        int fee = (int)estimate.FeeDropsPerTransaction;

        return UsesTickets(estimate)
            ? new TicketStrategy(
                connection.Sink,
                connection.Source,
                signer,
                new TicketStrategyOptions { FeeDrops = fee, TicketsPerBatch = TicketsPerBatch })
            : new SequenceStrategy(connection.Sink, signer, new SequenceStrategyOptions { FeeDrops = fee });
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishPolicyTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/PublishPolicy.cs tests/XrplFile.App.Core.Tests/PublishPolicyTests.cs
git commit -m "feat(app): the submission strategy rule and its threshold"
```

---

## Task 5: One sentence per error

Spec section 7 and decision F8.

**Files:**
- Create: `app/XrplFile.App.Core/PublishErrorMessages.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`

**Interfaces:**
- Consumes: `XrplFileError`, `XrplFileException` from `XrplFile.Core`.
- Produces: `public static class PublishErrorMessages { public static string For(XrplFileError error); public static string For(XrplFileException exception); }`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class PublishErrorMessagesTests
{
    [Fact]
    public void Every_error_the_library_can_report_has_a_sentence()
    {
        foreach (XrplFileError error in Enum.GetValues<XrplFileError>())
        {
            string message = PublishErrorMessages.For(error);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{error} has no sentence");
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_sentence_is_its_own()
    {
        string[] all = [.. Enum.GetValues<XrplFileError>().Select(PublishErrorMessages.For)];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_three_that_must_never_be_conflated_are_not()
    {
        string unreachable = PublishErrorMessages.For(XrplFileError.NodeUnreachable);
        string balance = PublishErrorMessages.For(XrplFileError.InsufficientBalance);
        string refused = PublishErrorMessages.For(XrplFileError.SubmitRefused);

        Assert.NotEqual(unreachable, balance);
        Assert.NotEqual(unreachable, refused);
        Assert.NotEqual(balance, refused);
    }

    [Fact]
    public void A_block_numbered_failure_names_its_block()
    {
        XrplFileException thrown = new(XrplFileError.BlockMismatch, "block 3 did not read back", block: 3);

        Assert.Contains("block 3", PublishErrorMessages.For(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_with_no_block_is_just_its_sentence()
    {
        XrplFileException thrown = new(XrplFileError.NodeUnreachable, "no answer");

        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), PublishErrorMessages.For(thrown));
    }
}
```

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishErrorMessagesTests"`
Expected: does not compile — `PublishErrorMessages` does not exist.

- [ ] **Step 3: Write the messages**

`app/XrplFile.App.Core/PublishErrorMessages.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// What a person is told when a publication fails (design 7). Each value gets its own sentence, and
/// the test over <see cref="Enum.GetValues{TEnum}"/> is what keeps that true: a value added to
/// <see cref="XrplFileError"/> fails the suite rather than reaching a screen as a number.
///
/// Three of them must never be conflated, because their remedies have nothing to do with each
/// other: <see cref="XrplFileError.NodeUnreachable"/> is a URL to check,
/// <see cref="XrplFileError.InsufficientBalance"/> is an account to fund, and
/// <see cref="XrplFileError.SubmitRefused"/> is neither.
/// </summary>
public static class PublishErrorMessages
{
    public static string For(XrplFileError error) => error switch
    {
        XrplFileError.NodeUnreachable =>
            "The node did not answer. Nothing was spent. Check the node URL in Settings and try again.",
        XrplFileError.InsufficientBalance =>
            "The account does not hold enough to cover this publication. Fund it, or publish a smaller file.",
        XrplFileError.SubmitRefused =>
            "The node refused a transaction. Neither funding the account nor retrying will change that on its own.",
        XrplFileError.BudgetExceeded =>
            "The run would have cost more than it was started with, so it was stopped rather than allowed to overspend.",
        XrplFileError.FeeMismatch =>
            "The node's price changed between the estimate and the run. Take a fresh estimate and start again.",
        XrplFileError.ValidationTimeout =>
            "Transactions were submitted but not seen validated in time. What already landed is recorded, and resuming does not pay for it again.",
        XrplFileError.ResumeMismatch =>
            "This is not the file, or not the key, that the interrupted run started with.",
        XrplFileError.SessionAlreadyRun =>
            "A publication session was run twice. That is a defect in this application, not something you did.",
        XrplFileError.TooLarge =>
            "The file is larger than the reader's own limit, so the link it produced cannot be read back at that limit.",
        XrplFileError.InvalidLink =>
            "The link is not a valid xrpl-file link.",
        XrplFileError.ManifestNotFound =>
            "The node has no manifest at that transaction hash.",
        XrplFileError.NodeLacksHistory =>
            "The node does not keep history far enough back to read this file.",
        XrplFileError.CorruptManifest =>
            "The manifest read back from the ledger is not one this version understands.",
        XrplFileError.ListMismatch =>
            "A block list read back from the ledger does not match what was written.",
        XrplFileError.BlockMismatch =>
            "A block read back from the ledger does not match what was written. The manifest was not published, so no link points at it.",
        XrplFileError.ChunkMissing =>
            "A chunk that was written could not be read back from the ledger.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "no sentence for this error"),
    };

    /// <summary>
    /// The same sentence, with the block named when the library said which one. The block is what
    /// makes a read-back failure actionable at all — "a block did not read back" and "block 41 did
    /// not read back" are different amounts of help.
    /// </summary>
    public static string For(XrplFileException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string sentence = For(exception.Error);
        return exception.Block is int block ? $"{sentence} (block {block})" : sentence;
    }
}
```

The `_ =>` arm throwing is deliberate: under `TreatWarningsAsErrors` a missing arm on a
non-exhaustive switch is a warning, and a silent fallback string would let a new error value ship
with no sentence and no failing test.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishErrorMessagesTests"`
Expected: 5 passing. If `Every_error_the_library_can_report_has_a_sentence` throws
`ArgumentOutOfRangeException`, a value was added to `XrplFileError` after this plan was written —
add its sentence rather than widening the fallback.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/PublishErrorMessages.cs tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs
git commit -m "feat(app): one sentence for every publication error"
```

---

## Task 6: Where runs live, and the publications file

Spec sections 12 and decisions F11, F12.

**Files:**
- Create: `app/XrplFile.App.Core/RunPaths.cs`
- Create: `app/XrplFile.App.Core/PublicationRecord.cs`
- Create: `app/XrplFile.App.Core/PublicationHistory.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class RunPaths(string? baseDirectory = null) { public string StateDirectory { get; } public string HistoryPath { get; } public void EnsureCreated(); }`
  - `public sealed record PublicationRecord(string Link, string Name, long Size, string Type, string Network, string Account, long Transactions, long FeeDropsEstimated, DateTimeOffset FinishedAt);`
  - `public sealed class PublicationHistory(string path) { public string? Failure { get; } public IReadOnlyList<PublicationRecord> Read(); public void Append(PublicationRecord record); public void Forget(string link); public void Clear(); }`

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs`:

```csharp
using System.Text;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationHistoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-history-").FullName;

    private string HistoryPath => Path.Combine(_directory, "publications.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PublicationRecord Record(string link, DateTimeOffset finishedAt) =>
        new(link, "photo.png", 4096, "image/png", "testnet", "rWriter", 7, 84, finishedAt);

    [Fact]
    public void An_absent_file_reads_as_an_empty_history_with_no_failure()
    {
        PublicationHistory history = new(HistoryPath);

        Assert.Empty(history.Read());
        Assert.Null(history.Failure);
    }

    [Fact]
    public void An_appended_record_reads_back()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ABC", DateTimeOffset.UnixEpoch));

        Assert.Equal("xrpl-file://testnet/ABC", Assert.Single(new PublicationHistory(HistoryPath).Read()).Link);
    }

    [Fact]
    public void The_newest_record_comes_first()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/OLD", DateTimeOffset.UnixEpoch));
        history.Append(Record("xrpl-file://testnet/NEW", DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.Equal("xrpl-file://testnet/NEW", history.Read()[0].Link);
    }

    [Fact]
    public void A_forgotten_record_is_gone_and_the_others_stay()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));
        history.Append(Record("xrpl-file://testnet/TWO", DateTimeOffset.UnixEpoch.AddDays(1)));

        history.Forget("xrpl-file://testnet/ONE");

        Assert.Equal("xrpl-file://testnet/TWO", Assert.Single(history.Read()).Link);
    }

    [Fact]
    public void Clearing_leaves_an_empty_history()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        history.Clear();

        Assert.Empty(history.Read());
    }

    [Fact]
    public void A_file_that_is_not_json_reads_as_empty_and_says_why_and_is_not_overwritten()
    {
        byte[] garbage = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(HistoryPath, garbage);

        PublicationHistory history = new(HistoryPath);

        Assert.Empty(history.Read());
        Assert.NotNull(history.Failure);
        Assert.Equal(garbage, File.ReadAllBytes(HistoryPath));
    }

    [Fact]
    public void Appending_leaves_no_temporary_file_behind()
    {
        PublicationHistory history = new(HistoryPath);
        history.Append(Record("xrpl-file://testnet/ONE", DateTimeOffset.UnixEpoch));

        Assert.Single(Directory.GetFiles(_directory));
    }
}
```

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublicationHistoryTests"`
Expected: does not compile — the types do not exist.

- [ ] **Step 3: Write the paths**

`app/XrplFile.App.Core/RunPaths.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Where a publication's state files and the publications list live: always in the application's
/// own data directory, never beside the vault file (design F12). The vault is portable by design
/// (C1's E5) and may sit on removable media or in a synchronised folder; a half-finished run's
/// state is neither portable nor something to synchronise.
/// </summary>
public sealed class RunPaths
{
    private const string ApplicationDirectoryName = "XrplFile";

    /// <param name="baseDirectory">
    /// Where to put them. Defaults to the application data directory, which is what the running
    /// application uses; tests pass a temporary directory.
    /// </param>
    public RunPaths(string? baseDirectory = null)
    {
        string root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ApplicationDirectoryName);

        StateDirectory = Path.GetFullPath(Path.Combine(root, "runs"));
        HistoryPath = Path.GetFullPath(Path.Combine(root, "publications.json"));
    }

    /// <summary>What <see cref="SessionOptions.StateDirectory"/> is set to.</summary>
    public string StateDirectory { get; }

    public string HistoryPath { get; }

    /// <summary>
    /// Creates the state directory. <see cref="PublicationSession.StartAsync"/> writes its state
    /// file there on the run's first save, and <see cref="PublicationSession.Unfinished"/> treats a
    /// missing directory as "no unfinished runs" — so this is called before a run starts, not
    /// before the list is read.
    /// </summary>
    public void EnsureCreated() => Directory.CreateDirectory(StateDirectory);
}
```

- [ ] **Step 4: Write the record**

`app/XrplFile.App.Core/PublicationRecord.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// One publication that succeeded (design 12). It holds no secret: the account is an address and
/// nothing here is a seed, which is the rule part A's own state file follows.
/// </summary>
public sealed record PublicationRecord(
    string Link,
    string Name,
    long Size,
    string Type,
    string Network,
    string Account,
    long Transactions,
    long FeeDropsEstimated,
    DateTimeOffset FinishedAt);
```

- [ ] **Step 5: Write the history**

`app/XrplFile.App.Core/PublicationHistory.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace XrplFile.App.Core;

/// <summary>
/// The publications list: one JSON array on disk, appended after a run succeeds (design 12). A link
/// nobody recorded is a file nobody can find again, so this exists; it is the person's own history,
/// so <see cref="Forget"/> and <see cref="Clear"/> exist too.
/// </summary>
public sealed class PublicationHistory(string path)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Why the last <see cref="Read"/> came back empty, when it came back empty because the file
    /// could not be read rather than because there is nothing in it. A screen shows this instead of
    /// pretending the person has never published anything.
    /// </summary>
    public string? Failure { get; private set; }

    /// <summary>Newest first. An unreadable file reads as empty and sets <see cref="Failure"/>.</summary>
    public IReadOnlyList<PublicationRecord> Read()
    {
        Failure = null;

        try
        {
            if (!File.Exists(path)) return [];

            List<PublicationRecord>? records = JsonSerializer.Deserialize<List<PublicationRecord>>(
                File.ReadAllBytes(path));

            if (records is null) return [];

            records.Sort((left, right) => right.FinishedAt.CompareTo(left.FinishedAt));
            return records;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Failure = "The publications file could not be read, so this list is empty. The file has been left as it is.";
            return [];
        }
    }

    public void Append(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        List<PublicationRecord> records = [.. Read(), record];
        Save(records);
    }

    public void Forget(string link)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);

        Save([.. Read().Where(record => !string.Equals(record.Link, link, StringComparison.Ordinal))]);
    }

    public void Clear() => Save([]);

    /// <summary>
    /// Written to a temporary file in the same directory and then moved over the real one, so a
    /// crash in the middle cannot leave a truncated array where a history used to be. The same
    /// shape as the vault's own durable write, for the same reason.
    /// </summary>
    private void Save(IReadOnlyList<PublicationRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records, SerializerOptions)));
        File.Move(temporary, path, overwrite: true);
    }
}
```

`Append` calls `Read`, which clears and may set `Failure`. That is intended: appending to a history
that could not be read replaces it with a file holding just the new record, and the caller has been
told through `Failure` that the old one was unreadable. The alternative — refusing to record a
publication that actually happened because an old file is corrupt — loses the link.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublicationHistoryTests"`
Expected: 7 passing.

- [ ] **Step 7: Commit**

```bash
git add app/XrplFile.App.Core/RunPaths.cs app/XrplFile.App.Core/PublicationRecord.cs app/XrplFile.App.Core/PublicationHistory.cs tests/XrplFile.App.Core.Tests/PublicationHistoryTests.cs
git commit -m "feat(app): run paths and the publications history file"
```

---

## Task 7: The file and the key

Spec section 4.1.

**Files:**
- Create: `app/XrplFile.App.Core/PublishRequest.cs`
- Create: `app/XrplFile.App.Core/PublishModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublishModelTests.cs`

**Interfaces:**
- Consumes: `IKeyVault.ListAsync(CancellationToken)` → `IReadOnlyList<VaultEntryInfo>` with
  `Reference`, `Kind`, `Label`, `Network`, `Address`; `IFilePickerService.PickExistingFilePathAsync`;
  `INodeSettingsStore` and `NodeEndpoint.Resolve` from Task 2; `VaultHarness` from
  `tests/XrplFile.App.Core.Tests/VaultHarness.cs`.
- Produces:
  - `public sealed record PublishRequest(ReadOnlyMemory<byte> File, string Type, string Name, string KeyReference, string Account, string Network);`
  - `public sealed class PublishModel(IKeyVault vault, IFilePickerService picker, INodeSettingsStore nodes)` with
    `IReadOnlyList<VaultEntryInfo> Keys`, `string? FilePath`, `string? FileName`, `long FileSize`,
    `string? SelectedReference`, `string? Message`, `string? Warning`, `bool CanEstimate`,
    `Task RefreshKeysAsync(CancellationToken)`, `Task PickFileAsync(CancellationToken)`,
    `void SelectKey(string reference)`, `Task<PublishRequest?> BuildRequestAsync(CancellationToken)`,
    and the constants `MaxFileBytes` and `ReaderDefaultMaxBytes`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublishModelTests.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class FakeFilePicker : IFilePickerService
{
    public string? PathToReturn { get; set; }

    public Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen never saves a file");

    public Task<byte[]?> PickFileAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("the publish screen picks a path, not bytes");

    public Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PathToReturn);
}

public sealed class PublishModelTests : VaultHarness
{
    private readonly FakeFilePicker _picker = new();
    private readonly string _files = Directory.CreateTempSubdirectory("xrpl-file-publish-").FullName;

    private string WriteFile(string name, long size)
    {
        string path = Path.Combine(_files, name);
        File.WriteAllBytes(path, new byte[size]);
        _picker.PathToReturn = path;
        return path;
    }

    private async Task<(PublishModel Model, KeyVault Vault, string Reference)> OpenAsync(
        CancellationToken token, INodeSettingsStore? nodes = null)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        string reference = await vault.StoreAsync(Entry("publishing key"), token);

        PublishModel model = new(vault, _picker, nodes ?? new FileNodeSettingsStore(Path.Combine(_files, "nodes.json")));
        await model.RefreshKeysAsync(token);
        return (model, vault, reference);
    }

    [Fact]
    public async Task With_nothing_chosen_it_refuses_and_says_what_is_missing()
    {
        (PublishModel model, _, _) = await OpenAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanEstimate);
        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_key_with_no_file_is_still_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("empty.bin", 0);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.False(model.CanEstimate);
        Assert.Contains("empty", model.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_and_a_key_build_a_request()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal("photo.png", request.Name);
        Assert.Equal("image/png", request.Type);
        Assert.Equal(2048, request.File.Length);
        Assert.Equal(reference, request.KeyReference);
        Assert.Equal("standalone", request.Network);
    }

    [Fact]
    public async Task An_unknown_extension_publishes_as_an_octet_stream()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("notes.wibble", 16);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        PublishRequest? request = await model.BuildRequestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("application/octet-stream", request!.Type);
    }

    [Fact]
    public async Task A_network_with_no_node_url_is_refused_before_anything_is_spent()
    {
        // The harness's entries are on "standalone", which Endpoints.For does not know, so an
        // empty settings store leaves the network unreachable.
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        Assert.True(model.CanEstimate);
        Assert.Null(model.NodeUrl);
        Assert.Contains("Settings", model.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_past_the_readers_default_limit_is_warned_about_and_still_allowed()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        // Nothing this size is written to disk: the warning is a function of the recorded size.
        model.SelectKey(reference);
        model.DescribeFileForTests("huge.bin", PublishModel.ReaderDefaultMaxBytes + 1);

        Assert.NotNull(model.Warning);
        Assert.True(model.CanEstimate);
    }

    [Fact]
    public async Task A_file_past_the_memory_ceiling_is_refused()
    {
        (PublishModel model, _, string reference) = await OpenAsync(TestContext.Current.CancellationToken);

        model.SelectKey(reference);
        model.DescribeFileForTests("enormous.bin", PublishModel.MaxFileBytes + 1);

        Assert.False(model.CanEstimate);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_locked_vault_refuses_to_build_a_request()
    {
        (PublishModel model, KeyVault vault, string reference) = await OpenAsync(TestContext.Current.CancellationToken);
        WriteFile("photo.png", 2048);
        await model.PickFileAsync(TestContext.Current.CancellationToken);
        model.SelectKey(reference);

        await vault.LockAsync();

        Assert.Null(await model.BuildRequestAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }
}
```

`DescribeFileForTests` is an `internal` method; `XrplFile.App.Core` already carries
`<InternalsVisibleTo Include="XrplFile.App.Core.Tests" />`, so the test can call it. It exists so
the two size rules can be tested without writing a 64 MB file to a CI runner's disk.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishModelTests"`
Expected: does not compile — `PublishModel` does not exist.

- [ ] **Step 3: Write the request**

`app/XrplFile.App.Core/PublishRequest.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. The bytes travel with it because
/// <see cref="PublicationSession"/> takes the whole file as a <see cref="ReadOnlyMemory{T}"/> — there
/// is no streaming path into it, which is also why <see cref="PublishModel.MaxFileBytes"/> exists.
/// </summary>
public sealed record PublishRequest(
    ReadOnlyMemory<byte> File,
    string Type,
    string Name,
    string KeyReference,
    string Account,
    string Network);
```

- [ ] **Step 4: Write the model**

`app/XrplFile.App.Core/PublishModel.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// The first publish screen (design 4.1): which file, which key, and every reason those two are not
/// yet enough to ask a node anything.
/// </summary>
public sealed class PublishModel(IKeyVault vault, IFilePickerService picker, INodeSettingsStore nodes)
{
    /// <summary>
    /// <see cref="ReaderOptions.MaxBytes"/>'s own default. A larger file publishes correctly and
    /// then produces a link the library's reader refuses unless its caller raises the ceiling, so
    /// this is a warning rather than a refusal (design 4.1) — the limit is the reader's default, not
    /// the format's, and the writer's read-back lifts it for itself.
    /// </summary>
    public const long ReaderDefaultMaxBytes = 20_000_000;

    /// <summary>
    /// The largest file this application will load. The whole file is held in memory as
    /// <see cref="ReadOnlyMemory{T}"/> and encoded into memos on top of that, and at roughly a
    /// kilobyte of file per memo this is already tens of thousands of transactions — a run whose
    /// cost stops it long before its size does.
    /// </summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".bin"] = "application/octet-stream",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".gif"] = "image/gif",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".md"] = "text/markdown",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".wasm"] = "application/wasm",
        [".webp"] = "image/webp",
        [".xml"] = "application/xml",
        [".zip"] = "application/zip",
    };

    public IReadOnlyList<VaultEntryInfo> Keys { get; private set; } = [];

    public string? FilePath { get; private set; }

    public string? FileName { get; private set; }

    public long FileSize { get; private set; }

    public string? SelectedReference { get; private set; }

    /// <summary>The chosen key's entry, or <see langword="null"/> when none is chosen.</summary>
    public VaultEntryInfo? SelectedKey =>
        SelectedReference is null
            ? null
            : Keys.FirstOrDefault(key => string.Equals(key.Reference, SelectedReference, StringComparison.Ordinal));

    /// <summary>The node this publication would go to, or <see langword="null"/> when there is none.</summary>
    public string? NodeUrl => SelectedKey is null ? null : NodeEndpoint.Resolve(nodes, SelectedKey.Network);

    /// <summary>Why the screen cannot go on, or <see langword="null"/> when it can.</summary>
    public string? Message { get; private set; }

    /// <summary>Something true and worth knowing that does not stop the publication.</summary>
    public string? Warning { get; private set; }

    public bool CanEstimate =>
        FilePath is not null && FileSize > 0 && FileSize <= MaxFileBytes && SelectedKey is not null;

    public async Task RefreshKeysAsync(CancellationToken cancellationToken)
    {
        Keys = await vault.ListAsync(cancellationToken).ConfigureAwait(false);

        // A key that was selected and is no longer listed -- deleted from another screen, or gone
        // with a vault that was locked and reopened elsewhere -- must not stay selected: SelectedKey
        // would answer null while SelectedReference still named it.
        if (SelectedKey is null) SelectedReference = null;

        Describe();
    }

    public async Task PickFileAsync(CancellationToken cancellationToken)
    {
        string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
        if (path is null) return;

        try
        {
            FileInfo info = new(path);
            FilePath = path;
            FileName = info.Name;
            FileSize = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FilePath = null;
            FileName = null;
            FileSize = 0;
            Message = "That file could not be read.";
            return;
        }

        Describe();
    }

    public void SelectKey(string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        SelectedReference = reference;
        Describe();
    }

    /// <summary>
    /// Records a file's name and size without touching the disk. Exists so the two size rules can be
    /// tested without writing tens of megabytes on a CI runner.
    /// </summary>
    internal void DescribeFileForTests(string name, long size)
    {
        FilePath = Path.Combine(Path.GetTempPath(), name);
        FileName = name;
        FileSize = size;
        Describe();
    }

    /// <summary>
    /// Reads the file and settles everything a run needs. Returns <see langword="null"/> when it
    /// cannot, having set <see cref="Message"/> to why — a locked vault and a file that has gone
    /// away between choosing it and pressing the button are both ordinary here.
    /// </summary>
    public async Task<PublishRequest?> BuildRequestAsync(CancellationToken cancellationToken)
    {
        Describe();
        if (!CanEstimate || FilePath is null || SelectedKey is null) return null;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return null;
        }

        if (bytes.Length == 0)
        {
            Message = "That file is empty, and an empty file has nothing to publish.";
            return null;
        }

        // Asked rather than remembered: the vault can be locked by the idle timer or by another
        // screen between this screen opening and this button being pressed, and a request built over
        // a locked vault fails at its first signature instead of here.
        try
        {
            IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Any(key => string.Equals(key.Reference, SelectedKey.Reference, StringComparison.Ordinal)))
            {
                Message = "That key is no longer in the vault.";
                return null;
            }
        }
        catch (VaultException ex)
        {
            Message = VaultErrorMessages.For(ex.Error);
            return null;
        }

        return new PublishRequest(
            bytes,
            TypeOf(FileName!),
            FileName!,
            SelectedKey.Reference,
            SelectedKey.Address,
            SelectedKey.Network);
    }

    private static string TypeOf(string fileName) =>
        TypesByExtension.TryGetValue(Path.GetExtension(fileName), out string? type)
            ? type
            : "application/octet-stream";

    private void Describe()
    {
        Warning = null;

        if (FilePath is null)
        {
            Message = "Choose a file to publish.";
            return;
        }

        if (FileSize == 0)
        {
            Message = "That file is empty, and an empty file has nothing to publish.";
            return;
        }

        if (FileSize > MaxFileBytes)
        {
            Message = $"That file is {FileSize:N0} bytes. This application publishes files up to {MaxFileBytes:N0} bytes.";
            return;
        }

        if (SelectedKey is null)
        {
            Message = "Choose the key that will publish it.";
            return;
        }

        if (FileSize > ReaderDefaultMaxBytes)
        {
            Warning =
                $"This file is larger than the {ReaderDefaultMaxBytes:N0} bytes a reader accepts by default, "
                + "so the link will only open for a reader whose limit has been raised. Publishing it still works.";
        }

        Message = NodeUrl is null
            ? $"No node URL is known for the network '{SelectedKey.Network}'. Add one in Settings."
            : null;
    }
}
```

`Message` doubles as both a refusal and the network notice, which is why
`A_network_with_no_node_url_is_refused_before_anything_is_spent` asserts `CanEstimate` is still true
while `Message` names Settings: the file and the key *are* chosen, and it is the estimate step that
cannot connect. The estimate screen refuses on the same fact (Task 8), which is where the money is
at stake.

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublishModelTests"`
Expected: 9 passing.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App.Core/PublishRequest.cs app/XrplFile.App.Core/PublishModel.cs tests/XrplFile.App.Core.Tests/PublishModelTests.cs
git commit -m "feat(app): the file and key choice for a publication"
```

---

## Task 8: The estimate

Spec section 4.2 and decision F9.

**Files:**
- Create: `app/XrplFile.App.Core/EstimateModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/EstimateModelTests.cs`

**Interfaces:**
- Consumes: `IConnectionFactory` and `FakeConnectionFactory` from Task 3; `PublishRequest` from
  Task 7; `PublishErrorMessages` from Task 5;
  `PublicationSession.EstimateAsync(ILedgerSource source, string account, long fileBytes, SessionOptions options, CancellationToken)`.
- Produces: `public sealed class EstimateModel(IConnectionFactory connections, INodeSettingsStore nodes, RunPaths paths)`
  with `PublicationEstimate? Estimate`, `string? NodeUrl`, `string? Message`, `bool CanPublish`,
  `Task EstimateAsync(PublishRequest request, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/EstimateModelTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class EstimateModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-estimate-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private EstimateModel NewModel() =>
        new(_connections, new FileNodeSettingsStore(Path.Combine(_directory, "nodes.json")), new RunPaths(_directory));

    private static PublishRequest Request(int size = 4096) =>
        new(new byte[size], "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone");

    [Fact]
    public async Task A_funded_account_gets_an_estimate_it_can_publish_from()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.True(model.CanPublish);
        Assert.Equal("fake://node", model.NodeUrl);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task An_account_that_cannot_cover_it_is_told_what_is_held_and_what_is_needed()
    {
        _connections.Ledger.Balances["rWriter"] = 1;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_so_in_its_own_words()
    {
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Null(model.Estimate);
        Assert.False(model.CanPublish);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
    }

    [Fact]
    public async Task The_connection_is_closed_whatever_happened()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(_connections.Connections, _connections.Disposals);
    }

    [Fact]
    public async Task A_second_estimate_replaces_the_first()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;

        EstimateModel model = NewModel();
        await model.EstimateAsync(Request(size: 4096), TestContext.Current.CancellationToken);
        long first = model.Estimate!.Chunks;

        await model.EstimateAsync(Request(size: 40_960), TestContext.Current.CancellationToken);

        Assert.True(model.Estimate!.Chunks > first);
    }
}
```

`FakeLedgerSource` must be able to answer a balance for an account. Check its members first: if it
has no `Balances` dictionary, add one to `tests/XrplFile.TestSupport/FakeLedgerSource.cs` in this
task, alongside the `TicketCounts` dictionary it already carries, and have
`BalanceDropsAsync`/`AccountStateAsync` read from it. Keep the change additive — `XrplFile.Core.Tests`
builds against the same file.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.EstimateModelTests"`
Expected: does not compile — `EstimateModel` does not exist.

- [ ] **Step 3: Write the model**

`app/XrplFile.App.Core/EstimateModel.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// The estimate screen (design 4.2). The figures are the library's, unaltered, even when the run
/// will use the sequence strategy (design F9): the estimate is ticket-shaped — it counts mints and a
/// peak ticket reserve a sequence run never pays — so showing it unchanged overstates the cost and
/// never understates it, and a second estimator would be one nothing validates.
/// </summary>
public sealed class EstimateModel(IConnectionFactory connections, INodeSettingsStore nodes, RunPaths paths)
{
    public PublicationEstimate? Estimate { get; private set; }

    /// <summary>The node the estimate came from, which is also the node a run would go to.</summary>
    public string? NodeUrl { get; private set; }

    public string? Message { get; private set; }

    public bool CanPublish =>
        Estimate is not null
        && Estimate.BalanceCovers
        && Estimate.FeeDropsPerTransaction <= int.MaxValue;

    public async Task EstimateAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Estimate = null;
        NodeUrl = null;
        Message = null;

        try
        {
            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            NodeUrl = connection.Url;

            SessionOptions options = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            };

            Estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, request.File.Length, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (XrplFileException ex)
        {
            Message = PublishErrorMessages.For(ex);
            return;
        }

        if (Estimate.FeeDropsPerTransaction > int.MaxValue)
        {
            // Both strategy option types take an int fee, and the run prices its strategy from this
            // number so the session does not refuse it with FeeMismatch. A price this large is not a
            // situation the protocol produces; it is one this application says no to rather than
            // wraps around.
            Message = "The node is quoting a price this application cannot use. Try a different node.";
            return;
        }

        if (!Estimate.BalanceCovers)
        {
            Message =
                $"This publication needs {Estimate.RequiredDrops:N0} drops and the account can spend "
                + $"{Estimate.SpendableDrops:N0}. Fund it, or publish a smaller file.";
        }
    }
}
```

The estimate is taken with `TicketsPerBatch` set even when the run will not use tickets: that
argument only changes the mint count the estimate reports, and reporting the ticket shape is exactly
what F9 says to do.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.EstimateModelTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/EstimateModel.cs tests/XrplFile.App.Core.Tests/EstimateModelTests.cs tests/XrplFile.TestSupport
git commit -m "feat(app): the publication estimate screen's model"
```

---

## Task 9: The run that outlives the screen

Spec sections 8 and 10, decisions F5, F6, F13, F14. The centre of this part.

**Files:**
- Create: `app/XrplFile.App.Core/PublicationRun.cs`
- Test: `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`

**Interfaces:**
- Consumes: `IConnectionFactory` (Task 3), `PublishPolicy` (Task 4), `PublishErrorMessages` (Task 5),
  `RunPaths` + `PublicationHistory` + `PublicationRecord` (Task 6), `PublishRequest` (Task 7);
  `VaultSigner.CreateAsync(IKeyVault vault, string reference, CancellationToken cancellationToken, VaultAutoLock? autoLock = null)`;
  `PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory<byte>, string type, string? name, string writer, string keyReference, string network, SessionOptions, CancellationToken)`;
  `PublicationSession.ResumeAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory<byte>, string statePath, SessionOptions, CancellationToken)`;
  `PublicationSession.RunAsync(IProgress<PublishProgress>?, CancellationToken)`;
  `VaultAutoLock.Touch()`.
- Produces:
  - `public enum PublicationRunState { Idle, Running, Succeeded, Failed, Cancelled }`
  - `public sealed record RunSnapshot(PublicationRunState State, PublishProgress? Progress, PublicationReport? Report, string? Failure, string? Name, string? Network);`
  - `public sealed class PublicationRun(IConnectionFactory connections, IKeyVault vault, RunPaths paths, PublicationHistory history, VaultAutoLock? autoLock = null)`
    with `RunSnapshot Snapshot`, `Task Completion`, `bool Start(PublishRequest)`,
    `bool Resume(string statePath, PublishRequest request)`, `void Cancel()`,
    `Task<bool> StopAsync(TimeSpan timeout)`, `void Reset()`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublicationRunTests.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationRunTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-run-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    private PublicationHistory History => new(Paths.HistoryPath);

    private async Task<(PublicationRun Run, PublishRequest Request)> ReadyAsync(CancellationToken token, int size = 4096)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        // A real seed, so VaultSigner can derive the account the request names and sign with it.
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            token);

        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        PublicationRun run = new(_connections, vault, Paths, History);
        PublishRequest request = new(
            new byte[size], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "standalone");

        return (run, request);
    }

    [Fact]
    public async Task A_run_starts_idle()
    {
        (PublicationRun run, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
    }

    [Fact]
    public async Task A_publication_succeeds_reports_progress_and_produces_a_link()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(request));
        await run.Completion;

        RunSnapshot snapshot = run.Snapshot;
        Assert.Equal(PublicationRunState.Succeeded, snapshot.State);
        Assert.NotNull(snapshot.Report);
        Assert.NotNull(snapshot.Progress);
        Assert.Equal("file.bin", snapshot.Name);
    }

    [Fact]
    public async Task A_publication_that_succeeded_is_recorded_in_the_history()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Start(request);
        await run.Completion;

        PublicationRecord recorded = Assert.Single(History.Read());
        Assert.Equal(run.Snapshot.Report!.Link.ToString(), recorded.Link);
        Assert.Equal("file.bin", recorded.Name);
        Assert.Equal("standalone", recorded.Network);
    }

    [Fact]
    public async Task A_second_run_while_one_is_going_is_refused()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(request));
        Assert.False(run.Start(request));

        await run.Completion;
    }

    [Fact]
    public async Task A_cancelled_run_reports_cancelled_and_leaves_a_state_file()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

        run.Start(request);
        run.Cancel();
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.NotEmpty(PublicationSession.Unfinished(Paths.StateDirectory));
    }

    [Fact]
    public async Task A_cancelled_run_records_nothing_in_the_history()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

        run.Start(request);
        run.Cancel();
        await run.Completion;

        Assert.Empty(History.Read());
    }

    [Fact]
    public async Task A_failing_node_leaves_the_run_failed_with_its_own_sentence()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        run.Start(request);
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), run.Snapshot.Failure);
    }

    [Fact]
    public async Task Stopping_returns_only_once_the_run_has_actually_stopped()
    {
        (PublicationRun run, PublishRequest request) = await ReadyAsync(TestContext.Current.CancellationToken, size: 200_000);

        run.Start(request);
        bool stopped = await run.StopAsync(TimeSpan.FromMinutes(1));

        Assert.True(stopped);
        Assert.True(run.Completion.IsCompleted);
        Assert.NotEqual(PublicationRunState.Running, run.Snapshot.State);
    }

    [Fact]
    public async Task Stopping_an_idle_run_is_immediate_and_true()
    {
        (PublicationRun run, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(await run.StopAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Progress_touches_the_idle_lock()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        FakeTimeProvider time = new();
        await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(5), time);

        PublicationRun run = new(_connections, vault, Paths, History, autoLock);
        run.Start(new PublishRequest(
            new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "standalone"));
        await run.Completion;

        // The run touched the lock; the vault is still open after a run that reported progress.
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(await vault.ListAsync(TestContext.Current.CancellationToken) is { Count: 1 });
    }
}
```

`FakeTimeProvider` — check whether `XrplFile.Keys.Tests` already has one before writing another; if
it does and it is not shareable, use `TimeProvider.System` here and assert only that the run
succeeded. The point of the test is that `PublicationRun` passes an `IProgress<PublishProgress>`
which touches the lock — if a simpler assertion is the honest one available, take it and say so in
the test's name.

`XrplWallet.Generate()` and `XrplWallet.Seed` come from the `Xrpl` SDK; confirm the member names
against `E:\GIT\XRPL\XrplCSharp\Xrpl\Wallet\XrplWallet.cs` before writing. `XrplFile.Keys.Tests`
already generates wallets this way — copy whatever it does.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublicationRunTests"`
Expected: does not compile — `PublicationRun` does not exist.

- [ ] **Step 3: Write the state types**

At the top of `app/XrplFile.App.Core/PublicationRun.cs`:

```csharp
using XrplFile.Keys;

namespace XrplFile.App.Core;

public enum PublicationRunState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One consistent view of the run. A screen takes this rather than reading fields one at a time:
/// separate reads could see <see cref="PublicationRunState.Succeeded"/> with no report yet, or
/// <see cref="PublicationRunState.Running"/> carrying the previous run's one.
/// </summary>
public sealed record RunSnapshot(
    PublicationRunState State,
    PublishProgress? Progress,
    PublicationReport? Report,
    string? Failure,
    string? Name,
    string? Network);
```

- [ ] **Step 4: Write the service**

The rest of `app/XrplFile.App.Core/PublicationRun.cs`:

```csharp
/// <summary>
/// The one publication this application can have going (design 8, F5). It holds the cancellation
/// token, the connection, the strategy and the session, and it outlives the screen that started it,
/// so navigating to the key list and back finds the same run at its current progress.
///
/// It does not outlive the window: closing stops it (design F6), and the state file part A writes is
/// what makes it resumable afterwards.
/// </summary>
public sealed class PublicationRun(
    IConnectionFactory connections,
    IKeyVault vault,
    RunPaths paths,
    PublicationHistory history,
    VaultAutoLock? autoLock = null)
{
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private PublicationRunState _state = PublicationRunState.Idle;
    private PublishProgress? _progress;
    private PublicationReport? _report;
    private string? _failure;
    private string? _name;
    private string? _network;

    /// <summary>Completes when the current run has finished, however it finished.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public RunSnapshot Snapshot
    {
        get { lock (_gate) return new RunSnapshot(_state, _progress, _report, _failure, _name, _network); }
    }

    /// <summary>Starts a publication. <see langword="false"/> when one is already going.</summary>
    public bool Start(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Begin(request, statePath: null);
    }

    /// <summary>Resumes an interrupted one. <see langword="false"/> when one is already going.</summary>
    public bool Resume(string statePath, PublishRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        ArgumentNullException.ThrowIfNull(request);

        return Begin(request, statePath);
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>
    /// Cancels and waits for the run to actually stop. What the window-close handler calls before it
    /// locks the vault (design 9): locking first would kill the run at its next signature.
    /// <see langword="false"/> means the timeout expired with the run still going — the caller
    /// proceeds anyway rather than holding a closing window open forever.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task running;
        lock (_gate)
        {
            _cancellation?.Cancel();
            running = Completion;
        }

        if (running.IsCompleted) return true;

        Task finished = await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(finished, running);
    }

    /// <summary>Puts a finished run's outcome away so the screen can start another.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_state == PublicationRunState.Running) return;

            _state = PublicationRunState.Idle;
            _progress = null;
            _report = null;
            _failure = null;
            _name = null;
            _network = null;
        }
    }

    private bool Begin(PublishRequest request, string? statePath)
    {
        CancellationTokenSource cancellation = new();

        lock (_gate)
        {
            if (_state == PublicationRunState.Running) return false;

            _cancellation?.Dispose();
            _cancellation = cancellation;
            _state = PublicationRunState.Running;
            _progress = null;
            _report = null;
            _failure = null;
            _name = request.Name;
            _network = request.Network;
            Completion = Task.Run(() => RunAsync(request, statePath, cancellation.Token));
        }

        return true;
    }

    private async Task RunAsync(PublishRequest request, string? statePath, CancellationToken cancellationToken)
    {
        try
        {
            paths.EnsureCreated();

            await using ILedgerConnection connection =
                await connections.ConnectAsync(request.Network, cancellationToken).ConfigureAwait(false);

            SessionOptions estimateOptions = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
            };

            // Estimated again here rather than carried from the screen: the node's price moves, and
            // StartAsync refuses a strategy priced differently (FeeMismatch). This is also what the
            // budget is set from, so a run cannot quietly cost more than what was just priced.
            PublicationEstimate estimate = await PublicationSession.EstimateAsync(
                connection.Source, request.Account, request.File.Length, estimateOptions, cancellationToken)
                .ConfigureAwait(false);

            SessionOptions options = new()
            {
                StateDirectory = paths.StateDirectory,
                TicketsPerBatch = PublishPolicy.TicketsPerBatch,
                BudgetDrops = estimate.RequiredDrops,
            };

            VaultSigner signer = await VaultSigner
                .CreateAsync(vault, request.KeyReference, cancellationToken, autoLock).ConfigureAwait(false);

            await using ISubmissionStrategy strategy = PublishPolicy.Create(estimate, connection, signer);

            PublicationSession session = statePath is null
                ? await PublicationSession.StartAsync(
                    strategy,
                    connection.Source,
                    request.File,
                    request.Type,
                    request.Name,
                    request.Account,
                    request.KeyReference,
                    request.Network,
                    options,
                    cancellationToken).ConfigureAwait(false)
                : await PublicationSession.ResumeAsync(
                    strategy, connection.Source, request.File, statePath, options, cancellationToken)
                    .ConfigureAwait(false);

            PublicationReport report = await session
                .RunAsync(new SynchronousProgress(Observe), cancellationToken).ConfigureAwait(false);

            history.Append(new PublicationRecord(
                report.Link.ToString(),
                request.Name,
                request.File.Length,
                request.Type,
                request.Network,
                request.Account,
                report.Transactions,
                report.FeeDropsEstimated,
                report.FinishedAt));

            lock (_gate)
            {
                _report = report;
                _state = PublicationRunState.Succeeded;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) _state = PublicationRunState.Cancelled;
        }
        catch (XrplFileException ex)
        {
            Fail(PublishErrorMessages.For(ex));
        }
        catch (VaultException ex)
        {
            Fail(VaultErrorMessages.For(ex.Error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("The publication's state could not be written to disk, so the run was stopped.");
        }
    }

    /// <summary>
    /// Every progress report is also activity on the vault (design 10). <see cref="VaultSigner"/>
    /// already touches the idle lock on each signature, but the read-back stage signs nothing and
    /// can outlast the idle window on a large file — and the manifest signature that follows it
    /// would then find a locked vault at the very last transaction of an otherwise complete run.
    /// </summary>
    private void Observe(PublishProgress progress)
    {
        autoLock?.Touch();
        lock (_gate) _progress = progress;
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _failure = message;
            _state = PublicationRunState.Failed;
        }
    }

    /// <summary>
    /// Reports straight through on the calling thread. <see cref="Progress{T}"/> posts to a
    /// synchronization context, which on a MAUI Blazor window means the report arrives after the
    /// screen has already read the state it was supposed to update.
    /// </summary>
    private sealed class SynchronousProgress(Action<PublishProgress> report) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value) => report(value);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.PublicationRunTests"`
Expected: 10 passing.

If `A_cancelled_run_reports_cancelled_and_leaves_a_state_file` is flaky because the run finishes
before `Cancel()` lands, raise the file size until the run reliably takes long enough, or have the
test cancel from inside the progress callback of the first `chunks` report. Do not make the test
pass by accepting `Succeeded` as an outcome — that would be a test asserting nothing.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App.Core/PublicationRun.cs tests/XrplFile.App.Core.Tests/PublicationRunTests.cs
git commit -m "feat(app): the running publication as a service that outlives its screen"
```

---

## Task 10: The run screen's model

Spec section 4.3.

**Files:**
- Create: `app/XrplFile.App.Core/RunModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/RunModelTests.cs`

**Interfaces:**
- Consumes: `PublicationRun`, `RunSnapshot`, `PublicationRunState` from Task 9.
- Produces: `public sealed class RunModel(PublicationRun run)` with `RunSnapshot Snapshot`,
  `string StageText`, `int Percent`, `bool CanCancel`, `bool IsFinished`, `string? Link`,
  `void Cancel()`, `void Dismiss()`, and `public static string StageNameOf(string stage)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/RunModelTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class RunModelTests
{
    [Fact]
    public void Every_stage_the_writer_reports_has_a_readable_name()
    {
        foreach (string stage in new[] { "chunks", "lists", "read-back", "manifest", "done" })
        {
            Assert.False(string.IsNullOrWhiteSpace(RunModel.StageNameOf(stage)));
            Assert.NotEqual(stage, RunModel.StageNameOf(stage));
        }
    }

    [Fact]
    public void The_read_back_stage_says_it_is_not_submitting_anything()
    {
        Assert.Contains("read", RunModel.StageNameOf("read-back"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submit", RunModel.StageNameOf("read-back"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stage_the_library_adds_later_is_shown_as_itself_rather_than_hidden()
    {
        Assert.Equal("something-new", RunModel.StageNameOf("something-new"));
    }

    [Fact]
    public void A_percentage_is_zero_before_anything_is_reported()
    {
        RunModel model = new(NewRun());

        Assert.Equal(0, model.Percent);
        Assert.False(model.IsFinished);
    }

    [Fact]
    public void A_percentage_comes_from_the_last_progress_report()
    {
        Assert.Equal(50, RunModel.PercentOf(new PublishProgress("chunks", 5, 10)));
        Assert.Equal(0, RunModel.PercentOf(new PublishProgress("chunks", 0, 0)));
        Assert.Equal(100, RunModel.PercentOf(new PublishProgress("manifest", 1, 1)));
    }

    private static PublicationRun NewRun()
    {
        string directory = Directory.CreateTempSubdirectory("xrpl-file-runmodel-").FullName;
        RunPaths paths = new(directory);
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }
}
```

`NeverVault` is a minimal `IKeyVault` whose every member throws `NotSupportedException` — these
tests never start a run, so nothing touches it. Write it in the same file. Check `IKeyVault`'s full
member list in `src/XrplFile.Keys/IKeyVault.cs` and implement every one; under
`TreatWarningsAsErrors` a partial implementation does not compile.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.RunModelTests"`
Expected: does not compile — `RunModel` does not exist.

- [ ] **Step 3: Write the model**

`app/XrplFile.App.Core/RunModel.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// The run screen (design 4.3). It decides nothing about the run itself — that lives in
/// <see cref="PublicationRun"/>, which survives this screen — and only turns the run's state into
/// what a person reads.
/// </summary>
public sealed class RunModel(PublicationRun run)
{
    public RunSnapshot Snapshot => run.Snapshot;

    public bool CanCancel => Snapshot.State == PublicationRunState.Running;

    public bool IsFinished => Snapshot.State is
        PublicationRunState.Succeeded or PublicationRunState.Failed or PublicationRunState.Cancelled;

    public string? Link => Snapshot.Report?.Link.ToString();

    public string StageText => Snapshot.Progress is { } progress
        ? $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}"
        : "Starting";

    public int Percent => Snapshot.Progress is { } progress ? PercentOf(progress) : 0;

    public void Cancel() => run.Cancel();

    /// <summary>Puts the outcome away so the Publish tab can offer a new publication.</summary>
    public void Dismiss() => run.Reset();

    public static int PercentOf(PublishProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return progress.Total <= 0 ? 0 : (int)Math.Clamp(progress.Done * 100 / progress.Total, 0, 100);
    }

    /// <summary>
    /// The writer's stage names, in words. "read-back" is the one that has to be named explicitly:
    /// it submits nothing, so a person watching a transaction counter would read a working run as a
    /// stalled one.
    /// </summary>
    public static string StageNameOf(string stage) => stage switch
    {
        "chunks" => "Writing the file",
        "lists" => "Writing the block list",
        "read-back" => "Reading it back from the ledger",
        "manifest" => "Writing the manifest",
        "done" => "Finished",

        // Not an exception: a stage this application does not know about is still a stage the run is
        // genuinely in, and showing its raw name beats showing nothing or crashing the screen.
        _ => stage,
    };
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.RunModelTests"`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/RunModel.cs tests/XrplFile.App.Core.Tests/RunModelTests.cs
git commit -m "feat(app): the run screen's model"
```

---

## Task 11: Unfinished runs

Spec sections 4.4 and 11.

**Files:**
- Create: `app/XrplFile.App.Core/UnfinishedModel.cs`
- Test: `tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs`

**Interfaces:**
- Consumes: `PublicationSession.Unfinished(string stateDirectory)` → `IReadOnlyList<UnfinishedRun>`
  where `UnfinishedRun` is `(string Path, PublicationState? State, string? Unreadable)`;
  `PublicationState` fields `Network`, `Writer`, `KeyReference`, `Type`, `Name`, `Size`, `Chunk`,
  `Root`, `Phase`; `PublicationRun.Resume(string, PublishRequest)` from Task 9; `RunPaths` from
  Task 6; `IFilePickerService`.
- Produces: `public sealed record UnfinishedRow(string Path, string? Network, string? Writer, string? KeyReference, string? Type, string? Name, long Size, string? Stage, int Chunks, string? Unreadable);`
  and `public sealed class UnfinishedModel(RunPaths paths, PublicationRun run, IFilePickerService picker)`
  with `IReadOnlyList<UnfinishedRow> Rows`, `string? Message`, `void Refresh()`,
  `Task<bool> ResumeAsync(UnfinishedRow row, CancellationToken)`, `void Delete(UnfinishedRow row)`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs`:

```csharp
using System.Text;

namespace XrplFile.App.Core.Tests;

public sealed class UnfinishedModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-unfinished-").FullName;
    private readonly FakeFilePicker _picker = new();

    private RunPaths Paths => new(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private PublicationRun NewRun()
    {
        RunPaths paths = Paths;
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }

    private void WriteUnreadableState(string fileName)
    {
        Directory.CreateDirectory(Paths.StateDirectory);
        File.WriteAllBytes(Path.Combine(Paths.StateDirectory, fileName), Encoding.UTF8.GetBytes("not a state file"));
    }

    [Fact]
    public void With_no_state_directory_there_are_no_rows_and_no_crash()
    {
        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.Empty(model.Rows);
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_is_still_listed_by_its_path()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        UnfinishedRow row = Assert.Single(model.Rows);
        Assert.NotNull(row.Unreadable);
        Assert.Contains("broken.json", row.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_run_cannot_be_resumed_and_says_why()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.False(await model.ResumeAsync(model.Rows[0], TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public void Deleting_a_state_file_removes_the_row()
    {
        WriteUnreadableState("broken.json");

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();
        model.Delete(model.Rows[0]);

        Assert.Empty(model.Rows);
        Assert.Empty(Directory.GetFiles(Paths.StateDirectory));
    }

    [Fact]
    public async Task Resuming_with_no_file_chosen_does_nothing()
    {
        WriteUnreadableState("broken.json");
        _picker.PathToReturn = null;

        UnfinishedModel model = new(Paths, NewRun(), _picker);
        model.Refresh();

        Assert.False(await model.ResumeAsync(model.Rows[0], TestContext.Current.CancellationToken));
    }
}
```

A test that resumes a *readable* state file needs a real interrupted run to exist. Produce one the
way Task 9's cancellation test does — start a publication over `FakeConnectionFactory`, cancel it,
then point `UnfinishedModel` at the same `RunPaths` — and assert that `ResumeAsync` with the same
bytes returns `true` and puts the run back into `Running`. Write that test in this task; it is the
only proof the resume path is wired at all.

- [ ] **Step 2: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.UnfinishedModelTests"`
Expected: does not compile — `UnfinishedModel` does not exist.

- [ ] **Step 3: Write the model**

`app/XrplFile.App.Core/UnfinishedModel.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// One run that did not finish. <see cref="Unreadable"/> is set exactly when the state file could
/// not be read, and then every other field except <see cref="Path"/> is empty: a file that cannot be
/// parsed still names, by its path, a run that spent money.
/// </summary>
public sealed record UnfinishedRow(
    string Path,
    string? Network,
    string? Writer,
    string? KeyReference,
    string? Type,
    string? Name,
    long Size,
    string? Stage,
    int Chunks,
    string? Unreadable);

/// <summary>
/// The unfinished-runs list (design 4.4) and resuming one (design 11).
/// </summary>
public sealed class UnfinishedModel(RunPaths paths, PublicationRun run, IFilePickerService picker)
{
    public IReadOnlyList<UnfinishedRow> Rows { get; private set; } = [];

    public string? Message { get; private set; }

    public void Refresh()
    {
        Message = null;

        Rows =
        [
            .. PublicationSession.Unfinished(paths.StateDirectory).Select(unfinished => unfinished.State is { } state
                ? new UnfinishedRow(
                    unfinished.Path, state.Network, state.Writer, state.KeyReference, state.Type,
                    state.Name, state.Size, state.Phase, state.Chunk, Unreadable: null)
                : new UnfinishedRow(
                    unfinished.Path, null, null, null, null, null, 0, null, 0,
                    unfinished.Unreadable ?? "This run's state file could not be read.")),
        ];
    }

    /// <summary>
    /// Asks for the file again and hands the run back to <see cref="PublicationRun"/>.
    /// <see langword="false"/> when nothing was started, with <see cref="Message"/> saying why —
    /// except when the person simply cancelled the file picker, which is not a failure and says
    /// nothing.
    /// </summary>
    public async Task<bool> ResumeAsync(UnfinishedRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        Message = null;

        if (row.Unreadable is not null)
        {
            Message = "This run's state file cannot be read, so there is nothing to resume from. It can be deleted.";
            return false;
        }

        string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
        if (path is null) return false;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return false;
        }

        // Checked here so the person hears "that is not the same file" from the screen rather than
        // from a run that starts, connects and then refuses. The library checks it again against the
        // recorded Merkle root, which is the check that actually binds -- this one only makes the
        // common mistake cheap.
        if (bytes.LongLength != row.Size)
        {
            Message =
                $"That file is {bytes.LongLength:N0} bytes and this run was publishing {row.Size:N0}. "
                + "Resuming needs the same file the run started with.";
            return false;
        }

        // The key, the type, the writer and the network all come from the state file rather than
        // from this screen: PublicationSession.ResumeAsync takes them from there too, and a resume
        // that disagreed with the state would be refused with ResumeMismatch after connecting.
        // KeyReference above all -- PublicationRun builds its VaultSigner from it, and an empty one
        // fails with NoSuchEntry at the worst possible moment.
        PublishRequest request = new(
            bytes,
            row.Type ?? "application/octet-stream",
            row.Name ?? Path.GetFileName(path),
            row.KeyReference!,
            row.Writer!,
            row.Network!);

        if (!run.Resume(row.Path, request))
        {
            Message = "A publication is already running.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deletes a state file. What this costs is stated on the screen, not here: the money that run
    /// spent is spent either way, and what is lost is the ability to finish it without paying again.
    /// </summary>
    public void Delete(UnfinishedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            File.Delete(row.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That state file could not be deleted.";
        }

        Refresh();
    }
}
```

The `!` on `row.KeyReference`, `row.Writer` and `row.Network` is safe because the `Unreadable is not
null` guard above returned already: those three are non-null exactly when the state file parsed.
`PublicationState` carries all of them (`KeyReference`, `Writer`, `Network`, `Type`), which is what
makes a resume possible from the state alone.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.UnfinishedModelTests"`
Expected: all passing, including the readable-resume test.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App.Core/UnfinishedModel.cs tests/XrplFile.App.Core.Tests/UnfinishedModelTests.cs
git commit -m "feat(app): listing and resuming unfinished publications"
```

---

## Task 12: The publications list

Spec section 4.5 and decision F11.

**Files:**
- Create: `app/XrplFile.App.Core/HistoryModel.cs`
- Modify: `app/XrplFile.App.Core/IClipboardService.cs`
- Modify: `app/XrplFile.App/MauiClipboardService.cs`
- Test: `tests/XrplFile.App.Core.Tests/HistoryModelTests.cs`

**Interfaces:**
- Consumes: `PublicationHistory`, `PublicationRecord` from Task 6; `IClipboardService`.
- Produces:
  - `IClipboardService` gains `Task CopyAsync(string value, CancellationToken cancellationToken);`
  - `public sealed class HistoryModel(PublicationHistory history, IClipboardService clipboard)` with
    `IReadOnlyList<PublicationRecord> Records`, `string? Message`, `void Refresh()`,
    `Task CopyLinkAsync(PublicationRecord record, CancellationToken)`,
    `void Forget(PublicationRecord record)`, `void Clear()`.

- [ ] **Step 1: Add the non-secret copy to the clipboard seam**

In `app/XrplFile.App.Core/IClipboardService.cs`, add:

```csharp
    /// <summary>
    /// Copies something that is not a secret and therefore does not expire. A published link is
    /// meant to be pasted somewhere and kept; <see cref="CopySecretAsync"/>'s clearing timer would
    /// take it back out of the clipboard before the person had used it.
    /// </summary>
    Task CopyAsync(string value, CancellationToken cancellationToken);
```

In `app/XrplFile.App/MauiClipboardService.cs`, implement it by setting the clipboard text and
nothing else — no timer, no clearing. Match whatever API the existing `CopySecretAsync` uses.

- [ ] **Step 2: Write the failing tests**

`tests/XrplFile.App.Core.Tests/HistoryModelTests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public string? Copied { get; private set; }

    public TimeSpan? Expiry { get; private set; }

    public Task CopySecretAsync(string value, TimeSpan expiry, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = expiry;
        return Task.CompletedTask;
    }

    public Task CopyAsync(string value, CancellationToken cancellationToken)
    {
        Copied = value;
        Expiry = null;
        return Task.CompletedTask;
    }
}

public sealed class HistoryModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-historymodel-").FullName;
    private readonly FakeClipboard _clipboard = new();

    private PublicationHistory History => new(Path.Combine(_directory, "publications.json"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static PublicationRecord Record(string link) =>
        new(link, "photo.png", 4096, "image/png", "testnet", "rWriter", 7, 84, DateTimeOffset.UnixEpoch);

    [Fact]
    public void An_empty_history_shows_nothing_and_says_nothing_is_wrong()
    {
        HistoryModel model = new(History, _clipboard);
        model.Refresh();

        Assert.Empty(model.Records);
        Assert.Null(model.Message);
    }

    [Fact]
    public void A_recorded_publication_is_listed()
    {
        History.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(History, _clipboard);
        model.Refresh();

        Assert.Equal("xrpl-file://testnet/ABC", Assert.Single(model.Records).Link);
    }

    [Fact]
    public async Task Copying_a_link_does_not_put_it_on_a_clearing_timer()
    {
        History.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(History, _clipboard);
        model.Refresh();
        await model.CopyLinkAsync(model.Records[0], TestContext.Current.CancellationToken);

        Assert.Equal("xrpl-file://testnet/ABC", _clipboard.Copied);
        Assert.Null(_clipboard.Expiry);
    }

    [Fact]
    public void Forgetting_a_record_removes_it_from_the_list_and_the_file()
    {
        PublicationHistory history = History;
        history.Append(Record("xrpl-file://testnet/ABC"));

        HistoryModel model = new(history, _clipboard);
        model.Refresh();
        model.Forget(model.Records[0]);

        Assert.Empty(model.Records);
        Assert.Empty(History.Read());
    }

    [Fact]
    public void Clearing_empties_the_list()
    {
        PublicationHistory history = History;
        history.Append(Record("xrpl-file://testnet/ABC"));
        history.Append(Record("xrpl-file://testnet/DEF"));

        HistoryModel model = new(history, _clipboard);
        model.Clear();

        Assert.Empty(model.Records);
    }
}
```

- [ ] **Step 3: Run them to watch them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.HistoryModelTests"`
Expected: does not compile — `HistoryModel` does not exist, and `IClipboardService.CopyAsync` may
not either if Step 1 was skipped.

- [ ] **Step 4: Write the model**

`app/XrplFile.App.Core/HistoryModel.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// The publications list (design 4.5). Forgetting a row and clearing the list change this file and
/// nothing else — the file is still in the ledger and the link still works for anyone who kept it,
/// which the screen says out loud.
/// </summary>
public sealed class HistoryModel(PublicationHistory history, IClipboardService clipboard)
{
    public IReadOnlyList<PublicationRecord> Records { get; private set; } = [];

    /// <summary>Why the list is empty, when it is empty for a reason worth showing.</summary>
    public string? Message { get; private set; }

    public void Refresh()
    {
        Records = history.Read();
        Message = history.Failure;
    }

    public Task CopyLinkAsync(PublicationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        return clipboard.CopyAsync(record.Link, cancellationToken);
    }

    public void Forget(PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        history.Forget(record.Link);
        Refresh();
    }

    public void Clear()
    {
        history.Clear();
        Refresh();
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class "XrplFile.App.Core.Tests.HistoryModelTests"`
Expected: 5 passing.

- [ ] **Step 6: Commit**

```bash
git add app/XrplFile.App.Core/HistoryModel.cs app/XrplFile.App.Core/IClipboardService.cs app/XrplFile.App/MauiClipboardService.cs tests/XrplFile.App.Core.Tests/HistoryModelTests.cs
git commit -m "feat(app): the publications list"
```

---

## Task 13: The screens

Spec section 4 and decision F13. Components hold no decisions (C1's E2): they read a model, render
it, and call its methods.

**Files:**
- Create: `app/XrplFile.App/Components/Publish.razor`
- Create: `app/XrplFile.App/Components/Estimate.razor`
- Create: `app/XrplFile.App/Components/Run.razor`
- Create: `app/XrplFile.App/Components/Unfinished.razor`
- Create: `app/XrplFile.App/Components/History.razor`
- Modify: `app/XrplFile.App/Components/Shell.razor`
- Modify: `app/XrplFile.App/Components/Settings.razor`
- Modify: `app/XrplFile.App/MauiProgram.cs`

**Interfaces:**
- Consumes: every model from Tasks 7–12.
- Produces: a `Publish` tab in the shell; DI registrations for `INodeSettingsStore`,
  `IConnectionFactory`, `RunPaths`, `PublicationHistory`, `PublicationRun`, `PublishModel`,
  `EstimateModel`, `RunModel`, `UnfinishedModel`, `HistoryModel`.

- [ ] **Step 1: Register everything in `MauiProgram.cs`**

Beside C1's registrations (`services.AddSingleton<VaultListModel>();` and its neighbours), add:

```csharp
        // The node settings file sits beside C1's vault-location.txt, in the application data
        // directory, and is read the same defensive way.
        services.AddSingleton<INodeSettingsStore>(new FileNodeSettingsStore());
        services.AddSingleton<IConnectionFactory>(
            provider => new XrplConnectionFactory(provider.GetRequiredService<INodeSettingsStore>()));

        // Run state and the publications file are in the application's data directory, never beside
        // the vault: the vault is portable by design (C1's E5) and this is not.
        services.AddSingleton(new RunPaths());
        services.AddSingleton(provider => new PublicationHistory(provider.GetRequiredService<RunPaths>().HistoryPath));

        // A singleton because a publication outlives the screen that started it (design F5). It is
        // handed the same VaultAutoLock the signer uses, so progress in a stage that signs nothing
        // still counts as activity (design 10).
        services.AddSingleton(provider => new PublicationRun(
            provider.GetRequiredService<IConnectionFactory>(),
            provider.GetRequiredService<IKeyVault>(),
            provider.GetRequiredService<RunPaths>(),
            provider.GetRequiredService<PublicationHistory>(),
            autoLock));

        services.AddSingleton<PublishModel>();
        services.AddSingleton<EstimateModel>();
        services.AddSingleton<RunModel>();
        services.AddSingleton<UnfinishedModel>();
        services.AddSingleton<HistoryModel>();
```

`autoLock` is the local variable C1 already creates and registers at
`app/XrplFile.App/MauiProgram.cs:164`. Registering `PublicationRun` with it rather than resolving
`VaultAutoLock` from the container keeps the wiring identical to how `TouchingVault` is built one
line below.

- [ ] **Step 2: Add the Publish tab to the shell**

In `app/XrplFile.App/Components/Shell.razor`, add `Publish` to the `UnlockedView` enum, a third
button to the button group in the same shape as the existing two, and a third branch to the
`@if`/`else` that renders `<KeyList />` or `<Settings />`:

```razor
@if (_unlockedView == UnlockedView.KeyList)
{
    <KeyList />
}
else if (_unlockedView == UnlockedView.Publish)
{
    <Publish />
}
else
{
    <Settings />
}
```

- [ ] **Step 3: Write `Publish.razor`**

It renders whichever of the three stops is current, and holds the one-second poll that C1's shell
already uses so the run screen notices progress (design F13):

```razor
@implements IDisposable
@inject PublishModel Model
@inject PublicationRun Run

@if (Run.Snapshot.State != PublicationRunState.Idle)
{
    <Run />
}
else if (_estimated)
{
    <Estimate Request="_request!" OnBack="() => _estimated = false" OnStarted="Started" />
}
else
{
    <div class="container py-3">
        <h5>Publish a file</h5>

        <button type="button" class="btn btn-outline-primary" @onclick="PickAsync">Choose a file…</button>

        @if (Model.FileName is not null)
        {
            <p class="mt-2">@Model.FileName — @Model.FileSize.ToString("N0") bytes</p>
        }

        <ul class="list-group my-3">
            @foreach (VaultEntryInfo key in Model.Keys)
            {
                <li class="list-group-item @(key.Reference == Model.SelectedReference ? "active" : "")"
                    @onclick="() => Model.SelectKey(key.Reference)">
                    @key.Label — @key.Address (@key.Network)
                </li>
            }
        </ul>

        @if (Model.Warning is not null)
        {
            <div class="alert alert-warning">@Model.Warning</div>
        }

        @if (Model.Message is not null)
        {
            <div class="alert alert-secondary">@Model.Message</div>
        }

        <button type="button" class="btn btn-primary" disabled="@(!Model.CanEstimate)" @onclick="EstimateAsync">
            Estimate
        </button>
    </div>

    <Unfinished />
    <History />
}
```

The `@code` block holds only a `PeriodicTimer` at one second calling `StateHasChanged`, the
`_estimated` flag, `_request`, and three `async Task` methods that call `Model.PickFileAsync`,
`Model.BuildRequestAsync` and nothing else. Copy the timer's shape verbatim from
`app/XrplFile.App/Components/Shell.razor` — including its `CancellationTokenSource`, its
`IDisposable` implementation and its `_disposed` guard — so both screens notice changes at the same
interval and neither leaks a timer.

- [ ] **Step 4: Write `Estimate.razor`, `Run.razor`, `Unfinished.razor`, `History.razor`**

Each is the same shape: inject its model, call its refresh in `OnInitializedAsync`, render its
fields, and wire buttons to its methods. No `if` in any of them decides anything a model has not
already decided — `CanPublish`, `CanCancel`, `IsFinished`, `Message` and `Warning` are all
model properties, and the markup only chooses whether to draw them.

`Estimate.razor` shows, from `EstimateModel.Estimate`: `Chunks`, `Transactions`,
`FeeDropsPerTransaction`, `FeeDropsTotal`, `RequiredDrops`, `BalanceDrops`, `SpendableDrops`, and a
line naming `EstimateModel.NodeUrl` and the request's network. Its publish button is disabled unless
`CanPublish`, and pressing it calls `PublicationRun.Start(request)`.

`Run.razor` shows `RunModel.StageText`, a progress bar at `RunModel.Percent`, a Cancel button
enabled on `CanCancel`, and — once `IsFinished` — either `RunModel.Link` with a copy button, or
`Snapshot.Failure`, plus a Dismiss button calling `RunModel.Dismiss()`.

- [ ] **Step 5: Add the node URLs to Settings**

In `app/XrplFile.App/Components/Settings.razor`, add a section listing `mainnet`, `testnet`,
`devnet` and any network already present in `INodeSettingsStore.Read()`, each with a text box bound
to its URL and a Save button that calls `INodeSettingsStore.Write`. Add one sentence saying that an
empty box means the built-in endpoint is used, and that a network the application does not know
needs a URL here before anything can be published to it.

- [ ] **Step 6: Build the application**

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
```
Expected: builds clean on Windows with the `maui-windows` workload installed. This is the only check
these files get — there is no component test framework (C1's E9), which is precisely why every
decision had to be in a model already.

- [ ] **Step 7: Check for BOMs before committing**

A MAUI or Visual Studio tool may write a UTF-8 BOM into new files, and this repository never changes
a file's encoding:

```bash
git diff --cached --name-only | while read -r f; do
  head -c 3 "$f" | grep -q $'\xef\xbb\xbf' && echo "BOM: $f"
done
```
Strip any that appear.

- [ ] **Step 8: Commit**

```bash
git add app/XrplFile.App
git commit -m "feat(app): the publish, estimate, run, unfinished and history screens"
```

---

## Task 14: Closing the window during a run

Spec section 9 and decisions F6, F7. The collision C1 left behind.

**Files:**
- Modify: `app/XrplFile.App/App.xaml.cs`
- Modify: `app/XrplFile.App/MauiProgram.cs` (the remarks that describe the close sequence)

**Interfaces:**
- Consumes: `PublicationRun.StopAsync(TimeSpan)` from Task 9.

- [ ] **Step 1: Stop the run before locking**

`App.xaml.cs` currently takes `IKeyVault` and `VaultAutoLock`. Add `PublicationRun`, and make
`OnWindowDestroying` do this, in this order:

```csharp
    /// <summary>
    /// How long a closing window waits for a running publication to stop. Bounded on purpose: a run
    /// that will not stop must not hold the window open forever, and when this expires the lock
    /// proceeds anyway — which is exactly C1's behaviour and no worse than it.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private async void OnWindowDestroying(object? sender, EventArgs e)
    {
        try
        {
            // Before the lock, not after. Locking first would land while a publication was still
            // going, and the run would die at its next signature with VaultError.Locked -- mid
            // batch, with tickets live and money already spent. Stopping first leaves the state file
            // part A writes, so the run is in the unfinished list next time the application opens.
            await _run.StopAsync(StopTimeout).ConfigureAwait(false);
            await _vault.LockAsync().ConfigureAwait(false);
        }
        finally
        {
            await _autoLock.DisposeAsync().ConfigureAwait(false);
        }
    }
```

Keep whatever `try`/`catch` shape the existing handler has — this step changes the ordering and adds
the stop, not the error handling.

- [ ] **Step 2: Find out whether the cancellable close event is reachable**

`Window.Destroying` cannot refuse a close. The cancellable event is WinUI's `AppWindow.Closing`,
reached through the MAUI window's platform handler. Before writing any of it, confirm against the
installed Windows App SDK that the path exists:

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
```

then, in a scratch edit, check that `window.Handler?.PlatformView` casts to
`Microsoft.UI.Xaml.Window` and that its `AppWindow` property exposes a `Closing` event whose
argument carries `Cancel`. If any link in that chain is absent on the installed SDK, **stop here**:
Step 1 alone is a correct application, record in the ledger that the confirmation could not be
wired and why, and skip Step 3. What is lost is the question, not the safety.

- [ ] **Step 3: Ask before closing, when the hook is there**

Inside `#if WINDOWS`, attach the handler when the window is created, and:

1. if `PublicationRun.Snapshot.State != PublicationRunState.Running`, do nothing — the close goes
   through as it always did;
2. otherwise set `args.Cancel = true`, show a confirmation naming the file being published and
   saying the run will be stopped and can be resumed later;
3. on confirmation, `await run.StopAsync(StopTimeout)` and then close the window for real, taking
   care not to re-enter the handler — a flag set before the second close is enough;
4. on refusal, leave the window open.

Keep this code as small as it can be. Every decision it might make — whether a run is going, what
stopping means, how long to wait — is already in `PublicationRun` where tests can reach it, and
nothing in CI can exercise this file.

- [ ] **Step 4: Build and check by hand**

```bash
dotnet build app/XrplFile.App/XrplFile.App.csproj --configuration Release
```

Then run the application on Windows against a standalone stand and, with a publication going, close
the window: the confirmation appears, refusing keeps the window open with the run still going, and
confirming stops the run and closes. Reopen and check the run is in the unfinished list. Record what
was actually observed — if the stand is not available, say that the hand check was not run rather
than implying it was.

- [ ] **Step 5: Commit**

```bash
git add app/XrplFile.App/App.xaml.cs app/XrplFile.App/MauiProgram.cs
git commit -m "fix(app): a closing window stops a running publication before it locks the vault"
```

---

## Task 15: The whole suite, and the status tables

- [ ] **Step 1: Run every suite**

```bash
dotnet build XrplFile.Libraries.slnf --configuration Release
dotnet run --project tests/XrplFile.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.Keys.Tests --configuration Release --no-build -- -trait- "Category=Integration"
dotnet run --project tests/XrplFile.App.Core.Tests --configuration Release --no-build -- -trait- "Category=Integration"
```

Expected: `XrplFile.Core.Tests` 294, `XrplFile.Keys.Tests` 244, and `XrplFile.App.Core.Tests` at
189 plus everything this plan added. A Core or Keys count that moved means this branch changed
behaviour it was not supposed to touch — investigate before going on.

```bash
cd web && node --test test/*.test.mjs
```
Expected: 74 passing.

- [ ] **Step 2: Update the status tables**

In `docs/superpowers/specs/2026-09-15-publishing-design.md`, change C2's row in the table to
`shipped`. Make the same change to C2's row in
`docs/superpowers/specs/2026-09-14-app-shell-and-vault-design.md` and
`docs/superpowers/specs/2026-09-13-key-vault-design.md`, so the three tables agree.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs
git commit -m "docs(specs): part C2 is shipped"
```

---

## Self-Review

**Spec coverage.** Section 1's scope → Tasks 7–13; the three "also in" items → Tasks 14 (close), 9
(idle lock, `Observe`), 1 (doubles). F1/F2 → Task 4. F3 → Task 7 (`request.Network` comes from
`SelectedKey.Network`). F4 → Task 2. F5/F6 → Task 9. F7 → Task 14. F8 → Task 5. F9 → Task 8.
F10 → Task 3. F11 → Tasks 6 and 12. F12 → Task 6 (`RunPaths`). F13 → Task 13 (the shell's timer,
reused). F14 → Task 9 (`Cancel` and `StopAsync` share one token). Section 4.1's warning → Task 7.
Section 4.4/11 → Task 11. Section 4.5 → Task 12. Section 7's list → Task 5. Section 12 → Task 6.
Section 13 → Task 1. Section 14's test list → distributed across the tasks that own each item.

**Known gaps, stated rather than hidden.** Two places in this plan hand the implementer a decision
instead of finished code, because the answer is not knowable from here: whether `FakeLedgerSource`
already exposes account balances (Task 8, Step 1) and whether WinUI's cancellable close is reachable
on the installed SDK (Task 14, Step 2). Both say what to do in either case. One place asks for a
test the plan describes but does not write out: the readable-resume test in Task 11, whose setup is
Task 9's cancellation test — it is spelled out in prose because it is the composition of two tests
that are written in full.

**Type consistency.** `PublishRequest` is constructed in Tasks 7, 8 (test), 9 (test) and 11 with the
same six positional fields. `RunSnapshot` is read in Tasks 9 and 10 only. `PublishPolicy.Create`
takes `(PublicationEstimate, ILedgerConnection, ITransactionSigner)` in Task 4 and is called with
exactly that in Task 9. `RunPaths.StateDirectory` feeds `SessionOptions.StateDirectory` in Tasks 8,
9 and `PublicationSession.Unfinished` in Task 11. `PublicationHistory.Append` takes the
`PublicationRecord` defined in Task 6 and is called with it in Task 9. `UnfinishedRow` carries
`KeyReference` and `Type` in its declaration, in `Refresh`, and in the request `ResumeAsync` builds —
all three in Task 11, because `PublicationRun` builds its `VaultSigner` from `request.KeyReference`
and an empty one would fail at the first signature.
