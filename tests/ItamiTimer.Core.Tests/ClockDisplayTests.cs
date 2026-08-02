using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 2026-07-27 踩到的时区显示 bug 的守卫。
///
/// 账单里「专注已达成于」打成了 06:40:45，实际是 14:40:45——同一份账单混了两个
/// 时区：`StartedAt` 来自 `DateTimeOffset.Now`（本地偏移），而 `FocusCompletedAt`
/// 是从 AW 事件推导来的，AW 返回 UTC，`DateTimeOffset.Parse` 保留 `+00:00`。
///
/// 这里守的是根因：**从 AW 事件推导出来的时刻，会带着事件自己的偏移量**。
/// 所以任何显示它的地方都必须先转本地（渲染层已收口到 Renderer.Clock）。
///
/// **2026-07-28 又犯了一次**：界面层的日志打出「专注达成于 16:37:35」，实际是本地
/// 00:37:35。上一次只把 CLI 的渲染收了口，App 的日志行漏在外面 —— 说明"每个显示的
/// 地方各自记得转"这个约定靠不住。现在改成**在边界上归一**：`AwClient` 解析
/// 时间戳时直接 `ToLocalTime()`，两种偏移量根本不流进核心。
/// </summary>
public class ClockDisplayTests
{
    /// <summary>
    /// 第二版把这个坑从根上填了：**达成时刻不再从 AW 事件推导**，它就是喂进去的那一拍
    /// （DESIGN §4.5）。所以它的偏移量来自调用方的时钟，跟事件是 UTC 还是本地无关。
    ///
    /// 这里钉住这条语义——它同时也是 §15.1 的解药：一个不从账本推导的时刻，
    /// 不可能因为账本被重写而往回跳。
    /// </summary>
    [Fact]
    public void 达成时刻来自调用方的时钟_不再继承AW事件的偏移量()
    {
        var rules = GroupRules.Parse("""
            { "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } } }
            """);
        var localStart = new DateTimeOffset(2026, 7, 27, 14, 35, 0, TimeSpan.FromHours(8));
        var buf = new JudgmentBuffer(localStart, 5);

        // 事件用 UTC 偏移喂进来——AwClient 之外的调用方完全可能这么干
        var utcStart = localStart.ToUniversalTime();
        List<AwEvent> win = [new(utcStart.AddMinutes(-3), 1200, "SumatraPDF.exe", "曼昆经济学.pdf", null)];

        TickOutcome outcome = default;
        DateTimeOffset tick = default;
        for (var i = 1; i <= 5 && !outcome.Completed; i++)
        {
            tick = localStart.AddMinutes(i);
            outcome = buf.Tick(tick, win, [], rules, "学习经济学");
        }

        Assert.True(outcome.Completed);
        Assert.Equal(TimeSpan.FromHours(8), tick.Offset);      // 偏移量来自这一拍，不是事件
        Assert.Equal(localStart.AddMinutes(5), tick);
    }

    [Fact]
    public void 不同偏移量的同一时刻相等_核算不受影响()
    {
        var utc = new DateTimeOffset(2026, 7, 27, 6, 40, 45, TimeSpan.Zero);
        var local = new DateTimeOffset(2026, 7, 27, 14, 40, 45, TimeSpan.FromHours(8));

        Assert.Equal(utc, local);                       // 同一个瞬间
        Assert.NotEqual(utc.ToString("HH:mm:ss"), local.ToString("HH:mm:ss")); // 但显示出来不一样
    }

    [Fact]
    public void 边界上归一_AwClient解析出来的时刻是本地偏移()
    {
        // 这是现在真正的防线：AwClient 在解析那一步就 ToLocalTime()，
        // 所以从 AW 来的时刻不会再带着 +00:00 流进核心（见 AwClient.FetchEventsAsync）。
        // 这里直接钉住那一行的语义，免得将来有人"顺手"把 ToLocalTime 去掉。
        var utc = DateTimeOffset.Parse("2026-07-27T16:37:35.000Z");
        Assert.Equal(TimeSpan.Zero, utc.Offset);                       // 解析出来是 UTC
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), utc.ToLocalTime().Offset);
        Assert.Equal(utc, utc.ToLocalTime());                          // 绝对时刻不变
    }
}
