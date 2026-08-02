using ItamiTimer.App;

namespace ItamiTimer.App.Tests;

/// <summary>
/// Boundary tests for the alarm model. All pure
/// functions/pure state, with now as a parameter -- no need to wait for real time.
/// </summary>
public class AlarmClockTests
{
    private static DateTime At(int h, int m, int s = 0) => new(2026, 7, 30, h, m, s);

    // ---- NextRing: the three worked examples the user gave on 2026-07-30 ----

    [Fact]
    public void ItIs2005_YellowHandAt905_RingsTonightAt2105()
    {
        // The yellow hand is 30° ahead of the hour hand = 1 hour
        Assert.Equal(At(21, 05), AlarmClock.NextRing(At(20, 05), 9 * 60 + 5));
    }

    [Fact]
    public void ItIs2005_YellowHandAt205_TheAfternoonSlotHasPassed_WaitsUntilTomorrow205Am()
    {
        var ring = AlarmClock.NextRing(At(20, 05), 2 * 60 + 5);
        Assert.Equal(new DateTime(2026, 7, 31, 2, 5, 0), ring);
    }

    [Fact]
    public void ItIs0805_YellowHandAt205_TheMorningSlotHasPassed_RingsAt1405()
    {
        Assert.Equal(At(14, 05), AlarmClock.NextRing(At(8, 05), 2 * 60 + 5));
    }

    // ---- Strictly less-than: an exact match means 12 hours later, never right now (DECISIONS E2) ----

    [Fact]
    public void AnExactMatchMeans12HoursLater_NotRightNow()
    {
        Assert.Equal(At(21, 00), AlarmClock.NextRing(At(9, 00), 9 * 60));
    }

    [Fact]
    public void AnExactMatchInTheAfternoonSlot_WaitsUntilTomorrowMorning()
    {
        var ring = AlarmClock.NextRing(At(21, 00), 9 * 60);
        Assert.Equal(new DateTime(2026, 7, 31, 9, 0, 0), ring);
    }

    [Fact]
    public void OneSecondShortStillCountsAsNotYet_RingsToday()
    {
        // Set to 09:00 at 08:59:59 -> should ring one second later, not jump to 21:00
        Assert.Equal(At(9, 00), AlarmClock.NextRing(At(8, 59, 59), 9 * 60));
    }

    // ---- Crossing the midnight boundary ----

    [Fact]
    public void YellowHandAt12OClockPosition_SetLateAtNight_RingsInTheHoursAfterMidnight()
    {
        // Yellow hand at minute 0 = clock position 12 = dial time 00:00.
        // At 23:30: today's 00:00 has passed, and 00:00+12=12:00 has also passed -> tomorrow's 00:00.
        var ring = AlarmClock.NextRing(At(23, 30), 0);
        Assert.Equal(new DateTime(2026, 7, 31, 0, 0, 0), ring);
    }

    [Fact]
    public void SetTheYellowHandToElevenFiftyFiveInTheEarlyMorning_RingsAt1155Am()
    {
        Assert.Equal(At(11, 55), AlarmClock.NextRing(At(0, 10), 11 * 60 + 55));
    }

    [Fact]
    public void TheRingMomentIsAlwaysStrictlyLaterThanTheMomentItWasSet()
    {
        // Exhaustively checks all 144 slots x one `now` every 7 minutes through the day: the invariant is FireAt > now
        for (var slot = 0; slot < 144; slot++)
            for (var minuteOfDay = 0; minuteOfDay < 24 * 60; minuteOfDay += 7)
            {
                var now = new DateTime(2026, 7, 30).AddMinutes(minuteOfDay);
                var ring = AlarmClock.NextRing(now, slot * 5);
                Assert.True(ring > now, $"slot={slot} now={now:HH:mm} ring={ring}");
                // And never more than 24 hours away -- the upper bound of the three-tier check
                Assert.True(ring <= now.AddHours(24), $"slot={slot} now={now:HH:mm} ring={ring}");
            }
    }

    // ---- Bump / ShouldFire / MarkFired ----

    [Fact]
    public void MovingTheYellowHandOneNotchShiftsFiveMinutes_WrapsWithoutOverflow()
    {
        var a = new AlarmClock();
        a.Bump(715, At(10, 0));
        Assert.Equal(715, a.Position);
        a.Bump(5, At(10, 0));
        Assert.Equal(0, a.Position);   // 719 -> wraps back to 0
    }

