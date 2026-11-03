using Xrpl.Wallet;
using XrplFile.App.Core.Localization;
using XrplFile.Keys;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class UnfinishedModelBatchTests : VaultHarness
{
    private readonly string _directory = Directory.CreateTempSubdirectory("xrpl-file-unfinished-batch-").FullName;
    private readonly FakeFilePicker _picker = new();

    /// <summary>The run a test may have resumed: awaited before the vault and directory go away.</summary>
    private PublicationRun? _run;

    private RunPaths Paths => new(_directory);

    public override async ValueTask DisposeAsync()
    {
        if (_run is not null) await _run.Completion;

        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>What a run reports when its request carried no payer, which the background task would otherwise swallow.</summary>
    private const string MissingPayer = "Something went wrong that this application does not have a specific message for. The run was stopped.";

    /// <summary>A vault holding a writer and a payer, and a Batch run's state file naming them, for a file on disk.</summary>
    private async Task<(UnfinishedModel Model, KeyVault Vault, string StatePath, string FilePath, string PayerReference)> BatchRunAsync(byte[] bytes)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), Token);
        XrplWallet writer = XrplWallet.Generate();
        XrplWallet payer = XrplWallet.Generate();
        string writerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "writer", "testnet", writer.ClassicAddress, writer.Seed), Token);
        string payerRef = await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "Payer key", "testnet", payer.ClassicAddress, payer.Seed), Token);

        string filePath = Path.Combine(_directory, "source.bin");
        await File.WriteAllBytesAsync(filePath, bytes, Token);

        Directory.CreateDirectory(Paths.StateDirectory);
        string statePath = Path.Combine(Paths.StateDirectory, "1-batch.json");
        new PublicationState(
            PublicationState.BatchVersion, "testnet", writer.ClassicAddress, writerRef, "application/octet-stream", "source.bin",
            bytes.Length, MemoLayout.ChunkBytes, PublicationSession.RootOf(bytes), 1, "chunks",
            Strategy: PublicationState.BatchStrategyName, SourcePath: filePath, Payer: payer.ClassicAddress, PayerKeyReference: payerRef, Fence: 0)
            .Save(statePath);

        PublicationRun run = new(new FakeConnectionFactory(), vault, Paths, new PublicationHistory(Paths.HistoryPath));
        _run = run;
        UnfinishedModel model = new(Paths, run, _picker, vault);
        await model.RefreshAsync(Token);

        return (model, vault, statePath, filePath, payerRef);
    }

    [Fact]
    public async Task A_batch_run_is_listed_with_its_method_and_payer_label()
    {
        (UnfinishedModel model, _, _, _, _) = await BatchRunAsync(new byte[3000]);

        Assert.Equal("Batch, paid by Payer key", TestText.En(Assert.Single(model.Rows).Method));
    }

    [Theory]
    [InlineData("tickets", 3000L, "Tickets")]
    [InlineData("sequence", 3000L, "Sequence")]
    [InlineData(null, 3000L, "Sequence")]
    [InlineData(null, 500_000L, "Tickets")]
    public void A_one_account_run_is_listed_by_its_recorded_or_implied_method(string? strategy, long size, string expected)
    {
        UnfinishedRow row = new("p", "testnet", "rW", "k", "t", "n", size, RunText.StageChunks, 3, null) { Strategy = strategy };

        Assert.Equal(expected, TestText.En(UnfinishedModel.MethodOf(row, new Dictionary<string, string>())));
    }

    [Fact]
    public void Batch_runs_and_recorded_sequence_runs_never_show_the_ticket_note()
    {
        UnfinishedRow big = new("p", "testnet", "rW", "k", "t", "n", 3_145_728, RunText.StageChunks, 3146, null);

        Assert.False(UnfinishedModel.ShowsTicketNoteFor([big with { IsBatch = true }]));
        Assert.False(UnfinishedModel.ShowsTicketNoteFor([big with { Strategy = PublicationState.SequenceStrategyName }]));
        Assert.True(UnfinishedModel.ShowsTicketNoteFor([big]));
    }

    [Fact]
    public async Task Resume_reads_the_recorded_file_and_never_opens_the_picker()
    {
        (UnfinishedModel model, _, _, _, _) = await BatchRunAsync(new byte[3000]);
        _picker.PickThrows = new InvalidOperationException("the picker must not open");

        Assert.True(await model.ResumeAsync(model.Rows[0], Token));
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task Resume_from_the_failed_run_screen_takes_the_same_path_without_a_picker()
    {
        (UnfinishedModel model, _, string statePath, _, _) = await BatchRunAsync(new byte[3000]);
        _picker.PickThrows = new InvalidOperationException("the picker must not open");
        List<string> signers = [];
        _run!.BatchSignerCreated = signer => signers.Add(signer.Account);
        PublicationState recorded = PublicationState.Load(statePath);

        Assert.True(await model.ResumeByPathAsync(statePath, Token));
        Assert.Null(model.Message);

        await _run.Completion;

        // The run built its signers from the request: the writer's, then the payer's the state names.
        Assert.Equal([recorded.Writer, recorded.Payer!], signers);
        Assert.NotEqual(MissingPayer, TestText.En(_run.Snapshot.Failure));
    }

    [Fact]
    public async Task The_failed_run_screens_resume_keeps_the_chooser_for_a_payer_that_left_the_vault()
    {
        (UnfinishedModel model, KeyVault vault, string statePath, _, string payerRef) = await BatchRunAsync(new byte[3000]);
        XrplWallet replacement = XrplWallet.Generate();
        await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "new payer", "testnet", replacement.ClassicAddress, replacement.Seed), Token);
        await vault.DeleteAsync(payerRef, Token);

        Assert.False(await model.ResumeByPathAsync(statePath, Token));
        Assert.NotNull(model.NeedsPayer);
        string? reason = TestText.En(model.Message);
        Assert.NotNull(reason);

        // The Unfinished list refreshes when the person reaches it: the chooser is still there.
        await model.RefreshAsync(Token);

        Assert.Equal(statePath, model.NeedsPayer?.Path);
        Assert.Equal(["new payer"], model.PayerChoices.Select(key => key.Label).ToArray());
        Assert.Equal(reason, TestText.En(model.Message));

        // Once its state file is gone there is nothing to choose a payer for.
        model.Delete(model.NeedsPayer!);

        Assert.Null(model.NeedsPayer);
        Assert.Empty(model.PayerChoices);
    }

    [Fact]
    public async Task A_locked_vault_is_not_reported_as_the_payer_key_having_left_it()
    {
        // The payer key is still in the vault; only the vault cannot be read right now. Saying the
        // key is gone and offering a replacement would send the person after a problem they do not have.
        (UnfinishedModel model, KeyVault vault, _, _, _) = await BatchRunAsync(new byte[3000]);
        await vault.LockAsync();

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));

        Assert.Null(model.NeedsPayer);
        Assert.Empty(model.PayerChoices);
        Assert.Equal(TestText.En(VaultErrorMessages.For(VaultError.Locked)), TestText.En(model.Message));
    }

    [Fact]
    public async Task Deleting_one_batch_run_keeps_the_payer_label_on_the_others()
    {
        (UnfinishedModel model, _, string statePath, _, _) = await BatchRunAsync(new byte[3000]);
        File.Copy(statePath, Path.Combine(Paths.StateDirectory, "2-batch.json"));
        await model.RefreshAsync(Token);
        Assert.Equal(2, model.Rows.Count);

        model.Delete(model.Rows[0]);

        Assert.Equal("Batch, paid by Payer key", TestText.En(Assert.Single(model.Rows).Method));
    }

    [Theory]
    [InlineData("moved")]
    [InlineData("resized")]
    public async Task A_moved_or_resized_file_falls_back_to_the_picker_with_the_reason(string what)
    {
        byte[] bytes = new byte[3000];
        (UnfinishedModel model, _, _, string filePath, _) = await BatchRunAsync(bytes);

        if (what == "moved") File.Delete(filePath);
        else await File.WriteAllBytesAsync(filePath, new byte[3001], Token);

        _picker.PathToReturn = null;

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));
        Assert.Equal(
            what == "moved"
                ? $"The file this run was publishing is no longer at {filePath}. Choose it again."
                : $"The file at {filePath} has changed since the run started. Choose the original file.",
            TestText.En(model.Message));
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_falls_back_to_the_picker_without_blaming_its_bytes()
    {
        (UnfinishedModel model, _, string statePath, string filePath, _) = await BatchRunAsync(new byte[3000]);
        await using FileStream exclusive = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
        _picker.PathToReturn = null;

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));

        Assert.Equal($"The file at {filePath} could not be read. Choose it again.", TestText.En(model.Message));
        Assert.False(_run!.HasRejectedSource(statePath));
    }

    [Fact]
    public async Task A_file_whose_bytes_changed_is_refused_by_the_run_and_the_next_resume_opens_the_picker()
    {
        (UnfinishedModel model, _, string statePath, string filePath, _) = await BatchRunAsync(new byte[3000]);
        await File.WriteAllBytesAsync(filePath, Enumerable.Repeat((byte)1, 3000).ToArray(), Token);
        _picker.PickThrows = new InvalidOperationException("the first resume trusts the recorded path");

        Assert.True(await model.ResumeAsync(model.Rows[0], Token));
        await _run!.Completion;

        Assert.Equal(PublicationRunState.Failed, _run.Snapshot.State);
        Assert.Equal($"The file at {filePath} has changed since the run started. Choose the original file.", TestText.En(_run.Snapshot.Failure));
        Assert.True(_run.HasRejectedSource(statePath));

        _picker.PickThrows = new InvalidOperationException("the picker was asked");
        _run.Reset();
        await model.RefreshAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ResumeAsync(model.Rows[0], Token));
        Assert.Equal($"The file at {filePath} has changed since the run started. Choose the original file.", TestText.En(model.Message));
    }

    [Fact]
    public async Task A_picked_file_that_is_not_the_runs_is_refused_by_its_root()
    {
        (UnfinishedModel model, _, string statePath, string filePath, _) = await BatchRunAsync(new byte[3000]);
        File.Delete(filePath);
        string other = Path.Combine(_directory, "other.bin");
        await File.WriteAllBytesAsync(other, Enumerable.Repeat((byte)7, 3000).ToArray(), Token);
        _picker.PathToReturn = other;

        Assert.True(await model.ResumeAsync(model.Rows[0], Token));
        await _run!.Completion;

        Assert.Equal(PublicationRunState.Failed, _run.Snapshot.State);
        Assert.Equal("That file is not the one this run was publishing.", TestText.En(_run.Snapshot.Failure));
        Assert.True(_run.HasRejectedSource(statePath));
    }

    [Fact]
    public async Task A_picked_file_of_another_size_is_refused_on_the_screen_before_any_run_starts()
    {
        (UnfinishedModel model, _, _, string filePath, _) = await BatchRunAsync(new byte[3000]);
        File.Delete(filePath);
        string other = Path.Combine(_directory, "other.bin");
        await File.WriteAllBytesAsync(other, new byte[2000], Token);
        _picker.PathToReturn = other;

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));

        Assert.Equal(
            "That file is 2,000 bytes and this run was publishing 3,000. Resuming needs the same file the run started with.",
            TestText.En(model.Message));
        Assert.Equal(PublicationRunState.Idle, _run!.Snapshot.State);
    }

    [Fact]
    public async Task With_the_payer_key_gone_the_person_chooses_another_payer_on_the_runs_network()
    {
        (UnfinishedModel model, KeyVault vault, string statePath, _, string payerRef) = await BatchRunAsync(new byte[3000]);
        XrplWallet replacement = XrplWallet.Generate();
        string replacementRef = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "new payer", "TESTNET", replacement.ClassicAddress, replacement.Seed), Token);
        await vault.StoreAsync(new NewVaultEntry(VaultEntryKind.Generated, "devnet key", "devnet", "rDevnetOnly", XrplWallet.Generate().Seed), Token);
        await vault.DeleteAsync(payerRef, Token);

        Assert.False(await model.ResumeAsync(model.Rows[0], Token));
        Assert.NotNull(model.NeedsPayer);
        Assert.Equal(["new payer"], model.PayerChoices.Select(key => key.Label).ToArray());

        List<string> signers = [];
        _run!.BatchSignerCreated = signer => signers.Add(signer.Account);
        PublicationState recorded = PublicationState.Load(statePath);

        Assert.True(await model.ResumeWithPayerAsync(model.NeedsPayer!, replacementRef, Token));
        Assert.Null(model.NeedsPayer);

        await _run.Completion;

        Assert.Equal([recorded.Writer, replacement.ClassicAddress], signers);
        Assert.NotEqual(MissingPayer, TestText.En(_run.Snapshot.Failure));
    }
}
