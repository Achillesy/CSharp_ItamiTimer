using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 第二版引擎的**边界与追赶**。
///
/// 这一批是把 2026-08-02 删掉的 `BoundaryTests` / `CheckpointCatchUpTests` 守的东西
/// 搬过来重写的——那两个文件守的是已经不存在的 `Replay`，但它们守的**问题**还在：
/// 跨整点会不会错位、漏了几拍能不能补齐、写到 buffer 尽头会不会越界。
///
/// 全是纯函数：喂合成事件，不等时间、不碰 AW。
/// </summary>
public class JudgmentBufferBoundaryTests
{
    private static readonly GroupRules Rules = GroupRules.Parse(
        """{ "groups": { "经济学": { "rules": [ { "app": "^econ$" } ] } } }""");

    private const string Goal = "经济学";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 2, 14, 5, 0, TimeSpan.FromHours(8));

    private static AwEvent Win(DateTimeOffset from, double seconds, string app)
        => new(from, seconds, app, $"{app} 窗口", null);

    private static AwEvent Afk(DateTimeOffset from, double seconds)
        => new(from, seconds, null, null, "afk");

    /// <summary>一条盖满整个查询窗口的窗口事件。</summary>
    private static List<AwEvent> Whole(DateTimeOffset tick, string app)
        => [Win(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                JudgmentBuffer.QueryWindowSeconds, app)];

    private static int GrayEnd(JudgmentBuffer buf)
    {
        for (var i = JudgmentBuffer.TotalSize - 1; i >= 0; i--)
            if (buf[i] == JudgmentCode.Gray) return i + 1;
        return -1;
    }

    // ---------------------------------------------------------------- 跨边界

    /// <summary>
    /// 2026-07-28 那个 bug 的守卫（承诺弧跨零点跳到第二圈）：格子的时刻和圈号都只能从
    /// **任务已走了多少分钟**来，不能从钟面上的绝对分钟数来。任何整点都会犯，零点只是
    /// 碰巧也是整点。
    /// </summary>
    [Fact]
    public void 跨过整点和午夜时格子不错位()
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
            Assert.Equal(late.AddMinutes(i), cells[i].Start);   // 23:58 / 23:59 / 00:00 …
        }
        // 第 2 格正好跨过午夜，日期要跟着走
        Assert.Equal(3, cells[2].Start.Day);
    }

    /// <summary>投影范围 = 已走过 + 承诺弧（§4.6），再往后是 Init，不吐。</summary>
    [Fact]
    public void 投影范围恰好是已走过加上承诺弧()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);      // 走了 1 分钟，赚了 1 分钟

        var cells = buf.ToMinuteCells();
        Assert.Equal(1 + 9, cells.Count);                       // 1 格走过 + 9 格还欠着
        Assert.Equal(60, cells[0].FocusSeconds);
        Assert.Equal(60, cells[^1].GraySeconds);
    }

    /// <summary>每一格恒为完整的 60 秒——五个计数加起来必须正好 60。</summary>
    [Fact]
    public void 每一格的五个计数加起来恒为六十()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(Start, 37, "econ"), Win(Start.AddSeconds(37), 40, "chrome")],
                 [Afk(Start.AddMinutes(1).AddSeconds(20), 25)], Rules, Goal);

        foreach (var c in buf.ToMinuteCells())
            Assert.Equal(60, c.FocusSeconds + c.OffTaskSeconds + c.AfkSeconds + c.GraySeconds + c.InitSeconds);
    }

    // ---------------------------------------------------------------- 追赶

    /// <summary>
    /// 漏拍**仍在 4 分钟窗口内**时必须完全补齐——buffer 按绝对偏移写入，晚一拍只是晚一拍。
    /// </summary>
    [Fact]
    public void 漏两拍但没超出查询窗口_下一拍全部补齐()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        // 第 2、3 拍没跑（AW 超时 / 界面卡住），第 4 拍才回来
        var t4 = Start.AddMinutes(4);
        buf.Tick(t4, Whole(t4, "econ"), [], Rules, Goal);

        Assert.Equal(4 * 60, buf.FocusedSeconds);               // 四分钟一秒不少
        Assert.Equal(4, buf.ToMinuteCells().Count(c => c.FocusSeconds == 60));
    }

    /// <summary>
    /// 漏得比查询窗口还久时，窗口之外那段判 `Init`：**不计入，也不能白送**。
    /// 判据是缺口——那段时间必须原封不动地欠着。
    /// </summary>
    [Fact]
    public void 漏拍超出窗口时缺口不减少()
    {
        var buf = new JudgmentBuffer(Start, 30);
        var t1 = Start.AddMinutes(1);
        var before = buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal).DeficitSeconds;

        // 睡了 20 分钟；醒来只查得到最近 4 分钟
        var wake = Start.AddMinutes(21);
        var after = buf.Tick(wake, Whole(wake, "econ"), [], Rules, Goal).DeficitSeconds;

        // 只有醒来那 4 分钟能减账，中间 16 分钟一秒都不算
        Assert.Equal(before - JudgmentBuffer.QueryWindowSeconds, after);
    }

    // ---------------------------------------------------------------- buffer 尽头

    /// <summary>
    /// 承诺弧比绘制区还长时要裁掉，**不能越界**。目标 200 分钟 = 12000 秒，
    /// 而绘制区只有 7200 秒。
    /// </summary>
    [Fact]
    public void 承诺弧超出绘制区时裁到末尾而不越界()
    {
        var buf = new JudgmentBuffer(Start, 200);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        Assert.Equal(JudgmentBuffer.TotalSize, GrayEnd(buf));           // 一直铺到末尾
        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.TotalSize - 1]);
    }

    /// <summary>
    /// §15.5 的**真**守卫：归档的结算范围必须是 `[180, 3780)`，不是 `[0, 3600)`。
    ///
    /// ⚠️ 这条测试要能抓到那个偏移，**padding 里的内容必须跟正文不一样**。
    /// 如果整个 buffer 都是专注，两种写法都算出 3600，测试全绿而 bug 还在——
    /// 「同一个量两处口径」的错就是这么溜过去的。
    ///
    /// 所以这里让点 Start **之前**那 3 分钟在摸鱼、之后全在专注：
    /// <list type="bullet">
    ///   <item>正确的 `[180, 3780)` → 3600 秒（整整一小时的专注）</item>
    ///   <item>错误的 `[0, 3600)`   → 3420 秒（漏掉了 [3600,3780) 那 3 分钟，
    ///         又把 padding 里 3 分钟的摸鱼算了进来）</item>
    /// </list>
    /// </summary>
    [Fact]
    public void 归档只结算任务开始之后的那一小时_padding不算()
    {
        var buf = new JudgmentBuffer(Start, 200);

        // 第一拍：窗口里前 3 分钟（padding，点 Start 之前）在摸鱼，之后在专注
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, [Win(Start.AddMinutes(-3), 180, "chrome"), Win(Start, 60, "econ")],
                 [], Rules, Goal);
        Assert.Equal(JudgmentCode.OffTask, buf[0]);            // padding 里确实是红的

        var settled = 0;
        for (var i = 2; i <= 120; i++)
        {
            var t = Start.AddMinutes(i);
            settled += buf.Tick(t, Whole(t, "econ"), [], Rules, Goal).SettledSeconds;
        }

        Assert.Equal(JudgmentBuffer.ArchiveSeconds, settled);
    }

    /// <summary>跑满 3 小时应该归档两次，结算出来的正好是两小时。</summary>
    [Fact]
    public void 三小时里归档两次_每次正好一小时()
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
        // 三个量对得上：原始 300 分钟 = 已结算 120 分钟 + 剩余目标
        Assert.Equal(300 * 60 - settled, buf.RemainingTargetSeconds);
    }

    // ---------------------------------------------------------------- 事件边界

    /// <summary>
    /// T1/F7：查询窗口往前放宽 6 小时，所以事件列表里会有**起点在几小时前**的长事件。
    /// 它必须能画进来（否则一直开着同一个窗口反而判成没记录），而且不能画出界。
    /// </summary>
    [Fact]
    public void 起点远在窗口之前的长事件也要画进来且不越界()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        // 六小时前开始、持续七小时的一条事件
        var long_ = Win(Start.AddHours(-6), 7 * 3600, "econ");

        buf.Tick(t1, [long_], [], Rules, Goal);

        Assert.Equal(60, buf.FocusedSeconds);                  // 任务这一分钟算上了
        Assert.Equal(JudgmentCode.Focused, buf[JudgmentBuffer.PaddingSeconds]);
    }

    /// <summary>
    /// 点 Start **之前**那 3 分钟（padding）永不计入，哪怕当时就在目标应用上。
    /// 起点截断到整分钟（A6）本来就白送了最多 59 秒，padding 再送就送过头了。
    /// </summary>
    [Fact]
    public void padding里的专注永不计入()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);
        // 事件盖满整个 4 分钟窗口，其中前 3 分钟在任务开始之前
        buf.Tick(t1, Whole(t1, "econ"), [], Rules, Goal);

        Assert.Equal(JudgmentCode.Focused, buf[0]);            // padding 里确实画上了
        Assert.Equal(60, buf.FocusedSeconds);                  // 但一秒都不算
    }

    /// <summary>
    /// afk 收缩（T5）会让承诺弧的末端**前移**——`RefreshGray` 必须先清后填，
    /// 否则原地留下一截残渣，弧比实际长。
    /// </summary>
    [Fact]
    public void afk被改判回专注时承诺弧末端前移且不留残渣()
    {
        var buf = new JudgmentBuffer(Start, 10);

        // 第一拍：整个第 0 分钟被 afk 盖住 → 一秒都没赚到
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, Whole(t1, "econ"), [Afk(Start, 60)], Rules, Goal);
        Assert.Equal(0, buf.FocusedSeconds);
        var endBefore = GrayEnd(buf);

        // 第二拍：afk 收缩到没有（人其实一直在），前两分钟都改判成专注
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, Whole(t2, "econ"), [], Rules, Goal);

        Assert.Equal(120, buf.FocusedSeconds);
        var endAfter = GrayEnd(buf);

        Assert.True(endAfter < endBefore,
            $"弧末端应该前移：{endBefore} → {endAfter}");
        Assert.NotEqual(JudgmentCode.Gray, buf[endAfter]);     // 末端之后干干净净，没有残渣
    }

    /// <summary>
    /// 一秒里既被 afk 盖住又有目标应用的窗口事件时判 `Afk`——afk 画在最后，盖住一切。
    /// 「停在目标应用上起身走开」是最省力的作弊路径（A4），必须堵死。
    /// </summary>
    [Fact]
    public void 停在目标应用上走开的那几秒判离开而不是专注()
    {
        var buf = new JudgmentBuffer(Start, 10);
        var t1 = Start.AddMinutes(1);

        // 整分钟都在目标应用上，但后 30 秒 afk
        buf.Tick(t1, Whole(t1, "econ"), [Afk(Start.AddSeconds(30), 30)], Rules, Goal);

        var cell = buf.ToMinuteCells()[0];
        Assert.Equal(30, cell.FocusSeconds);
        Assert.Equal(30, cell.AfkSeconds);
        Assert.Equal(0, cell.OffTaskSeconds);                  // 离开不怪你，不判红
    }
}
