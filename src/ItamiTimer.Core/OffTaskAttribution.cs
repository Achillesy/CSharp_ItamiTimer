namespace ItamiTimer.Core;

/// <summary>
/// **Diagnostic only, never judgment** (§16.5's family, same spirit as the "no fresh
/// events" staleness check): says *which* window most likely produced a minute's
/// <see cref="MinuteCell.OffTaskSeconds"/>, so the log can answer "why didn't this minute
/// count" without anyone having to go pull ActivityWatch's raw events by hand.
///
/// 2026-08-27: the log line this feeds (<c>TaskSession</c>'s "The minute just past had Ns
/// off-task") existed since before this file did, but a stale <c>cells[^1]</c> index bug
/// meant it always pointed at the commitment arc's grey projection tail, not the minute
/// that actually just happened -- so <see cref="MinuteCell.OffTaskSeconds"/> read there was
/// always 0 and the line had **never once fired** in production (confirmed: zero matches
/// across the entire real log history). Fixed alongside adding this attribution.
///
/// A pure function on the same inputs the real judgment already uses
/// (<see cref="GroupRules.GroupMatches"/>, called exactly the way <c>Judgment.Paint</c>
/// calls it) -- so "why" always agrees with "whether", even though this result is never
/// fed back into the buffer.
/// </summary>
public static class OffTaskAttribution
{
    /// <summary>
    /// Picks whichever (app, title) pair -- among events that do **not** match
    /// <paramref name="group"/> -- overlaps <c>[minuteStart, minuteStart + 60s)</c> the
    /// longest. Returns null when nothing overlaps at all (the off-task seconds came from a
    /// gap in event coverage, not a specific window -- the "No fresh window events" warning
    /// already flags that case, no need to repeat it here).
    /// </summary>
    public static string? Attribute(
        IReadOnlyList<AwEvent> windowEvents, DateTimeOffset minuteStart, GroupRules rules, string? group)
    {
        var minuteEnd = minuteStart.AddSeconds(60);

        var totals = new Dictionary<(string App, string Title), double>();
        foreach (var e in windowEvents)
        {
            var app = e.App ?? "";
            var title = e.Title ?? "";
            // 跟 Judgment.Paint 同一条空值语义：没有选中的小目标 = 什么都不算命中
            // （`selectedGroup is not null && rules.GroupMatches(...)`），不是另一套判断。
            if (group is not null && rules.GroupMatches(group, app, title)) continue; // on-task, not the culprit

            var start = e.Start > minuteStart ? e.Start : minuteStart;
            var end = e.End < minuteEnd ? e.End : minuteEnd;
            var overlap = (end - start).TotalSeconds;
            if (overlap <= 0) continue;

            var key = (app, title);
            totals[key] = totals.TryGetValue(key, out var sofar) ? sofar + overlap : overlap;
        }
        if (totals.Count == 0) return null;

        var (topApp, topTitle) = totals.Aggregate((best, next) => next.Value > best.Value ? next : best).Key;
        if (topApp.Length == 0 && topTitle.Length == 0) return null;
        if (topTitle.Length == 0) return topApp;
        if (topApp.Length == 0) return topTitle;
        return $"{topApp} \"{topTitle}\"";
    }
}
