using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 跑偏就把钟面反色（DESIGN §8.9）：窗口怎么取、什么才触发。纯函数，`now` 永远是参数，
/// 跟 <c>OffTaskAttributionTests</c> 一样的路数。
/// </summary>
public class InversionTests
{
    private const string Reading = "Reading";
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        { "groups": { "Reading": { "rules": [ { "title": "Book" } ] } } }
        """);

    /// <summary>窗口是 [now-20s, now-15s)，所以第 s 秒（相对 now）在窗口里 ⟺ -20 ≤ s < -15。</summary>
    private static AwEvent At(int offsetSeconds, double duration, string app, string title)
        => new(Now.AddSeconds(offsetSeconds), duration, app, title, null);

    private static AwEvent Afk(int offsetSeconds, double duration)
        => new(Now.AddSeconds(offsetSeconds), duration, null, null, "afk");

    private static bool Eval(AwEvent[] win, AwEvent[]? afk = null)
        => Inversion.Evaluate(Now, win, afk ?? [], Rules, Reading);

    // ---------------------------------------------------------------- 窗口

    [Fact]
    public void 窗口是当前秒往前的第二十秒到第十五秒()
    {
        var (from, to) = Inversion.WindowFor(Now);
        Assert.Equal(Now.AddSeconds(-20), from);
        Assert.Equal(Now.AddSeconds(-15), to);
        Assert.Equal(Inversion.SpanSeconds, (to - from).TotalSeconds);
    }

    [Fact]
    public void 亚秒的零头先截断掉否则每次采样的相位都不一样()
    {
        var (from, to) = Inversion.WindowFor(Now.AddMilliseconds(937));
        Assert.Equal(Now.AddSeconds(-20), from);
        Assert.Equal(Now.AddSeconds(-15), to);
    }

    [Fact]
    public void 采样节拍跟窗口宽度相等前后两次首尾相接不漏也不重()
    {
        Assert.Equal(Inversion.SpanSeconds, Inversion.SampleSeconds);
        var (_, prevTo) = Inversion.WindowFor(Now.AddSeconds(-Inversion.SampleSeconds));
        var (from, _) = Inversion.WindowFor(Now);
        Assert.Equal(from, prevTo);
    }

    [Fact]
    public void 窗口退后十五秒是为了躲开AW六到十二秒的滞后()
        => Assert.True(Inversion.LagSeconds > 12);

    // ---------------------------------------------------------------- 判据

    [Fact]
    public void 窗口内全是命中目标的事件不反色()
        => Assert.False(Eval([At(-25, 20, "Reader.exe", "Book.pdf")]));

    [Fact]
    public void 窗口内有一秒不命中就反色()
        => Assert.True(Eval([
            At(-25, 20, "Reader.exe", "Book.pdf"),
            At(-18, 1, "Chat.exe", "General"),
        ]));

    [Fact]
    public void 落在窗口之外的跑偏不算这一次的()
    {
        // -14 秒：比窗口末端（-15）还新，还没轮到它
        Assert.False(Eval([At(-25, 20, "Reader.exe", "Book.pdf"), At(-14, 3, "Chat.exe", "General")]));
        // -21 秒且只持续 1 秒：整个落在窗口起点之前
        Assert.False(Eval([At(-25, 20, "Reader.exe", "Book.pdf"), At(-21, 1, "Chat.exe", "General")]));
    }

    [Fact]
    public void 一条事件跨进窗口时只要重叠到就算()
        => Assert.True(Eval([At(-25, 20, "Reader.exe", "Book.pdf"), At(-22, 4, "Chat.exe", "General")]));

    [Fact]
    public void 一条事件都没有时不反色而不是当成跑偏()
    {
        // 整个窗口都是 AwOffline —— aw-watcher-window 死掉时就是这个样子。
        // ⚠️ 这一条正是"判据必须写成 == OffTask"的护栏：写成"不是 Focused 就反色"的话，
        // watcher 一死钟面就永久反色，而账本这段时间全判绿（§3.1 的 fail-open）。
        Assert.False(Eval([]));
    }

    [Fact]
    public void 人不在座的那些秒被afk盖掉不触发反色()
    {
        // 同一段时间里既有不命中的窗口事件、又有 afk：afk 画在最后、覆盖一切
        Assert.True(Eval([At(-22, 8, "Chat.exe", "General")]));
        Assert.False(Eval([At(-22, 8, "Chat.exe", "General")], [Afk(-25, 20)]));
    }

    [Fact]
    public void 整段都是afk时不反色()
        => Assert.False(Eval([], [Afk(-30, 30)]));

    [Fact]
    public void 只有afk盖住一部分时剩下的跑偏照样反色()
        => Assert.True(Eval(
            [At(-22, 8, "Chat.exe", "General")],
            [Afk(-25, 4)]));   // 只盖到 -21，窗口后半段的跑偏还在

    // ---------------------------------------------------------------- ShouldInvert 本身

    [Fact]
    public void 判据只认OffTask其余四种码都不触发()
    {
        Assert.True(Inversion.ShouldInvert([JudgmentCode.Focused, JudgmentCode.OffTask]));
        Assert.False(Inversion.ShouldInvert([JudgmentCode.Focused, JudgmentCode.AwOffline]));
        Assert.False(Inversion.ShouldInvert([JudgmentCode.Afk, JudgmentCode.AwOffline]));
        Assert.False(Inversion.ShouldInvert([JudgmentCode.Gray, JudgmentCode.Init]));
        Assert.False(Inversion.ShouldInvert([]));
    }
}
