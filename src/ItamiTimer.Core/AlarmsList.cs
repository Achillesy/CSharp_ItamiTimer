using System.Globalization;
using System.Text.RegularExpressions;

namespace ItamiTimer.Core;

/// <summary>一条打卡提醒：整分钟的时刻 + 原样进系统通知的文字。</summary>
public readonly record struct AlarmEntry(DateTime At, string Text);

/// <summary>
/// Alarms 清单（DESIGN §17）：解析 <c>alarms.md</c>、挑出该响哪一条。**纯函数，`now` 永远
/// 是参数**，跟 <see cref="AlarmClock"/> 一样的路数，不用等真实时间就能测。
///
/// 清单由外部生成，本类只管执行不管建立——不认识周期规则，只认展开好的具体时间戳；
/// 不回写、不清理，过期的行永远留在文件里，只在内存里按"是不是未来"过滤。
/// </summary>
public static class AlarmsList
{
    /// <summary>
    /// 只认 <c>- [ ] YYYY-MM-DD HH:mm 文字</c> 或 <c>- [x] ...</c>。别的行——标题、空行、
    /// 随笔、HTML 注释——一律当装饰忽略，不需要专门定义注释语法，因为"非清单格式的行"
    /// 本身就是天然的注释。
    /// </summary>
    private static readonly Regex Line = new(
        @"^\s*-\s*\[([ xX])\]\s*(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2})\s+(.*?)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析整份文件内容。<c>[x]</c>（勾选）恒被排除，不管是不是还没到——想临时不响某一条，
    /// 去清单里点一下勾选框就行，不用删行也不用整行注释掉，仍然是"改数据源"这个动作。
    ///
    /// 单行解析失败（日期/时间写错）等同于"不认识这一行"，直接跳过——不拖累整份文件，
    /// 也不需要报错：清单里能有多少种不是提醒的文字，程序完全不关心。
    /// </summary>
    public static IReadOnlyList<AlarmEntry> Parse(string text)
    {
        var result = new List<AlarmEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Line.Match(line);
            if (!m.Success) continue;
            if (m.Groups[1].Value is "x" or "X") continue;   // 勾选 = 永久跳过

            var stamp = $"{m.Groups[2].Value} {m.Groups[3].Value}";
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd H:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                continue;   // 时间格式错了，当成不认识的行

            var label = m.Groups[4].Value;
            if (label.Length == 0) continue;   // 没有文字的提醒没有意义，跳过

            result.Add(new AlarmEntry(at, label));
        }
        return result;
    }

    /// <summary>最早的一条未来条目；清单为空或全部已过去时返回 null。</summary>
    public static AlarmEntry? Next(IReadOnlyList<AlarmEntry> entries, DateTime now)
    {
        AlarmEntry? best = null;
        foreach (var e in entries)
            if (e.At > now && (best is not { } b || e.At < b.At))
                best = e;
        return best;
    }

    /// <summary>
    /// (after, now] 区间内到点的条目，按时间排序。**调用方自己推进 <paramref name="after"/>
    /// 这个水位线**（本类不持有任何状态）——纯内存、一次性，程序一关就没了也无所谓：
    /// 已经定了"不补响、只看未来"，重启后本来就该从"此刻往后"重新数，不需要跨会话持久化
    /// （跟闹钟的 `_fired` 不是同一类东西，见 DESIGN §17）。
    /// </summary>
    public static IReadOnlyList<AlarmEntry> Due(IReadOnlyList<AlarmEntry> entries, DateTime after, DateTime now)
    {
        var due = new List<AlarmEntry>();
        foreach (var e in entries)
            if (e.At > after && e.At <= now)
                due.Add(e);
        due.Sort((a, b) => a.At.CompareTo(b.At));
        return due;
    }

    /// <summary>
    /// 表盘小红圈的角度位置（0-719 分钟，跟 <see cref="AlarmClock.Position"/> 同一个换算：
    /// 时间点对 12 小时取余）。**只在下一条落在未来 12 小时以内时才返回非 null**——
    /// 黄针的 mod-12 换算从不骗人，前提是被取余的数从没超过 12 小时（<see cref="AlarmClock.NextRing"/>
    /// 保证这一点）；Alarms 清单的"下一条"没有这个保证，直接取余会把"5 天后 14 点"画成
    /// "2 点钟方向"、看着像"再等 6 小时"，这个误导正是要避开的（DESIGN §17）。
    /// </summary>
    public static double? DotPosition(AlarmEntry? next, DateTime now)
    {
        if (next is not { } n) return null;
        if (n.At - now >= TimeSpan.FromHours(12)) return null;
        return (n.At.Hour % 12) * 60 + n.At.Minute;
    }
}
