namespace ItamiTimer.Core;

/// <summary>
/// DESIGN.md §7 的重放算法 —— 这个项目的核心。
///
/// **全是纯函数：无 I/O、无时钟，<c>now</c> 是参数。** 所以测"专注 25 分钟走完"
/// 不用真等 25 分钟，喂合成事件列表就能穷举边界（§7）。
/// </summary>
public static class Replay
{
    /// <summary>把一条事件裁到半开区间 [since, until)，不相交返回 null。</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? Clip(
        AwEvent e, DateTimeOffset since, DateTimeOffset until)
    {
        var start = e.Start > since ? e.Start : since;
        var end = e.End < until ? e.End : until;
        return end <= start ? null : (start, end);
    }

    /// <summary>
    /// 收集所有边界并排序去重（§7 第 2 步）：since、until、两个 bucket 每条事件的两端。
    ///
    /// §5.4 改成并集之后，勾选集合不再随时刻变化，所以**不再**需要把勾选变更时刻
    /// 并进来——这是那次改动省下的东西。
    /// </summary>
    public static List<DateTimeOffset> Boundaries(
        IEnumerable<AwEvent> windowEvents, IEnumerable<AwEvent> afkEvents,
        DateTimeOffset since, DateTimeOffset until)
    {
        var set = new SortedSet<DateTimeOffset> { since, until };
        foreach (var e in windowEvents.Concat(afkEvents))
        {
            if (e.Start > since && e.Start < until) set.Add(e.Start);
            if (e.End > since && e.End < until) set.Add(e.End);
        }

        // T6：「尚未判定」的暂定期也有个终点，**它必须是一条边界**。
        // 否则一条 not-afk 事件之后的整段空洞会被它起点处的判断整个裹走 ——
        // 区间可能比 180 秒长得多，而分类是按区间【起点】算的。
        foreach (var e in afkEvents)
        {
            if (e.Status != "not-afk") continue;
            var deadline = e.End.AddSeconds(AfkTimeoutSeconds);
            if (deadline > since && deadline < until) set.Add(deadline);
        }
        return [.. set];
    }

    /// <summary>
    /// AW 的 afk watcher 的超时（`aw-watcher-afk.toml` 的 `timeout`，默认 180 秒）。
    /// 见 <see cref="PendingPresent"/>。
    /// </summary>
    public const double AfkTimeoutSeconds = 180;

    /// <summary>
    /// 相邻事件之间的微小空隙最多桥接多少秒。见 <see cref="Bridge"/>。
    /// 取 5 秒：足以吸收心跳节奏造成的亚秒空隙，又远小于任何值得报告的真实窟窿。
    /// </summary>
    public const double MaxBridgeSeconds = 5;

    /// <summary>
    /// **AW 行为特性 T4（2026-07-27 在本机真实数据上确认）：窗口事件的 duration 可以是 0。**
    ///
    /// 当窗口标题**每秒都在变**时，AW 无法把相邻事件合并，于是每秒产生一条
    /// `duration = 0.0s` 的事件。实测触发源：Claude Code 在 Windows Terminal 里的
    /// 转圈动画（`⠂ ⠐ ⠂ ⠐ ETF 学习`）——这一个来源就贡献了 3 天 5796 条事件里的大头。
    ///
    /// 零时长事件的 [start, end) 是**空区间**，<see cref="CoveringAt"/> 永远找不到
    /// 覆盖者，于是那段时间被整片误判成 `Gap`。实测把一段 22 分钟的重放里 2 分钟
    /// 报成了"AW 无数据"，而当时窗口其实一直是聚焦的。
    ///
    /// 修法：把每条事件的终点延伸到下一条的起点，**但只在空隙 ≤
    /// <see cref="MaxBridgeSeconds"/> 时**。这个上限是关键——无上限地延伸会把
    /// aw-server 宕掉造成的真实窟窿也一起填平，那就把 §6.3 的 Gap 检测毁了。
    /// </summary>
    public static List<AwEvent> Bridge(IReadOnlyList<AwEvent> sorted)
    {
        var result = new List<AwEvent>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (i + 1 < sorted.Count)
            {
                var hole = (sorted[i + 1].Start - e.End).TotalSeconds;
                if (hole > 0 && hole <= MaxBridgeSeconds)
                    e = e with { DurationSeconds = (sorted[i + 1].Start - e.Start).TotalSeconds };
            }
            result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// **AW 行为特性 T6（2026-07-27 实测）：afk 桶在「尚未判定」的那段时间是空的。**
    ///
    /// `aw-watcher-afk` 要连续 180 秒零输入才翻成 `afk`，而那条 not-afk 事件在**最后
    /// 一次输入**那一刻就停止延长了。于是：
    ///
    /// <code>
    /// 22:32:16  最后一次动键鼠 → not-afk 事件到此为止
    /// 22:32:16 ~ 22:35:16      → afk 桶里【什么都没有】，AW 还没判
    /// 22:35:16  超时，写下 afk 事件并【回填】到 22:32:16
    /// </code>
    ///
    /// 只要人安静下来，afk 桶的末端就有一个最长 180 秒的空洞。把它按 §6.1.1 判 `Gap`
    /// 是错的——那条规矩防的是「afk watcher 根本没在跑」，不是「AW 还没来得及判」。
    /// 实测后果：坐着看学习视频的头三分钟，表盘一格都不长。
    ///
    /// 所以区分两种「没有 afk 事件」：
    /// <list type="bullet">
    /// <item>末尾那条 <b>not-afk</b> 刚结束、还没到超时 → **尚未判定**，延续为在座</item>
    /// <item>其余（watcher 没跑、空洞超过超时、上一条是 afk） → 真的没数据 → `Gap`</item>
    /// </list>
    ///
    /// **这个「暂定」会自我纠正**：真走开了，180 秒后 AW 会把 afk 事件回填下来，下一轮
    /// 重放就正确判成 `Absent`。所以最终账目不受影响，§6.1.1 那条作弊路径也没被打开——
    /// 跟 §6.2「临时不可达不影响最终结果」是同一个道理。
    /// </summary>
    public static bool PendingPresent(IReadOnlyList<AwEvent> afkSorted, DateTimeOffset t)
    {
        AwEvent? last = null;
        foreach (var e in afkSorted)
        {
            if (e.Start > t) break;
            if (e.End <= t && (last is null || e.End > last.Value.End)) last = e;
        }
        return last is { Status: "not-afk" } l && (t - l.End).TotalSeconds < AfkTimeoutSeconds;
    }

    /// <summary>在已按 Start 排好序的事件里找覆盖时刻 t 的那条。找不到返回 null → Gap。</summary>
    public static AwEvent? CoveringAt(IReadOnlyList<AwEvent> sorted, DateTimeOffset t)
    {
        // 事件量是分钟级的（任务最长 50 分钟），线性扫足够；不做缓存是刻意的（§7.2）。
        foreach (var e in sorted)
        {
            if (e.Start > t) break;
            if (e.Start <= t && t < e.End) return e;
        }
        return null;
    }

    /// <summary>
    /// 把 [task.StartedAt, now) 切成一串分好类的区间（§7 第 1~3 步）。
    /// </summary>
    public static List<ClassifiedInterval> Slice(
        TaskRecord task, GroupRules rules,
        IReadOnlyList<AwEvent> windowEvents, IReadOnlyList<AwEvent> afkEvents,
        DateTimeOffset now)
    {
        var since = task.StartedAt;
        var until = now;
        var result = new List<ClassifiedInterval>();
        if (until <= since) return result;

        // T4：先把零时长/亚秒空隙桥接起来，否则整段会被误判成 Gap。
        windowEvents = Bridge(windowEvents);
        afkEvents = Bridge(afkEvents);

        var bounds = Boundaries(windowEvents, afkEvents, since, until);
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var (a, b) = (bounds[i], bounds[i + 1]);
            var win = CoveringAt(windowEvents, a);
            var afk = CoveringAt(afkEvents, a);

            IntervalKind kind;
            if (afk is null && !PendingPresent(afkEvents, a))
            {
                // afk 没数据就是没数据。**绝不能当成"在座"**——那会把"停在目标应用
                // 上起身走开"这条最省力的作弊路径重新打开（§6.1.1）。
                // 唯一的例外是 T6 那种「AW 尚未判定」的尾部空洞，见 PendingPresent。
                kind = IntervalKind.Gap;
            }
            else if (afk is { Status: "afk" })
            {
                // §4：Absent 优先级高于一切。锁屏时 LockApp.exe 在 ignore 名单里
                // （本该 Neutral、计入）而 afk 同时说 afk —— 必须判 Absent，
                // 否则就是"锁屏一小时专注时长照涨"的漏洞。
                kind = IntervalKind.Absent;
            }
            else if (win is null)
            {
                kind = IntervalKind.Gap;
            }
            else
            {
                kind = rules.Classify(win.Value.App ?? "", win.Value.Title ?? "", task.Groups);
            }

            result.Add(new ClassifiedInterval(a, b, kind, win?.App, win?.Title));
        }
        return result;
    }

    private static bool Counts(IntervalKind k)
        => k is IntervalKind.OnTask or IntervalKind.Neutral;

    /// <summary>§7 全部八步。纯函数。</summary>
    public static TaskState Run(
        TaskRecord task, GroupRules rules,
        IReadOnlyList<AwEvent> windowEvents, IReadOnlyList<AwEvent> afkEvents,
        DateTimeOffset now)
    {
        var intervals = Slice(task, rules, windowEvents, afkEvents, now);
        var target = task.FocusMinutes * 60.0;

        // 第 4~5 步：累计，并在跨过阈值的那个区间内**插值**出精确时刻。
        double focused = 0;
        DateTimeOffset? completedAt = null;
        foreach (var iv in intervals)
        {
            if (!Counts(iv.Kind)) continue;
            if (completedAt is null && focused + iv.Seconds >= target)
            {
                completedAt = iv.Start.AddSeconds(target - focused);
                focused = target;
                break; // 达成之后的区间属于休息阶段，不再参与专注核算
            }
            focused += iv.Seconds;
        }

        // 第 6 步：违规的极大连续段（用来报"偷懒了几次"）。
        var accountingEnd = completedAt ?? now;
        var violations = new List<ViolationRun>();
        var byApp = new Dictionary<string, double>();
        double absent = 0, gap = 0;
        DateTimeOffset? runStart = null, runEnd = null;

        foreach (var iv in intervals)
        {
            if (iv.Start >= accountingEnd) break;
            var end = iv.End < accountingEnd ? iv.End : accountingEnd;
            var secs = (end - iv.Start).TotalSeconds;

            switch (iv.Kind)
            {
                case IntervalKind.OffTask:
                    byApp[iv.App ?? "?"] = byApp.GetValueOrDefault(iv.App ?? "?") + secs;
                    if (runStart is not null && runEnd == iv.Start) runEnd = end;
                    else
                    {
                        if (runStart is not null) violations.Add(new ViolationRun(runStart.Value, runEnd!.Value));
                        (runStart, runEnd) = (iv.Start, end);
                    }
                    continue;
                case IntervalKind.Absent: absent += secs; break;
                case IntervalKind.Gap: gap += secs; break;
            }
            if (runStart is not null) { violations.Add(new ViolationRun(runStart.Value, runEnd!.Value)); runStart = null; }
        }
        if (runStart is not null) violations.Add(new ViolationRun(runStart.Value, runEnd!.Value));

        var restEndsAt = completedAt?.AddMinutes(task.RestMinutes);
        return new TaskState
        {
            Now = now,
            Phase = DerivePhase(task, intervals, completedAt, restEndsAt, now),
            Intervals = intervals,
            FocusedSeconds = focused,
            FocusCompletedAt = completedAt,
            RestEndsAt = restEndsAt,
            Violations = violations,
            OffTaskSecondsByApp = byApp,
            AbsentSeconds = absent,
            GapSeconds = gap,
        };
    }

    /// <summary>第 8 步：此刻是什么状态。**推导出来的分类，不是机器状态**（§3）。</summary>
    private static TaskPhase DerivePhase(
        TaskRecord task, List<ClassifiedInterval> intervals,
        DateTimeOffset? completedAt, DateTimeOffset? restEndsAt, DateTimeOffset now)
    {
        if (now < task.StartedAt) return TaskPhase.NotStarted;
        if (completedAt is not null)
            return now >= restEndsAt!.Value ? TaskPhase.Completed : TaskPhase.Resting;
        if (intervals.Count == 0) return TaskPhase.NoData;

        return intervals[^1].Kind switch
        {
            IntervalKind.OnTask or IntervalKind.Neutral => TaskPhase.Focusing,
            IntervalKind.OffTask => TaskPhase.Slacking,
            IntervalKind.Absent => TaskPhase.Away,
            _ => TaskPhase.NoData,
        };
    }

    /// <summary>
    /// 把重放结果投影成每分钟一格（§8.2.3）。
    /// **是投影，不是逐分钟累加的数组**——判据：关掉界面再打开，每格颜色都要能原样重建（§0.4.2）。
    ///
    /// 只吐**完整**的格子，正在走的那一分钟不吐（§14.2：否则那格会随秒数不停闪）。
    /// 唯一的例外是专注达成之后的末格，它按实际长度短一截（§14.4）。
    /// </summary>
    public static List<MinuteCell> ToMinuteCells(TaskRecord task, TaskState state)
    {
        var end = state.FocusCompletedAt ?? state.Now;
        var cells = new List<MinuteCell>();

        for (var i = 0; ; i++)
        {
            var cellStart = task.StartedAt.AddMinutes(i);
            var cellEnd = cellStart.AddMinutes(1);
            if (cellStart >= end) break;

            // 未达成时只吐完整格；达成之后允许最后一格短一截
            if (cellEnd > end && state.FocusCompletedAt is null) break;
            if (cellEnd > end) cellEnd = end;

            double counted = 0, off = 0, absent = 0, gap = 0;
            foreach (var iv in state.Intervals)
            {
                if (iv.End <= cellStart) continue;
                if (iv.Start >= cellEnd) break;
                var s = iv.Start > cellStart ? iv.Start : cellStart;
                var e = iv.End < cellEnd ? iv.End : cellEnd;
                var secs = (e - s).TotalSeconds;
                if (secs <= 0) continue;

                if (Counts(iv.Kind)) counted += secs;
                else if (iv.Kind == IntervalKind.OffTask) off += secs;
                else if (iv.Kind == IntervalKind.Absent) absent += secs;
                else gap += secs;
            }
            cells.Add(new MinuteCell(i, cellStart, counted, off, absent, gap));
        }
        return cells;
    }
}
