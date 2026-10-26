using Xunit;

namespace XrplFile.Tests;

public class BatchRegistryTests
{
    private static BatchCandidate Candidate(int ordinal, uint lastLedger, params int[] memos) =>
        new(ordinal, isOuter: true, "rPayer", (uint)(100 + ordinal), lastLedger, 165, memos, new SignedTransaction("00", $"HASH{ordinal}"));

    [Fact]
    public async Task A_candidate_is_registered_only_after_its_deadline_is_persisted()
    {
        RecordingFence recorder = new();
        BatchRegistry registry = new(new BatchFence(recorder), new byte[6], new BatchTrace());

        Assert.True(await registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken));
        Assert.True(await registry.TryRegisterAsync(Candidate(1, 520, 2, 3), TestContext.Current.CancellationToken));
        Assert.True(await registry.TryRegisterAsync(Candidate(2, 521, 4, 5), TestContext.Current.CancellationToken));

        // Persisted when it rises, not once per candidate.
        Assert.Equal([520L, 521L], recorder.Recorded);
        Assert.Equal(3, registry.Candidates.Count);
    }

    [Fact]
    public async Task Registering_reserves_a_candidate_for_every_memo_it_carries()
    {
        byte[] candidates = new byte[2];
        candidates[1] = 2;
        BatchRegistry registry = new(new BatchFence(new RecordingFence()), candidates, new BatchTrace());

        await registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, candidates[0]);
        Assert.Equal(3, candidates[1]);
    }

    [Fact]
    public async Task After_a_halt_nothing_registers_and_nothing_is_reserved()
    {
        byte[] candidates = new byte[2];
        BatchTrace trace = new();
        BatchRegistry registry = new(new BatchFence(new RecordingFence()), candidates, trace);

        await registry.HaltAsync("diverged", TestContext.Current.CancellationToken);

        Assert.False(await registry.TryRegisterAsync(Candidate(4, 520, 0, 1), TestContext.Current.CancellationToken));
        Assert.All(candidates, count => Assert.Equal(0, count));
        Assert.Empty(registry.Candidates);
        Assert.Equal(["halt:diverged", "discard:4"], trace.Events);
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_registers_nothing()
    {
        byte[] candidates = new byte[2];
        BatchRegistry registry = new(new BatchFence(new RecordingFence { Fail = true }), candidates, new BatchTrace());

        await Assert.ThrowsAsync<IOException>(() => registry.TryRegisterAsync(Candidate(0, 520, 0, 1), TestContext.Current.CancellationToken));

        Assert.All(candidates, count => Assert.Equal(0, count));
        Assert.Empty(registry.Candidates);
    }

    [Fact]
    public void A_candidate_is_resolved_by_a_ledger_past_its_deadline_or_by_its_sequence_being_used()
    {
        BatchCandidate candidate = Candidate(0, 520, 0, 1);

        Assert.False(candidate.IsResolvedBy(new LedgerSnapshot(520, new Dictionary<string, uint> { ["rPayer"] = 100 })));
        Assert.True(candidate.IsResolvedBy(new LedgerSnapshot(521, new Dictionary<string, uint> { ["rPayer"] = 100 })));
        Assert.True(candidate.IsResolvedBy(new LedgerSnapshot(510, new Dictionary<string, uint> { ["rPayer"] = 101 })));
    }
}
