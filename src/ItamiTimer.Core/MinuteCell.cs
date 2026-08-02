namespace ItamiTimer.Core;

/// <summary>
/// 色环上的一格 = 一分钟（DESIGN.md §4.6）。**判定层和渲染层之间唯一的契约。**
///
/// <b>这是 buffer 的一个投影，不是一个逐分钟累加的数组</b>（原则 4）。判据：
/// 关掉界面再打开，每一格的颜色都要能原样重建。所以它没有任何自己的状态——
/// 每拍从 <see cref="JudgmentBuffer"/> 重新算一遍。
///
/// <b>五个计数跟 <see cref="JudgmentCode"/> 一一对应</b>，加起来恒为 60：起点截断到
/// 整分钟（A6）、`ToMinuteCells` 只吐完整的分钟，所以不存在「末格不足 60 秒」那种情况。
///
/// <b>刻意只给原始秒数，不给颜色，也不给档位。</b> 怎么上色是渲染层的事
/// （§8 第四条纪律）：CLI 渲染成 ANSI 色块、表盘渲染成色环，两边消费同一个列表、
/// 各自决定怎么分档。「色块只为好看，不是账」能成立就是靠这一条——判定层从头到尾
/// 不知道什么是绿什么是红。
///
/// 保留原始计数而不是只存档位，还有一层意思：表盘将来想回到 D1 那种**连续**的木桶
/// 编码时，数据还在，不必改判定层。
/// </summary>
/// <param name="Index">
/// 从 0 开始，第 i 格覆盖 <c>[任务起点 + i 分钟, +1 分钟)</c>。
/// **它同时是圈号的来源**（<c>Index / 60</c> → lane 0 或 1，§8.3）。
/// ⚠️ 归档之后任务起点会往前走一小时（§4.4），`Index` 因此从 0 重来——那是**对的**，
/// 归档存在的理由就是让盘面永远只画最近的一到两小时。
/// </param>
/// <param name="Start">
/// 这一格的起始时刻，天然落在整分钟上——所以「分针就是写入头」（§8.2.2），
/// 角度直接从它来。
/// </param>
/// <param name="FocusSeconds">码 ≥ <see cref="JudgmentCode.Focused"/>，即计入专注的部分。</param>
/// <param name="OffTaskSeconds">有窗口事件但不命中小目标。红。</param>
/// <param name="AfkSeconds">afk 说人不在。不计入，但也不怪你——虚线空心框。</param>
/// <param name="GraySeconds">承诺弧：还没走到、预计还要花的时间。</param>
/// <param name="InitSeconds">没画过（漏拍留下的洞）。什么都不画。</param>
public readonly record struct MinuteCell(
    int Index,
    DateTimeOffset Start,
    int FocusSeconds,
    int OffTaskSeconds,
    int AfkSeconds,
    int GraySeconds,
    int InitSeconds);
