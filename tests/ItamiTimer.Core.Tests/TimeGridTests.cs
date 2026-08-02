using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// Whole-minute alignment. Looks trivial, but **the entire engine's coordinate system is
/// built on top of it**: the start truncated to a whole minute -> every cell is always a
/// full 60 seconds -> the write offset is always an integer (DECISIONS H9).
///
/// The `CeilToMinute` tests were removed on 2026-08-02 -- that function ended up only
/// used here, and the reasoning for "why floor instead of ceiling" (A6) living in
/// `FloorToMinute`'s own comment is enough on its own.
/// </summary>
public class TimeGridTests
{
    private static DateTimeOffset At(int h, int m, int s, int ms = 0)
        => new(2026, 7, 27, h, m, s, ms, TimeSpan.FromHours(8));

    [Fact]
    public void RoundsDown_WipesOutSecondsAndMilliseconds()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.FloorToMinute(At(10, 8, 37, 500)));
    }

    [Fact]
    public void ReturnsUnchangedIfAlreadyOnAWholeMinute()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.FloorToMinute(At(10, 8, 0)));
    }

    /// <summary>
    /// A6's knowingly accepted cost: truncation counts up to 59 seconds **before**
    /// clicking "Start" into the task too. The user chose this -- not wanting to sit and
    /// wait after clicking. This test pins those 59 seconds down, so nobody "fixes" it as
    /// a bug someday.
    /// </summary>
    [Fact]
    public void TheFlooredResultIsNeverLaterThanTheInput_AtMostCountsThePrevious59Seconds()
    {
        var t = At(10, 8, 59, 999);
        var floored = TimeGrid.FloorToMinute(t);

        Assert.True(floored <= t);
        Assert.True((t - floored).TotalSeconds < 60);
    }

    [Fact]
    public void RoundingPreservesTheOriginalTimeZoneOffset()
    {
        var t = new DateTimeOffset(2026, 7, 27, 10, 8, 37, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(8), TimeGrid.FloorToMinute(t).Offset);
    }
}
