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
    /// five inputs is pinned here so a future edit that adds a sixth <see cref="ReadState"/> value
    /// cannot silently leave it out of either side of the <see langword="is"/> pattern.
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
}
