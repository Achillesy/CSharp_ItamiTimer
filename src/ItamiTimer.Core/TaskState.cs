namespace ItamiTimer.Core;

/// <summary>
/// 任务此刻处在哪个阶段。**这是推导出来的分类，不是机器状态**（§3、§12）——
/// 每次重放重新算，进程内不持有它。
/// </summary>
public enum TaskPhase
{
    /// <summary>
    /// now &lt; StartedAt。
    ///
    /// ⚠️ 2026-07-27 起**实际上不可达**：起点改成截断到当前整分钟（§14.1），
    /// 于是 now 永远 ≥ StartedAt。保留这个枚举值是为了让重放对合成事件仍然是全函数
    /// （测试可以喂一个未来的 StartedAt），不是给真实任务用的。
    /// </summary>
    NotStarted,

    /// <summary>专注阶段，此刻在做正事。</summary>
    Focusing,

    /// <summary>专注阶段，此刻跑偏了 → **置顶提醒**。</summary>
    Slacking,

    /// <summary>专注阶段，此刻人不在。不提醒。</summary>
    Away,

    /// <summary>专注阶段，但最后一段区间没有 AW 数据。不提醒，也不显示编造的进度（§6.2）。</summary>
    NoData,

    /// <summary>专注已达成，休息中。此阶段无任何约束，**不需要访问 AW**（§3.1）。</summary>
    Resting,

    /// <summary>休息也走完了。停在这里等用户，绝不自动开下一轮（原则 1）。</summary>
    Completed,
}

/// <summary>切成一段一段之后的分类结果，重放的中间产物。</summary>
/// <param name="App">Gap 区间为 null。</param>
public readonly record struct ClassifiedInterval(
    DateTimeOffset Start,
    DateTimeOffset End,
    IntervalKind Kind,
    string? App,
    string? Title)
{
    public double Seconds => (End - Start).TotalSeconds;
}

/// <summary>一次连续的偷懒（§7 第 6 步：OffTask 区间的极大连续段）。用于报"偷懒 5 次"。</summary>
public readonly record struct ViolationRun(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => (End - Start).TotalSeconds;
}

/// <summary>
/// 重放的输出（DESIGN.md §7）。
///
/// 全部由 <c>纯函数(任务记录, AW 事件历史, now)</c> 算出，没有任何一项是攒出来的
/// （原则 4）。所以 §7.1 那份账单是**免费**的——不需要额外记账。
/// </summary>
public sealed record TaskState
{
    /// <summary>算这份状态时用的 now。重放是纯函数，now 是参数不是时钟读数。</summary>
    public required DateTimeOffset Now { get; init; }

    public required TaskPhase Phase { get; init; }

    /// <summary>切好并分类的全部区间，按时间排序、互不重叠。<see cref="MinuteCell"/> 由它投影而来。</summary>
    public required IReadOnlyList<ClassifiedInterval> Intervals { get; init; }

    /// <summary>累计专注时长 = Σ(OnTask ∪ Neutral) 的秒数（§7 第 4 步）。</summary>
    public required double FocusedSeconds { get; init; }

    /// <summary>
    /// 累计跨过承诺时长的那一瞬间，由区间内**插值**算出（§7 第 5 步）。未达成则为 null。
    ///
    /// 一般落在某分钟中间（比如 10:33:17），**不进位**（§14.4）——色环最后一格
    /// 按实际长度画短一截即可。进位会造成"账已结清但还得再撑 43 秒"这种没有
    /// 好答案的局面。
    /// </summary>
    public DateTimeOffset? FocusCompletedAt { get; init; }

    /// <summary>= FocusCompletedAt + 休息时长。纯算术，休息阶段不需要访问 AW（§3.1）。</summary>
    public DateTimeOffset? RestEndsAt { get; init; }

    /// <summary>偷懒的极大连续段。数量即"偷懒了几次"。</summary>
    public required IReadOnlyList<ViolationRun> Violations { get; init; }

    /// <summary>偷懒时长按应用汇总，用于账单里那几行明细。</summary>
    public required IReadOnlyDictionary<string, double> OffTaskSecondsByApp { get; init; }

    public required double AbsentSeconds { get; init; }

    /// <summary>AW 没有数据的合计。既不计入也不惩罚，但要如实报出来（§6.3）。</summary>
    public required double GapSeconds { get; init; }
}
