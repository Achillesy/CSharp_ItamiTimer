namespace ItamiTimer.Core;

/// <summary>
/// 一段区间的分类结果（DESIGN.md §4）。
///
/// 重放算法把 [startedAt, now) 切成一串互不重叠的区间，每段贴一个这样的标签。
/// 只有 <see cref="OnTask"/> 和 <see cref="Neutral"/> 计入累计专注时长。
/// </summary>
public enum IntervalKind
{
    /// <summary>命中当前已勾选的某个小目标。计入。</summary>
    OnTask,

    /// <summary>
    /// 中性：命中 ignore 名单，或者是 ItamiTimer 自己。**计入**。
    /// 文件对话框、explorer、瞄一眼本程序——都是干活的附带动作，
    /// 为它们停表既琐碎又制造噪音。
    /// </summary>
    Neutral,

    /// <summary>
    /// 其它（含规则文件里根本没有的应用）。不计入，**触发置顶提醒**。
    /// 不计入但也不惩罚——痛感来自任务被拖长，不来自作废重来。
    /// </summary>
    OffTask,

    /// <summary>
    /// AW 的 afk 数据说人不在。不计入，**不提醒**（人都不在，谈不上违规）。
    ///
    /// 优先级高于一切（§4）：锁屏时 LockApp.exe 在 ignore 名单里（本该 Neutral、
    /// 计入）而 afk 同时说 afk——必须判 Absent，否则就是"锁屏一小时专注时长
    /// 照涨"的漏洞。
    /// </summary>
    Absent,

    /// <summary>
    /// 这段区间 AW 根本没有数据（aw-server 宕了、系统休眠边界等）。
    /// 既不计入也不惩罚，如实报告（§6.3）。
    ///
    /// 注意 afk bucket 缺数据也算 Gap，**不能**当成"在座"——那会把
    /// "停在目标应用上起身走开"这条最省力的作弊路径重新打开（§6.1.1）。
    /// </summary>
    Gap,
}
