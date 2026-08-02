using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class TaskRecordTests
{
    private static TaskRecord WithFocus(int minutes) => new()
    {
        StartedAt = new DateTimeOffset(2026, 7, 27, 10, 9, 0, TimeSpan.FromHours(8)),
        FocusMinutes = minutes,
        Group = "Economics",
    };

    /// <summary>
    /// DESIGN §6.1: rest = ⌈focus ÷ 5⌉. The real range is entirely multiples of 5, so this is exactly one fifth.
    /// </summary>
    [Theory]
    [InlineData(10, 2)]
    [InlineData(15, 3)]
    [InlineData(25, 5)]
    [InlineData(50, 10)]
    public void RestLengthIsExactlyOneFifth(int focus, int expectedRest)
    {
        Assert.Equal(expectedRest, WithFocus(focus).RestMinutes);
    }

    /// <summary>
    /// **A guardrail test's values must cover what the slider can actually produce**
    /// (the warning in DESIGN §6.1).
    ///
    /// The old test, `even the worst-case detection lag can't eat into the nominal rest`,
    /// only checked 10/25/50 -- all multiples of 5, so `⌊f/5⌋+1` always held as far as it
    /// was concerned; but once the Debug slider's step changed to 1 on 2026-07-31, that
    /// formula broke on 6 of the 8 values in 3-10, and the test stayed silent.
    ///
    /// Now checks **every positive integer**: never zero, never over-given either.
    /// </summary>
    [Fact]
    public void EveryPositiveIntegerLengthGivesARoundedUpFifthAndNeverZero()
    {
        for (var focus = 1; focus <= 120; focus++)
        {
            var rest = WithFocus(focus).RestMinutes;
            Assert.Equal((int)Math.Ceiling(focus / 5.0), rest);
            Assert.True(rest >= 1, $"{focus} minutes of focus computed {rest} minutes of rest");
        }
    }

    /// <summary>
    /// DECISIONS H6: rest **only ever reads the FocusMinutes locked in at submission**,
    /// regardless of how long this round actually took. What archiving decrements is the
    /// "remaining target", a different quantity inside JudgmentBuffer -- using it to compute
    /// rest would mean the longer you drag it out, the shorter your break gets, a backwards
    /// incentive.
    /// </summary>
    [Fact]
    public void RestLengthDoesNotDependOnHowLongItActuallyTook()
    {
        var task = WithFocus(50);
        var buf = new JudgmentBuffer(task.StartedAt, task.FocusMinutes);

        // Simulate: a full two hours on another app -> not a second counted -> running 2 hours triggers archiving
        var tick = task.StartedAt;
        for (var i = 0; i < 125; i++)
        {
            tick = tick.AddMinutes(1);
            var win = new List<AwEvent>
            {
                new(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                    JudgmentBuffer.QueryWindowSeconds, "chrome", "off-task", null),
            };
            buf.Tick(tick, win, [], Rules, "Economics");
        }

        Assert.True(buf.ArchivedSeconds > 0, "Should have archived after two hours");
        Assert.Equal(10, task.RestMinutes);          // Still ⌈50/5⌉, unaffected by the remaining target
    }

    private static readonly GroupRules Rules =
        GroupRules.Parse("""{ "groups": { "Economics": { "rules": [ { "app": "^econ$" } ] } } }""");

    /// <summary>
    /// §8.4.2a: range constraints belong to the UI layer, Core must accept any duration --
    /// otherwise every manual verification run in §13 would mean sitting through a real
    /// 10 minutes.
    /// </summary>
    [Fact]
    public void CoreAcceptsShortDurationsOutsideTheSlidersRange_ForVerification()
    {
        Assert.Equal(1, WithFocus(1).RestMinutes);
    }

    /// <summary>
    /// The +1's second job used to be: **any nonzero duration gets some rest**. With plain
    /// integer division, FocusMinutes ≤ 4 always computed 0 minutes -- the rest phase
    /// wouldn't exist at all, and the rest wedge (§8.4.4) would never be visible. Core must
    /// accept any duration (the manual verification in §13 runs tasks of 1-2 minutes).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(11, 3)]
    public void EvenAFractionUnderFiveMinutesStillGetsOneMinuteOfRest_NeverZero(int focus, int rest)
    {
        Assert.Equal(rest, WithFocus(focus).RestMinutes);
        Assert.True(WithFocus(focus).RestMinutes >= 1, "Any task with a nonzero duration should have some rest");
    }

    [Fact]
    public void ANewTaskDefaultsToCommitted()
    {
        var task = WithFocus(25);
        Assert.Equal(RecordStatus.Committed, task.Status);
        Assert.Null(task.AbandonedAt);
    }
}
