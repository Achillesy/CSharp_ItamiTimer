using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// Alarms 清单（DESIGN §17）的解析和挑选规则。全是纯函数，`now` 永远是参数，跟
/// <c>AlarmClockTests</c> 一样的路数。
/// </summary>
public class AlarmsListTests
{
    private static DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0);

    // ---------------------------------------------------------------- Parse

    [Fact]
    public void OnlyChecklistLinesAreRecognizedAsEntries()
    {
        var text = """
            - [ ] 2026-08-06 14:00 吃药
            - [ ] 2026-08-06 21:30 洗澡
            """;
        var entries = AlarmsList.Parse(text);
        Assert.Equal(2, entries.Count);
        Assert.Equal("吃药", entries[0].Text);
        Assert.Equal(At(2026, 8, 6, 14, 0), entries[0].At);
    }

    [Fact]
    public void HeadingsBlankLinesAndProseAreSilentlyIgnored()
    {
        var text = """
            ## 8 月安排
            这一段随便写什么都行，程序看不懂就直接跳过

            - [ ] 2026-08-06 14:00 吃药

            <!-- 一条 HTML 注释 -->
            """;
        var entries = AlarmsList.Parse(text);
        Assert.Single(entries);
        Assert.Equal("吃药", entries[0].Text);
    }

    [Fact]
    public void CheckedEntriesAreAlwaysExcluded_EvenWhenStillInTheFuture()
    {
        var text = "- [x] 2099-01-01 09:00 未来但已勾选";
        Assert.Empty(AlarmsList.Parse(text));
    }

    [Fact]
    public void UppercaseXAlsoCountsAsChecked()
    {
        var text = "- [X] 2099-01-01 09:00 未来但已勾选";
        Assert.Empty(AlarmsList.Parse(text));
    }

    [Fact]
    public void AnUnparseableDateIsTreatedAsAnUnrecognizedLine_NotAnError()
    {
        var text = """
            - [ ] 2026-13-99 99:99 坏数据
            - [ ] 2026-08-06 14:00 吃药
            """;
        var entries = AlarmsList.Parse(text);
        Assert.Single(entries);
        Assert.Equal("吃药", entries[0].Text);
    }

    [Fact]
    public void TheLabelIsEverythingToTheEndOfTheLine_SpacesIncluded()
    {
        var entries = AlarmsList.Parse("- [ ] 2026-08-07 09:00 复诊，带上上次的报告");
        Assert.Equal("复诊，带上上次的报告", entries[0].Text);
    }

    [Fact]
    public void NoSecondsInTheFormat_TimestampIsAlwaysExactlyOnTheMinute()
    {
        var entries = AlarmsList.Parse("- [ ] 2026-08-06 14:00 吃药");
        Assert.Equal(0, entries[0].At.Second);
    }

    // ---------------------------------------------------------------- Next

    [Fact]
    public void NextPicksTheEarliestFutureEntry_RegardlessOfFileOrder()
    {
        var entries = new[]
        {
            new AlarmEntry(At(2026, 8, 10, 10, 0), "晚"),
            new AlarmEntry(At(2026, 8, 6, 21, 30), "早"),
        };
        var next = AlarmsList.Next(entries, At(2026, 8, 6, 20, 0));
        Assert.Equal("早", next!.Value.Text);
    }

    [Fact]
    public void NextSkipsEntriesAlreadyInThePast()
    {
        var entries = new[] { new AlarmEntry(At(2026, 8, 6, 8, 0), "已经过去") };
        Assert.Null(AlarmsList.Next(entries, At(2026, 8, 6, 9, 0)));
    }

    [Fact]
    public void NextReturnsNullOnAnEmptyList()
    {
        Assert.Null(AlarmsList.Next([], At(2026, 8, 6, 9, 0)));
    }

    // ---------------------------------------------------------------- Due

    [Fact]
    public void DueOnlyReturnsEntriesStrictlyAfterTheWatermark()
    {
        var entries = new[] { new AlarmEntry(At(2026, 8, 6, 14, 0), "吃药") };
        var after = At(2026, 8, 6, 14, 0);   // 水位线正好等于条目时间 -> 不算新到点，防止重复触发
        Assert.Empty(AlarmsList.Due(entries, after, At(2026, 8, 6, 14, 0)));
    }

    [Fact]
    public void DueReturnsMultipleEntriesInTheSameSweepSortedByTime()
    {
        var entries = new[]
        {
            new AlarmEntry(At(2026, 8, 6, 14, 5), "晚"),
            new AlarmEntry(At(2026, 8, 6, 14, 0), "早"),
        };
        var due = AlarmsList.Due(entries, At(2026, 8, 6, 13, 0), At(2026, 8, 6, 15, 0));
        Assert.Equal(["早", "晚"], due.Select(e => e.Text));
    }

    [Fact]
    public void DueNeverReturnsAnythingAtOrBeforeTheWatermark_NoCatchUpOnRestart()
    {
        // 程序关闭期间错过的条目：重新打开后水位线定在启动那一刻，之前的条目直接跳过
        var entries = new[] { new AlarmEntry(At(2026, 8, 4, 9, 0), "关机期间错过的") };
        var startedAt = At(2026, 8, 6, 8, 0);
        Assert.Empty(AlarmsList.Due(entries, startedAt, At(2026, 8, 6, 8, 1)));
    }

    // ---------------------------------------------------------------- DotPosition

    [Fact]
    public void DotPositionMatchesAlarmClockPositionsConvention_ModTwelveHours()
    {
        var next = new AlarmEntry(At(2026, 8, 6, 14, 30), "吃药");
        var now = At(2026, 8, 6, 8, 0);   // 6.5 小时以内
        Assert.Equal((14 % 12) * 60 + 30, AlarmsList.DotPosition(next, now));
    }

    [Fact]
    public void DotPositionIsNullBeyondTheTwelveHourHorizon()
    {
        // 现在 8 点，下一条是 5 天后的 14 点 —— mod-12 会画出"2 点钟方向"这种误导性的位置，
        // 所以直接不画（DESIGN §17）
        var next = new AlarmEntry(At(2026, 8, 11, 14, 0), "5 天后");
        var now = At(2026, 8, 6, 8, 0);
        Assert.Null(AlarmsList.DotPosition(next, now));
    }

    [Fact]
    public void DotPositionIsNullExactlyAtTheTwelveHourBoundary()
    {
        var now = At(2026, 8, 6, 8, 0);
        var next = new AlarmEntry(now.AddHours(12), "正好 12 小时后");
        Assert.Null(AlarmsList.DotPosition(next, now));
    }

    [Fact]
    public void DotPositionIsNullWhenThereIsNoNextEntry()
    {
        Assert.Null(AlarmsList.DotPosition(null, At(2026, 8, 6, 8, 0)));
    }
}
