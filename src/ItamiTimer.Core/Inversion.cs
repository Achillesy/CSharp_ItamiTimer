namespace ItamiTimer.Core;

/// <summary>
/// 跑偏就把钟面反色（DESIGN §8.9）：判断"最近这一小段里有没有跑偏"。
///
/// **纯函数，不碰时钟也不碰网络**——`now` 和事件都由调用方传进来，跟
/// <see cref="Judgment"/>、<see cref="GroupRules"/> 一样直接可测。
///
/// ⚠️ **绝不反哺判定**。这里画的是一个**临时的 5 格数组**，画完就扔；表盘上那一格是红是
/// 绿，只由 <see cref="JudgmentBuffer"/> 每整分钟重放出来（§4）。
/// **尤其不能改成调 <see cref="JudgmentBuffer.Cover"/>**：那个方法锚死在整分钟上
/// （DECISIONS H9——掺进亚秒零头会让边界秒来回翻面、写入偏移整段错位），而且它会写进
/// 任务的 buffer，那是账本。要复用的是下面一层的 <see cref="Judgment.Paint"/>。
/// </summary>
public static class Inversion
{
    /// <summary>
    /// 窗口末端离 `now` 有多远。
    ///
    /// **这 15 秒是全部关键**：AW 的窗口事件滞后 `now` 约 6~12 秒（§7.2 的 T3），
    /// 窗口若贴着 `now` 结束，尾巴上那 6~12 秒必然一条事件都没有 = <see cref="JudgmentCode.AwOffline"/>
    /// = 按约定**算专注**（§3.1 的知情 fail-open），判据就成了大半由 fail-open 约定决定。
    /// 退后 15 秒，这 5 秒的数据已经落定，一点水分都没有。
    ///
    /// ⚠️ 改小到 12 以下就会重新吃进那段滞后，而且**不报错**——只是变得越来越不敏感。
    /// </summary>
    public const int LagSeconds = 15;

    /// <summary>窗口宽度。</summary>
    public const int SpanSeconds = 5;

    /// <summary>
    /// 采样节拍，**故意跟 <see cref="SpanSeconds"/> 相等**：5 秒宽的窗口每 5 秒滑 5 秒
    /// ＝ 前后两次首尾相接、**无缝分块**，任何一秒都不会漏掉，也不会被数两次。
    ///
    /// 每秒采一次也是对的，但每次查询实际会把过去 6 小时的事件整个拉回来再裁
    /// （<see cref="AwClient.FetchEventsAsync"/> 为了绕过 T1 统一往前放宽 6 小时），
    /// 那是现在这个节拍的 5 倍开销，换来的只是反色的起止时刻更平滑（用户 2026-08-29 定）。
    /// </summary>
    public const int SampleSeconds = 5;

    /// <summary>这一次采样要看的那 5 秒：<c>[now-20s, now-15s)</c>，`now` 先truncate 到整秒。</summary>
    public static (DateTimeOffset From, DateTimeOffset To) WindowFor(DateTimeOffset now)
    {
        var sec = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Offset);
        var to = sec.AddSeconds(-LagSeconds);
        return (to.AddSeconds(-SpanSeconds), to);
    }

    /// <summary>
    /// 判据：**这一段里存在 <see cref="JudgmentCode.OffTask"/> 秒**就反色。
    ///
    /// ⚠️ **绝不能写成"存在不是 <see cref="JudgmentCode.Focused"/> 的秒"**。扣掉 afk 之后
    /// 剩下的只有三种码，而 <see cref="JudgmentCode.AwOffline"/>（这一秒 AW 一条事件都没有）
    /// 按约定**算专注**。写成"不是 Focused 就反色"的后果是：`aw-watcher-window` 一死，
    /// 整段全是 AwOffline，**钟面永久反色，而账本这段时间全判绿**——屏幕和账本对着说反话，
    /// 正是这个功能要避免的事。写成 `== OffTask`，watcher 死掉时它安静地不反色，跟 AW 整个
    /// 连不上是同一个结果。
    ///
    /// **afk 是白拿的**：<see cref="Judgment.Paint"/> 把 afk 画在最后、覆盖一切，所以人不在座
    /// 的那些秒根本不会以 OffTask 的身份留在数组里——"扣掉 afk"这一步由现成的覆盖顺序
    /// 完成，这里一个字都不用写。整段都是 afk 时自然就没有 OffTask，不反色。
    /// </summary>
    public static bool ShouldInvert(ReadOnlySpan<JudgmentCode> span)
    {
        foreach (var c in span)
            if (c == JudgmentCode.OffTask) return true;
        return false;
    }

    /// <summary>
    /// 一次完整的判断：把 <paramref name="windowEvents"/> / <paramref name="afkEvents"/>
    /// 画进一个临时的 5 格数组，再问 <see cref="ShouldInvert"/>。
    ///
    /// 初值是 <see cref="JudgmentCode.AwOffline"/>，跟 <see cref="JudgmentBuffer.Cover"/>
    /// 的第 (1) 步同一个约定：没有记录 ≠ 跑偏。
    ///
    /// 事件列表按 <see cref="AwClient.FetchEventsAsync"/> 的惯例可以宽于窗口，
    /// <see cref="Judgment.Paint"/> 自己会裁。
    /// </summary>
    public static bool Evaluate(
        DateTimeOffset now,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        var (from, _) = WindowFor(now);

        Span<JudgmentCode> slice = stackalloc JudgmentCode[SpanSeconds];
        slice.Fill(JudgmentCode.AwOffline);
        Judgment.Paint(slice, from, windowEvents, afkEvents, rules, selectedGroup);

        return ShouldInvert(slice);
    }
}
