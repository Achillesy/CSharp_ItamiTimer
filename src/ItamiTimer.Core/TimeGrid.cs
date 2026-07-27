namespace ItamiTimer.Core;

/// <summary>
/// 整分钟对齐（DESIGN.md §14.1）。纯函数，无时钟——所有时刻都是参数传进来的。
/// </summary>
public static class TimeGrid
{
    /// <summary>
    /// 进位到下一个整分钟。已经在整分钟上就原样返回。
    ///
    /// ⚠️ **任务的 startedAt 不再用这个**（2026-07-27 变更，见 <see cref="FloorToMinute"/>）。
    /// 原设计用进位，理由是不把点击之前的时间算进来；用户改成截断，理由是不想干等。
    /// 这个函数留着备用。
    ///
    /// 这一条一次消掉两个坑：
    ///   1. 每个色块恒为完整 60 秒。否则 startedAt 落在 10:08:37 时，第 0 格
    ///      [10:08, 10:09) 只有 23 秒属于任务；纯度分母若按 60 秒算，第 0 格
    ///      永远最多绿 38%，看着像一开局就在偷懒，而且极难查。
    ///   2. 顺带给了一个不到 60 秒的缓冲：点完开始还来得及切到目标应用。
    /// </summary>
    public static DateTimeOffset CeilToMinute(DateTimeOffset t)
    {
        var floored = FloorToMinute(t);
        return floored == t ? t : floored.AddMinutes(1);
    }

    /// <summary>
    /// 抹掉秒和更细的部分，落到当前所在的那个整分钟。
    ///
    /// 两处都用它：
    ///
    /// **任务的 startedAt**（2026-07-27 用户改定）。23:13:10 点的开始 → 23:13:00 起算，
    /// 而不是 23:14:00。代价说清楚：这会把点击**之前**最多 59 秒也算进来。原设计用
    /// 进位正是为了避免这一点，但那样用户点完要干等最多 59 秒才开始 —— 用户选了不等。
    /// 每格仍然是完整的 60 秒，因为起点照样落在整分钟上。
    ///
    /// **核算区间的末端**（§14.2）：正在进行的那一分钟不画，等它走完再画，
    /// 否则那一格会随着秒数变化不停闪。
    /// </summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset t)
        => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}
