namespace ItamiTimer.Core;

/// <summary>The result of one completed tick.</summary>
/// <param name="SettledSeconds">Focus seconds settled by archiving this tick. The caller must <c>+=</c> it into during (§11.2).</param>
/// <param name="DeficitSeconds">How many focus seconds are still owed (already rounded up to a whole minute). 0 = achieved.</param>
/// <param name="Completed">Whether this tick achieved completion. <b>The moment of completion is this very tick</b> — never derived retroactively.</param>
public readonly record struct TickOutcome(int SettledSeconds, int DeficitSeconds, bool Completed);

/// <summary>
/// Second-level focus storage (second version of the judgment engine).
///
/// <c>7380 = 180 seconds of padding + 7200 seconds of drawable span</c> (120 minutes = two
/// turns of the dial). <c>buffer[i]</c> corresponds to the absolute moment
/// <c>WallClock + i seconds</c>; <c>buffer[180]</c> is the task's start point.
///
/// <list type="bullet">
///   <item><b>Padding exists for exactly one reason</b>: the first tick needs to query the
///         3 minutes before the start point, and that data needs somewhere to land. It's
///         <b>never counted, never drawn</b>.</item>
///   <item><b>The 7200-second drawable span isn't a memory decision, it's a rendering
///         decision</b>: one turn of the dial is 60 minutes, the spiral only has two turns,
///         so 120 minutes is all that can be drawn. Anything beyond that is handled by
///         archiving (<see cref="TryArchive"/>).</item>
/// </list>
///
/// Five things happen every whole minute; the first four live here (the fifth, colouring,
/// belongs to the rendering layer):
/// <code>
/// 1. Cover     Cover()        Repaint [whole-minute-4min, whole-minute)
/// 2. Archive   TryArchive()   Only once ElapsedSeconds >= 7200
/// 3. Gray      RefreshGray()  Recompute the commitment arc
/// 4. Complete  Deficit <= 0 means achieved, at this very tick
/// </code>
/// <see cref="Tick"/> chains these four steps together — normal callers just call it.
/// </summary>
public sealed class JudgmentBuffer
{
    public const int PaddingSeconds = 180;
    public const int DrawSeconds = 7200;
    public const int TotalSize = PaddingSeconds + DrawSeconds;   // 7380

    /// <summary>The ActivityWatch query window is a fixed 4 minutes: afk defaults to taking 180 seconds to settle and backfill, so 4 minutes is guaranteed to cover it.</summary>
    public const int QueryWindowSeconds = 240;

    /// <summary>
    /// **刚跑完的那一分钟**是 <paramref name="cells"/> 里的哪一格。
    ///
    /// ⚠️ **绝不是 `cells[^1]`。** <c>ToMinuteCells</c> 吐的是"真实分钟 + 承诺弧那截灰色
    /// 投影"，而投影在缺口归零之前一直非空——所以本轮的绝大部分时间里，`[^1]` 都是一个
    /// **还没发生**的格子，`OffTaskSeconds` 恒为 0。DECISIONS L31 记的正是这个：那条
    /// "上一分钟跑偏了多少"的诊断日志**在生产里一次都没触发过**，翻遍历史日志零命中。
    ///
    /// 收进 Core 是因为**两个前端各写过一遍**，而且写法不等价：App 用的是这里这个
    /// 索引，`itami` 用的是"最后一个有秒数的格子"——长睡之后中间会出现纯
    /// <see cref="JudgmentCode.Init"/> 的格子（五个计数全 0），后者会跳过它、报到更早的
    /// 一分钟去。同一个规则两处定义，迟早漂（§15.7 那次事故的形状）。
    /// </summary>
    public static MinuteCell? LastCompleted(IReadOnlyList<MinuteCell> cells, int elapsedSeconds)
    {
        var real = elapsedSeconds / 60;
        return real > 0 && real <= cells.Count ? cells[real - 1] : null;
    }

    /// <summary>How many seconds one archive roll evicts.</summary>
    public const int ArchiveSeconds = 3600;

    private readonly JudgmentCode[] _buf = new JudgmentCode[TotalSize];

