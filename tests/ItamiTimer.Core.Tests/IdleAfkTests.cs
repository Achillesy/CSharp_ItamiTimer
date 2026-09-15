using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 表盘的「人不在」（DESIGN §7.5，3.10.0）：由本机键鼠空闲时长推出离开区间，不再
/// 从 AW 的 afk 桶取数。纯逻辑，<c>now</c> 和 <c>idle</c> 都是参数。
///
/// 最后两个用例是**这次改动真正要防住的那个 bug**：锁屏离开时窗口桶里躺着一条横跨
/// 整段的 <c>loginwindow</c>，它每次取数都会把整段重刷成跑偏——从前 afk 靠取数，
/// 「这一拍没取到」就没人盖回来，于是红格。
/// </summary>
public class IdleAfkTests
{
    private const string Reading = "Reading";
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        { "groups": { "Reading": { "rules": [ { "app": "^Reader\\.exe$" } ] } } }
        """);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);
    private static TimeSpan Sec(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void 没动到一百七十九秒还不算离开()
    {
        var idle = new IdleAfk();
        idle.Track(At(179), Sec(179));

        Assert.Empty(idle.Spans);
    }

    [Fact]
    public void 刚好一百八十秒算离开()
    {
        var idle = new IdleAfk();
        idle.Track(At(180), Sec(180));

        Assert.Single(idle.Spans);
    }

    [Fact]
    public void 起点回填到停止输入那一刻而不是跨过门槛那一刻()
    {
        var idle = new IdleAfk();
        idle.Track(At(200), Sec(180));

        Assert.Equal(At(20), idle.Spans[0].Start);
    }

    [Fact]
    public void 连着几拍还在离开中只算一段并且一直延长到此刻()
    {
        var idle = new IdleAfk();
        for (var t = 180; t <= 240; t++) idle.Track(At(t), Sec(t));

        var span = Assert.Single(idle.Spans);
        Assert.Equal(At(0), span.Start);
        Assert.Equal(At(240), span.End);
    }

    [Fact]
    public void 空闲秒数抖动一点仍算同一段()
    {
        var idle = new IdleAfk();
        idle.Track(At(180), Sec(180));
        idle.Track(At(181), Sec(180.4));   // 起点算出来差了 0.6 秒
        idle.Track(At(182), Sec(182.9));   // 起点算出来差了 0.9 秒

        Assert.Single(idle.Spans);
    }

    [Fact]
    public void 回来之后又离开是两段()
    {
        var idle = new IdleAfk();
        idle.Track(At(180), Sec(180));     // 第一次离开
        idle.Track(At(200), Sec(0));       // 动了键鼠
        idle.Track(At(400), Sec(190));     // 第二次离开

        Assert.Equal(2, idle.Spans.Count);
        Assert.Equal(At(0), idle.Spans[0].Start);
        Assert.Equal(At(210), idle.Spans[1].Start);
    }

    [Fact]
    public void 人回来以后那段离开仍然留着()
    {
        // 这一条是整个改动的支点：idle 归零了，可那几分钟还在镜像窗口里，
        // 而窗口事件每次取数仍会重刷它。只看此刻的信号在这里就会漏。
        var idle = new IdleAfk();
        idle.Track(At(180), Sec(180));
        idle.Track(At(181), Sec(0));

        Assert.Single(idle.Spans);
    }

    [Fact]
    public void 整段滑出镜像窗口之后丢掉()
    {
        var idle = new IdleAfk();
        idle.Track(At(180), Sec(180));                       // 覆盖 [0, 180]
        idle.Track(At(180 + AwMirror.Capacity), Sec(0));     // 末端刚好压在环沿上，还留着
        Assert.Single(idle.Spans);

        idle.Track(At(181 + AwMirror.Capacity), Sec(0));     // 整段出环
        Assert.Empty(idle.Spans);
    }

    [Fact]
    public void 拿不到空闲时长就什么都不画()
    {
        // InputIdle.Elapsed() 失败时返回 Zero（「刚动过」）。功能退化成 3.9.x 的样子，
        // 不是崩，也不是把人判成一直在离开。
        var idle = new IdleAfk();
        for (var t = 0; t < 600; t++) idle.Track(At(t), TimeSpan.Zero);

        Assert.Empty(idle.Spans);
        Assert.Empty(idle.Events());
    }

    [Fact]
    public void 还原出来的是镜像认得的afk事件()
    {
        var idle = new IdleAfk();
        idle.Track(At(200), Sec(180));

        var e = Assert.Single(idle.Events());
        Assert.Equal("afk", e.Status);
        Assert.Equal(At(20), e.Start);
        Assert.Equal(181, e.DurationSeconds);   // 闭区间 → 半开区间，末尾那一秒要算上
        Assert.Null(e.App);
    }

    [Fact]
    public void 锁屏窗口横跨整段也盖不掉离开()
    {
        // 复现用户 2026-09-13 那次：锁屏时窗口桶里是一条一直在长的 loginwindow，
        // 每次取数都把整段重刷成跑偏。afk 这一半现在每拍都是完整区间，所以盖得回来。
        var mirror = new AwMirror(At(0), Rules, Reading);
        var idle = new IdleAfk();

        for (var t = 1; t <= 240; t++)
        {
            idle.Track(At(t), t >= 180 ? Sec(t) : Sec(0));
            var win = new List<AwEvent> { new(At(0), t, "loginwindow", "", null) };
            mirror.Apply(win, idle.Events(), At(t));
        }

        // 离开是从 0 秒开始的（240 秒不动 ⇒ 起点回填到 0）
        for (var t = 0; t <= 240; t++)
            Assert.Equal(JudgmentCode.Afk, mirror.At(At(t)).Code);
    }

    [Fact]
    public void AW掉线时人不在照样画空白()
    {
        // 推翻 DECISIONS H2 的一半：AW 连不上 + 本机知道人不在 ⇒ 画空白，不算专注。
        var mirror = new AwMirror(At(0), Rules, Reading);
        var idle = new IdleAfk();

        for (var t = 1; t <= 300; t++)
        {
            idle.Track(At(t), t >= 100 ? Sec(t - 100) : Sec(0));   // 100 秒起没动，280 秒跨过门槛
            mirror.MarkUnavailable(At(t), idle.Events());
        }

        Assert.Equal(JudgmentCode.AwOffline, mirror.At(At(99)).Code);   // 还在座：无记录 = 专注
        Assert.Equal(JudgmentCode.Afk, mirror.At(At(100)).Code);        // 人不在：空白（回溯改写过）
        Assert.Equal(JudgmentCode.Afk, mirror.At(At(300)).Code);
    }
}
