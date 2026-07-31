namespace ItamiTimer.Core;

/// <summary>
/// 判定模型（ISSUE_FIX.md §7）。
///
/// 纯函数，无 I/O、无时钟。输入：AW 窗口事件 + afk 事件 + 选中的 group + 当前时刻。
/// 输出：一段 240 秒的分类结果（逐秒状态码），由调用方写入 <see cref="JudgmentBuffer"/>。
///
/// 分类逻辑（简化自 §5.3，去掉了 Neutral / ignore / 自身豁免）：
/// <list type="number">
///   <item>afk 事件覆盖的秒 → <see cref="JudgmentBuffer.Afk"/></item>
///   <item>匹配选中 group → <see cref="JudgmentBuffer.Focused"/></item>
///   <item>其余 → <see cref="JudgmentBuffer.OffTask"/></item>
/// </list>
/// </summary>
public static class Judgment
{
    /// <summary>
    /// 查询 AW 并分类的结果。调用方负责写入 buffer。
    /// </summary>
    /// <param name="windowEvents">窗口事件列表（从 AW 取回，已按 Start 排序）。</param>
    /// <param name="afkEvents">AFK 事件列表。</param>
    /// <param name="rules">编译好的规则。</param>
    /// <param name="selectedGroup">当前选中的唯一 goal 名（null = 未选）。</param>
    /// <param name="queryStart">本次查询的时间窗口起点（= now − 4min）。</param>
    /// <param name="queryEnd">本次查询的时间窗口终点（= now）。</param>
    /// <returns>byte[240]，queryStart 起的逐秒分类结果。</returns>
    public static byte[] ClassifySeconds(
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup,
        DateTimeOffset queryStart,
        DateTimeOffset queryEnd)
    {
        var n = (int)(queryEnd - queryStart).TotalSeconds;
        if (n <= 0) return [];
        var result = new byte[n];

        for (var i = 0; i < n; i++)
        {
            var t = queryStart.AddSeconds(i);
            result[i] = ClassifyMoment(windowEvents, afkEvents, rules, selectedGroup, t);
        }

        return result;
    }

    /// <summary>
    /// 判某一秒属于什么状态。
    /// afk 优先级最高 → 匹配 group → 其余 OffTask。
    /// </summary>
    private static byte ClassifyMoment(
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup,
        DateTimeOffset t)
    {
        // 1. AFK 优先：人不在 → Afk
        var afkEv = CoveringAt(afkEvents, t);
        if (afkEv is { Status: "afk" })
            return JudgmentBuffer.Afk;

        // 2. 窗口事件：匹配选中 group → Focused
        var win = CoveringAt(windowEvents, t);
        if (win is not null && selectedGroup is not null)
        {
            var app = win.Value.App ?? "";
            var title = win.Value.Title ?? "";
            if (rules.GroupMatches(selectedGroup, app, title))
                return JudgmentBuffer.Focused;
        }

        // 3. 有窗口事件但不匹配 → OffTask
        if (win is not null)
            return JudgmentBuffer.OffTask;

        // 4. 窗口事件缺失 → AW 脱机（默认算专注）
        //    注意：afk 事件存在且为 not-afk 时也算 AW 脱机——因为 window bucket 没数据
        //    但 afk 说人在
        return JudgmentBuffer.AwOffline;
    }

    /// <summary>
    /// AW 脱机时的退化分类：全段标记为 AwOffline。
    /// 查询不到 AW 时直接调用这个方法。
    /// </summary>
    public static byte[] AwOfflineFallback(int lengthSeconds)
    {
        var result = new byte[lengthSeconds];
        Array.Fill(result, JudgmentBuffer.AwOffline);
        return result;
    }

    /// <summary>
    /// 在已排序的事件列表里找覆盖时刻 t 的那条。找不到返回 null。
    /// 同 <see cref="Replay.CoveringAt"/>。
    /// </summary>
    private static AwEvent? CoveringAt(IReadOnlyList<AwEvent> sorted, DateTimeOffset t)
    {
        foreach (var e in sorted)
        {
            if (e.Start > t) break;
            if (e.Start <= t && t < e.End) return e;
        }
        return null;
    }
}
