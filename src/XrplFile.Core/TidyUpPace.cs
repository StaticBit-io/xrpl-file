namespace XrplFile;

/// <summary>
/// How fast the tidy-up goes (spec 4.6, I15): how many spends are in flight at once and how long it
/// pauses between rounds. A round that showed strain halves the first and doubles the second;
/// nothing speeds it up again, because a load warning means load already on the client's IP, which
/// rippled lets decay over a 32-second window.
/// </summary>
internal sealed class TidyUpPace(TimeSpan poll)
{
    /// <summary>Spends in flight at once before any strain: the ticket strategy's own default.</summary>
    public const int StartInFlight = 25;

    /// <summary>The longest pause, in polls: one second to sixteen with the default poll.</summary>
    public const int MaxPauseInPolls = 16;

    private readonly TimeSpan _maxPause = poll * MaxPauseInPolls;

    public int InFlight { get; private set; } = StartInFlight;

    public TimeSpan Pause { get; private set; } = poll;

    /// <summary>Slows down after a round that showed strain: <c>max(1, inFlight / 2)</c> and <c>min(16 × poll, pause × 2)</c>.</summary>
    public void Strained()
    {
        InFlight = Math.Max(1, InFlight / 2);

        TimeSpan doubled = Pause * 2;
        Pause = doubled > _maxPause ? _maxPause : doubled;
    }
}
