namespace ItamiTimer.Core;

/// <summary>
/// 覆盖算法（DESIGN.md §4.3）：<b>分层画，不逐秒查</b>。
///
/// 纯函数，无 I/O、无时钟。调用方给一段秒级切片和它的起始时刻，这里按码值从大到小
/// 逐层覆盖，后画的盖前面的：
///
/// <code>
/// ② 画 4   命中小目标的窗口事件 → Focused
/// ③ 画 3   其余窗口事件         → OffTask
/// ④ 画 2   status == "afk"      → Afk
/// </code>
/// （① 初始化成 <see cref="JudgmentCode.AwOffline"/> 由 <see cref="JudgmentBuffer"/> 按水位线做，
/// 见 §4.3 第 3 条——它有边界和水位线信息，这里没有。）
///
/// 这四条规则一次办了好几件事，都不用单独写规则：
/// <list type="bullet">
///   <item><b>一秒里有多条事件</b>（alt-tab）→ 后画的赢 → OffTask 压过 Focused → fail-closed。</item>
///   <item><b>afk 优先于一切</b> → 它画在最后，天然盖住所有窗口判定。</item>
///   <item><b>AW 整拍连不上 = 事件列表为空</b> → 没东西可画，切片保持初始化的 AwOffline。
///         不需要单独的兜底代码路径。</item>
/// </list>
///
/// <b>为什么不再需要 T4 的桥接</b>（旧 <c>Replay.Bridge</c>）：旧的逐秒查找问「哪条事件
/// <b>盖住</b>了这一秒」，零时长事件的 <c>[start, start)</c> 是空区间，答案永远是「没有」，
/// 于是标题每秒都在变的窗口（播放器时间码、编译进度、Claude Code 的转圈动画）整段变成
/// 「无记录」。这里问的是「这一秒里<b>出现过</b>什么事件」——零时长事件的时间戳实实在在
/// 落在某一秒里，照样能画。同一份数据换个问法，洞就没了。
/// </summary>
public static class Judgment
{
    /// <summary>
    /// 把事件画进 <paramref name="window"/>。<paramref name="windowStart"/> 是
    /// <c>window[0]</c> 对应的绝对时刻，之后每个元素 +1 秒。
    /// </summary>
    /// <param name="windowEvents">窗口事件（可以含窗口之外的，这里自己裁）。</param>
    /// <param name="afkEvents">afk 事件，同上。</param>
    /// <param name="rules">编译好的规则。</param>
    /// <param name="selectedGroup">当前选中的唯一 goal 名（null = 未选，那就全是 OffTask）。</param>
    public static void Paint(
        Span<JudgmentCode> window,
        DateTimeOffset windowStart,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        if (window.Length == 0) return;

        // 命中与否先算一遍：正则匹配不便宜，而下面要遍历两趟。
        var hit = new bool[windowEvents.Count];
        for (var i = 0; i < windowEvents.Count; i++)
        {
            var e = windowEvents[i];
            hit[i] = selectedGroup is not null
                  && rules.GroupMatches(selectedGroup, e.App ?? "", e.Title ?? "");
        }

        // ② Focused 先画，③ OffTask 后画 —— 顺序就是 tie-break：同一秒里两者都有时
        //    OffTask 赢（fail-closed）。别把这两趟合成一趟。
        for (var i = 0; i < windowEvents.Count; i++)
            if (hit[i]) PaintOne(window, windowStart, windowEvents[i], JudgmentCode.Focused);

        for (var i = 0; i < windowEvents.Count; i++)
            if (!hit[i]) PaintOne(window, windowStart, windowEvents[i], JudgmentCode.OffTask);

        // ④ afk 最后画 —— 人不在的时候窗口是什么无所谓（否则锁屏时长照涨）。
        foreach (var e in afkEvents)
            if (e.Status == "afk") PaintOne(window, windowStart, e, JudgmentCode.Afk);
    }

    /// <summary>
    /// 一条事件画哪几秒：<b>它触碰到的每一秒都画</b>，即 <c>floor(start) … ceil(end)−1</c>。
    ///
    /// <b>零时长事件也占满一秒</b>（T4）：<c>end == start</c> 时 ceil 和 floor 相等，
    /// 这里把区间撑到 1 秒——等价于「默认 duration = 0.001」。
    ///
    /// 边界那一秒会被相邻两条事件同时认领，那正好交给覆盖顺序去裁决：
    /// <b>归属由优先级决定，不由四舍五入决定</b>。代价是跨秒切换最多算错 1 秒，
    /// 方向恒定偏严。
    /// </summary>
    private static void PaintOne(Span<JudgmentCode> window, DateTimeOffset windowStart,
                                 AwEvent e, JudgmentCode code)
    {
        var from = (int)Math.Floor((e.Start - windowStart).TotalSeconds);
        var to = (int)Math.Ceiling((e.End - windowStart).TotalSeconds);
        if (to <= from) to = from + 1;              // 零时长：至少占它落进的那一秒

        if (from < 0) from = 0;                     // 裁到切片范围（6 小时预取，见 T1/F7）
        if (to > window.Length) to = window.Length;
        if (to <= from) return;

        window[from..to].Fill(code);
    }
}
