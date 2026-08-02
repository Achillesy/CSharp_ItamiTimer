namespace ItamiTimer.Core;

/// <summary>
/// The covering algorithm: <b>layered painting, not a per-second lookup</b>.
///
/// A pure function, no I/O, no clock. The caller hands in a second-level slice and its
/// start time; this covers it layer by layer from the largest code value down to the
/// smallest, later paints covering earlier ones:
///
/// <code>
/// (2) paint 4   Window events matching the goal -> Focused
/// (3) paint 3   All other window events          -> OffTask
/// (4) paint 2   status == "afk"                  -> Afk
/// </code>
/// (Step (1), initializing to <see cref="JudgmentCode.AwOffline"/>, is done by
/// <see cref="JudgmentBuffer"/> using the water mark, see §4.3 point 3 -- it has the
/// boundary and water-mark information this class doesn't.)
///
/// These four rules handle several things at once, with no separate rule needed for any
/// of them:
/// <list type="bullet">
///   <item><b>Multiple events in the same second</b> (alt-tab) -> the later paint wins -> OffTask beats Focused -> fail-closed.</item>
///   <item><b>Afk overrides everything</b> -> painted last, naturally covering every window judgment.</item>
///   <item><b>An entire tick can't reach ActivityWatch = an empty event list</b> -> nothing to paint, the slice stays at its initialized AwOffline. No separate fallback code path needed.</item>
/// </list>
///
/// <b>Why T4's bridging is no longer needed</b> (the old <c>Replay.Bridge</c>): the old
/// per-second lookup asked "which event <b>covers</b> this second" -- a zero-duration
/// event's <c>[start, start)</c> is an empty interval, and the answer was always "none",
/// so windows whose title changes every second (a media player's timestamp, a build's
/// progress, Claude Code's spinner) turned entirely into "no record". This asks instead
/// "what events <b>occurred</b> during this second" -- a zero-duration event's timestamp
/// genuinely lands inside some second, and still gets painted. Same data, different
/// question, and the hole is gone.
/// </summary>
public static class Judgment
{
    /// <summary>
    /// Paints events into <paramref name="window"/>. <paramref name="windowStart"/> is the
    /// absolute moment corresponding to <c>window[0]</c>, each subsequent element +1 second.
    /// </summary>
    /// <param name="windowEvents">Window events (may include some outside the window; clipped here).</param>
    /// <param name="afkEvents">Afk events, same as above.</param>
    /// <param name="rules">Compiled rules.</param>
    /// <param name="selectedGroup">The single currently-selected goal's name (null = none selected, in which case everything is OffTask).</param>
    public static void Paint(
        Span<JudgmentCode> window,
        DateTimeOffset windowStart,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        if (window.Length == 0) return;

        // Compute matches up front: regex matching isn't cheap, and the loop below runs twice.
        var hit = new bool[windowEvents.Count];
        for (var i = 0; i < windowEvents.Count; i++)
        {
            var e = windowEvents[i];
            hit[i] = selectedGroup is not null
                  && rules.GroupMatches(selectedGroup, e.App ?? "", e.Title ?? "");
        }

        // (2) Focused is painted first, (3) OffTask painted after -- the order itself is
        //     the tie-break: when both exist in the same second, OffTask wins
        //     (fail-closed). Don't merge these two passes into one.
        for (var i = 0; i < windowEvents.Count; i++)
            if (hit[i]) PaintOne(window, windowStart, windowEvents[i], JudgmentCode.Focused);

        for (var i = 0; i < windowEvents.Count; i++)
            if (!hit[i]) PaintOne(window, windowStart, windowEvents[i], JudgmentCode.OffTask);

        // (4) Afk is painted last -- when nobody's there, whatever the window shows
        //     doesn't matter (otherwise time locked away from the desk would still tick up).
        foreach (var e in afkEvents)
            if (e.Status == "afk") PaintOne(window, windowStart, e, JudgmentCode.Afk);
    }

    /// <summary>
    /// Which seconds one event paints: <b>every second it touches</b>, i.e.
    /// <c>floor(start) ... ceil(end)-1</c>.
    ///
    /// <b>A zero-duration event still fills one full second</b> (T4): when
    /// <c>end == start</c>, ceil and floor are equal, and this stretches the interval to
    /// 1 second -- equivalent to "duration defaults to 0.001".
    ///
    /// The second at a boundary gets claimed by both neighbouring events at once, which is
    /// exactly handed off to the covering order to decide: <b>ownership is decided by
    /// priority, not by rounding</b>. The cost is that a switch across a second boundary
    /// can be off by at most 1 second, always biased toward the stricter side.
    /// </summary>
    private static void PaintOne(Span<JudgmentCode> window, DateTimeOffset windowStart,
                                 AwEvent e, JudgmentCode code)
    {
        var from = (int)Math.Floor((e.Start - windowStart).TotalSeconds);
        var to = (int)Math.Ceiling((e.End - windowStart).TotalSeconds);
        if (to <= from) to = from + 1;              // Zero duration: occupies at least the one second it lands in

        if (from < 0) from = 0;                     // Clip to the slice's range (6-hour prefetch, see T1/F7)
        if (to > window.Length) to = window.Length;
        if (to <= from) return;

        window[from..to].Fill(code);
    }
}
