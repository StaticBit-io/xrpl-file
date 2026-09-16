using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class ReadSnapshotTests
{
    private static ReadSnapshot With(ReadState state) =>
        new(state, BlocksDone: 0, BlocksTotal: 0, BytesDone: 0, BytesTotal: 0, Name: null, Failure: null, Saved: false, Saving: false);

    /// <summary>
    /// <see cref="ReadSnapshot.IsFinished"/> is what Read.razor asks instead of comparing
    /// <see cref="ReadSnapshot.State"/> against three named values in markup itself -- the review
    /// round that added it (task 6, fix round 1) is explicit that this is the same decision
    /// <c>RunModel.IsFinished</c> makes for <c>PublicationRun</c> (design C2), and every one of its
    /// five inputs is pinned here. Pinning them is not what stops a sixth
    /// <see cref="ReadState"/> being forgotten — a hand-written list cannot notice a value nobody
    /// added it to. <see cref="Every_state_has_been_told_whether_it_has_ended"/> is what does that.
    /// </summary>
    [Theory]
    [InlineData(ReadState.Idle, false)]
    [InlineData(ReadState.Running, false)]
    [InlineData(ReadState.Succeeded, true)]
    [InlineData(ReadState.Failed, true)]
    [InlineData(ReadState.Cancelled, true)]
    public void IsFinished_names_exactly_the_three_terminal_states(ReadState state, bool expected)
    {
        Assert.Equal(expected, With(state).IsFinished);
    }

    /// <summary>
    /// Walks <see cref="ReadState"/> itself rather than a list somebody wrote down, so a member
    /// added later fails here instead of reaching a screen classified as "still running".
    /// </summary>
    [Fact]
    public void Every_state_has_been_told_whether_it_has_ended()
    {
        foreach (ReadState state in Enum.GetValues<ReadState>())
        {
            Exception? thrown = Record.Exception(() => With(state).IsFinished);

            Assert.Null(thrown);
        }
    }
}
