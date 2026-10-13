using Xunit;

namespace XrplFile.Tests;

public class TidyUpPaceTests
{
    [Fact]
    public void A_fresh_pace_sends_twenty_five_at_once_and_pauses_one_poll()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));

        Assert.Equal(25, pace.InFlight);
        Assert.Equal(TimeSpan.FromSeconds(1), pace.Pause);
    }

    [Fact]
    public void Each_strained_round_halves_the_spends_in_flight_down_to_one()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));
        List<int> inFlight = [];

        for (int round = 0; round < 6; round++)
        {
            pace.Strained();
            inFlight.Add(pace.InFlight);
        }

        Assert.Equal([12, 6, 3, 1, 1, 1], inFlight);
    }

    [Fact]
    public void Each_strained_round_doubles_the_pause_up_to_sixteen_polls()
    {
        TidyUpPace pace = new(TimeSpan.FromSeconds(1));
        List<TimeSpan> pauses = [];

        for (int round = 0; round < 6; round++)
        {
            pace.Strained();
            pauses.Add(pace.Pause);
        }

        Assert.Equal(
            [2, 4, 8, 16, 16, 16],
            pauses.Select(pause => (int)pause.TotalSeconds));
    }
}
