using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// §7 重放算法的测试。全部喂**合成事件**——重放是纯函数、now 是参数，
/// 所以测"专注 25 分钟走完"不用真等 25 分钟（§7 的可测性收益）。
/// </summary>
public class ReplayTests
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
        Groups = ["学习经济学"],
    };

    private static AwEvent Win(double fromMin, double toMin, string app, string title)
        => new(At(fromMin), (toMin - fromMin) * 60, app, title, null);

    private static AwEvent Afk(double fromMin, double toMin, string status)
        => new(At(fromMin), (toMin - fromMin) * 60, null, null, status);

    /// <summary>整段都在座的 afk 覆盖，省得每个用例都写一遍。</summary>
    private static List<AwEvent> Present(double toMin) => [Afk(0, toMin, "not-afk")];

    // ---------------------------------------------------------------- 基本

    [Fact]
    public void 全程读经济学_全部计入()
    {
        var s = Replay.Run(Task(), Rules,
            [Win(0, 10, "SumatraPDF.exe", "曼昆经济学.pdf")], Present(10), At(10));

        Assert.Equal(600, s.FocusedSeconds, 1);
        Assert.Equal(TaskPhase.Focusing, s.Phase);
        Assert.Empty(s.Violations);
    }

    [Fact]
    public void 中间跑去看小说_那段不计入且算一次偷懒()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 4, "SumatraPDF.exe", "曼昆经济学.pdf"),
            Win(4, 6, "chrome.exe", "斗破苍穹 - 起点中文网"),
            Win(6, 10, "SumatraPDF.exe", "曼昆经济学.pdf"),
        ], Present(10), At(10));

        Assert.Equal(8 * 60, s.FocusedSeconds, 1);          // 10 分钟里只有 8 分钟算数
        Assert.Single(s.Violations);
        Assert.Equal(120, s.OffTaskSecondsByApp["chrome.exe"], 1);
    }

    [Fact]
    public void 此刻在偷懒时状态是Slacking_这是要弹窗的触发条件()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 4, "SumatraPDF.exe", "经济学.pdf"),
            Win(4, 5, "Weixin.exe", "微信"),
        ], Present(5), At(5));

        Assert.Equal(TaskPhase.Slacking, s.Phase);
    }

    // ---------------------------------------------------------------- Absent 的优先级（§4）

    /// <summary>
    /// §13 第 5 条，最容易漏测、后果最严重的一条：把目标应用停在前台然后起身走开。
    /// **光看窗口事件测不出来**——窗口事件的 duration 会一路心跳长大，看起来像一直在专注。
    /// </summary>
    [Fact]
    public void 停在目标应用上走开_那段不能计入()
    {
        var s = Replay.Run(Task(), Rules,
            // 窗口事件一路长到 10 分钟，看起来完美
            [Win(0, 10, "SumatraPDF.exe", "曼昆经济学.pdf")],
            // 但 afk 说第 3 分钟起人就不在了
            [Afk(0, 3, "not-afk"), Afk(3, 10, "afk")],
            At(10));

        Assert.Equal(3 * 60, s.FocusedSeconds, 1);
        Assert.Equal(7 * 60, s.AbsentSeconds, 1);
        Assert.Equal(TaskPhase.Away, s.Phase);
        Assert.Empty(s.Violations);          // 人不在不算偷懒，也不提醒
    }

    /// <summary>
    /// §4：Absent 优先级高于一切。锁屏时 LockApp.exe 若在 ignore 名单里本该 Neutral
    /// （计入），但 afk 同时说 afk —— 必须判 Absent。搞错就是"锁屏一小时专注时长照涨"。
    /// </summary>
    [Fact]
    public void 锁屏不白涨时长_Absent压过Neutral()
    {
        var rules = GroupRules.Parse("""
            {
              "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } },
              "ignore": [ "^LockApp\\.exe$" ]
            }
            """);
        var s = Replay.Run(Task(), rules,
            [Win(0, 5, "LockApp.exe", "")],
            [Afk(0, 5, "afk")],
            At(5));

        Assert.Equal(0, s.FocusedSeconds, 1);
        Assert.Equal(5 * 60, s.AbsentSeconds, 1);
    }

    // ---------------------------------------------------------------- Gap（§6.3）

    [Fact]
    public void afk缺数据算Gap_绝不当成在座()
    {
        var s = Replay.Run(Task(), Rules,
            [Win(0, 10, "SumatraPDF.exe", "经济学.pdf")],
            [Afk(0, 4, "not-afk")],           // 第 4 分钟之后没有 afk 数据了
            At(10));

        Assert.Equal(4 * 60, s.FocusedSeconds, 1);
        Assert.Equal(6 * 60, s.GapSeconds, 1);
        Assert.Empty(s.Violations);           // Gap 既不计入也不惩罚
    }

    // ---------------------------------------------------------------- 达成与休息（§3、§8.4）

    [Fact]
    public void 达成时刻由插值算出_精确到秒()
    {
        // 承诺 5 分钟；前 2 分钟算数、中间 3 分钟偷懒、之后继续
        var s = Replay.Run(Task(5), Rules, [
            Win(0, 2, "SumatraPDF.exe", "经济学.pdf"),
            Win(2, 5, "Weixin.exe", "微信"),
            Win(5, 12, "SumatraPDF.exe", "经济学.pdf"),
        ], Present(12), At(12));

        // 累计到 5 分钟需要再读 3 分钟 → 第 5 分钟起再走 3 分钟 = 第 8 分钟
        Assert.Equal(At(8), s.FocusCompletedAt);
        Assert.Equal(5 * 60, s.FocusedSeconds, 1);
    }

    [Fact]
    public void 偷懒会把截止线往后推_这就是痛感的来源()
    {
        var clean = Replay.Run(Task(5), Rules,
            [Win(0, 12, "SumatraPDF.exe", "经济学.pdf")], Present(12), At(12));
        var lazy = Replay.Run(Task(5), Rules, [
            Win(0, 2, "SumatraPDF.exe", "经济学.pdf"),
            Win(2, 5, "chrome.exe", "微博"),
            Win(5, 12, "SumatraPDF.exe", "经济学.pdf"),
        ], Present(12), At(12));

        Assert.Equal(At(5), clean.FocusCompletedAt);   // 不偷懒：5 分钟就完
        Assert.Equal(At(8), lazy.FocusCompletedAt);    // 偷懒 3 分钟：拖到第 8 分钟
    }

    [Fact]
    public void 休息时长是专注的五分之一_休息中和休息后的阶段要分清()
    {
        var task = Task(5);   // 休息 1 分钟
        List<AwEvent> win = [Win(0, 20, "SumatraPDF.exe", "经济学.pdf")];

        // 第 5 分钟达成，休息到第 6 分钟
        Assert.Equal(TaskPhase.Resting, Replay.Run(task, Rules, win, Present(20), At(5.5)).Phase);
        Assert.Equal(TaskPhase.Completed, Replay.Run(task, Rules, win, Present(20), At(6.5)).Phase);
    }

    [Fact]
    public void 达成之后的时间不再计入专注_休息期间干什么都不重要()
    {
        var s = Replay.Run(Task(5), Rules,
            [Win(0, 30, "SumatraPDF.exe", "经济学.pdf")], Present(30), At(30));

        Assert.Equal(5 * 60, s.FocusedSeconds, 1);   // 不会涨到 30 分钟
    }

    // ---------------------------------------------------------------- §14.1 整分钟网格

    [Fact]
    public void 每一格都是完整的六十秒()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 3, "SumatraPDF.exe", "经济学.pdf"),
            Win(3, 5, "chrome.exe", "微博"),
        ], Present(5), At(5));
        var cells = Replay.ToMinuteCells(Task(), s);

        Assert.Equal(5, cells.Count);
        Assert.All(cells, c => Assert.Equal(60, c.TotalSeconds, 1));
        Assert.All(cells.Take(3), c => Assert.Equal(1.0, c.Purity, 3));   // 前 3 格全绿
        Assert.All(cells.Skip(3), c => Assert.Equal(0.0, c.Purity, 3));   // 后 2 格全红
    }

    [Fact]
    public void 半分钟处切换会得到一个半绿半红的格子()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 2.5, "SumatraPDF.exe", "经济学.pdf"),
            Win(2.5, 4, "chrome.exe", "微博"),
        ], Present(4), At(4));
        var cells = Replay.ToMinuteCells(Task(), s);

        Assert.Equal(0.5, cells[2].Purity, 3);
    }

    [Fact]
    public void 正在走的那一分钟不出格子_否则会闪()
    {
        var s = Replay.Run(Task(), Rules,
            [Win(0, 3.7, "SumatraPDF.exe", "经济学.pdf")], Present(3.7), At(3.7));

        Assert.Equal(3, Replay.ToMinuteCells(Task(), s).Count);   // 不是 4
    }

    [Fact]
    public void 任务开始前状态是NotStarted_那是进位留出的缓冲()
    {
        var task = Task() with { StartedAt = At(1) };
        var s = Replay.Run(task, Rules, [], [], At(0.5));

        Assert.Equal(TaskPhase.NotStarted, s.Phase);
    }

    // ---------------------------------------------------------------- §8.4.5a 色环是当前任务的投影

    /// <summary>
    /// 用户 2026-07-27：新开任务色环清除是必做的。这不是一个功能，是一条不能写错的
    /// 不变量——色环 = 当前任务重放结果的投影。只要 ToMinuteCells 保持纯函数、不藏
    /// 跨任务缓存，新任务就自动是空盘。这条测试守着"不藏缓存"。
    /// </summary>
    [Fact]
    public void 新任务的色环必须是空的_哪怕历史里全是上一轮的事件()
    {
        // 事件列表来自"上一轮"：整段都在读经济学
        List<AwEvent> oldEvents = [Win(0, 30, "SumatraPDF.exe", "经济学.pdf")];

        // 新任务从第 30 分钟才开始，此前的事件一格都不该带进来
        var fresh = Task() with { StartedAt = At(30) };
        var s = Replay.Run(fresh, Rules, oldEvents, Present(30), At(30));

        Assert.Empty(Replay.ToMinuteCells(fresh, s));
        Assert.Equal(0, s.FocusedSeconds, 1);
        Assert.Null(s.FocusCompletedAt);
    }

    // ---------------------------------------------------------------- T4 零时长事件

    /// <summary>
    /// 2026-07-27 在真实数据上发现：窗口标题每秒都变时（Claude Code 在终端里的
    /// 转圈动画），AW 会每秒产生一条 duration=0 的事件。不桥接的话整段变 Gap。
    /// </summary>
    [Fact]
    public void 每秒一条的零时长事件不能被误判成无数据()
    {
        // 模拟转圈动画：第 0~2 分钟每 5 秒一条零时长事件，标题都含经济学
        var win = new List<AwEvent>();
        for (var s = 0; s < 120; s += 5)
            win.Add(new AwEvent(T0.AddSeconds(s), 0, "SumatraPDF.exe", $"曼昆经济学 {s}", null));
        win.Add(Win(2, 3, "SumatraPDF.exe", "曼昆经济学.pdf"));

        var s2 = Replay.Run(Task(), Rules, win, Present(3), At(3));

        Assert.Equal(0, s2.GapSeconds, 1);
        Assert.Equal(180, s2.FocusedSeconds, 1);
    }

    /// <summary>
    /// 桥接必须有上限。无上限地把事件延伸到下一条，会把 aw-server 宕掉造成的
    /// 真实窟窿一起填平——那就把 §6.3 的 Gap 检测彻底毁了。
    /// </summary>
    [Fact]
    public void 真实的大窟窿不能被桥接填平()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 1, "SumatraPDF.exe", "经济学.pdf"),
            Win(5, 6, "SumatraPDF.exe", "经济学.pdf"),   // 中间 4 分钟没有任何数据
        ], Present(6), At(6));

        Assert.Equal(4 * 60, s.GapSeconds, 1);
        Assert.Equal(2 * 60, s.FocusedSeconds, 1);
    }

    /// <summary>
    /// 微秒精度的事件必须严格首尾相接，否则重放会切出亚毫秒碎片，把一段连续的
    /// 偷懒拆成很多段。AW 的时间戳就是微秒精度，而 AddSeconds(double) 会四舍五入
    /// 到毫秒——这条测试守着那个坑。
    /// </summary>
    [Fact]
    public void 微秒精度的连续偷懒只算一次而不是碎成很多次()
    {
        // 每条 1.0000005 秒（带微秒零头），首尾严格相接，共 100 条
        var win = new List<AwEvent>();
        var t = T0;
        for (var i = 0; i < 100; i++)
        {
            var e = new AwEvent(t, 1.0000005, "chrome.exe", $"微博 {i}", null);
            win.Add(e);
            t = e.End;
        }
        var s = Replay.Run(Task(), Rules, win, Present(5), t);

        Assert.Single(s.Violations);
        Assert.Equal(0, s.GapSeconds, 3);
    }

    // ---------------------------------------------------------------- §5.4 并集追溯生效

    /// <summary>
    /// §13 第 4 条（2026-07-27 期望反转）：中途补勾一个小目标，**之前**那段时间
    /// 也会追溯变绿。这与 v3 初稿的期望相反，是用户明确要求的（§5.4.1）。
    /// </summary>
    [Fact]
    public void 补勾小目标之后_之前那段也追溯变绿()
    {
        List<AwEvent> win = [
            Win(0, 5, "SumatraPDF.exe", "经济学.pdf"),
            Win(5, 10, "blender.exe", "Blender 入门"),
        ];
        var rules = GroupRules.Parse("""
            {
              "groups": {
                "学习经济学": { "rules": [ { "title": "经济学" } ] },
                "学习Blender": { "rules": [ { "app": "^blender\\.exe$" } ] }
              }
            }
            """);

        var before = Replay.Run(Task() with { Groups = ["学习经济学"] },
            rules, win, Present(10), At(10));
        var after = Replay.Run(Task() with { Groups = ["学习经济学", "学习Blender"] },
            rules, win, Present(10), At(10));

        Assert.Equal(5 * 60, before.FocusedSeconds, 1);    // Blender 那 5 分钟算偷懒
        Assert.Equal(10 * 60, after.FocusedSeconds, 1);    // 补勾之后追溯全绿
        Assert.Empty(after.Violations);
    }
}
