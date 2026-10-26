# Streaming Publication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish files up to 16 GiB through Batch with memory bounded by a round, not by the file: the file is read chunk by chunk from a held handle, memos are built on demand, per-chunk tracking is about 1.1 bytes, verification is deterministic plus a seeded sample, the run is priced in time as well as money, each session spends only what was approved, and the computer stays awake while it works.

**Architecture:** Core gains `IPublicationSource` (`FilePublicationSource`, `MemoryPublicationSource`), a streaming block-hash pass, a lazy memo list and compact landing structures (`LandedSet`, `ChunkLanding`, `SubmittedMemos`) that replace every per-chunk hash list. `BatchStrategy` runs capped rounds over compact sweep records; reconciliation streams the writer's history with filters and matches the manifest in a second streaming pass; verification is S9(a) checks plus an S21 sample through the reader, then an S22 check of the landed manifest. The application holds the file from the estimate on, carries an immutable `SpendingApproval` per session, shows a cost-and-time summary with a checkbox above a threshold, keeps the machine awake on a dedicated thread, touches the vault's idle lock through an activity lease and stops a run on Lock.

**Tech Stack:** .NET 10, C# latest, `Xrpl` 11.2.0 (pinned in `Directory.Packages.props`), System.Text.Json, System.Threading.Channels (in the BCL), xUnit v3 (`dotnet run` runner), MAUI Blazor (Windows head), Node 22+ `node:test` for `web/`, Docker stand `xrpl-file-ci`, testnet for acceptance.

**Spec:** `docs/superpowers/specs/2026-10-23-streaming-publication-design.md` (decisions S1–S27; S10 removed). It builds on `docs/superpowers/specs/2026-10-15-batch-publishing-design.md` (B1–B30 with its amendments) and `docs/superpowers/specs/2026-10-12-leftover-tickets-design.md` (I12). Where this plan departs from or fills a gap in the spec, the item is listed under **Spec questions** at the end with its ruling, and the plan follows the ruling.

## Global Constraints

- Repository: `E:\GIT\XRPL\xrpl-file`, branch as the executing session decides. Every path below is relative to it.
- `Directory.Build.props`: `net10.0`, `LangVersion latest`, `Nullable enable`, `TreatWarningsAsErrors true`, `GenerateDocumentationFile true`. Every `<see cref>`, `<paramref>` and `<param>` must resolve or the build fails. Many XML docs cite `PublicationSession.StartAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, string, string, string, string, SessionOptions, CancellationToken)` and `ResumeAsync(ISubmissionStrategy, ILedgerSource, ReadOnlyMemory{byte}, string, SessionOptions, CancellationToken)`: those `ReadOnlyMemory<byte>` overloads stay (spec 4.1), so the crefs keep resolving.
- No new NuGet packages. `System.Threading.Channels` is part of the shared framework.
- Tests are xUnit v3 with the built-in `Assert`. `dotnet test` does not work in this repository. Run: `dotnet run --project tests/<Project> -- -class <FullName>`, `-method <FullName>`, `-trait- "Category=Integration"` (unit only; from Task 20 on, `-trait- "Category=Integration" -trait- "Category=Slow"`, or the unit run starts the memory probe, which takes hours). Add `-showLiveOutput` when a step says to read output. Web: `cd web && node --test test/*.test.mjs`.
- Integration tests carry `[Trait("Category", "Integration")]` and `[Collection("Standalone")]` and read the node from `XRPL_FILE_NODE_URL`, else `ws://localhost:6006`. On this host the stand runs on `ws://localhost:16006`: `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d`, then set `XRPL_FILE_NODE_URL=ws://localhost:16006` for every stand run. Never start, stop or restart a compose project other than `xrpl-file-ci`; check with `docker ps --format '{{.Names}} {{.Label "com.docker.compose.project"}}'` first.
- Slow tests carry `[Trait("Category", "Slow")]`; CI excludes them (Task 20 adds the filter).
- Code style: explicit types (no `var`); `async`/`await` with `CancellationToken` passed through; `ConfigureAwait(false)` in library code (`src/`, `app/XrplFile.App.Core`); comments explain why, never what; English only; match the surrounding idiom (long XML remarks on public types, `// ...` only where the neighbours have them). Core builds transactions as `Dictionary<string, object>`.
- Exact values, verbatim from the spec: `MaxPublicationBytes` = 16 GiB (`16L * 1024 * 1024 * 1024`); `TicketPathMaxBytes` = 64 MiB (`64L * 1024 * 1024`); `MaxRoundMemos` = 16,384; trace ring and trace channel 10,000 entries each; reader history cache 4,096 records; Batch writing 85 and 70 memos/s; ticket writing 2 and 1 memos/s; sequence one memo per ledger; `LedgerTiming.ApproximateLedgerInterval` × 3 per round; reader 0.7 s per 400 records; sampled block ≈ 1,024 records; confirmation threshold: longer duration above 1 hour, or either account's worst case above 1 XRP (1,000,000 drops); activity lease touches immediately, then once a minute; `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` = `0x80000001`, cleared with `ES_CONTINUOUS` = `0x80000000`.
- Exact sentences, verbatim: "Choose a payer — files larger than 64 MiB publish through Batch."; "This application publishes files up to 16 GiB."; "I understand the cost and the time"; "Fees or balances changed since this was approved. Estimate it again."; "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key."; "Hashing the file — 412,000 of 1,689,810 chunks" (the shape: `Hashing the file — {done:N0} of {total:N0} chunks`); "Reading the ledger history".
- State JSON names, verbatim: version 2 and 3 no longer write `ChunkHashes` and still read files that carry it; new optional `ManifestStatus` (value `"landed, rejected"`) and `Failure` (value `"WriterWindowConflict"`), written only when set.
- Every new `XrplFileError` value gets its sentence in both message classes in the same task (spec B22): its own sentence in `PublishErrorMessages`, and in `ReadErrorMessages` a place in the publishing-only arm (reading cannot produce it). Each class's exhaustiveness test (`PublishErrorMessagesTests`, and `ReadErrorMessagesTests.Every_error_has_a_sentence`, whose switch throws for an unknown value) fails otherwise.
- `XrplFile.App.Core` sees only Core's public API (Core's `InternalsVisibleTo` names `XrplFile.Core.Tests` and `XrplFile.TestSupport` only).
- Save new and edited files as UTF-8; the literals carry "—", "…" and "·".
- Commits: conventional, English, subject and body; no attribution lines of any kind (no `Co-Authored-By`, no "Generated with").
- Desktop builds run only in the MauiLab VM (skill `maui-vm-lab`); never launch `XrplFile.App.exe` on the host. Building the app project on the host is fine: `dotnet build app/XrplFile.App/XrplFile.App.csproj`.

## Review Focus

1. **A file whose size is an exact multiple of a chunk or of a block** (999, 1,000, 1,001, 1,024,000 and 1,024,001 bytes): the streaming root equals the in-memory root, the last memo of the lazy list is the last chunk and no empty chunk is invented — tests in Tasks 1 and 3.
2. **A pass that leaves exactly one memo for its last round** (`MaxRoundMemos` + 1 memos): the strategy sends that memo as a solo after the full round, and the estimator counts it as a solo the same way — tests in Tasks 4 and 10.
3. **The held file is renamed or deleted from Explorer while the estimate or the run holds it**: Windows refuses, and the bytes published are the bytes estimated — test in Task 1 (rename and delete refused while held).
4. **The person leaves the Estimate screen while the estimate is still being taken**: the source that estimate opened is disposed when it completes, not left open until the next estimate — test in Task 12.
5. **Lock pressed while a resume waits for the person's confirmation**: the run cancels, the confirmation disappears from the run screen, the state file is unchanged and the run stays resumable — test in Task 17.

---

## File Structure

**Core (`src/XrplFile.Core`)**

| File | Responsibility | Task |
|---|---|---|
| `IPublicationSource.cs` (new) | the interface (S2) | 1 |
| `ChunkReads.cs` (new, internal) | chunk offsets, checked arithmetic, the fill loop shared by sources | 1 |
| `FilePublicationSource.cs` (new) | one `SafeFileHandle`, `FileShare.Read`, seekable only | 1 |
| `MemoryPublicationSource.cs` (new) | wraps `ReadOnlyMemory<byte>` | 1 |
| `FileTree.cs` | + `BlockHashesAsync`, `BlockHashes`, `RootHexOf`, `TryDecodeFragment` | 1, 6 |
| `ManifestPlan.cs` | `Entries(blockHashes, blockFirstLedgers)` | 1 |
| `LedgerRange.cs`, `LandedSet.cs`, `ChunkLanding.cs`, `SubmittedMemos.cs` (new) | compact per-chunk and per-block facts (S19, S23) | 2 |
| `ISubmissionStrategy.cs` | `SubmitAllAsync` returns `SubmittedMemos`; `SubmittedMemo` removed | 2 |
| `TicketStrategy.cs`, `SequenceStrategy.cs` | return `SubmittedMemos` | 2 |
| `XrplFileWriter.cs` | results as counts (S8); on a source with a lazy list (4.3); verification S9/S21/S22 | 2, 3, 7 |
| `PublicationReport.cs` | `ChunkCount`, `ListCount` replace the hash lists | 2 |
| `PublicationState.cs` | drops `ChunkHashes`; + `ManifestStatus`, `Failure` | 2, 5, 7 |
| `IndexRanges.cs`, `LazyMemoList.cs` (new, internal) | missing chunks as ranges; memos built when read (S4) | 3 |
| `PublicationSession.cs` | source overloads, streaming root, preparation progress, conflict and rejection records, streaming reconciliation, approvals | 3, 5, 6, 7, 11 |
| `BatchStrategy.cs`, `BatchRound.cs`, `BatchSweep.cs`, `BatchRegistry.cs`, `BatchProgress.cs`, `BatchStrategyOptions.cs` | capped rounds, compact records, byte candidate counts | 4 |
| `SweepRecord.cs`, `RoundMarkers.cs` (new, internal) | the S6 compact record; a round's marker map | 4 |
| `LedgerTransaction.cs`, `XrplClientLedgerSource.cs` | + `TransactionIndex` | 4 |
| `XrplFileException.cs` | + `SourceChanged`; + `WriterWindowConflict`, `ManifestRejected`, `ApprovalExceeded`; + `Link` | 3, 5, 7, 11 |
| `Reconciliation.cs`, `SequenceRanges.cs` (new, internal) | streaming filtered reconciliation (S20) | 6 |
| `PublicationCheck.cs` (new, internal) | S9(a) checks, the list against the ledger, and the S21 sample | 7 |
| `BatchTrace.cs` | ring of 10,000 and a listener (S18) | 8 |
| `ForwardHistory.cs`, `XrplFileReader.cs` | the S26 cache | 9 |
| `PublicationEstimate.cs`, `BatchPublicationEstimate.cs` | grouping per round; `Rounds` (S11) | 10 |
| `SpendingApproval.cs` (new) | `SpendingApproval`, `ResumeQuote`, `ResumeApproval` (S25) | 11 |

**Test support (`tests/XrplFile.TestSupport`)**: `VectorPublicationSource.cs`, `CountingPublicationSource.cs` (Task 1).

**App core (`app/XrplFile.App.Core`)**: `PublishRequest.cs`, `PublishModel.cs`, `PublishPolicy.cs`, `EstimateModel.cs`, `PublicationRun.cs`, `UnfinishedModel.cs` (12); `PublicationDuration.cs`, `CostSummary.cs`, `DurationText.cs`, `EstimateModel.cs` (13); `RunModel.cs`, `RunSnapshot` in `PublicationRun.cs` (14); `RunTraceChannel.cs`, `RunTraceLog.cs` (8); `KeepAwake.cs` (16); `ActivityLease.cs`, `UnlockModel.cs` (17); `ReadModel.cs`, `ReadRun.cs` (18); `PublishErrorMessages.cs` (5, 7, 11, 15); `ReadErrorMessages.cs` (5, 7, 11).

**App (`app/XrplFile.App`)**: `Platform/WindowsExecutionState.cs` (16), `MauiProgram.cs` (16, 17), `Components/Estimate.razor` (12, 13), `Run.razor` (14), `Read.razor` (18), `Publish.razor` (15).

**Web (`web/`)**: `src/history.mjs`, `src/reader.mjs`, `test/history.test.mjs` (9).

**Memory probe (`tests/XrplFile.MemoryProbe`, new)**: the child process of S27 (20).

---

### Task 1: Publication sources and the streaming tree (S2, S3)

**Files:**
- Create: `src/XrplFile.Core/IPublicationSource.cs`, `src/XrplFile.Core/ChunkReads.cs`, `src/XrplFile.Core/FilePublicationSource.cs`, `src/XrplFile.Core/MemoryPublicationSource.cs`
- Create: `tests/XrplFile.TestSupport/VectorPublicationSource.cs`, `tests/XrplFile.TestSupport/CountingPublicationSource.cs`
- Modify: `src/XrplFile.Core/FileTree.cs` (add three members), `src/XrplFile.Core/ManifestPlan.cs` (`Entries`), `src/XrplFile.Core/XrplFileWriter.cs:213` and `src/XrplFile.Core/PublicationSession.cs:664,856-869` (callers of `Entries` and `RootOf`), `src/XrplFile.Core/PublicationSession.cs` (add `MaxPublicationBytes`, `RootOfAsync`)
- Test: `tests/XrplFile.Core.Tests/FilePublicationSourceTests.cs`, `tests/XrplFile.Core.Tests/FileTreeStreamingTests.cs`

**Interfaces:**
- Consumes: `FileTree.ChunkCount`, `FileTree.BlockCount`, `FileTree.BlockChunks`, `MerkleTree.Leaf`, `MerkleTree.Root`, `MemoLayout.ChunkBytes`, `VectorStream`.
- Produces:
  - `public interface IPublicationSource : IAsyncDisposable { long Length { get; } int ReadChunk(long index, Span<byte> destination); ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken); }`
  - `public sealed class FilePublicationSource : IPublicationSource` — `static FilePublicationSource Open(string path)`, `FilePublicationSource(SafeFileHandle handle)` (takes ownership), `string? Path { get; }`, internal `FilePublicationSource(IPositionalReader reader, long length)`.
  - `internal interface IPositionalReader { int Read(Span<byte> buffer, long offset); ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken); }`
  - `public sealed class MemoryPublicationSource(ReadOnlyMemory<byte> bytes) : IPublicationSource`
  - `internal static class ChunkReads { long ChunkCount(long length); (long Offset, int Length) Locate(long length, long index); }`
  - `FileTree.BlockHashesAsync(IPublicationSource source, IProgress<long>? progress, CancellationToken cancellationToken) : Task<IReadOnlyList<byte[]>>` (progress = chunks hashed so far), `FileTree.BlockHashes(IReadOnlyList<byte[]> chunks) : IReadOnlyList<byte[]>`, `FileTree.RootHexOf(IReadOnlyList<byte[]> blockHashes) : string`
  - `ManifestPlan.Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers) : IReadOnlyList<BlockEntry>` (the old `Entries(chunks, chunkLedgers)` is removed)
  - `PublicationSession.MaxPublicationBytes = 16L * 1024 * 1024 * 1024` (public const), `PublicationSession.RootOfAsync(IPublicationSource file, IProgress<long>? progress, CancellationToken cancellationToken) : Task<string>`
  - TestSupport: `VectorPublicationSource(string seed, long length)` (bytes of `VectorStream.Bytes(seed, length)` computed on demand, never held), `CountingPublicationSource(IPublicationSource inner)` with `int Reads` and `bool Disposed`.

- [ ] **Step 1: Write the failing source tests**

Create `tests/XrplFile.Core.Tests/FilePublicationSourceTests.cs`:

```csharp
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace XrplFile.Tests;

public sealed class FilePublicationSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-source-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Written(long size, string seed = "source")
    {
        string path = Path.Combine(_directory, $"{seed}-{size}.bin");
        File.WriteAllBytes(path, VectorStream.Bytes(seed, size));
        return path;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(2_500)]
    public async Task Every_chunk_reads_back_as_the_file_has_it_sync_and_async(long size)
    {
        byte[] file = VectorStream.Bytes("source", size);
        await using FilePublicationSource source = FilePublicationSource.Open(Written(size));
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        long chunks = FileTree.ChunkCount(size, MemoLayout.ChunkBytes);

        Assert.Equal(size, source.Length);

        for (long index = 0; index < chunks; index++)
        {
            int expected = (int)Math.Min(MemoLayout.ChunkBytes, size - index * MemoLayout.ChunkBytes);

            Assert.Equal(expected, source.ReadChunk(index, buffer));
            Assert.Equal(file.AsSpan((int)(index * MemoLayout.ChunkBytes), expected).ToArray(), buffer.AsSpan(0, expected).ToArray());

            Assert.Equal(expected, await source.ReadChunkAsync(index, buffer, Token));
            Assert.Equal(file.AsSpan((int)(index * MemoLayout.ChunkBytes), expected).ToArray(), buffer.AsSpan(0, expected).ToArray());
        }
    }

    [Fact]
    public void The_short_final_chunk_leaves_the_rest_of_a_full_buffer_untouched()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        Array.Fill(buffer, (byte)0xEE);

        Assert.Equal(500, source.ReadChunk(2, buffer));
        Assert.All(buffer.AsSpan(500).ToArray(), value => Assert.Equal(0xEE, value));
    }

    [Fact]
    public void A_destination_shorter_than_the_chunk_is_refused()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));

        Assert.Throws<ArgumentException>(() => source.ReadChunk(0, new byte[999]));
        Assert.Equal(500, source.ReadChunk(2, new byte[500]));
    }

    [Fact]
    public void A_chunk_past_the_end_is_refused()
    {
        using FilePublicationSource source = FilePublicationSource.Open(Written(2_500));

        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(3, new byte[MemoLayout.ChunkBytes]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(-1, new byte[MemoLayout.ChunkBytes]));
    }

    [Fact]
    public async Task Partial_reads_are_retried_until_the_chunk_is_full()
    {
        byte[] file = VectorStream.Bytes("partial", 2_500);
        await using FilePublicationSource source = new(new TrickleReader(file, most: 7), file.Length);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        Assert.Equal(1_000, source.ReadChunk(1, buffer));
        Assert.Equal(file.AsSpan(1_000, 1_000).ToArray(), buffer);

        Assert.Equal(500, await source.ReadChunkAsync(2, buffer, Token));
        Assert.Equal(file.AsSpan(2_000, 500).ToArray(), buffer.AsSpan(0, 500).ToArray());
    }

    [Fact]
    public async Task A_file_cut_short_under_the_source_throws_IOException_instead_of_inventing_bytes()
    {
        string path = Written(2_500);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using FilePublicationSource source = new(handle);

        using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.SetLength(1_200);
        }

        Assert.Equal(1_000, source.ReadChunk(0, new byte[MemoLayout.ChunkBytes]));
        Assert.Throws<IOException>(() => source.ReadChunk(1, new byte[MemoLayout.ChunkBytes]));
        await Assert.ThrowsAsync<IOException>(async () => await source.ReadChunkAsync(2, new byte[MemoLayout.ChunkBytes], Token));
    }

    [Fact]
    public async Task While_held_nobody_else_can_write_the_file()
    {
        string path = Written(2_500);
        await using FilePublicationSource source = FilePublicationSource.Open(path);

        Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
    }

    [Fact]
    public async Task While_held_the_file_cannot_be_deleted_or_renamed_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "delete and rename sharing is what Windows' sharing modes give (spec S2)");

        string path = Written(2_500);
        await using FilePublicationSource source = FilePublicationSource.Open(path);

        Assert.ThrowsAny<IOException>(() => File.Delete(path));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
    }

    [Fact]
    public async Task After_disposal_the_file_is_free_again()
    {
        string path = Written(2_500);
        FilePublicationSource source = FilePublicationSource.Open(path);
        await source.DisposeAsync();

        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => source.ReadChunk(0, new byte[MemoLayout.ChunkBytes]));
    }

    [Fact]
    public void A_handle_that_cannot_seek_is_refused()
    {
        using AnonymousPipeServerStream pipe = new(PipeDirection.Out);
        using SafeFileHandle handle = new(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: false);

        Assert.Throws<ArgumentException>(() => new FilePublicationSource(handle));
    }

    [Fact]
    public async Task A_memory_source_reads_the_same_chunks()
    {
        byte[] file = VectorStream.Bytes("memory", 2_500);
        await using MemoryPublicationSource source = new(file);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        Assert.Equal(2_500, source.Length);
        Assert.Equal(500, await source.ReadChunkAsync(2, buffer, Token));
        Assert.Equal(file.AsSpan(2_000).ToArray(), buffer.AsSpan(0, 500).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadChunk(3, buffer));
    }

    /// <summary>A file that never hands out more than <paramref name="most"/> bytes per read, as a slow network share might.</summary>
    private sealed class TrickleReader(byte[] file, int most) : IPositionalReader
    {
        public int Read(Span<byte> buffer, long offset)
        {
            int take = (int)Math.Min(Math.Min(most, buffer.Length), Math.Max(0, file.Length - offset));
            file.AsSpan((int)offset, take).CopyTo(buffer);
            return take;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span, offset));
    }
}
```

`FilePublicationSource` implements `IDisposable` as well as `IAsyncDisposable` (Step 3), which is what lets a synchronous test use `using`.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FilePublicationSourceTests`
Expected: build error, `FilePublicationSource` and `IPublicationSource` do not exist.

- [ ] **Step 3: Write the sources**

`src/XrplFile.Core/IPublicationSource.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// The bytes of one publication, read a chunk at a time (spec S2), so that no part of the
/// publication path has to hold the file. A session borrows a source; whoever created it owns it
/// and disposes it.
/// </summary>
public interface IPublicationSource : IAsyncDisposable
{
    /// <summary>The file's length in bytes, as it was when the source was opened.</summary>
    long Length { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with chunk <paramref name="index"/>: exactly
    /// min(<see cref="MemoLayout.ChunkBytes"/>, <see cref="Length"/> − index × <see cref="MemoLayout.ChunkBytes"/>)
    /// bytes, retrying reads that return fewer, and returns that count. Bytes of
    /// <paramref name="destination"/> past the count are left as they were.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The file has no such chunk.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the chunk.</exception>
    /// <exception cref="IOException">The file ended before the chunk did.</exception>
    int ReadChunk(long index, Span<byte> destination);

    /// <summary>The asynchronous form of <see cref="ReadChunk"/>, with the same contract.</summary>
    ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken);
}
```

`src/XrplFile.Core/ChunkReads.cs`:

```csharp
namespace XrplFile;

/// <summary>Where a chunk sits in a file, in checked arithmetic, and the checks every source makes before reading it.</summary>
internal static class ChunkReads
{
    public static long ChunkCount(long length) => FileTree.ChunkCount(length, MemoLayout.ChunkBytes);

    /// <summary>The chunk's first byte and its length; a chunk the file does not have is refused.</summary>
    public static (long Offset, int Length) Locate(long length, long index)
    {
        long count = ChunkCount(length);

        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the file has {count} chunks");
        }

        long offset = checked(index * MemoLayout.ChunkBytes);
        return (offset, (int)Math.Min(MemoLayout.ChunkBytes, length - offset));
    }

    public static void RequireRoom(int chunkLength, int destinationLength)
    {
        if (destinationLength < chunkLength)
        {
            throw new ArgumentException($"the destination holds {destinationLength} bytes and the chunk is {chunkLength}", "destination");
        }
    }

    public static IOException EndedEarly(long index, long at, long length) =>
        new($"the file ended at byte {at}, inside chunk {index}; it was {length} bytes when it was opened");
}
```

`src/XrplFile.Core/FilePublicationSource.cs`:

```csharp
using Microsoft.Win32.SafeHandles;

namespace XrplFile;

/// <summary>Positional reads, so a test can stand in for a file that answers in pieces.</summary>
internal interface IPositionalReader
{
    int Read(Span<byte> buffer, long offset);

    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken);
}

/// <summary>
/// A file held open for the whole publication through one <see cref="SafeFileHandle"/> (spec S2,
/// S24). The handle shares reading only: on local Windows file systems nobody else can write,
/// delete or replace the file by rename while it is held, so the bytes that were estimated are the
/// bytes that are published. Positional reads on one handle are thread-safe, so the strategy and
/// the writer can read it at once. Other file systems give what their driver gives.
/// </summary>
public sealed class FilePublicationSource : IPublicationSource, IDisposable
{
    private readonly SafeFileHandle? _handle;
    private readonly IPositionalReader _reader;
    private int _disposed;

    /// <summary>Takes ownership of <paramref name="handle"/>: disposing the source closes it.</summary>
    /// <exception cref="ArgumentException">The handle is a pipe or a socket, which cannot be read at an offset.</exception>
    public FilePublicationSource(SafeFileHandle handle)
        : this(handle, path: null)
    {
    }

    internal FilePublicationSource(IPositionalReader reader, long length)
    {
        _reader = reader;
        Length = length;
    }

    private FilePublicationSource(SafeFileHandle handle, string? path)
    {
        ArgumentNullException.ThrowIfNull(handle);

        long length;

        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch (NotSupportedException e)
        {
            throw new ArgumentException("a publication is read at offsets, and this handle is a pipe or a socket", nameof(handle), e);
        }

        _handle = handle;
        _reader = new HandleReader(handle);
        Length = length;
        Path = path;
    }

    public long Length { get; }

    /// <summary>The full path it was opened from, or null when it was built from a handle.</summary>
    public string? Path { get; }

    /// <summary>Opens the file for reading and denies other writers, deletes and renames while it is held.</summary>
    public static FilePublicationSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string full = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            return new FilePublicationSource(handle, full);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public int ReadChunk(long index, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);

        for (int filled = 0; filled < length;)
        {
            int read = _reader.Read(destination.Slice(filled, length - filled), offset + filled);
            if (read == 0) throw ChunkReads.EndedEarly(index, offset + filled, Length);
            filled += read;
        }

        return length;
    }

    public async ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);

        for (int filled = 0; filled < length;)
        {
            int read = await _reader.ReadAsync(destination.Slice(filled, length - filled), offset + filled, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw ChunkReads.EndedEarly(index, offset + filled, Length);
            filled += read;
        }

        return length;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _handle?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class HandleReader(SafeFileHandle handle) : IPositionalReader
    {
        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) =>
            RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);
    }
}
```

`src/XrplFile.Core/MemoryPublicationSource.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// Bytes already in memory as a source (spec 4.1): what the <see cref="ReadOnlyMemory{T}"/>
/// overloads of the session and the writer wrap, and what tests use.
/// </summary>
public sealed class MemoryPublicationSource(ReadOnlyMemory<byte> bytes) : IPublicationSource, IDisposable
{
    private readonly ReadOnlyMemory<byte> _bytes = bytes;

    public long Length => _bytes.Length;

    public int ReadChunk(long index, Span<byte> destination)
    {
        (long offset, int length) = ChunkReads.Locate(Length, index);
        ChunkReads.RequireRoom(length, destination.Length);
        _bytes.Span.Slice((int)offset, length).CopyTo(destination);
        return length;
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadChunk(index, destination.Span));
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Run the source tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FilePublicationSourceTests`
Expected: PASS (the rename/delete test is skipped off Windows).

- [ ] **Step 5: Write the failing streaming-tree tests**

Create `tests/XrplFile.Core.Tests/FileTreeStreamingTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class FileTreeStreamingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(1_001)]
    [InlineData(1_024_000)]
    [InlineData(1_024_001)]
    [InlineData(2_048_000)]
    [InlineData(2_048_999)]
    [InlineData(3_000_017)]
    public async Task The_streaming_root_equals_the_in_memory_root(long size)
    {
        byte[] file = VectorStream.Bytes("tree", size);
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        await using VectorPublicationSource source = new("tree", size);

        IReadOnlyList<byte[]> streamed = await FileTree.BlockHashesAsync(source, null, Token);

        Assert.Equal(FileTree.BlockCount(chunks.Count), streamed.Count);
        Assert.Equal(FileTree.BlockHashes(chunks), streamed);
        Assert.Equal(PublicationSession.RootOf(file), FileTree.RootHexOf(streamed));
        Assert.Equal(PublicationSession.RootOf(file), await PublicationSession.RootOfAsync(source, null, Token));
    }

    [Fact]
    public async Task Progress_counts_the_chunks_hashed_after_every_block()
    {
        await using VectorPublicationSource source = new("tree", 2_500_500);
        List<long> reports = [];

        await FileTree.BlockHashesAsync(source, new SynchronousProgress<long>(reports.Add), Token);

        Assert.Equal([1_024L, 2_048L, 2_501L], reports);
    }

    [Fact]
    public async Task The_streaming_pass_reads_every_chunk_once()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("tree", 2_500_500));

        await FileTree.BlockHashesAsync(source, null, Token);

        Assert.Equal(2_501, source.Reads);
    }

    [Fact]
    public async Task An_empty_source_has_no_root()
    {
        await using MemoryPublicationSource source = new(Array.Empty<byte>());

        await Assert.ThrowsAsync<ArgumentException>(() => FileTree.BlockHashesAsync(source, null, Token));
    }

    [Fact]
    public void Entries_pair_each_block_hash_with_its_first_ledger()
    {
        byte[][] hashes = [MerkleTree.Leaf([1]), MerkleTree.Leaf([2])];

        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(hashes, [40, 41]);

        Assert.Equal(
            [new BlockEntry(0, Convert.ToHexString(hashes[0]), 40), new BlockEntry(1, Convert.ToHexString(hashes[1]), 41)],
            entries);
        Assert.Throws<ArgumentException>(() => ManifestPlan.Entries(hashes, [40]));
    }
}
```

`SynchronousProgress<T>` already exists in `tests/XrplFile.TestSupport/SynchronousProgress.cs`.

- [ ] **Step 6: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FileTreeStreamingTests`
Expected: build error, `BlockHashesAsync`, `VectorPublicationSource` and the new `Entries` do not exist.

- [ ] **Step 7: Add the test sources to TestSupport**

`tests/XrplFile.TestSupport/VectorPublicationSource.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace XrplFile.Tests;

/// <summary>
/// <see cref="VectorStream.Bytes"/> of any length as a source, computed for each read and never
/// held: what a test of hundreds of megabytes publishes without allocating them.
/// </summary>
public sealed class VectorPublicationSource(string seed, long length) : IPublicationSource
{
    private const int BlockBytes = 32;
    private readonly byte[] _seed = Encoding.UTF8.GetBytes(seed);
    private int _reads;

    public long Length { get; } = length;

    public int Reads => Volatile.Read(ref _reads);

    public int ReadChunk(long index, Span<byte> destination)
    {
        long chunks = FileTree.ChunkCount(Length, MemoLayout.ChunkBytes);
        if (index < 0 || index >= chunks) throw new ArgumentOutOfRangeException(nameof(index), index, $"the file has {chunks} chunks");

        long offset = index * MemoLayout.ChunkBytes;
        int count = (int)Math.Min(MemoLayout.ChunkBytes, Length - offset);
        if (destination.Length < count) throw new ArgumentException("the destination is shorter than the chunk", nameof(destination));

        Span<byte> input = stackalloc byte[_seed.Length + 4];
        _seed.CopyTo(input);
        Span<byte> block = stackalloc byte[BlockBytes];

        for (int filled = 0; filled < count;)
        {
            long at = offset + filled;
            uint counter = (uint)(at / BlockBytes);
            int within = (int)(at % BlockBytes);

            input[^4] = (byte)(counter >> 24);
            input[^3] = (byte)(counter >> 16);
            input[^2] = (byte)(counter >> 8);
            input[^1] = (byte)counter;
            SHA256.HashData(input, block);

            int take = Math.Min(BlockBytes - within, count - filled);
            block.Slice(within, take).CopyTo(destination[filled..]);
            filled += take;
        }

        Interlocked.Increment(ref _reads);
        return count;
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadChunk(index, destination.Span));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`tests/XrplFile.TestSupport/CountingPublicationSource.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>Counts the chunks read through it and whether it was disposed.</summary>
public sealed class CountingPublicationSource(IPublicationSource inner) : IPublicationSource
{
    private int _reads;
    private int _disposed;

    public long Length => inner.Length;

    public int Reads => Volatile.Read(ref _reads);

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public int ReadChunk(long index, Span<byte> destination)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadChunk(index, destination);
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadChunkAsync(index, destination, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return inner.DisposeAsync();
    }
}
```

- [ ] **Step 8: Write the streaming tree, the new `Entries` and `RootOfAsync`**

In `src/XrplFile.Core/FileTree.cs`, after `BlockHash`, add:

```csharp
    /// <summary>Every block's hash, from chunks in memory. For small files and tests; publication streams (spec S3).</summary>
    public static IReadOnlyList<byte[]> BlockHashes(IReadOnlyList<byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        int blocks = BlockCount(chunks.Count);
        List<byte[]> hashes = new(blocks);

        for (int block = 0; block < blocks; block++)
        {
            hashes.Add(BlockHash([.. chunks.Skip(block * BlockChunks).Take(BlockChunks)]));
        }

        return hashes;
    }

    /// <summary>
    /// Every block's hash in one forward pass over the source, keeping only the 32 bytes of each
    /// (spec S3): 1,651 blocks of a 1.7 GB file are 53 KB. <paramref name="progress"/> hears the
    /// number of chunks hashed after each block.
    /// </summary>
    public static async Task<IReadOnlyList<byte[]>> BlockHashesAsync(
        IPublicationSource source, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        long chunks = ChunkCount(source.Length, MemoLayout.ChunkBytes);
        if (chunks == 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(source));

        int blocks = BlockCount(chunks);
        List<byte[]> hashes = new(blocks);
        List<byte[]> leaves = new(BlockChunks);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        for (int block = 0; block < blocks; block++)
        {
            leaves.Clear();
            long first = (long)block * BlockChunks;
            long end = Math.Min(first + BlockChunks, chunks);

            for (long chunk = first; chunk < end; chunk++)
            {
                int length = await source.ReadChunkAsync(chunk, buffer, cancellationToken).ConfigureAwait(false);
                leaves.Add(MerkleTree.Leaf(buffer.AsSpan(0, length)));
            }

            hashes.Add(MerkleTree.Root(leaves));
            progress?.Report(end);
        }

        return hashes;
    }

    /// <summary>The file's root over its block hashes, in block order.</summary>
    public static string RootHexOf(IReadOnlyList<byte[]> blockHashes) => Convert.ToHexString(MerkleTree.Root(blockHashes));
```

In `src/XrplFile.Core/ManifestPlan.cs`, replace `Entries(IReadOnlyList<byte[]> chunks, IReadOnlyDictionary<int, long> chunkLedgers)` with:

```csharp
    /// <summary>
    /// Block entries from block hashes and each block's first ledger (spec S3): where a reader starts
    /// paging for the block, the earliest ledger holding one of its adopted chunks.
    /// </summary>
    public static IReadOnlyList<BlockEntry> Entries(IReadOnlyList<byte[]> blockHashes, IReadOnlyList<long> blockFirstLedgers)
    {
        ArgumentNullException.ThrowIfNull(blockHashes);
        ArgumentNullException.ThrowIfNull(blockFirstLedgers);

        if (blockHashes.Count != blockFirstLedgers.Count)
        {
            throw new ArgumentException($"{blockHashes.Count} block hashes and {blockFirstLedgers.Count} first ledgers", nameof(blockFirstLedgers));
        }

        List<BlockEntry> entries = new(blockHashes.Count);
        for (int block = 0; block < blockHashes.Count; block++)
        {
            entries.Add(new BlockEntry(block, Convert.ToHexString(blockHashes[block]), blockFirstLedgers[block]));
        }

        return entries;
    }

    /// <summary>Each block's earliest chunk ledger, from per-chunk ledgers. Bounded use only: the one-account paths (64 MiB).</summary>
    public static IReadOnlyList<long> FirstLedgers(int chunkCount, IReadOnlyDictionary<int, long> chunkLedgers)
    {
        int blocks = FileTree.BlockCount(chunkCount);
        long[] firsts = new long[blocks];

        for (int block = 0; block < blocks; block++)
        {
            int first = block * FileTree.BlockChunks;
            int end = Math.Min(first + FileTree.BlockChunks, chunkCount);
            firsts[block] = Enumerable.Range(first, end - first).Min(index => chunkLedgers[index]);
        }

        return firsts;
    }
```

`FirstLedgers` keeps the two existing callers working until Task 2 replaces per-chunk ledgers with per-block ranges; Task 2 deletes it.

Update the callers:
- `src/XrplFile.Core/XrplFileWriter.cs:213`: `IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(FileTree.BlockHashes(chunks), ManifestPlan.FirstLedgers(chunks.Count, chunkLedgers));`
- `src/XrplFile.Core/PublicationSession.cs:664`: `IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(FileTree.BlockHashes(chunks), ManifestPlan.FirstLedgers(chunks.Count, chunkLedgers));`
- Replace the private `RootOf(IReadOnlyList<byte[]> chunks, long placeholderLedger)` (lines 856-869) with

```csharp
    /// <summary>The file's root, computed the way the writer will compute it -- over block hashes alone.</summary>
    private static string RootOf(IReadOnlyList<byte[]> chunks) => FileTree.RootHexOf(FileTree.BlockHashes(chunks));
```

  and change its three call sites to drop the ledger argument (`RootOf(chunks, startedIn)` → `RootOf(chunks)`, `RootOf(FileTree.Split(file, MemoLayout.ChunkBytes), 0)` → `RootOf(FileTree.Split(file, MemoLayout.ChunkBytes))`, `RootOf(chunks, state.StartedInLedger)` → `RootOf(chunks)`).

In `PublicationSession`, after `public static string RootOf(ReadOnlyMemory<byte> file)`, add:

```csharp
    /// <summary>
    /// The largest file a publication takes (spec S1): 16 GiB. Every chunk index stays an
    /// <see cref="int"/>, and per-chunk tracking is about 19 MB at this size.
    /// </summary>
    public const long MaxPublicationBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>The root a source will publish under, in one streaming pass (spec S3, S7).</summary>
    public static async Task<string> RootOfAsync(IPublicationSource file, IProgress<long>? progress, CancellationToken cancellationToken) =>
        FileTree.RootHexOf(await FileTree.BlockHashesAsync(file, progress, cancellationToken).ConfigureAwait(false));
```

- [ ] **Step 9: Run the new tests and the whole unit suite**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.FileTreeStreamingTests -class XrplFile.Tests.FilePublicationSourceTests`
Expected: PASS.
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS, the same count as before plus the new tests.

- [ ] **Step 10: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests tests/XrplFile.TestSupport
git commit -m "feat(core): publication sources and a streaming block-hash pass

A publication can now read its file a chunk at a time from one held handle
(FilePublicationSource, FileShare.Read) or from memory, and the root is
computed in one forward pass that keeps only each block's 32-byte hash.
ManifestPlan.Entries takes block hashes and per-block first ledgers."
```

---

### Task 2: Strategies hand back `SubmittedMemos`; results and state carry no per-chunk data (S23, S8, S19 types)

This is the breaking change to `ISubmissionStrategy`, applied to all three strategies, the writer, the session, every fake and every test in one task, so the suite is green at its end. The writer still splits the file in memory here; Task 3 moves it onto a source.

**Files:**
- Create: `src/XrplFile.Core/LedgerRange.cs`, `src/XrplFile.Core/LandedSet.cs`, `src/XrplFile.Core/ChunkLanding.cs`, `src/XrplFile.Core/SubmittedMemos.cs`
- Create: `tests/XrplFile.TestSupport/SubmittedMemosTesting.cs`, `tests/XrplFile.TestSupport/LedgerRecords.cs`
- Create: `tests/XrplFile.Core.Tests/fixtures/state/v2-tickets.json`, `v2-before-strategies.json`, `v2-done.json`, `v3-batch.json`
- Modify: `src/XrplFile.Core/ISubmissionStrategy.cs`, `SequenceStrategy.cs:53-77`, `TicketStrategy.cs:132-246`, `BatchStrategy.cs:72-101`, `XrplFileWriter.cs` (records `LandedMemos`, `PublishResult`, `PublishCoreAsync`), `PublicationReport.cs`, `PublicationState.cs`, `PublicationSession.cs` (`Reconcile`, `FindManifest`, `RunAsync`, `Advance`, the state constructors, the Batch resume's missing count), `ManifestPlan.cs` (delete `FirstLedgers`)
- Modify (tests): every file the compiler names — the known list is `tests/XrplFile.Core.Tests/{ScriptedStrategy,PublicationSessionTests,WriterReadBackTests,XrplFileWriterTests,WriterLedgerTests,VaultPublicationTests,ManifestReconciliationTests,PublicationSessionBatchResumeTests,PublicationSessionStandaloneTests,BatchPublicationStandaloneTests,BatchStrategyTests,BatchIdentityTests,BatchOutageTests,BatchRoundStartTests,TicketStrategyTests,TicketStrategyStandaloneTests,SequenceStrategyTests,PublicationStateTests}.cs`, `tests/XrplFile.App.Core.Tests/{PublicationRunBatchTests,UnfinishedModelBatchTests,UnfinishedModelTests,PublicationHistoryTests,RunModelEtaTests}.cs`, `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj`
- Test: `tests/XrplFile.Core.Tests/SubmittedMemosTests.cs`, `tests/XrplFile.Core.Tests/ChunkLandingTests.cs`, `tests/XrplFile.Core.Tests/LegacyStateTests.cs`

**Interfaces:**
- Consumes: Task 1's `ManifestPlan.Entries(blockHashes, blockFirstLedgers)`, `FileTree.BlockHashes`.
- Produces (all in namespace `XrplFile`):
  - `public readonly record struct LedgerRange(long First, long Last)` with `static LedgerRange Of(long ledger)`, `LedgerRange Including(long ledger)`, `LedgerRange Including(LedgerRange other)`.
  - `public sealed class LandedSet` — `LandedSet(int capacity)`, `int Capacity`, `int Count`, `bool Contains(int index)`, `int NextMissing(int from)` (−1 when none), `internal bool Add(int index)`. One owner, not thread-safe.
  - `public sealed class ChunkLanding` — `ChunkLanding(long chunkCount)`, `long ChunkCount`, `int BlockCount`, `long Landed`, `bool IsComplete`, `bool IsLanded(long chunk)`, `int LandedIn(int block)`, `bool TryBlockLedgers(int block, out LedgerRange range)`, `IReadOnlyList<long> BlockFirstLedgers()`, `LedgerRange Window()`, `internal bool Land(long chunk, long ledgerIndex)`, `internal bool MarkLanded(long chunk)`, `internal void Widen(int block, LedgerRange range)`.
  - `public sealed class SubmittedMemos` — `int Count`, `LandedSet Landed`, `bool TryLedgerOf(int index, out long ledgerIndex)`, `bool TryHashOf(int index, [NotNullWhen(true)] out string? hash)`, `IReadOnlyDictionary<int, LedgerRange> BlockLedgers` (file block → range of the chunk-marked memos whose ledger the strategy knew).
  - `internal sealed class SubmittedMemosBuilder(int count, bool keepChunkFacts)` — `LandedSet Landed`, `void Record(int index, string memoTypeHex, string hash, long? ledgerIndex)`, `SubmittedMemos Build()`.
  - `ISubmissionStrategy.SubmitAllAsync(IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken) : Task<SubmittedMemos>`. The record `SubmittedMemo` is deleted.
  - `public sealed record LandedMemos(ChunkLanding Chunks, IReadOnlyDictionary<int, LandedMemo> Lists)` — the writer completes `Chunks` in place.
  - `public sealed record PublishResult(XrplFileLink Link, Manifest Manifest, string Writer, long ChunkCount, int ListCount, string ManifestHash)`.
  - `public sealed record PublicationReport(XrplFileLink Link, Manifest Manifest, string Writer, long Transactions, long FeeDropsEstimated, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, long ChunkCount, int ListCount, string ManifestHash)` (init members unchanged).
  - `PublicationState` loses its `ChunkHashes` parameter: `PublicationState(int Version, string Network, string Writer, string KeyReference, string Type, string? Name, long Size, int Chunk, string? Root, long StartedInLedger, string Phase, string? Strategy = null, string? SourcePath = null, string? ManifestHash = null, string? Payer = null, string? PayerKeyReference = null, long? Fence = null, uint? StartSequence = null)`.
  - TestSupport: `public sealed record LandedMemoFacts(int Index, string TxHash, long? LedgerIndex)`; `public static IReadOnlyList<LandedMemoFacts> Each(this SubmittedMemos memos)` (throws `InvalidOperationException` for a memo without a kept hash); `public static Task<IReadOnlyList<LedgerTransaction>> LedgerRecords.ChunksAsync(ILedgerSource source, string writer, long fromLedger, CancellationToken cancellationToken)` (the first successful copy of each chunk, in index order).

**Which facts each strategy keeps (spec S23):** `TicketStrategy` and `SequenceStrategy` build with `keepChunkFacts: true` (every memo keeps its hash; tickets also its ledger) — bounded by the 64 MiB limit. `BatchStrategy` builds with `keepChunkFacts: false`: a chunk-marked memo keeps only its landed bit and its block's range; list and manifest memos keep hash and ledger.

- [ ] **Step 1: Write the failing tests for the compact types**

`tests/XrplFile.Core.Tests/SubmittedMemosTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class SubmittedMemosTests
{
    [Fact]
    public void A_landed_set_counts_once_and_finds_the_next_gap()
    {
        LandedSet set = new(130);

        Assert.True(set.Add(0));
        Assert.False(set.Add(0));
        for (int i = 1; i < 70; i++) set.Add(i);
        set.Add(129);

        Assert.Equal(71, set.Count);
        Assert.True(set.Contains(64));
        Assert.False(set.Contains(70));
        Assert.Equal(70, set.NextMissing(0));
        Assert.Equal(128, set.NextMissing(71 + 57));
        Assert.Equal(-1, set.NextMissing(129));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Contains(130));
    }

    [Fact]
    public void Batch_keeps_no_hash_and_no_ledger_for_a_chunk_but_folds_it_into_its_block()
    {
        SubmittedMemosBuilder builder = new(3, keepChunkFacts: false);
        builder.Record(0, MemoLayout.ChunkMarkerHex(1023), "H0", 50);
        builder.Record(1, MemoLayout.ChunkMarkerHex(1024), "H1", 52);
        builder.Record(2, MemoLayout.ListMarkerHex(0), "H2", 53);

        SubmittedMemos memos = builder.Build();

        Assert.Equal(3, memos.Count);
        Assert.Equal(3, memos.Landed.Count);
        Assert.False(memos.TryHashOf(0, out _));
        Assert.False(memos.TryLedgerOf(1, out _));
        Assert.True(memos.TryHashOf(2, out string? list));
        Assert.Equal("H2", list);
        Assert.True(memos.TryLedgerOf(2, out long ledger));
        Assert.Equal(53, ledger);
        Assert.Equal(new LedgerRange(50, 50), memos.BlockLedgers[0]);
        Assert.Equal(new LedgerRange(52, 52), memos.BlockLedgers[1]);
    }

    [Fact]
    public void Tickets_keep_every_hash_and_ledger_and_the_first_record_of_a_memo_wins()
    {
        SubmittedMemosBuilder builder = new(2, keepChunkFacts: true);
        builder.Record(0, MemoLayout.ChunkMarkerHex(0), "first", 10);
        builder.Record(0, MemoLayout.ChunkMarkerHex(0), "second", 11);
        builder.Record(1, MemoLayout.ChunkMarkerHex(1), "other", null);

        SubmittedMemos memos = builder.Build();

        Assert.True(memos.TryHashOf(0, out string? hash));
        Assert.Equal("first", hash);
        Assert.True(memos.TryLedgerOf(0, out long ledger));
        Assert.Equal(10, ledger);
        Assert.True(memos.TryHashOf(1, out _));
        Assert.False(memos.TryLedgerOf(1, out _));
        Assert.Equal(new LedgerRange(10, 10), memos.BlockLedgers[0]);
    }
}
```

`tests/XrplFile.Core.Tests/ChunkLandingTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class ChunkLandingTests
{
    [Fact]
    public void The_first_landing_of_a_chunk_counts_and_later_copies_change_nothing()
    {
        ChunkLanding landing = new(2_500);

        Assert.True(landing.Land(5, 100));
        Assert.False(landing.Land(5, 90));

        Assert.Equal(1, landing.Landed);
        Assert.True(landing.TryBlockLedgers(0, out LedgerRange range));
        Assert.Equal(new LedgerRange(100, 100), range);
        Assert.False(landing.TryBlockLedgers(1, out _));
    }

    [Fact]
    public void Blocks_keep_their_first_and_last_ledger_and_a_landed_count()
    {
        ChunkLanding landing = new(2_500);

        for (long chunk = 0; chunk < 2_500; chunk++) landing.Land(chunk, 200 - (chunk % 3) + (chunk / 1_024) * 10);

        Assert.True(landing.IsComplete);
        Assert.Equal(1_024, landing.LandedIn(0));
        Assert.Equal(452, landing.LandedIn(2));
        Assert.Equal([198L, 208L, 218L], landing.BlockFirstLedgers());
        Assert.Equal(new LedgerRange(198, 220), landing.Window());
    }

    [Fact]
    public void A_block_widened_from_a_strategy_keeps_the_widest_range()
    {
        ChunkLanding landing = new(1_500);
        for (long chunk = 0; chunk < 1_500; chunk++) landing.MarkLanded(chunk);

        landing.Widen(0, new LedgerRange(40, 44));
        landing.Widen(0, new LedgerRange(38, 41));
        landing.Widen(1, new LedgerRange(45, 45));

        Assert.Equal([38L, 45L], landing.BlockFirstLedgers());
        Assert.Equal(new LedgerRange(38, 45), landing.Window());
    }

    [Fact]
    public void A_block_without_a_ledger_has_no_first_ledger_to_give()
    {
        ChunkLanding landing = new(1_500);
        landing.Land(0, 40);

        Assert.Throws<InvalidOperationException>(() => landing.BlockFirstLedgers());
    }

    [Fact]
    public void Every_chunk_index_stays_an_int()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkLanding((long)int.MaxValue + 1));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.SubmittedMemosTests -class XrplFile.Tests.ChunkLandingTests`
Expected: build error, the types do not exist.

- [ ] **Step 3: Write the compact types**

`src/XrplFile.Core/LedgerRange.cs`:

```csharp
namespace XrplFile;

/// <summary>The first and last ledger something landed in, both inclusive.</summary>
public readonly record struct LedgerRange(long First, long Last)
{
    public static LedgerRange Of(long ledger) => new(ledger, ledger);

    public LedgerRange Including(long ledger) => new(Math.Min(First, ledger), Math.Max(Last, ledger));

    public LedgerRange Including(LedgerRange other) => new(Math.Min(First, other.First), Math.Max(Last, other.Last));
}
```

`src/XrplFile.Core/LandedSet.cs`:

```csharp
using System.Numerics;

namespace XrplFile;

/// <summary>
/// One bit per memo of a call (spec S19, S23): whether it landed. It has one owner, who adds to it
/// from one thread at a time; it is not safe for concurrent writers.
/// </summary>
public sealed class LandedSet
{
    private readonly ulong[] _words;
    private int _count;

    public LandedSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        Capacity = capacity;
        _words = new ulong[(capacity + 63) / 64];
    }

    /// <summary>How many memos the set is about.</summary>
    public int Capacity { get; }

    /// <summary>How many have landed.</summary>
    public int Count => Volatile.Read(ref _count);

    public bool Contains(int index)
    {
        Check(index);
        return (_words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    /// <summary>The first index at or after <paramref name="from"/> that has not landed, or −1.</summary>
    public int NextMissing(int from)
    {
        if (from < 0) from = 0;

        for (int word = from >> 6; word < _words.Length; word++)
        {
            ulong missing = ~_words[word];
            if (word == from >> 6) missing &= ulong.MaxValue << (from & 63);
            if (missing == 0) continue;

            int index = (word << 6) + BitOperations.TrailingZeroCount(missing);
            return index < Capacity ? index : -1;
        }

        return -1;
    }

    /// <summary>Marks a memo landed; false when it already was.</summary>
    internal bool Add(int index)
    {
        Check(index);

        ulong bit = 1UL << (index & 63);
        ref ulong word = ref _words[index >> 6];
        if ((word & bit) != 0) return false;

        word |= bit;
        Interlocked.Increment(ref _count);
        return true;
    }

    private void Check(int index)
    {
        if ((uint)index >= (uint)Capacity) throw new ArgumentOutOfRangeException(nameof(index), index, $"the set is about {Capacity} memos");
    }
}
```

`src/XrplFile.Core/ChunkLanding.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What has landed of a file's chunks, compactly (spec S19): a bit per chunk, and per block its
/// first ledger, its last ledger and a landed count — about 1 bit per chunk plus 20 bytes per
/// block. No transaction hash and no ledger is kept per chunk: the manifest needs only each block's
/// first ledger and the window's ends, and nothing else in the library reads a chunk's hash.
/// </summary>
public sealed class ChunkLanding
{
    private readonly ulong[] _landed;
    private readonly long[] _first;
    private readonly long[] _last;
    private readonly int[] _count;

    public ChunkLanding(long chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);

        // Every chunk index stays an int (spec S1): 16 GiB is about 17 million chunks.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkCount, (long)int.MaxValue);

        ChunkCount = chunkCount;
        BlockCount = FileTree.BlockCount(chunkCount);
        _landed = new ulong[(chunkCount + 63) / 64];
        _first = new long[BlockCount];
        _last = new long[BlockCount];
        _count = new int[BlockCount];
        Array.Fill(_first, long.MaxValue);
        Array.Fill(_last, long.MinValue);
    }

    public long ChunkCount { get; }

    public int BlockCount { get; }

    /// <summary>How many chunks have landed.</summary>
    public long Landed { get; private set; }

    public bool IsComplete => Landed == ChunkCount;

    public bool IsLanded(long chunk)
    {
        Check(chunk);
        return (_landed[chunk >> 6] & (1UL << (int)(chunk & 63))) != 0;
    }

    /// <summary>How many of the block's chunks have landed.</summary>
    public int LandedIn(int block) => _count[block];

    /// <summary>The block's first and last ledger, when any of its chunks' ledgers is known.</summary>
    public bool TryBlockLedgers(int block, out LedgerRange range)
    {
        range = new LedgerRange(_first[block], _last[block]);
        return _first[block] != long.MaxValue;
    }

    /// <summary>Each block's first ledger, in block order: what the block list names.</summary>
    /// <exception cref="InvalidOperationException">A block has no known ledger.</exception>
    public IReadOnlyList<long> BlockFirstLedgers()
    {
        long[] firsts = new long[BlockCount];

        for (int block = 0; block < BlockCount; block++)
        {
            if (!TryBlockLedgers(block, out LedgerRange range))
            {
                throw new InvalidOperationException($"no ledger is known for block {block}, so the block list cannot name where it starts");
            }

            firsts[block] = range.First;
        }

        return firsts;
    }

    /// <summary>The first ledger of any chunk and the last ledger of any chunk.</summary>
    public LedgerRange Window()
    {
        IReadOnlyList<long> firsts = BlockFirstLedgers();
        return new LedgerRange(firsts.Min(), _last.Max());
    }

    /// <summary>Records a chunk's first copy in canonical order; false, and nothing changed, when it had already landed.</summary>
    internal bool Land(long chunk, long ledgerIndex)
    {
        if (!MarkLanded(chunk)) return false;

        Widen(FileTree.BlockOf(chunk), LedgerRange.Of(ledgerIndex));
        return true;
    }

    /// <summary>Records that a chunk landed without its ledger; the strategy reports its block's range instead.</summary>
    internal bool MarkLanded(long chunk)
    {
        Check(chunk);

        ulong bit = 1UL << (int)(chunk & 63);
        ref ulong word = ref _landed[chunk >> 6];
        if ((word & bit) != 0) return false;

        word |= bit;
        _count[FileTree.BlockOf(chunk)]++;
        Landed++;
        return true;
    }

    internal void Widen(int block, LedgerRange range)
    {
        _first[block] = Math.Min(_first[block], range.First);
        _last[block] = Math.Max(_last[block], range.Last);
    }

    private void Check(long chunk)
    {
        if (chunk < 0 || chunk >= ChunkCount) throw new ArgumentOutOfRangeException(nameof(chunk), chunk, $"the file has {ChunkCount} chunks");
    }
}
```

`src/XrplFile.Core/SubmittedMemos.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace XrplFile;

/// <summary>
/// What a strategy hands back for one call (spec S23): which memos landed, and the facts it kept
/// about them. Each <c>Try</c> method is false when the strategy did not keep that fact. Hashes are
/// kept for every list and manifest memo, and for every memo on the ticket and sequence paths, all
/// of them bounded; a Batch chunk pass keeps none (spec S19) and reports each file block's ledger
/// range instead.
/// </summary>
public sealed class SubmittedMemos
{
    private readonly IReadOnlyDictionary<int, string> _hashes;
    private readonly IReadOnlyDictionary<int, long> _ledgers;

    internal SubmittedMemos(
        LandedSet landed, IReadOnlyDictionary<int, string> hashes, IReadOnlyDictionary<int, long> ledgers,
        IReadOnlyDictionary<int, LedgerRange> blockLedgers)
    {
        Landed = landed;
        _hashes = hashes;
        _ledgers = ledgers;
        BlockLedgers = blockLedgers;
    }

    /// <summary>How many memos the call carried.</summary>
    public int Count => Landed.Capacity;

    public LandedSet Landed { get; }

    /// <summary>Per file block, the ledgers of the chunk-marked memos of this call whose ledger the strategy knew.</summary>
    public IReadOnlyDictionary<int, LedgerRange> BlockLedgers { get; }

    public bool TryLedgerOf(int index, out long ledgerIndex) => _ledgers.TryGetValue(index, out ledgerIndex);

    public bool TryHashOf(int index, [NotNullWhen(true)] out string? hash) => _hashes.TryGetValue(index, out hash);
}

/// <summary>How a strategy assembles its <see cref="SubmittedMemos"/>: the first record it confirms for a memo wins.</summary>
internal sealed class SubmittedMemosBuilder(int count, bool keepChunkFacts)
{
    private readonly Dictionary<int, string> _hashes = [];
    private readonly Dictionary<int, long> _ledgers = [];
    private readonly Dictionary<int, LedgerRange> _blocks = [];

    public LandedSet Landed { get; } = new(count);

    public void Record(int index, string memoTypeHex, string hash, long? ledgerIndex)
    {
        if (!Landed.Add(index)) return;

        long? chunk = MemoLayout.ChunkIndexOf(memoTypeHex);

        if (chunk is { } c && ledgerIndex is { } ledger)
        {
            int block = FileTree.BlockOf(c);
            _blocks[block] = _blocks.TryGetValue(block, out LedgerRange range) ? range.Including(ledger) : LedgerRange.Of(ledger);
        }

        if (chunk is not null && !keepChunkFacts) return;

        _hashes[index] = hash;
        if (ledgerIndex is { } known) _ledgers[index] = known;
    }

    public SubmittedMemos Build() => new(Landed, _hashes, _ledgers, _blocks);
}
```

- [ ] **Step 4: Run the compact-type tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.SubmittedMemosTests -class XrplFile.Tests.ChunkLandingTests`
Expected: PASS.

- [ ] **Step 5: Change the interface and the three strategies**

`src/XrplFile.Core/ISubmissionStrategy.cs`: delete the `SubmittedMemo` record and change the method to

```csharp
    /// <summary>
    /// Puts every memo into a validated ledger. When it returns, every memo of the call has landed
    /// with tesSUCCESS; what it says about them is <see cref="SubmittedMemos"/>'s business (spec S23).
    /// </summary>
    Task<SubmittedMemos> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken);
```

`SequenceStrategy.SubmitAllAsync`: replace `List<SubmittedMemo> landed = new(memos.Count);` with `SubmittedMemosBuilder landed = new(memos.Count, keepChunkFacts: true);`, and the loop's first two lines with

```csharp
            // Read once: a chunk pass builds an element from the file each time it is read.
            MemoSubmission memo = memos[i];
            landed.Record(i, memo.MemoTypeHex, await SubmitAsync(memo, sequence++, cancellationToken).ConfigureAwait(false), ledgerIndex: null);
            submitted?.Report(landed.Landed.Count);
```

and `return landed.Build();` at the end.

`TicketStrategy.SubmitAllAsync` (lines 244-245): replace the final `return [.. landed.OrderBy(...)...]` with

```csharp
        SubmittedMemosBuilder result = new(memos.Count, keepChunkFacts: true);
        foreach ((int index, LedgerTransaction tx) in landed.OrderBy(pair => pair.Key)) result.Record(index, memos[index].MemoTypeHex, tx.Hash, tx.LedgerIndex);

        return result.Build();
```

`BatchStrategy.SubmitAllAsync` (lines 99-100): replace the final `return` with

```csharp
        SubmittedMemosBuilder result = new(memos.Count, keepChunkFacts: false);
        foreach ((int index, LandedInner inner) in landed.OrderBy(pair => pair.Key)) result.Record(index, memos[index].MemoTypeHex, inner.Hash, inner.LedgerIndex);

        return result.Build();
```

(Task 4 replaces Batch's pass-wide dictionary with the compact tracker; the shape of what it returns stays.)

- [ ] **Step 6: Change `PublishResult`, `LandedMemos`, `PublicationReport` and `PublicationState`**

In `src/XrplFile.Core/XrplFileWriter.cs`:

```csharp
/// <summary>
/// What the ledger already holds for one publication: the chunks compactly (spec S19), block lists
/// by list index with their bytes. The writer completes <see cref="Chunks"/> in place: it is the one
/// copy of the publication's chunk tracking.
/// </summary>
public sealed record LandedMemos(ChunkLanding Chunks, IReadOnlyDictionary<int, LandedMemo> Lists);

/// <summary>A finished publication (spec S8): counts and the manifest's hash, never a per-chunk list.</summary>
public sealed record PublishResult(XrplFileLink Link, Manifest Manifest, string Writer, long ChunkCount, int ListCount, string ManifestHash);
```

In `src/XrplFile.Core/PublicationReport.cs` replace the two parameters `IReadOnlyList<string> ChunkHashes, IReadOnlyList<string> ListHashes` with `long ChunkCount, int ListCount`, and their `<param>` docs with:

```csharp
/// <param name="ChunkCount">Publication scope: the file's chunks, whichever session put them there (spec S8).</param>
/// <param name="ListCount">Publication scope: the file's block-list memos; zero for a file of one block.</param>
```

In `src/XrplFile.Core/PublicationState.cs` delete the parameter `IReadOnlyDictionary<int, string> ChunkHashes` and its `<param>` doc, and add to the type's summary:

```csharp
/// <para>
/// Files written before streaming publication carry a <c>ChunkHashes</c> map; it is read and
/// ignored (spec S8). Nothing in the library or the application read it, and at "done" it held a
/// hash per chunk.
/// </para>
```

System.Text.Json skips a property no constructor parameter or member names, so no attribute is needed.

- [ ] **Step 7: The writer on `SubmittedMemos` and `ChunkLanding`**

Replace `PublishCoreAsync` in `src/XrplFile.Core/XrplFileWriter.cs` with:

```csharp
    private async Task<PublishResult> PublishCoreAsync(
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<byte[]> chunks = FileTree.Split(file, MemoLayout.ChunkBytes);
        ChunkLanding landing = alreadyLanded?.Chunks ?? new ChunkLanding(chunks.Count);

        if (landing.ChunkCount != chunks.Count)
        {
            throw new ArgumentException($"the landed chunks describe {landing.ChunkCount} chunks and the file has {chunks.Count}", nameof(alreadyLanded));
        }

        // Chunks. What a resumed run found in the ledger is not sent again: it would be paid for
        // twice and put a second copy in the account's history.
        List<int> missing = [.. Enumerable.Range(0, chunks.Count).Where(index => !landing.IsLanded(index))];

        if (missing.Count > 0)
        {
            long carried = landing.Landed;

            SubmittedMemos sent = await _strategy.SubmitAllAsync(
                [.. missing.Select(index => new MemoSubmission(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex(chunks[index]), null))],
                new InlineProgress(done => progress?.Report(new PublishProgress("chunks", carried + done, chunks.Count))),
                cancellationToken).ConfigureAwait(false);

            await FoldAsync(sent, call => missing[call], landing, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new PublishProgress("chunks", chunks.Count, chunks.Count));
        }

        // The window is the ledgers the chunks are actually in, not those of the first and last
        // index: a resumed run re-sends a low index late.
        LedgerRange window = landing.Window();
        long lastLedger = window.Last;
        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(FileTree.BlockHashes(chunks), landing.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);

        if (listMemos.Count > 0)
        {
            lastLedger = Math.Max(lastLedger, await PublishListsAsync(listMemos, alreadyLanded, progress, cancellationToken).ConfigureAwait(false));
        }

        Manifest manifest = ManifestPlan.Build(type, name, file.Length, entries, window.First, lastLedger, listMemos.Count);

        if (_options.ReadBack)
        {
            await ReadEverythingBackAsync(file, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
        }

        SubmittedMemos manifestLanded = await _strategy.SubmitAllAsync([ManifestPlan.Memo(manifest)], null, cancellationToken).ConfigureAwait(false);

        if (!manifestLanded.TryHashOf(0, out string? manifestHash))
        {
            throw new InvalidOperationException("the strategy kept no hash for the manifest, and the link is that hash");
        }

        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(new XrplFileLink(network, manifestHash), manifest, writer, chunks.Count, listMemos.Count, manifestHash);
    }

    /// <summary>
    /// Folds a chunk pass into the landing (spec S19, S23): a memo's ledger when the strategy kept it,
    /// else the ledger of its hash (the sequence path), else only its bit — and then every block range
    /// the strategy reported. The two agree where both exist, and ranges only ever widen.
    /// </summary>
    private async Task FoldAsync(SubmittedMemos sent, Func<int, long> chunkOf, ChunkLanding landing, CancellationToken cancellationToken)
    {
        if (sent.Landed.Count != sent.Count)
        {
            throw new InvalidOperationException($"the strategy returned with {sent.Count - sent.Landed.Count} of {sent.Count} memos not landed");
        }

        for (int call = 0; call < sent.Count; call++)
        {
            long chunk = chunkOf(call);

            if (sent.TryLedgerOf(call, out long ledger)) landing.Land(chunk, ledger);
            else if (sent.TryHashOf(call, out string? hash)) landing.Land(chunk, await LedgerOfAsync(hash, cancellationToken).ConfigureAwait(false));
            else landing.MarkLanded(chunk);
        }

        foreach ((int block, LedgerRange range) in sent.BlockLedgers) landing.Widen(block, range);
    }

    /// <summary>
    /// Sends the block-list memos a resume did not find byte for byte, and returns the last ledger
    /// any list memo is in: they are reachable through the window too.
    /// </summary>
    private async Task<long> PublishListsAsync(
        IReadOnlyList<MemoSubmission> listMemos, LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        List<int> missing = [];
        long last = long.MinValue;

        for (int j = 0; j < listMemos.Count; j++)
        {
            // A list payload carries per-block ledgers, so the same index from an interrupted run can
            // describe another window; only an identical payload is the one this run would send.
            if (alreadyLanded is not null
                && alreadyLanded.Lists.TryGetValue(j, out LandedMemo? landed)
                && string.Equals(landed.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                last = Math.Max(last, landed.LedgerIndex);
                continue;
            }

            missing.Add(j);
        }

        if (missing.Count == 0)
        {
            progress?.Report(new PublishProgress("lists", listMemos.Count, listMemos.Count));
            return last;
        }

        int carried = listMemos.Count - missing.Count;
        SubmittedMemos sent = await _strategy.SubmitAllAsync(
            [.. missing.Select(j => listMemos[j])],
            new InlineProgress(done => progress?.Report(new PublishProgress("lists", carried + done, listMemos.Count))),
            cancellationToken).ConfigureAwait(false);

        for (int call = 0; call < sent.Count; call++)
        {
            long ledger = sent.TryLedgerOf(call, out long known)
                ? known
                : sent.TryHashOf(call, out string? hash)
                    ? await LedgerOfAsync(hash, cancellationToken).ConfigureAwait(false)
                    : throw new InvalidOperationException("the strategy kept neither a ledger nor a hash for a block-list memo");

            last = Math.Max(last, ledger);
        }

        return last;
    }

    /// <summary>Reads everything back through the reader a stranger would use, before the link exists.</summary>
    private async Task ReadEverythingBackAsync(
        ReadOnlyMemory<byte> file, Manifest manifest, string writer, long lastLedger, IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
        ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
        IReadOnlyList<BlockEntry> published = await ReadBackAsync(
            () => reader.BlockListAsync(unpublished, cancellationToken), cancellationToken).ConfigureAwait(false);

        long at = 0;

        for (int block = 0; block < published.Count; block++)
        {
            BlockEntry entry = published[block];
            byte[] content = await ReadBackAsync(
                () => reader.BlockAsync(unpublished, entry, cancellationToken), cancellationToken).ConfigureAwait(false);

            long remaining = file.Length - at;
            if (content.Length > remaining)
            {
                throw new XrplFileException(XrplFileError.BlockMismatch,
                    $"block {block} read back {content.Length} bytes but only {remaining} remain in the file", block);
            }

            if (!content.AsSpan().SequenceEqual(file.Span.Slice((int)at, content.Length)))
            {
                throw new XrplFileException(XrplFileError.BlockMismatch, $"block {block} read back differs from the file", block);
            }

            at += content.Length;
            progress?.Report(new PublishProgress("read-back", block + 1, published.Count));
        }
    }
```

Keep `InlineProgress`, the generic `ReadBackAsync<T>` retry helper and `LedgerOfAsync` as they are. Remove the `using`-free leftovers of the old body (`chunkByIndex`, `chunkHashes`, `listHashes`). Delete `ManifestPlan.FirstLedgers` (Task 1's bridge).

- [ ] **Step 8: The session**

In `src/XrplFile.Core/PublicationSession.cs`:

1. `Reconcile(IReadOnlyList<byte[]> chunks, IReadOnlyList<LedgerTransaction> history)` builds a `ChunkLanding landing = new(chunks.Count);` and, where it did `landedChunks.TryAdd(...)`, calls `landing.Land(chunkIndex, tx.LedgerIndex);` (it returns false for a later copy, which keeps "first in canonical order"). It returns `new LandedMemos(landing, landedLists)`.
2. `FindManifest(IReadOnlyList<byte[]> chunks, LandedMemos landed, IReadOnlyList<LedgerTransaction> history)`:

```csharp
    private PublishResult? FindManifest(IReadOnlyList<byte[]> chunks, LandedMemos landed, IReadOnlyList<LedgerTransaction> history)
    {
        if (!landed.Chunks.IsComplete) return null;

        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(FileTree.BlockHashes(chunks), landed.Chunks.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);
        LedgerRange window = landed.Chunks.Window();
        long lastLedger = window.Last;

        for (int j = 0; j < listMemos.Count; j++)
        {
            if (!landed.Lists.TryGetValue(j, out LandedMemo? list)
                || !string.Equals(list.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            lastLedger = Math.Max(lastLedger, list.LedgerIndex);
        }

        PublicationState state = State;
        Manifest manifest = ManifestPlan.Build(state.Type, state.Name, state.Size, entries, window.First, lastLedger, listMemos.Count);
        string data = ManifestPlan.Memo(manifest).MemoDataHex;

        foreach (LedgerTransaction tx in history)
        {
            if (!tx.Validated || !tx.Succeeded || !string.Equals(tx.Account, state.Writer, StringComparison.Ordinal)) continue;
            if (!MemoLayout.IsManifest(tx.MemoTypeHex) || !string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;

            return new PublishResult(new XrplFileLink(state.Network, tx.Hash), manifest, state.Writer, chunks.Count, listMemos.Count, tx.Hash);
        }

        return null;
    }
```

3. Batch resume: `long missingChunks = chunks.Count - session._landed.Chunks.Landed;`
4. `Advance(string phase, string? root, string? manifestHash = null)` — drop the chunk-hash parameter and the `ChunkHashes = ...` line; its callers become `Advance("done", done.Manifest.Root, done.ManifestHash)`, `Advance("done", result.Manifest.Root, result.ManifestHash)` and `session.Advance(value.Stage, root: null)`.
5. In `RunAsync`, the report of a found manifest becomes `new PublicationReport(done.Link, done.Manifest, done.Writer, done.ChunkCount + done.ListCount + 1, 0, _startedAt, DateTimeOffset.UtcNow, done.ChunkCount, done.ListCount, done.ManifestHash)`, and the normal one counts `long transactions = result.ChunkCount + result.ListCount + 1 + mints;` and passes `result.ChunkCount, result.ListCount, result.ManifestHash`.
6. Both `new PublicationState(...)` calls drop `ChunkHashes: new Dictionary<int, string>(),`.

- [ ] **Step 9: Test helpers and the migration of existing tests**

`tests/XrplFile.TestSupport/SubmittedMemosTesting.cs`:

```csharp
namespace XrplFile.Tests;

/// <summary>A landed memo as the tests of the ticket and sequence paths read it.</summary>
public sealed record LandedMemoFacts(int Index, string TxHash, long? LedgerIndex);

public static class SubmittedMemosTesting
{
    /// <summary>Every memo with its kept hash and ledger; for the paths that keep a hash for every memo.</summary>
    public static IReadOnlyList<LandedMemoFacts> Each(this SubmittedMemos memos)
    {
        ArgumentNullException.ThrowIfNull(memos);

        List<LandedMemoFacts> each = new(memos.Count);

        for (int index = 0; index < memos.Count; index++)
        {
            if (!memos.TryHashOf(index, out string? hash)) throw new InvalidOperationException($"memo {index} kept no hash");
            each.Add(new LandedMemoFacts(index, hash, memos.TryLedgerOf(index, out long ledger) ? ledger : null));
        }

        return each;
    }
}
```

`tests/XrplFile.TestSupport/LedgerRecords.cs`:

```csharp
namespace XrplFile.Tests;

public static class LedgerRecords
{
    /// <summary>
    /// The first successful copy of every chunk the writer carries from <paramref name="fromLedger"/>,
    /// in chunk order — the copy a reader takes. What tests read now that reports carry no chunk hashes.
    /// </summary>
    public static async Task<IReadOnlyList<LedgerTransaction>> ChunksAsync(
        ILedgerSource source, string writer, long fromLedger, CancellationToken cancellationToken)
    {
        SortedDictionary<long, LedgerTransaction> first = [];
        long to = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, fromLedger, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            if (tx.Succeeded && tx.Validated && string.Equals(tx.Account, writer, StringComparison.Ordinal)
                && MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index)
            {
                first.TryAdd(index, tx);
            }
        }

        return [.. first.Values];
    }
}
```

Migrate the existing tests mechanically — the compiler lists every site:

| Old | New |
|---|---|
| `IReadOnlyList<SubmittedMemo> x = await strategy.SubmitAllAsync(...)` | `SubmittedMemos x = await strategy.SubmitAllAsync(...)` |
| ticket/sequence: `x[i].TxHash`, `x.Select(m => m.TxHash)`, `foreach (SubmittedMemo m in x)`, `Assert.Single(x).TxHash`, `m.LedgerIndex` | the same on `x.Each()` (`x.Each()[i].TxHash`, `foreach (LandedMemoFacts m in x.Each())`, …) |
| Batch: `landed.Select(m => m.Index)` | `Enumerable.Range(0, landed.Count).Where(landed.Landed.Contains)` |
| Batch: a chunk memo's `TxHash` / `LedgerIndex` | read the record from `node.LandedByMarkerAsync()` by marker; for "carries the ledger it landed in" (`BatchStrategyTests` ~line 399) assert `landed.BlockLedgers[0]` equals `new LedgerRange(min, max)` of those records' ledgers |
| `report.ChunkHashes.Count`, `result.ChunkHashes.Count` | `report.ChunkCount`, `result.ChunkCount` |
| `report.ListHashes.Count`, `Assert.Single(report.ListHashes)`, `Assert.Empty(report.ListHashes)` | `report.ListCount`, `Assert.Equal(1, report.ListCount)`, `Assert.Equal(0, report.ListCount)` |
| `report.ChunkHashes[k]`, `foreach (string hash in report.ChunkHashes)` | `(await LedgerRecords.ChunksAsync(ledger, writer, state.StartedInLedger, token))[k].Hash` (and the same list in the loop) |
| `WriterLedgerTests`: `source.LookedUp.Intersect(result.ChunkHashes, …)` | intersect with `(await LedgerRecords.ChunksAsync(...)).Select(tx => tx.Hash)` |
| `state with { …, ChunkHashes = new Dictionary<int, string>() }` | `state with { … }` without it |
| `new PublicationState(..., "chunks", new Dictionary<int, string>(), ...)` (App.Core tests) and `ChunkHashes: new Dictionary<int, string>()` | drop that argument |
| `Assert.Empty(onDisk.ChunkHashes)` (`PublicationSessionTests` ~514) | delete the line |
| the property-name list in `PublicationSessionTests` ~351 | remove `"ChunkHashes"` from it |
| test strategies (`ScriptedStrategy`, `ScriptedReleaseStrategy`, `LedgerClosingStrategy`, `ArmingStrategy`, `ProbingStrategy`) | return type `Task<SubmittedMemos>`; `ProbingStrategy` reports `landed.Landed.Count` |

The JSON literal with `"ChunkHashes": {}` in `PublicationSessionTests` (~856) stays: it now proves an old file still loads.

- [ ] **Step 10: Fixtures in the current format, and the test that they still resume**

These four files are written by hand in the exact format `PublicationState.Save` produces at `bc37d07` (two-space indent, declaration order, nulls omitted, a trailing newline); keep them byte for byte. `Fixtures_are_byte_for_byte_what_Save_writes` below guards that claim, so a drift between them and the format fails a test instead of passing unnoticed. Writer seed `sEdSKaCy2JT7JaM7v95H9SxkhP9wS2r` (address `rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD`), payer seed `sEdV19BLfeQeKdEXyYA4NhjPJe6XBfG` (address `r3ofWfcM2UoBw6fnP7BFbsY5XneivkGyrY`), file `VectorStream.Bytes("legacy-state", 2_500)` (root `6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8`). All values are throwaway test values.

`tests/XrplFile.Core.Tests/fixtures/state/v2-tickets.json`:

```json
{
  "Version": 2,
  "Network": "testnet",
  "Writer": "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
  "KeyReference": "key-legacy",
  "Type": "text/plain",
  "Name": "legacy.txt",
  "Size": 2500,
  "Chunk": 1000,
  "Root": "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8",
  "StartedInLedger": 40,
  "Phase": "chunks",
  "ChunkHashes": {},
  "Strategy": "tickets",
  "SourcePath": "C:\\Users\\someone\\legacy.txt"
}
```

`tests/XrplFile.Core.Tests/fixtures/state/v2-before-strategies.json`:

```json
{
  "Version": 2,
  "Network": "testnet",
  "Writer": "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
  "KeyReference": "key-legacy",
  "Type": "text/plain",
  "Name": "legacy.txt",
  "Size": 2500,
  "Chunk": 1000,
  "Root": "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8",
  "StartedInLedger": 40,
  "Phase": "chunks",
  "ChunkHashes": {}
}
```

`tests/XrplFile.Core.Tests/fixtures/state/v2-done.json`:

```json
{
  "Version": 2,
  "Network": "testnet",
  "Writer": "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
  "KeyReference": "key-legacy",
  "Type": "text/plain",
  "Name": "legacy.txt",
  "Size": 2500,
  "Chunk": 1000,
  "Root": "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8",
  "StartedInLedger": 40,
  "Phase": "done",
  "ChunkHashes": {
    "0": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    "1": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
    "2": "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"
  },
  "Strategy": "sequence",
  "ManifestHash": "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD"
}
```

`tests/XrplFile.Core.Tests/fixtures/state/v3-batch.json`:

```json
{
  "Version": 3,
  "Network": "testnet",
  "Writer": "rLUEXYuLiQptky37CqLcm9USQpPiz5rkpD",
  "KeyReference": "key-legacy",
  "Type": "text/plain",
  "Name": "legacy.txt",
  "Size": 2500,
  "Chunk": 1000,
  "Root": "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8",
  "StartedInLedger": 40,
  "Phase": "chunks",
  "ChunkHashes": {},
  "Strategy": "batch",
  "SourcePath": "C:\\Users\\someone\\legacy.txt",
  "Payer": "r3ofWfcM2UoBw6fnP7BFbsY5XneivkGyrY",
  "PayerKeyReference": "key-payer",
  "Fence": 0,
  "StartSequence": 100
}
```

Add to `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj`, in the `ItemGroup` with the vectors:

```xml
    <!-- State files written by the application before streaming publication (spec 4.5): they must keep resuming. -->
    <None Include="fixtures\state\*.json" CopyToOutputDirectory="PreserveNewest" />
```

`tests/XrplFile.Core.Tests/LegacyStateTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec 4.5: unfinished version-2 and version-3 runs written by the application before this change keep resuming.</summary>
public sealed class LegacyStateTests : IDisposable
{
    private static readonly XrplWallet WriterWallet = XrplWallet.FromSeed("sEdSKaCy2JT7JaM7v95H9SxkhP9wS2r");
    private static readonly XrplWallet PayerWallet = XrplWallet.FromSeed("sEdV19BLfeQeKdEXyYA4NhjPJe6XBfG");
    private static readonly byte[] LegacyFile = VectorStream.Bytes("legacy-state", 2_500);

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-legacy-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _directory, TicketsPerBatch = 100, FencePoll = TimeSpan.FromMilliseconds(1) };

    private string Fixture(string name)
    {
        string path = Path.Combine(_directory, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "state", name), path);
        return path;
    }

    private static FakeLedgerSource OneAccountNode()
    {
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = 4_999 };
        ledger.Balances[WriterWallet.ClassicAddress] = 500_000_000;
        return ledger;
    }

    /// <summary>
    /// The fixtures are hand-written; this pins them to the format: loaded and saved again, each is
    /// itself minus its <c>ChunkHashes</c>, byte for byte up to line endings.
    /// </summary>
    [Theory]
    [InlineData("v2-tickets.json")]
    [InlineData("v2-before-strategies.json")]
    [InlineData("v2-done.json")]
    [InlineData("v3-batch.json")]
    public void Fixtures_are_byte_for_byte_what_Save_writes(string name)
    {
        string path = Fixture(name);
        string fixture = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

        // ChunkHashes in the middle of the object, then as its last member (no comma after it).
        string expected = Regex.Replace(fixture, "\n  \"ChunkHashes\": \\{[^}]*\\},", string.Empty);
        expected = Regex.Replace(expected, ",\n  \"ChunkHashes\": \\{[^}]*\\}\n", "\n");

        PublicationState.Load(path).Save(path);

        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void A_finished_version_2_file_with_chunk_hashes_loads_and_saves_without_them()
    {
        string path = Fixture("v2-done.json");

        PublicationState state = PublicationState.Load(path);
        state.Save(path);

        Assert.Equal("done", state.Phase);
        Assert.Equal(new string('D', 64), state.ManifestHash);
        Assert.Equal(PublicationState.SequenceStrategyName, state.Strategy);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v2-tickets.json")]
    [InlineData("v2-before-strategies.json")]
    public async Task An_unfinished_version_2_file_resumes_and_finishes(string name)
    {
        string path = Fixture(name);
        FakeLedgerSource ledger = OneAccountNode();
        FakeLedgerSink sink = new(ledger);
        WalletSigner signer = new(WriterWallet);
        await using ISubmissionStrategy strategy = name == "v2-tickets.json"
            ? new TicketStrategy(sink, ledger, signer, new TicketStrategyOptions { FeeDrops = 10 })
            : new SequenceStrategy(sink, signer, new SequenceStrategyOptions { FeeDrops = 10 });

        PublicationSession session = await PublicationSession.ResumeAsync(strategy, ledger, LegacyFile, path, Options, Token);
        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(LegacyFile, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.Equal(3, report.ChunkCount);
        Assert.Equal("done", PublicationState.Load(path).Phase);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfinished_version_3_file_resumes_and_finishes_on_Batch()
    {
        string path = Fixture("v3-batch.json");
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = 40 };
        FakeBatchLedgerSink sink = new(ledger);
        WalletSigner writer = new(WriterWallet);
        WalletSigner payer = new(PayerWallet);
        sink.SetSequence(writer.Account, 100);
        sink.SetSequence(payer.Account, 500);
        ledger.Balances[writer.Account] = 100_000_000;
        ledger.Balances[payer.Account] = 100_000_000;

        PublicationSession session = await PublicationSession.ResumeAsync(
            (budget, fence) => new BatchStrategy(sink, ledger, writer, payer, budget, fence, BatchTestNode.FastOptions),
            ledger, LegacyFile, path, payer.Account, "key-payer", Options, null, Token);
        PublicationReport report = await session.RunAsync(null, Token);

        Assert.Equal(LegacyFile, (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.Equal(payer.Account, report.Payer);
        Assert.DoesNotContain("ChunkHashes", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
```

If `FakeLedgerSink`'s first ledger (5,000 by default) ever moves, keep `CurrentLedger` one below it so the resume's history read runs from ledger 40 forward.

- [ ] **Step 11: Build and run everything**

Run: `dotnet build XrplFile.Libraries.slnf` — expect no warnings (they are errors).
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS, including `LegacyStateTests`, `SubmittedMemosTests`, `ChunkLandingTests`.

- [ ] **Step 12: Commit**

```bash
git add src tests
git commit -m "feat(core)!: strategies return SubmittedMemos; results carry counts, not hashes

ISubmissionStrategy.SubmitAllAsync now returns SubmittedMemos: a landed bit
per memo, hashes and ledgers only where the strategy keeps them, and per-block
ledger ranges. Batch keeps no per-chunk hash. PublishResult and
PublicationReport carry ChunkCount and ListCount, and the state file no longer
writes ChunkHashes while still reading files that have them. Fixtures written
by the previous format prove that unfinished version-2 and version-3 runs
still resume."
```

---

### Task 3: The writer and the session on a source (S4, spec 4.3, S7)

**Files:**
- Create: `src/XrplFile.Core/IndexRanges.cs`, `src/XrplFile.Core/LazyMemoList.cs`
- Modify: `src/XrplFile.Core/ChunkLanding.cs` (add `Missing()`), `src/XrplFile.Core/ChunkReads.cs` (add `MatchesHex`), `src/XrplFile.Core/XrplFileWriter.cs` (public `PublishAsync` overloads, `PublishCoreAsync`, `ReadEverythingBackAsync`), `src/XrplFile.Core/PublicationSession.cs` (fields, constructor, source overloads of `StartAsync`/`ResumeAsync`, `RequireResumableAsync`, `ReconcileAsync`, `FindManifest`, `RunAsync`), `src/XrplFile.Core/XrplFileException.cs` (+ `SourceChanged`)
- Test: `tests/XrplFile.Core.Tests/LazyMemoListTests.cs`, `tests/XrplFile.Core.Tests/WriterSourceTests.cs`, `tests/XrplFile.Core.Tests/SessionSourceTests.cs`

**Interfaces:**
- Consumes: Task 1 (`IPublicationSource`, `MemoryPublicationSource`, `FileTree.BlockHashesAsync`, `RootHexOf`, `MaxPublicationBytes`), Task 2 (`ChunkLanding`, `SubmittedMemos`, `LandedMemos`, `PublishResult`).
- Produces:
  - `internal sealed class IndexRanges : IReadOnlyList<long>` — `static IndexRanges Of(IReadOnlyList<(long Start, int Length)> ranges)`, `long this[int index]`, `int IndexOf(long value)` (−1 when absent), `int Count`.
  - `ChunkLanding.Missing() : IndexRanges` (internal) — the chunks not landed, ascending.
  - `internal sealed class LazyMemoList(IPublicationSource source, IndexRanges chunks) : IReadOnlyList<MemoSubmission>` — `long ChunkAt(int index)`, `string MarkerHexAt(int index)`; element *i* is read and encoded when it is read and never cached.
  - `ChunkReads.MatchesHex(string? memoDataHex, ReadOnlySpan<byte> chunk) : bool` (internal).
  - `XrplFileWriter.PublishAsync(IPublicationSource file, string type, string? name, string writer, string network, LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)` (public); `internal Task<PublishResult> PublishAsync(IPublicationSource file, IReadOnlyList<byte[]>? blockHashes, ...)` (same tail); the `ReadOnlyMemory<byte>` overload stays and wraps.
  - `PublicationSession` source overloads, each with an `IProgress<PublishProgress>? preparation` parameter just before the token (stage `"hashing"`, Done/Total in chunks; Task 6 adds `"history"`):
    - `StartAsync(ISubmissionStrategy strategy, ILedgerSource source, IPublicationSource file, string type, string? name, string writer, string keyReference, string network, SessionOptions options, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)`
    - `ResumeAsync(ISubmissionStrategy strategy, ILedgerSource source, IPublicationSource file, string statePath, SessionOptions options, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)`
    - `StartAsync(BatchStrategyFactory strategyFactory, ILedgerSource source, IPublicationSource file, string type, string? name, string writer, string keyReference, string payer, string payerKeyReference, string network, SessionOptions options, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)`
    - `ResumeAsync(BatchStrategyFactory strategyFactory, ILedgerSource source, IPublicationSource file, string statePath, string payer, string payerKeyReference, SessionOptions options, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)` — the Batch resume's preparation progress also carries the `"fence"` reports it carried before.
  - The four `ReadOnlyMemory<byte>` overloads keep their exact signatures, wrap the bytes in a `MemoryPublicationSource` the session owns, and dispose it when `RunAsync` ends or when starting fails.
  - A source longer than `PublicationSession.MaxPublicationBytes` is refused with `XrplFileError.TooLarge` before anything is read or estimated.
  - `XrplFileException.SourceChanged : bool` (public, `init`) — true only on a resume's refusal because the file's size or root differs from the recorded one. Every other `ResumeMismatch` (a finished run, another chunk size, another writer) leaves it false, so the application blames the file only when the file is to blame (Task 12).

- [ ] **Step 1: Write the failing lazy-list tests**

`tests/XrplFile.Core.Tests/LazyMemoListTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class LazyMemoListTests
{
    [Fact]
    public void It_builds_nothing_until_an_element_is_read_and_never_caches_one()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("lazy", 5_000));
        LazyMemoList memos = new(source, IndexRanges.Of([(1, 2), (4, 1)]));

        Assert.Equal(3, memos.Count);
        Assert.Equal(0, source.Reads);

        MemoSubmission second = memos[1];
        Assert.Equal(1, source.Reads);
        _ = memos[1];
        Assert.Equal(2, source.Reads);

        Assert.Equal(MemoLayout.ChunkMarkerHex(2), second.MemoTypeHex);
        Assert.Equal(MemoLayout.Hex(VectorStream.Bytes("lazy", 5_000).AsSpan(2_000, 1_000)), second.MemoDataHex);
        Assert.Null(second.MemoFormatHex);
        Assert.Equal(MemoLayout.ChunkMarkerHex(4), memos.MarkerHexAt(2));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void The_last_element_of_a_file_one_byte_past_a_block_is_that_byte()
    {
        byte[] file = VectorStream.Bytes("lazy", 1_024_001);
        ChunkLanding landing = new(FileTree.ChunkCount(file.Length, MemoLayout.ChunkBytes));
        LazyMemoList memos = new(new MemoryPublicationSource(file), landing.Missing());

        Assert.Equal(1_025, memos.Count);
        Assert.Equal(1_024, memos.ChunkAt(1_024));
        Assert.Equal(MemoLayout.Hex(file.AsSpan(1_024_000)), memos[1_024].MemoDataHex);
    }

    [Fact]
    public void Enumerating_reads_in_order_and_one_at_a_time()
    {
        CountingPublicationSource source = new(new VectorPublicationSource("lazy", 3_000));
        LazyMemoList memos = new(source, IndexRanges.Of([(0, 3)]));
        int seen = 0;

        foreach (MemoSubmission memo in memos)
        {
            Assert.Equal(MemoLayout.ChunkMarkerHex(seen), memo.MemoTypeHex);
            Assert.Equal(++seen, source.Reads);
        }
    }

    [Fact]
    public void Ranges_map_positions_to_chunks_and_back()
    {
        IndexRanges ranges = IndexRanges.Of([(3, 2), (10, 3)]);

        Assert.Equal([3L, 4L, 10L, 11L, 12L], ranges.ToArray());
        Assert.Equal(3, ranges.IndexOf(11));
        Assert.Equal(-1, ranges.IndexOf(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ranges[5]);
    }

    [Fact]
    public void Missing_chunks_come_back_as_ranges()
    {
        ChunkLanding landing = new(200);
        for (long chunk = 0; chunk < 200; chunk++)
        {
            if (chunk is not (5 or 6 or 7 or 64 or 199)) landing.Land(chunk, 10);
        }

        IndexRanges missing = landing.Missing();

        Assert.Equal([5L, 6L, 7L, 64L, 199L], missing.ToArray());
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LazyMemoListTests`
Expected: build error.

- [ ] **Step 3: Write `IndexRanges`, `LazyMemoList`, `Missing` and `MatchesHex`**

`src/XrplFile.Core/IndexRanges.cs`:

```csharp
using System.Collections;

namespace XrplFile;

/// <summary>
/// Ascending, disjoint runs of chunk indices, addressed by position (spec S19: "missing indices are
/// an int[] or ranges"). A fresh run of 17 million chunks is one range, not 68 MB of indices.
/// </summary>
internal sealed class IndexRanges : IReadOnlyList<long>
{
    private readonly long[] _starts;
    private readonly int[] _before;

    private IndexRanges(long[] starts, int[] before, int count)
    {
        _starts = starts;
        _before = before;
        Count = count;
    }

    public int Count { get; }

    public static IndexRanges Of(IReadOnlyList<(long Start, int Length)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        long[] starts = new long[ranges.Count];
        int[] before = new int[ranges.Count];
        int count = 0;
        long end = long.MinValue;

        for (int r = 0; r < ranges.Count; r++)
        {
            (long start, int length) = ranges[r];

            if (length <= 0 || start < end)
            {
                throw new ArgumentException("ranges are ascending, disjoint and not empty", nameof(ranges));
            }

            starts[r] = start;
            before[r] = count;
            count = checked(count + length);
            end = start + length;
        }

        return new IndexRanges(starts, before, count);
    }

    public long this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index), index, $"there are {Count} indices");

            int range = Array.BinarySearch(_before, index);
            if (range < 0) range = ~range - 1;

            return _starts[range] + (index - _before[range]);
        }
    }

    /// <summary>The position of <paramref name="value"/>, or −1 when it is not in any range.</summary>
    public int IndexOf(long value)
    {
        int range = Array.BinarySearch(_starts, value);
        if (range < 0) range = ~range - 1;
        if (range < 0) return -1;

        int length = (range + 1 < _before.Length ? _before[range + 1] : Count) - _before[range];
        long offset = value - _starts[range];

        return offset < length ? _before[range] + (int)offset : -1;
    }

    public IEnumerator<long> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
```

`src/XrplFile.Core/LazyMemoList.cs`:

```csharp
using System.Buffers;
using System.Collections;

namespace XrplFile;

/// <summary>
/// A chunk pass's memos, built from the source when one is read and never cached (spec S4). A
/// chunk read from a local disk costs microseconds against a submit round trip of hundreds of
/// milliseconds, so nothing is gained by holding the pass's memos, which at 4 KB of UTF-16 hex per
/// chunk were 7 GB for a 1.7 GB file. Consumers read it by index or in round-sized slices; no code
/// materializes it.
/// </summary>
internal sealed class LazyMemoList(IPublicationSource source, IndexRanges chunks) : IReadOnlyList<MemoSubmission>
{
    public int Count => chunks.Count;

    public MemoSubmission this[int index]
    {
        get
        {
            long chunk = chunks[index];
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MemoLayout.ChunkBytes);

            try
            {
                int length = source.ReadChunk(chunk, buffer);
                return new MemoSubmission(MemoLayout.ChunkMarkerHex(chunk), MemoLayout.Hex(buffer.AsSpan(0, length)), null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>The file chunk the memo at <paramref name="index"/> carries.</summary>
    public long ChunkAt(int index) => chunks[index];

    /// <summary>The memo's marker, without reading the file.</summary>
    public string MarkerHexAt(int index) => MemoLayout.ChunkMarkerHex(chunks[index]);

    public IEnumerator<MemoSubmission> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
```

In `ChunkLanding`, add:

```csharp
    /// <summary>The chunks that have not landed, ascending, as ranges.</summary>
    internal IndexRanges Missing()
    {
        List<(long Start, int Length)> ranges = [];
        long start = -1;

        for (long chunk = 0; chunk < ChunkCount; chunk++)
        {
            bool landed = (_landed[chunk >> 6] & (1UL << (int)(chunk & 63))) != 0;

            if (!landed && start < 0) start = chunk;

            if (landed && start >= 0)
            {
                ranges.Add((start, (int)(chunk - start)));
                start = -1;
            }
        }

        if (start >= 0) ranges.Add((start, (int)(ChunkCount - start)));

        return IndexRanges.Of(ranges);
    }
```

In `ChunkReads`, add:

```csharp
    /// <summary>Whether a record's MemoData is exactly these bytes, compared without building a string per record.</summary>
    public static bool MatchesHex(string? memoDataHex, ReadOnlySpan<byte> chunk)
    {
        if (memoDataHex is null || memoDataHex.Length != chunk.Length * 2) return false;

        Span<byte> decoded = stackalloc byte[MemoLayout.ChunkBytes];
        System.Buffers.OperationStatus status = Convert.FromHexString(memoDataHex, decoded, out _, out int written);

        return status == System.Buffers.OperationStatus.Done && decoded[..written].SequenceEqual(chunk);
    }
```

- [ ] **Step 4: Run the lazy-list tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.LazyMemoListTests`
Expected: PASS.

- [ ] **Step 5: Write the failing writer and session tests**

`tests/XrplFile.Core.Tests/WriterSourceTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class WriterSourceTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_source_publishes_what_the_same_bytes_would_and_reads_back()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        CountingPublicationSource file = new(new VectorPublicationSource("writer-source", 2_500_500));

        PublishResult result = await new XrplFileWriter(strategy, ledger).PublishAsync(
            file, "application/octet-stream", "w.bin", Wallet.ClassicAddress, "testnet", null, null, Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("writer-source", 2_500_500)), result.Manifest.Root);
        Assert.Equal(VectorStream.Bytes("writer-source", 2_500_500), (await new XrplFileReader(ledger, new ReaderOptions { MaxBytes = long.MaxValue }).ReadAsync(result.Link, Token)).Bytes);

        // Hashing, sending and the full read-back each read every chunk once; nothing more.
        Assert.InRange(file.Reads, 3 * 2_501, 3 * 2_501);
    }

    [Fact]
    public async Task Block_hashes_handed_in_are_not_computed_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        byte[] bytes = VectorStream.Bytes("writer-source", 3_000);
        CountingPublicationSource file = new(new MemoryPublicationSource(bytes));

        await new XrplFileWriter(strategy, ledger, new WriterOptions { ReadBack = false }).PublishAsync(
            file, FileTree.BlockHashes(FileTree.Split(bytes, MemoLayout.ChunkBytes)), "text/plain", null, Wallet.ClassicAddress, "testnet", null, null, Token);

        Assert.Equal(3, file.Reads);
    }
}
```

The sequence strategy reads through a lazy list: `SequenceStrategy.SubmitAllAsync` indexes `memos[i]` once per memo, which is one read each.

`tests/XrplFile.Core.Tests/SessionSourceTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class SessionSourceTests : IDisposable
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-session-source-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _directory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        ledger.Balances[Wallet.ClassicAddress] = 500_000_000;
        return ledger;
    }

    [Fact]
    public async Task Starting_from_a_source_reports_hashing_in_chunks_and_records_the_streamed_root()
    {
        FakeLedgerSource ledger = Node();
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource file = new("session-source", 2_500_500);
        List<PublishProgress> preparation = [];

        PublicationSession session = await PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options,
            new SynchronousProgress<PublishProgress>(preparation.Add), Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("session-source", 2_500_500)), session.State.Root);
        Assert.Equal(
            [new PublishProgress("hashing", 1_024, 2_501), new PublishProgress("hashing", 2_048, 2_501), new PublishProgress("hashing", 2_501, 2_501)],
            preparation);
    }

    [Fact]
    public async Task A_resume_from_another_file_is_refused_before_the_ledger_is_read()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource started = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, started, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await using VectorPublicationSource other = new("another", 3_000);
        int reads = ledger.AccountTransactionsCalls;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, other, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.True(error.SourceChanged);
        Assert.Equal(reads, ledger.AccountTransactionsCalls);
    }

    [Fact]
    public async Task A_resume_refused_for_its_chunk_size_does_not_blame_the_file()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using VectorPublicationSource file = new("session-source", 3_000);
        PublicationSession first = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        (PublicationState.Load(first.StatePath) with { Chunk = 999 }).Save(first.StatePath);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            strategy, ledger, file, first.StatePath, Options, null, Token));

        Assert.Equal(XrplFileError.ResumeMismatch, error.Error);
        Assert.False(error.SourceChanged);
    }

    [Fact]
    public async Task A_source_above_the_publication_limit_is_refused_before_anything_is_read()
    {
        FakeLedgerSource ledger = Node();
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10 });
        await using UnreadableSource file = new(PublicationSession.MaxPublicationBytes + 1);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token));

        Assert.Equal(XrplFileError.TooLarge, error.Error);
    }

    [Fact]
    public async Task Reconciliation_compares_landed_chunks_with_the_source()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, new WalletSigner(Wallet), new SequenceStrategyOptions { FeeDrops = 10, ValidationPoll = TimeSpan.FromMilliseconds(1) });
        await using VectorPublicationSource file = new("session-source", 2_500);
        ScriptedStrategy dies = new(strategy) { FailAtCall = 2 };
        PublicationSession first = await PublicationSession.StartAsync(
            dies, ledger, file, "text/plain", null, Wallet.ClassicAddress, "key", "testnet", Options, null, Token);
        await Assert.ThrowsAsync<XrplFileException>(() => first.RunAsync(null, Token));
        int submitted = sink.Submitted.Count;

        PublicationSession resumed = await PublicationSession.ResumeAsync(strategy, ledger, file, first.StatePath, Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        // The three chunks of the first session were adopted: only the manifest was sent (no list for one block).
        Assert.Equal(submitted + 1, sink.Submitted.Count);
        Assert.Equal(VectorStream.Bytes("session-source", 2_500), (await new XrplFileReader(ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    /// <summary>A file too large to publish, which must never be read.</summary>
    private sealed class UnreadableSource(long length) : IPublicationSource
    {
        public long Length => length;

        public int ReadChunk(long index, Span<byte> destination) => throw new InvalidOperationException("read");

        public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("read");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

`sink.Submitted` is `FakeLedgerSink`'s list of submissions (already used by `PublicationSessionTests`).

- [ ] **Step 6: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.WriterSourceTests -class XrplFile.Tests.SessionSourceTests`
Expected: build error (no source overloads).

- [ ] **Step 7: The writer on a source**

In `src/XrplFile.Core/XrplFileWriter.cs`, make the three entry points:

```csharp
    /// <summary>The bytes in memory, through a <see cref="MemoryPublicationSource"/>.</summary>
    public Task<PublishResult> PublishAsync(
        ReadOnlyMemory<byte> file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken) =>
        PublishAsync(new MemoryPublicationSource(file), blockHashes: null, type, name, writer, network, alreadyLanded, progress, cancellationToken);

    /// <param name="file">Read a chunk at a time, never held whole (spec 4.3); the caller keeps owning it.</param>
    public Task<PublishResult> PublishAsync(
        IPublicationSource file, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken) =>
        PublishAsync(file, blockHashes: null, type, name, writer, network, alreadyLanded, progress, cancellationToken);

    /// <param name="blockHashes">The file's block hashes when the caller already streamed them (the session does, for the root), or null.</param>
    internal async Task<PublishResult> PublishAsync(
        IPublicationSource file, IReadOnlyList<byte[]>? blockHashes, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(file));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("a manifest needs a MIME type", nameof(type));

        string canonical = XrplFileLink.RequireNetwork(network);

        try
        {
            return await PublishCoreAsync(file, blockHashes, type, name, writer, canonical, alreadyLanded, progress, cancellationToken).ConfigureAwait(false);
        }
        // ... the four catch clauses of today's PublishAsync, unchanged and with their comments ...
    }
```

Move the existing comment about the network check and the four `catch` clauses (with their comments) from today's `PublishAsync` into this internal method verbatim.

Replace `PublishCoreAsync` and `ReadEverythingBackAsync` with:

```csharp
    private async Task<PublishResult> PublishCoreAsync(
        IPublicationSource file, IReadOnlyList<byte[]>? blockHashes, string type, string? name, string writer, string network,
        LandedMemos? alreadyLanded, IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(file.Length);
        ChunkLanding landing = alreadyLanded?.Chunks ?? new ChunkLanding(chunkCount);

        if (landing.ChunkCount != chunkCount)
        {
            throw new ArgumentException($"the landed chunks describe {landing.ChunkCount} chunks and the file has {chunkCount}", nameof(alreadyLanded));
        }

        IReadOnlyList<byte[]> blocks = blockHashes ?? await FileTree.BlockHashesAsync(file, null, cancellationToken).ConfigureAwait(false);

        if (blocks.Count != landing.BlockCount)
        {
            throw new ArgumentException($"{blocks.Count} block hashes for a file of {landing.BlockCount} blocks", nameof(blockHashes));
        }

        // Chunks. What a resumed run found in the ledger is not sent again: it would be paid for
        // twice and put a second copy in the account's history.
        IndexRanges missing = landing.Missing();

        if (missing.Count > 0)
        {
            long carried = landing.Landed;

            SubmittedMemos sent = await _strategy.SubmitAllAsync(
                new LazyMemoList(file, missing),
                new InlineProgress(done => progress?.Report(new PublishProgress("chunks", carried + done, chunkCount))),
                cancellationToken).ConfigureAwait(false);

            await FoldAsync(sent, call => missing[call], landing, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new PublishProgress("chunks", chunkCount, chunkCount));
        }

        LedgerRange window = landing.Window();
        long lastLedger = window.Last;
        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(blocks, landing.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);

        if (listMemos.Count > 0)
        {
            lastLedger = Math.Max(lastLedger, await PublishListsAsync(listMemos, alreadyLanded, progress, cancellationToken).ConfigureAwait(false));
        }

        Manifest manifest = ManifestPlan.Build(type, name, file.Length, entries, window.First, lastLedger, listMemos.Count);

        if (_options.ReadBack)
        {
            await ReadEverythingBackAsync(file, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
        }

        SubmittedMemos manifestLanded = await _strategy.SubmitAllAsync([ManifestPlan.Memo(manifest)], null, cancellationToken).ConfigureAwait(false);

        if (!manifestLanded.TryHashOf(0, out string? manifestHash))
        {
            throw new InvalidOperationException("the strategy kept no hash for the manifest, and the link is that hash");
        }

        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(new XrplFileLink(network, manifestHash), manifest, writer, chunkCount, listMemos.Count, manifestHash);
    }

    /// <summary>
    /// Reads every block back through the reader a stranger would use and compares it with the
    /// source, chunk by chunk, one block in memory at a time. The ticket and sequence paths keep this
    /// full read-back (spec S9); Task 7 gives the Batch path its sampled one.
    /// </summary>
    private async Task ReadEverythingBackAsync(
        IPublicationSource file, Manifest manifest, string writer, long lastLedger, IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
        ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
        IReadOnlyList<BlockEntry> published = await ReadBackAsync(
            () => reader.BlockListAsync(unpublished, cancellationToken), cancellationToken).ConfigureAwait(false);

        for (int block = 0; block < published.Count; block++)
        {
            BlockEntry entry = published[block];
            byte[] content = await ReadBackAsync(
                () => reader.BlockAsync(unpublished, entry, cancellationToken), cancellationToken).ConfigureAwait(false);

            await RequireSameAsSourceAsync(file, entry.Index, content, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PublishProgress("read-back", block + 1, published.Count));
        }
    }

    /// <summary>A block read back is exactly the source's chunks of that block, no more and no fewer bytes.</summary>
    private static async Task RequireSameAsSourceAsync(IPublicationSource file, int block, byte[] content, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(file.Length);
        long first = (long)block * FileTree.BlockChunks;
        long end = Math.Min(first + FileTree.BlockChunks, chunkCount);
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        int at = 0;

        for (long chunk = first; chunk < end; chunk++)
        {
            int length = await file.ReadChunkAsync(chunk, buffer, cancellationToken).ConfigureAwait(false);

            if (at + length > content.Length || !content.AsSpan(at, length).SequenceEqual(buffer.AsSpan(0, length)))
            {
                throw new XrplFileException(XrplFileError.BlockMismatch, $"block {block} read back differs from the file", block);
            }

            at += length;
        }

        if (at != content.Length)
        {
            throw new XrplFileException(XrplFileError.BlockMismatch,
                $"block {block} read back {content.Length} bytes and the file has {at} in it", block);
        }
    }
```

- [ ] **Step 8: The session holds a borrowed source**

In `src/XrplFile.Core/PublicationSession.cs`:

1. Replace the field `private readonly ReadOnlyMemory<byte> _file;` with

```csharp
    private readonly IPublicationSource _file;

    /// <summary>The file's block hashes, streamed once at start or resume (spec S3, S7) and handed to the writer.</summary>
    private readonly IReadOnlyList<byte[]> _blockHashes;

    /// <summary>Set when a <see cref="ReadOnlyMemory{T}"/> overload wrapped the bytes: the session then disposes the source (spec 4.1).</summary>
    private bool _ownsFile;
```

and the private constructor's `ReadOnlyMemory<byte> file` parameter with `IPublicationSource file, IReadOnlyList<byte[]> blockHashes`, assigning both.

2. Add the size guard and the streamed hashing, used by every overload:

```csharp
    /// <summary>An empty file has no root, and a file above <see cref="MaxPublicationBytes"/> is refused before anything is read (spec 4.1).</summary>
    private static void RequirePublishable(IPublicationSource file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length <= 0) throw new ArgumentException("an empty file has no chunks and no root", nameof(file));

        if (file.Length > MaxPublicationBytes)
        {
            throw new XrplFileException(XrplFileError.TooLarge,
                $"the file is {file.Length} bytes and a publication takes at most {MaxPublicationBytes}");
        }
    }

    /// <summary>The file's block hashes in one streaming pass, reported as the "hashing" stage in chunks (spec 5.3).</summary>
    private static Task<IReadOnlyList<byte[]>> HashAsync(IPublicationSource file, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        long chunks = ChunkReads.ChunkCount(file.Length);
        IProgress<long>? hashed = preparation is null ? null : new Hashed(done => preparation.Report(new PublishProgress("hashing", done, chunks)));

        return FileTree.BlockHashesAsync(file, hashed, cancellationToken);
    }

    /// <summary>Reports on the hashing thread, like the writer's own adapter, rather than posting through <see cref="Progress{T}"/>.</summary>
    private sealed class Hashed(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>Wraps bytes for the <see cref="ReadOnlyMemory{T}"/> overloads; the session that results owns it.</summary>
    private static async Task<PublicationSession> OwningAsync(ReadOnlyMemory<byte> bytes, Func<IPublicationSource, Task<PublicationSession>> start)
    {
        MemoryPublicationSource owned = new(bytes);

        try
        {
            PublicationSession session = await start(owned).ConfigureAwait(false);
            session._ownsFile = true;
            return session;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
```

3. Each existing `ReadOnlyMemory<byte>` overload becomes a one-line delegation, keeping its XML docs and signature, e.g.

```csharp
    public static Task<PublicationSession> StartAsync(
        ISubmissionStrategy strategy, ILedgerSource source, ReadOnlyMemory<byte> file, string type, string? name,
        string writer, string keyReference, string network, SessionOptions options, CancellationToken cancellationToken) =>
        OwningAsync(file, owned => StartAsync(strategy, source, owned, type, name, writer, keyReference, network, options, null, cancellationToken));
```

and likewise for the one-account `ResumeAsync(strategy, source, file, statePath, options, ct)` → `ResumeAsync(..., owned, statePath, options, null, ct)`, the Batch `StartAsync(...)` → `(..., owned, ..., options, null, ct)` and the Batch `ResumeAsync(..., fenceProgress, ct)` → `(..., owned, ..., options, fenceProgress, ct)`.

4. The bodies move into the new source overloads, with these changes from today's code:
   - first line: `RequirePublishable(file);`
   - every `file.Length` stays (it is the source's length);
   - in the starts, replace `IReadOnlyList<byte[]> chunks = FileTree.Split(...)` and `RootOf(chunks)` with `IReadOnlyList<byte[]> blocks = await HashAsync(file, preparation, cancellationToken).ConfigureAwait(false);` and `FileTree.RootHexOf(blocks)`, placed after the estimate's refusals so a refused run reads nothing;
   - in the resumes, replace `RequireResumable(state, statePath, file)` with `IReadOnlyList<byte[]> blocks = await RequireResumableAsync(state, statePath, file, preparation, cancellationToken).ConfigureAwait(false);`;
   - the Batch resume passes `preparation` where it passed `fenceProgress` to `WaitForFenceAsync`;
   - every `new PublicationSession(..., file, ...)` passes `file, blocks`;
   - `session._landed = session.Reconcile(chunks, history)` becomes `session._landed = await session.ReconcileAsync(history, cancellationToken).ConfigureAwait(false);`, and `FindManifest(chunks, session._landed, history)` becomes `FindManifest(session._landed, history)`;
   - the Batch resume's `int blocks = FileTree.BlockCount(chunks.Count);` becomes `int blockCount = blocks.Count;`, `long missingChunks = ChunkReads.ChunkCount(file.Length) - session._landed.Chunks.Landed;`.

5. `RequireResumable` becomes:

```csharp
    /// <summary>The checks every resume makes before it reads the ledger; returns the file's block hashes (spec S7).</summary>
    private static async Task<IReadOnlyList<byte[]>> RequireResumableAsync(
        PublicationState state, string statePath, IPublicationSource file, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        // ... today's checks of the phase, the size and the chunk size, and "records no root", verbatim,
        // except that the size check's throw gains the initializer { SourceChanged = true } ...

        IReadOnlyList<byte[]> blocks = await HashAsync(file, preparation, cancellationToken).ConfigureAwait(false);
        string root = FileTree.RootHexOf(blocks);

        if (!string.Equals(recorded, root, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.ResumeMismatch,
                $"{statePath} was started for a file rooted at {recorded} and this one roots at {root}")
            {
                SourceChanged = true,
            };
        }

        return blocks;
    }
```

   The "records no root" check moves above the hashing so a file with no root is refused without a read.

   In `XrplFileException`, add:

```csharp
    /// <summary>
    /// True when a resume was refused because the file differs from the one the run recorded (its
    /// size or its root), and only then. A caller may then ask for the file again; for any other
    /// <see cref="XrplFileError.ResumeMismatch"/> the file is not what is wrong.
    /// </summary>
    public bool SourceChanged { get; init; }
```

6. `Reconcile` becomes asynchronous and reads the source instead of a split copy:

```csharp
    private async Task<LandedMemos> ReconcileAsync(IReadOnlyList<LedgerTransaction> history, CancellationToken cancellationToken)
    {
        long chunkCount = ChunkReads.ChunkCount(_file.Length);
        ChunkLanding landing = new(chunkCount);
        Dictionary<int, LandedMemo> landedLists = [];
        byte[] buffer = new byte[MemoLayout.ChunkBytes];

        foreach (LedgerTransaction tx in history)
        {
            if (!tx.Succeeded || !tx.Validated) continue;
            if (tx.MemoDataHex is not { } data) continue;

            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } chunkIndex)
            {
                if (chunkIndex >= chunkCount || landing.IsLanded(chunkIndex)) continue;

                int length = await _file.ReadChunkAsync(chunkIndex, buffer, cancellationToken).ConfigureAwait(false);

                // First in canonical order, exactly as a reader will resolve it; a copy with other
                // bytes is another file's straggler and is not adopted (spec I12).
                if (ChunkReads.MatchesHex(data, buffer.AsSpan(0, length))) landing.Land(chunkIndex, tx.LedgerIndex);
                continue;
            }

            if (MemoLayout.ListIndexOf(tx.MemoTypeHex) is { } listIndex)
            {
                if (listIndex > int.MaxValue) continue;

                // Last in canonical order wins for a list, as the reader resolves one.
                landedLists[(int)listIndex] = new LandedMemo(tx.Hash, tx.LedgerIndex, data);
            }
        }

        return new LandedMemos(landing, landedLists);
    }
```

7. `FindManifest(LandedMemos landed, IReadOnlyList<LedgerTransaction> history)` builds entries from `_blockHashes` (`ManifestPlan.Entries(_blockHashes, landed.Chunks.BlockFirstLedgers())`) and counts `landed.Chunks.ChunkCount`; the rest of Task 2's body stays.

8. In `RunAsync`, call the writer's internal overload `writer.PublishAsync(_file, _blockHashes, started.Type, started.Name, started.Writer, started.Network, _landed, new PhaseRecordingProgress(this, progress), cancellationToken)`, and add to the existing `finally`:

```csharp
            if (_ownsFile) await _file.DisposeAsync().ConfigureAwait(false);
```

- [ ] **Step 9: Run the new tests and the suites**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.WriterSourceTests -class XrplFile.Tests.SessionSourceTests -class XrplFile.Tests.LazyMemoListTests`
Expected: PASS.
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"` and `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS. The tests that collected `"fence"` reports from the Batch resume (`PublicationSessionBatchResumeTests`, ~line 79: `Assert.All(waited, progress => Assert.Equal("fence", progress.Stage))`) now also hear `"hashing"`: filter them with `waited.Where(progress => progress.Stage == "fence")` before asserting, and keep `Assert.NotEmpty` on the filtered list. `A_run_that_never_transmitted_has_no_fence_to_wait_for` asserts no `"fence"` report the same way.

- [ ] **Step 10: Commit**

```bash
git add src tests
git commit -m "feat(core): the writer and the session read the file from a source

The writer's chunk pass hands the strategy a lazy memo list over the missing
chunks, built from the source when read and never cached; the full read-back
compares one block at a time with the source. The session holds a borrowed
IPublicationSource, streams the root at start and on resume with a hashing
progress stage, reconciles against the source's chunks, and refuses a file
above 16 GiB. The ReadOnlyMemory overloads wrap their bytes and own them."
```

---

### Task 4: Bounded Batch rounds, compact sweep records and compact tracking (S5, S6, S19)

**Files:**
- Create: `src/XrplFile.Core/SweepRecord.cs`, `src/XrplFile.Core/RoundMarkers.cs`
- Modify: `src/XrplFile.Core/LedgerTransaction.cs` (+ `TransactionIndex`), `src/XrplFile.Core/XrplClientLedgerSource.cs:405-423` (parse it), `src/XrplFile.Core/BatchStrategyOptions.cs` (+ `MaxRoundMemos`), `src/XrplFile.Core/BatchStrategy.cs` (`SubmitAllAsync`, `RequireAnotherCandidate`, `RequireDistinctMarkers` removed, test seam), `src/XrplFile.Core/BatchRound.cs` (constructor, registry, markers, history), `src/XrplFile.Core/BatchRegistry.cs` (byte counts), `src/XrplFile.Core/BatchProgress.cs` (bit set), `src/XrplFile.Core/BatchSweep.cs` (compact records)
- Test: `tests/XrplFile.Core.Tests/BatchRoundsTests.cs`, `tests/XrplFile.Core.Tests/SweepRecordTests.cs`, `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs` (one test added)
- Modify (tests): `tests/XrplFile.Core.Tests/BatchRegistryTests.cs` (candidate counts as `byte[]`), `tests/XrplFile.Core.Tests/BatchIdentityTests.cs` (the coverage and payer-fee tests move onto `SweepRecord`)

**Interfaces:**
- Consumes: Task 2 (`LandedSet`, `SubmittedMemosBuilder`, `SubmittedMemos`), Task 3 (`LazyMemoList.MarkerHexAt`).
- Produces:
  - `LedgerTransaction.TransactionIndex : int?` (init; `meta.TransactionIndex`, null when the record does not say).
  - `BatchStrategyOptions.DefaultMaxRoundMemos = 16_384` (public const) and `int MaxRoundMemos { get; init; } = DefaultMaxRoundMemos`.
  - `internal enum ResultClass : byte { Success, Claimed, Other }`, `internal enum MarkerKind : byte { None, Chunk, List, Manifest, Other }`, `internal enum DataVerdict : byte { NotChecked, Matches, Conflicts }`.
  - `internal sealed record SweepRecord(string Hash, string Account, long LedgerIndex, int? TransactionIndex, bool Validated, ResultClass Result, string? TransactionType, uint Sequence, uint? TicketSequence, uint? TicketCount, long? Fee, string? ParentBatchID, MarkerKind Marker, long MarkerIndex, int CallIndex, DataVerdict Verdict)` with `bool Succeeded`, `static SweepRecord From(LedgerTransaction tx, RoundMarkers? markers)`, `static (MarkerKind Kind, long Index) Classify(string? memoTypeHex)`.
  - `internal sealed class RoundMarkers(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending)` — `bool TryIndexOf(string? memoTypeHex, out int callIndex)`, `bool Matches(int callIndex, string? memoDataHex)`, `static string MarkerHexAt(IReadOnlyList<MemoSubmission> memos, int index)`.
  - `BatchSweep.CompactHistoryAsync(ILedgerSource source, string account, long from, long to, RoundMarkers? markers, CancellationToken ct) : Task<IReadOnlyList<SweepRecord>>`; `BatchSweep.Covers(IEnumerable<SweepRecord> records, uint from, uint to)` (the `LedgerTransaction` overload and `HistoryAsync` stay for the session until Task 6).
  - `BatchRound(BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, byte[] candidatesPerMemo, BatchProgress progress)` with `internal BatchRegistry Registry`.
  - `BatchRegistry(BatchFence fence, byte[] candidatesPerMemo, BatchTrace trace)` with `internal BatchFence Fence`.
  - `BatchProgress(IProgress<int>? inner, int count)`.
  - `BatchStrategy.FinishedRegistries : IReadOnlyList<WeakReference>` and `BatchStrategy.RoundFences : IReadOnlyList<BatchFence>` (internal test seams: one entry per round run).

**What lives where after this task (spec S5):** per pass, owned by one `SubmitAllAsync` call — the landed bits (`SubmittedMemosBuilder.Landed`), the progress bits (`BatchProgress`), and a `byte[]` of candidate counts that every round of the pass increments; per round, released when it ends — the plan, the registry with its signed blobs, the round's compact history and its marker map; per strategy (`BatchContext`, as now) — fence, spending, ledger floor, window, trace.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/BatchRoundsTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class BatchRoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BatchStrategyOptions Capped(int memos) => BatchTestNode.FastOptions with { MaxRoundMemos = memos };

    private static int Rounds(BatchStrategy strategy) => strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal));

    [Fact]
    public async Task Three_hundred_memos_at_sixty_four_a_round_go_in_five_rounds_and_each_lands_once()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        SubmittedMemos landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        Assert.Equal(300, landed.Landed.Count);
        Assert.Equal(5, Rounds(strategy));
        IReadOnlyDictionary<string, List<LedgerTransaction>> records = await node.LandedByMarkerAsync();
        Assert.Equal(300, records.Count);
        Assert.All(records.Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task Every_rounds_registry_is_released_when_the_round_ends()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        // Relies on finished async state machines dropping their locals, which holds without a
        // debugger attached. Under a debugger, or with TPL async-debugging tracing on, the boxes keep
        // their state and this test fails; run it without one.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.Equal(5, strategy.FinishedRegistries.Count);
        Assert.All(strategy.FinishedRegistries, registry => Assert.False(registry.IsAlive));
    }

    [Fact]
    public async Task One_memo_past_a_full_round_goes_as_a_solo_after_it()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(65), null, Token);

        Assert.Equal(8, node.Sink.Transmissions.Count(t => t.TransactionType == "Batch"));
        Assert.Equal("AccountSet", node.Sink.Transmissions[^1].TransactionType);
        Assert.Equal(2, Rounds(strategy));
    }

    [Fact]
    public async Task Candidate_counts_carry_across_rounds_and_a_fourth_candidate_is_refused()
    {
        // Two memos whose inners always end in tec: each round sends them again, never as a solo,
        // until each has been in three candidates; the next round would need a fourth.
        BatchTestNode node = new(sink =>
        {
            sink.InnerResult[MemoLayout.ChunkMarkerHex(100)] = "tecFAKE";
            sink.InnerResult[MemoLayout.ChunkMarkerHex(101)] = "tecFAKE";
        });
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(130), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains(MemoLayout.ChunkMarkerHex(100), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, Rounds(strategy));
    }

    [Fact]
    public async Task The_payers_budget_spans_rounds()
    {
        // Two batches of eight a round at unit 15 cost 165 each: the third batch, in round two, is the last that fits 500.
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(
            Capped(16), new BatchBudget(15, 30, PayerBudgetDrops: 500, WriterBudgetDrops: 10_000));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(32), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Equal(3, node.Sink.Transmissions.Where(t => t.TransactionType == "Batch").Select(t => t.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Every_round_covers_its_deadlines_with_the_one_fence_of_the_strategy()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(16));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(48), null, Token);

        // Rising deadlines alone would pass with a fresh fence per round; the seam shows there is one.
        Assert.Equal(3, strategy.RoundFences.Count);
        Assert.Single(strategy.RoundFences.Distinct());
        IReadOnlyList<long> recorded = node.Fence.Recorded;
        Assert.Equal(recorded.Order(), recorded);
        Assert.Equal(recorded.Distinct().Count(), recorded.Count);
    }

    [Fact]
    public async Task The_ledger_floor_outlives_a_round()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(16) with { NodeOutageLimit = TimeSpan.FromMilliseconds(300) });
        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        // From now on every answer comes from a ledger the previous round already read past. A
        // round with a floor of its own would plan from it; the strategy's floor refuses it as a
        // node that is behind, until the outage limit ends the call.
        node.Sink.AutoClose = false;
        node.Ledger.CurrentLedger -= 10;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task A_chunk_pass_keeps_no_hash_and_reports_each_blocks_range()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        SubmittedMemos landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(100), null, Token);

        Assert.All(Enumerable.Range(0, 100), index => Assert.False(landed.TryHashOf(index, out _)));
        List<long> ledgers = [.. (await node.LandedByMarkerAsync()).Values.Select(copies => copies[0].LedgerIndex)];
        Assert.Equal(new LedgerRange(ledgers.Min(), ledgers.Max()), Assert.Single(landed.BlockLedgers).Value);
    }

    [Fact]
    public async Task A_manifest_pass_keeps_its_hash_and_ledger()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        SubmittedMemos landed = await strategy.SubmitAllAsync([new MemoSubmission(MemoLayout.ManifestMarkerHex, "7B7D", MemoLayout.JsonFormatHex)], null, Token);

        Assert.True(landed.TryHashOf(0, out string? hash));
        Assert.True(landed.TryLedgerOf(0, out long ledger));
        Assert.Equal(ledger, (await node.Ledger.TransactionAsync(hash, Token))!.LedgerIndex);
    }
}
```

`tests/XrplFile.Core.Tests/SweepRecordTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class SweepRecordTests
{
    private static LedgerTransaction Record(string? marker, string? data, string result = "tesSUCCESS") =>
        new("AB", 42, "rWriter", result == "tesSUCCESS", true, result, marker, data, null)
        {
            TransactionType = "AccountSet", Sequence = 7, Fee = 0, ParentBatchID = "CD", TransactionIndex = 3,
        };

    [Fact]
    public void A_chunk_record_is_classified_and_keeps_no_memo_data()
    {
        IReadOnlyList<MemoSubmission> memos = BatchTestNode.Memos(4);
        RoundMarkers markers = new(memos, [1, 2]);

        SweepRecord matching = SweepRecord.From(Record(memos[2].MemoTypeHex, memos[2].MemoDataHex), markers);
        SweepRecord conflicting = SweepRecord.From(Record(memos[1].MemoTypeHex, "00"), markers);
        SweepRecord outside = SweepRecord.From(Record(memos[3].MemoTypeHex, memos[3].MemoDataHex), markers);

        Assert.Equal((MarkerKind.Chunk, 2L, 2, DataVerdict.Matches), (matching.Marker, matching.MarkerIndex, matching.CallIndex, matching.Verdict));
        Assert.Equal(DataVerdict.Conflicts, conflicting.Verdict);
        Assert.Equal((-1, DataVerdict.NotChecked), (outside.CallIndex, outside.Verdict));
        Assert.Equal((3, "CD", 7u), (matching.TransactionIndex, matching.ParentBatchID, matching.Sequence));
        Assert.DoesNotContain(typeof(SweepRecord).GetProperties(), property => property.Name.Contains("Memo", StringComparison.Ordinal));
    }

    // The class is named as a string: ResultClass is internal, and a public test method may not
    // take it as a parameter (CS0051).
    [Theory]
    [InlineData("tesSUCCESS", "Success")]
    [InlineData("tecNO_PERMISSION", "Claimed")]
    [InlineData("tefPAST_SEQ", "Other")]
    [InlineData(null, "Other")]
    public void Results_fall_into_three_classes(string? result, string expected)
    {
        LedgerTransaction tx = new("AB", 42, "rWriter", result == "tesSUCCESS", true, result, null, null, null);

        Assert.Equal(expected, SweepRecord.From(tx, null).Result.ToString());
    }

    [Fact]
    public void A_foreign_ticket_create_covers_its_own_sequence_and_every_ticket_it_took()
    {
        SweepRecord create = SweepRecord.From(new LedgerTransaction("C", 5, "rW", true, true, "tesSUCCESS", null, null, null)
        {
            TransactionType = "TicketCreate", Sequence = 10, TicketCount = 3,
        }, null);
        SweepRecord next = SweepRecord.From(new LedgerTransaction("N", 6, "rW", false, true, "tecFAKE", null, null, null) { Sequence = 14 }, null);

        Assert.True(BatchSweep.Covers([create, next], 10, 15));
        Assert.False(BatchSweep.Covers([create], 10, 15));
        Assert.True(BatchSweep.Covers(Array.Empty<SweepRecord>(), 10, 10));
    }

    [Fact]
    public void Payer_fees_are_counted_by_the_outer_hash_and_only_when_validated()
    {
        BatchCandidate outer = new(0, isOuter: true, "rPayer", 500, 900, 120, [0], new SignedTransaction("00", "AA"));
        LedgerTransaction validated = new("AA", 5, "rPayer", true, true, "tesSUCCESS", null, null, null) { Fee = 120 };
        SweepRecord ours = SweepRecord.From(validated, null);
        SweepRecord pendingOne = SweepRecord.From(validated with { Validated = false, Fee = 999 }, null);
        SweepRecord foreign = SweepRecord.From(validated with { Hash = "BB", Fee = 500 }, null);

        RoundResult counted = BatchSweep.Outcome([outer], [], [ours, foreign]);
        RoundResult skipped = BatchSweep.Outcome([outer], [], [pendingOne]);

        Assert.Equal(120, counted.PayerFeeDrops);
        Assert.Equal(0, skipped.PayerFeeDrops);
        Assert.True(skipped.PayerFeeKnown);
    }

    [Fact]
    public void List_and_manifest_markers_are_named()
    {
        Assert.Equal((MarkerKind.List, 3L), SweepRecord.Classify(MemoLayout.ListMarkerHex(3)));
        Assert.Equal((MarkerKind.Manifest, 0L), SweepRecord.Classify(MemoLayout.ManifestMarkerHex));
        Assert.Equal((MarkerKind.None, 0L), SweepRecord.Classify(null));
        Assert.Equal((MarkerKind.Other, 0L), SweepRecord.Classify("ABCD"));
    }
}
```

In `tests/XrplFile.Core.Tests/XrplClientLedgerSourceTests.cs`, add:

```csharp
    [Fact]
    public void A_record_carries_its_position_in_the_ledger()
    {
        using JsonDocument record = JsonDocument.Parse(
            """{"hash":"ab","ledger_index":9,"validated":true,"tx_json":{"Account":"rW","TransactionType":"AccountSet","Sequence":3},"meta":{"TransactionResult":"tesSUCCESS","TransactionIndex":17}}""");

        Assert.Equal(17, XrplClientLedgerSource.ParseRecord(record.RootElement).TransactionIndex);
    }
```

(Add `using System.Text.Json;` at the top of that file if it is not there.)

Migrate the existing tests that the new signatures break:

- `tests/XrplFile.Core.Tests/BatchRegistryTests.cs`: the counts are a `byte[]` indexed by memo. `A_candidate_is_registered_only_after_its_deadline_is_persisted` passes `new byte[6]`; `Registering_reserves_a_candidate_for_every_memo_it_carries` builds `byte[] candidates = new byte[2]; candidates[1] = 2;` and asserts `Assert.Equal(1, candidates[0]); Assert.Equal(3, candidates[1]);`; `After_a_halt_nothing_registers_and_nothing_is_reserved` and `A_fence_that_cannot_be_saved_registers_nothing` pass `byte[] candidates = new byte[2]` and replace `Assert.Empty(candidates)` with `Assert.All(candidates, count => Assert.Equal(0, count));`.
- `tests/XrplFile.Core.Tests/BatchIdentityTests.cs`: delete `A_ticket_create_covers_its_own_sequence_and_its_tickets` and `A_payer_record_that_is_not_validated_is_not_counted_in_the_payer_fee`; `SweepRecordTests` above carries both over compact records (`Covers([], 10, 10)` would also be ambiguous between the two overloads). The identity tests of that file (foreign transactions answering `AccountInUse`) are unchanged and now run over compact records, because `FinalAsync` builds them.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchRoundsTests -class XrplFile.Tests.SweepRecordTests`
Expected: build error (`MaxRoundMemos`, `SweepRecord`, `RoundMarkers`, `FinishedRegistries`, `TransactionIndex` missing).

- [ ] **Step 3: `TransactionIndex` and the option**

In `src/XrplFile.Core/LedgerTransaction.cs`, add:

```csharp
    /// <summary>The transaction's position in its ledger (<c>meta.TransactionIndex</c>), or null when the record does not say (spec S6).</summary>
    public int? TransactionIndex { get; init; }
```

In `XrplClientLedgerSource.ParseRecord`, add to the object initializer:

```csharp
            TransactionIndex = UInt(meta, "TransactionIndex") is { } position ? (int)position : null,
```

In `src/XrplFile.Core/BatchStrategyOptions.cs`, add:

```csharp
    /// <summary>The most pending memos one round takes (spec S5): 2,048 batches, about 37 MB of signed blobs.</summary>
    public const int DefaultMaxRoundMemos = 16_384;

    /// <summary>
    /// The most pending memos one round takes (spec S5); a pass with more runs a series of rounds,
    /// each released when it ends. Tests lower it.
    /// </summary>
    public int MaxRoundMemos { get; init; } = DefaultMaxRoundMemos;
```

- [ ] **Step 4: The compact record and the round's markers**

`src/XrplFile.Core/RoundMarkers.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// A round's memos by marker, built from the round's pending memos only (at most
/// <see cref="BatchStrategyOptions.MaxRoundMemos"/>). Chunk and list markers carry their index, and a
/// pass's indices are distinct, so the pass-wide check of distinct markers is gone (spec 4.4); a
/// duplicate within a round is still refused, because the sweep could not tell the two apart.
/// </summary>
internal sealed class RoundMarkers
{
    private readonly IReadOnlyList<MemoSubmission> _memos;
    private readonly Dictionary<string, int> _byMarker;

    public RoundMarkers(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending)
    {
        _memos = memos;
        _byMarker = new Dictionary<string, int>(pending.Count, StringComparer.OrdinalIgnoreCase);

        foreach (int index in pending)
        {
            string marker = MarkerHexAt(memos, index);

            if (!_byMarker.TryAdd(marker, index))
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"two memos carry the marker {marker}, which the sweep could not tell apart");
            }
        }
    }

    public bool TryIndexOf(string? memoTypeHex, out int callIndex)
    {
        callIndex = -1;
        return memoTypeHex is not null && _byMarker.TryGetValue(memoTypeHex, out callIndex);
    }

    /// <summary>Whether a record carries exactly the bytes this memo carries. The memo is built from the source to answer.</summary>
    public bool Matches(int callIndex, string? memoDataHex) =>
        string.Equals(memoDataHex, _memos[callIndex].MemoDataHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>A memo's marker, read without the file when the memos are a chunk pass's lazy list.</summary>
    public static string MarkerHexAt(IReadOnlyList<MemoSubmission> memos, int index) =>
        memos is LazyMemoList lazy ? lazy.MarkerHexAt(index) : memos[index].MemoTypeHex;
}
```

`src/XrplFile.Core/SweepRecord.cs`:

```csharp
namespace XrplFile;

/// <summary>A record's result, as identity, coverage and fees need it (spec S6).</summary>
internal enum ResultClass : byte
{
    Success,

    /// <summary>A <c>tec</c>: applied, sequence and fee consumed, nothing written.</summary>
    Claimed,

    Other,
}

internal enum MarkerKind : byte
{
    None,
    Chunk,
    List,
    Manifest,
    Other,
}

/// <summary>What a record's data says about the memo of this round that wears its marker (spec S6).</summary>
internal enum DataVerdict : byte
{
    /// <summary>No memo of this round wears its marker.</summary>
    NotChecked,
    Matches,
    Conflicts,
}

/// <summary>
/// A history record as a Batch round keeps it (spec S6): classified the moment a sweep reads it, and
/// kept with the fields identity (B12), coverage (B30) and fee attribution (B29) need. The memo's
/// data is compared once and dropped, so a round's history holds no chunk bytes.
/// </summary>
internal sealed record SweepRecord(
    string Hash,
    string Account,
    long LedgerIndex,
    int? TransactionIndex,
    bool Validated,
    ResultClass Result,
    string? TransactionType,
    uint Sequence,
    uint? TicketSequence,
    uint? TicketCount,
    long? Fee,
    string? ParentBatchID,
    MarkerKind Marker,
    long MarkerIndex,
    int CallIndex,
    DataVerdict Verdict)
{
    public bool Succeeded => Result == ResultClass.Success;

    public static SweepRecord From(LedgerTransaction tx, RoundMarkers? markers)
    {
        ArgumentNullException.ThrowIfNull(tx);

        (MarkerKind kind, long index) = Classify(tx.MemoTypeHex);
        int call = -1;
        DataVerdict verdict = DataVerdict.NotChecked;

        if (markers is not null && markers.TryIndexOf(tx.MemoTypeHex, out int found))
        {
            call = found;
            verdict = markers.Matches(found, tx.MemoDataHex) ? DataVerdict.Matches : DataVerdict.Conflicts;
        }

        ResultClass result = tx.Succeeded ? ResultClass.Success
            : tx.Result is { } code && code.StartsWith("tec", StringComparison.Ordinal) ? ResultClass.Claimed
            : ResultClass.Other;

        return new SweepRecord(
            tx.Hash, tx.Account, tx.LedgerIndex, tx.TransactionIndex, tx.Validated, result, tx.TransactionType, tx.Sequence,
            tx.TicketSequence, tx.TicketCount, tx.Fee, tx.ParentBatchID, kind, index, call, verdict);
    }

    public static (MarkerKind Kind, long Index) Classify(string? memoTypeHex)
    {
        if (memoTypeHex is null) return (MarkerKind.None, 0);
        if (MemoLayout.ChunkIndexOf(memoTypeHex) is { } chunk) return (MarkerKind.Chunk, chunk);
        if (MemoLayout.ListIndexOf(memoTypeHex) is { } list) return (MarkerKind.List, list);
        if (MemoLayout.IsManifest(memoTypeHex)) return (MarkerKind.Manifest, 0);

        return (MarkerKind.Other, 0);
    }
}
```

- [ ] **Step 5: `BatchSweep` over compact records**

In `src/XrplFile.Core/BatchSweep.cs`:

1. `FinalAsync` takes `RoundMarkers markers` instead of `IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending`, reads with `CompactHistoryAsync(context.Source, writer, writerFrom, settled.LedgerIndex, markers, token)` for the writer and `CompactHistoryAsync(context.Source, payer, ..., markers: null, token)` for the payer, and works on `List<SweepRecord>` throughout (`Own`, `Covers`, `RequireIdentity`, `Outcome`). `known.Records` is `IReadOnlyList<SweepRecord>`.

2. Add:

```csharp
    /// <summary>
    /// An account's records over [<paramref name="from"/>, <paramref name="to"/>], classified as they
    /// arrive (spec S6): no page's memo data outlives the page.
    /// </summary>
    internal static async Task<IReadOnlyList<SweepRecord>> CompactHistoryAsync(
        ILedgerSource source, string account, long from, long to, RoundMarkers? markers, CancellationToken cancellationToken)
    {
        List<SweepRecord> records = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(account, from, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            records.Add(SweepRecord.From(tx, markers));
        }

        return records;
    }

    /// <inheritdoc cref="Covers(IEnumerable{LedgerTransaction}, uint, uint)"/>
    internal static bool Covers(IEnumerable<SweepRecord> records, uint from, uint to) =>
        CoversCore(records.Select(tx => (tx.Sequence, tx.TransactionType, tx.TicketCount)), from, to);
```

   and make the existing `Covers(IEnumerable<LedgerTransaction> records, uint from, uint to)` delegate to `CoversCore(records.Select(tx => (tx.Sequence, tx.TransactionType, tx.TicketCount)), from, to)`, moving its body into

```csharp
    private static bool CoversCore(IEnumerable<(uint Sequence, string? TransactionType, uint? TicketCount)> records, uint from, uint to)
```

   unchanged except that it reads the tuple's members.

3. `Outcome` over compact records:

```csharp
    internal static RoundResult Outcome(IReadOnlyList<BatchCandidate> candidates, IReadOnlyList<SweepRecord> writerRecords, IReadOnlyList<SweepRecord> payerRecords) =>
        Outcome(new RoundIdentity(candidates), writerRecords, payerRecords);

    private static RoundResult Outcome(RoundIdentity identity, IReadOnlyList<SweepRecord> writerRecords, IReadOnlyList<SweepRecord> payerRecords)
    {
        Dictionary<int, LandedInner> landed = [];

        foreach (SweepRecord tx in writerRecords)
        {
            if (!tx.Succeeded || !tx.Validated || !identity.IsOurs(tx)) continue;

            // Matched on marker and data (spec B28): the verdict was taken when the record was read.
            if (tx.CallIndex < 0 || tx.Verdict != DataVerdict.Matches) continue;

            landed.TryAdd(tx.CallIndex, new LandedInner(tx.Hash, tx.LedgerIndex));
        }

        long fee = 0;
        bool known = true;

        foreach (SweepRecord tx in payerRecords)
        {
            if (!tx.Validated || !identity.IsOuter(tx.Hash)) continue;

            if (tx.Fee is { } drops) fee += drops;
            else known = false;
        }

        return new RoundResult(landed, fee, known);
    }
```

4. `RoundIdentity.IsOurs` takes a `SweepRecord`; `Own` and `RequireIdentity` take `IReadOnlyList<SweepRecord>`; the message text is unchanged.

5. `KnownHistory` becomes `internal sealed record KnownHistory(IReadOnlyList<SweepRecord> Records, long ReadFrom);`.

`HistoryAsync` (returning `LedgerTransaction`s) stays for `PublicationSession` until Task 6.

- [ ] **Step 6: The registry, the progress and the round**

`BatchRegistry`: the primary constructor takes `byte[] candidatesPerMemo`, and the reservation becomes

```csharp
            foreach (int memo in candidate.Memos)
            {
                candidatesPerMemo[memo] = checked((byte)(candidatesPerMemo[memo] + 1));
            }
```

`BatchProgress` keeps its contract with a bit set:

```csharp
internal sealed class BatchProgress(IProgress<int>? inner, int count)
{
    private readonly Lock _gate = new();
    private readonly LandedSet _found = new(count);
    private int _reported;
    private bool _sent;

    // Sent() unchanged.

    public void Found(IEnumerable<int> indices)
    {
        lock (_gate)
        {
            foreach (int index in indices) _found.Add(index);

            if (_found.Count <= _reported) return;

            _reported = _found.Count;
            inner?.Report(_reported);
        }
    }
}
```

`BatchRound` keeps its primary constructor, with the counts as a `byte[]`, and gains two initialized members:

```csharp
internal sealed class BatchRound(
    BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, byte[] candidatesPerMemo,
    BatchProgress progress)
{
    // ... the constants and fields as today, with _writerHistory now a List<SweepRecord> ...

    private readonly RoundMarkers _markers = new(memos, pending);

    /// <summary>The round's candidates and their signed blobs; nothing outlives the round that holds it.</summary>
    internal BatchRegistry Registry { get; } = new(context.Fence, candidatesPerMemo, context.Trace);
```

`BatchRegistry` exposes the fence it was built with, for the strategy's seam: `internal BatchFence Fence => fence;`.

In `RunAsync`, delete `BatchRegistry registry = new(...)` and use `Registry` where `registry` was used; pass `_markers` to `BatchSweep.FinalAsync(context, _markers, Registry.Candidates, start, settled, KnownWriterHistory(), cancellationToken)`. In `ProgressLoopAsync`, read with `BatchSweep.CompactHistoryAsync(context.Source, Writer, from, to, _markers, cancellationToken)`, report `BatchSweep.Outcome(registry.Candidates, records, []).Landed.Keys`, and keep `records` as `IReadOnlyList<SweepRecord>` in `RememberWriterHistory`. `KnownWriterHistory` returns `new BatchSweep.KnownHistory([.. _writerHistory.Where(tx => tx.LedgerIndex < _writerReadFrom).OrderBy(tx => tx.LedgerIndex)], _writerReadFrom)`.

- [ ] **Step 7: The strategy's passes as capped rounds**

In `src/XrplFile.Core/BatchStrategy.cs`, add the fields `private readonly List<WeakReference> _finishedRegistries = [];` and `private readonly List<BatchFence> _roundFences = [];` and replace `SubmitAllAsync`, `RequireAnotherCandidate` and `RequireDistinctMarkers` with:

```csharp
    /// <summary>One weak reference per round run, to its registry: a test seam that proves a round's blobs do not outlive it (spec S5).</summary>
    internal IReadOnlyList<WeakReference> FinishedRegistries
    {
        get { lock (_finishedRegistries) return [.. _finishedRegistries]; }
    }

    /// <summary>The fence each round ran with: a test seam that proves the fence is the strategy's, not the round's (spec S5).</summary>
    internal IReadOnlyList<BatchFence> RoundFences
    {
        get { lock (_finishedRegistries) return [.. _roundFences]; }
    }

    public async Task<SubmittedMemos> SubmitAllAsync(
        IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memos);

        // Per pass and compact (spec S19): a landed bit and a candidate count per memo. Each round
        // takes at most MaxRoundMemos of the memos still missing, in index order, so what a round
        // returned unlanded goes first into the next one.
        SubmittedMemosBuilder landed = new(memos.Count, keepChunkFacts: false);
        byte[] candidates = new byte[memos.Count];
        BatchProgress progress = new(submitted, memos.Count);

        while (landed.Landed.Count < memos.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int[] pending = NextRound(landed.Landed, _options.MaxRoundMemos);
            RequireAnotherCandidate(memos, pending, candidates);

            RoundResult round = await RunRoundAsync(memos, pending, candidates, progress, cancellationToken).ConfigureAwait(false);

            foreach ((int index, LandedInner inner) in round.Landed)
            {
                landed.Record(index, RoundMarkers.MarkerHexAt(memos, index), inner.Hash, inner.LedgerIndex);
            }

            Interlocked.Add(ref _payerFeeDrops, round.PayerFeeDrops);
            if (!round.PayerFeeKnown) _payerFeeUnknown = true;
        }

        return landed.Build();
    }

    /// <summary>Runs one round; its registry, history and marker map go out of scope with it.</summary>
    private async Task<RoundResult> RunRoundAsync(
        IReadOnlyList<MemoSubmission> memos, int[] pending, byte[] candidates, BatchProgress progress, CancellationToken cancellationToken)
    {
        BatchRound round = new(_context, memos, pending, candidates, progress);
        lock (_finishedRegistries)
        {
            _finishedRegistries.Add(new WeakReference(round.Registry));
            _roundFences.Add(round.Registry.Fence);
        }

        return await round.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first <paramref name="cap"/> memos that have not landed, in index order.</summary>
    private static int[] NextRound(LandedSet landed, int cap)
    {
        List<int> pending = new(Math.Min(cap, landed.Capacity - landed.Count));

        for (int index = landed.NextMissing(0); index >= 0 && pending.Count < cap; index = landed.NextMissing(index + 1))
        {
            pending.Add(index);
        }

        return [.. pending];
    }

    /// <summary>A memo that would need a fourth candidate fails the run (spec B13); the counts carry across rounds (spec S5).</summary>
    private void RequireAnotherCandidate(IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, byte[] candidates)
    {
        foreach (int index in pending)
        {
            if (candidates[index] >= _options.MaxAttemptsPerMemo)
            {
                throw new XrplFileException(XrplFileError.SubmitRefused,
                    $"the memo marked {RoundMarkers.MarkerHexAt(memos, index)} did not land in the {_options.MaxAttemptsPerMemo} candidates a session allows it");
            }
        }
    }
```

The class summary's sentence "the candidates per memo and the budget are counted over its lifetime" becomes "the budget is counted over its lifetime and the candidates per memo over each call, across its rounds".

- [ ] **Step 8: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchRoundsTests -class XrplFile.Tests.SweepRecordTests -class XrplFile.Tests.XrplClientLedgerSourceTests -class XrplFile.Tests.BatchRegistryTests -class XrplFile.Tests.BatchIdentityTests`
Expected: PASS.
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS. A test that asserted "two memos carry the marker" across a whole call still passes, because the round check raises the same error and message.

- [ ] **Step 9: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): capped Batch rounds over compact sweep records

A Batch pass now runs rounds of at most 16,384 pending memos; each round's
registry, history and marker map are released when it ends, while candidate
counts (a byte per memo), the budget, the fence and the ledger floor carry
across rounds. Sweeps classify a record the moment they read it and keep only
what identity, coverage and fees need, never memo data. LedgerTransaction
gains TransactionIndex."
```

---

### Task 5: A conflicting copy fails the run (S17)

**Files:**
- Modify: `src/XrplFile.Core/XrplFileException.cs` (+ `WriterWindowConflict`), `src/XrplFile.Core/PublicationState.cs` (+ `Failure`), `src/XrplFile.Core/PublicationSession.cs` (`ReconcileAsync`, `RequireResumableAsync`, the Batch resume), `app/XrplFile.App.Core/PublishErrorMessages.cs`, `app/XrplFile.App.Core/ReadErrorMessages.cs` (the publishing-only arm), `app/XrplFile.App.Core/RunModel.cs` (`IsResumable`)
- Test: `tests/XrplFile.Core.Tests/WindowConflictTests.cs`, `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs` (one test), `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs` (one test), `tests/XrplFile.App.Core.Tests/RunModelTests.cs` (one test)

**Interfaces:**
- Consumes: Task 4 (`SweepRecord`, `DataVerdict`, `MarkerKind`), Task 3 (`ReconcileAsync`, `RequireResumableAsync`, `ChunkReads.MatchesHex`).
- Produces:
  - `XrplFileError.WriterWindowConflict`.
  - `PublicationState.Failure : string?` (JSON `Failure`, written only when set) and `public const string WriterWindowConflictFailure = "WriterWindowConflict"` on `PublicationState`.
  - Every resume refuses a state whose `Failure` is `"WriterWindowConflict"` with `WriterWindowConflict`, before it hashes, waits or reads anything.

**Scope (Spec question 4):** the conflict rule applies to Batch runs, at the reconciliation of a version-3 resume. Within a round no separate check is needed: `BatchSweep.RequireIdentity` runs first and refuses any writer record that is not an inner of the round's own outers with `AccountInUse` (spec B12), and the round's own inners carry the round's own bytes, so a conflicting copy written during a round surfaces as B12 — which is what the spec's own rationale for S17 ("B12 stops concurrent use during a round") relies on. A one-account run keeps rule I12: a copy with other bytes is a straggler and is ignored; its full read-back (S9) catches a block it would break.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/WindowConflictTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public sealed class WindowConflictTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-conflict-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node) =>
        (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);

    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node)
    {
        byte[] file = VectorStream.Bytes("conflict", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= 1) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;
        return (session, file);
    }

    /// <summary>A chunk memo of the writer, validated and successful, that another publication left in the window.</summary>
    private static void Plant(BatchTestNode node, long chunk, byte[] data, string hash) =>
        node.Ledger.Add(new LedgerTransaction(hash, node.Ledger.CurrentLedger, node.Writer.Account, true, true, "tesSUCCESS",
            MemoLayout.ChunkMarkerHex(chunk), MemoLayout.Hex(data), null)
        {
            TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
        });

    [Fact]
    public async Task A_conflicting_copy_in_the_window_fails_the_resume_and_is_recorded()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, [1, 2, 3], new string('C', 64));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, error.Error);
        Assert.Equal(PublicationState.WriterWindowConflictFailure, PublicationState.Load(first.StatePath).Failure);
    }

    [Fact]
    public async Task A_recorded_conflict_is_reported_again_without_reading_or_sending()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, [1, 2, 3], new string('C', 64));
        await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));
        int reads = node.Ledger.AccountTransactionsCalls;
        int snapshots = node.Ledger.SnapshotReads;
        int sent = node.Sink.Transmissions.Count;

        XrplFileException again = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token));

        Assert.Equal(XrplFileError.WriterWindowConflict, again.Error);
        Assert.Equal((reads, snapshots, sent), (node.Ledger.AccountTransactionsCalls, node.Ledger.SnapshotReads, node.Sink.Transmissions.Count));
    }

    [Fact]
    public async Task An_identical_duplicate_is_harmless()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        Plant(node, 0, file.AsSpan(0, MemoLayout.ChunkBytes).ToArray(), new string('D', 64));

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }
}
```

`FakeLedgerSource.SnapshotReads` and `Add` already exist.

In `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`, add:

```csharp
    [Fact]
    public void A_conflict_in_the_window_asks_for_a_fresh_writer()
    {
        Assert.Equal(
            "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key.",
            PublishErrorMessages.For(XrplFileError.WriterWindowConflict));
    }
```

In `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs`, add (Tasks 7 and 11 extend its list):

```csharp
    [Fact]
    public void The_streaming_publication_errors_share_the_publishing_only_sentence()
    {
        string shared = ReadErrorMessages.For(XrplFileError.SubmitRefused);

        foreach (XrplFileError error in new[] { XrplFileError.WriterWindowConflict })
        {
            Assert.Equal(shared, ReadErrorMessages.For(error));
        }
    }
```

In `tests/XrplFile.App.Core.Tests/RunModelTests.cs`, add beside the existing `IsResumable` tests:

```csharp
    [Fact]
    public void A_run_stopped_by_a_window_conflict_offers_no_Resume()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("xrpl-file-runmodel-").FullName, "run.json");
        new PublicationState(3, "testnet", "rWriter", "key", "text/plain", null, 2500, 1000, "AB", 1, "chunks",
            Strategy: "batch", Payer: Xrpl.Wallet.XrplWallet.Generate().ClassicAddress, PayerKeyReference: "key-p", Fence: 0,
            Failure: PublicationState.WriterWindowConflictFailure).Save(path);
        RunSnapshot snapshot = new(PublicationRunState.Failed, null, null, "failed", "f", "testnet", null) { StatePath = path };

        Assert.False(RunModel.IsResumable(snapshot));
    }
```

`PublicationState.Load` refuses a version-3 state whose payer is not a classic address, hence the generated one.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.WindowConflictTests`
Expected: build error (`WriterWindowConflict`, `Failure` missing).

- [ ] **Step 3: The error, the state field and the sentence**

`XrplFileError`, after `FeeTooHigh`:

```csharp
    /// <summary>
    /// The writer carries a validated, successful chunk memo of this publication's index with other
    /// data inside the publication's window (spec S17): a reader takes the first eligible copy, so
    /// the block can break whatever the run adopted. Recorded on the state; a resume reports it again.
    /// </summary>
    WriterWindowConflict,
```

`PublicationState`: add the parameter at the end of the record

```csharp
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Failure = null)
```

with its doc

```csharp
/// <param name="Failure">
/// Why the run cannot go on, when that outlives a resume: <see cref="WriterWindowConflictFailure"/>
/// (spec S17). A resume reports it again without sending anything. Absent otherwise.
/// </param>
```

and the constant

```csharp
    /// <summary>The value of <see cref="Failure"/> for spec S17's conflict: the error's own name.</summary>
    public const string WriterWindowConflictFailure = nameof(XrplFileError.WriterWindowConflict);
```

`PublishErrorMessages.For(XrplFileError)`, after `FeeTooHigh`:

```csharp
        XrplFileError.WriterWindowConflict =>
            "The writer account carries other data for this file's chunks — was it used for another publication while this one was interrupted? Publish this file again from a fresh writer key.",
```

`ReadErrorMessages.For(XrplFileError)`: add `or XrplFileError.WriterWindowConflict` to the publishing-only arm (the one ending `or XrplFileError.AccountInUse or XrplFileError.FeeTooHigh =>`). Reading cannot produce it, and the arm's `_ =>` fallback throws, which `Every_error_has_a_sentence` would catch.

- [ ] **Step 4: Reconciliation, the record on the state, and the refusal on resume**

In `PublicationSession`:

1. `ReconcileAsync(IReadOnlyList<LedgerTransaction> history, bool conflictsFail, CancellationToken cancellationToken)`; the Batch resume passes `true`, the one-account resume `false`. The chunk branch becomes:

```csharp
            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } chunkIndex)
            {
                if (chunkIndex >= chunkCount) continue;
                if (landing.IsLanded(chunkIndex) && !conflictsFail) continue;

                int length = await _file.ReadChunkAsync(chunkIndex, buffer, cancellationToken).ConfigureAwait(false);

                if (ChunkReads.MatchesHex(data, buffer.AsSpan(0, length)))
                {
                    // First in canonical order, exactly as a reader resolves it.
                    landing.Land(chunkIndex, tx.LedgerIndex);
                }
                else if (conflictsFail)
                {
                    RecordFailure(PublicationState.WriterWindowConflictFailure);
                    throw new XrplFileException(XrplFileError.WriterWindowConflict,
                        $"the writer's {tx.Hash} in ledger {tx.LedgerIndex} carries chunk {chunkIndex} with other data than this file's");
                }

                // A one-account run ignores a copy with other bytes as a straggler (spec I12).
                continue;
            }
```

2. Add:

```csharp
    /// <summary>Writes a failure that must outlive this session (spec S17) before it is thrown.</summary>
    private void RecordFailure(string failure)
    {
        lock (_stateLock)
        {
            if (string.Equals(_state.Failure, failure, StringComparison.Ordinal)) return;

            _state = _state with { Failure = failure };
            _state.Save(StatePath);
        }
    }
```

3. In `RequireResumableAsync`, first of all (before the "done" check):

```csharp
        if (string.Equals(state.Failure, PublicationState.WriterWindowConflictFailure, StringComparison.Ordinal))
        {
            throw new XrplFileException(XrplFileError.WriterWindowConflict,
                $"{statePath} was stopped because the writer carries other data for this file's chunks; it is not resumed");
        }
```

  In the Batch `ResumeAsync`, move `RequireResumableAsync` so it stays before `WaitForFenceAsync` (it already is), so a recorded conflict waits for nothing.

- [ ] **Step 5: No Resume button for such a run**

In `RunModel.IsResumable`, replace the `return !string.Equals(...Phase, "done"...)` with:

```csharp
            PublicationState state = PublicationState.Load(path);
            return !string.Equals(state.Phase, "done", StringComparison.Ordinal) && state.Failure is null;
```

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.WindowConflictTests`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishErrorMessagesTests -class XrplFile.App.Core.Tests.ReadErrorMessagesTests -class XrplFile.App.Core.Tests.RunModelTests`
Run: both unit suites with `-trait- "Category=Integration"`.
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src app tests
git commit -m "feat(core): a conflicting copy of a chunk fails a Batch run

A validated, successful writer record with one of this publication's chunk
markers and other data in the window stops a resume's reconciliation with
WriterWindowConflict; within a round, identity (B12) already refuses any
record that is not the round's own. The failure is written
to the state file, a resume reports it again without sending, and the run
screen offers no Resume for it. One-account runs keep ignoring stragglers."
```

---

### Task 6: Streaming, filtered reconciliation and the manifest's second pass (S20)

**Files:**
- Create: `src/XrplFile.Core/SequenceRanges.cs`
- Modify: `src/XrplFile.Core/FileTree.cs` (+ `TryDecodeFragment`), `src/XrplFile.Core/PublicationSession.cs` (replace `ReconcileAsync`, `HistoryAsync`, `CompleteHistoryAsync`, `FindManifest` with streaming versions; both resumes), `src/XrplFile.Core/BatchSweep.cs` (delete `HistoryAsync` and the `LedgerTransaction` `Covers` overload, now unused)
- Test: `tests/XrplFile.Core.Tests/StreamingReconciliationTests.cs`, `tests/XrplFile.Core.Tests/SequenceRangesTests.cs`, `tests/XrplFile.Core.Tests/FileTreeTests.cs` (one test)

**Interfaces:**
- Consumes: Task 3 (`_file`, `_blockHashes`, the resume structure, `preparation`), Task 5 (`conflictsFail`, `RecordFailure`).
- Produces:
  - `internal sealed class SequenceRanges` — `void Add(LedgerTransaction tx)`, `void Add(uint start, uint end)` (half-open), `bool Covers(uint from, uint to)`, `uint? Lowest`.
  - `internal static bool FileTree.TryDecodeFragment(string payload, [NotNullWhen(true)] out IReadOnlyList<BlockEntry>? entries)` — one list memo's entries, without the whole-list checks.
  - In `PublicationSession` (private): `StreamReconcileAsync(long to, bool conflictsFail, IProgress<PublishProgress>? preparation, CancellationToken)` → `Reconciled(LandedMemos Landed, SequenceRanges Sequences)`; `CompleteReconcileAsync(IProgress<PublishProgress>? preparation, CancellationToken)` → `(LandedMemos Landed, long To)`; `FindManifestAsync(LandedMemos landed, long to, CancellationToken)` → `PublishResult?`.
  - Preparation stage `"history"`: `PublishProgress("history", recordsRead, 0)`, reported when a pass starts and every 1,000 records.

**The rules (spec S20), as implemented:**
- The writer's history is streamed page by page from `StartedInLedger` to a pinned ledger `to`; nothing but the compact landing, the kept lists and the sequence ranges outlives a record.
- Chunk records: compared with the source and adopted first-copy-in-canonical-order (Task 3); a conflict on a Batch resume raises S17 (Task 5).
- List records: kept only when the index is below this file's list count — `PublicationEstimator.BlockListMemoCount(blocks)`, the packer's count at nine-digit ledgers, which bounds the real one (Spec question 7) — and every entry of the decoded memo names one of this file's blocks with that block's hash; the last such copy per index wins.
- Manifest records: not kept. Once the landing is complete and every list matches the payload the writer would send, a second streaming pass over the same `[StartedInLedger, to]` compares every manifest-marker record, validated and successful, of the writer, against the exact expected bytes, and stops at the first match (B27).
- Coverage (Batch resume, B30): sorted sequence ranges; a stream that fails part-way, or a complete one that does not cover `[StartSequence, through)`, is discarded and read again from the start, every `FencePoll`, for up to `HistoryLagLimit`.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/SequenceRangesTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class SequenceRangesTests
{
    private static LedgerTransaction Tx(uint sequence, string type = "AccountSet", uint? tickets = null) =>
        new("AB", 1, "rW", true, true, "tesSUCCESS", null, null, null) { Sequence = sequence, TransactionType = type, TicketCount = tickets };

    [Fact]
    public void Records_out_of_order_merge_into_one_run()
    {
        SequenceRanges ranges = new();
        foreach (uint sequence in new uint[] { 12, 10, 11, 14, 13 }) ranges.Add(Tx(sequence));

        Assert.True(ranges.Covers(10, 15));
        Assert.False(ranges.Covers(9, 15));
        Assert.False(ranges.Covers(10, 16));
        Assert.Equal(10u, ranges.Lowest);
    }

    [Fact]
    public void A_ticket_create_covers_the_sequences_its_tickets_took_and_a_ticketed_record_covers_nothing()
    {
        SequenceRanges ranges = new();
        ranges.Add(Tx(20, "TicketCreate", tickets: 3));
        ranges.Add(Tx(0));
        ranges.Add(Tx(24));

        Assert.True(ranges.Covers(20, 25));
        Assert.True(ranges.Covers(7, 7));
        Assert.False(ranges.Covers(19, 21));
    }
}
```

In `tests/XrplFile.Core.Tests/FileTreeTests.cs`, add:

```csharp
    [Fact]
    public void One_list_memo_decodes_on_its_own_and_garbage_does_not()
    {
        List<BlockEntry> entries =
            [.. Enumerable.Range(0, 30).Select(i => new BlockEntry(i, Convert.ToHexString(MerkleTree.Leaf([(byte)i])), 106497159 + i))];
        IReadOnlyList<string> payloads = FileTree.EncodeEntries(entries);
        Assert.True(payloads.Count > 1);

        Assert.True(FileTree.TryDecodeFragment(payloads[^1], out IReadOnlyList<BlockEntry>? fragment));
        Assert.Equal(entries.Skip(entries.Count - fragment.Count).Take(fragment.Count), fragment);
        Assert.False(FileTree.TryDecodeFragment("not json", out _));
        Assert.False(FileTree.TryDecodeFragment("[{\"i\":0}]", out _));
        Assert.False(FileTree.TryDecodeFragment("[]", out _));
    }
```

A list memo carries 982 bytes of JSON and an entry here takes about 95, so thirty entries fill three memos.

`tests/XrplFile.Core.Tests/StreamingReconciliationTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class StreamingReconciliationTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-s20-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100, FencePoll = TimeSpan.FromMilliseconds(1) };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    private static void ForgetTheEnding(string statePath)
    {
        PublicationState state = PublicationState.Load(statePath);
        (state with { Phase = "manifest", ManifestHash = null }).Save(statePath);
    }

    private static void Plant(FakeLedgerSource ledger, string hash, string marker, string data) =>
        ledger.Add(new LedgerTransaction(hash, ledger.CurrentLedger, Wallet.ClassicAddress, true, true, "tesSUCCESS", marker, data, MemoLayout.JsonFormatHex)
        {
            TransactionType = "AccountSet", Sequence = 0, TicketSequence = 900_000, Fee = 12,
        });

    [Fact]
    public async Task A_finished_run_is_recognised_with_another_publications_lists_and_manifest_after_its_own()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        byte[] file = VectorStream.Bytes("s20", 1_100_000);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee, ValidationPoll = TimeSpan.FromMilliseconds(1) });
        PublicationSession run = await PublicationSession.StartAsync(strategy, ledger, file, "application/octet-stream", null, Wallet.ClassicAddress, "key", "testnet", Options, Token);
        PublicationReport original = await run.RunAsync(null, Token);
        ForgetTheEnding(run.StatePath);

        // Another publication by the same writer, later in the same window: a list memo of index 0
        // with other blocks' hashes, and a manifest with the same root and other bytes.
        string foreignList = FileTree.EncodeEntries([new BlockEntry(0, new string('E', 64), 5_000), new BlockEntry(1, new string('F', 64), 5_001)])[0];
        Plant(ledger, new string('1', 64), MemoLayout.ListMarkerHex(0), MemoLayout.Utf8Hex(foreignList));
        Manifest foreign = original.Manifest with { FirstLedger = 1, LastLedger = 2 };
        Plant(ledger, new string('2', 64), MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(foreign.Encode()));
        int submitted = sink.Submitted.Count;
        int reads = ledger.AccountTransactionsCalls;
        List<PublishProgress> preparation = [];

        await using MemoryPublicationSource source = new(file);
        PublicationSession resumed = await PublicationSession.ResumeAsync(
            strategy, ledger, source, run.StatePath, Options, new SynchronousProgress<PublishProgress>(preparation.Add), Token);
        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(original.Link, report.Link);
        Assert.Equal(submitted, sink.Submitted.Count);
        Assert.Equal(reads + 2, ledger.AccountTransactionsCalls);
        Assert.Contains(preparation, progress => progress.Stage == "history");
    }

    [Fact]
    public async Task A_Batch_resume_whose_history_stream_breaks_part_way_starts_it_again_and_pays_for_nothing_twice()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        BatchStrategyFactory factory = (budget, fence) => new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);
        byte[] file = VectorStream.Bytes("s20-batch", 40_000);
        PublicationSession first = await PublicationSession.StartAsync(
            factory, node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options, Token);
        using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            node.Sink.Transmitting = transmission =>
            {
                if (transmission.Ordinal >= 2) stop.Cancel();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
            node.Sink.Transmitting = null;
        }

        // The break fires only in a walk of more than five records: make sure the first one is.
        long startedIn = first.State.StartedInLedger;
        int writerRecords = 0;
        await foreach (LedgerTransaction _ in node.Ledger.AccountTransactionsAsync(node.Writer.Account, startedIn, node.Ledger.CurrentLedger, true, Token))
        {
            writerRecords++;
        }

        Assert.True(writerRecords > 5, $"the interrupted run left {writerRecords} writer records; the break needs more than five");
        int callsBefore = node.Ledger.Calls.Count;
        node.Ledger.HistoryFailsAfter = 5;

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            factory, node.Ledger, file, first.StatePath, node.Payer.Account, "key-p", Options, null, Token);

        // Broken once, read again from the start: two walks of the writer from the run's first ledger.
        Assert.True(node.Ledger.Calls.Skip(callsBefore).Count(call => call.Account == node.Writer.Account && call.FromLedger == startedIn) >= 2);

        PublicationReport report = await resumed.RunAsync(null, Token);

        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
        Assert.All((await node.LandedByMarkerAsync()).Where(pair => MemoLayout.ChunkIndexOf(pair.Key) is not null), pair => Assert.Single(pair.Value));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.StreamingReconciliationTests -class XrplFile.Tests.SequenceRangesTests -method "XrplFile.Tests.FileTreeTests.One_list_memo_decodes_on_its_own_and_garbage_does_not"`
Expected: build error for `SequenceRanges` and `TryDecodeFragment`; once those exist, `A_finished_run_is_recognised...` fails on the foreign list (the old rule keeps the last copy) and on the read count.

- [ ] **Step 3: `SequenceRanges` and `TryDecodeFragment`**

`src/XrplFile.Core/SequenceRanges.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// The sequences an account's records took, as sorted, merged half-open ranges (spec S20, B30). A
/// run's own sequences are contiguous, so a resume of a large run holds a handful of ranges where a
/// set of every sequence held one entry per transaction.
/// </summary>
internal sealed class SequenceRanges
{
    private readonly List<(uint Start, uint End)> _ranges = [];

    /// <summary>The lowest sequence any record took, or null when none took one.</summary>
    public uint? Lowest => _ranges.Count == 0 ? null : _ranges[0].Start;

    /// <summary>A record with a non-zero sequence takes it; a TicketCreate also takes the sequences of its tickets; a ticketed record takes none.</summary>
    public void Add(LedgerTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);

        if (tx.Sequence == 0) return;

        uint tickets = string.Equals(tx.TransactionType, "TicketCreate", StringComparison.Ordinal) && tx.TicketCount is { } count ? count : 0;
        Add(tx.Sequence, checked(tx.Sequence + 1 + tickets));
    }

    /// <summary>Adds [<paramref name="start"/>, <paramref name="end"/>), merging with every range it touches.</summary>
    public void Add(uint start, uint end)
    {
        if (end <= start) return;

        int at = _ranges.FindIndex(range => range.End >= start);
        if (at < 0)
        {
            _ranges.Add((start, end));
            return;
        }

        uint mergedStart = Math.Min(start, _ranges[at].Start);
        uint mergedEnd = end;
        int last = at;

        while (last < _ranges.Count && _ranges[last].Start <= end)
        {
            mergedEnd = Math.Max(mergedEnd, _ranges[last].End);
            last++;
        }

        if (last == at)
        {
            _ranges.Insert(at, (start, end));
            return;
        }

        _ranges.RemoveRange(at, last - at);
        _ranges.Insert(at, (mergedStart, mergedEnd));
    }

    /// <summary>Whether every sequence in [<paramref name="from"/>, <paramref name="to"/>) was taken.</summary>
    public bool Covers(uint from, uint to)
    {
        if (to <= from) return true;

        foreach ((uint start, uint end) in _ranges)
        {
            if (start <= from && end >= to) return true;
        }

        return false;
    }
}
```

The search for the first touching range is linear; ranges stay few because a run's own sequences are contiguous. In `FileTree`, add (with `using System.Diagnostics.CodeAnalysis;`):

```csharp
    /// <summary>
    /// One block-list memo's entries, or false when it is not a JSON array of well-formed entries
    /// (spec S20). Unlike <see cref="DecodeEntries"/> it does not ask for the whole list: a resume
    /// reads lists one memo at a time and keeps only those that name this file's blocks.
    /// </summary>
    internal static bool TryDecodeFragment(string payload, [NotNullWhen(true)] out IReadOnlyList<BlockEntry>? entries)
    {
        entries = null;
        Wire[]? wires;

        try
        {
            wires = JsonSerializer.Deserialize<Wire[]>(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (wires is null || wires.Length == 0) return false;

        List<BlockEntry> decoded = new(wires.Length);

        foreach (Wire? wire in wires)
        {
            if (wire?.i is not { } index || wire.l is not { } ledger) return false;
            if (wire.h is not { } hash || hash.Length != MerkleTree.HashBytes * 2 || !hash.All(Uri.IsHexDigit)) return false;

            decoded.Add(new BlockEntry(index, hash.ToUpperInvariant(), ledger));
        }

        entries = decoded;
        return true;
    }
```

- [ ] **Step 4: The streaming reconciliation in the session**

In `PublicationSession`, add a field filled in the constructor from the block hashes:

```csharp
    /// <summary>This file's block hashes as list entries spell them, for the list filter of spec S20.</summary>
    private readonly IReadOnlyList<string> _blockHashesHex;
```

(`_blockHashesHex = [.. blockHashes.Select(Convert.ToHexString)];`)

Replace `ReconcileAsync`, `HistoryAsync`, `CompleteHistoryAsync` and the list-based `FindManifest` with:

```csharp
    /// <summary>What one streaming pass found: the landing and the kept lists, and the sequences the writer's records took.</summary>
    private sealed record Reconciled(LandedMemos Landed, SequenceRanges Sequences);

    /// <summary>How often the "history" stage is reported while a pass streams.</summary>
    private const int HistoryReportEvery = 1_000;

    /// <summary>
    /// One streaming pass over the writer's history from <see cref="PublicationState.StartedInLedger"/>
    /// to <paramref name="to"/> (spec S20): nothing outlives a record but the compact landing, the
    /// lists that carry this file's block hashes, and the sequence ranges.
    /// </summary>
    private async Task<Reconciled> StreamReconcileAsync(
        long to, bool conflictsFail, IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        string writer = _state.Writer;
        long chunkCount = ChunkReads.ChunkCount(_file.Length);
        int listCount = _blockHashes.Count > 1 ? PublicationEstimator.BlockListMemoCount(_blockHashes.Count) : 0;
        ChunkLanding landing = new(chunkCount);
        Dictionary<int, LandedMemo> lists = [];
        SequenceRanges sequences = new();
        byte[] buffer = new byte[MemoLayout.ChunkBytes];
        long read = 0;

        preparation?.Report(new PublishProgress("history", 0, 0));

        await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(writer, _state.StartedInLedger, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            if (++read % HistoryReportEvery == 0) preparation?.Report(new PublishProgress("history", read, 0));

            // account_tx also returns what others sent to the writer, which is not "of" it.
            if (!string.Equals(tx.Account, writer, StringComparison.Ordinal)) continue;

            sequences.Add(tx);

            if (!tx.Succeeded || !tx.Validated || tx.MemoDataHex is not { } data) continue;

            if (MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } chunkIndex)
            {
                if (chunkIndex >= chunkCount) continue;
                if (landing.IsLanded(chunkIndex) && !conflictsFail) continue;

                int length = await _file.ReadChunkAsync(chunkIndex, buffer, cancellationToken).ConfigureAwait(false);

                if (ChunkReads.MatchesHex(data, buffer.AsSpan(0, length)))
                {
                    landing.Land(chunkIndex, tx.LedgerIndex);
                }
                else if (conflictsFail)
                {
                    RecordFailure(PublicationState.WriterWindowConflictFailure);
                    throw new XrplFileException(XrplFileError.WriterWindowConflict,
                        $"the writer's {tx.Hash} in ledger {tx.LedgerIndex} carries chunk {chunkIndex} with other data than this file's");
                }

                continue;
            }

            // Last copy per index wins, as the reader resolves a list; only one that names this
            // file's blocks is this file's, so another publication's lists are not kept.
            if (MemoLayout.ListIndexOf(tx.MemoTypeHex) is { } listIndex && listIndex < listCount && CarriesThisFilesBlocks(data))
            {
                lists[(int)listIndex] = new LandedMemo(tx.Hash, tx.LedgerIndex, data);
            }
        }

        return new Reconciled(new LandedMemos(landing, lists), sequences);
    }

    private bool CarriesThisFilesBlocks(string payloadHex)
    {
        string payload;

        try
        {
            payload = MemoLayout.FromHexUtf8(payloadHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return FileTree.TryDecodeFragment(payload, out IReadOnlyList<BlockEntry>? entries)
            && entries.All(entry => entry.Index >= 0 && entry.Index < _blockHashesHex.Count
                && string.Equals(entry.HashHex, _blockHashesHex[entry.Index], StringComparison.Ordinal));
    }

    /// <summary>
    /// A Batch resume's reconciliation (spec 5.2 step 3, B30, S20): a snapshot after the fence pins
    /// the range, and a pass counts only when the writer's records cover its sequences from
    /// <see cref="PublicationState.StartSequence"/> up to the snapshot's. A pass that breaks part-way,
    /// or that does not cover them, is discarded and taken again from the start every
    /// <see cref="SessionOptions.FencePoll"/>, for up to <see cref="SessionOptions.HistoryLagLimit"/>.
    /// </summary>
    private async Task<(LandedMemos Landed, long To)> CompleteReconcileAsync(IProgress<PublishProgress>? preparation, CancellationToken cancellationToken)
    {
        string writer = _state.Writer;
        BatchStrategyOptions reads = new() { ChainCheck = _options.FencePoll, NodeOutageLimit = _options.NodeOutageLimit };
        LedgerSnapshot snapshot = await BatchReads.RetryAsync(
            token => _source.SnapshotAsync([writer], token), reads, cancellationToken).ConfigureAwait(false);
        uint through = snapshot.SequenceOf(writer);
        DateTimeOffset giveUp = DateTimeOffset.UtcNow + _options.HistoryLagLimit;

        while (true)
        {
            Reconciled pass = await BatchReads.RetryAsync(
                token => StreamReconcileAsync(snapshot.LedgerIndex, conflictsFail: true, preparation, token), reads, cancellationToken).ConfigureAwait(false);

            // A state written before StartSequence was recorded has only its own records to start from.
            uint from = _state.StartSequence ?? pass.Sequences.Lowest ?? through;

            if (pass.Sequences.Covers(from, through)) return (pass.Landed, snapshot.LedgerIndex);

            if (DateTimeOffset.UtcNow >= giveUp)
            {
                throw new XrplFileException(XrplFileError.NodeLacksHistory,
                    $"account_tx did not cover the writer's sequences from {from} up to {through} by ledger {snapshot.LedgerIndex} "
                    + $"within {_options.HistoryLagLimit}");
            }

            await Task.Delay(_options.FencePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The manifest this run would send, if the ledger already holds it (spec B27, S20): built from the
    /// reconciled landing and lists, then matched in a second streaming pass over the same range
    /// against those exact bytes. The first validated, successful match of the writer is the one.
    /// </summary>
    private async Task<PublishResult?> FindManifestAsync(LandedMemos landed, long to, CancellationToken cancellationToken)
    {
        if (!landed.Chunks.IsComplete) return null;

        IReadOnlyList<BlockEntry> entries = ManifestPlan.Entries(_blockHashes, landed.Chunks.BlockFirstLedgers());
        IReadOnlyList<MemoSubmission> listMemos = ManifestPlan.ListMemos(entries);
        LedgerRange window = landed.Chunks.Window();
        long lastLedger = window.Last;

        for (int j = 0; j < listMemos.Count; j++)
        {
            if (!landed.Lists.TryGetValue(j, out LandedMemo? list)
                || !string.Equals(list.MemoDataHex, listMemos[j].MemoDataHex, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            lastLedger = Math.Max(lastLedger, list.LedgerIndex);
        }

        PublicationState state = State;
        Manifest manifest = ManifestPlan.Build(state.Type, state.Name, state.Size, entries, window.First, lastLedger, listMemos.Count);
        string data = ManifestPlan.Memo(manifest).MemoDataHex;

        await foreach (LedgerTransaction tx in _source.AccountTransactionsAsync(state.Writer, state.StartedInLedger, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            if (!tx.Validated || !tx.Succeeded || !string.Equals(tx.Account, state.Writer, StringComparison.Ordinal)) continue;
            if (!MemoLayout.IsManifest(tx.MemoTypeHex) || !string.Equals(tx.MemoDataHex, data, StringComparison.OrdinalIgnoreCase)) continue;

            return new PublishResult(new XrplFileLink(state.Network, tx.Hash), manifest, state.Writer, landed.Chunks.ChunkCount, listMemos.Count, tx.Hash);
        }

        return null;
    }
```

- [ ] **Step 5: The two resumes use them**

One-account `ResumeAsync` (source overload), after `session.RecordSourcePath(...)`:

```csharp
        long to = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);
        session._landed = (await session.StreamReconcileAsync(to, conflictsFail: false, preparation, cancellationToken).ConfigureAwait(false)).Landed;

        // Nothing is sent, so nothing has to be affordable (spec B27).
        if (await session.FindManifestAsync(session._landed, to, cancellationToken).ConfigureAwait(false) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }
```

Batch `ResumeAsync` (source overload), after `session.RecordSourcePath(...)`:

```csharp
        (LandedMemos landed, long to) = await session.CompleteReconcileAsync(preparation, cancellationToken).ConfigureAwait(false);
        session._landed = landed;

        if (await session.FindManifestAsync(landed, to, cancellationToken).ConfigureAwait(false) is { } done)
        {
            session._alreadyPublished = done;
            return session;
        }
```

Delete `BatchSweep.HistoryAsync` and the `Covers(IEnumerable<LedgerTransaction>, ...)` overload (nothing calls them now: Task 4 moved the tests that did onto `SweepRecord`); keep `CoversCore` and the `SweepRecord` overload. The `SweepRecord` overload's `<inheritdoc cref="Covers(IEnumerable{LedgerTransaction}, uint, uint)"/>` would no longer resolve (CS1574 is an error here), so give it the deleted overload's summary as its own `<summary>`.

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.StreamingReconciliationTests -class XrplFile.Tests.SequenceRangesTests -class XrplFile.Tests.FileTreeTests`
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS, including `ManifestReconciliationTests`, `PublicationSessionBatchResumeTests` and `WindowConflictTests`.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): resumes reconcile by streaming the writer's history

Reconciliation now streams account_tx page by page and keeps only the compact
landing, sorted sequence ranges, and list memos whose entries name this file's
block hashes. The manifest is matched in a second streaming pass against the
exact bytes the writer would send, so another publication's lists and
manifests in the window can neither replace this run's nor be adopted. A pass
that breaks or does not cover the writer's sequences is read again."
```

---

### Task 7: Verification — deterministic reachability, the seeded sample, the landed manifest (S9, S21, S22)

**Files:**
- Create: `src/XrplFile.Core/PublicationCheck.cs`
- Modify: `src/XrplFile.Core/XrplFileException.cs` (+ `ManifestRejected`, + `Link`), `src/XrplFile.Core/XrplFileWriter.cs` (verification before the manifest, the check after it), `src/XrplFile.Core/PublicationState.cs` (+ `ManifestStatus`), `src/XrplFile.Core/PublicationSession.cs` (`RunAsync`, both resumes, `RequireResumableAsync`), `app/XrplFile.App.Core/PublishErrorMessages.cs`, `app/XrplFile.App.Core/ReadErrorMessages.cs` (the publishing-only arm), `app/XrplFile.App.Core/RunModel.cs` (`IsResumable`)
- Test: `tests/XrplFile.Core.Tests/PublicationCheckTests.cs`, `tests/XrplFile.Core.Tests/SampledReadBackTests.cs`, `tests/XrplFile.Core.Tests/ManifestAcceptanceTests.cs`, `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs` (one test), `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs` (one value added)

**Interfaces:**
- Consumes: Task 2 (`ChunkLanding`), Task 3 (`RequireSameAsSourceAsync`, `ReadEverythingBackAsync`, the generic `ReadBackAsync<T>` retry), Task 5 (`ReadErrorMessagesTests.The_streaming_publication_errors_share_the_publishing_only_sentence`), Task 6 (`FindManifestAsync`, `FileTree.TryDecodeFragment`).
- Produces:
  - `XrplFileError.ManifestRejected`; `XrplFileException.Link : XrplFileLink?` and a `link` constructor parameter (last, optional).
  - `PublicationState.ManifestStatus : string?` (JSON `ManifestStatus`, written only when set) and `public const string ManifestLandedRejected = "landed, rejected"`.
  - `internal static class PublicationCheck` — `void RequireReachable(IReadOnlyList<BlockEntry> plan, ChunkLanding landing, Manifest manifest)`; `void RequireListed(IReadOnlyList<BlockEntry> plan, IReadOnlyList<BlockEntry> published)`; `IReadOnlyList<int> SampleBlocks(string rootHex, int blocks)`; `Task RequireAcceptedAsync(ILedgerSource source, XrplFileLink link, Manifest planned, string writer, CancellationToken cancellationToken)`.
  - The writer's `"read-back"` stage counts blocks read: Total = the sample's size on the Batch path, the block count otherwise (spec 4.3).

**Verification, as implemented:**
- **S9(a), every path, no reads:** for every block, the plan's `FirstLedger` equals the block's first adopted ledger; every block's adopted ledgers lie inside `[manifest.FirstLedger, manifest.LastLedger]`.
- **List equals plan:** compared with the block list *as read from the ledger* by the read-back of S9(b) (`BlockListAsync`, rooted to the manifest root), ledgers included, on both paths. The list the writer built is derived from the plan, so comparing it with the plan would compare the run's data with itself; the list on the ledger is the one a reader uses, and the root does not cover ledgers, so only this comparison can fail. A single-block file has no list memo; the reader's "list" is then the manifest's own entry, which must equal the plan's one entry.
- **S9(b), Batch:** the full block list through `BlockListAsync` (which roots it to the manifest root), then the blocks of S21 through `BlockAsync`, each compared with the source. **Ticket and sequence:** every block, as Task 3 left it.
- **S21:** one block → `[0]`; up to four → all; otherwise `[0, n−1, r₁, r₂]` with rᵢ = 1 + (v mod (n−2)), v the first 8 bytes big-endian of SHA-256(root ‖ counter as 4 bytes big-endian), counter from 0, a duplicate skipped.
- **S22, every path:** after the manifest lands, `ManifestAsync` on the real link; accepted when it reads, is of the writer, lies in a ledger after `LastLedger` (the reader's own rule) and encodes to the plan's exact JSON. `CorruptManifest` or a mismatch is a rejection: the state records `ManifestStatus = "landed, rejected"` and the hash, and the error carries the link. `NodeUnreachable` is retried three times like the read-back; `ManifestNotFound` and `NodeLacksHistory` (what the reader throws when `tx` misses on a node whose history starts late, as many public testnet nodes and load-balanced clusters do) three times, then `ValidationTimeout` (a transient lag must not become a permanent rejection). A manifest a resume recovers (B27) passes the same check before the run is called done. A state that records a rejection is refused by every resume with `ManifestRejected`, before anything is read.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/PublicationCheckTests.cs`:

```csharp
using Xunit;

namespace XrplFile.Tests;

public class PublicationCheckTests
{
    private const string Root = "6EDDD4789DCA250C1AE69229121C6CB3EE0190E82A9BEF6F797F728EB8D3B7C8";

    [Theory]
    [InlineData(1, new[] { 0 })]
    [InlineData(2, new[] { 0, 1 })]
    [InlineData(3, new[] { 0, 1, 2 })]
    [InlineData(4, new[] { 0, 1, 2, 3 })]
    public void Up_to_four_blocks_every_block_is_checked(int blocks, int[] expected)
    {
        Assert.Equal(expected, PublicationCheck.SampleBlocks(Root, blocks));
    }

    [Theory]
    [InlineData(Root, 1_651, new[] { 0, 1650, 1245, 1637 })]
    [InlineData(Root, 5, new[] { 0, 4, 3, 1 })]
    [InlineData(Root, 6, new[] { 0, 5, 4, 2 })]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", 1_651, new[] { 0, 1650, 1574, 1540 })]
    [InlineData("E52D9C508C502347344D8C07AD91CBD6068AFC75FF6292F062A09CA381C89E71", 5, new[] { 0, 4, 2, 3 })]
    public void The_sample_is_seeded_by_the_root(string root, int blocks, int[] expected)
    {
        // The last root draws block 2 at counters 0 and 1: the duplicate is skipped and counter 2 gives block 3.
        Assert.Equal(expected, PublicationCheck.SampleBlocks(root, blocks));
    }

    private static (List<BlockEntry> Plan, ChunkLanding Landing, Manifest Manifest) Published()
    {
        ChunkLanding landing = new(2_048);
        for (long chunk = 0; chunk < 2_048; chunk++) landing.Land(chunk, chunk < 1_024 ? 100 + chunk % 2 : 102);

        List<BlockEntry> plan = [new BlockEntry(0, new string('A', 64), 100), new BlockEntry(1, new string('B', 64), 102)];
        Manifest manifest = new("application/octet-stream", 2_048_000, 1_000, Root, 100, 103, 2, 1, null);
        return (plan, landing, manifest);
    }

    [Fact]
    public void A_published_plan_is_reachable()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();

        PublicationCheck.RequireReachable(plan, landing, manifest);
        PublicationCheck.RequireListed(plan, [.. plan.Select(entry => entry with { Hash = entry.Hash.ToLowerInvariant() })]);
    }

    [Fact]
    public void A_block_whose_first_ledger_is_after_one_of_its_chunks_fails_before_any_read()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();
        List<BlockEntry> late = [plan[0] with { FirstLedger = 101 }, plan[1]];

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationCheck.RequireReachable(late, landing, manifest));

        Assert.Equal(XrplFileError.BlockMismatch, error.Error);
        Assert.Equal(0, error.Block);
    }

    [Fact]
    public void A_chunk_outside_the_window_fails()
    {
        (List<BlockEntry> plan, ChunkLanding landing, Manifest manifest) = Published();

        XrplFileException error = Assert.Throws<XrplFileException>(() =>
            PublicationCheck.RequireReachable(plan, landing, manifest with { LastLedger = 101 }));

        Assert.Equal(1, error.Block);
    }

    [Fact]
    public void A_list_on_the_ledger_that_is_not_the_plan_fails()
    {
        (List<BlockEntry> plan, _, _) = Published();
        List<BlockEntry> listed = [plan[0], plan[1] with { FirstLedger = 103 }];

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationCheck.RequireListed(plan, listed));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
    }
}
```

`tests/XrplFile.Core.Tests/SampledReadBackTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xunit;

namespace XrplFile.Tests;

public class SampledReadBackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<List<PublishProgress>> PublishAsync(long bytes)
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();
        List<PublishProgress> reports = [];
        await using VectorPublicationSource file = new("sampled", bytes);

        PublishResult result = await new XrplFileWriter(strategy, node.Ledger).PublishAsync(
            file, "application/octet-stream", null, node.Writer.Account, "testnet", null,
            new SynchronousProgress<PublishProgress>(reports.Add), Token);

        Assert.Equal(PublicationSession.RootOf(VectorStream.Bytes("sampled", bytes)), result.Manifest.Root);
        return reports;
    }

    [Fact]
    public async Task A_Batch_file_of_five_blocks_reads_back_four_sampled_blocks()
    {
        List<PublishProgress> reports = await PublishAsync(5 * 1_024_000);

        Assert.Equal(
            [new PublishProgress("read-back", 1, 4), new PublishProgress("read-back", 2, 4), new PublishProgress("read-back", 3, 4), new PublishProgress("read-back", 4, 4)],
            reports.Where(progress => progress.Stage == "read-back"));
    }

    [Fact]
    public async Task A_Batch_file_of_three_blocks_reads_back_every_block()
    {
        List<PublishProgress> reports = await PublishAsync(2 * 1_024_000 + 1);

        Assert.Equal(3, reports.Where(progress => progress.Stage == "read-back").Select(progress => progress.Total).Distinct().Single());
        Assert.Equal(3, reports.Count(progress => progress.Stage == "read-back"));
    }

    [Fact]
    public async Task A_block_list_on_the_ledger_whose_ledgers_differ_from_the_plan_fails_the_read_back()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();
        await using VectorPublicationSource file = new("sampled-list", 2 * 1_024_000);

        // The strategy writes to the honest ledger; only the writer's read-back sees the shifted list.
        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(strategy, new ListShiftingSource(node.Ledger)).PublishAsync(
            file, "application/octet-stream", null, node.Writer.Account, "testnet", null, null, Token));

        Assert.Equal(XrplFileError.ListMismatch, error.Error);
        Assert.Contains("is not the plan", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shows every block-list memo with its first entry's ledger moved one earlier. The root covers
    /// block hashes and not ledgers, so the list still roots; it is not the plan.
    /// </summary>
    private sealed class ListShiftingSource(FakeLedgerSource inner) : ILedgerSource
    {
        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (LedgerTransaction tx in inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken))
            {
                if (MemoLayout.ListIndexOf(tx.MemoTypeHex) is null || tx.MemoDataHex is null
                    || !FileTree.TryDecodeFragment(MemoLayout.FromHexUtf8(tx.MemoDataHex), out IReadOnlyList<BlockEntry>? entries))
                {
                    yield return tx;
                    continue;
                }

                List<BlockEntry> shifted = [entries[0] with { FirstLedger = entries[0].FirstLedger - 1 }, .. entries.Skip(1)];
                yield return tx with { MemoDataHex = MemoLayout.Utf8Hex(FileTree.EncodeEntries(shifted)[0]) };
            }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => inner.TransactionAsync(hash, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);
    }
}
```

`tests/XrplFile.Core.Tests/ManifestAcceptanceTests.cs`:

```csharp
using System.Runtime.CompilerServices;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class ManifestAcceptanceTests : IDisposable
{
    private const int Fee = 10;
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-s22-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options => new() { StateDirectory = _stateDirectory, TicketsPerBatch = 100 };

    private static FakeLedgerSource Node()
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(Fee, 1_000_000, 200_000) };
        source.Balances[Wallet.ClassicAddress] = 500_000_000;
        return source;
    }

    [Fact]
    public async Task A_landed_manifest_the_reader_rejects_fails_the_run_records_it_and_names_the_link()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        TamperingSource tampering = new(ledger);
        PublicationSession session = await PublicationSession.StartAsync(
            strategy, tampering, VectorStream.Bytes("s22", 2_500), "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => session.RunAsync(null, Token));

        Assert.Equal(XrplFileError.ManifestRejected, error.Error);
        Assert.NotNull(error.Link);
        PublicationState state = PublicationState.Load(session.StatePath);
        Assert.Equal(PublicationState.ManifestLandedRejected, state.ManifestStatus);
        Assert.Equal(error.Link!.TxHash, state.ManifestHash);
        Assert.NotEqual("done", state.Phase);
    }

    [Fact]
    public async Task A_resume_of_a_rejected_manifest_is_refused_without_reading_or_sending()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        byte[] file = VectorStream.Bytes("s22", 2_500);
        PublicationSession session = await PublicationSession.StartAsync(
            strategy, new TamperingSource(ledger), file, "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);
        await Assert.ThrowsAsync<XrplFileException>(() => session.RunAsync(null, Token));
        int submitted = sink.Submitted.Count;
        int reads = ledger.AccountTransactionsCalls;

        XrplFileException again = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(strategy, ledger, file, session.StatePath, Options, Token));

        Assert.Equal(XrplFileError.ManifestRejected, again.Error);
        Assert.Equal((submitted, reads), (sink.Submitted.Count, ledger.AccountTransactionsCalls));
    }

    [Fact]
    public async Task A_manifest_a_resume_recovers_is_checked_too_and_a_rejection_is_not_success()
    {
        FakeLedgerSource ledger = Node();
        FakeLedgerSink sink = new(ledger);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { FeeDrops = Fee });
        byte[] file = VectorStream.Bytes("s22-recovered", 2_500);
        PublicationSession run = await PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", "s.txt", Wallet.ClassicAddress, "key", "testnet", Options, Token);
        await run.RunAsync(null, Token);
        PublicationState finished = PublicationState.Load(run.StatePath);
        (finished with { Phase = "manifest", ManifestHash = null }).Save(run.StatePath);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() =>
            PublicationSession.ResumeAsync(strategy, new TamperingSource(ledger), file, run.StatePath, Options, Token));

        Assert.Equal(XrplFileError.ManifestRejected, error.Error);
        Assert.Equal(PublicationState.ManifestLandedRejected, PublicationState.Load(run.StatePath).ManifestStatus);
    }

    /// <summary>The node answers a tx lookup of a manifest with another file's description, as a reader would see a broken link.</summary>
    private sealed class TamperingSource(FakeLedgerSource inner) : ILedgerSource
    {
        public async Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
        {
            LedgerTransaction? tx = await inner.TransactionAsync(hash, cancellationToken);
            if (tx is null || !MemoLayout.IsManifest(tx.MemoTypeHex) || tx.MemoDataHex is null) return tx;

            Manifest real = Manifest.Decode(MemoLayout.FromHexUtf8(tx.MemoDataHex));
            return tx with { MemoDataHex = MemoLayout.Utf8Hex((real with { Name = "someone else's" }).Encode()) };
        }

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
            inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);
    }
}
```

The writer reads back through the session's source, so `StartAsync(strategy, tampering, ...)` makes the run's own reads go through `TamperingSource`; only tx lookups of manifests are changed, so chunks, lists and the read-back are honest.

In `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs`, add `XrplFileError.ManifestRejected` to the array of `The_streaming_publication_errors_share_the_publishing_only_sentence` (Task 5).

In `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`, add:

```csharp
    [Fact]
    public void A_rejected_manifest_names_its_link()
    {
        XrplFileLink link = new("testnet", new string('A', 64));
        XrplFileException thrown = new(XrplFileError.ManifestRejected, "rejected", link: link);

        Assert.Contains(link.ToString(), PublishErrorMessages.For(thrown), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationCheckTests -class XrplFile.Tests.SampledReadBackTests -class XrplFile.Tests.ManifestAcceptanceTests`
Expected: build error.

- [ ] **Step 3: The error, the link and the state field**

`XrplFileError`, after `WriterWindowConflict`:

```csharp
    /// <summary>
    /// The manifest landed and a reader does not accept it as this file's (spec S22). The link is on
    /// <see cref="XrplFileException.Link"/>; the state records it as landed and rejected, and no resume
    /// turns it into a success.
    /// </summary>
    ManifestRejected,
```

`XrplFileException`: add

```csharp
    /// <summary>For <see cref="XrplFileError.ManifestRejected"/>: the link that was written; otherwise null.</summary>
    public XrplFileLink? Link { get; }
```

and the constructor parameter `XrplFileLink? link = null` after `role`, assigning `Link = link;`.

`PublicationState`: before `Failure`, add

```csharp
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManifestStatus = null,
```

documented as

```csharp
/// <param name="ManifestStatus">
/// <see cref="ManifestLandedRejected"/> when the manifest landed and a reader rejected it (spec S22);
/// <see cref="ManifestHash"/> then names it. Absent otherwise.
/// </param>
```

and

```csharp
    /// <summary>The value of <see cref="ManifestStatus"/> for a manifest that landed and that a reader does not accept (spec S22).</summary>
    public const string ManifestLandedRejected = "landed, rejected";
```

`PublishErrorMessages.For(XrplFileError)`:

```csharp
        XrplFileError.ManifestRejected =>
            "The manifest landed, but reading it back did not give this file's description, so its link must not be shared. Publish the file again from a fresh writer key.",
```

and in `For(XrplFileException exception)`, after the sentence is chosen:

```csharp
        if (exception is { Error: XrplFileError.ManifestRejected, Link: { } link }) sentence = $"{sentence} The link that was written: {link}.";
```

`ReadErrorMessages.For(XrplFileError)`: add `or XrplFileError.ManifestRejected` to the publishing-only arm; reading cannot produce it.

- [ ] **Step 4: `PublicationCheck`**

`src/XrplFile.Core/PublicationCheck.cs`:

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XrplFile;

/// <summary>
/// The writer's check of its own publication (spec S9, S21, S22). The root covers block hashes and
/// not each block's first ledger, so a list that roots correctly does not prove its blocks are
/// reachable; the deterministic checks here do, from the run's own data and without a read.
/// </summary>
internal static class PublicationCheck
{
    private const int ManifestReadAttempts = 3;

    private static readonly TimeSpan ManifestReadPause = TimeSpan.FromSeconds(1);

    /// <summary>Spec S9(a), for every block.</summary>
    public static void RequireReachable(IReadOnlyList<BlockEntry> plan, ChunkLanding landing, Manifest manifest)
    {
        for (int block = 0; block < plan.Count; block++)
        {
            if (!landing.TryBlockLedgers(block, out LedgerRange range))
            {
                throw new XrplFileException(XrplFileError.ChunkMissing, $"no chunk of block {block} is known to have landed", block);
            }

            if (plan[block].FirstLedger != range.First)
            {
                throw new XrplFileException(XrplFileError.BlockMismatch,
                    $"block {block} names ledger {plan[block].FirstLedger} as its first and its first chunk is in {range.First}", block);
            }

            if (range.First < manifest.FirstLedger || range.Last > manifest.LastLedger)
            {
                throw new XrplFileException(XrplFileError.BlockMismatch,
                    $"block {block}'s chunks lie in ledgers {range.First}..{range.Last}, outside the window {manifest.FirstLedger}..{manifest.LastLedger}", block);
            }
        }

    }

    /// <summary>
    /// The block list as read from the ledger equals the plan, ledgers included (spec S9). The root
    /// covers block hashes only, so a list that roots correctly can still send a reader to the wrong
    /// ledger; the list the writer built cannot show that, because it was built from the plan.
    /// </summary>
    public static void RequireListed(IReadOnlyList<BlockEntry> plan, IReadOnlyList<BlockEntry> published)
    {
        bool same = plan.Count == published.Count
            && plan.Zip(published).All(pair => pair.First.Index == pair.Second.Index
                && pair.First.FirstLedger == pair.Second.FirstLedger
                && string.Equals(pair.First.Hash, pair.Second.Hash, StringComparison.OrdinalIgnoreCase));

        if (!same)
        {
            throw new XrplFileException(XrplFileError.ListMismatch, "the block list on the ledger is not the plan");
        }
    }

    /// <summary>
    /// Spec S21. The sample is the writer's check of its own publication, not a proof for third
    /// parties: a seed from the root is deterministic and recomputable and needs no extra read.
    /// </summary>
    public static IReadOnlyList<int> SampleBlocks(string rootHex, int blocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, 1);

        if (blocks <= 4) return [.. Enumerable.Range(0, blocks)];

        byte[] input = new byte[MerkleTree.HashBytes + 4];
        Convert.FromHexString(rootHex).CopyTo(input, 0);
        List<int> sample = [0, blocks - 1];

        for (uint counter = 0; sample.Count < 4; counter++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(MerkleTree.HashBytes), counter);
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(input));
            int block = 1 + (int)(value % (ulong)(blocks - 2));

            if (!sample.Contains(block)) sample.Add(block);
        }

        return sample;
    }

    /// <summary>
    /// Spec S22: the link as a public reader takes it. Only the real transaction proves a reader
    /// accepts the link, and a manifest a resume recovered deserves the same proof.
    /// </summary>
    public static async Task RequireAcceptedAsync(
        ILedgerSource source, XrplFileLink link, Manifest planned, string writer, CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(source);
        ManifestRecord record;

        try
        {
            record = await ReadAsync(reader, link, cancellationToken).ConfigureAwait(false);
        }
        catch (XrplFileException e) when (e.Error == XrplFileError.CorruptManifest)
        {
            throw Rejected(link, e.Message, e);
        }

        if (!string.Equals(record.Writer, writer, StringComparison.Ordinal))
        {
            throw Rejected(link, $"it was written by {record.Writer}, not by {writer}");
        }

        if (!string.Equals(record.Manifest.Encode(), planned.Encode(), StringComparison.Ordinal))
        {
            throw Rejected(link, "it describes another file than the one written");
        }
    }

    /// <summary>
    /// A node that did not answer, or does not show the transaction yet, is asked again: neither is a
    /// rejection, which is permanent. A node whose history starts late answers a missing <c>tx</c>
    /// with <see cref="XrplFileError.NodeLacksHistory"/>; a manifest that just landed is missing in
    /// the same way. Still missing after that, the run stops resumable.
    /// </summary>
    private static async Task<ManifestRecord> ReadAsync(XrplFileReader reader, XrplFileLink link, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await reader.ManifestAsync(link, cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error is XrplFileError.NodeUnreachable or XrplFileError.ManifestNotFound or XrplFileError.NodeLacksHistory
                && attempt < ManifestReadAttempts)
            {
                await Task.Delay(ManifestReadPause, cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error is XrplFileError.ManifestNotFound or XrplFileError.NodeLacksHistory)
            {
                throw new XrplFileException(XrplFileError.ValidationTimeout,
                    $"the manifest {link.TxHash} landed and the node does not show it to a reader yet: {e.Message}", inner: e);
            }
        }
    }

    private static XrplFileException Rejected(XrplFileLink link, string why, Exception? inner = null) =>
        new(XrplFileError.ManifestRejected, $"the manifest {link.TxHash} landed and a reader does not accept it: {why}", inner: inner, link: link);
}
```

- [ ] **Step 5: The writer verifies before the manifest and checks it after**

In `XrplFileWriter.PublishCoreAsync`, replace the block from `Manifest manifest = ManifestPlan.Build(...)` to the `return` with:

```csharp
        Manifest manifest = ManifestPlan.Build(type, name, file.Length, entries, window.First, lastLedger, listMemos.Count);

        PublicationCheck.RequireReachable(entries, landing, manifest);

        if (_options.ReadBack)
        {
            // Batch publications are sampled (spec S9, S21); ticket and sequence ones, at most 64 MiB
            // and with no bytes checked as their memos land, are read back in full.
            if (_strategy is BatchStrategy)
            {
                await ReadSampleBackAsync(file, entries, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReadEverythingBackAsync(file, entries, manifest, writer, lastLedger, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        SubmittedMemos manifestLanded = await _strategy.SubmitAllAsync([ManifestPlan.Memo(manifest)], null, cancellationToken).ConfigureAwait(false);

        if (!manifestLanded.TryHashOf(0, out string? manifestHash))
        {
            throw new InvalidOperationException("the strategy kept no hash for the manifest, and the link is that hash");
        }

        XrplFileLink link = new(network, manifestHash);
        await PublicationCheck.RequireAcceptedAsync(_source, link, manifest, writer, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PublishProgress("manifest", 1, 1));

        return new PublishResult(link, manifest, writer, chunkCount, listMemos.Count, manifestHash);
```

`ReadEverythingBackAsync` (Task 3) gains the parameter `IReadOnlyList<BlockEntry> plan` after `file`, and right after it reads `published`: `PublicationCheck.RequireListed(plan, published);`. Add beside it:

```csharp
    /// <summary>The full block list rooted to the manifest root and compared with the plan, then the blocks of spec S21, each compared with the source.</summary>
    private async Task ReadSampleBackAsync(
        IPublicationSource file, IReadOnlyList<BlockEntry> plan, Manifest manifest, string writer, long lastLedger,
        IProgress<PublishProgress>? progress, CancellationToken cancellationToken)
    {
        XrplFileReader reader = new(_source, new ReaderOptions { MaxBytes = long.MaxValue });
        ManifestRecord unpublished = new(manifest, writer, lastLedger + 1, string.Empty);
        IReadOnlyList<BlockEntry> published = await ReadBackAsync(
            () => reader.BlockListAsync(unpublished, cancellationToken), cancellationToken).ConfigureAwait(false);
        PublicationCheck.RequireListed(plan, published);
        IReadOnlyList<int> sample = PublicationCheck.SampleBlocks(manifest.Root, published.Count);

        for (int k = 0; k < sample.Count; k++)
        {
            BlockEntry entry = published[sample[k]];
            byte[] content = await ReadBackAsync(
                () => reader.BlockAsync(unpublished, entry, cancellationToken), cancellationToken).ConfigureAwait(false);

            await RequireSameAsSourceAsync(file, entry.Index, content, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PublishProgress("read-back", k + 1, sample.Count));
        }
    }
```

- [ ] **Step 6: The session records a rejection and refuses to resume it**

In `PublicationSession`:

1. In `RunAsync`, wrap the writer call so a rejection reaches the state before it reaches the caller:

```csharp
            PublishResult result;

            try
            {
                result = await writer.PublishAsync(
                    _file, _blockHashes, started.Type, started.Name, started.Writer, started.Network, _landed,
                    new PhaseRecordingProgress(this, progress), cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e is { Error: XrplFileError.ManifestRejected, Link: { } rejected })
            {
                RecordRejectedManifest(rejected.TxHash);
                throw;
            }
```

2. Add:

```csharp
    /// <summary>A landed, rejected manifest goes to the state before the error does (spec S22), so no resume can call the run done.</summary>
    private void RecordRejectedManifest(string manifestHash)
    {
        lock (_stateLock)
        {
            _state = _state with { ManifestStatus = PublicationState.ManifestLandedRejected, ManifestHash = manifestHash };
            _state.Save(StatePath);
        }
    }
```

3. In both resumes, where a found manifest is adopted:

```csharp
        if (await session.FindManifestAsync(landed, to, cancellationToken).ConfigureAwait(false) is { } done)
        {
            try
            {
                await PublicationCheck.RequireAcceptedAsync(source, done.Link, done.Manifest, done.Writer, cancellationToken).ConfigureAwait(false);
            }
            catch (XrplFileException e) when (e.Error == XrplFileError.ManifestRejected)
            {
                session.RecordRejectedManifest(done.ManifestHash);
                throw;
            }

            session._alreadyPublished = done;
            return session;
        }
```

  (in the one-account resume the landing variable is `session._landed`).

4. In `RequireResumableAsync`, right after the conflict refusal of Task 5:

```csharp
        if (string.Equals(state.ManifestStatus, PublicationState.ManifestLandedRejected, StringComparison.Ordinal))
        {
            XrplFileLink? link = state.ManifestHash is { } hash ? new XrplFileLink(state.Network, hash) : null;
            throw new XrplFileException(XrplFileError.ManifestRejected,
                $"{statePath} records a manifest that landed and that a reader does not accept; it is not resumed", link: link);
        }
```

5. `RunModel.IsResumable` also returns false when `state.ManifestStatus` is set: `return ... && state.Failure is null && state.ManifestStatus is null;`.

- [ ] **Step 7: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.PublicationCheckTests -class XrplFile.Tests.SampledReadBackTests -class XrplFile.Tests.ManifestAcceptanceTests`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishErrorMessagesTests -class XrplFile.App.Core.Tests.ReadErrorMessagesTests`
Run: both unit suites with `-trait- "Category=Integration"`.
Expected: PASS. Tests that count `TransactionAsync` lookups after a publication (`WriterLedgerTests`) now see one more, for the manifest: assert on chunk hashes as Task 2 changed them, which excludes it.

- [ ] **Step 8: Commit**

```bash
git add src app tests
git commit -m "feat(core): verify reachability, sample Batch read-backs, check the landed manifest

Before the manifest, every publication checks that each block's first ledger
is its earliest adopted chunk and that every chunk lies in the window, and its
read-back compares the block list on the ledger with the plan, ledgers
included. Batch publications then read a sample seeded by the root: both end
blocks and two middle ones; ticket and sequence publications keep the full
read-back. A manifest not yet visible, or hidden by a node with short history,
is read again before the run stops resumable. After the manifest lands, and when a resume recovers one, the
link is read as a stranger would read it; a rejection is recorded on the
state with the link and is never turned into success by a resume."
```

---

### Task 8: The trace, bounded at both ends (S18)

**Files:**
- Modify: `src/XrplFile.Core/BatchTrace.cs` (ring, listener, `Total`), `src/XrplFile.Core/BatchStrategyOptions.cs` (+ `TraceListener`), `src/XrplFile.Core/BatchStrategy.cs` (pass the listener)
- Create: `app/XrplFile.App.Core/RunTraceChannel.cs`
- Modify: `app/XrplFile.App.Core/RunTraceLog.cs` (build the block after taking the gate), `app/XrplFile.App.Core/PublicationRun.cs` (`RunAttempt`, `RunBatchAsync`'s `Build`, `WriteTraceAsync`)
- Test: `tests/XrplFile.Core.Tests/BatchTraceTests.cs` (two tests), `tests/XrplFile.App.Core.Tests/RunTraceChannelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `BatchTrace.Capacity = 10_000` (internal const); `BatchTrace(Action<BatchTraceEntry>? listener = null)`; `long Total` (entries ever added). `Entries` and `Events` return the ring's contents, oldest first.
  - `BatchStrategyOptions.TraceListener : Action<BatchTraceEntry>?` — called for every entry in order, under the trace's lock; it must not block.
  - `public sealed class RunTraceChannel` in App.Core — `RunTraceChannel(Func<IReadOnlyList<string>, Task> write)`, `const int Capacity = 10_000`, `void Post(string line)`, `long Dropped`, `int Pending`, `Task CompleteAsync()`.

- [ ] **Step 1: Write the failing tests**

In `tests/XrplFile.Core.Tests/BatchTraceTests.cs`, add:

```csharp
    [Fact]
    public void The_trace_keeps_its_last_ten_thousand_entries_and_hands_every_one_to_its_listener()
    {
        List<string> heard = [];
        BatchTrace trace = new(entry => heard.Add(entry.Event));

        for (int i = 0; i < BatchTrace.Capacity + 50; i++) trace.Add($"register:{i}");

        Assert.Equal(BatchTrace.Capacity + 50, trace.Total);
        Assert.Equal(BatchTrace.Capacity, trace.Entries.Count);
        Assert.Equal("register:50", trace.Events[0]);
        Assert.Equal($"register:{BatchTrace.Capacity + 49}", trace.Events[^1]);
        Assert.Equal(BatchTrace.Capacity + 50, heard.Count);
    }

    [Fact]
    public async Task A_strategy_hands_its_entries_to_the_listener_its_options_name()
    {
        BatchTestNode node = new();
        List<string> heard = [];
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { TraceListener = entry => { lock (heard) heard.Add(entry.Event); } });

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, TestContext.Current.CancellationToken);

        lock (heard) Assert.Equal(strategy.Trace, heard);
    }
```

`tests/XrplFile.App.Core.Tests/RunTraceChannelTests.cs`:

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class RunTraceChannelTests
{
    [Fact]
    public async Task A_slow_writer_drops_the_oldest_entries_counts_them_and_never_queues_more_than_the_channel_holds()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> written = [];
        RunTraceChannel channel = new(async lines =>
        {
            await release.Task;
            lock (written) written.AddRange(lines);
        });

        for (int i = 0; i < 25_000; i++)
        {
            channel.Post($"line {i}");
            Assert.True(channel.Pending <= RunTraceChannel.Capacity);
        }

        release.SetResult();
        await channel.CompleteAsync();

        List<string> lines = [.. written.Where(line => line.StartsWith("line ", StringComparison.Ordinal))];
        Assert.Equal(25_000, lines.Count + channel.Dropped);
        Assert.True(channel.Dropped >= 25_000 - RunTraceChannel.Capacity - 512, $"dropped {channel.Dropped}");
        Assert.Equal("line 24999", lines[^1]);
        Assert.Contains(written, line => line == $"dropped {channel.Dropped} trace entries: the log could not keep up");
    }

    [Fact]
    public async Task A_fast_writer_loses_nothing_and_keeps_the_order()
    {
        List<string> written = [];
        RunTraceChannel channel = new(lines =>
        {
            lock (written) written.AddRange(lines);
            return Task.CompletedTask;
        });

        for (int i = 0; i < 2_000; i++) channel.Post($"line {i}");
        await channel.CompleteAsync();

        Assert.Equal(0, channel.Dropped);
        Assert.Equal([.. Enumerable.Range(0, 2_000).Select(i => $"line {i}")], written);
    }

    [Fact]
    public async Task A_writer_that_throws_does_not_stop_the_drain()
    {
        int calls = 0;
        RunTraceChannel channel = new(_ => Interlocked.Increment(ref calls) == 1 ? throw new IOException("disk") : Task.CompletedTask);

        channel.Post("one");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        channel.Post("two");
        await channel.CompleteAsync();

        Assert.True(calls >= 2);
    }
}
```

The 512 is the drain's batch size (Step 3): the batch the writer was holding when it blocked is neither pending nor dropped.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchTraceTests` and `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunTraceChannelTests`
Expected: build errors (`Capacity`, `Total`, the listener constructor, `TraceListener`, `RunTraceChannel`).

- [ ] **Step 3: The ring and the channel**

Replace `BatchTrace`'s storage:

```csharp
internal sealed class BatchTrace(Action<BatchTraceEntry>? listener = null)
{
    /// <summary>The entries kept (spec S18); a session-wide list grew with the run.</summary>
    public const int Capacity = 10_000;

    private readonly Lock _gate = new();
    private readonly BatchTraceEntry[] _ring = new BatchTraceEntry[Capacity];
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _total;
    private long _signingTicks;

    /// <summary>Entries ever added, the dropped ones included.</summary>
    public long Total
    {
        get { lock (_gate) return _total; }
    }

    public IReadOnlyList<string> Events => [.. Entries.Select(entry => entry.Event)];

    /// <summary>The last <see cref="Capacity"/> entries, oldest first.</summary>
    public IReadOnlyList<BatchTraceEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                int kept = (int)Math.Min(_total, Capacity);
                int oldest = (int)((_total - kept) % Capacity);
                BatchTraceEntry[] copy = new BatchTraceEntry[kept];

                for (int i = 0; i < kept; i++) copy[i] = _ring[(oldest + i) % Capacity];

                return copy;
            }
        }
    }

    public TimeSpan SigningTime => TimeSpan.FromTicks(Interlocked.Read(ref _signingTicks));

    public void Add(string entry)
    {
        // Under the gate, so the entries' times never go backwards in their order and the listener hears them in that order.
        lock (_gate)
        {
            BatchTraceEntry added = new(Stopwatch.GetElapsedTime(_started), entry);
            _ring[(int)(_total % Capacity)] = added;
            _total++;
            listener?.Invoke(added);
        }
    }

    // TimeSigningAsync unchanged.
}
```

`BatchStrategyOptions`:

```csharp
    /// <summary>
    /// Hears every trace entry, in order, as it is added (spec S18): how the application writes a run's
    /// trace as it goes rather than copying it at the end. Called under the trace's lock, so it must not block.
    /// </summary>
    public Action<BatchTraceEntry>? TraceListener { get; init; }
```

In the `BatchStrategy` constructor, `new BatchTrace()` becomes `new BatchTrace(_options.TraceListener)`. Update the `TraceEntries` doc: "The last 10,000 events of what this instance did, in order …".

`app/XrplFile.App.Core/RunTraceChannel.cs`:

```csharp
using System.Threading.Channels;

namespace XrplFile.App.Core;

/// <summary>
/// The way from a run's trace to its log (spec S18): a bounded channel of
/// <see cref="Capacity"/> lines, drained by one background writer with awaited writes. When the log
/// cannot keep up, the oldest pending line is dropped and counted, so a slow disk never queues
/// without bound behind a run; the count is written when the channel completes.
/// </summary>
public sealed class RunTraceChannel
{
    public const int Capacity = 10_000;

    /// <summary>The most lines one write is handed.</summary>
    private const int Batch = 512;

    private readonly Func<IReadOnlyList<string>, Task> _write;
    private readonly Channel<string> _channel;
    private readonly Task _drain;
    private long _dropped;

    public RunTraceChannel(Func<IReadOnlyList<string>, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        _write = write;
        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Lines dropped because the channel was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Lines waiting for the writer; never more than <see cref="Capacity"/>.</summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>Queues a line without waiting; a full channel drops its oldest line instead.</summary>
    public void Post(string line) => _channel.Writer.TryWrite(line);

    /// <summary>Stops taking lines and waits until every queued one, and the count of the dropped, is written.</summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            List<string> lines = new(Batch);
            while (lines.Count < Batch && _channel.Reader.TryRead(out string? line)) lines.Add(line);

            await WriteQuietlyAsync(lines).ConfigureAwait(false);
        }

        long dropped = Dropped;
        if (dropped > 0) await WriteQuietlyAsync([$"dropped {dropped} trace entries: the log could not keep up"]).ConfigureAwait(false);
    }

    /// <summary>A diagnostic that can fail the run it describes is worse than none.</summary>
    private async Task WriteQuietlyAsync(IReadOnlyList<string> lines)
    {
        try
        {
            await _write(lines).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
```

In `RunTraceLog.AppendAsync`, move the building of `block` and `bytes` (and the cut to `_maxBytes`) to just after `await _gate.WaitAsync(...)` succeeds, inside the `try`: a writer waiting for the gate then holds no buffer (spec S18's table).

- [ ] **Step 4: The run writes its trace as it goes**

In `PublicationRun`:

1. `RunAttempt` replaces `BatchStrategy? Batch` and `DateTimeOffset BatchBuiltAt` with `public RunTraceChannel? Trace { get; set; }`.
2. In `RunBatchAsync`, `Build` becomes:

```csharp
            BatchStrategy Build(BatchBudget budget, IBatchFenceRecorder fence)
            {
                RunTraceChannel trace = new(lines => _traceLog.AppendAsync(lines, CancellationToken.None));
                trace.Post(string.Create(CultureInfo.InvariantCulture,
                    $"{_time.GetUtcNow():O} batch run {StateFileOf(attempt)} network={request.Network} writer={request.Account} payer={request.PayerAccount} bytes={request.File.Length}"));

                BatchStrategyOptions options = (batchOptions ?? new BatchStrategyOptions()) with
                {
                    TraceListener = entry => trace.Post(string.Create(CultureInfo.InvariantCulture, $"+{entry.Elapsed.TotalSeconds:F3} {entry.Event}")),
                };

                built = PublishPolicy.CreateBatch(budget, fence, connection, writer, payerKey, options);
                attempt.Trace = trace;
                return built;
            }
```

   with `private static string StateFileOf(RunAttempt attempt) => attempt.StatePath is null ? "(no state file)" : Path.GetFileName(attempt.StatePath);`. The header names the state file when the run is a resume (its path is known before the strategy is built); a fresh run's file name is not known yet at that moment, so its header says "(no state file)" and the file name follows on the end line (below).

3. `WriteTraceAsync` becomes:

```csharp
    private async Task WriteTraceAsync(PublishRequest request, RunAttempt attempt, PublicationRunState finalState, Exception? caught)
    {
        if (attempt.Trace is not { } trace) return;

        string ending = /* the existing switch over caught, unchanged */;

        trace.Post(string.Create(CultureInfo.InvariantCulture, $"{_time.GetUtcNow():O} end {finalState}{ending} {StateFileOf(attempt)}"));
        await trace.CompleteAsync().ConfigureAwait(false);
    }
```

   `request` stays a parameter only if still used; drop it otherwise.

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchTraceTests`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunTraceChannelTests -class XrplFile.App.Core.Tests.PublicationRunBatchTests -class XrplFile.App.Core.Tests.RunTraceLogTests`
Expected: PASS. `A_batch_run_writes_its_round_trace_to_the_log_under_its_state_file_name` still finds the state file's name (now on the end line), `round:`, `transmit:0:tesSUCCESS`, `settled:` and `end Succeeded`.

- [ ] **Step 6: Commit**

```bash
git add src app tests
git commit -m "feat: bound the Batch trace in memory and on its way to the log

BatchTrace keeps a ring of its last 10,000 entries and hands each one to a
listener as it is added. The application posts every entry to a bounded
channel of 10,000 lines drained by one background writer; a log that cannot
keep up loses its oldest pending lines, and how many is written at the end.
PublicationRun no longer copies the trace when a run ends."
```

---

### Task 9: The reader's history cache stays correct and bounded (S26)

**Files:**
- Modify: `src/XrplFile.Core/ForwardHistory.cs` (`LetGo`, a held-ledger rule, `PeakHeld`), `src/XrplFile.Core/XrplFileReader.cs` (`ReaderOptions.HistoryCacheRecords`, `HistoryPeakHeld`)
- Modify: `web/src/history.mjs`, `web/src/reader.mjs` (constructor option)
- Test: `tests/XrplFile.Core.Tests/ForwardHistoryTests.cs` (two tests), `web/test/history.test.mjs` (two tests)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `ForwardHistory.PeakHeld : int` (internal) — the most records held after any record was taken in.
  - `ReaderOptions.HistoryCacheRecords : int` (internal init, default `ForwardHistory.DefaultMaxRecords` = 4,096); `XrplFileReader.HistoryPeakHeld : int` (internal).
  - Web: `new ForwardHistory(source, maxRecords)` gains `peakHeld`; `new XrplFileReader(source, { maxBytes, historyCacheRecords })` (default `DEFAULT_MAX_RECORDS`).

**The rule (spec S26):** the cache records the ledger range it holds completely — every record of the writer from ledger X (`_from`) to the cursor's place. A walk is answered from it only when its first ledger is ≥ X; otherwise it starts over at its first ledger from the node. When holding a record would exceed the cap, the oldest whole ledgers are dropped and X rises past them. If what is left is the one ledger being walked and it alone exceeds the cap, that ledger is not cached: its held records are dropped, X becomes the ledger after it, and the walk streams through the rest of it without holding them; the cache starts again with the next ledger. The active walk never skips a record.

- [ ] **Step 1: Write the failing .NET tests**

In `tests/XrplFile.Core.Tests/ForwardHistoryTests.cs`, add:

```csharp
    /// <summary>The 2,500,500-byte vector with every chunk moved into one ledger behind 17,499 records without memos: 20,000 records in one ledger.</summary>
    private static (FakeLedgerSource Source, ManifestRecord Record, List<BlockEntry> Blocks) OneCrowdedLedger()
    {
        long ledger = Vector.Ledger.First;
        FakeLedgerSource source = new();

        for (int i = 0; i < 17_499; i++)
        {
            source.Add(new LedgerTransaction(Vectors.Hash($"filler:{i}"), ledger, Vectors.Writer, true, true, "tesSUCCESS", null, null, null));
        }

        foreach (LedgerTransaction tx in Vectors.Ledger(Vector).Where(tx => MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is not null))
        {
            source.Add(tx with { LedgerIndex = ledger });
        }

        Manifest manifest = Manifest.Decode(Vector.Manifest) with { FirstLedger = ledger, LastLedger = ledger };
        List<BlockEntry> blocks = [.. Vector.BlockHashes.Select((hash, index) => new BlockEntry(index, hash, ledger))];

        return (source, new ManifestRecord(manifest, Vectors.Writer, ledger + 1, Vectors.ManifestHash), blocks);
    }

    [Fact]
    public async Task A_ledger_larger_than_the_cap_and_a_revisited_block_read_the_same_with_and_without_the_cache()
    {
        (FakeLedgerSource source, ManifestRecord record, List<BlockEntry> blocks) = OneCrowdedLedger();
        XrplFileReader cached = new(source);
        int[] order = [2, 0, 1, 0, 2];

        foreach (int block in order)
        {
            byte[] withCache = await cached.BlockAsync(record, blocks[block], TestContext.Current.CancellationToken);

            // The baseline shares nothing: a fresh reader per block walks the node from the block's
            // first ledger, so no cursor or cache of an earlier walk can stand behind its answer.
            byte[] withoutCache = await new XrplFileReader(source).BlockAsync(record, blocks[block], TestContext.Current.CancellationToken);

            Assert.Equal(withoutCache, withCache);
        }

        Assert.InRange(cached.HistoryPeakHeld, 0, ForwardHistory.DefaultMaxRecords);
    }

    [Fact]
    public async Task A_ledger_larger_than_the_cap_is_streamed_through_and_the_cache_starts_again_after_it()
    {
        FakeLedgerSource source = new();
        for (int i = 0; i < 300; i++) source.Add(new LedgerTransaction(Vectors.Hash($"big:{i}"), 10, Vectors.Writer, true, true, "tesSUCCESS", null, null, null));
        for (int i = 0; i < 50; i++) source.Add(new LedgerTransaction(Vectors.Hash($"next:{i}"), 11, Vectors.Writer, true, true, "tesSUCCESS", null, null, null));
        ForwardHistory history = new(source, maxRecords: 100);

        List<LedgerTransaction> whole = [];
        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, 10, 11, TestContext.Current.CancellationToken)) whole.Add(tx);
        List<LedgerTransaction> tail = [];
        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, 11, 11, TestContext.Current.CancellationToken)) tail.Add(tx);
        List<LedgerTransaction> again = [];
        await foreach (LedgerTransaction tx in history.WalkAsync(Vectors.Writer, 10, 11, TestContext.Current.CancellationToken)) again.Add(tx);

        Assert.Equal(350, whole.Count);
        Assert.Equal(whole.Skip(300), tail);
        Assert.Equal(whole, again);
        Assert.InRange(history.PeakHeld, 0, 100);
        Assert.Equal(2, source.AccountTransactionsCalls);
    }
```

The walk to 11 is answered from the cache (ledger 11 was held), the walk from 10 starts over (ledger 10 was not): two node walks in all.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ForwardHistoryTests`
Expected: build error (`HistoryCacheRecords`, `HistoryPeakHeld`, `PeakHeld`); after Step 3's members exist and before its rule, the peak assertions fail (the old rule holds the whole crowded ledger).

- [ ] **Step 3: The .NET rule**

In `ForwardHistory`, add the fields and the property:

```csharp
    // A ledger that alone held more than the cap: its records are streamed through and not held.
    private long _uncached = long.MinValue;

    /// <summary>The most records held after any record was taken in.</summary>
    public int PeakHeld { get; private set; }
```

In `WalkAsync`'s cursor loop, replace `_held.Add(tx); LetGo();` with `Hold(tx);`. Replace `LetGo` with:

```csharp
    private void Hold(LedgerTransaction tx)
    {
        if (tx.LedgerIndex != _uncached)
        {
            _held.Add(tx);
            LetGo();
        }

        PeakHeld = Math.Max(PeakHeld, _held.Count);
    }

    /// <summary>
    /// Keeps what is held within the cap (spec S26). The oldest ledgers go first, whole, and what is
    /// held stays every record from <see cref="_from"/> on. When the ledger being walked alone is
    /// larger than the cap, it is not held at all: <see cref="_from"/> moves past it, so a later walk
    /// from it asks the node again instead of getting a ledger with records missing.
    /// </summary>
    private void LetGo()
    {
        while (_held.Count > _maxRecords && _held[0].LedgerIndex < _position)
        {
            long oldest = _held[0].LedgerIndex;
            int end = _held.FindIndex(tx => tx.LedgerIndex != oldest);
            _held.RemoveRange(0, end);
            _from = oldest + 1;
        }

        if (_held.Count > _maxRecords)
        {
            _uncached = _position;
            _held.Clear();
            _from = _position + 1;
        }
    }
```

`StartOver` resets `_uncached = long.MinValue;` along with the rest. Update the class summary's paragraph on memory: "Memory is bounded by maxRecords: … once more than that many records are held the oldest ledgers are let go whole, and a ledger that alone is larger than the cap is streamed through without being held (spec S26). A walk from a ledger that was let go … starts over."

In `XrplFileReader.cs`:

```csharp
public sealed class ReaderOptions
{
    /// <summary>The largest file the reader assembles. Checked against the manifest before any chunk is read.</summary>
    public long MaxBytes { get; init; } = 20_000_000;

    /// <summary>The records the block walks may share (spec S26). Zero shares none: what a test compares the cache against.</summary>
    internal int HistoryCacheRecords { get; init; } = ForwardHistory.DefaultMaxRecords;
}
```

and in `XrplFileReader`, initialize the history from the options and expose its peak:

```csharp
    private readonly ForwardHistory _history = new(source, (options ?? new ReaderOptions()).HistoryCacheRecords);

    /// <summary>The most records this read's block walks held at once.</summary>
    internal int HistoryPeakHeld => _history.PeakHeld;
```

- [ ] **Step 4: Run the .NET tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.ForwardHistoryTests -class XrplFile.Tests.XrplFileReaderTests`
Expected: PASS.

- [ ] **Step 5: The same rule and tests in the browser library**

`web/src/history.mjs`: in the constructor add `this.uncached = -Infinity;` and `this.peakHeld = 0;`; in `startOver` reset `this.uncached = -Infinity;`; in `walk` replace `this.held.push(tx); this.letGo();` with `this.hold(tx);`; replace `letGo` with:

```javascript
  hold(tx) {
    if (tx.ledgerIndex !== this.uncached) {
      this.held.push(tx);
      this.letGo();
    }
    this.peakHeld = Math.max(this.peakHeld, this.held.length);
  }

  /**
   * Keeps what is held within the cap (spec S26), as the .NET ForwardHistory does: the oldest ledgers
   * go first, whole; a ledger that alone is larger than the cap is not held at all, and `from` moves
   * past it, so a later walk from it asks the node again instead of getting a ledger with records missing.
   */
  letGo() {
    while (this.held.length > this.maxRecords && this.held[0].ledgerIndex < this.position) {
      const oldest = this.held[0].ledgerIndex;
      const end = this.held.findIndex((tx) => tx.ledgerIndex !== oldest);
      this.held.splice(0, end);
      this.from = oldest + 1;
    }

    if (this.held.length > this.maxRecords) {
      this.uncached = this.position;
      this.held = [];
      this.from = this.position + 1;
    }
  }
```

and update the class comment's memory paragraph the same way as the .NET one.

`web/src/reader.mjs`: the constructor becomes

```javascript
  constructor(source, { maxBytes = 20_000_000, historyCacheRecords = DEFAULT_MAX_RECORDS } = {}) {
    this.source = source;
    this.maxBytes = maxBytes;
    // Shared by the blocks of one read: a block starts in the ledger the block before it ended
    // in, and walking each block afresh read that ledger twice.
    this.history = new ForwardHistory(source, historyCacheRecords);
  }
```

with `import { ForwardHistory, DEFAULT_MAX_RECORDS } from './history.mjs';`.

In `web/test/history.test.mjs`, add (extend the imports with `XrplFileReader` from `../src/reader.mjs`, `decodeManifest` from `../src/manifest.mjs`, `DEFAULT_MAX_RECORDS` from `../src/history.mjs` and `labelHash` from `./vectors.mjs`):

```javascript
async function crowdedLedger() {
  const ledgerIndex = vector.ledger.first;
  const fillers = Array.from({ length: 17_499 }, (_, i) => ({
    hash: labelHash(`filler:${i}`), ledgerIndex, account: WRITER, succeeded: true, validated: true,
    memoTypeHex: null, memoDataHex: null, memoFormatHex: null,
  }));
  const chunks = (await vectorLedger(vector)).filter((tx) => chunkIndexOf(tx.memoTypeHex) !== null).map((tx) => ({ ...tx, ledgerIndex }));
  const manifest = { ...decodeManifest(vector.manifest), firstLedger: ledgerIndex, lastLedger: ledgerIndex };
  const blocks = vector.blockHashes.map((h, i) => ({ i, h, l: ledgerIndex }));
  return { source: new FakeSource([...fillers, ...chunks]), record: { manifest, writer: WRITER }, blocks };
}

test('a ledger larger than the cap and a revisited block read the same with and without the cache', async () => {
  const { source, record, blocks } = await crowdedLedger();
  const cached = new XrplFileReader(source);

  // The baseline shares nothing: a fresh reader per block walks from the block's first ledger.
  for (const block of [2, 0, 1, 0, 2]) {
    assert.deepEqual(await cached.block(record, blocks[block]), await new XrplFileReader(source).block(record, blocks[block]));
  }

  assert.ok(cached.history.peakHeld <= DEFAULT_MAX_RECORDS, `held ${cached.history.peakHeld}`);
});

test('a ledger larger than the cap is streamed through and the cache starts again after it', async () => {
  const record = (i, ledgerIndex) => ({
    hash: labelHash(`r:${ledgerIndex}:${i}`), ledgerIndex, account: WRITER, succeeded: true, validated: true,
    memoTypeHex: null, memoDataHex: null, memoFormatHex: null,
  });
  const source = new FakeSource([...Array.from({ length: 300 }, (_, i) => record(i, 10)), ...Array.from({ length: 50 }, (_, i) => record(i, 11))]);
  const history = new ForwardHistory(source, 100);
  const collect = async (from) => { const seen = []; for await (const tx of history.walk(WRITER, from, 11)) seen.push(tx); return seen; };

  const whole = await collect(10);
  const tail = await collect(11);
  const again = await collect(10);

  assert.equal(whole.length, 350);
  assert.deepEqual(tail, whole.slice(300));
  assert.deepEqual(again, whole);
  assert.ok(history.peakHeld <= 100, `held ${history.peakHeld}`);
  assert.equal(source.calls.length, 2);
});
```

- [ ] **Step 6: Run the web tests**

Run: `cd web && node --test test/*.test.mjs`
Expected: PASS, including the existing history and reader tests.

- [ ] **Step 7: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests web
git commit -m "fix(reader): keep the history cache bounded and correct for any ledger size

The cache answers a walk only from the range it holds completely. When a
ledger alone is larger than the cap it is streamed through without being held
and the range starts after it, so a later or revisited block that starts in it
asks the node again. The .NET and the browser reader follow the same rule and
are tested against a 20,000-record ledger with and without the cache."
```

---

### Task 10: The Batch estimate grouped per round (S11, library part)

**Files:**
- Modify: `src/XrplFile.Core/BatchPublicationEstimate.cs` (+ `Rounds`), `src/XrplFile.Core/PublicationEstimate.cs` (`EstimateBatchAsync(source, writer, payer, chunks, blocks, lists, ct)`)
- Test: `tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs` (three tests)

**Interfaces:**
- Consumes: Task 4's `BatchStrategyOptions.DefaultMaxRoundMemos`, `BatchGrouping.MaxInners`.
- Produces: `BatchPublicationEstimate.Rounds : int` (init) — rounds over the three passes, as the strategy will run them. `Batches`, `SoloTransactions`, `PayerFeeDrops` and `WriterFeeDrops` follow the same grouping: rounds of `MaxRoundMemos`, within a round 8 to a batch with a trailing 8 + 1 becoming 7 + 2 (the count of batches is unchanged by that), and a round of exactly one memo a solo. The worst cases of B26 are unchanged.

- [ ] **Step 1: Write the failing tests**

In `tests/XrplFile.Core.Tests/BatchPublicationEstimateTests.cs`, add (reusing the class's existing node/balance setup helper; if it has none, use `FakeLedgerSource source = new(); source.Balances["rWriter"] = source.Balances["rPayer"] = 100_000_000_000;`):

```csharp
    [Fact]
    public async Task The_spec_example_of_a_1_7_GB_file_runs_104_chunk_rounds()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 1_689_809_534, TestContext.Current.CancellationToken);

        Assert.Equal(1_689_810, estimate.Chunks);
        Assert.Equal(1_651, estimate.Blocks);
        Assert.Equal(166, estimate.Lists);
        Assert.Equal(104 + 1 + 1, estimate.Rounds);
        Assert.Equal(103 * 2_048 + 283 + 21, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
    }

    [Fact]
    public async Task One_memo_past_a_full_round_is_a_solo_as_the_strategy_sends_it()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 16_385_000, TestContext.Current.CancellationToken);

        Assert.Equal(16_385, estimate.Chunks);
        Assert.Equal(2, estimate.Lists);
        Assert.Equal(2 + 1 + 1, estimate.Rounds);
        Assert.Equal(2_048 + 1, estimate.Batches);
        Assert.Equal(2, estimate.SoloTransactions);
        Assert.Equal((16_384 + 2 + 3 * 2_049) * estimate.FeeUnitDrops, estimate.PayerFeeDrops);
        Assert.Equal(2 * estimate.FeeUnitDrops, estimate.WriterFeeDrops);
    }

    [Fact]
    public async Task A_small_file_is_one_round_per_pass()
    {
        FakeLedgerSource source = new();

        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(
            source, "rWriter", "rPayer", 9_000, TestContext.Current.CancellationToken);

        Assert.Equal(2, estimate.Rounds);
        Assert.Equal(2, estimate.Batches);
        Assert.Equal(1, estimate.SoloTransactions);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchPublicationEstimateTests`
Expected: build error (`Rounds`), then the solo test fails (the old count is one pass of 16,385: 2,049 batches and no chunk solo).

- [ ] **Step 3: The grouping**

In `BatchPublicationEstimate`, add after the record's parameter list:

```csharp
    /// <summary>
    /// Rounds over the three passes (spec S11), as <see cref="BatchStrategy"/> runs them: rounds of
    /// <see cref="BatchStrategyOptions.DefaultMaxRoundMemos"/> memos. Each costs about three ledgers to
    /// settle, which is what the estimate's duration adds per round.
    /// </summary>
    public int Rounds { get; init; }
```

and update the `<param name="Batches">` and `<param name="SoloTransactions">` docs to "… grouped per round as the strategy groups them (spec S11)" and "Memos sent on their own: a round of exactly one memo".

In `PublicationEstimator.EstimateBatchAsync(... long chunks, int blocks, int lists, ...)`, replace the loop with:

```csharp
        int rounds = 0;
        int batches = 0;
        int solos = 0;
        long batchedMemos = 0;

        foreach (long pass in new[] { chunks, lists, 1L })
        {
            if (pass == 0) continue;

            long full = pass / BatchStrategyOptions.DefaultMaxRoundMemos;
            long rest = pass % BatchStrategyOptions.DefaultMaxRoundMemos;

            rounds = checked(rounds + (int)full + (rest > 0 ? 1 : 0));
            batches = checked(batches + (int)(full * BatchesOf(BatchStrategyOptions.DefaultMaxRoundMemos)));
            batchedMemos += full * BatchStrategyOptions.DefaultMaxRoundMemos;

            // A round of exactly one memo is a solo of the writer (spec B11); the borrowing of 8 + 1
            // into 7 + 2 changes sizes, not the number of batches.
            if (rest == 1)
            {
                solos++;
            }
            else if (rest > 1)
            {
                batches = checked(batches + BatchesOf(rest));
                batchedMemos += rest;
            }
        }
```

with

```csharp
    private static int BatchesOf(long memos) => (int)((memos + BatchGrouping.MaxInners - 1) / BatchGrouping.MaxInners);
```

and set `Rounds = rounds` in an object initializer on the returned `BatchPublicationEstimate`. The arithmetic replaces `BatchGrouping.Sizes(checked((int)pass))`, which built a list of 2 million sizes for a 16 GiB file to count it.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.BatchPublicationEstimateTests -class XrplFile.Tests.BatchGroupingTests`
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/XrplFile.Core tests/XrplFile.Core.Tests
git commit -m "feat(core): the Batch estimate counts rounds as the strategy runs them

BatchPublicationEstimate gains Rounds, and its batches, solos and fees follow
the strategy's grouping per round of 16,384 memos: one memo left after a full
round is a solo of the writer. The count is arithmetic, not a list of sizes."
```

---

### Task 11: Every session spends only what it was approved for (S25, library part)

**Files:**
- Create: `src/XrplFile.Core/SpendingApproval.cs`
- Modify: `src/XrplFile.Core/XrplFileException.cs` (+ `ApprovalExceeded`), `src/XrplFile.Core/PublicationSession.cs` (`SessionOptions.Approval`, `SessionOptions.ApproveResume`; both starts, both resumes, `AttachBatchAsync`), `app/XrplFile.App.Core/PublishErrorMessages.cs`, `app/XrplFile.App.Core/ReadErrorMessages.cs` (the publishing-only arm)
- Test: `tests/XrplFile.Core.Tests/SpendingApprovalTests.cs`, `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs` (one test), `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs` (one value added)

**Interfaces:**
- Consumes: Task 10 (`BatchPublicationEstimate`), the existing `PublicationEstimate`, `BatchBudget`.
- Produces (namespace `XrplFile`):
  - `public sealed record SpendingApproval(string Writer, string? Payer, long FeeUnitCeilingDrops, long WriterCeilingDrops, long PayerCeilingDrops)` — `static SpendingApproval For(PublicationEstimate estimate, string writer)` (fee ceiling = `FeeDropsPerTransaction`, writer ceiling = `FeeDropsWorstCase`, payer 0), `static SpendingApproval For(BatchPublicationEstimate estimate, string writer, string payer)` (fee ceiling = `FeeCeilingDrops`, writer = `WriterFeeDropsWorstCase`, payer = `PayerFeeDropsWorstCase`), `internal void Require(PublicationEstimate estimate, string writer)`, `internal void Require(BatchPublicationEstimate estimate, string writer, string payer)`, `internal BatchPublicationEstimate AtCeiling(BatchPublicationEstimate estimate)`, `internal BatchBudget Budget(BatchPublicationEstimate estimate)`.
  - `public sealed record ResumeQuote(string Writer, string? Payer, long MissingChunks, PublicationEstimate? OneAccount, BatchPublicationEstimate? Batch)`.
  - `public delegate Task<SpendingApproval> ResumeApproval(ResumeQuote quote, CancellationToken cancellationToken)`.
  - `SessionOptions.Approval : SpendingApproval?` (fresh runs) and `SessionOptions.ApproveResume : ResumeApproval?` (resumes). Null keeps today's behaviour for a library caller: the session's own estimate is the approval.
  - `XrplFileError.ApprovalExceeded`, sentence "Fees or balances changed since this was approved. Estimate it again."

**The rule (spec S25):** an approval names the writer, the payer, the fee-unit ceiling (B15) and each account's ceiling — worst-case fees (Spec question 2). A fresh run's approval comes from the caller (the Estimate screen); a resume's is asked for after reconciliation, from a quote of the remaining work. The session refuses, before anything is sent, any estimate it takes that exceeds the approval. **Batch:** only exceeding a ceiling refuses. The re-estimated fee unit is compared with the approved fee-unit ceiling (B15 was designed to let the unit rise up to it), and each account's worst case is computed at the approved ceiling — the most B26 lets the run spend — and compared with that account's approved ceiling, which in effect asks that the run be no larger than the one approved. Balances are then checked against those same figures. A unit that rises from 15 to 18 drops under a ceiling of 30 therefore starts; a unit above 30 does not. **One-account:** the per-transaction fee is fixed in the strategy, so a fee above the approved one, or a worst case above the approved one, refuses. A Batch strategy's budget is the approval's: the round unit may rise to the approved fee-unit ceiling and the spending of each account to its ceiling (B26). A different writer or payer is a caller's mistake (`ArgumentException`).

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.Core.Tests/SpendingApprovalTests.cs`:

```csharp
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public sealed class SpendingApprovalTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-approval-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SessionOptions Options(SpendingApproval? approval = null, ResumeApproval? resume = null) =>
        new() { StateDirectory = _stateDirectory, FencePoll = TimeSpan.FromMilliseconds(1), TicketsPerBatch = 100, Approval = approval, ApproveResume = resume };

    private static BatchTestNode Funded()
    {
        BatchTestNode node = new();
        node.Ledger.Fees = new NetworkFees(10, 1_000_000, 200_000);
        node.Ledger.Balances[node.Writer.Account] = 100_000_000;
        node.Ledger.Balances[node.Payer.Account] = 100_000_000;
        return node;
    }

    private static BatchStrategyFactory Factory(BatchTestNode node, List<BatchBudget> budgets) =>
        (budget, fence) =>
        {
            budgets.Add(budget);
            return new BatchStrategy(node.Sink, node.Ledger, node.Writer, node.Payer, budget, fence, BatchTestNode.FastOptions);
        };

    [Fact]
    public async Task A_fresh_Batch_run_spends_on_the_approvals_ceilings()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account) with { PayerCeilingDrops = estimate.PayerFeeDropsWorstCase + 7 };

        await PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token);

        Assert.Equal(new BatchBudget(estimate.FeeUnitDrops, approval.FeeUnitCeilingDrops, approval.PayerCeilingDrops, approval.WriterCeilingDrops), Assert.Single(budgets));
    }

    [Fact]
    public async Task A_fee_unit_that_rises_under_the_approved_ceiling_still_starts()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account);
        Assert.Equal((15L, 30L), (estimate.FeeUnitDrops, approval.FeeUnitCeilingDrops));

        // The open-ledger fee moves between the Estimate screen and the run, as testnet's does.
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(12, 10);

        await PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token);

        BatchBudget budget = Assert.Single(budgets);
        Assert.Equal((18L, 30L), (budget.FeeUnitDrops, budget.FeeCeilingDrops));
    }

    [Fact]
    public async Task A_fresh_Batch_run_whose_estimate_exceeds_its_approval_stops_before_anything_is_written()
    {
        BatchTestNode node = Funded();
        List<BatchBudget> budgets = [];
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval stale = SpendingApproval.For(estimate, node.Writer.Account, node.Payer.Account);
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(30, 10);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            Factory(node, budgets), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(stale), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
        Assert.Empty(budgets);
        Assert.Empty(Directory.GetFiles(_stateDirectory));
    }

    [Fact]
    public async Task A_one_account_run_priced_above_its_approval_is_refused()
    {
        XrplWallet wallet = XrplWallet.Generate();
        FakeLedgerSource ledger = new() { Fees = new NetworkFees(10, 1_000_000, 200_000) };
        ledger.Balances[wallet.ClassicAddress] = 500_000_000;
        byte[] file = VectorStream.Bytes("approval", 2_500);
        PublicationEstimate estimate = await PublicationSession.EstimateAsync(ledger, wallet.ClassicAddress, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, wallet.ClassicAddress);
        ledger.Fees = new NetworkFees(12, 1_000_000, 200_000);
        await using SequenceStrategy strategy = new(new FakeLedgerSink(ledger), new WalletSigner(wallet), new SequenceStrategyOptions { FeeDrops = 12 });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => PublicationSession.StartAsync(
            strategy, ledger, file, "text/plain", null, wallet.ClassicAddress, "key", "testnet", Options(approval), Token));

        Assert.Equal(XrplFileError.ApprovalExceeded, error.Error);
    }

    [Fact]
    public async Task An_approval_for_another_writer_is_a_callers_mistake()
    {
        BatchTestNode node = Funded();
        byte[] file = VectorStream.Bytes("approval", 20_000);
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(node.Ledger, node.Writer.Account, node.Payer.Account, file.Length, Options(), Token);
        SpendingApproval approval = SpendingApproval.For(estimate, "rSomebodyElse", node.Payer.Account);

        await Assert.ThrowsAsync<ArgumentException>(() => PublicationSession.StartAsync(
            Factory(node, []), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet",
            Options(approval), Token));
    }

    private async Task<(PublicationSession Session, byte[] File)> InterruptedAsync(BatchTestNode node)
    {
        byte[] file = VectorStream.Bytes("approval-resume", 40_000);
        PublicationSession session = await PublicationSession.StartAsync(
            Factory(node, []), node.Ledger, file, "text/plain", null, node.Writer.Account, "key-w", node.Payer.Account, "key-p", "testnet", Options(), Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        node.Sink.Transmitting = transmission =>
        {
            if (transmission.Ordinal >= 1) stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync(null, stop.Token));
        node.Sink.Transmitting = null;
        return (session, file);
    }

    [Fact]
    public async Task A_resume_asks_for_its_approval_after_reconciliation_with_a_quote_of_the_remaining_work()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        List<ResumeQuote> quotes = [];
        List<BatchBudget> budgets = [];

        PublicationSession resumed = await PublicationSession.ResumeAsync(
            Factory(node, budgets), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p",
            Options(resume: (quote, _) =>
            {
                quotes.Add(quote);
                return Task.FromResult(SpendingApproval.For(quote.Batch!, quote.Writer, quote.Payer!));
            }), null, Token);

        ResumeQuote asked = Assert.Single(quotes);
        Assert.Equal(node.Writer.Account, asked.Writer);
        Assert.Equal(node.Payer.Account, asked.Payer);
        Assert.Equal(asked.MissingChunks, asked.Batch!.Chunks);
        Assert.Equal(asked.Batch.PayerFeeDropsWorstCase, Assert.Single(budgets).PayerBudgetDrops);

        PublicationReport report = await resumed.RunAsync(null, Token);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(report.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_resume_whose_approval_is_declined_sends_nothing_and_leaves_the_state_as_it_was()
    {
        BatchTestNode node = Funded();
        (PublicationSession first, byte[] file) = await InterruptedAsync(node);
        byte[] before = await File.ReadAllBytesAsync(first.StatePath, Token);
        int sent = node.Sink.Transmissions.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublicationSession.ResumeAsync(
            Factory(node, []), node.Ledger, file, first.StatePath, node.Payer.Account, "key-p",
            Options(resume: (_, _) => throw new OperationCanceledException("declined")), null, Token));

        Assert.Equal(sent, node.Sink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(first.StatePath, Token));
    }
}
```

In `tests/XrplFile.App.Core.Tests/ReadErrorMessagesTests.cs`, add `XrplFileError.ApprovalExceeded` to the array of `The_streaming_publication_errors_share_the_publishing_only_sentence` (Tasks 5 and 7).

In `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`, add:

```csharp
    [Fact]
    public void A_changed_estimate_asks_for_a_new_one()
    {
        Assert.Equal("Fees or balances changed since this was approved. Estimate it again.", PublishErrorMessages.For(XrplFileError.ApprovalExceeded));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.SpendingApprovalTests`
Expected: build error.

- [ ] **Step 3: The approval types, the error and the sentence**

`src/XrplFile.Core/SpendingApproval.cs`:

```csharp
namespace XrplFile;

/// <summary>
/// What one session may spend (spec S25): the writer and the payer it is for, the fee-unit ceiling
/// of spec B15, and each account's ceiling in drops — its worst-case fees. Immutable: a resume is a
/// new session with new spending and carries an approval of its own.
/// </summary>
/// <param name="Writer">The writer the approval is for.</param>
/// <param name="Payer">The payer, or null for a one-account run.</param>
/// <param name="FeeUnitCeilingDrops">A Batch round's unit may rise to this; a one-account run's per-transaction fee may not exceed it.</param>
/// <param name="WriterCeilingDrops">The most the writer may spend in fees.</param>
/// <param name="PayerCeilingDrops">The most the payer may spend in fees; zero without one.</param>
public sealed record SpendingApproval(string Writer, string? Payer, long FeeUnitCeilingDrops, long WriterCeilingDrops, long PayerCeilingDrops)
{
    /// <summary>A one-account estimate as it was shown: its fee and its worst case.</summary>
    public static SpendingApproval For(PublicationEstimate estimate, string writer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new SpendingApproval(writer, null, estimate.FeeDropsPerTransaction, estimate.FeeDropsWorstCase, 0);
    }

    /// <summary>A Batch estimate as it was shown: its ceiling (B15) and each account's worst case (B26).</summary>
    public static SpendingApproval For(BatchPublicationEstimate estimate, string writer, string payer)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new SpendingApproval(writer, payer, estimate.FeeCeilingDrops, estimate.WriterFeeDropsWorstCase, estimate.PayerFeeDropsWorstCase);
    }

    internal void Require(PublicationEstimate estimate, string writer)
    {
        RequireAccounts(writer, payer: null);

        if (estimate.FeeDropsPerTransaction > FeeUnitCeilingDrops || estimate.FeeDropsWorstCase > WriterCeilingDrops)
        {
            throw Exceeded($"the run prices a transaction at {estimate.FeeDropsPerTransaction} drops and {estimate.FeeDropsWorstCase} at worst; "
                + $"{FeeUnitCeilingDrops} and {WriterCeilingDrops} were approved");
        }
    }

    /// <summary>
    /// Only exceeding a ceiling refuses (spec S25, B15): the unit may rise up to the approved ceiling,
    /// and each worst case is taken at that ceiling, the most B26 lets the run spend, so it exceeds
    /// its approved figure only when the run is larger than the one approved.
    /// </summary>
    internal void Require(BatchPublicationEstimate estimate, string writer, string payer)
    {
        RequireAccounts(writer, payer);
        BatchPublicationEstimate priced = AtCeiling(estimate);

        if (estimate.FeeUnitDrops > FeeUnitCeilingDrops
            || priced.WriterFeeDropsWorstCase > WriterCeilingDrops
            || priced.PayerFeeDropsWorstCase > PayerCeilingDrops)
        {
            throw Exceeded($"the run's unit is {estimate.FeeUnitDrops} drops, and at the approved ceiling the writer's worst case is {priced.WriterFeeDropsWorstCase} "
                + $"and the payer's {priced.PayerFeeDropsWorstCase}; {FeeUnitCeilingDrops}, {WriterCeilingDrops} and {PayerCeilingDrops} were approved");
        }
    }

    /// <summary>
    /// The estimate priced at the approved fee-unit ceiling. B26's worst cases are a count of units
    /// times the ceiling, so they scale exactly.
    /// </summary>
    internal BatchPublicationEstimate AtCeiling(BatchPublicationEstimate estimate) => estimate with
    {
        FeeCeilingDrops = FeeUnitCeilingDrops,
        PayerFeeDropsWorstCase = checked(estimate.PayerFeeDropsWorstCase / estimate.FeeCeilingDrops * FeeUnitCeilingDrops),
        WriterFeeDropsWorstCase = checked(estimate.WriterFeeDropsWorstCase / estimate.FeeCeilingDrops * FeeUnitCeilingDrops),
    };

    /// <summary>The strategy is built from the approval (spec S25), and B26's runtime count enforces each ceiling.</summary>
    internal BatchBudget Budget(BatchPublicationEstimate estimate) =>
        new(estimate.FeeUnitDrops, FeeUnitCeilingDrops, PayerCeilingDrops, WriterCeilingDrops);

    private void RequireAccounts(string writer, string? payer)
    {
        if (!string.Equals(writer, Writer, StringComparison.Ordinal) || !string.Equals(payer, Payer, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"the approval is for writer {Writer} and payer {Payer ?? "none"}, and the run is for {writer} and {payer ?? "none"}", "approval");
        }
    }

    private static XrplFileException Exceeded(string detail) =>
        new(XrplFileError.ApprovalExceeded, $"fees or balances changed since this was approved: {detail}");
}

/// <summary>What a resume will still spend, priced after reconciliation from the work that remains (spec S25).</summary>
/// <param name="Writer">The run's writer.</param>
/// <param name="Payer">The payer this resume runs with, or null for a one-account run.</param>
/// <param name="MissingChunks">The chunks reconciliation did not find in the ledger.</param>
/// <param name="OneAccount">The estimate of a one-account resume, else null.</param>
/// <param name="Batch">The estimate of a Batch resume, else null.</param>
public sealed record ResumeQuote(string Writer, string? Payer, long MissingChunks, PublicationEstimate? OneAccount, BatchPublicationEstimate? Batch);

/// <summary>
/// How a resume gets its approval (spec S25): handed the quote, it returns what the person approved,
/// or throws <see cref="OperationCanceledException"/> when they declined, which ends the resume before anything is sent.
/// </summary>
public delegate Task<SpendingApproval> ResumeApproval(ResumeQuote quote, CancellationToken cancellationToken);
```

`XrplFileError`, after `ManifestRejected`:

```csharp
    /// <summary>An estimate taken inside a run exceeds what the session was approved to spend (spec S25).</summary>
    ApprovalExceeded,
```

`PublishErrorMessages.For`:

```csharp
        XrplFileError.ApprovalExceeded =>
            "Fees or balances changed since this was approved. Estimate it again.",
```

`ReadErrorMessages.For(XrplFileError)`: add `or XrplFileError.ApprovalExceeded` to the publishing-only arm.

- [ ] **Step 4: The session takes approvals**

`SessionOptions`:

```csharp
    /// <summary>
    /// What a fresh run was approved to spend (spec S25), from the screen that showed its estimate.
    /// Null approves the session's own estimate, which is what a library caller without a screen gets.
    /// </summary>
    public SpendingApproval? Approval { get; init; }

    /// <summary>
    /// Asked for a resume's approval after reconciliation, with a quote of the remaining work (spec S25).
    /// Null approves that quote as it is.
    /// </summary>
    public ResumeApproval? ApproveResume { get; init; }
```

In `PublicationSession`:

1. `AttachBatchAsync(BatchStrategyFactory strategyFactory, BatchPublicationEstimate estimate, BatchBudget budget, bool resuming)` — the caller passes the budget; the method's own `new BatchBudget(...)` line goes.
2. One-account `StartAsync` (source overload), after `PublicationEstimator.Require(estimate, options.BudgetDrops);`: `options.Approval?.Require(estimate, writer);`
3. Batch `StartAsync` (source overload), in place of the existing `PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);`:

```csharp
        SpendingApproval approval = options.Approval ?? SpendingApproval.For(estimate, writer, payer);
        approval.Require(estimate, writer, payer);
```

   followed by `PublicationEstimator.RequireBatch(approval.AtCeiling(estimate), options.BudgetDrops);`: the balances must cover what the run may spend under the approval, not what the latest unit alone would cost.

   and `AttachBatchAsync(strategyFactory, estimate, approval.Budget(estimate), resuming: false)`. Both approval checks of the starts come before the hashing of Task 3, so a refused run reads nothing of the file.
4. One-account `ResumeAsync`, after its `PublicationEstimator.Require(...)`:

```csharp
        long missing = ChunkReads.ChunkCount(file.Length) - session._landed.Chunks.Landed;
        SpendingApproval approval = options.ApproveResume is { } approve
            ? await approve(new ResumeQuote(state.Writer, null, missing, estimate, null), cancellationToken).ConfigureAwait(false)
            : SpendingApproval.For(estimate, state.Writer);
        approval.Require(estimate, state.Writer);
```

5. Batch `ResumeAsync`, in place of the existing `PublicationEstimator.RequireBatch(estimate, options.BudgetDrops);` (the quote is priced from the estimate, then balances are checked at the approved ceiling as in item 3):

```csharp
        SpendingApproval approval = options.ApproveResume is { } approve
            ? await approve(new ResumeQuote(state.Writer, payer, missingChunks, null, estimate), cancellationToken).ConfigureAwait(false)
            : SpendingApproval.For(estimate, state.Writer, payer);
        approval.Require(estimate, state.Writer, payer);
        PublicationEstimator.RequireBatch(approval.AtCeiling(estimate), options.BudgetDrops);

        await session.AttachBatchAsync(strategyFactory, estimate, approval.Budget(estimate), resuming: true).ConfigureAwait(false);
```

   The payer change (B18) stays before the estimate, as today: it is written only after the fence, and a declined approval leaves the payer the person chose recorded, which is what they asked for. The state is otherwise unchanged by a declined approval (the test passes the recorded payer, so nothing is written).

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/XrplFile.Core.Tests -- -class XrplFile.Tests.SpendingApprovalTests`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishErrorMessagesTests -class XrplFile.App.Core.Tests.ReadErrorMessagesTests`
Run: both unit suites with `-trait- "Category=Integration"`.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src app tests
git commit -m "feat(core): sessions spend only what was approved

SpendingApproval names the writer, the payer, the fee-unit ceiling and each
account's worst-case ceiling. A fresh run takes it from its caller and a
resume asks for one after reconciliation with a quote of the remaining work;
a Batch run refuses only a fee unit above the approved ceiling or a worst
case, taken at that ceiling, above an account's approved figure, so a unit
that rises under the ceiling still runs. Refusals stop with ApprovalExceeded
before anything is sent, and a Batch strategy's budget is the approval's."
```

---

### Task 12: App — the file is held from the estimate on, and nothing reads it whole (S24, S7, spec 5.1–5.4)

**Files:**
- Modify: `app/XrplFile.App.Core/PublishRequest.cs`, `app/XrplFile.App.Core/PublishModel.cs` (`BuildRequestAsync`), `app/XrplFile.App.Core/PublishPolicy.cs` (`Effective`), `app/XrplFile.App.Core/EstimateModel.cs` (open, hold, hand off, discard; approval), `app/XrplFile.App.Core/PublicationRun.cs` (take the source, open on resume, dispose, rejected sources, source overloads, `Approval`), `app/XrplFile.App.Core/UnfinishedModel.cs` (no reads)
- Modify: `app/XrplFile.App/Components/Estimate.razor` (size, hand-off, `IAsyncDisposable`)
- Create: `tests/XrplFile.App.Core.Tests/FakeFiles.cs`, `tests/XrplFile.App.Core.Tests/Requests.cs`
- Modify (tests): every `new PublishRequest(bytes, ...)` the compiler names — `PublicationRunTests`, `PublicationRunBatchTests`, `EstimateModelTests`, `PublishPolicyTests`, `PublishModelTests`, `PublishModelPayerTests`, `UnfinishedModelTests`, `UnfinishedModelBatchTests`, `PublicationRunStandaloneTests`, `PublishedFileFixture`, `RunModelTests`, `RunModelEtaTests`
- Test: `tests/XrplFile.App.Core.Tests/HeldSourceTests.cs`, `tests/XrplFile.App.Core.Tests/FailingSecondReadSource.cs`

**Interfaces:**
- Consumes: Task 1 (`IPublicationSource`, `FilePublicationSource.Open`, `MemoryPublicationSource`), Task 3 (session source overloads with `preparation`; `XrplFileException.SourceChanged`, set only on a resume's size or root refusal), Task 11 (`SpendingApproval`, `SessionOptions.Approval`, `SpendingApproval.AtCeiling`).
- Produces:
  - `public sealed record PublishRequest(long Size, string Type, string Name, string KeyReference, string Account, string Network, string? PayerReference = null, string? PayerAccount = null, string? SourcePath = null)` with init members `WriterLabel`, `PayerLabel` (as today), `IPublicationSource? Source`, `SpendingApproval? Approval`, `bool SourceRecorded`.
  - `EstimateModel(IConnectionFactory connections, RunPaths paths, Func<string, IPublicationSource>? open = null)`; `SpendingApproval? Approval`; `PublishRequest? HandOff()`; `Task DiscardAsync()`.
  - `PublicationRun` gains `Func<string, IPublicationSource>? openSource = null` as its last constructor parameter (Task 16 appends `IKeepAwake? keepAwake = null` after it; Task 17 builds the activity lease from the `VaultAutoLock` it already takes). `Start(request)` requires `request.Source` and `request.Approval` (`ArgumentException` otherwise) and, when it returns true, owns the source; `Resume(statePath, request)` opens `request.SourcePath` itself. `bool HasRejectedSource(string statePath)`.
  - Tests: `FakeFiles` (path → bytes; `Open(path)` returns a `CountingPublicationSource` and remembers it in `Opened`), `Requests.Fresh(byte[] bytes, string type, string name, string keyReference, string account, string network, string? payerReference = null, string? payerAccount = null, string? sourcePath = null)` (a request with a `MemoryPublicationSource` and an unbounded approval).

**What owns the source when (spec S24):** `EstimateModel` opens it when it estimates and holds it; a new estimate, `DiscardAsync` (Back, leaving the screen) or an estimate that finishes after it was discarded dispose it. `HandOff()` gives it to the request; `PublicationRun.Start` returning true takes it, and the run disposes it when it ends by any path. `Start` returning false leaves it with the caller (Estimate.razor disposes it). A resume's source is opened by the run and disposed by it.

- [ ] **Step 1: Test helpers**

`tests/XrplFile.App.Core.Tests/FakeFiles.cs`:

```csharp
using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

/// <summary>Files by path, opened as sources that count their reads and remember their disposal.</summary>
public sealed class FakeFiles
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CountingPublicationSource> _opened = [];

    public IReadOnlyList<CountingPublicationSource> Opened
    {
        get { lock (_opened) return [.. _opened]; }
    }

    public string Add(string path, byte[] bytes)
    {
        _files[path] = bytes;
        return path;
    }

    public IPublicationSource Open(string path)
    {
        if (!_files.TryGetValue(path, out byte[]? bytes)) throw new FileNotFoundException("fake: no such file", path);

        CountingPublicationSource source = new(new MemoryPublicationSource(bytes));
        lock (_opened) _opened.Add(source);
        return source;
    }
}
```

`tests/XrplFile.App.Core.Tests/Requests.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

public static class Requests
{
    /// <summary>A request as the Estimate screen hands it to a run: the bytes as a held source, and an approval that bounds nothing.</summary>
    public static PublishRequest Fresh(
        byte[] bytes, string type, string name, string keyReference, string account, string network,
        string? payerReference = null, string? payerAccount = null, string? sourcePath = null) =>
        new(bytes.Length, type, name, keyReference, account, network, payerReference, payerAccount, sourcePath)
        {
            Source = new MemoryPublicationSource(bytes),
            Approval = new SpendingApproval(account, payerAccount, UnboundedFeeDrops, long.MaxValue, payerAccount is null ? 0 : long.MaxValue),
        };

    /// <summary>
    /// A fee-unit ceiling no test node reaches. Not <see cref="long.MaxValue"/>: the session prices the
    /// worst cases at the approved ceiling (Task 11), and that product must stay a number of drops.
    /// </summary>
    public const long UnboundedFeeDrops = 1_000;
}
```

`tests/XrplFile.App.Core.Tests/FailingSecondReadSource.cs`:

```csharp
namespace XrplFile.App.Core.Tests;

/// <summary>A held file that fails the second time one chunk is read: hashing reads it once, sending reads it again.</summary>
public sealed class FailingSecondReadSource(IPublicationSource inner, long failingChunk) : IPublicationSource
{
    private int _reads;

    public long Length => inner.Length;

    public int ReadChunk(long index, Span<byte> destination)
    {
        Check(index);
        return inner.ReadChunk(index, destination);
    }

    public ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Check(index);
        return inner.ReadChunkAsync(index, destination, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Check(long index)
    {
        if (index == failingChunk && Interlocked.Increment(ref _reads) >= 2) throw new IOException("fake: the drive the file was on went away");
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/XrplFile.App.Core.Tests/HeldSourceTests.cs`:

```csharp
using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class HeldSourceTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-held-").FullName;
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFiles _files = new();

    private RunPaths Paths => new(_directory);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private PublishRequest Picked(string path, int size) =>
        new(size, "application/octet-stream", "file.bin", "ref-1", "rWriter", "standalone", SourcePath: _files.Add(path, new byte[size]));

    private EstimateModel Model(IConnectionFactory? connections = null) => new(connections ?? _connections, Paths, _files.Open);

    [Fact]
    public async Task The_estimate_holds_the_file_and_a_second_estimate_lets_the_first_go()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();

        await model.EstimateAsync(Picked("a.bin", 4_096), Token);
        await model.EstimateAsync(Picked("b.bin", 4_096), Token);

        Assert.Equal(2, _files.Opened.Count);
        Assert.True(_files.Opened[0].Disposed);
        Assert.False(_files.Opened[1].Disposed);
        Assert.Equal(0, _files.Opened[1].Reads);
    }

    [Fact]
    public async Task Discarding_the_estimate_lets_the_file_go()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();
        await model.EstimateAsync(Picked("a.bin", 4_096), Token);

        await model.DiscardAsync();

        Assert.True(Assert.Single(_files.Opened).Disposed);
        Assert.Null(model.HandOff());
    }

    [Fact]
    public async Task Handing_off_gives_the_run_the_held_file_and_its_approval()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        EstimateModel model = Model();
        await model.EstimateAsync(Picked("a.bin", 4_096), Token);

        PublishRequest handed = model.HandOff()!;
        await model.DiscardAsync();

        Assert.Same(_files.Opened[0], handed.Source);
        Assert.False(_files.Opened[0].Disposed);
        Assert.Equal(SpendingApproval.For(model.Estimate!, "rWriter"), handed.Approval);
        Assert.Equal(4_096, handed.Size);
    }

    [Fact]
    public async Task Leaving_while_the_estimate_is_still_being_taken_lets_the_file_go_when_it_finishes()
    {
        _connections.Ledger.Balances["rWriter"] = 100_000_000;
        GatedConnections gated = new(_connections);
        EstimateModel model = Model(gated);

        Task estimating = model.EstimateAsync(Picked("a.bin", 4_096), Token);
        await gated.Reached.WaitAsync(Token);
        await model.DiscardAsync();
        gated.Release();
        await estimating;

        Assert.True(Assert.Single(_files.Opened).Disposed);
        Assert.Null(model.HandOff());
    }

    [Fact]
    public async Task A_run_disposes_the_file_it_was_handed_when_it_ends()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();
        CountingPublicationSource held = (CountingPublicationSource)request.Source!;

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.True(held.Disposed);
    }

    [Fact]
    public async Task A_cancelled_run_disposes_the_file_too()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();
        CountingPublicationSource held = (CountingPublicationSource)request.Source!;
        _connections.ConnectThrows = new OperationCanceledException();

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.True(held.Disposed);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_mid_run_says_so_and_stays_resumable()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);

        Assert.True(run.Start(request with { Source = new FailingSecondReadSource(request.Source!, failingChunk: 10) }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Equal($"The file at {request.SourcePath} could not be read while it was being published. Make it available again and resume the run.", run.Snapshot.Failure);
        Assert.True(File.Exists(run.Snapshot.StatePath));
        Assert.True(RunModel.IsResumable(run.Snapshot));
    }

    [Fact]
    public async Task A_resume_refused_for_another_reason_than_a_changed_file_does_not_mark_the_file()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);
        _connections.Sink.Throws[5] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: the node went away");
        Assert.True(run.Start(request));
        await run.Completion;
        string statePath = run.Snapshot.StatePath!;

        // A state recorded for another chunk size is refused with ResumeMismatch, and the file is not to blame.
        PublicationState recorded = PublicationState.Load(statePath);
        (recorded with { Phase = "chunks", Chunk = 999 }).Save(statePath);
        run.Reset();

        Assert.True(run.Resume(statePath, request with { Source = null, SourceRecorded = true }));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.False(run.HasRejectedSource(statePath));
    }

    [Fact]
    public async Task A_fresh_run_needs_a_held_file_and_an_approval()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync();

        Assert.Throws<ArgumentException>(() => run.Start(request with { Source = null }));
        Assert.Throws<ArgumentException>(() => run.Start(request with { Approval = null }));
    }

    [Fact]
    public async Task A_resume_opens_the_recorded_file_through_the_run_and_reads_it_as_a_stream()
    {
        (PublicationRun run, PublishRequest request) = await RunReadyAsync(size: 30_000);
        _connections.Sink.Throws[5] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: the node went away");
        Assert.True(run.Start(request));
        await run.Completion;
        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        string statePath = run.Snapshot.StatePath!;

        UnfinishedModel unfinished = new(Paths, run, new FakeFilePicker());
        UnfinishedRow row = Assert.Single((await RefreshedAsync(unfinished)).Rows);
        Assert.True(await unfinished.ResumeAsync(row, Token));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        CountingPublicationSource reopened = _files.Opened[^1];
        Assert.True(reopened.Disposed);
        Assert.True(reopened.Reads > 0);
        Assert.Equal(statePath, row.Path);
    }

    private static async Task<UnfinishedModel> RefreshedAsync(UnfinishedModel model)
    {
        await model.RefreshAsync(Token);
        return model;
    }

    private async Task<(PublicationRun Run, PublishRequest Request)> RunReadyAsync(int size = 4_096)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed), Token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        // On disk for UnfinishedModel's checks of the recorded path, and in FakeFiles for the run, which counts its reads.
        string path = Path.Combine(_directory, "file.bin");
        byte[] bytes = VectorStream.Bytes("held", size);
        await File.WriteAllBytesAsync(path, bytes, Token);
        _files.Add(path, bytes);

        PublicationRun run = new(_connections, vault, Paths, new PublicationHistory(Paths.HistoryPath), openSource: _files.Open);
        PublishRequest request = Requests.Fresh(bytes, "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1", sourcePath: path)
            with { Source = _files.Open(path) };

        return (run, request);
    }

    /// <summary>Holds the first connection until released, so a test can act while an estimate is in flight.</summary>
    private sealed class GatedConnections(IConnectionFactory inner) : IConnectionFactory
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ConnectAsync(network, cancellationToken);
        }
    }
}
```

`FakeFilePicker` already exists in the App.Core tests. `FakeLedgerSink.Throws` fails the submission at a position, which leaves the run unfinished with its first chunks landed. If `IConnectionFactory` has members beyond `ConnectAsync`, forward them to `inner`.

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.HeldSourceTests`
Expected: build errors (`PublishRequest(long, …)`, `Source`, `Approval`, `HandOff`, `DiscardAsync`, the `openSource` and `open` parameters).

- [ ] **Step 4: The request carries a size, the held source and the approval**

`app/XrplFile.App.Core/PublishRequest.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Everything a run needs, settled before any node is contacted. It carries no bytes (spec 5.1): a
/// fresh run gets the file held open since its estimate in <see cref="Source"/>, a resume opens the
/// recorded <see cref="SourcePath"/> itself.
/// </summary>
/// <param name="Size">The file's length in bytes, as the source that is published has it.</param>
/// <param name="PayerReference">The vault key that pays for a Batch run, or null for a one-account run (spec 6.1).</param>
/// <param name="PayerAccount">That key's classic address.</param>
/// <param name="SourcePath">Where the file is, recorded so a resume need not ask for it (spec B17).</param>
public sealed record PublishRequest(
    long Size,
    string Type,
    string Name,
    string KeyReference,
    string Account,
    string Network,
    string? PayerReference = null,
    string? PayerAccount = null,
    string? SourcePath = null)
{
    /// <summary>The writer key's label, for the estimate card; null when not known.</summary>
    public string? WriterLabel { get; init; }

    /// <summary>The payer key's label, for the estimate card and the method line; null when not known.</summary>
    public string? PayerLabel { get; init; }

    /// <summary>
    /// The file held open from the estimate on (spec S24), so what was approved is what is published.
    /// A run that <see cref="PublicationRun.Start"/> accepts owns it from then on.
    /// </summary>
    public IPublicationSource? Source { get; init; }

    /// <summary>What the person approved on the Estimate screen (spec S25); a fresh run needs one.</summary>
    public SpendingApproval? Approval { get; init; }

    /// <summary>Whether <see cref="SourcePath"/> is the path the interrupted run recorded, rather than one just picked.</summary>
    public bool SourceRecorded { get; init; }
}
```

`PublishPolicy.Effective`: `TooSmallForBatch(request.File.Length)` → `TooSmallForBatch(request.Size)`.

`PublishModel.BuildRequestAsync`: replace the `byte[] bytes … File.ReadAllBytesAsync …` block and the empty check with

```csharp
        long size;

        try
        {
            // Measured again rather than remembered: the file may have changed since it was picked. Its
            // bytes are not read here: the estimate opens and holds it (spec S24).
            size = new FileInfo(FilePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return null;
        }

        if (size == 0)
        {
            Message = "That file is empty, and an empty file has nothing to publish.";
            return null;
        }
```

and construct `new PublishRequest(size, TypeOf(FileName!), FileName!, …, FilePath)`. Update the class remarks that mention reading the file.

- [ ] **Step 5: `EstimateModel` opens, holds, hands off and discards**

In `app/XrplFile.App.Core/EstimateModel.cs`, change the primary constructor to `EstimateModel(IConnectionFactory connections, RunPaths paths, Func<string, IPublicationSource>? open = null)` and add:

```csharp
    private readonly Func<string, IPublicationSource> _open = open ?? (path => FilePublicationSource.Open(path));
    private readonly Lock _gate = new();
    private IPublicationSource? _held;

    /// <summary>
    /// Bumped by every estimate, hand-off and discard: an estimate keeps the source it opened only
    /// when nothing of the kind happened while it ran, so a screen left mid-estimate leaves no file open.
    /// </summary>
    private int _generation;

    /// <summary>What a run started from this estimate may spend (spec S25); null while the estimate cannot be published.</summary>
    public SpendingApproval? Approval { get; private set; }

    /// <summary>
    /// The estimated request with the held file and the approval, for a run to take (spec S24, S25).
    /// The model lets go of the file: from here on it is the caller's, and a discard does not touch it.
    /// Null when there is nothing to publish.
    /// </summary>
    public PublishRequest? HandOff()
    {
        lock (_gate)
        {
            if (_held is not { } source || Request is not { } request || !CanPublish || Approval is not { } approval) return null;

            _held = null;
            _generation++;
            return request with { Source = source, Approval = approval };
        }
    }

    /// <summary>Lets the held file go (spec S24): Back, another file, another key, or leaving the screen.</summary>
    public async Task DiscardAsync()
    {
        IPublicationSource? held;

        lock (_gate)
        {
            held = _held;
            _held = null;
            _generation++;
        }

        if (held is not null) await held.DisposeAsync().ConfigureAwait(false);
    }
```

`EstimateAsync` becomes:

```csharp
    public async Task EstimateAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int generation;
        IPublicationSource? previous;

        lock (_gate)
        {
            generation = ++_generation;
            previous = _held;
            _held = null;
        }

        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);

        Estimate = null;
        NodeUrl = null;
        Message = null;
        BatchEstimate = null;
        MethodLine = null;
        Approval = null;

        if (request.SourcePath is not { } path)
        {
            throw new ArgumentException("an estimate is of a file on disk, which it holds until the run takes it", nameof(request));
        }

        IPublicationSource source;

        try
        {
            source = _open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "That file could not be read.";
            return;
        }

        bool kept = false;

        try
        {
            // The size priced is the size of what is held, whatever the picker measured earlier.
            await EstimateHeldAsync(request with { Size = source.Length }, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (generation == _generation)
                {
                    _held = source;
                    kept = true;
                }
            }
        }
        finally
        {
            if (!kept) await source.DisposeAsync().ConfigureAwait(false);
        }
    }
```

where `EstimateHeldAsync(PublishRequest request, CancellationToken)` is today's body of `EstimateAsync` from `PublishRequest effective = PublishPolicy.Effective(request);` on, with `request.File.Length` replaced by `request.Size`, and at each successful end (after the shortfall message is set, in both the one-account and the Batch branch):

```csharp
        if (CanPublish)
        {
            Approval = BatchEstimate is { } batch
                ? SpendingApproval.For(batch, Request!.Account, Request.PayerAccount!)
                : SpendingApproval.For(Estimate!, Request!.Account);
        }
```

(Task 13 gates this approval behind the confirmation.)

- [ ] **Step 6: The run takes the source, opens it on resume, and disposes it**

In `app/XrplFile.App.Core/PublicationRun.cs`:

1. Constructor: add `Func<string, IPublicationSource>? openSource = null` as the last parameter, with a field `private readonly Func<string, IPublicationSource> _openSource = openSource ?? (path => FilePublicationSource.Open(path));` and `private readonly HashSet<string> _rejectedSources = new(StringComparer.OrdinalIgnoreCase);` guarded by `_gate`.

2. `Start`:

```csharp
    /// <summary>
    /// Starts a publication with the file the estimate held and the approval it made (spec S24, S25).
    /// <see langword="false"/> when one is already going; the request's source then stays the caller's.
    /// On <see langword="true"/> the run owns the source and disposes it when it ends.
    /// </summary>
    public bool Start(PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Source is null) throw new ArgumentException("a fresh run publishes the file its estimate held", nameof(request));
        if (request.Approval is null) throw new ArgumentException("a fresh run spends what its estimate approved", nameof(request));

        return Begin(request, statePath: null);
    }
```

3. `Resume`: unchanged signature; when it returns true for a request with `SourceRecorded == false`, remove the state path from `_rejectedSources` (a picked file is a fresh try).

4. Add:

```csharp
    /// <summary>
    /// Whether the last resume of this run found the file at its recorded path changed (spec B17): the
    /// next resume then asks for the file instead of trying the same path again.
    /// </summary>
    public bool HasRejectedSource(string statePath)
    {
        lock (_gate) return _rejectedSources.Contains(Path.GetFullPath(statePath));
    }
```

5. `Begin`: `_fileBytes = request.Size;`.

6. `RunAsync`: open or take the source before anything else, and dispose it on every way out:

```csharp
        IPublicationSource? source = request.Source;

        try
        {
            paths.EnsureCreated();
            source ??= OpenRecorded(request, statePath!);
            // ... the rest of today's try body, passing `source` on ...
        }
        // ... today's catch clauses, plus, before the generic XrplFileException clause:
        catch (SourceUnreadableException ex)
        {
            failure = $"The file at {ex.Path} could not be read. Choose it again.";
            finalState = PublicationRunState.Failed;
        }
        catch (SourceReadFailedException ex)
        {
            // Not the state file: the generic IOException clause below says the state could not be
            // written, which would send the person after the wrong thing. The state is intact, so the
            // run stays resumable once the file is back.
            failure = ex.Path is null
                ? "The file could not be read while it was being published. Make it available again and resume the run."
                : $"The file at {ex.Path} could not be read while it was being published. Make it available again and resume the run.";
            finalState = PublicationRunState.Failed;
        }
        catch (XrplFileException ex) when (ex is { Error: XrplFileError.ResumeMismatch, SourceChanged: true } && statePath is not null)
        {
            // Only a size or root refusal blames the file; a finished run, another chunk size or
            // another writer is refused with ResumeMismatch too, and would open the picker for nothing.
            lock (_gate) _rejectedSources.Add(Path.GetFullPath(statePath));
            failure = request.SourceRecorded
                ? $"The file at {request.SourcePath} has changed since the run started. Choose the original file."
                : "That file is not the one this run was publishing.";
            finalState = PublicationRunState.Failed;
        }
```

   and in a `finally` around the whole try (before `WriteTraceAsync`): `if (source is not null) await DisposeQuietlyAsync(source).ConfigureAwait(false);`. Both new clauses go before today's `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`.

   The source is passed on wrapped, so a failed read of the file is told apart from a failed write of the state: right after `source ??= OpenRecorded(...)`, `IPublicationSource reading = new ReadReportingSource(source, request.SourcePath);`, and `reading` is what the session gets. The run still disposes `source` itself; the wrapper owns nothing.

```csharp
    /// <summary>Turns a failed read of the held file into <see cref="SourceReadFailedException"/>, so the run names the file and not the state.</summary>
    private sealed class ReadReportingSource(IPublicationSource inner, string? path) : IPublicationSource
    {
        public long Length => inner.Length;

        public int ReadChunk(long index, Span<byte> destination)
        {
            try
            {
                return inner.ReadChunk(index, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceReadFailedException(path, ex);
            }
        }

        public async ValueTask<int> ReadChunkAsync(long index, Memory<byte> destination, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.ReadChunkAsync(index, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceReadFailedException(path, ex);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Local control flow: the held file failed a read during the run (a drive removed, a share gone, the file cut short).</summary>
    private sealed class SourceReadFailedException(string? path, Exception inner) : Exception(path, inner)
    {
        public string? Path { get; } = path;
    }
```

```csharp
    /// <summary>A resume reads the file the run recorded, held the same way an estimate holds it (spec S7, S24).</summary>
    private IPublicationSource OpenRecorded(PublishRequest request, string statePath)
    {
        string path = request.SourcePath ?? throw new InvalidOperationException("a resume's request names the file to read");

        try
        {
            return _openSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _rejectedSources.Add(Path.GetFullPath(statePath));
            throw new SourceUnreadableException(path, ex);
        }
    }

    /// <summary>Local control flow, like <see cref="FeeTooLargeForApplicationException"/>: the recorded file could not be opened.</summary>
    private sealed class SourceUnreadableException(string path, Exception inner) : Exception(path, inner)
    {
        public string Path { get; } = path;
    }
```

7. `RunOneAccountAsync` and `RunBatchAsync` take `IPublicationSource source` and use the session's source overloads: `PublicationSession.EstimateAsync(connection.Source, request.Account, source.Length, …)`, `PublicationSession.StartAsync(probe, connection.Source, source, request.Type, request.Name, request.Account, request.KeyReference, request.Network, options, new SynchronousProgress(Observe), cancellationToken)`, `PublicationSession.ResumeAsync(probe, connection.Source, source, statePath, options, new SynchronousProgress(Observe), cancellationToken)`, and the Batch equivalents (`StartAsync(Build, connection.Source, source, …, options, new SynchronousProgress(Observe), cancellationToken)`, `ResumeAsync(Build, connection.Source, source, statePath, payer, payerReference, options, new SynchronousProgress(Observe), cancellationToken)`). Both fresh-start `SessionOptions` get `Approval = request.Approval`. Task 8's trace header uses `source.Length` for `bytes=`.

- [ ] **Step 7: `UnfinishedModel` reads nothing**

Replace `RecordedSourceAsync`, `IsTheRunsFile` and `ResumeWithAsync` with:

```csharp
    /// <summary>
    /// Why the recorded file cannot be used (spec B17), or null when it can be tried: it has gone, it
    /// cannot be opened, its size changed, or the last resume found its bytes changed. Nothing is
    /// read: the run opens the file and checks its root as it hashes it (spec S7).
    /// </summary>
    private string? RecordedSourceProblem(UnfinishedRow row, string source)
    {
        if (!File.Exists(source)) return $"The file this run was publishing is no longer at {source}. Choose it again.";

        if (run.HasRejectedSource(row.Path)) return $"The file at {source} has changed since the run started. Choose the original file.";

        try
        {
            if (new FileInfo(source).Length != row.Size) return $"The file at {source} has changed since the run started. Choose the original file.";

            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The file at {source} could not be read. Choose it again.";
        }

        return null;
    }

    private async Task<bool> ResumeWithAsync(UnfinishedRow row, string? payerReference, string? payerAccount, CancellationToken cancellationToken)
    {
        string? sourcePath = row.SourcePath;
        bool recorded = sourcePath is not null && (Message = RecordedSourceProblem(row, sourcePath)) is null;

        if (!recorded)
        {
            string? path = await picker.PickExistingFilePathAsync(cancellationToken).ConfigureAwait(false);
            if (path is null) return false;

            long size;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Message = "That file could not be read.";
                return false;
            }

            // Checked here so the person hears "that is not the same file" from the screen rather than
            // from a run that starts and refuses. The run checks the root, which is what binds.
            if (size != row.Size)
            {
                Message =
                    $"That file is {size:N0} bytes and this run was publishing {row.Size:N0}. "
                    + "Resuming needs the same file the run started with.";
                return false;
            }

            sourcePath = path;
        }

        Message = null;

        // The key, the type, the writer and the network come from the state file, as the session takes them.
        PublishRequest request = new(
            row.Size,
            row.Type ?? "application/octet-stream",
            row.Name ?? Path.GetFileName(sourcePath ?? row.Path),
            row.KeyReference!,
            row.Writer!,
            row.Network!,
            row.IsBatch ? payerReference : null,
            row.IsBatch ? payerAccount : null,
            sourcePath)
        {
            SourceRecorded = recorded,
        };

        if (!run.Resume(row.Path, request))
        {
            Message = "A publication is already running.";
            return false;
        }

        return true;
    }
```

  `ResumeWithAsync` is no longer `async` where nothing awaits outside the picker; keep it `async` for the picker call. The `using` of the handle opens and closes the file once, without reading it.

- [ ] **Step 8: `Estimate.razor`**

- `@implements IDisposable` → `@implements IAsyncDisposable`.
- `@Request.File.Length.ToString("N0")` → `@((Model.Request ?? Request).Size.ToString("N0"))`.
- `PublishAsync`:

```csharp
            PublishRequest? request = Model.HandOff();
            if (request is null) return;

            if (Run.Start(request)) Picker.RecordStarted(request);
            else await request.Source!.DisposeAsync();

            await OnStarted.InvokeAsync();
```

- `Dispose()` → 

```csharp
    public async ValueTask DisposeAsync()
    {
        _lifetime.End();

        // Leaving the screen lets the held file go (spec S24); a run that took it already owns it.
        await Model.DiscardAsync();
    }
```

- [ ] **Step 9: Migrate the existing App.Core tests**

| Old | New |
|---|---|
| `new PublishRequest(new byte[size] or bytes, type, name, key, account, network[, payer…][, path])` passed to `run.Start` | `Requests.Fresh(bytes, type, name, key, account, network[, payer…][, sourcePath: path])` |
| `new PublishRequest(bytes, …)` passed to `EstimateModel.EstimateAsync` | `new PublishRequest(bytes.Length, …, SourcePath: files.Add("<name>", bytes))` with the model built as `new EstimateModel(connections, paths, files.Open)` (`FakeFiles files = new();` in the class) |
| `new PublishRequest(bytes, …)` passed to `run.Resume` | write the bytes to a file under the test's directory and pass `new PublishRequest(bytes.Length, …, sourcePath)`; or build the run with `openSource: files.Open` |
| `request.File.Length` | `request.Size` |
| `PublishPolicy.Effective(new PublishRequest(new byte[n], …))` | `PublishPolicy.Effective(new PublishRequest(n, …))` |

`UnfinishedModelTests` that resumed by reading bytes keep their files on disk (they already write them) and now see the run open them; the "changed" case is the same message for a size change, and for a root change the run fails with "The file at … has changed since the run started. Choose the original file." and the next `ResumeAsync` opens the picker (assert `run.HasRejectedSource(row.Path)` and the picker being asked).

- [ ] **Step 10: Build and run**

Run: `dotnet build XrplFile.Libraries.slnf` and `dotnet build app/XrplFile.App/XrplFile.App.csproj`
Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`
Expected: no warnings; PASS, including `HeldSourceTests`.

- [ ] **Step 11: Commit**

```bash
git add app tests
git commit -m "feat(app): hold the file from the estimate on and never read it whole

PublishRequest carries the file's size, the source the Estimate screen opened
and held, and the approval it made, never bytes. The estimate lets the file
go on Back, on another estimate and on leaving the screen, also when it was
still being taken; the run takes it over and disposes it however it ends. A
resume opens the recorded file itself, hashes it as a stream, and on a changed
file asks for it next time. No ReadAllBytes is left on the publication path."
```

---

### Task 13: App — the duration range, the cost summary and its checkbox (S11, S12, S25 fresh)

**Files:**
- Create: `app/XrplFile.App.Core/PublicationDuration.cs`, `app/XrplFile.App.Core/DurationText.cs`, `app/XrplFile.App.Core/CostSummary.cs`
- Modify: `app/XrplFile.App.Core/EstimateModel.cs` (summary, confirmation, approval gated), `app/XrplFile.App.Core/RunModel.cs` (its private `Duration` moves to `DurationText`)
- Modify: `app/XrplFile.App/Components/Estimate.razor`
- Test: `tests/XrplFile.App.Core.Tests/PublicationDurationTests.cs`, `tests/XrplFile.App.Core.Tests/CostSummaryTests.cs`, `tests/XrplFile.App.Core.Tests/EstimateModelConfirmationTests.cs`, `tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs` (one test)

**Interfaces:**
- Consumes: Task 7 (`PublicationCheck.SampleBlocks`' sizes: one block → 1, up to four → all, else 4), Task 10 (`BatchPublicationEstimate.Rounds`), Task 11 (`SpendingApproval`, `ApprovalExceeded`), Task 12 (`EstimateModel` holding, `HandOff`, `PublishRequest.Size`, `FakeFiles`, `Requests.Fresh`, and `PublicationRunBatchTests.ReadyAsync` as Task 12 migrated it).
- Produces:
  - `public readonly record struct DurationRange(TimeSpan Shortest, TimeSpan Longest)`.
  - `public static class PublicationDuration` — `const double BatchFastMemosPerSecond = 85`, `BatchSlowMemosPerSecond = 70`, `TicketFastMemosPerSecond = 2`, `TicketSlowMemosPerSecond = 1`, `ReaderSecondsPer400Records = 0.7`, `const int RecordsPerSampledBlock = 1_024`; `static TimeSpan SettlePerRound` (= `LedgerTiming.ApproximateLedgerInterval * 3`); `static int SampledBlocks(int blocks)`; `static TimeSpan Reading(long records)`; `static DurationRange ForBatch(BatchPublicationEstimate estimate)`; `static DurationRange ForOneAccount(PublicationEstimate estimate)`; `static TimeSpan ForRead(Manifest manifest)`.
  - `public static class DurationText` — `static string Of(TimeSpan span)` (RunModel's rule, moved), `static string Of(DurationRange range)` ("{shortest} to {longest}", or one figure when they read the same).
  - `public sealed record AccountCost(string Role, string Name, long RequiredDrops, long WorstCaseFeeDrops)`; `public sealed record CostSummary(DurationRange Duration, long Transactions, IReadOnlyList<AccountCost> Accounts)` — `static readonly TimeSpan ConfirmationDuration` (1 h), `const long ConfirmationDrops = 1_000_000`, `bool NeedsConfirmation`, `static CostSummary Of(PublicationEstimate, PublishRequest)`, `static CostSummary Of(BatchPublicationEstimate, PublishRequest)`.
  - `EstimateModel.Summary : CostSummary?`, `NeedsConfirmation`, `Confirmed`, `void Confirm(bool understood)`, `const string ConfirmationLabel = "I understand the cost and the time"`. `CanPublish` and `Approval` require the confirmation when it is needed.

**Durations (spec S11), as implemented:**
- **Batch:** writing `Memos / 85` to `Memos / 70` seconds; plus `Rounds × 3 × LedgerTiming.ApproximateLedgerInterval` (12 s a round); plus the sample's read: `SampledBlocks(Blocks) × 1,024` records (capped at `Chunks`), at 0.7 s per 400 records. The same addends go into both ends.
- **Tickets** (`PublishPolicy.UsesTickets`): `Memos / 2` to `Memos / 1` seconds, plus a full read-back of `Chunks + Lists` records at the reader rate.
- **Sequence:** `Memos × ApproximateLedgerInterval`, plus the same full read-back; both ends equal.
- `Memos` is chunks + lists + manifest (`PublishPolicy.MemoCount` for one-account estimates, `Memos` for Batch).

**The summary (spec S12):** duration range; transactions (`Transactions` one-account; `Memos + Batches` for Batch: every inner, solo and outer); each account's required balance and its fees in the worst case. Shown, with the checkbox, when the longer duration exceeds 1 hour or either account's worst-case fees exceed 1 XRP (Spec question 2). The checkbox resets on every new estimate (another file, key, payer or network, or the same request estimated again) and on discard.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/PublicationDurationTests.cs`:

```csharp
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublicationDurationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_spec_example_of_a_1_7_GB_file_takes_about_six_to_seven_hours_through_Batch()
    {
        BatchPublicationEstimate estimate = await PublicationEstimator.EstimateBatchAsync(new FakeLedgerSource(), "rWriter", "rPayer", 1_689_809_534, Token);

        DurationRange range = PublicationDuration.ForBatch(estimate);

        // 1,689,977 memos at 85 and 70 a second, 106 rounds of 12 s, and four sampled blocks of 1,024 records at 0.7 s per 400.
        double fixedPart = 106 * 12 + 4 * 1_024 * 0.7 / 400;
        Assert.Equal(1_689_977 / 85.0 + fixedPart, range.Shortest.TotalSeconds, precision: 3);
        Assert.Equal(1_689_977 / 70.0 + fixedPart, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("5 h 53 min to 7 h 4 min", DurationText.Of(range));
    }

    [Fact]
    public async Task A_ticketed_file_is_priced_at_two_and_one_memos_a_second_with_a_full_read_back()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(new FakeLedgerSource(), "rWriter", 1_000_000, 100, Token);

        DurationRange range = PublicationDuration.ForOneAccount(estimate);

        Assert.Equal(1_001 / 2.0 + 1_000 * 0.7 / 400, range.Shortest.TotalSeconds, precision: 3);
        Assert.Equal(1_001 / 1.0 + 1_000 * 0.7 / 400, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("8 min to 17 min", DurationText.Of(range));
    }

    [Fact]
    public async Task A_sequence_file_is_one_memo_per_ledger()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(new FakeLedgerSource(), "rWriter", 50_000, 100, Token);

        DurationRange range = PublicationDuration.ForOneAccount(estimate);

        Assert.Equal(range.Shortest, range.Longest);
        Assert.Equal(51 * 4 + 50 * 0.7 / 400, range.Longest.TotalSeconds, precision: 3);
        Assert.Equal("3 min", DurationText.Of(range));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(1_651, 4)]
    public void The_sample_size_follows_the_writer(int blocks, int sampled)
    {
        Assert.Equal(sampled, PublicationDuration.SampledBlocks(blocks));
    }
}
```

`tests/XrplFile.App.Core.Tests/CostSummaryTests.cs`:

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class CostSummaryTests
{
    private static CostSummary Summary(TimeSpan longest, long worstCase) =>
        new(new DurationRange(longest / 2, longest), 10, [new AccountCost("Writer", "w", 2_000_000, worstCase)]);

    [Fact]
    public void An_hour_and_one_XRP_are_the_lines()
    {
        Assert.False(Summary(TimeSpan.FromHours(1), 1_000_000).NeedsConfirmation);
        Assert.True(Summary(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1), 1_000_000).NeedsConfirmation);
        Assert.True(Summary(TimeSpan.FromMinutes(5), 1_000_001).NeedsConfirmation);
    }

    [Fact]
    public void Either_account_crossing_the_line_is_enough()
    {
        CostSummary summary = new(new DurationRange(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)), 10,
            [new AccountCost("Payer", "p", 3_000_000, 1_000_001), new AccountCost("Writer", "w", 2_000_000, 5)]);

        Assert.True(summary.NeedsConfirmation);
    }
}
```

`tests/XrplFile.App.Core.Tests/EstimateModelConfirmationTests.cs`:

```csharp
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class EstimateModelConfirmationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-confirm-").FullName;
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFiles _files = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private EstimateModel NewModel() => new(_connections, new RunPaths(_directory), _files.Open);

    private PublishRequest Batch(string path, int size) =>
        new(size, "application/octet-stream", "f.bin", "ref-w", "rWriter", "standalone", "ref-p", "rPayer", _files.Add(path, new byte[size]))
        {
            WriterLabel = "writer",
            PayerLabel = "payer",
        };

    public EstimateModelConfirmationTests()
    {
        _connections.Ledger.Balances["rWriter"] = 1_000_000_000;
        _connections.Ledger.Balances["rPayer"] = 1_000_000_000;
    }

    [Fact]
    public async Task Below_the_threshold_nothing_needs_confirming()
    {
        EstimateModel model = NewModel();

        await model.EstimateAsync(Batch("small.bin", 9_000), Token);

        Assert.False(model.NeedsConfirmation);
        Assert.True(model.CanPublish);
        Assert.NotNull(model.Approval);
        Assert.Equal(["Payer", "Writer"], model.Summary!.Accounts.Select(account => account.Role));
    }

    [Fact]
    public async Task Above_the_threshold_Publish_waits_for_the_checkbox()
    {
        EstimateModel model = NewModel();

        // 5,000 chunks, one list and the manifest: 5,002 memos. At a 30-drop ceiling the payer's worst
        // case is ceil(5,002 x 7.5) = 37,515 units x 30 = 1,125,450 drops.
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);

        Assert.True(model.NeedsConfirmation);
        Assert.False(model.CanPublish);
        Assert.Null(model.Approval);
        Assert.Null(model.HandOff());

        model.Confirm(true);

        Assert.True(model.CanPublish);
        Assert.NotNull(model.HandOff());
    }

    [Fact]
    public async Task The_checkbox_resets_when_the_estimate_changes()
    {
        EstimateModel model = NewModel();
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);
        model.Confirm(true);

        await model.EstimateAsync(Batch("other.bin", 5_000_000), Token);

        Assert.False(model.Confirmed);
        Assert.False(model.CanPublish);
    }

    [Fact]
    public async Task The_checkbox_resets_when_the_estimate_is_discarded()
    {
        EstimateModel model = NewModel();
        await model.EstimateAsync(Batch("large.bin", 5_000_000), Token);
        model.Confirm(true);

        await model.DiscardAsync();

        Assert.False(model.Confirmed);
    }

    [Fact]
    public void The_label_is_the_specs()
    {
        Assert.Equal("I understand the cost and the time", EstimateModel.ConfirmationLabel);
    }
}
```

In `tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs`, add (spec 6.4: a ceiling exceeded by a re-estimate):

```csharp
    [Fact]
    public async Task A_run_whose_fee_unit_rose_past_the_approved_ceiling_is_refused_before_anything_is_sent()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync();
        BatchPublicationEstimate estimate = await PublicationSession.EstimateAsync(
            _connections.Ledger, request.Account, request.PayerAccount!, request.Size, new SessionOptions(), Token);
        PublishRequest approved = request with { Approval = SpendingApproval.For(estimate, request.Account, request.PayerAccount!) };

        // The Estimate screen approved a unit of 15 and a ceiling of 30; the node now asks 45.
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(30, 10);

        Assert.True(run.Start(approved));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        Assert.Contains("Estimate it again.", run.Snapshot.Failure, StringComparison.Ordinal);
        Assert.Empty(_connections.BatchSink!.Transmissions);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublicationDurationTests -class XrplFile.App.Core.Tests.CostSummaryTests -class XrplFile.App.Core.Tests.EstimateModelConfirmationTests -class XrplFile.App.Core.Tests.PublicationRunBatchTests`
Expected: build errors; once the types exist, `A_run_whose_fee_unit_rose…` already passes (Task 11 does the refusal; this pins it at the application's edge).

- [ ] **Step 3: Durations, their text and the summary**

`app/XrplFile.App.Core/DurationText.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>A span as the screens show it: seconds under a minute and a half, then the nearest minute, then hours and minutes.</summary>
public static class DurationText
{
    /// <summary>Rounded, not ceilinged: 64 s used to read "2 min".</summary>
    public static string Of(TimeSpan span)
    {
        double seconds = Math.Max(1, Math.Ceiling(span.TotalSeconds));
        if (seconds < 90) return $"{seconds:0} s";

        long minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"{minutes} min";

        long rest = minutes % 60;
        return rest == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {rest} min";
    }

    /// <summary>"5 h 53 min to 7 h 4 min", or one figure when both ends read the same.</summary>
    public static string Of(DurationRange range)
    {
        string shortest = Of(range.Shortest);
        string longest = Of(range.Longest);

        return string.Equals(shortest, longest, StringComparison.Ordinal) ? shortest : $"{shortest} to {longest}";
    }
}
```

`RunModel`'s private `Duration` is deleted and its callers use `DurationText.Of`.

`app/XrplFile.App.Core/PublicationDuration.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>How long a publication takes, as a range shown before a run (spec S11). The run corrects it with measured rates.</summary>
public readonly record struct DurationRange(TimeSpan Shortest, TimeSpan Longest);

public static class PublicationDuration
{
    /// <summary>Batch writing, fast end: the platform's measured best (spec S11).</summary>
    public const double BatchFastMemosPerSecond = 85;

    /// <summary>Batch writing, slow end: the platform's conservative figure, also the run screen's prior (spec B21).</summary>
    public const double BatchSlowMemosPerSecond = 70;

    /// <summary>The ticket path's measured testnet range (spec S11).</summary>
    public const double TicketFastMemosPerSecond = 2;

    public const double TicketSlowMemosPerSecond = 1;

    /// <summary>The reader's measured pace: 0.7 s per 400 records.</summary>
    public const double ReaderSecondsPer400Records = 0.7;

    /// <summary>A sampled block's chunks, assumed contiguous: about 1,024 records (spec S11).</summary>
    public const int RecordsPerSampledBlock = FileTree.BlockChunks;

    /// <summary>One round's settle: about three ledgers (spec S11).</summary>
    public static TimeSpan SettlePerRound { get; } = LedgerTiming.ApproximateLedgerInterval * 3;

    /// <summary>How many blocks the writer's sampled read takes (spec S21).</summary>
    public static int SampledBlocks(int blocks) => Math.Min(blocks, 4);

    /// <summary>Reading this many history records at the reader's pace.</summary>
    public static TimeSpan Reading(long records) => TimeSpan.FromSeconds(records * ReaderSecondsPer400Records / 400);

    public static DurationRange ForBatch(BatchPublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        TimeSpan settles = SettlePerRound * estimate.Rounds;
        TimeSpan verification = Reading(Math.Min(estimate.Chunks, (long)SampledBlocks(estimate.Blocks) * RecordsPerSampledBlock));
        TimeSpan fixedPart = settles + verification;

        return new DurationRange(
            TimeSpan.FromSeconds(estimate.Memos / BatchFastMemosPerSecond) + fixedPart,
            TimeSpan.FromSeconds(estimate.Memos / BatchSlowMemosPerSecond) + fixedPart);
    }

    public static DurationRange ForOneAccount(PublicationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        long memos = PublishPolicy.MemoCount(estimate);
        TimeSpan readBack = Reading(estimate.Chunks + estimate.Lists);

        if (!PublishPolicy.UsesTickets(estimate))
        {
            TimeSpan sequence = LedgerTiming.ApproximateLedgerInterval * memos + readBack;
            return new DurationRange(sequence, sequence);
        }

        return new DurationRange(
            TimeSpan.FromSeconds(memos / TicketFastMemosPerSecond) + readBack,
            TimeSpan.FromSeconds(memos / TicketSlowMemosPerSecond) + readBack);
    }

    /// <summary>Downloading a file on the Read screen: every chunk and list record at the reader's pace (spec S15).</summary>
    public static TimeSpan ForRead(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Reading(manifest.ChunkCount + manifest.Lists);
    }
}
```

`app/XrplFile.App.Core/CostSummary.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>One account's part of a publication, as the summary shows it (spec S12).</summary>
/// <param name="Role">"Writer" or "Payer".</param>
/// <param name="Name">The key's label, or its address when the label is not known.</param>
public sealed record AccountCost(string Role, string Name, long RequiredDrops, long WorstCaseFeeDrops);

/// <summary>
/// What a run will cost in time and money (spec S12): the duration range, the transactions, and each
/// account's required balance and worst-case fees. Above an hour or one XRP of either account's worst
/// case, it has to be confirmed.
/// </summary>
public sealed record CostSummary(DurationRange Duration, long Transactions, IReadOnlyList<AccountCost> Accounts)
{
    public static readonly TimeSpan ConfirmationDuration = TimeSpan.FromHours(1);

    public const long ConfirmationDrops = 1_000_000;

    public bool NeedsConfirmation =>
        Duration.Longest > ConfirmationDuration || Accounts.Any(account => account.WorstCaseFeeDrops > ConfirmationDrops);

    public static CostSummary Of(PublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        return new CostSummary(
            PublicationDuration.ForOneAccount(estimate), estimate.Transactions,
            [new AccountCost("Writer", request.WriterLabel ?? request.Account, estimate.RequiredDrops, estimate.FeeDropsWorstCase)]);
    }

    public static CostSummary Of(BatchPublicationEstimate estimate, PublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(request);

        return new CostSummary(
            PublicationDuration.ForBatch(estimate), estimate.Memos + estimate.Batches,
            [
                new AccountCost("Payer", request.PayerLabel ?? request.PayerAccount ?? string.Empty, estimate.PayerRequiredDrops, estimate.PayerFeeDropsWorstCase),
                new AccountCost("Writer", request.WriterLabel ?? request.Account, estimate.WriterRequiredDrops, estimate.WriterFeeDropsWorstCase),
            ]);
    }
}
```

- [ ] **Step 4: The Estimate screen's confirmation**

In `EstimateModel`:

```csharp
    /// <summary>The checkbox's words (spec S12).</summary>
    public const string ConfirmationLabel = "I understand the cost and the time";

    private SpendingApproval? _approval;

    /// <summary>The cost-and-time summary of the current estimate, or null before one.</summary>
    public CostSummary? Summary { get; private set; }

    /// <summary>Whether the summary has to be confirmed before Publish (spec S12).</summary>
    public bool NeedsConfirmation => Summary?.NeedsConfirmation == true;

    /// <summary>Whether the person ticked the checkbox for the current estimate.</summary>
    public bool Confirmed { get; private set; }

    public void Confirm(bool understood) => Confirmed = understood;
```

- `CanPublish` ends with `&& (!NeedsConfirmation || Confirmed)`.
- `Approval` becomes `public SpendingApproval? Approval => CanPublish ? _approval : null;` and Task 12's assignment writes `_approval`.
- `EstimateAsync` and `DiscardAsync` set `Summary = null; Confirmed = false; _approval = null;` first.
- At each successful estimate, `Summary = CostSummary.Of(BatchEstimate, Request)` or `CostSummary.Of(Estimate, Request)` (`Request` is the effective request).

`Estimate.razor`, inside each estimate card after the existing tables (Batch and one-account alike), add the time row and, when needed, the summary and the checkbox, before the alert:

```razor
            @if (Model.Summary is { } summary)
            {
                <table class="table xf-kv-table mt-4">
                    <tbody>
                        <tr><th>Time</th><td>@DurationText.Of(summary.Duration)</td></tr>
                    </tbody>
                </table>
            }
```

and below the card:

```razor
    @if (Model.NeedsConfirmation && Model.Summary is { } confirm)
    {
        <section class="xf-card">
            <h2 class="xf-section-title">This publication is large</h2>
            <table class="table xf-kv-table">
                <tbody>
                    <tr><th>Time</th><td>@DurationText.Of(confirm.Duration)</td></tr>
                    <tr><th>Transactions</th><td>@confirm.Transactions.ToString("N0")</td></tr>
                    @foreach (AccountCost account in confirm.Accounts)
                    {
                        <tr><th>@account.Role @account.Name needs</th><td>@XrpAmount.Format(account.RequiredDrops)</td></tr>
                        <tr><th>@account.Role fees in the worst case</th><td>@XrpAmount.Format(account.WorstCaseFeeDrops)</td></tr>
                    }
                </tbody>
            </table>
            <div class="form-check mb-0">
                <input class="form-check-input" type="checkbox" id="xf-understood" checked="@Model.Confirmed"
                       @onchange="change => Model.Confirm(change.Value is true)" />
                <label class="form-check-label" for="xf-understood">@EstimateModel.ConfirmationLabel</label>
            </div>
        </section>
    }
```

- [ ] **Step 5: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS; `RunModelEtaTests` are unchanged, because `DurationText.Of` is `RunModel`'s rule moved, not changed.
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings.

- [ ] **Step 6: Commit**

```bash
git add app tests
git commit -m "feat(app): price the time of a publication and confirm large ones

The estimate shows a duration range per strategy: Batch at 85 to 70 memos/s
plus 12 s a round and its sampled read, tickets at 2 to 1 memos/s, sequence a
memo per ledger, both with a full read-back. Above an hour or 1 XRP of either
account's worst case, a summary and a checkbox gate Publish, and the checkbox
resets with every estimate."
```

---

### Task 14: App — the resume confirmation and the run screen's time left (S25 resume, S11 run screen)

**Files:**
- Create: `tests/XrplFile.App.Core.Tests/BatchTestWait.cs`, `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`
- Modify: `app/XrplFile.App.Core/PublicationRun.cs` (resume approval, `RunSnapshot.Confirmation`, `AnswerConfirmation`), `app/XrplFile.App.Core/RunModel.cs` (Batch settles per round, the read-back's time left, the confirmation), `app/XrplFile.App.Core/UnfinishedModel.cs` (labels on the request)
- Modify: `app/XrplFile.App/Components/Run.razor`
- Test: `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`, `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs` (two tests)

**Interfaces:**
- Consumes: Task 11 (`ResumeQuote`, `ResumeApproval`, `SessionOptions.ApproveResume`), Task 12 (`Requests.Fresh`, `PublicationRun`'s resume opening the recorded path), Task 13 (`CostSummary`, `AccountCost`, `PublicationDuration.SettlePerRound`, `PublicationDuration.Reading`, `PublicationDuration.RecordsPerSampledBlock`, `DurationText`, `EstimateModel.ConfirmationLabel`).
- Produces:
  - `RunSnapshot.Confirmation : CostSummary?` (a resume waiting for the person); `PublicationRun.AnswerConfirmation(bool approved) : bool`; `RunView.Confirmation`; `RunModel.ConfirmResume()`, `RunModel.DeclineResume()`.
  - Test helpers used by later tasks: `internal static class BatchTestWait` with `static Task UntilAsync(Func<bool> condition)` (Task 17); `ResumeConfirmationTests` with `ReadyAsync(int size = 20_000) : Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)>`, `InterruptedExpensiveAsync() : Task<(PublicationRun Run, PublishRequest Request, string StatePath, KeyVault Vault)>`, `static PublishRequest Resuming(PublishRequest started, string path)`, `_connections` and `Token` (Task 17 adds two tests to this class).

**The run screen's time left (Spec question 8):** on the Batch path one settle per round still to come — the rounds of the current pass after the one in progress, plus later passes — and the read-back stage's own time: blocks still to read at the measured rate, or 1,024 records per block at 0.7 s per 400 before a rate exists. The ticket path keeps one settle per pass (B21).

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/BatchTestWait.cs` (the Core suite's `BatchTestNode.EventuallyAsync` is the same thing, and the App suite has none):

```csharp
namespace XrplFile.App.Core.Tests;

internal static class BatchTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("the condition did not become true in 20 s");
            await Task.Delay(5);
        }
    }
}
```

`tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`:

```csharp
using Xrpl.Wallet;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Spec S25: a resume asks for its approval after reconciliation, and above the threshold it asks the person.</summary>
public sealed class ResumeConfirmationTests : VaultHarness
{
    private static readonly BatchStrategyOptions Fast = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(5),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),

        // The fake closes a ledger on every read, so its deadlines pass in milliseconds.
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-resume-confirm-").FullName;
    private readonly FakeConnectionFactory _connections = new();

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A writer and a payer of 100 XRP each in a vault, and a Batch request for <paramref name="size"/> bytes recorded at <c>file.bin</c>.</summary>
    private async Task<(PublicationRun Run, PublishRequest Request, PublicationHistory History, KeyVault Vault)> ReadyAsync(int size = 20_000)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);

        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        string writerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "testnet", writer.ClassicAddress, writer.Seed), Token);
        string payerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "payer", "testnet", payer.ClassicAddress, payer.Seed), Token);

        FakeBatchLedgerSink sink = _connections.EnableBatch();
        sink.SetSequence(writer.ClassicAddress, 100);
        sink.SetSequence(payer.ClassicAddress, 500);
        _connections.Ledger.Balances[writer.ClassicAddress] = 100_000_000;
        _connections.Ledger.Balances[payer.ClassicAddress] = 100_000_000;

        PublicationHistory history = new(Paths.HistoryPath);
        PublicationRun run = new(_connections, vault, Paths, history, batchOptions: Fast);
        PublishRequest request = Requests.Fresh(
            VectorStream.Bytes("app-batch", size), "application/octet-stream", "file.bin", writerRef, writer.ClassicAddress, "testnet",
            payerRef, payer.ClassicAddress, sourcePath: Path.Combine(_directory, "file.bin"));

        return (run, request, history, vault);
    }

    /// <summary>A Batch run of 250 chunks at an open-ledger fee of 190: the payer's worst case is 1,883 x 570 = 1,073,310 drops, past the line.</summary>
    private async Task<(PublicationRun Run, PublishRequest Request, string StatePath, KeyVault Vault)> InterruptedExpensiveAsync()
    {
        (PublicationRun run, PublishRequest request, _, KeyVault vault) = await ReadyAsync(250_000);
        _connections.Ledger.OpenLedgerFee = new OpenLedgerFee(190, 10);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;
        _connections.BatchSink.Transmitting = null;
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);

        return (run, request, run.Snapshot.StatePath!, vault);
    }

    private static PublishRequest Resuming(PublishRequest started, string path) =>
        new(started.Size, started.Type, started.Name, started.KeyReference, started.Account, started.Network,
            started.PayerReference, started.PayerAccount, path) { SourceRecorded = true };

    [Fact]
    public async Task A_resume_above_the_threshold_waits_for_the_person_and_goes_on_when_confirmed()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 250_000), Token);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        CostSummary asked = run.Snapshot.Confirmation!;
        Assert.True(asked.NeedsConfirmation);
        Assert.True(run.AnswerConfirmation(true));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
    }

    [Fact]
    public async Task A_declined_resume_sends_nothing_and_stays_resumable()
    {
        (PublicationRun run, PublishRequest request, string statePath, _) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 250_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);
        int sent = _connections.BatchSink!.Transmissions.Count;

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);
        Assert.True(run.AnswerConfirmation(false));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Equal(sent, _connections.BatchSink.Transmissions.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));
    }

    [Fact]
    public async Task A_resume_below_the_threshold_asks_nobody()
    {
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(20_000);
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };
        Assert.True(run.Start(request));
        await run.Completion;
        _connections.BatchSink.Transmitting = null;
        string statePath = run.Snapshot.StatePath!;
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 20_000), Token);
        bool asked = false;

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        while (!run.Completion.IsCompleted)
        {
            asked |= run.Snapshot.Confirmation is not null;
            await Task.Delay(5, Token);
        }

        Assert.False(asked);
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
    }
}
```

The resume opens the recorded path through the run's default `FilePublicationSource.Open`, so each test writes the file to `request.SourcePath` before it resumes.

In `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs`, add:

```csharp
    [Fact]
    public void A_Batch_pass_with_rounds_still_to_come_adds_a_settle_for_each()
    {
        const long fileBytes = 40_000_000;
        string text = RunModel.StageTextOf(Running("chunks", 0, 40_000, batch: true, TimeSpan.FromSeconds(6), fileBytes), Now, rate: 70);

        // 40,000 at 70/s is 571 s; two more rounds of this pass, the list pass and the manifest: 4 x 12 s.
        Assert.Equal($"Writing the file — 0 of {40_000:N0} · 70 chunks/s · about 10 min left · 0:06 elapsed", text);
    }

    [Fact]
    public void The_read_back_says_how_long_its_remaining_blocks_take()
    {
        string text = RunModel.StageTextOf(Running("read-back", 1, 4, batch: true, TimeSpan.FromSeconds(10)), Now, rate: null);

        // Three blocks of 1,024 records at 0.7 s per 400: 5.4 s.
        Assert.Equal("Reading it back from the ledger — 1 of 4 · about 6 s left · 0:10 elapsed", text);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.ResumeConfirmationTests -class XrplFile.App.Core.Tests.RunModelEtaTests`
Expected: build errors (`Confirmation`, `AnswerConfirmation`); then the two ETA tests fail (one settle per pass, no read-back time).

- [ ] **Step 3: The resume confirmation and the run screen**

In `PublicationRun`:

```csharp
    private CostSummary? _confirmation;
    private TaskCompletionSource<bool>? _answer;

    /// <summary>
    /// Answers a resume that waits for the person (spec S25): true goes on, false stops the run
    /// before anything is sent. False when no resume is waiting.
    /// </summary>
    public bool AnswerConfirmation(bool approved)
    {
        TaskCompletionSource<bool>? answer;

        lock (_gate)
        {
            answer = _answer;
        }

        return answer?.TrySetResult(approved) == true;
    }

    /// <summary>
    /// A resume's approval (spec S25), made from the quote of what remains: given at once below the
    /// threshold, asked of the person on the run screen above it. Declining is a cancellation.
    /// </summary>
    private ResumeApproval ApproveResume(PublishRequest request, int generation) => async (quote, cancellationToken) =>
    {
        CostSummary summary = quote.Batch is { } batch ? CostSummary.Of(batch, request) : CostSummary.Of(quote.OneAccount!, request);
        SpendingApproval approval = quote.Batch is { } priced
            ? SpendingApproval.For(priced, quote.Writer, quote.Payer!)
            : SpendingApproval.For(quote.OneAccount!, quote.Writer);

        if (!summary.NeedsConfirmation) return approval;

        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_generation != generation) throw new OperationCanceledException(cancellationToken);
            _confirmation = summary;
            _answer = answer;
        }

        try
        {
            using CancellationTokenRegistration cancelled = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));

            if (!await answer.Task.ConfigureAwait(false)) throw new OperationCanceledException("the person declined the resume's cost", cancellationToken);

            return approval;
        }
        finally
        {
            lock (_gate)
            {
                _confirmation = null;
                _answer = null;
            }
        }
    };
```

- Both resume paths set `ApproveResume = ApproveResume(request, generation)` in their `SessionOptions`.
- `RunSnapshot` gains `public CostSummary? Confirmation { get; init; }`, filled from `_confirmation` in `Snapshot`.
- `Reset` clears `_confirmation` and `_answer` (an answer pending on a reset run is cancelled with `_answer?.TrySetCanceled()`).

`UnfinishedModel.ResumeWithAsync` names the keys on the request it builds: `WriterLabel = _labels.GetValueOrDefault(row.KeyReference ?? string.Empty)`, `PayerLabel = payerReference is null ? null : _labels.GetValueOrDefault(payerReference)`.

In `RunModel`:

- `RunView` gains `CostSummary? Confirmation` (last), filled in `Capture` from `snapshot.Confirmation`.
- `public bool ConfirmResume() => run.AnswerConfirmation(true);` and `public bool DeclineResume() => run.AnswerConfirmation(false);`.
- In `StageTextOf`, for a Batch run replace `SettlePerPass * PassesAfter(progress.Stage, snapshot.FileBytes)` with `SettlesAfter(progress.Stage, remaining, snapshot.FileBytes, snapshot.UsesBatch)`:

```csharp
    /// <summary>
    /// The settles still to come (spec S11, B21): on the Batch path one per round — the rounds of this
    /// pass after the one in progress, and every round of the passes after it; on the ticket path one
    /// per pass still to come.
    /// </summary>
    private static TimeSpan SettlesAfter(string stage, long remaining, long fileBytes, bool batch)
    {
        if (!batch) return SettlePerPass * PassesAfter(stage, fileBytes);

        long thisPass = Math.Max(0, (remaining + BatchStrategyOptions.DefaultMaxRoundMemos - 1) / BatchStrategyOptions.DefaultMaxRoundMemos - 1);
        return PublicationDuration.SettlePerRound * (thisPass + PassesAfter(stage, fileBytes));
    }
```

  (A list pass of more than 16,384 memos would need 1.7 TB; one round per later pass is exact.)
- For the `"read-back"` stage, before `return head + elapsed;`:

```csharp
        if (progress.Stage == "read-back")
        {
            long blocks = Math.Max(0, progress.Total - progress.Done);
            TimeSpan left = rate is { } measured && measured > 0
                ? TimeSpan.FromSeconds(blocks / measured)
                : PublicationDuration.Reading(blocks * PublicationDuration.RecordsPerSampledBlock);

            return blocks == 0 ? head + elapsed : $"{head} · about {DurationText.Of(left)} left{elapsed}";
        }
```

`Run.razor`, inside the running page's card, after the progress bar:

```razor
            @if (view.Confirmation is { } confirm)
            {
                <h2 class="xf-section-title mt-4">Resuming costs</h2>
                <table class="table xf-kv-table">
                    <tbody>
                        <tr><th>Time</th><td>@DurationText.Of(confirm.Duration)</td></tr>
                        <tr><th>Transactions</th><td>@confirm.Transactions.ToString("N0")</td></tr>
                        @foreach (AccountCost account in confirm.Accounts)
                        {
                            <tr><th>@account.Role @account.Name needs</th><td>@XrpAmount.Format(account.RequiredDrops)</td></tr>
                            <tr><th>@account.Role fees in the worst case</th><td>@XrpAmount.Format(account.WorstCaseFeeDrops)</td></tr>
                        }
                    </tbody>
                </table>
                <div class="form-check">
                    <input class="form-check-input" type="checkbox" id="xf-resume-understood" @bind="_understood" />
                    <label class="form-check-label" for="xf-resume-understood">@EstimateModel.ConfirmationLabel</label>
                </div>
                <div class="xf-actionbar">
                    <button type="button" class="btn btn-outline-secondary" @onclick="() => Model.DeclineResume()">Stop</button>
                    <button type="button" class="btn btn-primary" disabled="@(!_understood)" @onclick="() => Model.ConfirmResume()">Continue</button>
                </div>
            }
```

with, in `@code`, the field and its reset in the poll that refreshes `view` (the existing timer callback that captures `Model.View` and calls `StateHasChanged`):

```csharp
    /// <summary>The resume's checkbox; cleared whenever no confirmation is shown, so the next one starts unticked.</summary>
    private bool _understood;
```

```csharp
            view = Model.View;
            if (view.Confirmation is null) _understood = false;
```

(`view` is the field the page already renders from; if the callback names it differently, reset `_understood` right after that assignment.)

- [ ] **Step 4: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS. Existing `RunModelEtaTests` for small Batch files are unchanged (no further round in their pass).
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings.

- [ ] **Step 5: Commit**

```bash
git add app tests
git commit -m "feat(app): approve every resume, and count rounds in the time left

A resume asks for its approval after reconciliation: at once below the line,
on the run screen above it, with the same summary and checkbox as the
estimate; declining stops it before anything is sent. The run screen adds a
settle for every Batch round still to come and says how long the read-back
has left."
```

---

### Task 15: App — the Publish screen's size policy (S1, S16)

**Files:**
- Modify: `app/XrplFile.App.Core/PublishModel.cs` (limits, sentences, `Describe` order, `Warning` removed), `app/XrplFile.App.Core/PublishErrorMessages.cs` (`TooLarge`), `app/XrplFile.App.Core/PublishRequest.cs` (a cref, if Task 12 left it), `app/XrplFile.App/Components/Publish.razor` (the warning alert removed)
- Test: `tests/XrplFile.App.Core.Tests/PublishModelPayerTests.cs` (six tests), `tests/XrplFile.App.Core.Tests/PublishModelTests.cs` (three tests removed), `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs` (one test)

**Interfaces:**
- Consumes: Task 1 (`PublicationSession.MaxPublicationBytes`), Task 12 (`BuildRequestAsync` measures the size, reads no bytes).
- Produces:
  - `PublishModel.TicketPathMaxBytes = 64L * 1024 * 1024` (public const; replaces `MaxFileBytes`), `PublishModel.MaxPublicationBytes = PublicationSession.MaxPublicationBytes` (public const).
  - `PublishModel.PayerRequiredMessage = "Choose a payer — files larger than 64 MiB publish through Batch."`, `PublishModel.TooLargeMessage = "This application publishes files up to 16 GiB."` (public consts).
  - `PublishModel.SizeLimit : long` — `MaxPublicationBytes` with a payer, `TicketPathMaxBytes` without.
  - Removed: `PublishModel.ReaderDefaultMaxBytes`, `PublishModel.MaxFileBytes`, `PublishModel.Warning`.

**The rule (spec S1, S16), in the order `Describe` checks it:** no file; an empty file; above 16 GiB (`TooLargeMessage`, whoever pays); no key; a network the format cannot name; above 64 MiB with no payer — Nobody chosen, or a network where Batch is unavailable (`PayerRequiredMessage`); no node URL. `CanEstimate` follows the same limits. `SelectPayer` and `RefreshPayerAsync` describe again, because the payer now decides whether the file is allowed.

- [ ] **Step 1: Write the failing tests**

In `tests/XrplFile.App.Core.Tests/PublishModelPayerTests.cs`, add:

```csharp
    private async Task<(PublishModel Model, Dictionary<string, string> References)> WriterAndPayerAsync()
    {
        (PublishModel model, Dictionary<string, string> refs) = await OpenAsync(("writer", "testnet", null), ("payer", "testnet", null));
        model.SelectKey(refs["writer"]);
        await model.RefreshPayerAsync(Token);

        return (model, refs);
    }

    [Fact]
    public async Task Without_a_payer_64_MiB_is_the_limit()
    {
        (PublishModel model, _) = await WriterAndPayerAsync();
        model.SelectPayer(null);

        model.DescribeFileForTests("limit.bin", PublishModel.TicketPathMaxBytes);
        Assert.True(model.CanEstimate);
        Assert.Null(model.Message);

        model.DescribeFileForTests("over.bin", PublishModel.TicketPathMaxBytes + 1);
        Assert.False(model.CanEstimate);
        Assert.Equal("Choose a payer — files larger than 64 MiB publish through Batch.", model.Message);
    }

    [Fact]
    public async Task With_a_payer_16_GiB_is_the_limit()
    {
        (PublishModel model, _) = await WriterAndPayerAsync();
        Assert.NotNull(model.SelectedPayer);

        model.DescribeFileForTests("large.bin", PublishModel.TicketPathMaxBytes + 1);
        Assert.True(model.CanEstimate);

        model.DescribeFileForTests("limit.bin", 16L * 1024 * 1024 * 1024);
        Assert.True(model.CanEstimate);
        Assert.Null(model.Message);

        model.DescribeFileForTests("over.bin", 16L * 1024 * 1024 * 1024 + 1);
        Assert.False(model.CanEstimate);
        Assert.Equal("This application publishes files up to 16 GiB.", model.Message);
    }

    [Fact]
    public async Task Above_16_GiB_the_size_is_the_reason_even_with_nobody_paying()
    {
        (PublishModel model, _) = await WriterAndPayerAsync();
        model.SelectPayer(null);

        model.DescribeFileForTests("over.bin", PublishModel.MaxPublicationBytes + 1);

        Assert.Equal(PublishModel.TooLargeMessage, model.Message);
    }

    [Fact]
    public async Task Choosing_the_payer_again_lifts_the_refusal_at_once()
    {
        (PublishModel model, Dictionary<string, string> refs) = await WriterAndPayerAsync();
        model.SelectPayer(null);
        model.DescribeFileForTests("large.bin", 100_000_000);
        Assert.Equal(PublishModel.PayerRequiredMessage, model.Message);

        model.SelectPayer(refs["payer"]);

        Assert.Null(model.Message);
        Assert.True(model.CanEstimate);
    }

    [Fact]
    public async Task On_a_network_without_Batch_a_large_file_needs_a_payer_it_cannot_have()
    {
        _connections.Ledger.EnabledAmendments.Clear();
        (PublishModel model, _) = await WriterAndPayerAsync();
        Assert.True(model.PayerLocked);

        model.DescribeFileForTests("large.bin", 100_000_000);

        Assert.False(model.CanEstimate);
        Assert.Equal(PublishModel.PayerRequiredMessage, model.Message);
    }

    [Fact]
    public async Task A_file_past_the_readers_default_carries_no_warning()
    {
        (PublishModel model, _) = await WriterAndPayerAsync();

        model.DescribeFileForTests("big.bin", new ReaderOptions().MaxBytes + 1);

        Assert.True(model.CanEstimate);
        Assert.Null(model.Message);
    }
```

In `tests/XrplFile.App.Core.Tests/PublishModelTests.cs`, delete `ReaderDefaultMaxBytes_is_the_readers_own_default_not_a_copy_of_it`, `A_file_past_the_readers_default_limit_is_warned_about_and_still_allowed` and `A_file_past_the_memory_ceiling_is_refused` (the payer tests above replace them).

In `tests/XrplFile.App.Core.Tests/PublishErrorMessagesTests.cs`, add:

```csharp
    [Fact]
    public void TooLarge_says_the_applications_limit()
    {
        Assert.Equal(PublishModel.TooLargeMessage, PublishErrorMessages.For(XrplFileError.TooLarge));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.PublishModelPayerTests -class XrplFile.App.Core.Tests.PublishErrorMessagesTests`
Expected: build errors (`TicketPathMaxBytes`, `MaxPublicationBytes`, `PayerRequiredMessage`, `TooLargeMessage`).

- [ ] **Step 3: The policy**

In `PublishModel`, replace `ReaderDefaultMaxBytes` and `MaxFileBytes` with:

```csharp
    /// <summary>
    /// The largest file published without a payer (spec S16): tickets or sequence numbers. A ticket
    /// run of a gigabyte would hold about 50 XRP of reserve for hours.
    /// </summary>
    public const long TicketPathMaxBytes = 64L * 1024 * 1024;

    /// <summary>The largest file this application publishes, through Batch (spec S1).</summary>
    public const long MaxPublicationBytes = PublicationSession.MaxPublicationBytes;

    /// <summary>Above <see cref="TicketPathMaxBytes"/> with nobody paying (spec S1).</summary>
    public const string PayerRequiredMessage = "Choose a payer — files larger than 64 MiB publish through Batch.";

    /// <summary>Above <see cref="MaxPublicationBytes"/> (spec S1).</summary>
    public const string TooLargeMessage = "This application publishes files up to 16 GiB.";

    /// <summary>The size limit of the publication as chosen: a payer lifts it from 64 MiB to 16 GiB.</summary>
    public long SizeLimit => SelectedPayer is null ? TicketPathMaxBytes : MaxPublicationBytes;
```

- `CanEstimate`: replace `FileSize <= MaxFileBytes` with `FileSize <= SizeLimit`.
- Delete the `Warning` property, its assignment in `RecordStarted` and `Warning = null;` at the top of `Describe`.
- `SelectPayer` ends with `Describe();`; `RefreshPayerAsync` calls `Describe()` before each of its three `return` points and at its end (the payer it settles on decides the limit).
- In `Describe`, replace the `FileSize > MaxFileBytes` block with

```csharp
        if (FileSize > MaxPublicationBytes)
        {
            Message = TooLargeMessage;
            return;
        }
```

  and, after the network check and in place of the `ReaderDefaultMaxBytes` warning, add

```csharp
        if (FileSize > TicketPathMaxBytes && SelectedPayer is null)
        {
            Message = PayerRequiredMessage;
            return;
        }
```

- Update the class's remarks where they speak of loading the whole file: the file is measured here and held by the estimate (spec S24).

In `PublishErrorMessages.For`, the `TooLarge` arm becomes `XrplFileError.TooLarge => PublishModel.TooLargeMessage,` — a publication refuses `TooLarge` only above 16 GiB now (Task 3), and the reader's own limit is the Read screen's business (`ReadErrorMessages`).

If `PublishRequest.cs` still cites `PublishModel.MaxFileBytes` in a `<see cref>`, point it at `PublishModel.TicketPathMaxBytes` (the build fails otherwise).

In `app/XrplFile.App/Components/Publish.razor`, delete the `@if (Model.Warning is not null) { … }` block.

- [ ] **Step 4: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS. `PublishModelTests.A_file_and_a_key_build_a_request` and the other existing tests are unaffected: their files are a few bytes.
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings.

- [ ] **Step 5: Commit**

```bash
git add app tests
git commit -m "feat(app): publish up to 16 GiB through Batch, 64 MiB without a payer

PublishModel.MaxFileBytes becomes TicketPathMaxBytes, applied only when nobody
pays; with a payer the limit is 16 GiB. Each refusal has its sentence, the
payer choice re-describes the file, and the warning about the reader's
default limit is gone."
```

---

### Task 16: Keep the computer awake on a thread of its own (S13)

**Files:**
- Create: `app/XrplFile.App.Core/KeepAwake.cs` (`IKeepAwake`, `IExecutionState`, `ThreadKeepAwake`), `app/XrplFile.App/Platform/WindowsExecutionState.cs`
- Modify: `app/XrplFile.App.Core/PublicationRun.cs` (constructor parameter, acquire and release), `app/XrplFile.App/MauiProgram.cs` (registration)
- Test: `tests/XrplFile.App.Core.Tests/ThreadKeepAwakeTests.cs`, `tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs` (three tests, `ReadyAsync` parameter)

**Interfaces:**
- Consumes: Task 12 (`RunAsync`'s outer `finally`, the constructor's `openSource` parameter).
- Produces:
  - `public interface IKeepAwake { void Acquire(); void Release(); }` — `Release` is idempotent; `Acquire` while held does nothing.
  - `public interface IExecutionState { bool Require(); void Clear(); }` — `Require` false: the system refused.
  - `public sealed class ThreadKeepAwake(IExecutionState state, Action<string>? log = null) : IKeepAwake` — its thread is named `"xrpl-file keep-awake"` and is a background thread.
  - `PublicationRun(..., Func<string, IPublicationSource>? openSource = null, IKeepAwake? keepAwake = null)`.
  - `internal sealed class WindowsExecutionState : IExecutionState` in `XrplFile.App.Platform`.

**Why a thread (spec S13):** `SetThreadExecutionState` belongs to the calling thread. A run's continuations hop between pool threads (`ConfigureAwait(false)`), so a call at the start of a run and another at its end would land on different threads, and the first would never be cleared. The thread that sets the state is the one that clears it.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/ThreadKeepAwakeTests.cs`:

```csharp
using System.Collections.Concurrent;
using Xunit;

namespace XrplFile.App.Core.Tests;

/// <summary>Records which thread made each call, so a test can tell the state was set and cleared by one thread.</summary>
internal sealed class RecordingExecutionState(bool succeeds = true) : IExecutionState
{
    private readonly ConcurrentQueue<(string Call, int Thread)> _calls = new();

    public IReadOnlyList<(string Call, int Thread)> Calls => [.. _calls];

    public bool Require()
    {
        _calls.Enqueue(("require", Environment.CurrentManagedThreadId));
        return succeeds;
    }

    public void Clear() => _calls.Enqueue(("clear", Environment.CurrentManagedThreadId));
}

public sealed class ThreadKeepAwakeTests
{
    [Fact]
    public void The_state_is_set_and_cleared_by_one_thread_that_is_not_the_callers()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, state.Calls[0].Thread);
    }

    [Fact]
    public void Release_twice_clears_once_and_acquire_twice_holds_once()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Acquire();
        keepAwake.Release();
        keepAwake.Release();

        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    [Fact]
    public void A_refused_call_is_logged_and_nothing_fails()
    {
        List<string> log = [];
        ThreadKeepAwake keepAwake = new(new RecordingExecutionState(succeeds: false), line => { lock (log) log.Add(line); });

        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Single(log);
        Assert.Contains("SetThreadExecutionState", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void It_can_be_held_again_after_a_release()
    {
        RecordingExecutionState state = new();
        ThreadKeepAwake keepAwake = new(state);

        keepAwake.Acquire();
        keepAwake.Release();
        keepAwake.Acquire();
        keepAwake.Release();

        Assert.Equal(["require", "clear", "require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[2].Thread, state.Calls[3].Thread);
    }
}
```

In `tests/XrplFile.App.Core.Tests/PublicationRunBatchTests.cs`, give `ReadyAsync` a last parameter `IKeepAwake? keepAwake = null` and construct the run with `new(_connections, vault, Paths, history, batchOptions: Fast, keepAwake: keepAwake)`. Add:

```csharp
    private static void AssertHeldAndClearedOnOneThread(RecordingExecutionState state)
    {
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
    }

    [Fact]
    public async Task A_run_that_succeeds_holds_the_computer_awake_from_start_to_end_on_one_thread()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }

    [Fact]
    public async Task A_run_that_fails_lets_the_computer_sleep_again()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "fake: no node");

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Failed, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }

    [Fact]
    public async Task A_cancelled_run_lets_the_computer_sleep_again()
    {
        RecordingExecutionState state = new();
        (PublicationRun run, PublishRequest request, _, _) = await ReadyAsync(keepAwake: new ThreadKeepAwake(state));
        _connections.BatchSink!.Transmitting = transmission =>
        {
            if (transmission.Ordinal == 1) run.Cancel();
        };

        Assert.True(run.Start(request));
        await run.Completion;

        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        AssertHeldAndClearedOnOneThread(state);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.ThreadKeepAwakeTests -class XrplFile.App.Core.Tests.PublicationRunBatchTests`
Expected: build errors (`IExecutionState`, `ThreadKeepAwake`, `keepAwake`).

- [ ] **Step 3: `ThreadKeepAwake`**

`app/XrplFile.App.Core/KeepAwake.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>Keeps the computer from sleeping while a publication runs (spec S13).</summary>
public interface IKeepAwake
{
    /// <summary>Starts holding the computer awake. Does nothing while already held.</summary>
    void Acquire();

    /// <summary>Lets the computer sleep again. Idempotent.</summary>
    void Release();
}

/// <summary>The platform's execution-state calls, made on <see cref="ThreadKeepAwake"/>'s own thread.</summary>
public interface IExecutionState
{
    /// <summary>Asks the system to stay awake for the calling thread. False when the system refused.</summary>
    bool Require();

    /// <summary>Withdraws the calling thread's request.</summary>
    void Clear();
}

/// <summary>
/// Holds the computer awake from a thread it owns (spec S13). The execution state belongs to the
/// thread that set it, and a run's continuations move between pool threads, so the state is set
/// and cleared here, on one thread that waits between the two. A refused call is logged and does not
/// stop the run: a computer that may sleep is worse, not fatal, because the run stays resumable.
/// </summary>
public sealed class ThreadKeepAwake(IExecutionState state, Action<string>? log = null) : IKeepAwake
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _release;

    public void Acquire()
    {
        lock (_gate)
        {
            if (_thread is not null) return;

            ManualResetEventSlim release = new(initialState: false);
            Thread thread = new(() => Hold(release)) { IsBackground = true, Name = "xrpl-file keep-awake" };
            _release = release;
            _thread = thread;
            thread.Start();
        }
    }

    public void Release()
    {
        Thread? thread;
        ManualResetEventSlim? release;

        lock (_gate)
        {
            thread = _thread;
            release = _release;
            _thread = null;
            _release = null;
        }

        if (thread is null || release is null) return;

        release.Set();

        // Joined so that "released" means cleared: a run that ends and a next one that starts must
        // not see this thread's request outlive the run it was for.
        thread.Join();
        release.Dispose();
    }

    private void Hold(ManualResetEventSlim release)
    {
        bool held = false;

        try
        {
            held = state.Require();
            if (!held) log?.Invoke("keep-awake: SetThreadExecutionState was refused; the computer may sleep during this run");
        }
        catch (Exception error)
        {
            log?.Invoke($"keep-awake: SetThreadExecutionState failed: {error.Message}");
        }

        release.Wait();

        if (!held) return;

        try
        {
            state.Clear();
        }
        catch (Exception error)
        {
            log?.Invoke($"keep-awake: clearing the execution state failed: {error.Message}");
        }
    }
}
```

- [ ] **Step 4: The run holds it**

In `PublicationRun`, append `IKeepAwake? keepAwake = null` to the primary constructor's parameters (after `openSource`). In `RunAsync`, the first statement becomes `keepAwake?.Acquire();` — before `paths.EnsureCreated()`, so a resume's hashing and reconciliation are covered (spec S13: "from the start of a run's or resume's preparation to its end by any path"). In the outer `finally` Task 12 added, after the source is disposed: `keepAwake?.Release();`.

`app/XrplFile.App/Platform/WindowsExecutionState.cs`:

```csharp
using System.Runtime.InteropServices;

using XrplFile.App.Core;

namespace XrplFile.App.Platform;

/// <summary><c>SetThreadExecutionState</c> for <see cref="ThreadKeepAwake"/>'s thread (spec S13).</summary>
internal sealed class WindowsExecutionState : IExecutionState
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>The previous state on success; zero when Windows refused.</summary>
    public bool Require() => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;

    public void Clear() => SetThreadExecutionState(EsContinuous);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
```

In `MauiProgram.cs`, before the `PublicationRun` registration:

```csharp
        // The display may still turn off; only system sleep is held off, and only while a run works (spec S13).
        services.AddSingleton<IKeepAwake>(new ThreadKeepAwake(new WindowsExecutionState(), line => StartupLog.Write(line)));
```

and in the `PublicationRun` factory pass `keepAwake: provider.GetRequiredService<IKeepAwake>()` as a named argument.

- [ ] **Step 5: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS.
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings.

- [ ] **Step 6: Commit**

```bash
git add app tests
git commit -m "feat(app): keep the computer awake while a run works

ThreadKeepAwake owns a background thread that sets the execution state,
waits for the release and clears it, because the state belongs to the thread
that set it and a run's continuations move between pool threads. The run
holds it from the first step of its preparation to its end by any path; a
refused call is logged and does not fail the run."
```

---

### Task 17: The activity lease, and Lock stops the run (S14)

**Files:**
- Create: `app/XrplFile.App.Core/ActivityLease.cs`
- Modify: `app/XrplFile.App.Core/PublicationRun.cs` (the lease), `app/XrplFile.App.Core/UnlockModel.cs` (constructor, `LockAsync`), `app/XrplFile.App/MauiProgram.cs` (`UnlockModel` registration)
- Test: `tests/XrplFile.App.Core.Tests/ActivityLeaseTests.cs`, `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs` (one test), `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs` (two tests)

**Interfaces:**
- Consumes: `VaultAutoLock.Touch` (a no-op once disposed), `PublicationRun.StopAsync(TimeSpan)` (cancels at once, then waits), Task 14 (`InterruptedExpensiveAsync`, `Resuming`, `ReadyAsync`, `BatchTestWait` in `ResumeConfirmationTests`), Task 16 (`RunAsync`'s outer `finally`).
- Produces:
  - `public sealed class ActivityLease : IDisposable` — `static readonly TimeSpan Interval` (1 min), `static ActivityLease Start(Action touch, TimeProvider time)`.
  - `UnlockModel(IKeyVault vault, bool vaultPathIsDefault = true, PublicationRun? run = null)`; `static readonly TimeSpan RunStopTimeout` (10 s).

**The rule (spec S14):** the lease touches the idle lock at once, then every minute, from the first preparation step of a run or resume to its end by any path. Progress reports keep touching as they do today; the lease covers the long awaits that report nothing (hashing a large file before Task 19's reports, a connection that hangs, a history page that takes a minute). Lock cancels an active run at once, waits at most `RunStopTimeout` for it to stop, and then locks whether or not it stopped.

- [ ] **Step 1: Write the failing tests**

`tests/XrplFile.App.Core.Tests/ActivityLeaseTests.cs`:

```csharp
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ActivityLeaseTests
{
    [Fact]
    public void It_touches_at_once_then_every_minute_until_disposed()
    {
        ManualTimeProvider time = new();
        int touches = 0;

        ActivityLease lease = ActivityLease.Start(() => touches++, time);
        Assert.Equal(1, touches);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, touches);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, touches);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, touches);

        lease.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, touches);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        ActivityLease lease = ActivityLease.Start(() => { }, new ManualTimeProvider());

        lease.Dispose();
        lease.Dispose();
    }
}
```

In `tests/XrplFile.App.Core.Tests/PublicationRunTests.cs` (after Task 12 the request comes from `Requests.Fresh`), add:

```csharp
    [Fact]
    public async Task The_lease_keeps_the_vault_open_through_a_long_await_that_reports_nothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "run key", "standalone", wallet.ClassicAddress, wallet.Seed), token);
        _connections.Ledger.Balances[wallet.ClassicAddress] = 100_000_000;

        ManualTimeProvider time = new();
        await using VaultAutoLock autoLock = new(vault, TimeSpan.FromMinutes(5), time);
        GatedConnectFactory gated = new(_connections, gateAt: 1);
        PublicationRun run = new(gated, vault, Paths, History, autoLock, time);

        Assert.True(run.Start(Requests.Fresh(new byte[4096], "application/octet-stream", "file.bin", reference, wallet.ClassicAddress, "1")));
        await gated.Reached;

        // Ten minutes inside one await, twice the idle window, with no progress report at all.
        for (int minute = 0; minute < 10; minute++) time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(vault.IsUnlocked);

        gated.Release();
        await run.Completion;
        Assert.Equal(PublicationRunState.Succeeded, run.Snapshot.State);

        // The lease ended with the run: the idle window applies again.
        time.Advance(TimeSpan.FromMinutes(6));
        await BatchTestWait.UntilAsync(() => !vault.IsUnlocked);
    }
```

In `tests/XrplFile.App.Core.Tests/ResumeConfirmationTests.cs`, add:

```csharp
    [Fact]
    public async Task Lock_while_a_resume_waits_for_the_person_stops_it_and_keeps_it_resumable()
    {
        (PublicationRun run, PublishRequest request, string statePath, KeyVault vault) = await InterruptedExpensiveAsync();
        await File.WriteAllBytesAsync(request.SourcePath!, VectorStream.Bytes("app-batch", 250_000), Token);
        byte[] before = await File.ReadAllBytesAsync(statePath, Token);

        Assert.True(run.Resume(statePath, Resuming(request, request.SourcePath!)));
        await BatchTestWait.UntilAsync(() => run.Snapshot.Confirmation is not null);

        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);
        await unlock.LockAsync();

        Assert.True(run.Completion.IsCompleted);
        Assert.Equal(PublicationRunState.Cancelled, run.Snapshot.State);
        Assert.Null(run.Snapshot.Confirmation);
        Assert.False(vault.IsUnlocked);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath, Token));
    }

    [Fact]
    public async Task Lock_with_no_run_going_just_locks()
    {
        (PublicationRun run, _, _, KeyVault vault) = await ReadyAsync();
        UnlockModel unlock = new(vault, vaultPathIsDefault: true, run);

        await unlock.LockAsync();

        Assert.False(vault.IsUnlocked);
        Assert.Equal(PublicationRunState.Idle, run.Snapshot.State);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.ActivityLeaseTests -class XrplFile.App.Core.Tests.ResumeConfirmationTests -method XrplFile.App.Core.Tests.PublicationRunTests.The_lease_keeps_the_vault_open_through_a_long_await_that_reports_nothing`
Expected: build errors (`ActivityLease`, the `UnlockModel` parameter); once they build, the lease test fails on `vault.IsUnlocked` (the auto-lock fired at minute five).

- [ ] **Step 3: The lease**

`app/XrplFile.App.Core/ActivityLease.cs`:

```csharp
namespace XrplFile.App.Core;

/// <summary>
/// Keeps the vault's idle lock from firing while a run works (spec S14): a touch at once, then one
/// a minute, until disposed. Progress reports touch too, but a run can wait minutes in one await
/// that reports nothing. A touch racing an idle lock already firing changes nothing; the run then
/// meets a locked vault at its next signature and ends resumable.
/// </summary>
public sealed class ActivityLease : IDisposable
{
    /// <summary>Well inside the five-minute idle window, so one late tick cannot let it lapse.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ITimer _timer;

    private ActivityLease(Action touch, TimeProvider time)
    {
        touch();
        _timer = time.CreateTimer(_ => touch(), null, Interval, Interval);
    }

    public static ActivityLease Start(Action touch, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(touch);
        ArgumentNullException.ThrowIfNull(time);

        return new ActivityLease(touch, time);
    }

    public void Dispose() => _timer.Dispose();
}
```

In `PublicationRun.RunAsync`, right after `keepAwake?.Acquire();`:

```csharp
        using ActivityLease? lease = autoLock is null ? null : ActivityLease.Start(autoLock.Touch, _time);
```

(A `using` declaration ends with the method, after the outer `finally`, so the lease covers every exit.)

- [ ] **Step 4: Lock stops the run**

In `UnlockModel`, change the primary constructor to `UnlockModel(IKeyVault vault, bool vaultPathIsDefault = true, PublicationRun? run = null)` and:

```csharp
    /// <summary>How long Lock waits for an active run to stop before locking anyway (spec S14).</summary>
    public static readonly TimeSpan RunStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Locks the vault and returns the screen to <see cref="UnlockStage.Locked"/>. An active run is
    /// cancelled first, at once, and given <see cref="RunStopTimeout"/> to stop (spec S14): the person
    /// asked for the vault to be shut, and a signer that kept going after a lock and an unlock would
    /// go on spending without being asked. The cancellation does not depend on the wait finishing.
    /// </summary>
    public async Task LockAsync()
    {
        if (run is not null) await run.StopAsync(RunStopTimeout).ConfigureAwait(false);

        await vault.LockAsync().ConfigureAwait(false);
        Stage = UnlockStage.Locked;
        // ... the rest of today's body unchanged ...
    }
```

In `MauiProgram.cs`, the `UnlockModel` factory passes the run:

```csharp
        services.AddSingleton(provider => new UnlockModel(
            provider.GetRequiredService<IKeyVault>(),
            VaultLocation.IsDefaultPath(vaultPath),
            provider.GetRequiredService<PublicationRun>()));
```

- [ ] **Step 5: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS. `Progress_touches_the_idle_lock` still passes (the lease only adds touches).
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings.

- [ ] **Step 6: Commit**

```bash
git add app tests
git commit -m "feat(app): an activity lease for the run, and Lock stops it

A run touches the vault's idle lock at once and every minute until it ends, so
an await that reports nothing for minutes cannot let the vault lock under it.
Lock cancels an active run first, waits up to 10 s for it to stop, then locks:
a lock followed by an unlock no longer lets the signer continue."
```

---

### Task 18: App — the Read screen reads any size (S15)

**Files:**
- Modify: `app/XrplFile.App.Core/ReadModel.cs` (ceiling removed, `ReadDuration`), `app/XrplFile.App.Core/ReadRun.cs` (ceiling check removed), `app/XrplFile.App/Components/Read.razor` (the duration)
- Test: `tests/XrplFile.App.Core.Tests/ReadModelTests.cs` (two removed, one added), `tests/XrplFile.App.Core.Tests/ReadRunTests.cs` (one replaced)

**Interfaces:**
- Consumes: Task 13 (`PublicationDuration.ForRead`, `DurationText`).
- Produces: `ReadModel.ReadDuration : TimeSpan?` (null without a manifest). Removed: `ReadModel.MaxBytes`, `ReadModel._ceiling`, `ReadModel.SetCeilingForTests`. `ReaderOptions.MaxBytes` and its default stay in the library (spec S15); `ReadErrorMessages`' `TooLarge` sentence stays, for the library's own refusal.

- [ ] **Step 1: Change the tests**

In `tests/XrplFile.App.Core.Tests/ReadModelTests.cs`, delete `MaxBytes_is_the_readers_own_default_not_a_copy_of_it` and `A_file_past_the_ceiling_is_refused_before_a_block_is_fetched`, and add:

```csharp
    [Fact]
    public async Task A_looked_up_file_says_how_long_it_takes_to_read()
    {
        XrplFileLink link = await PublishAsync(_connections, "photo.png", "image/png", 4096);
        ReadModel model = NewModel();
        Assert.Null(model.ReadDuration);

        model.Link = link.ToString();
        await model.LookUpAsync(TestContext.Current.CancellationToken);

        // Five chunks and no block list, at 0.7 s per 400 records.
        Assert.True(model.CanRead);
        Assert.Equal(TimeSpan.FromSeconds(5 * 0.7 / 400), model.ReadDuration);
    }
```

In `tests/XrplFile.App.Core.Tests/ReadRunTests.cs`, replace `A_manifest_larger_than_the_ceiling_is_refused_before_any_block_is_fetched` with:

```csharp
    [Fact]
    public async Task A_manifest_larger_than_the_readers_default_is_not_refused_for_its_size()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        // Built with `with`: the claim of size is what used to be refused before a connection opened.
        // The run now goes to the node, and whatever it finds there is not a refusal for size.
        ManifestRecord oversized = manifest with
        {
            Manifest = manifest.Manifest with { Size = new ReaderOptions().MaxBytes + 1 },
        };
        int connectionsBefore = _connections.Connections;

        Assert.True(run.Start(link, oversized));
        await run.Completion;

        Assert.NotEqual(ReadErrorMessages.For(XrplFileError.TooLarge), run.Snapshot.Failure);
        Assert.True(_connections.Connections > connectionsBefore);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.ReadModelTests -class XrplFile.App.Core.Tests.ReadRunTests`
Expected: build error (`ReadDuration`); then the `ReadRunTests` test fails: the run is refused with the `TooLarge` sentence before it connects.

- [ ] **Step 3: Remove the ceiling**

In `ReadModel`:
- delete `MaxBytes` (with its remarks), `_ceiling` and `SetCeilingForTests`;
- in `LookUpAsync`, construct the reader as `new XrplFileReader(connection.Source)` and delete the comment above it about `MaxBytes` being inert, and the `if (record.Manifest.Size > _ceiling) { … }` block with the comment above it;
- add

```csharp
    /// <summary>
    /// How long the read takes at the reader's measured pace (spec S15), shown with the size before
    /// the person agrees; null before a manifest is looked up.
    /// </summary>
    public TimeSpan? ReadDuration => Manifest is { } record ? PublicationDuration.ForRead(record.Manifest) : null;
```

In `ReadRun.RunAsync`, delete `ReaderOptions options = new();`, the comment above the check and the `if (manifest.Manifest.Size > options.MaxBytes) { throw … }` block, and construct the reader further down (`XrplFileReader reader = new(connection.Source, options);`, about line 345) as `XrplFileReader reader = new(connection.Source);`. The run streams blocks to a temporary file already, so the size costs disk, not memory.

In `app/XrplFile.App/Components/Read.razor`, after `<p class="mb-1">@manifest.Manifest.Size.ToString("N0") bytes</p>`:

```razor
                <p class="mb-1">About @DurationText.Of(PublicationDuration.ForRead(manifest.Manifest)) to read</p>
```

- [ ] **Step 4: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS. `ReadErrorMessagesTests` keeps its `TooLarge` case: the sentence still exists for the library's refusal.
Run: `dotnet build app/XrplFile.App/XrplFile.App.csproj` — expect no warnings (no `<see cref="ReadModel.MaxBytes"/>` may remain anywhere; search for `ReadModel.MaxBytes` and remove what the build names).

- [ ] **Step 5: Commit**

```bash
git add app tests
git commit -m "feat(app): the Read screen reads any size and says how long it takes

ReadModel and ReadRun no longer apply the reader's default ceiling: the read
streams to disk. The screen shows the size and a duration at the reader's
measured pace before the person starts. The library's default stays."
```

---

### Task 19: App — the preparation stages in words (spec 5.3)

**Files:**
- Modify: `app/XrplFile.App.Core/RunModel.cs` (`StageNameOf`, a shared head, the stopped line)
- Test: `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs` (four tests)

**Interfaces:**
- Consumes: Task 3 (`PublishProgress("hashing", chunksHashed, chunks)`), Task 6 (`PublishProgress("history", recordsRead, 0)`), Task 12 (preparation reports reach `RunSnapshot.Progress` through `Observe`).
- Produces: `StageNameOf("hashing") == "Hashing the file"`, `StageNameOf("history") == "Reading the ledger history"`; a private `HeadOf(PublishProgress)` used by the running and the stopped lines.

- [ ] **Step 1: Write the failing tests**

In `tests/XrplFile.App.Core.Tests/RunModelEtaTests.cs`, add:

```csharp
    [Fact]
    public void Hashing_says_how_many_chunks_of_how_many()
    {
        string text = RunModel.StageTextOf(Running("hashing", 412_000, 1_689_810, batch: true, TimeSpan.FromSeconds(30)), Now, rate: null);

        Assert.Equal("Hashing the file — 412,000 of 1,689,810 chunks · 0:30 elapsed", text);
    }

    [Fact]
    public void Reading_the_history_says_so_before_and_after_its_first_thousand_records()
    {
        Assert.Equal(
            "Reading the ledger history · 0:05 elapsed",
            RunModel.StageTextOf(Running("history", 0, 0, batch: true, TimeSpan.FromSeconds(5)), Now, rate: null));
        Assert.Equal(
            $"Reading the ledger history — {3_000:N0} records · 0:05 elapsed",
            RunModel.StageTextOf(Running("history", 3_000, 0, batch: true, TimeSpan.FromSeconds(5)), Now, rate: null));
    }

    [Fact]
    public void A_run_stopped_while_hashing_says_where()
    {
        RunSnapshot cancelled = Running("hashing", 412_000, 1_689_810, batch: true, TimeSpan.FromSeconds(40)) with
        {
            State = PublicationRunState.Cancelled,
            FinishedAt = Now - TimeSpan.FromSeconds(10),
        };

        Assert.Equal("Stopped while hashing the file — 412,000 of 1,689,810 chunks · 0:30 elapsed", RunModel.StageTextOf(cancelled, Now, rate: null));
    }

    [Fact]
    public void The_stage_names_are_the_specs()
    {
        Assert.Equal("Hashing the file", RunModel.StageNameOf("hashing"));
        Assert.Equal("Reading the ledger history", RunModel.StageNameOf("history"));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet run --project tests/XrplFile.App.Core.Tests -- -class XrplFile.App.Core.Tests.RunModelEtaTests`
Expected: the four new tests FAIL (raw stage names, no unit).

- [ ] **Step 3: The words**

In `RunModel.StageNameOf`, add before `"chunks"`:

```csharp
        "hashing" => "Hashing the file",
        "history" => "Reading the ledger history",
```

Add:

```csharp
    /// <summary>
    /// A stage with its count (spec 5.3): hashing counts chunks of the file's total; the history pass
    /// counts records and has no total, because how far back the writer's history goes is what it is
    /// finding out.
    /// </summary>
    private static string HeadOf(PublishProgress progress) => progress.Stage switch
    {
        "hashing" => $"{StageNameOf("hashing")} — {progress.Done:N0} of {progress.Total:N0} chunks",
        "history" => progress.Done > 0 ? $"{StageNameOf("history")} — {progress.Done:N0} records" : StageNameOf("history"),
        _ => $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}",
    };
```

In `StageTextOf`, `string head = $"{StageNameOf(progress.Stage)} — {progress.Done:N0} of {progress.Total:N0}";` becomes `string head = HeadOf(progress);`. In `StoppedTextOf`, the last arm becomes `{ } progress => $"Stopped while {Lowered(HeadOf(progress))}",`.

- [ ] **Step 4: Run the tests**

Run: the command of Step 2, then `dotnet run --project tests/XrplFile.App.Core.Tests -- -trait- "Category=Integration"`.
Expected: PASS; the existing lines for chunks, lists, read-back and the fence are unchanged.

- [ ] **Step 5: Commit**

```bash
git add app tests
git commit -m "feat(app): name the preparation stages on the run screen

A run or resume now says \"Hashing the file — 412,000 of 1,689,810 chunks\"
and \"Reading the ledger history\" while it prepares, and a run stopped there
says where it stopped."
```

---

### Task 20: The memory test in a child process (S27)

**Files:**
- Create: `tests/XrplFile.MemoryProbe/XrplFile.MemoryProbe.csproj`, `tests/XrplFile.MemoryProbe/Program.cs`, `tests/XrplFile.MemoryProbe/Probe.cs`, `tests/XrplFile.MemoryProbe/CompactLedger.cs`
- Create: `tests/XrplFile.Core.Tests/PublicationMemoryTests.cs`
- Modify: `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj` (build-order reference, probe path), `XrplFile.sln`, `XrplFile.Libraries.slnf`, `.github/workflows/test.yml` (exclude `Slow`)

**Interfaces:**
- Consumes: Task 1 (`IPublicationSource`, `VectorPublicationSource`), Task 2 (`PublicationReport.ChunkCount`), Task 3 (session source overloads with `preparation`), Task 4 (`LedgerTransaction.TransactionIndex`, rounds), Task 7 (the sampled read-back and the S22 manifest check, which read through `ILedgerSource`).
- Produces:
  - `XrplFile.MemoryProbe <bytes>`: publishes `bytes` of `VectorPublicationSource("memory-probe", bytes)` through Batch against `CompactLedger`, stops the run halfway through its chunk pass, resumes it, and prints one JSON line `{"Bytes":…,"Chunks":…,"PeakPrivateBytes":…,"FakeBytes":…,"Seconds":…}`; exit code 0 on success.
  - `internal sealed class CompactLedger : ILedgerSink, ILedgerSource` (probe only).
  - Test `XrplFile.Tests.PublicationMemoryTests` with `[Trait("Category", "Slow")]`.

**What is measured (spec S27):** peak private bytes of the child process — `Process.PeakPagedMemorySize64` on Windows (the peak of the private commit charge); on Linux, which keeps no such peak, `VmHWM` from `/proc/self/status`. The absolute bound is on the raw peak, fake included: `PeakPrivateBytes` of the 800 MB run below 300 MB, as the spec says. The fake ledger preallocates its record arrays and reports their exact size as `FakeBytes`, and only the slope is taken net of it: (`PeakPrivateBytes − FakeBytes`) of the 800 MB run minus the same of the 50 MB run, below 40 MB (Spec question 15). Both figures are printed.

**The fake (spec S27):** `FakeBatchLedgerSink`'s Batch rules — an outer takes the payer's sequence in the open ledger, its inners apply at the close against the writer's validated sequence, an early outer answers `terPRE_SEQ` and is held, a late one `tefPAST_SEQ`, a lapsed one `tefMAX_LEDGER` — with each landed record kept as a 16-byte row and each signed transaction's hash and fee in flat arrays. A chunk memo is checked against the source when it lands and regenerated from the source when history is read; any other memo (block lists, the manifest) is kept as text. Every 10,007th chunk's first landing ends in `tecINSUFFICIENT_RESERVE` (a retry), and history trails the validated ledger by two ledgers (lagging history). Every ledger read closes a ledger, as the general fake does.

- [ ] **Step 1: The probe project**

`tests/XrplFile.MemoryProbe/XrplFile.MemoryProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <RootNamespace>XrplFile.MemoryProbe</RootNamespace>
    <!--
      One heap, collected without a background thread: the figure this process exists to report is
      the run's memory, and server GC's per-core heaps would add headroom that grows with the host,
      not with the file.
    -->
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\XrplFile.Core\XrplFile.Core.csproj" />
    <ProjectReference Include="..\XrplFile.TestSupport\XrplFile.TestSupport.csproj" />
  </ItemGroup>
</Project>
```

`tests/XrplFile.MemoryProbe/Program.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using XrplFile.MemoryProbe;

if (args.Length != 1 || !long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
{
    Console.Error.WriteLine("usage: XrplFile.MemoryProbe <bytes>");
    return 2;
}

// The 800 MB probe signs and decodes about 130,000 outers twice over; on a slow host that takes most
// of an hour, so the probe's own limit is the test's.
using CancellationTokenSource timeout = new(TimeSpan.FromHours(3));
ProbeResult result = await Probe.RunAsync(bytes, timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;
```

`tests/XrplFile.MemoryProbe/Probe.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using Xrpl.Wallet;
using XrplFile.Tests;

namespace XrplFile.MemoryProbe;

internal sealed record ProbeResult(long Bytes, long Chunks, long PeakPrivateBytes, long FakeBytes, double Seconds);

/// <summary>One Batch publication, stopped halfway through its chunks and resumed, against <see cref="CompactLedger"/> (spec S27).</summary>
internal static class Probe
{
    private const uint WriterStart = 100;
    private const uint PayerStart = 500;

    /// <summary>The Batch tests' fast options: the fake closes a ledger on every read, so deadlines are in ledgers, not seconds.</summary>
    private static readonly BatchStrategyOptions Options = new()
    {
        ChainCheck = TimeSpan.FromMilliseconds(5),
        ProgressSweep = TimeSpan.FromMilliseconds(250),
        HistoryLagLimit = TimeSpan.FromSeconds(2),
        FeeWait = TimeSpan.FromMilliseconds(20),
        TransportRetryStep = TimeSpan.FromMilliseconds(1),
        DeadlineLedgers = 200,
    };

    public static async Task<ProbeResult> RunAsync(long bytes, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ITransactionSigner writer = new WalletSigner(XrplWallet.Generate());
        ITransactionSigner payer = new WalletSigner(XrplWallet.Generate());
        await using VectorPublicationSource file = new("memory-probe", bytes);
        long chunks = FileTree.ChunkCount(bytes, MemoLayout.ChunkBytes);
        CompactLedger ledger = new(file, writer.Account, WriterStart, payer.Account, PayerStart, failOnce: Every(10_007, chunks));
        string stateDirectory = Directory.CreateTempSubdirectory("xrpl-file-memory-probe-").FullName;
        List<BatchStrategy> built = [];

        BatchStrategyFactory factory = (budget, fence) =>
        {
            BatchStrategy strategy = new(ledger, ledger, writer, payer, budget, fence, Options);
            built.Add(strategy);
            return strategy;
        };

        try
        {
            SessionOptions session = new() { StateDirectory = stateDirectory, FencePoll = TimeSpan.FromMilliseconds(5) };
            PublicationSession first = await PublicationSession.StartAsync(
                factory, ledger, file, "application/octet-stream", "probe.bin", writer.Account, "writer", payer.Account, "payer", "1",
                session, null, cancellationToken).ConfigureAwait(false);

            using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    await first.RunAsync(new StopAt(stop, chunks / 2), stop.Token).ConfigureAwait(false);
                    throw new InvalidOperationException("the run finished before it could be stopped halfway");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Stopped halfway, as intended: the resume below is half of what is measured.
                }
            }

            PublicationSession resumed = await PublicationSession.ResumeAsync(
                factory, ledger, file, first.StatePath, payer.Account, "payer", session, null, cancellationToken).ConfigureAwait(false);
            PublicationReport report = await resumed.RunAsync(null, cancellationToken).ConfigureAwait(false);

            if (report.ChunkCount != chunks) throw new InvalidOperationException($"the report counts {report.ChunkCount} chunks of {chunks}");

            return new ProbeResult(bytes, report.ChunkCount, PeakPrivateBytes(), ledger.ReservedBytes, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            foreach (BatchStrategy strategy in built) await strategy.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static IEnumerable<long> Every(long step, long chunks)
    {
        for (long index = 5; index < chunks; index += step) yield return index;
    }

    /// <summary>The peak of this process's private memory.</summary>
    private static long PeakPrivateBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            using Process self = Process.GetCurrentProcess();
            self.Refresh();

            // PeakPagefileUsage: the peak of the private commit charge, which is what "private bytes" plots.
            return self.PeakPagedMemorySize64;
        }

        // Linux keeps no peak of private memory; the peak resident set is the nearest figure it keeps.
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;

            string kilobytes = line["VmHWM:".Length..].Trim().Split(' ')[0];
            return long.Parse(kilobytes, NumberStyles.None, CultureInfo.InvariantCulture) * 1024;
        }

        throw new PlatformNotSupportedException("this platform reports no peak memory figure");
    }

    /// <summary>Stops the run once its chunk pass reports <paramref name="at"/> chunks.</summary>
    private sealed class StopAt(CancellationTokenSource stop, long at) : IProgress<PublishProgress>
    {
        public void Report(PublishProgress value)
        {
            // Off the reporting thread: Cancel runs the token's callbacks synchronously, and a report
            // comes from inside the strategy's own locks.
            if (value.Stage == "chunks" && value.Done >= at) _ = Task.Run(stop.Cancel);
        }
    }
}
```

- [ ] **Step 2: The compact ledger**

`tests/XrplFile.MemoryProbe/CompactLedger.cs`:

```csharp
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xrpl.BinaryCodec;
using Xrpl.Utils.Hashes;

namespace XrplFile.MemoryProbe;

/// <summary>
/// A Batch node for the memory test (spec S27). The rules are <c>FakeBatchLedgerSink</c>'s; the
/// storage is not. A landed record is a 16-byte row, and a signed transaction adds its hash and fee
/// to flat arrays sized up front. A chunk's memo is checked against the source when it lands and
/// written back from the source when history is read, because the general fake keeps every memo and
/// would measure itself. Memos that are not chunks are kept as text: a few hundred at most.
/// </summary>
internal sealed class CompactLedger : ILedgerSink, ILedgerSource
{
    private const int OuterKind = 0;
    private const int InnerKind = 1;
    private const int SoloKind = 2;
    private const int NoMemo = -1;

    /// <summary>The tec a scripted retry ends in; the strategy sends the chunk again.</summary>
    private const string RetriedResult = "tecINSUFFICIENT_RESERVE";

    /// <summary>History trails the validated ledger by this many ledgers.</summary>
    private const int HistoryLagLedgers = 2;

    private const long Balance = 1_000_000_000_000;

    private readonly Lock _gate = new();
    private readonly IPublicationSource _file;
    private readonly string _writer;
    private readonly string _payer;
    private readonly HashSet<long> _failOnce;
    private readonly Dictionary<string, uint> _validated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _open = new(StringComparer.Ordinal);
    private readonly List<Pending> _openLedger = [];
    private readonly List<Pending> _held = [];
    private readonly List<(string? Type, string? Data, string? Format)> _otherMemos = [];

    private Row[] _rows;
    private int _rowCount;
    private byte[] _hashes;
    private int[] _fees;
    private int _slotCount;
    private long _current = 2;

    public CompactLedger(IPublicationSource file, string writer, uint writerNext, string payer, uint payerNext, IEnumerable<long> failOnce)
    {
        _file = file;
        _writer = writer;
        _payer = payer;
        _failOnce = [.. failOnce];
        _validated[writer] = _open[writer] = writerNext;
        _validated[payer] = _open[payer] = payerNext;

        // Sized once for the whole run, retries and the resume's re-sends included, so growth never
        // doubles an array in the middle of the measurement.
        long chunks = FileTree.ChunkCount(file.Length, MemoLayout.ChunkBytes);
        _rows = new Row[checked((int)(chunks + chunks / 4 + 65_536))];
        int slots = checked((int)(chunks / 6 + 16_384));
        _hashes = new byte[slots * 32];
        _fees = new int[slots];
    }

    /// <summary>The bytes this fake's own record arrays hold, which the probe takes off its peak.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_gate) return (long)_rows.Length * Unsafe.SizeOf<Row>() + _hashes.Length + (long)_fees.Length * sizeof(int);
        }
    }

    /// <param name="Packed">Kind in bits 0–1, failed in bit 2, and from bit 3 the link: an outer or solo's hash slot, an inner's outer row.</param>
    private readonly record struct Row(int Ledger, int Memo, uint Sequence, int Packed)
    {
        public int Kind => Packed & 3;

        public bool Failed => (Packed & 4) != 0;

        public int Link => Packed >> 3;

        public static Row Of(int ledger, int memo, uint sequence, int kind, bool failed, int link) =>
            new(ledger, memo, sequence, kind | (failed ? 4 : 0) | (link << 3));
    }

    private readonly record struct InnerMemo(int Memo, uint Sequence);

    private sealed record Pending(byte[] Hash, int Kind, string Account, uint Sequence, uint LastLedger, int Fee, int Memo, InnerMemo[] Inners);

    public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult((validated ? _validated : _open).GetValueOrDefault(account, 1u));
    }

    public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
    {
        SignedTransaction signed = await signer.SignAsync(transaction, cancellationToken).ConfigureAwait(false);
        string answer = await SubmitBlobAsync(signed.TxBlob, cancellationToken).ConfigureAwait(false);
        return new SubmitOutcome(answer, signed.Hash);
    }

    public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
    {
        // Concurrent senders overlap, as on a node.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        Pending pending = Decode(txBlob);

        lock (_gate) return Apply(pending);
    }

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        byte[] wanted = Convert.FromHexString(hash);

        lock (_gate)
        {
            for (int slot = 0; slot < _slotCount; slot++)
            {
                if (!_hashes.AsSpan(slot * 32, 32).SequenceEqual(wanted)) continue;

                for (int row = 0; row < _rowCount; row++)
                {
                    if (_rows[row].Kind != InnerKind && _rows[row].Link == slot) return Task.FromResult<LedgerTransaction?>(Record(_rows, _rowCount, row));
                }
            }
        }

        return Task.FromResult<LedgerTransaction?>(null);
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (fromLedger > toLedger)
        {
            throw new XrplFileException(XrplFileError.NodeUnreachable, $"probe: invalidLgrRange from {fromLedger} to {toLedger}");
        }

        Row[] rows;
        int count;
        long visible;

        lock (_gate)
        {
            rows = _rows;
            count = _rowCount;
            visible = _current - HistoryLagLedgers;
        }

        bool payer = string.Equals(account, _payer, StringComparison.Ordinal);
        bool writer = string.Equals(account, _writer, StringComparison.Ordinal);
        long to = Math.Min(toLedger, visible);
        if ((!payer && !writer) || fromLedger > to) yield break;

        // Rows are appended in ledger order and never changed, so the slice is read without the lock.
        int first = FirstRowAt(rows, count, fromLedger);
        int end = FirstRowAt(rows, count, to + 1);

        for (int step = 0; step < end - first; step++)
        {
            int index = forward ? first + step : end - 1 - step;
            if ((rows[index].Kind == OuterKind) != payer) continue;

            if (step % 256 == 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            yield return Record(rows, count, index);
        }
    }

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => Task.FromResult<long?>(1);

    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(new NetworkFees(10, 1_000_000, 200_000));

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => Task.FromResult(Balance);

    public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => Task.FromResult(new AccountState(Balance, 0, 0));

    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LiveTicket>>([]);

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CloseLedger();
            return Task.FromResult(_current);
        }
    }

    public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CloseLedger();

            Dictionary<string, uint> sequences = new(StringComparer.Ordinal);
            foreach (string account in accounts) sequences[account] = _validated.GetValueOrDefault(account);

            return Task.FromResult(new LedgerSnapshot(_current, sequences));
        }
    }

    public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => Task.FromResult(new OpenLedgerFee(10, 10));

    public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(string.Equals(name, Amendments.Batch, StringComparison.Ordinal));

    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(new DestinationState(true, false, false));

    private Pending Decode(string txBlob)
    {
        JsonObject tx = XrplBinaryCodec.Decode(txBlob).AsObject();
        byte[] hash = Convert.FromHexString(HashLedger.HashSignedTx(txBlob));
        string type = (string)tx["TransactionType"]!;
        string account = (string)tx["Account"]!;

        if (tx["TicketSequence"] is not null) throw new NotSupportedException("probe: the Batch path sends no ticketed transaction");

        uint sequence = (uint)Number(tx["Sequence"]);
        uint lastLedger = (uint)Number(tx["LastLedgerSequence"]);
        int fee = checked((int)Number(tx["Fee"]));

        if (type != "Batch") return new Pending(hash, SoloKind, account, sequence, lastLedger, fee, MemoOf(tx), []);

        JsonArray raw = (JsonArray)tx["RawTransactions"]!;
        InnerMemo[] inners = new InnerMemo[raw.Count];

        for (int i = 0; i < raw.Count; i++)
        {
            JsonObject inner = raw[i]!["RawTransaction"]!.AsObject();
            if (!string.Equals((string?)inner["Account"], _writer, StringComparison.Ordinal)) throw new NotSupportedException("probe: an inner of another account");

            inners[i] = new InnerMemo(MemoOf(inner), (uint)Number(inner["Sequence"]));
        }

        return new Pending(hash, OuterKind, account, sequence, lastLedger, fee, NoMemo, inners);
    }

    /// <summary>A chunk memo that matches the source becomes its index; anything else is kept as text.</summary>
    private int MemoOf(JsonObject tx)
    {
        if (tx["Memos"] is not JsonArray { Count: > 0 } memos || memos[0]?["Memo"] is not JsonObject memo) return NoMemo;

        string? type = (string?)memo["MemoType"];
        string? data = (string?)memo["MemoData"];
        string? format = (string?)memo["MemoFormat"];

        if (format is null && MemoLayout.ChunkIndexOf(type) is long index && index <= int.MaxValue
            && string.Equals(data, ChunkHex(index), StringComparison.OrdinalIgnoreCase))
        {
            return (int)index;
        }

        lock (_gate)
        {
            _otherMemos.Add((type, data, format));
            return -2 - (_otherMemos.Count - 1);
        }
    }

    private string ChunkHex(long index)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MemoLayout.ChunkBytes);

        try
        {
            int read = _file.ReadChunk(index, buffer);
            return MemoLayout.Hex(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Called under the gate.</summary>
    private string Apply(Pending pending)
    {
        long openLedger = _current + 1;
        uint next = _open.GetValueOrDefault(pending.Account, 1u);

        if (pending.Sequence < next) return "tefPAST_SEQ";
        if (pending.LastLedger != 0 && pending.LastLedger < openLedger) return "tefMAX_LEDGER";

        if (pending.Sequence > next)
        {
            if (!_held.Exists(held => held.Hash.AsSpan().SequenceEqual(pending.Hash))) _held.Add(pending);
            return "terPRE_SEQ";
        }

        _openLedger.Add(pending);
        _open[pending.Account] = pending.Sequence + 1;

        while (_held.Find(held => held.Account == pending.Account && held.Sequence == _open[pending.Account]) is { } ready)
        {
            _held.Remove(ready);
            _openLedger.Add(ready);
            _open[ready.Account] = ready.Sequence + 1;
        }

        return "tesSUCCESS";
    }

    /// <summary>Called under the gate.</summary>
    private void CloseLedger()
    {
        int ledger = checked((int)(_current + 1));

        foreach (Pending pending in _openLedger) Write(pending, ledger);
        _openLedger.Clear();

        // Nothing held can apply in a ledger past its deadline.
        _held.RemoveAll(held => held.LastLedger != 0 && held.LastLedger < ledger + 1);

        foreach ((string account, uint validated) in _validated) _open[account] = Math.Max(_open.GetValueOrDefault(account, 1u), validated);

        _current = ledger;
    }

    /// <summary>Called under the gate.</summary>
    private void Write(Pending pending, int ledger)
    {
        // Applied in a ledger whose earlier transactions already took this sequence: no record.
        if (pending.Sequence != _validated.GetValueOrDefault(pending.Account, 1u)) return;

        _validated[pending.Account] = pending.Sequence + 1;
        int slot = AddSlot(pending.Hash, pending.Fee);

        if (pending.Kind == SoloKind)
        {
            AddRow(Row.Of(ledger, pending.Memo, pending.Sequence, SoloKind, failed: false, slot));
            return;
        }

        int outer = AddRow(Row.Of(ledger, NoMemo, pending.Sequence, OuterKind, failed: false, slot));

        foreach (InnerMemo inner in pending.Inners)
        {
            uint expected = _validated.GetValueOrDefault(_writer, 1u);
            if (inner.Sequence != expected) continue;

            _validated[_writer] = expected + 1;
            bool failed = inner.Memo >= 0 && _failOnce.Remove(inner.Memo);
            AddRow(Row.Of(ledger, inner.Memo, inner.Sequence, InnerKind, failed, outer));
        }
    }

    /// <summary>Called under the gate.</summary>
    private int AddRow(Row row)
    {
        if (_rowCount == _rows.Length) Array.Resize(ref _rows, _rows.Length + _rows.Length / 2);

        _rows[_rowCount] = row;
        return _rowCount++;
    }

    /// <summary>Called under the gate.</summary>
    private int AddSlot(byte[] hash, int fee)
    {
        if (_slotCount == _fees.Length)
        {
            Array.Resize(ref _fees, _fees.Length + _fees.Length / 2);
            Array.Resize(ref _hashes, _fees.Length * 32);
        }

        hash.CopyTo(_hashes.AsSpan(_slotCount * 32, 32));
        _fees[_slotCount] = fee;
        return _slotCount++;
    }

    private LedgerTransaction Record(Row[] rows, int count, int index)
    {
        Row row = rows[index];
        int position = index - FirstRowAt(rows, count, row.Ledger);
        string hash;
        string account;
        string type;
        long fee;
        string? parent = null;

        lock (_gate)
        {
            switch (row.Kind)
            {
                case OuterKind:
                    (hash, account, type, fee) = (HashHex(row.Link), _payer, "Batch", _fees[row.Link]);
                    break;
                case SoloKind:
                    (hash, account, type, fee) = (HashHex(row.Link), _writer, "AccountSet", _fees[row.Link]);
                    break;
                default:
                    int outerSlot = rows[row.Link].Link;
                    parent = HashHex(outerSlot);
                    hash = InnerHash(outerSlot, index);
                    (account, type, fee) = (_writer, "AccountSet", 0);
                    break;
            }
        }

        (string? memoType, string? memoData, string? memoFormat) = MemoText(row.Memo);
        string result = row.Failed ? RetriedResult : "tesSUCCESS";

        return new LedgerTransaction(hash, row.Ledger, account, !row.Failed, true, result, memoType, memoData, memoFormat)
        {
            TransactionType = type,
            Sequence = row.Sequence,
            Fee = fee,
            ParentBatchID = parent,
            TransactionIndex = position,
        };
    }

    private (string? Type, string? Data, string? Format) MemoText(int memo)
    {
        if (memo == NoMemo) return (null, null, null);
        if (memo >= 0) return (MemoLayout.ChunkMarkerHex(memo), ChunkHex(memo), null);

        lock (_gate) return _otherMemos[-2 - memo];
    }

    /// <summary>Called under the gate.</summary>
    private string HashHex(int slot) => Convert.ToHexString(_hashes, slot * 32, 32);

    /// <summary>Called under the gate. An inner's hash is not the outer's business; any stable, distinct value will do.</summary>
    private string InnerHash(int outerSlot, int row)
    {
        Span<byte> input = stackalloc byte[36];
        _hashes.AsSpan(outerSlot * 32, 32).CopyTo(input);
        BitConverter.TryWriteBytes(input[32..], row);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>The first row at or after <paramref name="ledger"/>; <paramref name="count"/> when there is none.</summary>
    private static int FirstRowAt(Row[] rows, int count, long ledger)
    {
        int low = 0;
        int high = count;

        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (rows[middle].Ledger < ledger) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private static long Number(JsonNode? node) =>
        node is null ? 0 : long.Parse(node.ToJsonString().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture);
}
```

Build it: `dotnet build tests/XrplFile.MemoryProbe/XrplFile.MemoryProbe.csproj` — expect no warnings. Then a smoke run on a small size, which must print one JSON line and exit 0:

Run: `dotnet run --project tests/XrplFile.MemoryProbe -- 3000000`
Expected: `{"Bytes":3000000,"Chunks":3000,"PeakPrivateBytes":…,"FakeBytes":…,"Seconds":…}`.

- [ ] **Step 3: The test that runs it**

In `tests/XrplFile.Core.Tests/XrplFile.Core.Tests.csproj`, add:

```xml
  <ItemGroup>
    <!--
      Built with the tests, never loaded by them: PublicationMemoryTests runs it as a child process
      (spec S27), so the peak it reports is the publication's and not the test host's.
    -->
    <ProjectReference Include="..\XrplFile.MemoryProbe\XrplFile.MemoryProbe.csproj" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>MemoryProbePath</_Parameter1>
      <!-- Path.Combine, not backslashes: the path must resolve on Linux too (VmHWM there). -->
      <_Parameter2>$([System.IO.Path]::Combine('$(MSBuildThisFileDirectory)', '..', 'XrplFile.MemoryProbe', 'bin', '$(Configuration)', '$(TargetFramework)', 'XrplFile.MemoryProbe.dll'))</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
```

`tests/XrplFile.Core.Tests/PublicationMemoryTests.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec S27: a run's memory is bounded by a round, not by the file.</summary>
[Trait("Category", "Slow")]
public sealed class PublicationMemoryTests
{
    private sealed record Peak(long Bytes, long Chunks, long PeakPrivateBytes, long FakeBytes, double Seconds)
    {
        public long RunBytes => PeakPrivateBytes - FakeBytes;
    }

    private static string ProbePath =>
        typeof(PublicationMemoryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "MemoryProbePath").Value!;

    private static async Task<Peak> ProbeAsync(long bytes, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ProbePath);
        start.ArgumentList.Add(bytes.ToString(CultureInfo.InvariantCulture));

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("the probe did not start");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string printed = await output;
        Assert.True(process.ExitCode == 0, $"the probe for {bytes:N0} bytes exited with {process.ExitCode}: {await errors}");

        using JsonDocument result = JsonDocument.Parse(printed.Trim().Split('\n')[^1]);
        JsonElement root = result.RootElement;

        return new Peak(
            root.GetProperty("Bytes").GetInt64(), root.GetProperty("Chunks").GetInt64(), root.GetProperty("PeakPrivateBytes").GetInt64(),
            root.GetProperty("FakeBytes").GetInt64(), root.GetProperty("Seconds").GetDouble());
    }

    [Fact]
    public async Task A_runs_peak_memory_is_bounded_and_grows_little_with_the_file()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(3));

        Peak small = await ProbeAsync(50_000_000, timeout.Token);
        Peak middle = await ProbeAsync(200_000_000, timeout.Token);
        Peak large = await ProbeAsync(800_000_000, timeout.Token);

        foreach (Peak peak in new[] { small, middle, large })
        {
            Assert.Equal(FileTree.ChunkCount(peak.Bytes, MemoLayout.ChunkBytes), peak.Chunks);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{peak.Bytes:N0} bytes: peak private {peak.PeakPrivateBytes:N0}, the fake's records {peak.FakeBytes:N0}, the run {peak.RunBytes:N0}; {peak.Seconds:F0} s");
        }

        // Absolute: the raw peak, the fake's records included, as the spec states it.
        Assert.True(large.PeakPrivateBytes < 300_000_000, $"the 800 MB run peaked at {large.PeakPrivateBytes:N0} bytes");

        // Slope: net of the fake's preallocated records, which grow with the file by design.
        Assert.True(large.RunBytes - small.RunBytes < 40_000_000, $"from 50 MB to 800 MB the peak grew by {large.RunBytes - small.RunBytes:N0} bytes");
    }
}
```

- [ ] **Step 4: Register the project and keep CI off it**

Run: `dotnet sln XrplFile.sln add tests/XrplFile.MemoryProbe/XrplFile.MemoryProbe.csproj`
In `XrplFile.Libraries.slnf`, add `"tests\\XrplFile.MemoryProbe\\XrplFile.MemoryProbe.csproj",` after the `XrplFile.Keys.Tests` line.

In `.github/workflows/test.yml`, the three unit runs end with `-- -trait- "Category=Integration" -trait- "Category=Slow"`, and the integration run becomes `-- -trait "Category=Integration" -trait- "Category=Slow" ${{ matrix.classes }}`. Add above the unit runs:

```yaml
      # Category=Slow is excluded everywhere: the memory test (spec S27) runs for tens of minutes and
      # the 50 MB stand runs (Task 21) for longer. They are run by hand before a release.
```

- [ ] **Step 5: Run it**

Run (Release, so the figures are the shipped code's): `dotnet build XrplFile.Libraries.slnf --configuration Release` then `dotnet run --project tests/XrplFile.Core.Tests --configuration Release --no-build -- -class XrplFile.Tests.PublicationMemoryTests -showLiveOutput`
Expected: PASS, with three lines of figures; keep them for the pull request description. If an assertion fails, do not loosen it: find what grows (a `dotnet-counters`/`dotnet-gcdump` of the 800 MB probe shows the type) and fix it in the task that owns that structure.
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait- "Category=Integration" -trait- "Category=Slow"` — the unit suite, as CI runs it, does not start the probe.

- [ ] **Step 6: Commit**

```bash
git add tests XrplFile.sln XrplFile.Libraries.slnf .github/workflows/test.yml
git commit -m "test(core): measure a run's peak memory in a child process

XrplFile.MemoryProbe publishes 50, 200 and 800 MB through Batch against a
compact fake ledger, stops each run halfway and resumes it, with retried
chunks and lagging history, and reports its peak private bytes. The test
asserts the 800 MB run stays under 300 MB and grows less than 40 MB over the
50 MB one. It is Category=Slow, which CI now excludes."
```

The measured figures go into the pull request description of the branch, with the date and the host they were measured on.

---

### Task 21: Fifty megabytes on the stand (spec 6.3)

**Files:**
- Modify: `tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs` (two tests, two helpers)

**Interfaces:**
- Consumes: Task 1 (`FilePublicationSource.Open`), Task 3 (session source overloads), Task 2 (`PublicationReport.ChunkCount`), Task 20 (CI excludes `Category=Slow`); the class's `KeysAsync`, `Factory`, `Session`, `StoppingSink`, `ChunkRecordsAsync`, `Endpoint`.
- Produces: `Fifty_megabytes_go_through_Batch_from_a_held_file_and_read_back` and `A_stopped_fifty_megabyte_run_resumes_from_its_path`, both `[Trait("Category", "Slow")]` on top of the class's `Integration` trait.

- [ ] **Step 1: Write the tests**

In `tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs`, add:

```csharp
    /// <summary>The reader's own default stops at 20 MB; these files are larger, and the library's default is not what is tested.</summary>
    private static ReaderOptions FiftyMegabytes { get; } = new() { MaxBytes = 64L * 1024 * 1024 };

    private string WriteFile(string name, int size)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, VectorStream.Bytes(name, size));
        return path;
    }

    private static async Task AssertReadsBackAsync(ILedgerSource source, XrplFileLink link, string path, CancellationToken token)
    {
        ReadResult read = await new XrplFileReader(source, FiftyMegabytes).ReadAsync(link, token);
        Assert.True(read.Bytes.AsSpan().SequenceEqual(await File.ReadAllBytesAsync(path, token)), "the bytes read back differ from the file");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Fifty_megabytes_go_through_Batch_from_a_held_file_and_read_back()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(90));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink sink = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, sink, token);
            string path = WriteFile("stand-50mb.bin", 50_000_000);
            PublicationReport report;

            await using (FilePublicationSource file = FilePublicationSource.Open(path))
            {
                PublicationSession session = await PublicationSession.StartAsync(
                    Factory(sink, source, keys, keys.Payer), source, file, "application/octet-stream", "stand-50mb.bin",
                    keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, null, token);
                report = await session.RunAsync(null, token);
            }

            Assert.Equal(50_000, report.ChunkCount);
            await AssertReadsBackAsync(source, report.Link, path, token);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"50 MB: {report.Took.TotalMinutes:F1} min, payer paid {report.PayerFeeDrops:N0} drops");
        }
        finally
        {
            await client.Disconnect();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_stopped_fifty_megabyte_run_resumes_from_its_path()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(90));
        CancellationToken token = timeout.Token;
        await using LedgerPump pump = new(Endpoint);
        IXrplClient client = new XrplClient(Endpoint);
        await client.Connect(token);

        try
        {
            XrplClientLedgerSink inner = new(client);
            XrplClientLedgerSource source = new(client);
            await using Keys keys = await KeysAsync(client, inner, token);
            string path = WriteFile("stand-50mb-resume.bin", 50_000_000);
            string statePath;
            long startedIn;

            using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                await using FilePublicationSource file = FilePublicationSource.Open(path);
                StoppingSink stopping = new(inner, stop, stopAfter: 1_000);
                PublicationSession first = await PublicationSession.StartAsync(
                    Factory(stopping, source, keys, keys.Payer), source, file, "application/octet-stream", "stand-50mb-resume.bin",
                    keys.Writer.Account, "writer", keys.Payer.Account, "payer", "1", Session, null, stop.Token);
                statePath = first.StatePath;
                startedIn = first.State.StartedInLedger;

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RunAsync(null, stop.Token));
            }

            // Opened again by its path, as the application's resume opens the file it recorded (spec S7).
            PublicationReport report;

            await using (FilePublicationSource reopened = FilePublicationSource.Open(path))
            {
                PublicationSession resumed = await PublicationSession.ResumeAsync(
                    Factory(inner, source, keys, keys.Payer), source, reopened, statePath, keys.Payer.Account, "payer", Session, null, token);
                report = await resumed.RunAsync(null, token);
            }

            await AssertReadsBackAsync(source, report.Link, path, token);

            List<(long Index, string? Parent)> chunks = await ChunkRecordsAsync(source, keys.Writer.Account, startedIn, token);
            Assert.Equal(50_000, chunks.Select(chunk => chunk.Index).Distinct().Count());
        }
        finally
        {
            await client.Disconnect();
        }
    }
```

- [ ] **Step 2: Run them on the stand**

Check what is running first, and start only the `xrpl-file-ci` project:

```bash
docker ps --format '{{.Names}} {{.Label "com.docker.compose.project"}}'
docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml up -d
```

Run (PowerShell): `$env:XRPL_FILE_NODE_URL = 'ws://localhost:16006'; dotnet run --project tests/XrplFile.Core.Tests -- -method XrplFile.Tests.BatchPublicationStandaloneTests.Fifty_megabytes_go_through_Batch_from_a_held_file_and_read_back -method XrplFile.Tests.BatchPublicationStandaloneTests.A_stopped_fifty_megabyte_run_resumes_from_its_path -showLiveOutput`
Expected: PASS, each in well under the 90-minute timeout; the first prints its time and the payer's fees.
Run: `dotnet run --project tests/XrplFile.Core.Tests -- -trait "Category=Integration" -trait- "Category=Slow" -class XrplFile.Tests.BatchPublicationStandaloneTests` — the class as CI runs it: the two new tests are not selected.
Then: `docker compose -p xrpl-file-ci -f .ci-config/docker-compose.ci.yml -f .ci-config/docker-compose.local-ports.yml down`.

If a run stalls, read the stand's log (`docker compose -p xrpl-file-ci logs --tail 200`) and the test's live output before changing anything: a node that stops accepting ledgers is not a defect of this plan's code.

- [ ] **Step 3: Commit**

```bash
git add tests/XrplFile.Core.Tests/BatchPublicationStandaloneTests.cs
git commit -m "test(core): fifty megabytes through Batch on the stand, fresh and resumed

One run publishes 50 MB from a held FilePublicationSource and reads it back;
another is stopped after 1,000 outers, resumed from the file's path through a
new source, and lands every chunk. Both are Integration and Slow."
```

---

### Task 22: Acceptance in the MauiLab VM against testnet, with a 300 MB video (by hand)

Spec 6.5. Nothing here is automated; the deliverable is a report with screenshots and figures. A full 1.7 GB run is not part of acceptance (about 35 XRP of testnet funds); the 300 MB run exercises every path.

**Files:**
- Create: `docs/superpowers/reports/2026-10-25-streaming-publication/report.md` and screenshots in the same folder
- Scratch only (never committed): `vm-cdp.ps1` (the CDP helper of `docs/superpowers/plans/2026-10-13-dark-theme.md` Task 8 Step 2, copied verbatim into your scratchpad), `sample-private-bytes.ps1`

**Interfaces:**
- Consumes: the finished application (Tasks 1–21); the `maui-vm-lab` skill and lab at `D:\Lab\maui-vm-lab`; testnet `wss://s.altnet.rippletest.net:51233`.

- [ ] **Step 1: Deploy and launch non-elevated with DevTools**

Read the `maui-vm-lab` skill first. Never start `XrplFile.App.exe` on the host. On the host:

```powershell
cd D:\Lab\maui-vm-lab
.\06-deploy-and-run.ps1 -ProjectPath E:\GIT\XRPL\xrpl-file\app\XrplFile.App\XrplFile.App.csproj -ExeName XrplFile.App.exe -Action Deploy
```

In the guest, through PowerShell Direct, register and start the non-elevated task exactly as dark-theme Task 8 Step 1 does (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, scheduled task `XrplPlain`, `-RunLevel Limited`). Save the CDP helper as `vm-cdp.ps1` in your scratchpad and check `.\vm-cdp.ps1 -Eval 'document.title'`. Unlock the vault (`labtestvault1`).

- [ ] **Step 2: A five-minute sleep timeout in the guest**

In the guest, record the current values, then set both to five minutes:

```powershell
powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE
powercfg /change standby-timeout-ac 5
powercfg /change standby-timeout-dc 5
```

Write the recorded values in the report; Step 8 restores them.

- [ ] **Step 3: Keys and the file**

In Accounts, create a writer key and a payer key on network `testnet` and fund each from the Funding screen's testnet faucet. The payer needs about 6 XRP of fees for 300 MB (the spec's 35 XRP for 1.7 GB, scaled) plus its reserve; fund it again if one faucet grant is short of the estimate's "needs" line. Screenshot Accounts (`01-accounts.png`).

Use a video of 280 to 320 MB from the host. If there is none, make one (ffmpeg on the host; 4 Mbit/s for 10 minutes is about 300 MB):

```powershell
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 600 -c:v libx264 -b:v 4M -pix_fmt yuv420p "$env:TEMP\accept-300mb.mp4"
(Get-Item "$env:TEMP\accept-300mb.mp4").Length
(Get-FileHash "$env:TEMP\accept-300mb.mp4" -Algorithm SHA256).Hash
```

Copy it into the guest (`Copy-VMFile` as the skill shows) to `C:\Lab\accept-300mb.mp4`. Record its size and SHA-256.

- [ ] **Step 4: The estimate, its summary and its checkbox**

Publish › choose the file › select the writer key › "Paid by" shows the payer key. Expected: no refusal (the file is over 64 MiB, and a payer is chosen); choose "Nobody" once and check the screen says "Choose a payer — files larger than 64 MiB publish through Batch." (screenshot `02-payer-required.png`), then choose the payer again.

Estimate. Expected: the card with "Time" as a range, and below it "This publication is large" with the duration, transactions, each account's needs and worst-case fees, and the checkbox "I understand the cost and the time"; Publish is disabled until it is ticked (screenshot `03-summary.png`). Record the range. Leave the screen with Back and estimate again: the checkbox is clear again. Tick it.

- [ ] **Step 5: Publish, and watch memory and sleep**

Save as `sample-private-bytes.ps1` in your scratchpad and start it in the guest through PowerShell Direct as a background job just before pressing Publish:

```powershell
$out = 'C:\Lab\private-bytes.csv'
'utc,privateBytes' | Set-Content -Encoding utf8 $out
while ($true) {
    $process = Get-Process XrplFile.App -ErrorAction SilentlyContinue
    if ($process) { "{0:o},{1}" -f (Get-Date).ToUniversalTime(), $process.PrivateMemorySize64 | Add-Content -Encoding utf8 $out }
    Start-Sleep -Seconds 30
}
```

Press Publish; note the time. Expected: the run line first says "Hashing the file — … of … chunks" (screenshot `04-hashing.png`), then writes. Screenshot the run line early and after a minute (`04-run.png`: rate, time left, elapsed). While it writes, run `powercfg /requests` in the guest: the SYSTEM section lists the application's request (paste the output into the report). Leave the guest untouched for more than five minutes; then check the run is still advancing and that the guest did not sleep:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 42 } -MaxEvents 5 -ErrorAction SilentlyContinue
```

Expected: no entry since the run started.

- [ ] **Step 6: Lock mid-run, then resume**

About ten minutes in, press Lock in the shell. Expected: the run stops at once (the run screen shows where it stopped), and the vault locks within ten seconds (screenshot `05-locked.png`). Unlock. Under Publish › Unfinished runs the row is there; press Resume. Expected, in order: no file dialog; "Hashing the file — … of … chunks"; "Reading the ledger history"; then the resume's summary "Resuming costs" with the checkbox and Continue disabled until it is ticked (screenshot `06-resume-confirm.png`); tick it and Continue; the fence wait, then writing; then "Published." (screenshot `07-published.png`). Note the time "Published." appears.

- [ ] **Step 7: Read it back in the application**

In the Read tab paste the link and look it up. Expected: the size and "About … to read" before the button (screenshot `08-read-lookup.png`). Read it and save it; compare the saved file's SHA-256 with Step 3's (screenshot `09-read.png`).

- [ ] **Step 8: Restore the guest, write the report and commit**

Stop the sampling job, copy `C:\Lab\private-bytes.csv` to the report folder, and restore the sleep timeouts recorded in Step 2.

`docs/superpowers/reports/2026-10-25-streaming-publication/report.md`: date, app commit, node, the file (size, SHA-256), the estimate's figures and duration range, wall time from Publish to "Published." (excluding the time locked) against that range, the peak and the shape of private bytes over the run (from the CSV, with the 300 MB S27 bound beside it), the `powercfg /requests` output and the absence of sleep events, the Lock and resume (stop seen, resume confirmation seen, preparation lines seen, same bytes read back), the payer's spend from its balance before and after, and every defect found, each with a screenshot. Defects are not fixed in this task; each becomes its own follow-up.

```bash
git add docs/superpowers/reports/2026-10-25-streaming-publication
git commit -m "docs(reports): streaming publication acceptance on testnet"
```

---

## Spec questions

Defects or gaps found while planning, each with the ruling this plan follows. The spec is not changed by this plan.

1. **S22 names no error and no sentence for a landed manifest the reader rejects.** *Ruling:* a new `XrplFileError.ManifestRejected` carrying the written link on a new `XrplFileException.Link`, with the sentence "The manifest landed, but reading it back did not give this file's description, so its link must not be shared. Publish the file again from a fresh writer key." followed by " The link that was written: {link}." A manifest the node does not return yet is not a rejection: `ManifestNotFound`, and `NodeLacksHistory` (what the reader throws when `tx` misses on a node whose history starts late, as many public testnet nodes and load-balanced clusters do), are read again a bounded number of times and then end as `ValidationTimeout`, which stays resumable (Task 7).
2. **S12 and S25 do not say which figures "worst case" and "each account's ceiling" are, nor how a re-estimate is compared with them.** *Ruling:* both are the estimate's worst-case fees per account (`FeeDropsWorstCase` one-account; `WriterFeeDropsWorstCase` and `PayerFeeDropsWorstCase` Batch). The fee-unit ceiling is B15's ceiling on the Batch path and the per-transaction fee on the one-account path. On the Batch path only exceeding a ceiling refuses: the re-estimated fee unit is compared with the approved ceiling, each worst case is computed at the approved ceiling (the most B26 lets the run spend) and compared with its approved figure, and balances are checked against those same figures; a unit that rises from 15 to 18 under a ceiling of 30 runs. The one-account strategy's fee is fixed per transaction, so there a higher fee refuses. A refusal is `ApprovalExceeded` (Tasks 11, 13).
3. **The spec names the new state facts but not their JSON names.** *Ruling:* `Failure` with the value `"WriterWindowConflict"` (S17) and `ManifestStatus` with the value `"landed, rejected"` (S22), both optional and written only when set, so existing state files read unchanged (Tasks 5, 7).
4. **S17 does not say whether a one-account run fails on a conflicting copy, nor how a round's sweep would see one.** *Ruling:* only Batch runs fail on it, at the reconciliation of a version-3 resume. Within a round no separate check exists: identity (B12) runs first and refuses with `AccountInUse` every writer record that is not an inner of the round's own outers, and those carry the round's own bytes, so a conflicting copy written during a round surfaces as B12 — what the spec's own rationale for S17 relies on. A one-account run keeps rule I12 (a copy with other bytes is a straggler and is ignored), and its full read-back catches any block such a copy would break (Task 5).
5. **S5 lists the candidate counts among session-wide state carried "across rounds", which suggests `BatchContext`.** In the code the counts were per `SubmitAllAsync` call. *Ruling:* a `byte[]` per pass, owned by the pass's `SubmitAllAsync` call and handed to each of its rounds; budget, fence and ledger floor stay in `BatchContext`. A memo belongs to exactly one pass, so a fourth candidate is still refused across rounds (Task 4).
6. **S25 asks for a quote of the remaining work on resume; the one-account resume has no coverage figure before it runs.** *Ruling:* the one-account resume quotes the whole file's estimate (conservative), with `MissingChunks` from the landed set; the Batch resume quotes the remaining work. On a one-account resume the run screen's "Resuming costs" therefore shows the whole file's cost, an overstatement and never an understatement (Tasks 11, 14).
7. **S20 keeps list records "below this file's list count", but the list count is only known once the plan is packed with real ledgers.** *Ruling:* the bound is `PublicationEstimator.BlockListMemoCount(blocks)`, the packer's count at nine-digit ledgers, which is never below the real count; entries must also carry this file's block hashes (Task 6).
8. **S11 prices the duration before a run; it does not say what the run screen's "time left" adds for rounds, and it asks that the verification ETA use "the real planned traversal once the chunks have landed".** *Ruling:* on the Batch path the run screen adds one settle per round still to come (the rounds after the current one in this pass, plus later passes); the ticket path keeps one per pass (B21). **Deviation from S11, with its reason:** the read-back's time left is blocks still to read at the measured per-block rate, and before the first block is read, 1,024 records per block at 0.7 s per 400. The records a sampled walk crosses include every other transaction of the writer in the same ledgers, which the writer cannot know before it reads them; what it does know, the run's own chunk records per sampled block, is at most 1,024, so the prior is the planned traversal to within one short block, and the measured rate replaces it after the first block. Reporting the read-back in records instead would change that stage's unit on both paths, on the run screen and in its tests, for a figure shown for seconds (Task 14).
9. **S12's "transactions" is ambiguous on the Batch path.** *Ruling:* every transaction submitted: `Memos + Batches` (each inner and solo memo, and each outer) (Task 13).
10. **S7 and S24 say a resume opens the recorded path, but not what happens when that file no longer matches.** *Ruling:* only a refusal for the file's size or root marks it: the session sets `XrplFileException.SourceChanged` on those two refusals and on no other `ResumeMismatch` (a finished run, another chunk size, another writer). The run then fails with "The file at {path} has changed since the run started. Choose the original file." (a picked file: "That file is not the one this run was publishing."), and the state path joins an in-memory set of rejected sources, so the next Resume of that row opens the file picker instead of the recorded path. The set lives on the run singleton: after the application restarts, a changed recorded file is tried once more and refused again, which costs a hash of the file and nothing on the ledger (Tasks 3, 12).
11. **S18 does not say what the trace header names for a fresh run, whose state file does not exist yet.** *Ruling:* the header says "(no state file)" and the end line names the state file (Task 8).
12. **`PublishErrorMessages`' `TooLarge` sentence speaks of the reader's limit, which no longer applies to publishing.** *Ruling:* it becomes S1's "This application publishes files up to 16 GiB." (Task 15); `ReadErrorMessages` keeps its own sentence for the library's refusal.
13. **S6 keeps "transaction index in the ledger", which `LedgerTransaction` does not carry.** *Ruling:* `LedgerTransaction.TransactionIndex` (`int?`, from `meta.TransactionIndex`) is added and parsed (Task 4).
14. **S27 does not say how the child process is built or how "peak private bytes" is read off.** *Ruling:* a separate project `tests/XrplFile.MemoryProbe` (an executable referenced by the Core tests for build order only, its path composed with `Path.Combine` so it resolves on Windows and Linux); `Process.PeakPagedMemorySize64` (the peak commit charge) on Windows, `VmHWM` on Linux, which keeps no peak of private bytes; `Category=Slow`, which CI excludes (Task 20).
15. **S27's slope bound (40 MB from 50 MB to 800 MB) would count the fake ledger's own records,** about 20 MB at 800 MB even at 16 bytes a record. *Ruling:* the absolute bound is asserted on the raw peak, as the spec says: peak(800) < 300 MB, the fake included. Only the slope subtracts the fake: it preallocates its record arrays and reports their exact size, and peak(800) − peak(50) is taken net of the two sizes. Both figures are printed (Task 20).
16. **S1's refusal "Choose a payer …" on a network without Batch, where no payer can be chosen.** *Ruling:* the same sentence; the "Paid by" note already says Batch is unavailable on that network, and the two together say what is possible (Task 15).
17. **S14 says Lock waits "a bounded time" for the run.** *Ruling:* 10 seconds, `UnlockModel.RunStopTimeout`, the same bound the window-close handler uses with `PublicationRun.StopAsync` (Task 17).
18. **S9 says "the list equals the plan"; compared with the list the writer built, that compares the plan with itself.** *Ruling:* the plan is compared with the block list as the read-back reads it from the ledger (`BlockListAsync`, rooted to the manifest root), ledgers included, on the Batch and the one-account path. The root covers block hashes and not ledgers, so this is the comparison that can fail (Task 7).
19. **The spec does not say what a failed read of the held file mid-run tells the person.** Today's `IOException` clause says the state could not be written. *Ruling:* the run wraps the source it hands the session, so a failed read says "The file at {path} could not be read while it was being published. Make it available again and resume the run." and the state, which is intact, stays resumable (Task 12).

## Self-review

**Spec coverage.**

| Decision | Task(s) |
|---|---|
| S1 | 3 (the session refuses above 16 GiB), 15 |
| S2 | 1 |
| S3 | 1 |
| S4 | 3 |
| S5 | 4 |
| S6 | 4 |
| S7 | 3, 12 |
| S8 | 2 |
| S9 | 7 |
| S10 | removed in the spec |
| S11 | 10 (estimate), 13 (durations), 14 (run screen) |
| S12 | 13 |
| S13 | 16 |
| S14 | 17 |
| S15 | 18 |
| S16 | 15 |
| S17 | 5 |
| S18 | 8 |
| S19 | 2 (types), 4 (Batch tracking) |
| S20 | 6 |
| S21 | 7 |
| S22 | 7 |
| S23 | 2 |
| S24 | 12 |
| S25 | 11 (library), 13 (the Estimate screen's approval and a ceiling exceeded at the run), 14 (resume confirmation) |
| S26 | 9 |
| S27 | 20 (the reader part in 9) |
| Spec 5.3 preparation stages | 3 and 6 (reports), 19 (words) |
| Spec 6.1 compact records, ledger floor across rounds | 4 |
| Spec 6.3 stand | 21 |
| Spec 6.5 acceptance | 22 |
| Unfinished v2/v3 state files keep resuming | 2 (fixtures hand-written in the format of `bc37d07`, guarded byte for byte; `LegacyStateTests`) |

**Review Focus.** Each line has its test in the owning task: exact chunk and block multiples (Tasks 1 and 3), `MaxRoundMemos` + 1 as a solo (Tasks 4 and 10), rename and delete refused while held (Task 1), leaving Estimate mid-estimate (Task 12), Lock while a resume awaits confirmation (Task 17).

**Every task ends green.** Each new `XrplFileError` gets its sentence in both message classes in the task that adds it (Tasks 5, 7, 11); every existing test a signature change breaks is migrated in that task (Task 2's list, Task 4's `BatchRegistryTests` and `BatchIdentityTests`, Task 12's request migration).

**Placeholders.** None: every step names its files, shows the code it adds or the exact lines it replaces, and gives the command and the expected outcome. Where a task edits a long existing method, it names the statement to replace and shows the replacement; the rest of the method is unchanged.

**Type consistency.** Names used across tasks, checked against their producing task: `IPublicationSource`, `FilePublicationSource.Open`, `VectorPublicationSource`, `CountingPublicationSource` (1); `SubmittedMemos`, `LandedSet`, `ChunkLanding`, `PublicationReport.ChunkCount` (2); the session's source overloads with `IProgress<PublishProgress>? preparation` before the token, `XrplFileException.SourceChanged` (3); `BatchStrategyOptions.DefaultMaxRoundMemos`, `LedgerTransaction.TransactionIndex`, `BatchStrategy.RoundFences` (4); `XrplFileError.WriterWindowConflict`, `PublicationState.Failure`, `ReadErrorMessagesTests.The_streaming_publication_errors_share_the_publishing_only_sentence` (5); stage `"history"` with total 0, `FileTree.TryDecodeFragment` (6); `XrplFileError.ManifestRejected`, `XrplFileException.Link`, `PublicationState.ManifestStatus`, `PublicationCheck.RequireListed` (7); `BatchPublicationEstimate.Rounds` (10); `SpendingApproval`, `SpendingApproval.AtCeiling`, `ResumeQuote`, `ResumeApproval`, `SessionOptions.Approval`, `SessionOptions.ApproveResume` (11); `PublishRequest.Size`, `.Source`, `.Approval`, `.SourceRecorded`, `EstimateModel.HandOff`, `DiscardAsync`, `Requests.Fresh`, `Requests.UnboundedFeeDrops`, `FakeFiles`, `FailingSecondReadSource` (12); `CostSummary`, `PublicationDuration`, `DurationText`, `EstimateModel.ConfirmationLabel` (13); `RunSnapshot.Confirmation`, `PublicationRun.AnswerConfirmation`, `BatchTestWait`, `ResumeConfirmationTests.ReadyAsync` / `InterruptedExpensiveAsync` / `Resuming` (14); `PublicationRun(..., openSource, keepAwake)` in that order (12, 16); `ActivityLease`, `UnlockModel(vault, isDefault, run)` (17).
