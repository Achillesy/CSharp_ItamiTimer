using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 回填的判定规则（§11.2 / DECISIONS I2）。全是纯函数：喂合成事件，不等待，不碰
/// ActivityWatch。
///
/// **这一组测试守的是一条和运行期相反的规则**，所以每一条都值得单独立着：运行期
/// 「查了但 AW 没数据」判 <see cref="JudgmentCode.AwOffline"/> 并**计入专注**（H2 的知情
/// fail-open），而回填必须 **fail-closed**——程序当时没在跑，AW 没数据最大的可能是机器
/// 关着，照 fail-open 记账一次跨周末的回填能凭空记 48 小时。
///
/// 每条不变量都在文档里有出处——**测试名就是那条不变量**。
/// </summary>
public class BackfillTests
{
    private static readonly GroupRules Rules = GroupRules.Parse(
        """{ "groups": { "Economics": { "rules": [ { "app": "^econ$" } ] } } }""");

    private const string Goal = "Economics";
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 6, 9, 0, 0, TimeSpan.FromHours(8));

    private static AwEvent Win(DateTimeOffset from, double seconds, string app)
        => new(from, seconds, app, $"{app} window", null);

    private static AwEvent Afk(DateTimeOffset from, double seconds)
        => new(from, seconds, null, null, "afk");

    private static long Count(int seconds, List<AwEvent> win, List<AwEvent> afk)
        => Backfill.CountSpan(T0, seconds, win, afk, Rules, Goal);

    // ---------------------------------------------------------------- fail-closed

    /// <summary>
    /// 整段没有任何 AW 记录 → **0 秒**。
    ///
    /// 这条和 <c>JudgmentBufferTests</c> 里「连不上 = 空事件列表 → 计入专注」正好相反，
    /// 而两者都对：区别不在数据，在**当时程序有没有在跑**。
    /// </summary>
    [Fact]
    public void NoRecordsAtAllCountAsZero_NotAsFocus_TheOppositeOfTheLiveFailOpen()
    {
        Assert.Equal(0, Count(3600, [], []));
    }

    /// <summary>关机一整夜（AW 两个 bucket 都没有事件）绝不能凭空记成 8 小时专注。</summary>
    [Fact]
    public void AWholeNightWithTheMachineOffCreditsNothing()
    {
        Assert.Equal(0, Count(8 * 3600, [], []));
    }

    /// <summary>只有真的命中规则的窗口事件才算数。</summary>
    [Fact]
    public void OnlyWindowEventsMatchingTheGoalAreCounted()
    {
        var win = new List<AwEvent> { Win(T0, 600, "econ") };
        Assert.Equal(600, Count(3600, win, []));
    }

    /// <summary>不命中的窗口事件不计入——它证明的是「人在，但没在做这件事」。</summary>
    [Fact]
    public void WindowEventsThatDoNotMatchCreditNothing()
    {
        var win = new List<AwEvent> { Win(T0, 600, "chrome") };
        Assert.Equal(0, Count(3600, win, []));
    }

    /// <summary>
    /// afk 盖在最上层：人不在，窗口摆着什么都不算。
    ///
    /// **回填里这一层比运行期更不能少**：AW 的窗口 watcher 会在人离开时持续拉长当前窗口的
    /// <c>duration</c>（AWJ 的 Note T2），没有 afk 盖顶，一个长驻的匹配窗口能记一整夜。
    /// </summary>
    [Fact]
    public void AfkOverridesAMatchingWindowEvent_OtherwiseALingeringWindowWouldCreditTheWholeNight()
    {
        var win = new List<AwEvent> { Win(T0, 3600, "econ") };   // 窗口一直挂着
        var afk = new List<AwEvent> { Afk(T0.AddSeconds(600), 3000) };   // 但人 10 分钟后就走了

        Assert.Equal(600, Count(3600, win, afk));
    }

    // ---------------------------------------------------------------- 分块

    /// <summary>
    /// 跨切片边界的事件不会被数两遍：切成两片分别数，和一次数完是同一个数。
    ///
    /// 每片的 span 都是 <c>[from, to)</c> 半开区间，同一秒只可能落在一片里；
    /// <c>Judgment.PaintOne</c> 会把越界部分裁掉，右邻那片再画同一个事件时 <c>to &lt;= from</c>
    /// 直接返回。
    /// </summary>
    [Fact]
    public void AnEventSpanningAChunkBoundaryIsNotCountedTwice()
    {
        var win = new List<AwEvent> { Win(T0.AddSeconds(30), 60, "econ") };   // 30..90 秒，跨 60 这条线

        var whole = Backfill.CountSpan(T0, 120, win, [], Rules, Goal);
        var first = Backfill.CountSpan(T0, 60, win, [], Rules, Goal);
        var second = Backfill.CountSpan(T0.AddSeconds(60), 60, win, [], Rules, Goal);

        Assert.Equal(60, whole);
        Assert.Equal(whole, first + second);
    }

    /// <summary>复用的 scratch 数组必须每片清零，否则上一片的判定会漏进下一片。</summary>
    [Fact]
    public void AReusedScratchBufferIsClearedBetweenChunks()
    {
        var scratch = new JudgmentCode[120];
        var busy = new List<AwEvent> { Win(T0, 60, "econ") };

        Assert.Equal(60, Backfill.CountSpan(T0, 60, busy, [], Rules, Goal, scratch));
        // 同一块 scratch 再用一次，这回没有任何事件 —— 必须是 0，不能留着上一片的绿
        Assert.Equal(0, Backfill.CountSpan(T0.AddSeconds(60), 60, [], [], Rules, Goal, scratch));
    }

    /// <summary>空区间不炸也不记账。</summary>
    [Fact]
    public void AnEmptySpanCountsAsZero()
    {
        Assert.Equal(0, Count(0, [Win(T0, 600, "econ")], []));
        Assert.Equal(0, Count(-1, [Win(T0, 600, "econ")], []));
    }

    // ---------------------------------------------------------------- 和运行期的关系

    /// <summary>
    /// **同一段时间，回填数出来的绝不会比运行期多**——这正是「下次启动后数字可能变小」
    /// 的来源（DECISIONS I2），也是这个设计敢让运行期继续 fail-open 的理由。
    /// </summary>
    [Fact]
    public void BackfillNeverCreditsMoreThanTheLiveRunDidForTheSameSpan()
    {
        // 运行期：AW 半路哑了（后半段没有任何事件）→ 那半段判 AwOffline，计入专注
        var win = new List<AwEvent> { Win(T0, 1800, "econ") };

        var buf = new JudgmentBuffer(T0, 60);
        for (var m = 1; m <= 60; m++)
            buf.Tick(T0.AddMinutes(m), win, [], Rules, Goal);
        var live = buf.FocusedSeconds;

        var backfilled = Count(3600, win, []);

        Assert.Equal(1800, backfilled);       // 只认 AW 真有记录的那半小时
        Assert.True(live > backfilled, $"live {live} should exceed backfilled {backfilled}");
    }
}
