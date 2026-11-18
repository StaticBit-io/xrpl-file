using XrplFile.App.Core.Tutorial;
using Xunit;

namespace XrplFile.App.Core.Tests.Tutorial;

public sealed class TutorialPlacementTests
{
    private const double BubbleWidth = 320;
    private const double BubbleHeight = 200;

    [Fact]
    public void Hole_is_the_padded_union_clamped_to_the_view()
    {
        TourRect? hole = TutorialPlacement.Hole([new TourRect(100, 100, 50, 20), new TourRect(300, 140, 40, 40)], 1100, 713);

        Assert.Equal(new TourRect(94, 94, 252, 92), hole);

        TourRect? corner = TutorialPlacement.Hole([new TourRect(2, 2, 10, 10)], 1100, 713);

        Assert.NotNull(corner);
        Assert.Equal(0, corner.Value.X);
        Assert.Equal(0, corner.Value.Y);
    }

    [Fact]
    public void Hole_ignores_empty_rects_and_is_null_without_any()
    {
        Assert.Null(TutorialPlacement.Hole([], 1100, 713));
        Assert.Null(TutorialPlacement.Hole([new TourRect(10, 10, 0, 20), new TourRect(10, 10, 20, 0)], 1100, 713));

        TourRect? hole = TutorialPlacement.Hole([new TourRect(500, 500, 0, 0), new TourRect(100, 100, 50, 20)], 1100, 713);

        Assert.Equal(new TourRect(94, 94, 62, 32), hole);
    }

    [Fact]
    public void Hole_ignores_rects_wholly_outside_the_view()
    {
        Assert.Null(TutorialPlacement.Hole([new TourRect(1200, 100, 50, 20), new TourRect(-80, 10, 50, 20), new TourRect(10, 800, 50, 20)], 1100, 713));

        Assert.Equal(new TourRect(94, 94, 62, 32), TutorialPlacement.Hole([new TourRect(1200, 100, 50, 20), new TourRect(100, 100, 50, 20)], 1100, 713));
    }

    [Fact]
    public void A_bubble_larger_than_the_view_is_pinned_to_the_margin()
    {
        Assert.Equal(new TourPoint(TutorialPlacement.Margin, TutorialPlacement.Margin), TutorialPlacement.Place(null, 500, 400, 300, 200));
        Assert.Equal(new TourPoint(TutorialPlacement.Margin, TutorialPlacement.Margin), TutorialPlacement.Place(new TourRect(100, 50, 50, 50), 500, 400, 300, 200));
    }

    [Fact]
    public void A_zero_size_view_gives_a_finite_result()
    {
        Assert.Null(TutorialPlacement.Hole([new TourRect(0, 0, 10, 10)], 0, 0));

        TourPoint at = TutorialPlacement.Place(new TourRect(0, 0, 0, 0), 320, 160, 0, 0);

        Assert.True(double.IsFinite(at.Left) && double.IsFinite(at.Top));
        Assert.True(double.IsFinite(TutorialPlacement.Place(null, 320, 160, 0, 0).Left));
    }

    [Fact]
    public void No_hole_centres_the_bubble()
    {
        Assert.Equal(new TourPoint(390, 276.5), TutorialPlacement.Place(null, 320, 160, 1100, 713));
    }

    [Fact]
    public void Prefers_below_aligned_left()
    {
        Assert.Equal(new TourPoint(100, 152), TutorialPlacement.Place(new TourRect(100, 100, 200, 40), 320, 160, 1100, 713));
    }

    [Fact]
    public void Goes_above_when_below_has_no_room()
    {
        Assert.Equal(new TourPoint(100, 428), TutorialPlacement.Place(new TourRect(100, 600, 200, 40), 320, 160, 1100, 713));
    }

    [Fact]
    public void Goes_right_then_left_when_neither_below_nor_above_fits()
    {
        Assert.Equal(352, TutorialPlacement.Place(new TourRect(40, 8, 300, 697), 320, 160, 1100, 713).Left);
        Assert.Equal(428, TutorialPlacement.Place(new TourRect(760, 8, 300, 697), 320, 160, 1100, 713).Left);
    }

    public static TheoryData<double, double, double, double, double, double> AnyHole() => new()
    {
        { 1100, 713, 0, 0, 60, 40 },
        { 1100, 713, 1040, 0, 60, 40 },
        { 1100, 713, 0, 673, 60, 40 },
        { 1100, 713, 1040, 673, 60, 40 },
        { 1100, 713, 500, 340, 100, 40 },
        { 1100, 713, 0, 300, 1100, 60 },
        { 1100, 713, 500, 0, 80, 713 },
        { 1100, 713, 0, 0, 1100, 713 },
        { 375, 713, 0, 0, 60, 40 },
        { 375, 713, 315, 673, 60, 40 },
        { 375, 713, 100, 340, 100, 40 },
        { 375, 713, 0, 300, 375, 60 },
        { 375, 713, 150, 0, 60, 713 },
        { 375, 713, 0, 0, 375, 713 },
    };

    [Theory]
    [MemberData(nameof(AnyHole))]
    public void Never_leaves_the_viewport(double viewWidth, double viewHeight, double x, double y, double width, double height)
    {
        TourPoint at = TutorialPlacement.Place(new TourRect(x, y, width, height), BubbleWidth, BubbleHeight, viewWidth, viewHeight);

        Assert.True(at.Left >= TutorialPlacement.Margin, $"Left {at.Left}");
        Assert.True(at.Top >= TutorialPlacement.Margin, $"Top {at.Top}");
        Assert.True(at.Left + BubbleWidth <= viewWidth - TutorialPlacement.Margin, $"Right {at.Left + BubbleWidth}");
        Assert.True(at.Top + BubbleHeight <= viewHeight - TutorialPlacement.Margin, $"Bottom {at.Top + BubbleHeight}");
    }

    public static TheoryData<double, double, double, double, double, double> HoleWithRoomOnASide() => new()
    {
        { 1100, 713, 0, 0, 60, 40 },
        { 1100, 713, 1040, 0, 60, 40 },
        { 1100, 713, 0, 673, 60, 40 },
        { 1100, 713, 1040, 673, 60, 40 },
        { 1100, 713, 500, 340, 100, 40 },
        { 1100, 713, 0, 300, 1100, 60 },
        { 1100, 713, 500, 0, 80, 713 },
        { 375, 713, 0, 0, 60, 40 },
        { 375, 713, 315, 673, 60, 40 },
        { 375, 713, 100, 340, 100, 40 },
        { 375, 713, 0, 300, 375, 60 },
    };

    [Theory]
    [MemberData(nameof(HoleWithRoomOnASide))]
    public void Does_not_cover_the_hole_when_a_side_has_room(double viewWidth, double viewHeight, double x, double y, double width, double height)
    {
        TourPoint at = TutorialPlacement.Place(new TourRect(x, y, width, height), BubbleWidth, BubbleHeight, viewWidth, viewHeight);

        bool overlapsX = at.Left < x + width && at.Left + BubbleWidth > x;
        bool overlapsY = at.Top < y + height && at.Top + BubbleHeight > y;

        Assert.False(overlapsX && overlapsY, $"Bubble at {at} covers the hole");
    }
}
