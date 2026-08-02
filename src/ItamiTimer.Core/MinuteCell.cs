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
/// <b>不给颜色</b>——怎么上色是渲染层的事（§8 第四条纪律）：CLI 渲染成 ANSI 色块、
/// 表盘渲染成色环。「色块只为好看，不是账」能成立就是靠这一条：判定层从头到尾不知道
/// 什么是绿什么是红。
///
/// <b>但给档位</b>（<see cref="Tier"/>，2026-08-02 加）。原来这里写着「也不给档位」，
/// 结果 §4.6 那套规则在 CLI 和表盘**各写了一遍**——三档阈值、argmax、平局取大值，
/// 一字不差地重复。两份现在恰好一致，可改一处忘另一处不会报错，跟 `executeCommand`
/// 两条读取路径是同一个病（§15.4）。
///
/// 分界线是这样划的：**「这一格该读成什么」是判定，「读成这样该画成什么」才是渲染。**
/// 想回到连续编码的渲染层照样可以无视 <see cref="Tier"/>、直接用原始计数。
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
    int InitSeconds)
{
    /// <summary>
    /// 这一格该读成什么（DESIGN.md §4.6）。**规则只写在这里一处。**
    ///
    /// 有 focus 就按 <c>&gt;40 / &gt;20 / &gt;0</c> 分三档；一秒 focus 都没有时，
    /// 在其余四类里取<b>计数最大</b>的，平局取<b>码值大</b>的
    /// （OffTask &gt; Afk &gt; Gray &gt; Init，fail-closed）。
    ///
    /// argmax 而不是「过半」：三类混合时可能谁都不过半，按阈值写会默认掉进红色，
    /// 于是「离开 29 秒 + 摸鱼 28 秒」被整格判成全红——<b>把起身离开画成红色等于
    /// 冤枉自己</b>（§0.4.1）。argmax 没有阈值，也就没有那道悬崖。
    /// </summary>
    public CellTier Tier
    {
        get
        {
            if (FocusSeconds > 40) return CellTier.FocusFull;
            if (FocusSeconds > 20) return CellTier.FocusMid;
            if (FocusSeconds > 0) return CellTier.FocusLow;

            var best = InitSeconds;
            var pick = CellTier.NotDrawn;
            if (GraySeconds >= best) { best = GraySeconds; pick = CellTier.Pending; }
            if (AfkSeconds >= best) { best = AfkSeconds; pick = CellTier.Away; }
            if (OffTaskSeconds >= best) pick = CellTier.OffTask;
            return pick;
        }
    }
}

/// <summary>
/// 一格的读法（§4.6）。**顺序就是「谁盖谁」的顺序**，跟 <see cref="JudgmentCode"/> 一样：
/// 平局时取靠后的那个。
/// </summary>
public enum CellTier : byte
{
    /// <summary>没画过（漏拍留的洞）。什么都不画。</summary>
    NotDrawn,

    /// <summary>承诺弧：还没走到。</summary>
    Pending,

    /// <summary>人不在。不计入，也不怪你。</summary>
    Away,

    /// <summary>有窗口事件但不命中。</summary>
    OffTask,

    /// <summary>1~20 秒专注。</summary>
    FocusLow,

    /// <summary>21~40 秒专注。</summary>
    FocusMid,

    /// <summary>41~60 秒专注。</summary>
    FocusFull,
}