    /// <summary>The absolute moment corresponding to <c>buffer[0]</c> = task start − 180 seconds. Advances by +3600 on each archive.</summary>
    public DateTimeOffset WallClock { get; private set; }

    /// <summary>Task start = <see cref="WallClock"/> + 180 seconds. <b>It moves forward by an hour after archiving</b> (§4.4).</summary>
    public DateTimeOffset TaskStart => WallClock.AddSeconds(PaddingSeconds);

    /// <summary>Seconds elapsed since the (current) task start = the highest offset written so far. −3600 on each archive.</summary>
    public int ElapsedSeconds { get; private set; }

    /// <summary>The write head's index into the buffer.</summary>
    public int Head => PaddingSeconds + ElapsedSeconds;

    /// <summary>
    /// <b>Remaining</b> target seconds — the sole basis for both the completion check and
    /// the commitment arc's length; decremented on archiving (§4.4).
    ///
    /// ⚠️ It is <b>not</b> the basis for the rest length. Rest only ever reads the
    /// <c>TaskRecord.FocusMinutes</c> locked in at submission, regardless of how long this
    /// round actually took (DECISIONS H6: the longer you drag it out, the shorter your
    /// break would get, which is a backwards incentive).
    /// </summary>
    public int RemainingTargetSeconds { get; private set; }

    /// <summary>Seconds archived so far this round (+3600 on each archive). Rarely needed outside of lap-number bookkeeping.</summary>
    public int ArchivedSeconds { get; private set; }

    public JudgmentBuffer(DateTimeOffset taskStart, int focusMinutes)
    {
        WallClock = taskStart.AddSeconds(-PaddingSeconds);
        RemainingTargetSeconds = focusMinutes * 60;
        // The opening grey arc uses the exact same per-tick algorithm — no separate
        // initialization routine (§4.5).
        RefreshGray();
    }

    public JudgmentCode this[int index] => _buf[index];

    // Read-only views for tests and the CLI to inspect internals directly
    public ReadOnlySpan<JudgmentCode> Raw => _buf;
    public ReadOnlySpan<JudgmentCode> DrawSpan => _buf.AsSpan(PaddingSeconds, DrawSeconds);

    /// <summary>Seconds counted as focus so far = the count of codes >= Focused within <c>[180, 7380)</c>. <b>Excludes padding.</b></summary>
    public int FocusedSeconds => CountFocused(PaddingSeconds, TotalSize);

    /// <summary>Whether focus has been achieved. Equivalent to "the commitment arc is empty".</summary>
    public bool IsFocusComplete => RemainingTargetSeconds - FocusedSeconds <= 0;

    /// <summary>The full pipeline for one tick: cover -> archive -> recompute the commitment arc -> check completion (§4.2).</summary>
    public TickOutcome Tick(
        DateTimeOffset now,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        var settled = Cover(now, windowEvents, afkEvents, rules, selectedGroup);
        settled += TryArchive();
        var deficit = RefreshGray();
        return new TickOutcome(settled, deficit, deficit <= 0);
    }

