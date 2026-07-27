namespace ItamiTimer.Core;

/// <summary>
/// 整分钟对齐（DESIGN.md §14.1）。纯函数，无时钟——所有时刻都是参数传进来的。
/// </summary>
public static class TimeGrid
{
    /// <summary>
    /// 进位到下一个整分钟。已经在整分钟上就原样返回。
    ///
    /// 任务的 startedAt 用这个（§14.1）。**绝不向后取整**——那会把点击「开始」
    /// 之前的时间也算进来，是追溯发放专注时长。
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
    /// 核算区间的**末端**用这个（§14.2）：正在进行的那一分钟不画，等它走完再画，
    /// 否则那一格会随着秒数变化不停闪。
    /// </summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset t)
        => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}
