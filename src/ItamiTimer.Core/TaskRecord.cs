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
/// 一次勾选变更的审计记录（§5.4）。
///
/// **纯显示用，绝不参与判定。** 删掉它不影响任何数字——重放只看
/// <see cref="TaskRecord.Groups"/> 的当前值。它存在的唯一理由是让最终账单
/// 能写出那行「中途添加小目标 1 次  10:23 加入了「学习 Blender」」。
/// </summary>
/// <param name="At">变更发生的时刻。</param>
/// <param name="Groups">变更**之后**的完整勾选集合。</param>
public readonly record struct GroupChange(DateTimeOffset At, IReadOnlyList<string> Groups);

/// <summary>
/// 已提交任务的持久化模型（DESIGN.md §8 模块 3）。
///
/// 这是程序需要落盘的**全部**东西，很小。只在三个时刻写：提交任务、改勾选、
/// 任务终结（§8.1）。轮询过程中什么都不写——**没有任何可变累加值需要落盘**，
/// 这正是"关掉界面 / 重启电脑不影响结果"的根据（原则 4）。
///
/// 历史数据不在这里，在 aw-server 里。要知道现在什么状态，就拿 StartedAt
/// 向 AW 查区间、重放一遍（§2）。
/// </summary>
public sealed record TaskRecord
{
    /// <summary>
    /// 任务开始时刻，**已进位到整分钟**（<see cref="TimeGrid.CeilToMinute"/>，§14.1）。
    /// UTC 存储，界面显示时再转本地。提交后锁定，永不可变（原则 1）。
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 承诺的专注时长（分钟）。提交后锁定——中途改时长等于移动球门（§5.5）。
    ///
    /// 界面上的滑块限制在 10~50、步进 5（§8.4.2a），但**这里不做范围检查**：
    /// §13 的手动验证要求把时长临时设成 1~2 分钟，下限硬编码进 Core 的话
    /// 每验一次要枯坐 10 分钟。范围约束属于 UI 层。
    /// </summary>
    public required int FocusMinutes { get; init; }

    /// <summary>
    /// 当前勾选的小目标名字（对应 rules.json 里的组名）。
    ///
    /// **⚠️ 这是一个当前值，不是时间线。** 重放时整段历史一律用这个集合的
    /// 规则**并集**打标，改动追溯生效（§5.4）。用户 2026-07-27 明确否决了
    /// 带时间戳的 groupTimeline 方案，理由和放弃的东西见 §5.4.1。
    /// **不要"改进"成时间线。**
    /// </summary>
    public required IReadOnlyList<string> Groups { get; init; }

    /// <summary>勾选变更的审计记录。纯显示用，见 <see cref="GroupChange"/>。</summary>
    public IReadOnlyList<GroupChange> GroupChanges { get; init; } = [];

    public RecordStatus Status { get; init; } = RecordStatus.Committed;

    /// <summary>仅当 <see cref="Status"/> 为 Abandoned 时有值。</summary>
    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>
    /// 休息时长（分钟）= 专注 ÷ 5（§8.4.2）。推导而不是存储：滑块步进 5
    /// 保证了它恒为整数分钟（10→2、25→5、50→10），不需要取整规则。
    ///
    /// 注意验证用的短任务（FocusMinutes = 1~2）会算出 0 分钟休息，即专注
    /// 达成后立刻 Completed。这对测试正好方便，不是 bug。
    ///
    /// **不落盘**：推导值写进 JSON 会让人以为它可以手改，改了又不生效。
    /// </summary>
    [JsonIgnore]
    public int RestMinutes => FocusMinutes / 5;
}
