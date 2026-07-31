using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// <see cref="TaskPhase"/> 的守卫。2026-07-28 实跑抓到的 bug：
/// 6 个计时点，6 行全是 <c>NoData</c>，一次例外都没有。
///
/// 根因：AW 的窗口监听总是落后 <c>now</c> 几秒（§14.4a T3），所以时间线末尾**恒有**
/// 一小截 Gap，而 DerivePhase 直接取 <c>intervals[^1]</c>。后果是 CLI 永远打
/// 「● AW 无数据」而不是「● 专注中」，§0.5 那条"断线换任务栏图标"接上去也会
/// 永远显示断线 —— 那个被动信号就废了。
/// </summary>
public class PhaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 28, 10, 0, 0, TimeSpan.FromHours(8));
    private static DateTimeOffset At(double m) => T0.AddMinutes(m);

    private static readonly GroupRules Rules = GroupRules.Parse("""
        { "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } } }
        """);

    private static TaskRecord Task() => new()
    {
        StartedAt = T0, FocusMinutes = 25, Group = "学习经济学",
    };

    private static AwEvent Win(double a, double b, string app, string title)
        => new(At(a), (b - a) * 60, app, title, null);

    private static AwEvent Afk(double a, double b, string s) => new(At(a), (b - a) * 60, null, null, s);

    [Fact]
    public void 末尾几秒心跳滞后_仍然算专注中()
    {
        // 窗口事件只到 T0+4:50，afk 到 T0+5 —— 末尾 10 秒是 AW 的正常滞后
        var s = Replay.Run(Task(), Rules,
            [Win(0, 4 + 50 / 60.0, "SumatraPDF.exe", "曼昆经济学.pdf")],
            [Afk(0, 5, "not-afk")], At(5));

        Assert.Equal(TaskPhase.Focusing, s.Phase);
        Assert.True(s.GapSeconds > 5, "Gap 该记多少还是记多少，只有 Phase 跳过它");
    }

    [Fact]
    public void 末尾滞后也不掩盖跑偏()
    {
        var s = Replay.Run(Task(), Rules, [
            Win(0, 3, "SumatraPDF.exe", "曼昆经济学.pdf"),
            Win(3, 4 + 50 / 60.0, "chrome.exe", "斗破苍穹"),
        ], [Afk(0, 5, "not-afk")], At(5));

        Assert.Equal(TaskPhase.Slacking, s.Phase);
    }

    [Fact]
    public void 空洞超过容差_照样报无数据()
    {
        // aw-server 真宕掉：末尾空了 2 分钟，远超 30 秒容差
        var s = Replay.Run(Task(), Rules,
            [Win(0, 3, "SumatraPDF.exe", "曼昆经济学.pdf")],
            [Afk(0, 3, "not-afk")], At(5));

        Assert.Equal(TaskPhase.NoData, s.Phase);
    }

    [Fact]
    public void 整段都没数据_是无数据不是专注中()
    {
        var s = Replay.Run(Task(), Rules, [], [], At(5));
        Assert.Equal(TaskPhase.NoData, s.Phase);
    }
}