    [Fact]
    public void MovingCounterclockwisePastTwelve_WrapsToTheOtherEnd_NeverGoesNegative()
    {
        var a = new AlarmClock();
        a.Bump(-5, At(10, 0));         // 0 -> one notch counterclockwise
        Assert.Equal(715, a.Position);
        a.Bump(-30, At(10, 0));
        Assert.Equal(685, a.Position);
    }

    [Fact]
    public void MovingForwardThenBackToTheSamePosition_RestoresTheSameFireTime()
    {
        var a = new AlarmClock();
        var now = At(8, 58);
        a.Bump(5, now);
        var first = a.FireAt;
        a.Bump(30, now);
        a.Bump(-30, now);
        Assert.Equal(5, a.Position);
        Assert.Equal(first, a.FireAt);
    }

    [Fact]
    public void NeverFiresIfTheHandWasNeverMoved()
    {
        var a = new AlarmClock();
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void FiresOnceAtTheDueTime_ThenIsDone_NotADailyAlarm()
    {
        var a = new AlarmClock();
        a.Bump(5, At(8, 58));           // Yellow hand at 00:05 -> dial time 00:05; at 08:58 -> today 12:05
        Assert.Equal(At(12, 05), a.FireAt);

        Assert.False(a.ShouldFire(At(12, 04, 59)));
        Assert.True(a.ShouldFire(At(12, 05)));

        a.MarkFired();
        Assert.False(a.ShouldFire(At(23, 59)));
        Assert.Equal(5, a.Position);    // The time point stays -- it's still the source of the yellow hand's position
    }

    // ---- Restore: only one time point is stored, the yellow hand's position is derived from it mod 12 hours (2026-07-30) ----

    [Fact]
    public void AfterRestoreItIsNotActive_TheYellowHandIsVisibleButSilentUntilActivated()
    {
        var a = new AlarmClock();
        a.Restore(At(21, 05), At(20, 00));
        Assert.Equal((21 % 12) * 60 + 5, a.Position);   // 21:05 -> the dial position for 9:05
        Assert.Equal(At(21, 05), a.FireAt);
        Assert.False(a.ShouldFire(At(21, 04, 59)));     // Not activated, doesn't fire
        Assert.False(a.ShouldFire(At(21, 05)));
        a.Activate(At(20, 00));
        Assert.False(a.ShouldFire(At(21, 04, 59)));
        Assert.True(a.ShouldFire(At(21, 05)));           // Only fires after activation
    }

    [Fact]
    public void AnExpiredTimePointDoesNotFireLate_ActivatingItDoesNothing()
    {
        var a = new AlarmClock();
        a.Restore(At(9, 05), At(20, 00));   // The stored fire time has already passed
        Assert.Equal(9 * 60 + 5, a.Position);
        Assert.False(a.ShouldFire(At(23, 59)));
        a.Activate(At(20, 00));              // An expired time point, activating has no effect
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void IfTheHandWasNeverMoved_ItRestsAtTwelveOClockAndNeverFires()
    {
        var a = new AlarmClock();
        a.Restore(null, At(10, 00));
        Assert.Equal(0, a.Position);
        Assert.Null(a.FireAt);
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void MovingTheHandFurtherFromARestoredAfterimage_StartsFromTheAfterimagesPosition()
    {
        var a = new AlarmClock();
        a.Restore(At(9, 05), At(20, 00));   // An expired afterimage sits at 545
        a.Bump(5, At(20, 00));              // Move one notch -> 550 = dial time 9:10
        Assert.Equal(550, a.Position);
        Assert.Equal(At(21, 10), a.FireAt); // 20:00 < 21:10 (9:10+12h)
    }

    [Fact]
    public void MovingTheHandRepeatedly_RecomputesTheFireTimeEachTime_DisplayAndFireStayInSync()
    {
        var a = new AlarmClock();
        var now = At(20, 05);
        for (var i = 0; i < 12; i++) a.Bump(5, now);   // At 20:05, move the yellow hand to 01:00
        Assert.Equal(60, a.Position);
        // Dial time 01:00: 20:05 > 01:00, > 13:00 -> tomorrow at 01:00
        Assert.Equal(new DateTime(2026, 7, 31, 1, 0, 0), a.FireAt);
    }

}
