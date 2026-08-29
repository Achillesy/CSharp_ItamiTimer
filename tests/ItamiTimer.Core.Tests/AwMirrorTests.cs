using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 内存镜像（DESIGN §7.5）：把 AW 的区间事件摊平成每秒一个判定。纯逻辑，`now` 和事件
/// 都是参数，跟 <c>JudgmentBufferTests</c> 一样的路数。
/// </summary>
public class AwMirrorTests
{
    private const string Reading = "Reading";
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 11, 0, 0, TimeSpan.Zero);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        { "groups": { "Reading": { "rules": [ { "app": "^Reader\\.exe$" } ] } } }
        """);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static AwEvent Win(double startOffset, double duration, string app)
        => new(At(startOffset), duration, app, app + " - window", null);

    private static AwEvent Afk(double startOffset, double duration)
        => new(At(startOffset), duration, null, null, "afk");

    private static AwMirror New(double nowOffset = 0)
        => new(At(nowOffset), Rules, Reading);

    private static JudgmentCode Code(AwMirror m, double offset) => m.At(At(offset)).Code;

    // ---------------------------------------------------------------- 摊平

    [Fact]
    public void 一条事件被摊成它覆盖的每一秒()
    {
        var m = New();
        m.Apply([Win(-10, 4, "Reader.exe")], [], At(0));

        Assert.Equal(JudgmentCode.Focused, Code(m, -10));
        Assert.Equal(JudgmentCode.Focused, Code(m, -9));
        Assert.Equal(JudgmentCode.Focused, Code(m, -8));
        Assert.Equal(JudgmentCode.Focused, Code(m, -7));
        Assert.Equal(JudgmentCode.Focused, Code(m, -6));   // ceil：末端不足一秒也占满
    }

    [Fact]
    public void 不命中小目标的事件是OffTask并记下是哪个窗口()
    {
        var m = New();
        m.Apply([Win(-5, 3, "Chat.exe")], [], At(0));

        var s = m.At(At(-4));
        Assert.Equal(JudgmentCode.OffTask, s.Code);
        Assert.Equal("Chat.exe", s.App);
        Assert.Equal("Chat.exe - window", s.Title);
    }

    [Fact]
    public void 零时长事件也占满它落在的那一秒()
    {
        // T4：标题每秒变的窗口会产生一堆 duration=0 的事件，实测占 18.5%
        var m = New();
        m.Apply([Win(-30, 25, "Reader.exe"), Win(-4, 0, "Chat.exe")], [], At(0));

        Assert.Equal(JudgmentCode.OffTask, Code(m, -4));
    }

    [Fact]
    public void 同一秒里后开的窗口赢()
    {
        var m = New();
        m.Apply([Win(-5, 1, "Reader.exe"), Win(-5, 1, "Chat.exe")], [], At(0));

        Assert.Equal(JudgmentCode.OffTask, Code(m, -5));
    }

    // ---------------------------------------------------------------- 预测

    [Fact]
    public void 事件之间的空隙沿用前一秒()
    {
        // 实测：AW 的事件不是首尾相接的，切窗口时留 ~1 秒空隙
        var m = New();
        m.Apply([Win(-20, 9, "Reader.exe"), Win(-10, 8, "Chat.exe")], [], At(0));

        Assert.Equal(JudgmentCode.Focused, Code(m, -12));
        Assert.Equal(JudgmentCode.Focused, Code(m, -11));   // 空隙：沿用前一秒的 Reader
        Assert.Equal(JudgmentCode.OffTask, Code(m, -10));
    }

    [Fact]
    public void 末尾AW还没吐出来的那几秒也用预测填到now()
    {
        // 用户举的例子：11:00:01 更新时，AW 只知道到 10:59:55，后面几秒靠预测
        var m = New();
        m.Apply([Win(-30, 25, "Chat.exe")], [], At(0));

        Assert.Equal(JudgmentCode.OffTask, Code(m, -5));
        Assert.Equal(JudgmentCode.OffTask, Code(m, -1));
        Assert.Equal(JudgmentCode.OffTask, Code(m, 0));    // 一直预测到 now
    }

    [Fact]
    public void 预测过的秒会被后到的真实事件整段改回来()
    {
        // 用户 2026-08-29 举的例子（时刻换算成相对偏移）：
        //   01:09 起一直在目标窗口里；01:10:00 切走了，但 AW 还没吐出新事件。
        //   01:10:03 这一刻刷新 → 镜像只能预测：00/01/02/03 沿用上一个事件，判 Focused。
        //   随后 AW 提交了 start=01:10:00 的新事件 → 那四秒**必须被改回 OffTask**。
        var m = New();

        // ① 01:06:00~01:10:00 一直在 Reader.exe 里
        m.Apply([Win(-240, 240, "Reader.exe")], [], At(3));

        Assert.Equal(JudgmentCode.Focused, Code(m, -1));   // 有真实事件的秒
        Assert.Equal(JudgmentCode.Focused, Code(m, 0));    // ↓ 这四秒是**预测**出来的
        Assert.Equal(JudgmentCode.Focused, Code(m, 1));
        Assert.Equal(JudgmentCode.Focused, Code(m, 2));
        Assert.Equal(JudgmentCode.Focused, Code(m, 3));

        // ② AW 终于吐出那条 start=01:10:00 的事件（心跳提交粒度 ~10 秒，所以晚了几秒）
        m.Apply([Win(0, 8, "Chat.exe")], [], At(8));

        Assert.Equal(JudgmentCode.OffTask, Code(m, 0));    // 预测被真值整段改回来
        Assert.Equal(JudgmentCode.OffTask, Code(m, 1));
        Assert.Equal(JudgmentCode.OffTask, Code(m, 2));
        Assert.Equal(JudgmentCode.OffTask, Code(m, 3));
        Assert.Equal(JudgmentCode.Focused, Code(m, -1));   // 切走之前的那一秒不受影响
    }

    [Fact]
    public void 改正的范围由事件自己的start决定不受四分钟窗口以外的影响()
    {
        // 三分半之前的那一秒照样改得动——只要还在环里
        var m = New();
        m.Apply([Win(-240, 240, "Reader.exe")], [], At(0));
        Assert.Equal(JudgmentCode.Focused, Code(m, -210));

        m.Apply([Win(-215, 20, "Chat.exe")], [], At(0));
        Assert.Equal(JudgmentCode.OffTask, Code(m, -210));
    }

    [Fact]
    public void 预测最多延续一个环的长度不会画出几小时()
    {
        // watcher 死掉：再也没有新事件。预测只往后传，跨不过环的起点
        var m = New();
        m.Apply([Win(-30, 25, "Chat.exe")], [], At(0));
        m.MarkUnavailable(At(AwMirror.Capacity * 2));

        Assert.Equal(JudgmentCode.AwOffline, Code(m, AwMirror.Capacity * 2));
        Assert.Equal(JudgmentCode.AwOffline, Code(m, AwMirror.Capacity * 2 - 10));
    }

    // ---------------------------------------------------------------- afk

    [Fact]
    public void afk覆盖窗口判定但保留窗口名字()
    {
        var m = New();
        m.Apply([Win(-30, 30, "Chat.exe")], [Afk(-20, 10)], At(0));

        var s = m.At(At(-15));
        Assert.Equal(JudgmentCode.Afk, s.Code);
        Assert.Equal("Chat.exe", s.App);          // 人不在时屏幕上停着的还是它
        Assert.Equal(JudgmentCode.OffTask, Code(m, -25));
    }

    [Fact]
    public void afk的回溯写入靠事件自己的start改写过去()
    {
        // T5：10:00 停止输入，AW 到 10:03 才写下一条 start=10:00 的 afk 事件
        var m = New();
        m.Apply([Win(-200, 200, "Chat.exe")], [], At(0));
        Assert.Equal(JudgmentCode.OffTask, Code(m, -150));

        // 三分钟后那条 afk 事件才出现，start 回填到 -180
        m.Apply([], [Afk(-180, 180)], At(0));
        Assert.Equal(JudgmentCode.Afk, Code(m, -150));
    }

    [Fact]
    public void 重叠的多条afk事件全部画上去不是只取最新一条()
    {
        // 实测 afk 桶里有 start 相同、duration 不同的重叠事件；只取最新那条会把在座时间算多
        var m = New();
        m.Apply([Win(-60, 60, "Chat.exe")], [Afk(-50, 20), Afk(-50, 40)], At(0));

        Assert.Equal(JudgmentCode.Afk, Code(m, -45));
        Assert.Equal(JudgmentCode.Afk, Code(m, -15));   // 靠更长的那条盖到
    }

    // ---------------------------------------------------------------- 推进即清空

    [Fact]
    public void 推进时跳过的秒被清空不会留下旧数据()
    {
        var m = New();
        m.Apply([Win(-5, 5, "Reader.exe")], [], At(0));
        Assert.Equal(JudgmentCode.Focused, Code(m, 0));

        // 环长一圈之后，同一个槽位不能还认得出旧值
        m.MarkUnavailable(At(AwMirror.Capacity));
        Assert.Equal(JudgmentCode.AwOffline, Code(m, AwMirror.Capacity));
    }

    [Fact]
    public void 睡一觉回来整个环过期()
    {
        var m = New();
        m.Apply([Win(-30, 30, "Reader.exe")], [], At(0));

        m.MarkUnavailable(At(3 * 3600));   // 睡了三小时
        for (var i = 0; i < AwMirror.Capacity; i++)
            Assert.Equal(JudgmentCode.AwOffline, Code(m, 3 * 3600 - i));
    }

    [Fact]
    public void 查询失败只推进不预测()
    {
        var m = New();
        m.Apply([Win(-10, 10, "Chat.exe")], [], At(0));
        Assert.Equal(JudgmentCode.OffTask, Code(m, 0));

        m.MarkUnavailable(At(5));
        Assert.Equal(JudgmentCode.AwOffline, Code(m, 5));
        Assert.Equal(JudgmentCode.OffTask, Code(m, 0));   // 已经写下的不受影响
    }

    // ---------------------------------------------------------------- 边界

    [Fact]
    public void 环至少装得下账本每分钟要重画的四分钟()
    {
        Assert.True(AwMirror.Capacity > JudgmentBuffer.QueryWindowSeconds);
        Assert.Equal(JudgmentBuffer.QueryWindowSeconds, AwMirror.WindowSeconds);
    }

    [Fact]
    public void 落在环外的秒一律AwOffline()
    {
        var m = New();
        m.Apply([Win(-10, 10, "Reader.exe")], [], At(0));

        Assert.Equal(JudgmentCode.AwOffline, Code(m, 1));                      // 还没到
        Assert.Equal(JudgmentCode.AwOffline, Code(m, -AwMirror.Capacity));     // 太老
    }

    [Fact]
    public void 亚秒零头一律截断()
    {
        // 跟 §4.2 / DECISIONS H9 同一条纪律：掺进亚秒零头会让每拍相位不同、边界秒来回翻面
        var m = new AwMirror(At(0).AddMilliseconds(937), Rules, Reading);
        Assert.Equal(At(0), m.Newest);

        m.Apply([Win(-3, 3, "Reader.exe")], [], At(0).AddMilliseconds(400));
        Assert.Equal(At(0), m.Newest);                                  // 没被 400ms 推到下一秒
        Assert.Equal(JudgmentCode.Focused, Code(m, -1));
        // 同一秒内的任意时刻取到的是同一格
        Assert.Equal(m.At(At(-1)), m.At(At(-1).AddMilliseconds(999)));
    }

    [Fact]
    public void 重复吸收同一批事件是幂等的()
    {
        var m = New();
        var evs = new[] { Win(-20, 9, "Reader.exe"), Win(-10, 8, "Chat.exe") };

        m.Apply(evs, [], At(0));
        var before = m.Slice(At(-20), At(1));
        m.Apply(evs, [], At(0));
        var after = m.Slice(At(-20), At(1));

        Assert.Equal(before, after);
    }

    [Fact]
    public void 没有选中小目标时一切都算跑偏()
    {
        var m = new AwMirror(At(0), Rules, null);
        m.Apply([Win(-5, 5, "Reader.exe")], [], At(0));

        Assert.Equal(JudgmentCode.OffTask, Code(m, -3));
    }

    // ---------------------------------------------------------------- 还原成事件

    [Fact]
    public void 还原出来的事件喂回Paint能得到一模一样的每秒判定()
    {
        // 这是账本切到镜像的全部依据：round-trip 必须逐秒相等
        var m = New();
        m.Apply([Win(-60, 20, "Reader.exe"), Win(-30, 25, "Chat.exe")], [Afk(-15, 5)], At(0));

        var (win, afk) = m.EventsIn(At(-60), At(1));

        var span = new JudgmentCode[61];
        Array.Fill(span, JudgmentCode.AwOffline);
        Judgment.Paint(span, At(-60), win, afk, Rules, Reading);

        for (var i = 0; i < span.Length; i++)
            Assert.Equal(Code(m, -60 + i), span[i]);
    }

    [Fact]
    public void 相邻相同的秒合并成一条事件()
    {
        var m = New();
        m.Apply([Win(-10, 10, "Reader.exe")], [], At(0));

        var (win, _) = m.EventsIn(At(-10), At(0));
        Assert.Single(win);
        Assert.Equal(At(-10), win[0].Start);
        Assert.Equal(10, win[0].DurationSeconds);
    }

    [Fact]
    public void AwOffline的秒什么都不吐()
    {
        var m = New();
        m.MarkUnavailable(At(0));

        var (win, afk) = m.EventsIn(At(-10), At(1));
        Assert.Empty(win);
        Assert.Empty(afk);
    }

    [Fact]
    public void afk的秒只吐afk事件不吐窗口事件()
    {
        var m = New();
        m.Apply([Win(-20, 20, "Chat.exe")], [Afk(-10, 10)], At(0));

        var (win, afk) = m.EventsIn(At(-10), At(0));
        Assert.Empty(win);
        Assert.Single(afk);
        Assert.Equal("afk", afk[0].Status);
    }
}
