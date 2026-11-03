using System.Runtime.CompilerServices;

using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ReadRunTests : IDisposable
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeFilePicker _picker = new();

    /// <summary>
    /// Every <see cref="ReadRun"/> this class has constructed, so <see cref="Dispose"/> can put each
    /// one away. A succeeded run's temporary file survives until something resets it --
    /// <see cref="ReadRun.Reset"/>, <see cref="ReadRun.StopAsync"/> or a following
    /// <see cref="ReadRun.Start"/> -- and most of these tests end on
    /// Succeeded without calling any of those themselves, which otherwise leaves one
    /// "xrpl-file-read-*" directory behind per test, every run, forever.
    /// </summary>
    private readonly List<ReadRun> _runs = [];

    private ReadRun Track(ReadRun run)
    {
        _runs.Add(run);
        return run;
    }

    public void Dispose()
    {
        foreach (ReadRun run in _runs)
        {
            try
            {
                // ReadRun.Dispose, not Reset(): Reset() returns immediately while the run is still
                // Running (by design -- it must not tear down a run's state out from under its own
                // background task), which would leave a gated test's run that never got released
                // cleaned up by nothing at all. Dispose() cancels first, so even a run parked at a
                // gate this test forgot to release still gets its token and its temporary file put
                // away -- it is exactly the method ReadRun grew IDisposable for.
                run.Dispose();
            }
            catch (Exception)
            {
                // ReadRun.Dispose already swallows its own cleanup failures (DeleteTemporary's catch
                // is unconditional); this exists only in case a future change reintroduces one that
                // does not, so a flaky teardown here can never replace whatever the test itself
                // actually failed on -- the same reasoning PublishedFileFixture's own guarded cleanup
                // follows.
            }
        }
    }

    private async Task<(ReadRun Run, XrplFileLink Link, ManifestRecord Manifest)> ReadyAsync(
        CancellationToken token, string name = "photo.png", int size = 4096)
    {
        XrplFileLink link = await PublishAsync(_connections, name, "image/png", size);

        await using ILedgerConnection connection = await _connections.ConnectAsync(link.Network, token);
        ManifestRecord manifest = await new XrplFileReader(connection.Source).ManifestAsync(link, token);

        return (Track(new ReadRun(_connections, _picker)), link, manifest);
    }

    /// <summary>
    /// Seeds <paramref name="connections"/>'s fake ledger with a single-chunk file's manifest and
    /// chunk transaction, computed directly -- no signing, no submission, no
    /// <c>PublicationSession</c> round trip. <see cref="Resetting_a_run_that_is_still_going_changes_nothing"/>
    /// and <see cref="Disposing_during_a_save_clears_saving_the_same_way_start_and_reset_do"/> used
    /// to reach <see cref="ReadyAsync"/>, whose publish signs for real through <c>XrplWallet</c>.
    /// They were moved off it while a thread-pool defect in <c>XrplFileWriter</c> made a third heavy
    /// signer in this project enough to fail <c>PublicationRunTests.Progress_touches_the_idle_lock</c>;
    /// that defect is fixed and the test class that serialised them is gone. This seed stays because
    /// it was the right fixture regardless: neither test is about publishing -- one is about
    /// <see cref="ReadRun.Reset"/>, the other about <see cref="ReadRun.Dispose"/> -- and this reaches
    /// the same read paths a real publish would
    /// (a genuine <see cref="XrplFileReader.ManifestAsync"/> lookup and a genuine, gate-able
    /// <see cref="XrplFileReader.BlockAsync"/> scan, both cryptographically real -- the block hash
    /// really is <see cref="FileTree.BlockHash"/> over the chunk, and the reader really does check it)
    /// without any of the signing cost.
    /// </summary>
    private static async Task<(XrplFileLink Link, ManifestRecord Manifest)> SeedSyntheticFileAsync(
        FakeConnectionFactory connections, CancellationToken token, string name = "synthetic.bin", int size = 256)
    {
        const string writer = "rSyntheticWriter";
        const long chunkLedger = 2000;
        const long manifestLedger = chunkLedger + 1;

        byte[] bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;

        IReadOnlyList<byte[]> chunks = FileTree.Split(bytes, MemoLayout.ChunkBytes);

        // Guards this fixture staying the single-chunk, single-block shape it is documented as: a
        // size that grew past one chunk would silently need a second ledger transaction this helper
        // does not write, and BlockListAsync's own Blocks == 1 fast path (no AccountTransactionsAsync
        // call at all) would stop matching what the caller actually gets back.
        Assert.Single(chunks);

        string blockHash = Convert.ToHexString(FileTree.BlockHash(chunks));

        Manifest manifest = new(
            "application/octet-stream", size, MemoLayout.ChunkBytes, blockHash,
            chunkLedger, chunkLedger, Blocks: 1, Lists: 0, name);

        string chunkHash = SyntheticHash("chunk");
        string manifestHash = SyntheticHash("manifest");

        connections.Ledger
            .Add(new LedgerTransaction(
                chunkHash, chunkLedger, writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoLayout.ChunkMarkerHex(0), MemoLayout.Hex(chunks[0]), MemoFormatHex: null))
            .Add(new LedgerTransaction(
                manifestHash, manifestLedger, writer, Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoLayout.ManifestMarkerHex, MemoLayout.Utf8Hex(manifest.Encode()), MemoLayout.JsonFormatHex));

        XrplFileLink link = new("testnet", manifestHash);

        await using ILedgerConnection connection = await connections.ConnectAsync(link.Network, token);
        ManifestRecord record = await new XrplFileReader(connection.Source).ManifestAsync(link, token);

        return (link, record);
    }

    private static string SyntheticHash(string label) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));

    [Fact]
    public async Task A_read_starts_idle()
    {
        (ReadRun run, _, _) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReadState.Idle, run.Snapshot.State);
    }

    [Fact]
    public async Task A_read_succeeds_and_reports_progress()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Succeeded, snapshot.State);
        Assert.Equal(snapshot.BlocksTotal, snapshot.BlocksDone);
        Assert.Equal(4096, snapshot.BytesDone);
        Assert.Equal(4096, snapshot.BytesTotal);
        Assert.Equal("photo.png", snapshot.Name);
        Assert.Null(snapshot.Failure);
    }

    [Fact]
    public async Task What_was_read_is_byte_for_byte_what_the_library_reads()
    {
        // The differential test (design 3.1). Four things moved out of XrplFileReader.ReadAsync and
        // into this run: the ceiling, the history check, the assembly and the final length. If the
        // application ever assembles a file differently from the reference implementation, this is
        // what says so -- but only if the file genuinely has more than one block. With Blocks == 1,
        // XrplFileReader.BlockListAsync short-circuits identically on both sides (no block-list memos
        // are even read), and BlockAsync is then called exactly once on each side, comparing a single
        // array against itself regardless of how -- or whether -- the assembly loop actually works.
        // A block is 1024 chunks of up to 1000 bytes each (MemoLayout.ChunkBytes), so the smallest
        // file with more than one block is 1,024,001 bytes. 1_100_437 clears that with room to spare
        // (1101 chunks, the last one ragged at 437 bytes, across two blocks: 1024 full chunks and 77)
        // while keeping the fixture's chunk-transaction count close to the coordinator's own "about a
        // thousand" estimate -- see the report for the measured publish cost.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(
            TestContext.Current.CancellationToken, size: 1_100_437);

        // Asserted before the comparison, not only implied by the size above: if this fixture ever
        // drifted back to one block in some future edit, the comparison below would keep passing,
        // silently vacuous again -- which is exactly how it got here the first time.
        Assert.True(
            manifest.Manifest.Blocks > 1,
            "the fixture must produce more than one block, or this test cannot detect an assembly-order defect");

        Assert.True(run.Start(link, manifest));
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        await using ILedgerConnection connection =
            await _connections.ConnectAsync(link.Network, TestContext.Current.CancellationToken);
        ReadResult expected = await new XrplFileReader(connection.Source)
            .ReadAsync(link, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Bytes, _picker.SavedStream);
    }

    [Fact]
    public async Task Saving_suggests_the_name_from_the_manifest()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("photo.png", _picker.SavedStreamName);
        Assert.True(run.Snapshot.Saved);
    }

    [Fact]
    public async Task A_file_published_with_no_name_still_gets_a_suggestion()
    {
        XrplFileLink link = await PublishAsync(_connections, name: null, "application/octet-stream", 2048);

        await using ILedgerConnection connection =
            await _connections.ConnectAsync(link.Network, TestContext.Current.CancellationToken);
        ManifestRecord manifest = await new XrplFileReader(connection.Source)
            .ManifestAsync(link, TestContext.Current.CancellationToken);

        ReadRun run = Track(new ReadRun(_connections, _picker));
        Assert.True(run.Start(link, manifest));
        await run.Completion;
        await run.SaveAsync(TestContext.Current.CancellationToken);

        // Pinned to the actual fallback rather than merely "not blank": that weaker assertion would
        // still pass if the suggestion regressed to the MIME type, the link, or any other non-empty
        // string that is not the name ReadRun.cs actually picks.
        Assert.Equal("xrpl-file.bin", _picker.SavedStreamName);
    }

    [Fact]
    public async Task A_save_dialog_that_throws_reports_a_failure_instead_of_escaping()
    {
        // SaveAsync's own error boundary (design 4.2's C2 fix): the picker throwing is what a full
        // disk or a revoked permission on the platform side surfaces as. Reachable proof it is
        // actually caught, not merely present in the source: FakeFilePicker had no way to throw
        // until this round -- SaveStreamResult can only report a person cancelling the dialog,
        // never the dialog itself failing.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        _picker.SaveStreamThrows = new IOException("disk full");

        await run.SaveAsync(TestContext.Current.CancellationToken);

        ReadSnapshot snapshot = run.Snapshot;
        Assert.False(snapshot.Saved);
        Assert.Equal(
            "The file could not be opened to save, or could not be written to where you chose.",
            TestText.En(snapshot.Failure));

        // The read itself is untouched by a save failure: still the succeeded read it was, still
        // able to be retried.
        Assert.Equal(ReadState.Succeeded, snapshot.State);

        // Task 6 fix round 1, Minor 2's other half: Saving clears on the failure outcome exactly
        // the way it clears on a success (see Saving_is_true_only_while_the_save_dialog_is_open
        // below for that side), not only while nothing goes wrong.
        Assert.False(snapshot.Saving);
    }

    [Fact]
    public async Task Saving_is_true_only_while_the_save_dialog_is_open()
    {
        // Task 6 fix round 1, Minor 2: Read.razor disables the Save button off this flag so a
        // double-click cannot open two file dialogs. Proven reachable, not merely present in the
        // source, using the same GatedFilePicker the generation test just below holds a save open
        // with, long enough to observe it mid-flight.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedFilePicker gate = new(_picker);
        ReadRun run = Track(new ReadRun(_connections, gate));

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        Assert.False(run.Snapshot.Saving);

        Task saving = run.SaveAsync(TestContext.Current.CancellationToken);
        await gate.Reached;

        Assert.True(run.Snapshot.Saving);

        gate.Release();
        await saving;

        Assert.False(run.Snapshot.Saving);
        Assert.True(run.Snapshot.Saved);
    }

    [Fact]
    public async Task A_reset_while_the_save_dialog_is_open_does_not_land_saved_on_a_run_that_no_longer_exists()
    {
        // The generation pin SaveAsync carries (design's Important 3 fix), made reachable rather
        // than merely present: a gated picker holds SaveAsync inside the platform call -- as if the
        // person were still looking at the dialog -- so a Reset() can land in the exact window
        // between the dialog opening and its returning that the pin exists to guard.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedFilePicker gate = new(_picker);
        ReadRun run = Track(new ReadRun(_connections, gate));

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        Task saving = run.SaveAsync(TestContext.Current.CancellationToken);
        await gate.Reached;

        // The dialog is open (the picker's own SaveStreamAsync is parked at the gate) when this
        // runs -- exactly what a screen reacting to a fresh navigation, or a second read starting,
        // would do while the person is still looking at the save dialog from this one.
        run.Reset();

        gate.Release();
        await saving;

        Assert.False(run.Snapshot.Saved);
    }

    [Fact]
    public async Task Resetting_a_run_that_is_still_going_changes_nothing()
    {
        // The decision History.razor's Read button leans on (design's Important 1 fix): pressing
        // Read beside a row calls Reset() unconditionally, without checking State itself first, so
        // Reset() has to be the one place that decides whether a genuinely running read may be
        // discarded. It may not -- a running read must not be silently thrown away just because
        // somebody pointed the Read tab at a different link -- and this is the test that proves
        // Reset() actually refuses, not merely that the source reads as though it does.
        //
        // Seeded synthetically rather than through ReadyAsync: this test is about Reset(), not
        // about publishing -- see SeedSyntheticFileAsync's own remarks.
        (XrplFileLink link, ManifestRecord manifest) =
            await SeedSyntheticFileAsync(_connections, TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Reset();

        Assert.Equal(ReadState.Running, run.Snapshot.State);
        Assert.True(File.Exists(temporary));

        gate.Release();
        await run.Completion;

        Assert.Equal(ReadState.Succeeded, run.Snapshot.State);
    }

    [Fact]
    public async Task Disposing_during_a_save_clears_saving_the_same_way_start_and_reset_do()
    {
        // _generation's own remarks describe Start, Reset and Dispose as one family of bumpers that
        // all baseline _saving to false for the generation they leave behind -- until this fix,
        // Dispose was the one of the three that did not, so a save superseded by a Dispose would
        // leave Saving stuck true on a snapshot nothing will ever clear again.
        //
        // Seeded synthetically rather than through ReadyAsync: this test is about Dispose(), not
        // about publishing -- see SeedSyntheticFileAsync's own remarks.
        (XrplFileLink link, ManifestRecord manifest) =
            await SeedSyntheticFileAsync(_connections, TestContext.Current.CancellationToken);

        GatedFilePicker gate = new(_picker);
        ReadRun run = Track(new ReadRun(_connections, gate));

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        Task saving = run.SaveAsync(TestContext.Current.CancellationToken);
        await gate.Reached;

        Assert.True(run.Snapshot.Saving);

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);

        run.Dispose();

        Assert.False(run.Snapshot.Saving);

        gate.Release();
        await saving;

        Assert.False(run.Snapshot.Saving);

        // Round 2, N2: Dispose() ran while SaveAsync still held the temporary file open, so
        // DeleteTemporary's own delete failed and left _temporary set for a retry (its own remarks
        // describe exactly this). Dispose() itself guards against a second call once _disposed is
        // true, so the class's own teardown loop -- which calls Dispose() again for every tracked
        // run -- never reaches that retry and used to leak one "xrpl-file-read-*" directory per run
        // of this test. Reset() carries no such guard, so it is the one caller left that can still
        // retry the delete once the save has actually released the file, which by this point (after
        // awaiting `saving` above) it has -- proven here rather than left to the class teardown.
        //
        // The stranded delete is a Windows behaviour: Windows refuses to delete a file another handle
        // still holds open. POSIX unlinks it at once, so on Linux Dispose's own delete already
        // succeeded and there is nothing left for Reset to retry. Either way the file must be gone.
        if (OperatingSystem.IsWindows()) Assert.Equal(temporary, run.TemporaryPathForTests);

        run.Reset();
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task A_read_put_away_by_reset_lets_the_next_read_start_fresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (ReadRun run, XrplFileLink firstLink, ManifestRecord firstManifest) = await ReadyAsync(token, "first.json", 597);
        (XrplFileLink secondLink, ManifestRecord secondManifest) = await LookUpAsync(token, "second.bin", 3000);

        Assert.True(run.Start(firstLink, firstManifest));
        await run.Completion;
        await run.SaveAsync(token);
        Assert.True(run.Snapshot.Saved);

        run.Reset();
        Assert.Equal(ReadState.Idle, run.Snapshot.State);

        Assert.True(run.Start(secondLink, secondManifest));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Succeeded, snapshot.State);
        Assert.Equal("second.bin", snapshot.Name);
        Assert.Equal(3000, snapshot.BytesDone);
        Assert.False(snapshot.Saved);
    }

    [Fact]
    public async Task A_finished_read_can_be_followed_by_another_without_a_reset()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (ReadRun run, XrplFileLink firstLink, ManifestRecord firstManifest) = await ReadyAsync(token, "first.json", 597);
        (XrplFileLink secondLink, ManifestRecord secondManifest) = await LookUpAsync(token, "second.bin", 3000);

        Assert.True(run.Start(firstLink, firstManifest));
        await run.Completion;

        Assert.True(run.Start(secondLink, secondManifest));
        await run.Completion;

        Assert.Equal("second.bin", run.Snapshot.Name);
        Assert.Equal(ReadState.Succeeded, run.Snapshot.State);
    }

    private async Task<(XrplFileLink Link, ManifestRecord Manifest)> LookUpAsync(CancellationToken token, string name, int size)
    {
        XrplFileLink link = await PublishAsync(_connections, name, "application/octet-stream", size);

        await using ILedgerConnection connection = await _connections.ConnectAsync(link.Network, token);
        ManifestRecord manifest = await new XrplFileReader(connection.Source).ManifestAsync(link, token);

        return (link, manifest);
    }

    [Fact]
    public async Task A_second_read_while_one_is_going_is_refused()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        Assert.False(run.Start(link, manifest));

        await run.Completion;
    }

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

    [Fact]
    public async Task A_node_whose_history_is_too_short_refuses_before_any_block_is_fetched()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        // The manifest names the ledger the file's first chunk was written in. A node that has
        // pruned past it cannot answer, and finding that out one block at a time wastes the
        // person's time.
        _connections.Ledger.EarliestLedger = manifest.Manifest.FirstLedger + 1;

        int callsBefore = _connections.Ledger.AccountTransactionsCalls;

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        ReadSnapshot snapshot = run.Snapshot;
        Assert.Equal(ReadState.Failed, snapshot.State);
        Assert.Equal(ReadErrorMessages.For(XrplFileError.NodeLacksHistory), snapshot.Failure);

        // "Before any block is fetched" is what the test's own name claims: proven here, not merely
        // implied by the code's ordering -- no block-list or block scan of the ledger ever happened,
        // and nothing was written to disk or counted as progress.
        Assert.Equal(callsBefore, _connections.Ledger.AccountTransactionsCalls);
        Assert.Null(run.TemporaryPathForTests);
        Assert.Equal(0, snapshot.BlocksDone);
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_run_is_reset()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Reset();

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Nothing_is_left_on_disk_after_a_run_is_disposed()
    {
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Dispose();

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Starting_a_disposed_run_throws()
    {
        // Without the disposed flag, Start() would happily begin a brand-new run on an object its
        // owner already considers gone -- the object Dispose() exists to put away for good.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        run.Dispose();

        Assert.Throws<ObjectDisposedException>(() => run.Start(link, manifest));
    }

    [Fact]
    public async Task Stopping_deletes_the_temporary_file_too()
    {
        // Not gated: over this fixture's 4096-byte, single-block file nothing the run touches
        // before finishing ever actually observes the cancellation token (FakeConnectionFactory and
        // FakeLedgerSource check none of their own calls against it), so the run always reaches
        // Succeeded before StopAsync's wait returns -- the same fact the brief's own note next to
        // this file's cancellation test calls out. What this proves instead: StopAsync's own Reset()
        // deletes whatever a run -- however it ended -- left on disk, exactly as a direct Reset()
        // call does in the test above. The genuinely in-flight cancel-and-wait path StopAsync exists
        // for is covered separately below, gated the same way the cancellation test is.
        //
        // The temporary path has to be read while the file is known to still exist, before
        // StopAsync's Reset() clears it back to null: reading it afterward and falling back to
        // Path.GetTempFileName() on null would create a brand-new, genuinely existing temporary file
        // and make the assertion below pass for the wrong reason.
        (ReadRun run, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        await run.StopAsync(TimeSpan.FromMinutes(1));

        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Stopping_a_read_that_is_genuinely_in_flight_cancels_it_and_cleans_up()
    {
        // The window-close path StopAsync exists for: unlike the test above, this one is held open
        // by the same gate the cancellation test uses, so StopAsync's own cancel-and-wait branch
        // (Task.WhenAny over the run's Completion and a timeout) is the one actually exercised here,
        // not merely the fast path over an already-finished run.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        // StopAsync's own first act, synchronously before its first await, is to call Cancel() --
        // so by the time this returns a Task<bool>, the token held at the gate has already been
        // cancelled, and releasing the gate right away is enough to let it proceed and see that.
        Task<bool> stopping = run.StopAsync(TimeSpan.FromSeconds(30));
        gate.Release();

        // Not asserted here: run.Snapshot.State == Cancelled. StopAsync's own success path calls
        // Reset() immediately once the run finishes, which publishes Idle over whatever RunAsync's
        // own terminal block just set -- there is no await point between the two a test could
        // observe from outside. What proves the run was genuinely cancelled rather than merely
        // having raced to a natural finish is the gate itself: it does not let RunAsync past the
        // fetch at all until Release() runs, and Release() here only runs after Cancel() already
        // has, so RunAsync's own cancellationToken.ThrowIfCancellationRequested() is certain to fire
        // -- the same reasoning the plain Cancel()-based test above relies on.
        Assert.True(await stopping);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task Stopping_returns_false_when_the_timeout_expires_with_the_read_still_going()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        // Wrapped in try/finally: the run is parked inside the gated fetch from this point on, until
        // gate.Release() runs. Without the finally, either assertion failing here would leave that
        // background task blocked forever rather than just failing the test.
        try
        {
            bool stopped = await run.StopAsync(TimeSpan.FromMilliseconds(50));

            Assert.False(stopped);
            Assert.Equal(ReadState.Running, run.Snapshot.State);
        }
        finally
        {
            gate.Release();
        }

        await run.Completion;
        Assert.Equal(ReadState.Cancelled, run.Snapshot.State);
    }

    [Fact]
    public async Task Cancelling_while_a_block_is_being_fetched_stops_the_run_and_cleans_up()
    {
        // Made deterministic the way PublicationRunTests does it (GatedSubmitSink): a decorator over
        // ILedgerSource holds the run's one AccountTransactionsAsync call -- the call
        // XrplFileReader.BlockAsync makes to fetch the single block this fixture's file has -- open
        // until the test releases it, so Cancel() is known to land on a run that is genuinely still
        // going, never on one that had already finished.
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);

        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        // The block list has already been fetched and verified, and the temporary file already
        // exists, by the time the gate is reached -- proving this cancels a download in progress,
        // not one that never started.
        string? temporary = run.TemporaryPathForTests;
        Assert.NotNull(temporary);
        Assert.True(File.Exists(temporary));

        run.Cancel();
        gate.Release();

        await run.Completion;

        Assert.Equal(ReadState.Cancelled, run.Snapshot.State);
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task A_read_holds_the_computer_awake_and_lets_go_when_it_succeeds()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);
        RecordingExecutionState state = new();
        ReadRun run = Track(new ReadRun(_connections, _picker, new ThreadKeepAwake(state)));

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        Assert.Equal(ReadState.Succeeded, run.Snapshot.State);
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
        Assert.Equal(state.Calls[0].Thread, state.Calls[1].Thread);
    }

    [Fact]
    public async Task A_keep_awake_that_throws_on_release_does_not_change_the_reads_outcome()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);
        ThrowingReleaseKeepAwake keepAwake = new();
        List<string> log = [];
        ReadRun run = Track(new ReadRun(_connections, _picker, keepAwake, line => { lock (log) log.Add(line); }));

        Assert.True(run.Start(link, manifest));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(ReadState.Succeeded, run.Snapshot.State);
        Assert.Equal(1, keepAwake.Acquired);
        Assert.Equal(1, keepAwake.Released);

        string line = Assert.Single(log);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains("the platform refused to let the computer sleep again", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_that_fails_lets_the_computer_sleep_again()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.EarliestLedger = manifest.Manifest.FirstLedger + 1;
        RecordingExecutionState state = new();
        ReadRun run = Track(new ReadRun(_connections, _picker, new ThreadKeepAwake(state)));

        Assert.True(run.Start(link, manifest));
        await run.Completion;

        Assert.Equal(ReadState.Failed, run.Snapshot.State);
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    [Fact]
    public async Task A_read_holds_the_computer_awake_while_it_runs_and_a_cancelled_one_lets_go()
    {
        (ReadRun _, XrplFileLink link, ManifestRecord manifest) = await ReadyAsync(TestContext.Current.CancellationToken);
        GatedAccountTransactionsSource gate = new(_connections.Ledger);
        RecordingExecutionState state = new();
        ReadRun run = Track(new ReadRun(new GatedReadConnectionFactory(_connections, gate), _picker, new ThreadKeepAwake(state)));

        Assert.True(run.Start(link, manifest));
        await gate.Reached;

        // Parked mid-download: held, not yet cleared.
        await BatchTestWait.UntilAsync(() => state.Calls.Count == 1);
        Assert.Equal(["require"], state.Calls.Select(call => call.Call));

        run.Cancel();
        gate.Release();
        await run.Completion;

        Assert.Equal(ReadState.Cancelled, run.Snapshot.State);
        Assert.Equal(["require", "clear"], state.Calls.Select(call => call.Call));
    }

    /// <summary>
    /// Holds the <paramref name="gateAt"/>-th <see cref="AccountTransactionsAsync"/> call open until
    /// <see cref="Release"/> is called, mirroring <c>GatedSubmitSink</c>: deliberately not looking at
    /// the caller's <see cref="CancellationToken"/> until then, so a test decides exactly when the
    /// gate lets go rather than racing real time.
    /// </summary>
    private sealed class GatedAccountTransactionsSource(ILedgerSource inner, int gateAt = 1) : ILedgerSource
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        /// <summary>Resolves the instant the gated call begins, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the gated call proceed.</summary>
        public void Release() => _release.TrySetResult();

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
            inner.TransactionAsync(hash, cancellationToken);

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == gateAt)
            {
                _reached.TrySetResult();

                // Not awaited against cancellationToken: the test decides exactly when this
                // proceeds by calling Release().
                await _release.Task.ConfigureAwait(false);

                // Once released, honour cancellation exactly where a real scan's own checks would --
                // this is what lets a test call Cancel() while the run is held here and see it
                // actually take effect once Release() lets the run notice.
                cancellationToken.ThrowIfCancellationRequested();
            }

            await foreach (LedgerTransaction tx in inner
                .AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return tx;
            }
        }

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) =>
            inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
            inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) =>
            inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
            inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) =>
            inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
            inner.DestinationAsync(account, cancellationToken);
    }

    /// <summary>
    /// A connection factory that hands out <see cref="FakeConnectionFactory"/>'s own ledger and sink,
    /// wrapped so its source's account-transaction scans go through a
    /// <see cref="GatedAccountTransactionsSource"/>.
    /// </summary>
    private sealed class GatedReadConnectionFactory(FakeConnectionFactory inner, GatedAccountTransactionsSource source)
        : IConnectionFactory
    {
        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            ILedgerConnection connection = await inner.ConnectAsync(network, cancellationToken).ConfigureAwait(false);
            return new Connection(connection, source);
        }

        private sealed class Connection(ILedgerConnection inner, ILedgerSource source) : ILedgerConnection
        {
            public ILedgerSource Source => source;

            public ILedgerSink Sink => inner.Sink;

            public string Url => inner.Url;

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>
    /// Holds <see cref="SaveStreamAsync"/> open until <see cref="Release"/> is called, standing in
    /// for a modal save dialog the person has not yet dismissed -- the same <c>Reached</c>/<c>Release</c>
    /// shape as <see cref="GatedAccountTransactionsSource"/>, applied to the picker seam instead of
    /// the ledger one.
    /// </summary>
    private sealed class GatedFilePicker(IFilePickerService inner) : IFilePickerService
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolves the instant the gated call begins, before it waits to be released.</summary>
        public Task Reached => _reached.Task;

        /// <summary>Lets the gated call proceed.</summary>
        public void Release() => _release.TrySetResult();

        public Task<bool> SaveFileAsync(string suggestedFileName, byte[] content, CancellationToken cancellationToken) =>
            inner.SaveFileAsync(suggestedFileName, content, cancellationToken);

        public async Task<bool> SaveStreamAsync(string suggestedFileName, Stream content, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();

            // Not awaited against cancellationToken: the test decides exactly when this proceeds by
            // calling Release(), the same contract every other gate in this file follows.
            await _release.Task.ConfigureAwait(false);

            return await inner.SaveStreamAsync(suggestedFileName, content, cancellationToken).ConfigureAwait(false);
        }

        public Task<byte[]?> PickFileAsync(CancellationToken cancellationToken) =>
            inner.PickFileAsync(cancellationToken);

        public Task<string?> PickExistingFilePathAsync(CancellationToken cancellationToken) =>
            inner.PickExistingFilePathAsync(cancellationToken);
    }
}
