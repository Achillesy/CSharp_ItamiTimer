using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// The second-version judgment engine. All pure functions: feed in
/// synthetic events, no waiting, no touching ActivityWatch.
///
/// Every invariant guarded here has a source in the docs -- **the test name is the
/// invariant**.
/// </summary>
public class JudgmentBufferTests
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

    /// <summary>Builds one window event covering the entire query window.</summary>
    private static List<AwEvent> WholeWindow(DateTimeOffset tick, string app)
        => [Win(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                JudgmentBuffer.QueryWindowSeconds, app)];

    // ---------------------------------------------------------------- Coordinates

    [Fact]
    public void BufferCell180IsTaskStart_TheFirst180SecondsArePadding()
    {
        var buf = new JudgmentBuffer(Start, 25);
        Assert.Equal(Start.AddSeconds(-JudgmentBuffer.PaddingSeconds), buf.WallClock);
        Assert.Equal(Start, buf.TaskStart);
        Assert.Equal(JudgmentBuffer.PaddingSeconds, buf.Head);
    }

    [Fact]
    public void TheOpeningCommitmentArcIsTheWholeTaskLength_UsingTheSamePerTickAlgorithm()
    {
        var buf = new JudgmentBuffer(Start, 25);

        // Padding is not Gray -- it's never counted, never drawn
        Assert.Equal(JudgmentCode.Init, buf[0]);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds - 1]);

        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.PaddingSeconds]);
        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.PaddingSeconds + 25 * 60 - 1]);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds + 25 * 60]);
    }

    // ---------------------------------------------------------------- Covering algorithm

    [Fact]
    public void HittingTheGoalCountsAsFocus_EverythingElseCountsAsOffTask()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "econ"), [], Rules, Goal);
        Assert.Equal(60, buf.FocusedSeconds);

        var buf2 = new JudgmentBuffer(Start, 25);
        buf2.Tick(t1, WholeWindow(t1, "chrome"), [], Rules, Goal);
        Assert.Equal(0, buf2.FocusedSeconds);
        Assert.Equal(JudgmentCode.OffTask, buf2[JudgmentBuffer.PaddingSeconds]);
    }

    [Fact]
    public void AfkOverridesEverything_WalkingAwayWhileOnTheGoalAppDoesNotCount()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // The whole window stays on the goal app, but afk says nobody's there
        buf.Tick(t1, WholeWindow(t1, "econ"),
                 [Afk(t1.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                      JudgmentBuffer.QueryWindowSeconds)],
                 Rules, Goal);

        Assert.Equal(0, buf.FocusedSeconds);
        Assert.Equal(JudgmentCode.Afk, buf[JudgmentBuffer.PaddingSeconds]);
    }

    [Fact]
    public void WhenASecondHasBothFocusAndOffTaskItIsJudgedOffTask_CoveringOrderIsFailClosed()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // Both events land in the task's second 0: Focused is painted first, OffTask painted after -> OffTask wins
        var events = new List<AwEvent>
        {
            Win(Start, 0.4, "econ"),
            Win(Start.AddSeconds(0.4), 0.6, "chrome"),
        };
        buf.Tick(t1, events, [], Rules, Goal);

        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);
    }

    /// <summary>
    /// T4: when a title changes every second, ActivityWatch writes out a string of
    /// <c>duration = 0</c> events. The old second-by-second lookup asked "which event
    /// covers this second" -- an empty interval could never answer that, and the whole
    /// stretch got misjudged as "no record". The new per-second painting must be able to
    /// draw these in.
    /// </summary>
    [Fact]
    public void ZeroDurationEventsStillFillTheSecondTheyLandIn()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        var churn = new List<AwEvent>();
        for (var i = 0; i < 60; i++) churn.Add(Win(Start.AddSeconds(i), 0, "econ"));

        buf.Tick(t1, churn, [], Rules, Goal);
        Assert.Equal(60, buf.FocusedSeconds);
    }

    // ---------------------------------------------------------------- Missing ActivityWatch data

    [Fact]
    public void WhenATickCannotReachActivityWatchOnlyTheLastMinuteBecomesAwOffline_TheFirstThreeMinutesAreUntouched()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "chrome"), [], Rules, Goal);   // Minute 0 judged off-task
        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);

        // Second tick can't reach ActivityWatch = empty event list
        buf.Tick(Start.AddMinutes(2), [], [], Rules, Goal);

        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);      // Not wiped back to green
        Assert.Equal(JudgmentCode.AwOffline, buf[JudgmentBuffer.PaddingSeconds + 60]);
        Assert.Equal(60, buf.FocusedSeconds);                                        // Only the new minute counts
    }

    /// <summary>
    /// T3: ActivityWatch window events are always 6-12 seconds behind, so the last few
    /// seconds of every tick have no record yet and get judged AwOffline. The next tick's
    /// 4-minute rewrite must reclassify them -- that's "self-healing", no extra mechanism
    /// needed.
    /// </summary>
    [Fact]
    public void TheLaggingFinalSecondsCountAsFocusFirst_ThenGetReclassifiedByRealDataNextTick()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // First tick: data only reaches t1-10s, the last 10 seconds are empty
        buf.Tick(t1, [Win(t1.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                          JudgmentBuffer.QueryWindowSeconds - 10, "chrome")], [], Rules, Goal);
        Assert.Equal(10, buf.FocusedSeconds);                       // Those 10 seconds were given away for free
        Assert.Equal(JudgmentCode.AwOffline, buf[JudgmentBuffer.PaddingSeconds + 59]);

        // Second tick: ActivityWatch has caught up, those 10 seconds were actually off-task
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(t2.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                          JudgmentBuffer.QueryWindowSeconds - 10, "chrome")], [], Rules, Goal);
        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds + 59]);
    }

    /// <summary>
    /// Once a missed tick exceeds the query window, those minutes **stay Init, uncounted**.
    /// "I simply never queried this" must not be handed out for free as "ActivityWatch has
    /// no record" -- otherwise sleeping through the night could fill an entire task.
    /// </summary>
    [Fact]
    public void WhenAMissedTickIsTooLongTheGapOutsideTheWindowIsNeverGivenAwayForFree()
    {
        var buf = new JudgmentBuffer(Start, 60);
        buf.Tick(Start.AddMinutes(1), WholeWindow(Start.AddMinutes(1), "econ"), [], Rules, Goal);

        // Slept for 20 minutes; on waking, this tick can only query the last 4 minutes
        var wake = Start.AddMinutes(21);
        buf.Tick(wake, WholeWindow(wake, "econ"), [], Rules, Goal);

        // Only two things count: the first tick's 1 minute + the 4-minute window on waking
        Assert.Equal(60 + JudgmentBuffer.QueryWindowSeconds, buf.FocusedSeconds);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds + 60 * 10]);
    }

    // ---------------------------------------------------------------- The commitment arc and completion

    private static int GrayEnd(JudgmentBuffer buf)
    {
        for (var i = JudgmentBuffer.TotalSize - 1; i >= 0; i--)
            if (buf[i] == JudgmentCode.Gray) return i + 1;
        return -1;
    }

    [Fact]
    public void WhenFullyFocusedTheCommitmentArcsEndDoesNotMove_ItOnlyShrinks()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "econ"), [], Rules, Goal);
        var end = GrayEnd(buf);

        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, WholeWindow(t2, "econ"), [], Rules, Goal);

        Assert.Equal(end, GrayEnd(buf));
    }

    [Fact]
    public void OneMinuteOffTaskSlidesTheDeadlineArcForwardByOneCell()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "econ"), [], Rules, Goal);
        var end = GrayEnd(buf);

        // Minute 0 is still focus (the rewrite covers it, so it has to be fed back in unchanged); minute 1 goes off-task
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(Start, 60, "econ"), Win(Start.AddMinutes(1), 60, "chrome")],
                 [], Rules, Goal);

        Assert.Equal(end + 60, GrayEnd(buf));
    }

    /// <summary>
    /// Completion is defined as "this tick computes a deficit &lt;= 0", equivalent to "the
    /// commitment arc is empty". It's an **event**, not a moment derived from the ledger --
    /// so it can never regress (DECISIONS H5).
    /// </summary>
    [Fact]
    public void TheMomentTheCommitmentArcDisappearsIsTheMomentFocusIsAchieved()
    {
        var buf = new JudgmentBuffer(Start, 3);
        TickOutcome outcome = default;

        for (var i = 1; i <= 3; i++)
        {
            var t = Start.AddMinutes(i);
            outcome = buf.Tick(t, WholeWindow(t, "econ"), [], Rules, Goal);
            Assert.Equal(outcome.Completed, GrayEnd(buf) < 0);
        }

        Assert.True(outcome.Completed);
        Assert.Equal(0, outcome.DeficitSeconds);
        Assert.True(buf.IsFocusComplete);
    }

    [Fact]
    public void TheDeficitRoundsUpToAWholeMinute_SoNoCellIsHalfGrayHalfData()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        // Only 37 seconds of focus this minute
        buf.Tick(t1, [Win(Start, 37, "econ"), Win(Start.AddSeconds(37), 23, "chrome")],
                 [], Rules, Goal);

        var cells = buf.ToMinuteCells();
        foreach (var c in cells)
            Assert.True(c.GraySeconds is 0 or 60, $"Cell {c.Index} was gray for {c.GraySeconds} seconds");
    }

    // ---------------------------------------------------------------- Archiving

    /// <summary>
    /// Archiving = "abandoned an hour ago, immediately restarted with the remaining
    /// target". So it must be **fully continuous**: "how much is still owed" must be
    /// identical before and after archiving (DESIGN §4.4). Writing it as `[0,3600)` would
    /// make it jump, in either direction.
    /// </summary>
    [Fact]
    public void TheDeficitIsIdenticalBeforeAndAfterArchiving_AndTheSettlementRangeExcludesPadding()
    {
        var buf = new JudgmentBuffer(Start, 200);        // Target is deliberately large so 2 full hours still doesn't complete it
        var deficitBefore = 0;
        var settled = 0;

        for (var i = 1; i <= 120; i++)
        {
            var t = Start.AddMinutes(i);
            if (i == 120) deficitBefore = 200 * 60 - buf.FocusedSeconds;
            var outcome = buf.Tick(t, WholeWindow(t, "econ"), [], Rules, Goal);
            settled += outcome.SettledSeconds;
        }

        // Exactly one hour gets settled, not a second more -- the 3 minutes of focus inside padding don't count
        Assert.Equal(JudgmentBuffer.ArchiveSeconds, settled);
        Assert.Equal(JudgmentBuffer.ArchiveSeconds, buf.ArchivedSeconds);

        var deficitAfter = buf.RemainingTargetSeconds - buf.FocusedSeconds;
        Assert.Equal(deficitBefore - 60, deficitAfter);   // Only differs by the 60 seconds just earned on this last tick
    }

    [Fact]
    public void AfterArchivingTheTaskStartMovesForwardByAnHour_TheLapNumberFollowsTheBufferPosition()
    {
        var buf = new JudgmentBuffer(Start, 200);
        for (var i = 1; i <= 120; i++)
        {
            var t = Start.AddMinutes(i);
            buf.Tick(t, WholeWindow(t, "econ"), [], Rules, Goal);
        }

        Assert.Equal(Start.AddHours(1), buf.TaskStart);
        Assert.Equal(3600, buf.ElapsedSeconds);
        Assert.Equal(60, buf.ToMinuteCells().Count(c => c.FocusSeconds > 0));
    }

    /// <summary>
    /// §15.6: once the write offset runs past the end of the buffer, ElapsedSeconds freezes
    /// and the archive condition never becomes true again, permanently deadlocking the
    /// session. Even a very long suspend must still be able to continue.
    /// </summary>
    [Fact]
    public void SleepingForOverTwoHoursDoesNotFreezeTheSession()
    {
        var buf = new JudgmentBuffer(Start, 25);
        buf.Tick(Start.AddMinutes(1), WholeWindow(Start.AddMinutes(1), "econ"), [], Rules, Goal);

        var wake = Start.AddHours(5);                     // Slept for 5 hours
        var outcome = buf.Tick(wake, WholeWindow(wake, "econ"), [], Rules, Goal);

        Assert.True(buf.ArchivedSeconds > 0, "Should have rolled forward several times to make room for this tick");
        Assert.Equal(JudgmentBuffer.QueryWindowSeconds, buf.FocusedSeconds);
        Assert.True(outcome.DeficitSeconds > 0);

        // Can still keep running
        var next = wake.AddMinutes(1);
        buf.Tick(next, WholeWindow(next, "econ"), [], Rules, Goal);
        Assert.Equal(JudgmentBuffer.QueryWindowSeconds + 60, buf.FocusedSeconds);
    }
}
