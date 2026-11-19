namespace XrplFile.App.Core.Tutorial;

/// <summary>A rectangle in CSS pixels, origin at the top left of the view.</summary>
public readonly record struct TourRect(double X, double Y, double Width, double Height);

/// <summary>A position in CSS pixels of the top left corner of a box.</summary>
public readonly record struct TourPoint(double Left, double Top);

/// <summary>
/// Where the tutorial spotlights and puts its bubble. Pure geometry in CSS pixels, so the rules
/// (never off screen, never over what it points at when there is room) are testable without a UI.
/// </summary>
public static class TutorialPlacement
{
    /// <summary>Space between the lit area and the bubble.</summary>
    public const double Gap = 12;

    /// <summary>Smallest distance the bubble keeps from every edge of the view.</summary>
    public const double Margin = 8;

    /// <summary>How far the spotlight extends past the lit elements.</summary>
    public const double HolePadding = 6;

    /// <summary>
    /// The spotlight over the union of the lit rectangles, grown by <see cref="HolePadding"/> and
    /// clamped to the view; null when no rectangle has any area inside the view.
    /// </summary>
    public static TourRect? Hole(IReadOnlyList<TourRect> lit, double viewWidth, double viewHeight)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        bool any = false;

        foreach (TourRect rect in lit)
        {
            // Clip first: an element scrolled or laid out wholly off screen must not pull the hole there.
            double clippedLeft = Math.Max(0, rect.X);
            double clippedTop = Math.Max(0, rect.Y);
            double clippedRight = Math.Min(viewWidth, rect.X + rect.Width);
            double clippedBottom = Math.Min(viewHeight, rect.Y + rect.Height);
            if (clippedRight <= clippedLeft || clippedBottom <= clippedTop) continue;

            any = true;
            left = Math.Min(left, clippedLeft);
            top = Math.Min(top, clippedTop);
            right = Math.Max(right, clippedRight);
            bottom = Math.Max(bottom, clippedBottom);
        }

        if (!any) return null;

        left = Math.Max(0, left - HolePadding);
        top = Math.Max(0, top - HolePadding);
        right = Math.Min(viewWidth, right + HolePadding);
        bottom = Math.Min(viewHeight, bottom + HolePadding);

        return new TourRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// The top left corner for a bubble of the given size. Tries below, above, right, then left of
    /// the hole and takes the first side with room; when none has, takes the clamped position that
    /// covers the least of the hole (ties keep that order) and accepts the overlap. The result always lies inside the view by <see cref="Margin"/>, or at
    /// <see cref="Margin"/> when the bubble is larger than the view.
    /// </summary>
    public static TourPoint Place(TourRect? hole, double bubbleWidth, double bubbleHeight, double viewWidth, double viewHeight)
    {
        if (hole is not TourRect h)
        {
            return new TourPoint(
                Clamp((viewWidth - bubbleWidth) / 2, bubbleWidth, viewWidth),
                Clamp((viewHeight - bubbleHeight) / 2, bubbleHeight, viewHeight));
        }

        double below = viewHeight - Margin - (h.Y + h.Height + Gap);
        double above = h.Y - Gap - Margin;
        double right = viewWidth - Margin - (h.X + h.Width + Gap);
        double left = h.X - Gap - Margin;

        if (below >= bubbleHeight) return BelowAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight);
        if (above >= bubbleHeight) return AboveAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight);
        if (right >= bubbleWidth) return RightAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight);
        if (left >= bubbleWidth) return LeftAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight);

        // Room is a poor proxy once the clamp pushes the bubble back over the hole: the roomiest side
        // can cover more than a tighter one, so compare the clamped rectangles by overlap area.
        TourPoint best = BelowAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight);
        double bestOverlap = OverlapArea(h, best, bubbleWidth, bubbleHeight);

        foreach (TourPoint candidate in new[]
        {
            AboveAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight),
            RightAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight),
            LeftAt(h, bubbleWidth, bubbleHeight, viewWidth, viewHeight),
        })
        {
            double overlap = OverlapArea(h, candidate, bubbleWidth, bubbleHeight);
            if (overlap < bestOverlap)
            {
                best = candidate;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static TourPoint BelowAt(TourRect h, double bubbleWidth, double bubbleHeight, double viewWidth, double viewHeight) =>
        new(Clamp(h.X, bubbleWidth, viewWidth), Clamp(h.Y + h.Height + Gap, bubbleHeight, viewHeight));

    private static TourPoint AboveAt(TourRect h, double bubbleWidth, double bubbleHeight, double viewWidth, double viewHeight) =>
        new(Clamp(h.X, bubbleWidth, viewWidth), Clamp(h.Y - Gap - bubbleHeight, bubbleHeight, viewHeight));

    private static TourPoint RightAt(TourRect h, double bubbleWidth, double bubbleHeight, double viewWidth, double viewHeight) =>
        new(Clamp(h.X + h.Width + Gap, bubbleWidth, viewWidth), Clamp(h.Y, bubbleHeight, viewHeight));

    private static TourPoint LeftAt(TourRect h, double bubbleWidth, double bubbleHeight, double viewWidth, double viewHeight) =>
        new(Clamp(h.X - Gap - bubbleWidth, bubbleWidth, viewWidth), Clamp(h.Y, bubbleHeight, viewHeight));

    private static double OverlapArea(TourRect h, TourPoint at, double bubbleWidth, double bubbleHeight)
    {
        double width = Math.Min(h.X + h.Width, at.Left + bubbleWidth) - Math.Max(h.X, at.Left);
        double height = Math.Min(h.Y + h.Height, at.Top + bubbleHeight) - Math.Max(h.Y, at.Top);
        return width > 0 && height > 0 ? width * height : 0;
    }

    // A bubble wider than the view has no valid range; pinning it to the margin keeps its start visible.
    private static double Clamp(double value, double size, double viewSize)
    {
        double max = viewSize - size - Margin;
        return max < Margin ? Margin : Math.Clamp(value, Margin, max);
    }
}
