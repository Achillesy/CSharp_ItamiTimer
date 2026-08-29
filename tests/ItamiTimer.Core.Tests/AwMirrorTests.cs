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
}
