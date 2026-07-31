using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 2026-07-30 对外发布前的边界补测：把散落在注释和 DECISIONS.md 里的行为约定
/// 逐条钉进测试。每条测试名直接陈述被钉住的行为。
/// </summary>
public class BoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 27, 10, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double min) => T0.AddMinutes(min);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        {
          "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } },
          "ignore": [ "^explorer\\.exe$" ]
        }
        """);

    private static TaskRecord Task(int focusMinutes = 25) => new()
    {
        StartedAt = T0,
        FocusMinutes = focusMinutes,
        Group = "学习经济学",
    };

    private static AwEvent Win(double fromMin, double toMin, string app, string title)
        => new(At(fromMin), (toMin - fromMin) * 60, app, title, null);

    private static AwEvent Afk(double fromMin, double toMin, string status)
        => new(At(fromMin), (toMin - fromMin) * 60, null, null, status);

    // ---------------------------------------------------------------- Bridge（T4）的精确边界

    [Fact]
    public void 空隙恰好五秒_桥接()
    {
        var events = new List<AwEvent>
        {
            new(At(0), 60, "读书.exe", "经济学", null),
            new(At(1).AddSeconds(5), 55, "读书.exe", "经济学", null),
        };
        var bridged = Replay.Bridge(events);
        Assert.Equal(events[1].Start, bridged[0].End);
    }

    [Fact]
    public void 空隙超过五秒_不桥接_留给Gap检测()
    {
        var events = new List<AwEvent>
        {
            new(At(0), 60, "读书.exe", "经济学", null),
            new(At(1).AddSeconds(5.5), 54.5, "读书.exe", "经济学", null),
        };
        var bridged = Replay.Bridge(events);
        Assert.Equal(events[0].End, bridged[0].End);   // 原样，没被延伸
    }

    [Fact]
    public void 事件重叠时不会把终点往回缩()
    {
        // hole < 0（重叠）不该触发桥接——只有正向空隙才延伸
        var events = new List<AwEvent>
        {
            new(At(0), 90, "读书.exe", "经济学", null),        // 到 1.5 分钟
            new(At(1), 60, "读书.exe", "经济学", null),
        };
        var bridged = Replay.Bridge(events);
        Assert.Equal(events[0].End, bridged[0].End);
    }

    // ---------------------------------------------------------------- PendingPresent（T6）的精确边界

    [Fact]
    public void 安静恰好一百八十秒_暂定期结束_不再算在座()
    {
        // not-afk 到 At(1) 为止，之后空洞。暂定窗口是 [End, End+180)，严格小于。
        var afk = new List<AwEvent> { Afk(0, 1, "not-afk") };
        Assert.True(Replay.PendingPresent(afk, At(1).AddSeconds(179.9)));
        Assert.False(Replay.PendingPresent(afk, At(1).AddSeconds(180)));
    }

    [Fact]
    public void 最后一条是afk_空洞不算暂定在座()
    {
        var afk = new List<AwEvent> { Afk(0, 1, "afk") };
        Assert.False(Replay.PendingPresent(afk, At(1).AddSeconds(10)));
    }

    // ---------------------------------------------------------------- ProjectedEnd 的已知紧张（DECISIONS A7）

    [Fact]
    public void Gap也会推截止线_这是记录在案的字面实现_改之前先看DECISIONS_A7()
    {
        // 前 1 分钟有数据且专注，后 1 分钟整段 Gap。
        // 按「应该 − 实际」的字面公式，Gap 那一分钟也算欠账。
        var task = Task(10);
        var win = new List<AwEvent> { Win(0, 1, "读书.exe", "经济学") };
        var afk = new List<AwEvent> { Afk(0, 1, "not-afk") };
        var st = Replay.Run(task, Rules, win, afk, At(2));

        Assert.Equal(60, st.GapSeconds, 1);
        // 截止线被 Gap 推后一分钟：T0 + 10 + 1
        Assert.Equal(At(11), Replay.ProjectedEnd(task, st));
    }

    [Fact]
    public void 点击前白送的时间不会把截止线拉到承诺之前()
    {
        // startedAt 截断（§14.1）会把点击前的活动算进来，FocusedSeconds 可能
        // 超过 now − startedAt。缺口必须钳到 0，截止线不得早于 T0 + 承诺。
        var task = Task(10);
        var win = new List<AwEvent> { Win(0, 2, "读书.exe", "经济学") };
        var afk = new List<AwEvent> { Afk(0, 2, "not-afk") };
        var st = Replay.Run(task, Rules, win, afk, At(1));   // now 在事件末端之前

        Assert.Equal(At(10), Replay.ProjectedEnd(task, st));
    }

    // ---------------------------------------------------------------- 达成插值的边界

    [Fact]
    public void 恰好在区间边界上攒够_达成时刻等于那条边界()
    {
        var task = Task(2);
        var win = new List<AwEvent> { Win(0, 2, "读书.exe", "经济学"), Win(2, 5, "novel.exe", "小说") };
        var afk = new List<AwEvent> { Afk(0, 5, "not-afk") };
        var st = Replay.Run(task, Rules, win, afk, At(5));

        Assert.Equal(At(2), st.FocusCompletedAt);
        Assert.Equal(2 * 60, st.FocusedSeconds, 1);
    }

    [Fact]
    public void 达成后立刻放弃去摸鱼_违规不再累计()
    {
        var task = Task(2);
        var win = new List<AwEvent> { Win(0, 2, "读书.exe", "经济学"), Win(2, 10, "novel.exe", "小说") };
        var afk = new List<AwEvent> { Afk(0, 10, "not-afk") };
        var st = Replay.Run(task, Rules, win, afk, At(10));

        // 休息期间干什么都不重要（§6）：8 分钟小说不算违规
        Assert.Empty(st.Violations);
        Assert.Equal(0, st.OffTaskSecondsByApp.Values.Sum(), 1);
    }

    // ---------------------------------------------------------------- MinuteCell 的除零与纯度

    [Fact]
    public void 空格子的纯度是零不是NaN()
    {
        var cell = new MinuteCell(0, T0, 0, 0, 0, 0);
        Assert.Equal(0, cell.Purity);
    }

    [Fact]
    public void 末格短一截时纯度按实际长度算_不被六十秒分母冤枉()
    {
        var task = Task(2) with { FocusMinutes = 2 };
        // 2.5 分钟处才攒够 2 分钟（中间 0.5 分钟摸鱼）
        var win = new List<AwEvent>
        {
            Win(0, 1.5, "读书.exe", "经济学"),
            Win(1.5, 2, "novel.exe", "小说"),
            Win(2, 3, "读书.exe", "经济学"),
        };
        var afk = new List<AwEvent> { Afk(0, 3, "not-afk") };
        var st = Replay.Run(task, Rules, win, afk, At(3));
        var cells = Replay.ToMinuteCells(task, st);

        // 达成在 2.5 分钟：末格 [2.0, 2.5) 只有 30 秒，但纯度是 1.0
        Assert.Equal(At(2.5), st.FocusCompletedAt);
        var last = cells[^1];
        Assert.Equal(30, last.TotalSeconds, 1);
        Assert.Equal(1.0, last.Purity, 2);
    }


    // ---------------------------------------------------------------- Boundaries 的 T6 边界注入

    [Fact]
    public void 每条notAfk的终点加一百八十秒都是边界()
    {
        var afk = new List<AwEvent> { Afk(0, 1, "not-afk") };
        var bounds = Replay.Boundaries([], afk, T0, At(10));
        Assert.Contains(At(1).AddSeconds(180), bounds);
    }

    [Fact]
    public void 暂定期终点落在区间外就不注入()
    {
        var afk = new List<AwEvent> { Afk(0, 9, "not-afk") };
        var bounds = Replay.Boundaries([], afk, T0, At(10));
        // 9 分钟 + 180 秒 = 12 分钟 > until，不该出现
        Assert.DoesNotContain(At(9).AddSeconds(180), bounds);
    }

    // ---------------------------------------------------------------- Clip 的半开区间

    [Fact]
    public void 恰好贴着区间边缘的事件裁出来是null不是零长区间()
    {
        var e = new AwEvent(At(-1), 60, "x", "y", null);   // 正好结束于 since
        Assert.Null(Replay.Clip(e, T0, At(10)));

        var e2 = new AwEvent(At(10), 60, "x", "y", null);  // 正好开始于 until
        Assert.Null(Replay.Clip(e2, T0, At(10)));
    }
}