    /// <summary>
    /// Step 1: cover (§4.3). The query window is
    /// <c>[FloorToMinute(now) − 4min, FloorToMinute(now))</c>.
    ///
    /// <b>Everything is computed from the whole minute, never mixing in <c>now</c>'s
    /// sub-second remainder</b> (DECISIONS H9): otherwise the phase differs from tick to
    /// tick, the same buffer second gets written twice by two sample points nearly a second
    /// apart, boundary seconds flip back and forth; and when ActivityWatch responds 10
    /// seconds slow, the write offset ends up shifted for the whole span.
    ///
    /// Returns: focus seconds forcibly settled by archiving to make room for this tick
    /// (normally always 0).
    /// </summary>
    public int Cover(
        DateTimeOffset now,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        var minute = TimeGrid.FloorToMinute(now);
        var settled = 0;

        // §15.6: when the machine sleeps/suspends longer than the drawable span's
        // capacity, the write offset would permanently run out of bounds -> ElapsedSeconds
        // freezes -> the archive condition never becomes true again -> the session
        // deadlocks (the dial stalls, completion never comes).
        // Roll forward first until this tick fits in the buffer. 64 iterations = 64 hours,
        // enough for any single suspend.
        for (var guard = 0; guard < 64; guard++)
        {
            if (OffsetOf(minute) <= TotalSize) break;   // The window's end (= this tick) now fits in the buffer
            settled += Archive();
        }

        var offset = OffsetOf(minute) - QueryWindowSeconds;   // Window start relative to buffer[0]
        var from = Math.Max(offset, 0);
        var to = Math.Min(offset + QueryWindowSeconds, TotalSize);
        if (to <= from) return settled;

        // (1) Initialize "new ground" = [last write head, this write head) intersected with
        // this query window, filling it with AwOffline.
        //
        // This cut is **deliberate**: in normal operation the new ground is just the last
        // minute, the same rule as "an ActivityWatch outage only ever writes the last
        // minute" (H4); missing up to three ticks still lands entirely inside the window
        // and gets repainted by real events. But missing longer than that (the machine
        // slept for two hours), the minutes **outside** the window stay Init and
        // uncounted — "I simply never queried this" must not be handed out for free as
        // "ActivityWatch has no record", or sleeping through the night could fill an
        // entire task.
        var head = Head;
        if (head < from)
        {
            // Missed longer than the query window itself (the machine slept for two
            // hours): the stretch **outside** the window was neither queried nor ever will
            // be, so it's cleared to Init — not counted, not drawn. "I simply never
            // queried this" must not be handed out for free as "ActivityWatch has no
            // record", or sleeping through the night could fill an entire task.
            // It can't be left untouched either: the previous tick's commitment arc is
            // still sitting there, and leaving it would get it coloured as "not reached yet".
            _buf.AsSpan(head, from - head).Fill(JudgmentCode.Init);
        }
        var newFrom = Math.Max(head, from);
        if (newFrom < to) _buf.AsSpan(newFrom, to - newFrom).Fill(JudgmentCode.AwOffline);

        // (2)(3)(4) layered covering. The last 3 minutes are only ever **repainted**, never
        // cleared: once a second has been judged Afk/OffTask/Focused, it never falls back
        // to AwOffline — it can only be revised by later, real data. Don't clear the whole
        // 4 minutes back to AwOffline "for consistency" and repaint — if ActivityWatch ever
        // returns an incomplete response, that would wipe seconds already judged red back
        // to green in one stroke.
        Judgment.Paint(_buf.AsSpan(from, to - from), WallClock.AddSeconds(from),
                       windowEvents, afkEvents, rules, selectedGroup);

        var elapsed = to - PaddingSeconds;
        if (elapsed > ElapsedSeconds) ElapsedSeconds = elapsed;
        return settled;
    }

    /// <summary>
    /// Step 2: archive (§4.4): rolls forward once the buffer has filled up 2 hours. Returns
    /// the focus seconds settled (0 = didn't archive).
    ///
    /// <b>The first roll happens at 2 full hours, then once every hour after that</b>
    /// (after archiving, <see cref="ElapsedSeconds"/> returns to 3600).
    /// </summary>
    public int TryArchive() => ElapsedSeconds < DrawSeconds ? 0 : Archive();

    /// <summary>
    /// Rolls forward unconditionally, once. Semantically equivalent to <b>"abandoning the
    /// task an hour ago, and immediately restarting at that same instant with the remaining
    /// target"</b> — that sentence is the standard for judging whether this code is
    /// correct.
    ///
    /// So what gets settled must be <b>exactly "the entirety of the previous task's
    /// time"</b>, i.e. <c>[180, 3780)</c>: from the old start to the new start, exactly
    /// 3600 seconds, no more, no less.
    ///
    /// ⚠️ Writing this as <c>[0, 3600)</c> is exactly the bug in §15.5 — off by 180, it both
    /// credits the focus seconds from "before Start was clicked" and drops the 3 minutes in
    /// <c>[3600, 3780)</c>; at the instant of archiving, "how much is left" jumps, in either
    /// direction, and when it jumps negative a nearly-complete task suddenly regresses with
    /// no warning at all.
    /// </summary>
    private int Archive()
    {
        var settled = CountFocused(PaddingSeconds, PaddingSeconds + ArchiveSeconds);

        RemainingTargetSeconds -= settled;
        if (RemainingTargetSeconds < 0) RemainingTargetSeconds = 0;

        // [3600, 7380) -> [0, 3780):
        //   old [3780,7380) becomes new [180,3780) -- the new task's first hour
        //   old [3600,3780) becomes new [0,180)     -- the new task's padding, already settled, not counted again
        Array.Copy(_buf, ArchiveSeconds, _buf, 0, TotalSize - ArchiveSeconds);
        Array.Fill(_buf, JudgmentCode.Init, TotalSize - ArchiveSeconds, ArchiveSeconds);

        WallClock = WallClock.AddSeconds(ArchiveSeconds);
        ArchivedSeconds += ArchiveSeconds;
        ElapsedSeconds = Math.Max(0, ElapsedSeconds - ArchiveSeconds);
        return settled;
    }

