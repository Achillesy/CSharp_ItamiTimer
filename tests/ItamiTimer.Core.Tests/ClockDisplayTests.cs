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
/// </summary>
public class ClockDisplayTests
{
    [Fact]
    public void 从UTC事件推导出的达成时刻会带着UTC偏移()
    {
        // AW 返回的就是这种：UTC 时间戳
        var utcStart = new DateTimeOffset(2026, 7, 27, 6, 35, 0, TimeSpan.Zero);
        var rules = GroupRules.Parse("""
            { "groups": { "学习经济学": { "rules": [ { "title": "经济学" } ] } } }
            """);
        var task = new TaskRecord
        {
            StartedAt = utcStart,
            FocusMinutes = 5,
            Groups = ["学习经济学"],
        };
        List<AwEvent> win = [new(utcStart, 1200, "SumatraPDF.exe", "曼昆经济学.pdf", null)];
        List<AwEvent> afk = [new(utcStart, 1200, null, null, "not-afk")];

        var s = Replay.Run(task, rules, win, afk, utcStart.AddMinutes(20));

        Assert.NotNull(s.FocusCompletedAt);
        // 推导出来的时刻保留了事件的偏移量（这里是 UTC），直接格式化就会显示 UTC 时钟
        Assert.Equal(TimeSpan.Zero, s.FocusCompletedAt.Value.Offset);
        // 但它作为绝对时刻是对的——转本地之后才是给人看的那个数
        Assert.Equal(utcStart.AddMinutes(5), s.FocusCompletedAt.Value);
    }

    [Fact]
    public void 不同偏移量的同一时刻相等_核算不受影响()
    {
        var utc = new DateTimeOffset(2026, 7, 27, 6, 40, 45, TimeSpan.Zero);
        var local = new DateTimeOffset(2026, 7, 27, 14, 40, 45, TimeSpan.FromHours(8));

        Assert.Equal(utc, local);                       // 同一个瞬间
        Assert.NotEqual(utc.ToString("HH:mm:ss"), local.ToString("HH:mm:ss")); // 但显示出来不一样
    }
}
