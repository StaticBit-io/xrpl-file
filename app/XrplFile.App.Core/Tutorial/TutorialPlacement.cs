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
    /// the hole and takes the first side with room; when none has, takes the side with the most room
    /// and accepts overlap. The result always lies inside the view by <see cref="Margin"/>, or at
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

        Side side;
        if (below >= bubbleHeight) side = Side.Below;
        else if (above >= bubbleHeight) side = Side.Above;
        else if (right >= bubbleWidth) side = Side.Right;
        else if (left >= bubbleWidth) side = Side.Left;
        else side = MostRoom(below, above, right, left);

        return side switch
        {
            Side.Below => new TourPoint(Clamp(h.X, bubbleWidth, viewWidth), Clamp(h.Y + h.Height + Gap, bubbleHeight, viewHeight)),
            Side.Above => new TourPoint(Clamp(h.X, bubbleWidth, viewWidth), Clamp(h.Y - Gap - bubbleHeight, bubbleHeight, viewHeight)),
            Side.Right => new TourPoint(Clamp(h.X + h.Width + Gap, bubbleWidth, viewWidth), Clamp(h.Y, bubbleHeight, viewHeight)),
            _ => new TourPoint(Clamp(h.X - Gap - bubbleWidth, bubbleWidth, viewWidth), Clamp(h.Y, bubbleHeight, viewHeight)),
        };
    }

    private enum Side { Below, Above, Right, Left }

    private static Side MostRoom(double below, double above, double right, double left)
    {
        Side best = Side.Below;
        double room = below;

        if (above > room) { best = Side.Above; room = above; }
        if (right > room) { best = Side.Right; room = right; }
        if (left > room) best = Side.Left;

        return best;
    }

    // A bubble wider than the view has no valid range; pinning it to the margin keeps its start visible.
    private static double Clamp(double value, double size, double viewSize)
    {
        double max = viewSize - size - Margin;
        return max < Margin ? Margin : Math.Clamp(value, Margin, max);
    }
}
