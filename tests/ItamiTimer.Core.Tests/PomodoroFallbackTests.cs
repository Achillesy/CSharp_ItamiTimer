using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 钉住 §11.1 第 1 条那个承诺：**纯番茄钟模式下 Core 一行不用改。**
///
/// 做法是喂一对合成事件 —— 一条 `app="ItamiTimer"` 的窗口事件（`GroupRules` 把它
/// 硬编码判成 `Neutral`，§5.3 第 1 步的自身豁免）+ 一条 `not-afk`，整段重放出来
/// 就是满格计入。不需要 rules.json、不需要勾任何小目标。
///
/// **这些测试真正防的是什么**：哪天有人把 `Neutral` 改成不计入、或者动了自身豁免
/// 那几个名字，番茄钟模式会**静默地不再计时** —— 界面上看不出来（盘面本来就该全绿），
/// 日志里也不会报错。这类"不报错、不崩溃、只是安静把事情做错"的 bug，这个项目
/// 已经出过好几个（Phase 恒为 NoData、承诺弧跨整点跳圈、达成时刻打成 UTC）。
/// </summary>
public class PomodoroFallbackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 28, 20, 0, 0, TimeSpan.FromHours(8));

    /// <summary>番茄钟模式下用的就是这一份：什么规则都没有。</summary>
    private static readonly GroupRules NoRules = GroupRules.Empty;

    private static TaskRecord Task(int focus = 25) => new()
    {
        StartedAt = T0,
        FocusMinutes = focus,
        Groups = [],          // 番茄钟模式没有小目标
    };

    /// <summary>跟 <c>TaskSession.SyntheticSpan</c> 造的是同一对事件。</summary>
    private static (List<AwEvent> Win, List<AwEvent> Afk) Synthetic(double minutes)
    {
        var s = minutes * 60;
        return ([new AwEvent(T0, s, "ItamiTimer", "Pomodoro", null)],
                [new AwEvent(T0, s, null, null, "not-afk")]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7.5)]
    [InlineData(25)]
    public void 合成事件整段计入_专注时长就等于已过去的时间(double minutes)
    {
        var (win, afk) = Synthetic(minutes);
        var s = Replay.Run(Task(), NoRules, win, afk, T0.AddMinutes(minutes));

        Assert.Equal(minutes * 60, s.FocusedSeconds, 1);
        Assert.Empty(s.Violations);
        Assert.Equal(0, s.AbsentSeconds, 1);
        Assert.Equal(0, s.GapSeconds, 1);
    }

    [Fact]
    public void 每一格都是满格_盘面全绿没有短板()
    {
        var (win, afk) = Synthetic(5);
        var task = Task();
        var cells = Replay.ToMinuteCells(task, Replay.Run(task, NoRules, win, afk, T0.AddMinutes(5)));

        Assert.Equal(5, cells.Count);
        Assert.All(cells, c => Assert.Equal(1.0, c.Purity, 3));
        Assert.All(cells, c => Assert.Equal(0, c.OffTaskSeconds, 1));
    }

    [Fact]
    public void 截止线永远不往前滑()
    {
        var task = Task();
        foreach (var m in new[] { 1, 5, 12, 24 })
        {
            var (win, afk) = Synthetic(m);
            var s = Replay.Run(task, NoRules, win, afk, T0.AddMinutes(m));
            Assert.Equal(T0.AddMinutes(task.FocusMinutes), Replay.ProjectedEnd(task, s));
        }
    }

    [Fact]
    public void 走满承诺时长就达成()
    {
        var task = Task(10);
        var (win, afk) = Synthetic(10);
        var s = Replay.Run(task, NoRules, win, afk, T0.AddMinutes(10));

        Assert.Equal(T0.AddMinutes(10), s.FocusCompletedAt);
    }

    /// <summary>
    /// 这一条直接指着依赖本身：自身豁免必须判 <c>Neutral</c>，而 <c>Neutral</c> 必须计入。
    /// 两者任一被改动，番茄钟模式就静默失效。
    /// </summary>
    [Fact]
    public void 承诺的依赖_自身豁免判Neutral且Neutral计入()
    {
        var (win, afk) = Synthetic(3);
        var s = Replay.Run(Task(), NoRules, win, afk, T0.AddMinutes(3));

        Assert.All(s.Intervals, iv => Assert.Equal(IntervalKind.Neutral, iv.Kind));
        Assert.Equal(180, s.FocusedSeconds, 1);
    }
}
