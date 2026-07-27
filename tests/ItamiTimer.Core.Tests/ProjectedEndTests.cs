using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// <see cref="Replay.ProjectedEnd"/>：偷懒多久，截止线就往前滑多久（向上取整到整分钟）。
/// 用户 2026-07-27 给的模型，下面第一条就是他给的那个算例。
/// </summary>
public class ProjectedEndTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 27, 23, 13, 0, TimeSpan.FromHours(8));

    private static TaskRecord Task(int focus = 10) => new()
    {
        StartedAt = T0,
        FocusMinutes = focus,
        Groups = [],
    };

    private static TaskState State(double nowMinutes, double focusedMinutes,
                                   DateTimeOffset? completed = null) => new()
    {
        Now = T0.AddMinutes(nowMinutes),
        FocusedSeconds = focusedMinutes * 60,
        FocusCompletedAt = completed,
        Phase = completed is null ? TaskPhase.Focusing : TaskPhase.Resting,
        Intervals = [],
        Violations = [],
        OffTaskSecondsByApp = new Dictionary<string, double>(),
        AbsentSeconds = 0,
        GapSeconds = 0,
    };

    [Fact]
    public void 用户给的算例_第一个计时点只学到半分钟()
    {
        // 23:13:00 起算、承诺 10 分钟；到 23:14:00 只学到 0.5 分钟
        // → 缺 0.5 分钟 → 向上取整 1 分钟 → 23:13 + 10:00 + 1:00 = 23:24:00
        var end = Replay.ProjectedEnd(Task(), State(nowMinutes: 1, focusedMinutes: 0.5));
        Assert.Equal(T0.AddMinutes(11), end);
        Assert.Equal(new TimeSpan(23, 24, 0), end.TimeOfDay);
    }

    [Fact]
    public void 刚点开始_截止线就是承诺时长()
    {
        Assert.Equal(T0.AddMinutes(10), Replay.ProjectedEnd(Task(), State(0, 0)));
    }

    [Fact]
    public void 一直全神贯注_截止线纹丝不动()
    {
        // 每一分钟都足额，缺口恒为 0
        for (var m = 1; m <= 9; m++)
            Assert.Equal(T0.AddMinutes(10), Replay.ProjectedEnd(Task(), State(m, m)));
    }

    [Fact]
    public void 整分钟的欠账不会被取整放大()
    {
        // 缺口正好 2 分钟 → 补 2 分钟，不该变成 3
        Assert.Equal(T0.AddMinutes(12), Replay.ProjectedEnd(Task(), State(5, 3)));
    }

    [Fact]
    public void 零头一律算成一整分钟()
    {
        // 只差 1 秒也补满一分钟：整条时间线都落在整分钟上（§14.1）
        var end = Replay.ProjectedEnd(Task(), State(5, 5 - 1 / 60.0));
        Assert.Equal(T0.AddMinutes(11), end);
    }

    [Fact]
    public void 截止线只会往后滑不会往回缩()
    {
        // 第 3 分钟摸鱼 2 分钟 → 补 2；第 4 分钟专心 → 缺口不变，截止线原地不动
        var a = Replay.ProjectedEnd(Task(), State(3, 1));
        var b = Replay.ProjectedEnd(Task(), State(4, 2));
        Assert.Equal(T0.AddMinutes(12), a);
        Assert.Equal(b, a);
    }

    [Fact]
    public void 达成之后就钉在达成时刻()
    {
        var done = T0.AddMinutes(13);
        Assert.Equal(done, Replay.ProjectedEnd(Task(), State(14, 10, completed: done)));
    }

    [Fact]
    public void 实际时长超过应该时长时不会把截止线拉早()
    {
        // 起点截断到整分钟，会把点击【之前】最多 59 秒也算进来，
        // 于是 FocusedSeconds 可能短暂地超过 Now-StartedAt。缺口钳到 0，不能变负数。
        Assert.Equal(T0.AddMinutes(10), Replay.ProjectedEnd(Task(), State(1, 1.5)));
    }
}
