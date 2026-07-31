using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 用户 2026-07-28 提的那个担心：点击开始的瞬间正好是 23:59:00.999，界面画完已经
/// 跨过了第一个计时点 00:00:00 —— 那一格是不是就丢了？
///
/// **丢不了。** 计时点不是"必须被打卡的时刻"，只是"抬头看一眼的时刻"；账在 AW 里，
/// 什么时候看都是同一本。下面几条把这个结论钉死。
///
/// 时间轴按用户的原话摆：起算 T0，承诺 5 分钟。
/// </summary>
public class CheckpointCatchUpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 27, 23, 59, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double min) => T0.AddMinutes(min);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        {
          "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } },
          "ignore": []
        }
        """);

    private static TaskRecord Task() => new()
    {
        StartedAt = T0,
        FocusMinutes = 5,
        Group = "学习经济学",
    };

    private static AwEvent Win(double a, double b, string app, string title)
        => new(At(a), (b - a) * 60, app, title, null);

    private static AwEvent Afk(double a, double b, string s) => new(At(a), (b - a) * 60, null, null, s);

    /// <summary>
    /// 第一分钟是点击【之前】的活动（用户在终端里看测试计划 → 偷懒），
    /// 第二分钟起老实读经济学。
    /// </summary>
    private static List<AwEvent> Windows() =>
    [
        Win(0, 1, "WindowsTerminal.exe", "Windows PowerShell"),
        Win(1, 3, "SumatraPDF.exe", "曼昆经济学原理第五版.pdf"),
    ];

    [Fact]
    public void 漏掉第一个计时点_下一次一次补出两格_内容跟分别查两次完全一样()
    {
        var task = Task();

        // (a) 老老实实在 00:00:00 和 00:01:00 各查一次
        var atFirst = Replay.ToMinuteCells(task, Replay.Run(task, Rules, Windows(), [Afk(0, 2, "not-afk")], At(1)));
        var atSecond = Replay.ToMinuteCells(task, Replay.Run(task, Rules, Windows(), [Afk(0, 2, "not-afk")], At(2)));

        // (b) 界面画得慢，00:00:00 整个错过，直到 00:01:00 才第一次查
        var caughtUp = atSecond;

        Assert.Single(atFirst);                 // 第一次只该有一格
        Assert.Equal(2, caughtUp.Count);        // 补的时候一次吐两格
        // 补出来的第 0 格必须跟当初那一格逐字段相同 —— 否则"补"就是在改历史
        Assert.Equal(atFirst[0], caughtUp[0]);
    }

    [Fact]
    public void 补出来的第一格是偷懒_截止线往后推一分钟()
    {
        var task = Task();
        var s = Replay.Run(task, Rules, Windows(), [Afk(0, 2, "not-afk")], At(1));

        // 到 T0+1 只走完第 0 分钟，而那一分钟整个是点击【之前】的终端 → 一秒都没学到
        Assert.Equal(0, s.FocusedSeconds, 1);
        // 已过去 1 分钟、学到 0 分钟 → 缺 1 分钟 → 预计结束 = T0 + 5 + 1
        Assert.Equal(At(6), Replay.ProjectedEnd(task, s));
    }

    [Fact]
    public void 用户给的算例_第三个计时点欠75秒_补两分钟()
    {
        var task = Task();

        // 到 00:02:00：已过去 3 分钟，其中偷懒 75 秒 → 学到 105 秒
        var win = new List<AwEvent>
        {
            Win(0, 1.25, "WindowsTerminal.exe", "Windows PowerShell"),  // 75 秒偷懒
            Win(1.25, 3, "SumatraPDF.exe", "曼昆经济学原理第五版.pdf"),
        };
        var s = Replay.Run(task, Rules, win, [Afk(0, 3, "not-afk")], At(3));

        Assert.Equal(105, s.FocusedSeconds, 1);
        // 缺 75 秒 → 向上取整 2 分钟 → T0 + 05:00 + 02:00
        Assert.Equal(At(7), Replay.ProjectedEnd(task, s));
    }

    [Fact]
    public void 偷懒是累计值不是增量_补时不会被重复叠加()
    {
        var task = Task();
        var win = new List<AwEvent>
        {
            Win(0, 1, "WindowsTerminal.exe", "Windows PowerShell"),   // 只在第一分钟偷懒
            Win(1, 5, "SumatraPDF.exe", "曼昆经济学原理第五版.pdf"),
        };
        var afk = new List<AwEvent> { Afk(0, 5, "not-afk") };

        // 此后每一分钟都老实学：欠账停在 1 分钟不动，截止线也就钉在 T0+6 不再往后滑。
        // 要是写成"每拍把本分钟的缺口累加上去"，这里会一路滑到 T0+9。
        foreach (var m in new[] { 1, 2, 3, 4, 5 })
        {
            var s = Replay.Run(task, Rules, win, afk, At(m));
            Assert.Equal(At(6), Replay.ProjectedEnd(task, s));
        }
    }

    [Fact]
    public void 一次都没查过_直接在末尾查一次_结果照样完整()
    {
        // 极端情况：整轮任务期间界面一次都没查成（比如 AW 一直连不上），
        // 最后恢复了查一次 —— 因为每轮都重查 [startedAt, now] 整段（§7.2 不做缓存），
        // 历史在 aw-server 里一条不少。
        var task = Task();
        var win = new List<AwEvent>
        {
            Win(0, 1, "WindowsTerminal.exe", "Windows PowerShell"),
            Win(1, 6, "SumatraPDF.exe", "曼昆经济学原理第五版.pdf"),
        };
        var s = Replay.Run(task, Rules, win, [Afk(0, 6, "not-afk")], At(6));

        Assert.Equal(At(6), s.FocusCompletedAt);           // 5 分钟专注在 T0+6 攒够
        Assert.Equal(6, Replay.ToMinuteCells(task, s).Count);
    }
}
