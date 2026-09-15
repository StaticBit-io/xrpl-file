using XrplFile.Keys;

using Xunit;

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

    [Fact]
    public void An_idle_run_cannot_be_cancelled()
    {
        RunModel model = new(NewRun());

        Assert.False(model.CanCancel);
    }

    [Fact]
    public void An_idle_run_has_no_link_failure_or_history_note()
    {
        RunModel model = new(NewRun());

        Assert.Null(model.Link);
        Assert.Null(model.Failure);
        Assert.Null(model.HistoryNote);
    }

    [Fact]
    public void The_stage_text_says_starting_before_any_progress_is_reported()
    {
        RunModel model = new(NewRun());

        Assert.Equal("Starting", model.StageText);
    }

    private static PublicationRun NewRun()
    {
        string directory = Directory.CreateTempSubdirectory("xrpl-file-runmodel-").FullName;
        RunPaths paths = new(directory);
        return new PublicationRun(
            new FakeConnectionFactory(), new NeverVault(), paths, new PublicationHistory(paths.HistoryPath));
    }

    /// <summary>
    /// A minimal <see cref="IKeyVault"/> whose every member throws <see cref="NotSupportedException"/>.
    /// Safe here because these tests never start a run: nothing in <see cref="RunModel"/> touches the
    /// vault, only <see cref="PublicationRun.Snapshot"/>, and none of these tests call
    /// <see cref="PublicationRun.Start"/> or <see cref="PublicationRun.Resume"/>.
    /// </summary>
    private sealed class NeverVault : IKeyVault
    {
        public bool IsUnlocked => throw new NotSupportedException();

        public IReadOnlyCollection<string> References => throw new NotSupportedException();

        public IReadOnlyList<string> DamagedReferences => throw new NotSupportedException();

        public bool WritesAreRefused => throw new NotSupportedException();

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task LockAsync() => throw new NotSupportedException();

        public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> ExportAsync(
            string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
