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
    /// 休息时长（分钟）= <b>⌈专注 ÷ 5⌉</b>（DESIGN.md §6.1，用户 2026-08-02 定稿）。
    ///
    /// <b>只读提交时锁定的 <see cref="FocusMinutes"/>，跟这一轮实际拖了多久毫无关系</b>：
    /// 哪怕跨过两次归档（§4.4），50 分钟的任务照样歇 10 分钟。归档扣减的是「剩余目标」，
    /// 那是另一个量。拿剩余目标算休息 = 拖得越久歇得越少，激励方向就反了（DECISIONS H6）。
    ///
    /// 原公式是 <c>⌊focus/5⌋+1</c>，那个 +1 有两个职责，<b>现在两个都没了</b>：
    ///
    /// <b>① 补「发现延迟」</b>——旧的休息从<b>账本里推导出的</b>达成时刻起算，比发现时刻
    /// 早最多 60 秒。第二版的达成时刻<b>就是发现的那一拍</b>（§4.5），没有延迟可补。
    ///
    /// <b>② 保证任何非零时长都有休息</b>——<c>⌊focus/5⌋</c> 在 focus &lt; 5 时算出 0，
    /// 休息阶段整个不存在、休息扇形永远看不见。<c>⌈focus/5⌉</c> 在 focus ≥ 1 时恒 ≥ 1，
    /// 这个补丁不需要了。
    ///
    /// 所以现在是干净的「精确五分之一」：10→2、25→5、50→10。
    ///
    /// ⚠️ 旧的 +1 还藏着一个从没写进文档的前提：<b>滑块只出 5 的倍数</b>。2026-07-31 把
    /// 滑块改成步进 1 之后，<c>⌊f/5⌋+1</c> 在 8 个取值里破了 6 个，而守它的测试
    /// <c>InlineData</c> 还停在 10/25/50——全是 5 的倍数，恒成立。<b>护栏测试的取值必须
    /// 覆盖滑块实际能出的值。</b>
    ///
    /// Core 不设时长范围（范围约束属于 UI 层），所以这个公式对<b>任意正整数</b>都得成立。
    ///
    /// <b>不落盘</b>：推导值写进 JSON 会让人以为它可以手改，改了又不生效。
    /// </summary>
    [JsonIgnore]
    public int RestMinutes => (FocusMinutes + 4) / 5;
}
