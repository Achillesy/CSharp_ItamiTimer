namespace ItamiTimer.Core;

/// <summary>
/// 整分钟对齐（DESIGN.md §14.1）。纯函数，无时钟——所有时刻都是参数传进来的。
/// </summary>
public static class TimeGrid
{
    /// <summary>
    /// 抹掉秒和更细的部分，落到当前所在的那个整分钟。
    ///
    /// 两处都用它：
    ///
    /// **任务的 startedAt**（2026-07-27 用户改定，DECISIONS A6）。23:13:10 点的开始
    /// → 23:13:00 起算，而不是 23:14:00。代价说清楚：这会把点击**之前**最多 59 秒
    /// 也算进来。
    ///
    /// 原设计是**进位**，正是为了避免那 59 秒；但那样用户点完要干等最多 59 秒才开始
    /// —— 用户选了不等。（`CeilToMinute` 因此在 2026-08-02 删掉：它到最后只剩自己的
    /// 测试在用，而那条决策的理由留在这里就够了。）
    ///
    /// 进位还有一个副作用是**这里同样成立**的：每格恒为完整的 60 秒，因为起点照样
    /// 落在整分钟上。
    ///
    /// **核算区间的末端**（§14.2）：正在进行的那一分钟不画，等它走完再画，
    /// 否则那一格会随着秒数变化不停闪。
    /// </summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset t)
        => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}
