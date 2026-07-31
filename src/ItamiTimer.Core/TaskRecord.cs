using System.Text.Json.Serialization;

namespace ItamiTimer.Core;

/// <summary>任务的终局状态。中间不存在"状态机状态"——阶段是推导出来的（§3）。</summary>
public enum RecordStatus
{
    /// <summary>已提交，尚未终结。</summary>
    Committed,

    /// <summary>专注达成、休息也走完了。到此终结，绝不自动开下一轮（原则 1）。</summary>
    Completed,

    /// <summary>用户中途放弃。</summary>
    Abandoned,
}

/// <summary>
/// 已提交任务的持久化模型（DESIGN.md §8 模块 3 / ISSUE_FIX.md §7）。
///
/// Radio 单选一个 goal，Start 后锁定不可改。
/// </summary>
public sealed record TaskRecord
{
    /// <summary>
    /// 任务开始时刻，**截断到整分钟**（<see cref="TimeGrid.FloorToMinute"/>，§14.1）。
    /// 提交后锁定，永不可变（原则 1）。
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 承诺的专注时长（分钟）。提交后锁定。
    /// </summary>
    public required int FocusMinutes { get; init; }

    /// <summary>
    /// 当前选中的小目标名字（对应 rules.json 里的组名）。
    /// Radio 单选，Start 后锁定。
    /// </summary>
    public required string? Group { get; init; }

    public RecordStatus Status { get; init; } = RecordStatus.Committed;

    /// <summary>仅当 <see cref="Status"/> 为 Abandoned 时有值。</summary>
    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>
    /// 休息时长（分钟）= <b>⌊专注 ÷ 5⌋ + 1</b>（§8.4.2，用户 2026-07-28 定稿）。
    ///
    /// 那个 <b>+1 不是慷慨，是补偿</b>，它一次解决两件事：
    ///
    /// <b>① 补上"发现延迟"</b>（§14.0a）。专注是在某个真实时刻攒够的（比如 01:14:19），
    /// 但程序要到下一个整分钟的计时点才发现（01:15:00）。而休息是从<b>真正达成那一刻</b>
    /// 起算的，于是用户实际能歇的比名义时长少了一截。这个延迟被计时点间隔<b>封死在
    /// 60 秒以内</b>，所以整整加 1 分钟就能保证：<b>实际休息永不少于名义时长</b>。
    ///
    /// <b>② 任何非零时长都有休息</b>。原来用整除，`FocusMinutes ≤ 4` 全部算出 0 分钟，
    /// 休息阶段整个不存在、休息扇形（§8.4.4）永远看不见。Core 必须接受任意时长
    /// （§8.4.2a：范围约束属于 UI 层，§13 的手动验证会用 1~2 分钟的任务跑）。
    ///
    /// 滑块量程全是 5 的倍数，所以实际效果就是"该给多少给多少，再多给一分钟"：
    /// 10→3、25→6、50→11。
    ///
    /// ⚠️ <b>这个 +1 跟计时点间隔是绑死的。</b>哪天把查询节拍从 60 秒改成别的，
    /// 这里必须跟着改 —— 补偿量等于延迟的上界，而延迟的上界就是节拍长度。
    ///
    /// <b>不落盘</b>：推导值写进 JSON 会让人以为它可以手改，改了又不生效。
    /// </summary>
    [JsonIgnore]
    public int RestMinutes => FocusMinutes / 5 + 1;
}