    /// <summary>
    /// Step 3: recompute the commitment arc (§4.5). Returns the deficit in seconds
    /// (already rounded up to a whole minute); <c>0</c> means achieved.
    ///
    /// <b>Recomputed every tick, no state kept.</b> "Remember the last Gray position and
    /// advance from there" is wrong: when afk shrinkage (T5) reclassifies dozens of seconds
    /// from Afk to Focused, the deficit shrinks faster than the write head advances, and the
    /// arc's end <b>moves backward</b> — an incremental approach would leave a leftover
    /// smear behind, making the commitment arc longer than it should be. So <c>[Head,
    /// 7380)</c> is cleared to Init first, then filled with Gray. One 7KB Fill per minute
    /// costs nothing worth mentioning, and it eliminates every "which range needs
    /// clearing" question.
    /// </summary>
    public int RefreshGray()
    {
        var deficit = RemainingTargetSeconds - FocusedSeconds;
        if (deficit < 0) deficit = 0;
        deficit = (deficit + 59) / 60 * 60;                     // Round up to a whole minute

        var head = Head;
        if (head >= TotalSize) return deficit;

        Array.Fill(_buf, JudgmentCode.Init, head, TotalSize - head);
        var grayEnd = Math.Min(head + deficit, TotalSize);      // Clip if it exceeds the drawable span
        if (grayEnd > head) Array.Fill(_buf, JudgmentCode.Gray, head, grayEnd - head);
        return deficit;
    }

    /// <summary>
    /// Projects into one cell per minute (§4.6). Range is <c>[180, Head + deficit)</c> —
    /// <b>elapsed so far + the commitment arc</b> — everything beyond that is Init and
    /// isn't emitted. Only whole 60-second cells are emitted.
    /// </summary>
    public List<MinuteCell> ToMinuteCells()
    {
        var deficit = RemainingTargetSeconds - FocusedSeconds;
        if (deficit < 0) deficit = 0;
        deficit = (deficit + 59) / 60 * 60;

        var end = Math.Min(Head + deficit, TotalSize);
        var cells = new List<MinuteCell>();

        for (var i = 0; ; i++)
        {
            var bufStart = PaddingSeconds + i * 60;
            if (bufStart + 60 > end) break;

            int focus = 0, off = 0, afk = 0, gray = 0, init = 0;
            for (var s = 0; s < 60; s++)
            {
                switch (_buf[bufStart + s])
                {
                    case JudgmentCode.Focused:
                    case JudgmentCode.AwOffline: focus++; break;
                    case JudgmentCode.OffTask: off++; break;
                    case JudgmentCode.Afk: afk++; break;
                    case JudgmentCode.Gray: gray++; break;
                    default: init++; break;
                }
            }

            cells.Add(new MinuteCell(i, TaskStart.AddMinutes(i), focus, off, afk, gray, init));
        }
        return cells;
    }

    private int CountFocused(int from, int to)
    {
        var n = 0;
        for (var i = from; i < to; i++)
            if (_buf[i] >= JudgmentCode.Focused) n++;
        return n;
    }

    /// <summary>The index of an absolute moment within the buffer (may be out of range; the caller clips it themselves).</summary>
    private int OffsetOf(DateTimeOffset t) => (int)Math.Round((t - WallClock).TotalSeconds);
}
