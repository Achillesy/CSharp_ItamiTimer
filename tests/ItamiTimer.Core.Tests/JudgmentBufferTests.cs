using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 第二版判定引擎（DESIGN.md §4）。全是纯函数：喂合成事件，不等时间、不碰 AW。
///
/// 这里守的每一条不变量在文档里都有出处——**测试名就是那条不变量**。
/// </summary>
public class JudgmentBufferTests
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

    /// <summary>造一条覆盖整个查询窗口的窗口事件。</summary>
    private static List<AwEvent> WholeWindow(DateTimeOffset tick, string app)
        => [Win(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                JudgmentBuffer.QueryWindowSeconds, app)];

    // ---------------------------------------------------------------- 坐标

    [Fact]
    public void buffer第180格就是任务起点_前面180秒是padding()
    {
        var buf = new JudgmentBuffer(Start, 25);
        Assert.Equal(Start.AddSeconds(-JudgmentBuffer.PaddingSeconds), buf.WallClock);
        Assert.Equal(Start, buf.TaskStart);
        Assert.Equal(JudgmentBuffer.PaddingSeconds, buf.Head);
    }

    [Fact]
    public void 开局的承诺弧就是整段任务时长_而且用的是每拍那套算法()
    {
        var buf = new JudgmentBuffer(Start, 25);

        // padding 不是 Gray——它永不计入、永不绘制
        Assert.Equal(JudgmentCode.Init, buf[0]);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds - 1]);

        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.PaddingSeconds]);
        Assert.Equal(JudgmentCode.Gray, buf[JudgmentBuffer.PaddingSeconds + 25 * 60 - 1]);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds + 25 * 60]);
    }

    // ---------------------------------------------------------------- 覆盖算法

    [Fact]
    public void 命中小目标算专注_其余算偷懒()
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
    public void afk盖住一切_停在目标应用上走开不算数()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // 整个窗口都停在目标应用上，但 afk 说人不在
        buf.Tick(t1, WholeWindow(t1, "econ"),
                 [Afk(t1.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                      JudgmentBuffer.QueryWindowSeconds)],
                 Rules, Goal);

        Assert.Equal(0, buf.FocusedSeconds);
        Assert.Equal(JudgmentCode.Afk, buf[JudgmentBuffer.PaddingSeconds]);
    }

    [Fact]
    public void 一秒里既有专注又有偷懒时判偷懒_覆盖顺序就是fail_closed()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // 两条事件都落在任务的第 0 秒里：先画 Focused、后画 OffTask → OffTask 赢
        var events = new List<AwEvent>
        {
            Win(Start, 0.4, "econ"),
            Win(Start.AddSeconds(0.4), 0.6, "chrome"),
        };
        buf.Tick(t1, events, [], Rules, Goal);

        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);
    }

    /// <summary>
    /// T4：标题每秒都在变时 AW 会写出一串 <c>duration = 0</c> 的事件。旧的逐秒查找问
    /// 「哪条事件盖住了这一秒」，空区间永远答不上来，整段被误判成「无记录」。
    /// 新的按秒落格必须能把它们画出来。
    /// </summary>
    [Fact]
    public void 零时长事件也占满它落进的那一秒()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        var churn = new List<AwEvent>();
        for (var i = 0; i < 60; i++) churn.Add(Win(Start.AddSeconds(i), 0, "econ"));

        buf.Tick(t1, churn, [], Rules, Goal);
        Assert.Equal(60, buf.FocusedSeconds);
    }

    // ---------------------------------------------------------------- AW 缺数据

    [Fact]
    public void 整拍连不上时只有最后一分钟变AwOffline_前三分钟原样不动()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "chrome"), [], Rules, Goal);   // 第 0 分钟判偷懒
        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);

        // 第二拍连不上 = 空事件列表
        buf.Tick(Start.AddMinutes(2), [], [], Rules, Goal);

        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds]);      // 没被抹绿
        Assert.Equal(JudgmentCode.AwOffline, buf[JudgmentBuffer.PaddingSeconds + 60]);
        Assert.Equal(60, buf.FocusedSeconds);                                        // 只有新的那一分钟计入
    }

    /// <summary>
    /// T3：AW 窗口事件恒定滞后 6~12 秒，所以每拍末尾那几秒必然没记录、判 AwOffline。
    /// 下一拍的 4 分钟重写必须把它改判过来——这就是「自愈」，不需要任何额外机制。
    /// </summary>
    [Fact]
    public void 滞后的末尾几秒先算专注_下一拍被真实数据改判()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);

        // 第一拍：数据只到 t1−10s，最后 10 秒是空的
        buf.Tick(t1, [Win(t1.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                          JudgmentBuffer.QueryWindowSeconds - 10, "chrome")], [], Rules, Goal);
        Assert.Equal(10, buf.FocusedSeconds);                       // 那 10 秒白送了
        Assert.Equal(JudgmentCode.AwOffline, buf[JudgmentBuffer.PaddingSeconds + 59]);

        // 第二拍：AW 补齐了，那 10 秒其实在摸鱼
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(t2.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                          JudgmentBuffer.QueryWindowSeconds - 10, "chrome")], [], Rules, Goal);
        Assert.Equal(JudgmentCode.OffTask, buf[JudgmentBuffer.PaddingSeconds + 59]);
    }

    /// <summary>
    /// 漏拍超出查询窗口之后，那些分钟**保持 Init、不计入**。
    /// 不能把「我根本没查过」当成「AW 没记录」白送出去——否则睡一觉就能刷满任务。
    /// </summary>
    [Fact]
    public void 漏拍太久时窗口之外的空洞不白送时间()
    {
        var buf = new JudgmentBuffer(Start, 60);
        buf.Tick(Start.AddMinutes(1), WholeWindow(Start.AddMinutes(1), "econ"), [], Rules, Goal);

        // 睡了 20 分钟，醒来这一拍只查得到最近 4 分钟
        var wake = Start.AddMinutes(21);
        buf.Tick(wake, WholeWindow(wake, "econ"), [], Rules, Goal);

        // 计入的只有：第一拍 1 分钟 + 醒来这一拍的 4 分钟窗口
        Assert.Equal(60 + JudgmentBuffer.QueryWindowSeconds, buf.FocusedSeconds);
        Assert.Equal(JudgmentCode.Init, buf[JudgmentBuffer.PaddingSeconds + 60 * 10]);
    }

    // ---------------------------------------------------------------- 承诺弧与达成

    private static int GrayEnd(JudgmentBuffer buf)
    {
        for (var i = JudgmentBuffer.TotalSize - 1; i >= 0; i--)
            if (buf[i] == JudgmentCode.Gray) return i + 1;
        return -1;
    }

    [Fact]
    public void 全程专注时承诺弧的终点不动_只是变短()
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
    public void 偷懒一分钟_截止弧就往前滑一格()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        buf.Tick(t1, WholeWindow(t1, "econ"), [], Rules, Goal);
        var end = GrayEnd(buf);

        // 第 0 分钟仍然专注（重写会盖到它，得原样喂回去），第 1 分钟去摸鱼
        var t2 = Start.AddMinutes(2);
        buf.Tick(t2, [Win(Start, 60, "econ"), Win(Start.AddMinutes(1), 60, "chrome")],
                 [], Rules, Goal);

        Assert.Equal(end + 60, GrayEnd(buf));
    }

    /// <summary>
    /// 达成的定义就是「这一拍算出缺口 ≤ 0」，等价于「承诺弧为空」。
    /// 它是个**事件**，不是从账本里推出来的时刻——所以不可能回退（DECISIONS H5）。
    /// </summary>
    [Fact]
    public void 承诺弧消失的那一刻就是专注达成的那一刻()
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
    public void 缺口向上取整到整分钟_所以格子不会半灰半数据()
    {
        var buf = new JudgmentBuffer(Start, 25);
        var t1 = Start.AddMinutes(1);
        // 这一分钟只专注了 37 秒
        buf.Tick(t1, [Win(Start, 37, "econ"), Win(Start.AddSeconds(37), 23, "chrome")],
                 [], Rules, Goal);

        var cells = buf.ToMinuteCells();
        foreach (var c in cells)
            Assert.True(c.GraySeconds is 0 or 60, $"第 {c.Index} 格灰了 {c.GraySeconds} 秒");
    }

    // ---------------------------------------------------------------- 归档

    /// <summary>
    /// 归档 = 「1 小时前放弃、又立刻用剩余目标重开」。所以它必须是**完全连续**的：
    /// 「还差多少」在归档前后恒等（DESIGN §4.4）。写成 `[0,3600)` 就会跳，正负都可能。
    /// </summary>
    [Fact]
    public void 归档前后还差多少恒等_而且结算范围不含padding()
    {
        var buf = new JudgmentBuffer(Start, 200);        // 目标故意大，跑满 2 小时也完不成
        var deficitBefore = 0;
        var settled = 0;

        for (var i = 1; i <= 120; i++)
        {
            var t = Start.AddMinutes(i);
            if (i == 120) deficitBefore = 200 * 60 - buf.FocusedSeconds;
            var outcome = buf.Tick(t, WholeWindow(t, "econ"), [], Rules, Goal);
            settled += outcome.SettledSeconds;
        }

        // 结算的正好是一小时，一秒不多——padding 里那 3 分钟专注不算数
        Assert.Equal(JudgmentBuffer.ArchiveSeconds, settled);
        Assert.Equal(JudgmentBuffer.ArchiveSeconds, buf.ArchivedSeconds);

        var deficitAfter = buf.RemainingTargetSeconds - buf.FocusedSeconds;
        Assert.Equal(deficitBefore - 60, deficitAfter);   // 只差最后这一拍新赚的 60 秒
    }

    [Fact]
    public void 归档后任务起点往前走一小时_圈号跟着buffer位置走()
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
    /// §15.6：写入偏移一旦越出 buffer，ElapsedSeconds 就冻住、归档条件再也不成立，
    /// 会话永久死锁。挂起再久也必须能接着跑。
    /// </summary>
    [Fact]
    public void 睡眠超过两小时也不会把会话冻死()
    {
        var buf = new JudgmentBuffer(Start, 25);
        buf.Tick(Start.AddMinutes(1), WholeWindow(Start.AddMinutes(1), "econ"), [], Rules, Goal);

        var wake = Start.AddHours(5);                     // 睡了 5 小时
        var outcome = buf.Tick(wake, WholeWindow(wake, "econ"), [], Rules, Goal);

        Assert.True(buf.ArchivedSeconds > 0, "应该滚动过若干次，给这一拍腾出位置");
        Assert.Equal(JudgmentBuffer.QueryWindowSeconds, buf.FocusedSeconds);
        Assert.True(outcome.DeficitSeconds > 0);

        // 还能继续走
        var next = wake.AddMinutes(1);
        buf.Tick(next, WholeWindow(next, "econ"), [], Rules, Goal);
        Assert.Equal(JudgmentBuffer.QueryWindowSeconds + 60, buf.FocusedSeconds);
    }
}
