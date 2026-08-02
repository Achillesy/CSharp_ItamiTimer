using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// **Boundaries and catch-up** for the second-version engine.
///
/// This batch rewrites and carries over what `BoundaryTests` / `CheckpointCatchUpTests`
/// (deleted 2026-08-02) used to guard — those two files guarded the now-deleted `Replay`,
/// but the **problems** they guarded against are still real: does crossing a whole hour
/// misalign anything, can missed ticks be caught up, does writing to the end of the buffer
/// overflow.
///
/// All pure functions: feed in synthetic events, no waiting, no touching ActivityWatch.
/// </summary>
public class JudgmentBufferBoundaryTests
{
    private static readonly GroupRules Rules = GroupRules.Parse(
        """{ "groups": { "Economics": { "rules": [ { "app": "^econ$" } ] } } }""");

    private const string Goal = "Economics";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 2, 14, 5, 0, TimeSpan.FromHours(8));

    private static AwEvent Win(DateTimeOffset from, double seconds, string app)
        => new(from, seconds, app, $"{app} window", null);

    private static AwEvent Afk(DateTimeOffset from, double seconds)
        => new(from, seconds, null, null, "afk");

    /// <summary>One window event that covers the entire query window.</summary>
    private static List<AwEvent> Whole(DateTimeOffset tick, string app)
        => [Win(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                JudgmentBuffer.QueryWindowSeconds, app)];

    private static int GrayEnd(JudgmentBuffer buf)
    {
        for (var i = JudgmentBuffer.TotalSize - 1; i >= 0; i--)
            if (buf[i] == JudgmentCode.Gray) return i + 1;
        return -1;
    }

    // ---------------------------------------------------------------- Crossing boundaries

    /// <summary>
    /// Guard for the 2026-07-28 bug (the commitment arc jumping to the second lap across
    /// midnight): a cell's time and lap number must both come from **how many minutes the
    /// task has been running**, never from the wall-clock's absolute minute number. Any
    /// whole hour would trigger this; midnight just happens to also be one.
    /// </summary>
    [Fact]
    public void CellsDoNotShiftAcrossHourAndMidnightBoundaries()
    {
        var late = new DateTimeOffset(2026, 8, 2, 23, 58, 0, TimeSpan.FromHours(8));
        var buf = new JudgmentBuffer(late, 10);

        for (var i = 1; i <= 5; i++)
        {
            var t = late.AddMinutes(i);
            buf.Tick(t, Whole(t, "econ"), [], Rules, Goal);
        }

        var cells = buf.ToMinuteCells();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i, cells[i].Index);
            Assert.Equal(late.AddMinutes(i), cells[i].Start);   // 23:58 / 23:59 / 00:00 ...
        }
        // Cell 2 crosses midnight exactly; the date must follow along
        Assert.Equal(3, cells[2].Start.Day);
    }

    /// <summary>Projection range = elapsed so far + the commitment arc (§4.6); beyond that is Init, not emitted.</summary>
    [Fact]
    public void ProjectionRangeIsExactlyElapsedPlusCommitmentArc()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);      // 1 minute elapsed, 1 minute earned

        var cells = buf.ToMinuteCells();
        Assert.Equal(1 + 9, cells.Count);                       // 1 cell elapsed + 9 cells still owed
        Assert.Equal(60, cells[0].FocusSeconds);
        Assert.Equal(60, cells[^1].GraySeconds);
    }

    /// <summary>Every cell is always exactly 60 seconds -- the five counts must always add up to 60.</summary>
    [Fact]
    public void TheFiveCountsInEveryCellAlwaysAddUpToSixty()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(Start, 37, "econ"), Win(Start.AddSeconds(37), 40, "chrome")],
                 [Afk(Start.AddMinutes(1).AddSeconds(20), 25)], Rules, Goal);

        foreach (var c in buf.ToMinuteCells())
            Assert.Equal(60, c.FocusSeconds + c.OffTaskSeconds + c.AfkSeconds + c.GraySeconds + c.InitSeconds);
    }

    // ---------------------------------------------------------------- Catch-up

    /// <summary>
    /// A tick missed **still within the 4-minute window** must be fully caught up -- the
    /// buffer writes by absolute offset, so a late tick is only late, not lost.
    /// </summary>
    [Fact]
    public void MissingTwoTicksStillWithinTheQueryWindowIsFullyCaughtUpNextTick()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        // Ticks 2 and 3 didn't run (ActivityWatch timeout / UI froze), tick 4 comes back
        var t4 = Start.AddMinutes(4);
        buf.Tick(t4, Whole(t4, "econ"), [], Rules, Goal);

        Assert.Equal(4 * 60, buf.FocusedSeconds);               // Not a second short across four minutes
        Assert.Equal(4, buf.ToMinuteCells().Count(c => c.FocusSeconds == 60));
    }

    /// <summary>
    /// When the gap is longer than the query window, the stretch outside the window is
    /// judged `Init`: **not counted, and not given away for free either**. The deficit is
    /// the test: that stretch of time must remain owed, untouched.
    /// </summary>
    [Fact]
    public void DeficitDoesNotShrinkWhenAMissedTickExceedsTheWindow()
    {
        var buf = new JudgmentBuffer(Start, 30);
        var t1 = Start.AddMinutes(1);
        var before = buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal).DeficitSeconds;

        // Slept for 20 minutes; on waking only the last 4 minutes can be queried
        var wake = Start.AddMinutes(21);
        var after = buf.Tick(wake, Whole(wake, "econ"), [], Rules, Goal).DeficitSeconds;

        // Only the 4 minutes right after waking reduce the deficit; the 16 minutes in between don't count at all
        Assert.Equal(before - JudgmentBuffer.QueryWindowSeconds, after);
    }

    // ---------------------------------------------------------------- End of buffer

    /// <summary>
    /// When the commitment arc would be longer than the drawable span, it must be clipped,
    /// **never overflow**. Target is 200 minutes = 12000 seconds, while the drawable span
    /// is only 7200 seconds.
    /// </summary>
    [Fact]
    public void CommitmentArcIsClippedAtTheEndWhenItExceedsTheDrawableSpan()
    {
        var buf = new JudgmentBuffer(Start, 200);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        Assert.Equal(JudgmentBuffer.TotalSize, GrayEnd(buf));           // Runs all the way to the end
        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.TotalSize - 1]);
    }

    /// <summary>
    /// The **real** guard for §15.5: the settlement range for archiving must be
    /// `[180, 3780)`, not `[0, 3600)`.
    ///
    /// ⚠️ For this test to actually catch that offset, **the content inside padding must
    /// differ from the content in the real span**. If the whole buffer were focus time,
    /// both formulas would come out to 3600 and the test would pass green while the bug
    /// stayed alive -- that's exactly how a "same quantity, two different conventions" bug
    /// slips through.
    /// </summary>
    [Fact]
    public void ArchivingOnlySettlesTheHourAfterTaskStart_PaddingDoesNotCount()
    {
        var buf = new JudgmentBuffer(Start, 200);

        // First tick: the first 3 minutes in the window (padding, before Start was clicked)
        // are off-task, everything after is focus
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, [Win(Start.AddMinutes(-3), 180, "chrome"), Win(Start, 60, "econ")],
                 [], Rules, Goal);
        Assert.Equal(JudgmentCode.OffTask, buf[0]);            // Padding really is red

        var settled = 0;
        for (var i = 2; i <= 120; i++)
        {
            var t = Start.AddMinutes(i);
            settled += buf.Tick(t, Whole(t, "econ"), [], Rules, Goal).SettledSeconds;
        }

        Assert.Equal(JudgmentBuffer.ArchiveSeconds, settled);
    }

    /// <summary>Running for a full 3 hours should archive twice, settling exactly two hours.</summary>
    [Fact]
    public void ThreeHoursArchivesTwice_ExactlyOneHourEachTime()
    {
        var buf = new JudgmentBuffer(Start, 300);
        var settled = 0;
        for (var i = 1; i <= 180; i++)
        {
            var t = Start.AddMinutes(i);
            settled += buf.Tick(t, Whole(t, "econ"), [], Rules, Goal).SettledSeconds;
        }

        Assert.Equal(2 * JudgmentBuffer.ArchiveSeconds, settled);
        Assert.Equal(2 * JudgmentBuffer.ArchiveSeconds, buf.ArchivedSeconds);
        Assert.Equal(Start.AddHours(2), buf.TaskStart);
        // The three quantities line up: the original 300 minutes = 120 minutes settled + the remaining target
        Assert.Equal(300 * 60 - settled, buf.RemainingTargetSeconds);
    }

    // ---------------------------------------------------------------- Event boundaries

    /// <summary>
    /// T1/F7: the query window is widened 6 hours into the past, so the event list can
    /// contain long events that **started hours earlier**. They must still be paintable
    /// (otherwise leaving the same window open the whole time would wrongly read as no
    /// record), and must not paint out of bounds.
    /// </summary>
    [Fact]
    public void ALongEventStartingWellBeforeTheWindowStillPaintsWithoutOverflowing()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        // An event that started six hours ago and lasts seven hours
        var long_ = Win(Start.AddHours(-6), 7 * 3600, "econ");

        buf.Tick(t1, [long_], [], Rules, Goal);

        Assert.Equal(60, buf.FocusedSeconds);                  // This minute of the task is counted
        Assert.Equal(JudgmentCode.Focused, buf[JudgmentBuffer.PaddingSeconds]);
    }

    /// <summary>
    /// The 3 minutes **before** Start was clicked (padding) are never counted, even if the
    /// goal app was already in focus at the time. Truncating the start to a whole minute
    /// (A6) already gives away up to 59 seconds for free; giving away padding too would be
    /// too generous.
    /// </summary>
    [Fact]
    public void FocusInsidePaddingIsNeverCounted()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        // The event covers the entire 4-minute window, of which the first 3 minutes are before the task started
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        Assert.Equal(JudgmentCode.Focused, buf[0]);            // Padding really did get painted
        Assert.Equal(60, buf.FocusedSeconds);                  // But not a single second of it counts
    }

    /// <summary>
    /// Afk shrinkage (T5) can move the commitment arc's end **backward** --
    /// `RefreshGray` must clear before it fills, otherwise a leftover smear stays behind and
    /// the arc ends up longer than it should be.
    /// </summary>
    [Fact]
    public void CommitmentArcEndMovesBackWhenAfkIsReclassifiedAsFocusWithNoLeftoverSmear()
    {
        var buf = new JudgmentBuffer(Start, 10);

        // First tick: the entire minute 0 is covered by afk -> not a single second earned
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [Afk(Start, 60)], Rules, Goal);
        Assert.Equal(0, buf.FocusedSeconds);
        var endBefore = GrayEnd(buf);

        // Second tick: afk shrinks to nothing (the person was there the whole time), the first two minutes get reclassified as focus
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, Whole(t2, "econ"), [], Rules, Goal);

        Assert.Equal(120, buf.FocusedSeconds);
        var endAfter = GrayEnd(buf);

        Assert.True(endAfter < endBefore,
            $"The arc's end should move backward: {endBefore} -> {endAfter}");
        Assert.NotEqual(JudgmentCode.Gray, buf[endAfter]);     // Clean past the end, no leftover smear
    }

    /// <summary>
    /// When a second is covered by both afk and a window event in the goal app, it's judged
    /// `Afk` -- afk is painted last and covers everything. "Walk away while the goal app
    /// stays focused" is the cheapest exploit available (A4) and must be sealed shut.
    /// </summary>
    [Fact]
    public void WalkingAwayWhileStayingOnTheGoalAppIsJudgedAwayNotFocus()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);

        // The whole minute is on the goal app, but afk for the last 30 seconds
        buf.Tick(t1, Whole(t1, "econ"), [Afk(Start.AddSeconds(30), 30)], Rules, Goal);

        var cell = buf.ToMinuteCells()[0];
        Assert.Equal(30, cell.FocusSeconds);
        Assert.Equal(30, cell.AfkSeconds);
        Assert.Equal(0, cell.OffTaskSeconds);                  // Being away isn't your fault, not judged red
    }
}
