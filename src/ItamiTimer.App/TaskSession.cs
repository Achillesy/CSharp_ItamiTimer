using Avalonia.Threading;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// The runtime for one task: drives <see cref="JudgmentBuffer"/> through one tick every
/// whole minute, hands the projected cells to the UI, and beeps on three
/// events.
///
/// **Always runs in constrained mode** -- the fallback to plain-pomodoro was deleted
/// entirely on 2026-07-31 (DECISIONS B3): ActivityWatch being unavailable is absorbed by
/// the judgment model itself (§3.1), the UI never changes shape depending on whether
/// ActivityWatch is up.
/// </summary>
public sealed class TaskSession : IDisposable
{
    /// <summary>
    /// The three events worth a beep. Each can be individually turned off in Settings
    /// (§8.3.1).
    /// </summary>
    public enum Interrupt
    {
        FocusDone,
        RestDone,
        Idle,
    }

    public const int NudgeFloorSeconds = 5;
    public const int IdleNudgeSeconds = 60;
    private const int AwAfkTimeoutSeconds = 180;

    /// <summary>
    /// A diagnostic threshold, not part of judgment (DESIGN §16.5): as long as a bucket's
    /// watcher is alive its heartbeat keeps advancing, so if a tick has not one event
    /// close to <c>now</c>, the watcher is probably dead (or the machine just woke up --
    /// the two look identical under this signal, so no guessing, just log it and let the
    /// user check).
    /// </summary>
    private const int AwStaleSeconds = 60;

    /// <summary>每秒那次 `?limit=N` 取多少条。十几秒内正常最多几条，20 是很大的余量。</summary>
    private const int MirrorFetchLimit = 20;

    private readonly GroupRules _rules;
    private readonly AwClient _aw;
    private readonly JudgmentBuffer _buffer;
    private string? _winBucket, _afkBucket;
    private bool _busy;

    /// <summary>
    /// 这一轮的 AW 内存镜像（DESIGN §7.5）。**本会话内所有判定都从它读**——常驻期唯一
    /// 还在碰 AW 的是 <see cref="RefreshMirrorAsync"/> 那一条路。
    /// （`Backfill` 是例外：它扫的是跨天的历史，245 秒的镜像装不下，走自己的查询。）
    /// </summary>
    private readonly AwMirror _mirror;

    private bool _mirrorReady;

    /// <summary>上一次探到的两个桶的 <c>last_updated</c>：既是"有没有新东西"的游标，也是陈旧诊断的依据。</summary>
    private DateTimeOffset _winSeen, _afkSeen;

    /// <summary>AW 掉线的日志只在**状态变化**时打，不是每秒一行。</summary>
    private bool _awDown;

    public TaskRecord Task { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    /// <summary>
    /// The rest wedge's starting point. **A projected value before focus is achieved** (=
    /// the wall-clock moment corresponding to the commitment arc's end, recomputed every
    /// tick along with the deficit), **locked to the actual completion moment once
    /// achieved** -- the two are the same number on the tick the deficit hits zero, so the
    /// handoff never jumps. It has a value the instant the task is constructed, without
    /// waiting for the first tick's ActivityWatch response.
    /// </summary>
    public DateTimeOffset? RestFrom { get; private set; }

    public bool InRest => _focusDoneAt is not null;
    public bool Finished { get; private set; }

    /// <summary>
    /// The most recently **completed** minute's judgment, for the tick's "quiet is earned"
    /// rule (MainWindow.OnFrame, 2026-08-13). <c>null</c> until the first real tick lands.
    ///
    /// ⚠️ **Deliberately not <c>Cells[^1]</c>.** <see cref="Cells"/> is real minutes **plus**
    /// the commitment arc's grey projection tacked on the end (<c>JudgmentBuffer.ToMinuteCells</c>),
    /// and the projection is not empty until the deficit hits zero — so for almost this
    /// entire round, <c>Cells[^1]</c> is a cell that hasn't happened yet, not the one that
    /// just did. This field is set from the correct index (the last real minute, before the
    /// grey tail starts) right when it's computed, so callers never have to know the
    /// distinction exists.
    /// </summary>
    public MinuteCell? LastCompletedMinute { get; private set; }

    /// <summary>
    /// 本轮到目前为止的专注秒数 = 归档已结算的 + 还在 buffer 里的。
    ///
    /// **整数** —— 数的是 buffer 里的格子数，不是 AW 事件的 `duration`，所以永远没有小数
    /// 部分（用户 2026-08-02）。每一处 `/ 60` 都得写成 `60.0`，否则整数除法会把小数位悄悄
    /// 吃掉（DECISIONS G）。
    ///
    /// ⚠️ **这个数不落盘，纯粹是本轮的实时显示项**（§11.2 / DECISIONS I2）。它带着 H2 的
    /// fail-open 水分（`AwOffline` 计入专注），而账本只认下次启动时 fail-closed 重数出来的
    /// 结果。1.0.x 那套 `Settled` 事件 / `UnbankedSeconds` / `TakeUnbankedSeconds` /
    /// `_banked` 幂等标志已经整套删掉了——它们防的是「这一秒现在不记就永远没了」，
    /// 而 AW 是地面真相，那个前提不成立了。
    /// </summary>
    public int FocusedSeconds() => _settledSeconds + _buffer.FocusedSeconds;

    private int _settledSeconds;
    private int _deficitSeconds;

    public event Action? Updated;
    public event Action<Interrupt>? Interrupted;

    private DateTimeOffset? _focusDoneAt;

    public TaskSession(TaskRecord task, GroupRules rules, string awBaseUrl = "http://127.0.0.1:5600")
    {
        Task = task;
        _rules = rules;
        _aw = new AwClient(awBaseUrl);
        _buffer = new JudgmentBuffer(task.StartedAt, task.FocusMinutes);
        // 镜像跟任务同生共死：Focused/OffTask 相对于选中的小目标才有意义，而目标恰好在
        // 点 Start 这一刻锁定（之后单选框就禁用了）。DECISIONS O2。
        _mirror = new AwMirror(task.StartedAt, rules, task.Group);
        _deficitSeconds = task.FocusMinutes * 60;
        _lastAwMinute = task.StartedAt;
        // The dial needs something to show the instant the button is pressed: the whole
        // grey arc is already laid out the moment the buffer is constructed (§4.5), so one
        // projection is all it takes -- no waiting for the first ActivityWatch response, and
        // the UI layer doesn't need to compute a separate copy.
        Cells = _buffer.ToMinuteCells();
        // The rest wedge gets its preview at the same instant: the deficit is still the
        // full commitment right now, and projecting it lands exactly on start + focus length.
        RestFrom = _buffer.TaskStart.AddSeconds(_buffer.ElapsedSeconds + _deficitSeconds);
        Log.Info($"Task started. Goal: {task.Group}  focus {task.FocusMinutes} min  " +
                 $"from {task.StartedAt:HH:mm:ss}  break {task.RestMinutes} min");
    }

    private DateTimeOffset _lastAwMinute;
    private int _lastCellCount = -1;

    /// <summary>
    /// 每秒一次：把镜像推到 <paramref name="nowLocal"/>（DESIGN §7.5）。
    /// **这是常驻期唯一还在碰 AW 的地方。**
    ///
    /// 流程：
    /// <list type="number">
    ///   <item>第一次调用 → 初始化：拿"覆盖镜像起点的那条事件"外加一次范围查询，
    ///         两条加起来就是完备的，不需要任何放宽（见 <see cref="InitializeMirrorAsync"/>）。</item>
    ///   <item>之后每秒探一次两个桶的 <c>last_updated</c>（实测 739 字节），
    ///         **前进了才**去拉事件。稳态下事件查询约每 10 秒才真的发生一次。</item>
    ///   <item>没前进也要 <see cref="AwMirror.Apply"/> 空列表——镜像仍然要推进到 now，
    ///         让预测把空隙和末尾填上。</item>
    ///   <item>查询失败 → <see cref="AwMirror.MarkUnavailable"/>，那些秒留在
    ///         <see cref="JudgmentCode.AwOffline"/>（= 算专注，§3.1 的知情 fail-open）。</item>
    /// </list>
    ///
    /// **日志只在掉线/恢复那一下打**：每秒一行足够把日志淹掉，而 §8.1a 要的"原因不能
    /// 消失"由这两行状态变化完整覆盖。
    /// </summary>
    public async Task RefreshMirrorAsync(DateTime nowLocal)
    {
        // 休息期间**没有任何人读镜像**：这个方法自己在休息阶段早早 return，闪烁被
        // `!InRest` 挡掉，滴答跟着 `_drifting` 也是关的。照查就是一次 5 分钟的休息白费
        // 300 次往返（2026-08-29）。
        if (Finished || InRest) return;
        var now = new DateTimeOffset(nowLocal);

        try
        {
            if (_winBucket is null || _afkBucket is null)
                (_winBucket, _afkBucket) = await _aw.FindWatcherBucketsAsync();

            if (!_mirrorReady)
            {
                await InitializeMirrorAsync(now);
                _mirrorReady = true;
            }
            else
            {
                var seen = await _aw.FetchLastUpdatedAsync();
                var win = await PullIfChangedAsync(_winBucket, seen, _winSeen);
                var afk = await PullIfChangedAsync(_afkBucket, seen, _afkSeen);
                if (seen.TryGetValue(_winBucket, out var w)) _winSeen = w;
                if (seen.TryGetValue(_afkBucket, out var a)) _afkSeen = a;
                _mirror.Apply(win, afk, now);
            }

            if (_awDown)
            {
                _awDown = false;
                Log.Info("ActivityWatch is back; the mirror is being refreshed again.");
            }
        }
        catch (AwUnavailableException ex)
        {
            _mirror.MarkUnavailable(now);
            if (!_awDown)
            {
                _awDown = true;
                Log.Warn($"ActivityWatch unreachable; those seconds stay unrecorded (fail-open, counts as focus): {ex.Message}");
            }
        }
    }

    /// <summary>某个桶的 <c>last_updated</c> 前进了才去拉事件，否则连请求都不发。</summary>
    private async Task<List<AwEvent>> PullIfChangedAsync(
        string bucket, IReadOnlyDictionary<string, DateTimeOffset> seen, DateTimeOffset last)
        => seen.TryGetValue(bucket, out var lu) && lu > last
            ? await _aw.FetchLatestAsync(bucket, MirrorFetchLimit)
            : [];

    /// <summary>
    /// 镜像初始化（DESIGN §7.5）：**"覆盖起点的那条事件" + "[起点, now] 范围查询"**。
    ///
    /// 第一条不可省：AW 只按事件自己的 start 过滤（T1），而你可能六个小时前就开着那个
    /// 窗口没动过——范围查询根本看不到它。实测 367 字节就够，而且对"三天前打开的窗口"
    /// 同样正确；靠"往前放宽 N 小时"去猜是猜不完的（DECISIONS O11）。
    /// </summary>
    private async Task InitializeMirrorAsync(DateTimeOffset now)
    {
        var from = _mirror.Oldest;

        var win = await _aw.FetchLatestAsync(_winBucket!, 1, before: from);
        var afk = await _aw.FetchLatestAsync(_afkBucket!, 1, before: from);
        win.AddRange(await _aw.FetchEventsAsync(_winBucket!, from, now));
        afk.AddRange(await _aw.FetchEventsAsync(_afkBucket!, from, now));

        _mirror.Apply(win, afk, now);
        Log.Info($"Mirror initialized: {AwMirror.Capacity}s ending {now:HH:mm:ss}, " +
                 $"{win.Count} window / {afk.Count} afk events absorbed.");
    }

    /// <summary>
    /// 镜像里某一秒的观测（DESIGN §7.5）。**只读，不发任何请求**——镜像已经由每秒那次
    /// 刷新维护好了，这里就是一次数组下标。
    /// </summary>
    public MirrorSecond MirrorAt(DateTimeOffset second) => _mirror.At(second);

    /// <summary>
    /// 这一分钟该做的事。**由 <c>MainWindow.OnMinute</c> 在整分钟边界调用，这个类自己
    /// 不再持有定时器**（用户 2026-08-08：所有以分为单位的功能收到一条直线上）。
    ///
    /// 原来这里挂着一个 1 秒的 <c>DispatcherTimer</c>，而它锚在**任务开始那一刻**，跟
    /// MainWindow 那个 33ms 的钟各走各的——分钟边界到来时，闹钟/清单在 ≤33ms 内触发，
    /// 这边的 AW 查询却在 0~1000ms 后才到，两者没有任何协调。闹钟发出关机命令之后
    /// AW 查询紧跟着到，就是这么来的（见 <c>MainWindow.OnMinute</c> 的注释）。
    ///
    /// 休息阶段原来是**每秒**跑一遍的，现在也在这里：`RestFrom` 在休息期间恒等于
    /// `done`，每秒重复赋同一个值再触发一次 `Updated`（重画表盘 + 重算任务栏图标）本来
    /// 就是白做；而 `done` 是整分钟、`RestMinutes` 是整数，`done + rest` 必然落在整分钟上，
    /// 所以按分钟判断"休息结束了没有"跟按秒判断的结果**完全一样**，一秒都不会晚。
    /// </summary>
    public async Task TickMinuteAsync(DateTime nowLocal)
    {
        if (_busy || Finished) return;
        var now = new DateTimeOffset(nowLocal);

        // ---- Rest phase: purely local timing
        if (_focusDoneAt is { } done)
        {
            var rest = TimeSpan.FromMinutes(Task.RestMinutes);
            RestFrom = done;
            Updated?.Invoke();
            if (now >= done + rest)
            {
                Finished = true;
                RestFrom = null;
                Log.Info("Break over; task finished.");
                Interrupted?.Invoke(Interrupt.RestDone);
            }
            return;
        }

        // ---- Tick point: once every whole minute
        var minute = TimeGrid.FloorToMinute(now);
        if (minute <= _lastAwMinute) return;
        _lastAwMinute = minute;

        // 1) Keyboard/mouse idle
        var idle = InputIdle.Elapsed().TotalSeconds;
        var idleNudge = idle is >= IdleNudgeSeconds and < AwAfkTimeoutSeconds;
        if (idleNudge)
            Log.Info($"No input for {idle:F0}s, nudging (in another {AwAfkTimeoutSeconds - idle:F0}s this time is written off)");

        // 2) Query ActivityWatch, update the buffer (4-minute window)
        var focusDone = false;

        _busy = true;
        try
        {
            // The query interval is anchored to a whole minute, never mixing in now's
            // sub-second remainder (DESIGN §4.2 / DECISIONS H9)
            var queryEnd = minute;
            var queryStart = queryEnd.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);

            // **不再查 AW，从镜像读**（DESIGN §7.5）。镜像已经由每秒那次刷新推到了 now，
            // 这里要的 [整分钟-4min, 整分钟) 全都在它的 245 秒里（那 5 秒余量正是为了
            // "now 总是略晚于整分钟"这件事）。`EventsIn` 把秒级内容还原成事件，所以
            // **判定链路一个字都没改**：Cover/Paint 拿到的仍然是两个事件列表。
            //
            // 镜像没有数据时 `EventsIn` 吐两个空列表，跟原来"连不上 → 喂空列表"完全一样：
            // 这一分钟被判成 AwOffline，而 AwOffline 算专注（§3.1 的知情 fail-open）。
            // **这一拍照样跑完**——跳过等于把 ElapsedSeconds、承诺弧、休息扇形一起冻住，
            // 那是"暂停"不是 fail-open（2026-08-02 实机踩过）。
            var (win, afk) = _mirror.EventsIn(queryStart, queryEnd);

            // 诊断（只写日志，绝不改判定，§16.5）：watcher 悄悄死掉时 AwOffline 仍然算
            // 专注，出了事只能靠日志回头查。⚠️ **判据换成探针的 last_updated**——镜像里的
            // 秒会被预测填满，拿它判"新不新鲜"永远为真，等于把这条诊断安静废掉。
            if (_winSeen != default && queryEnd - _winSeen > TimeSpan.FromSeconds(AwStaleSeconds))
                Log.Warn($"aw-watcher-window hasn't written for {(queryEnd - _winSeen).TotalSeconds:F0}s - it may be stuck (or the machine just woke up)");
            // ⚠️ afk 桶**故意不判**（DECISIONS O9）：实测它的 last_updated 卡住 38 秒还在涨，
            // 正常节奏就比 window 慢得多，同一个阈值会误报；而误判成"死了"的代价是 afk
            // 覆盖失效 → 离开被算成跑偏 → 冤枉人。阈值要先实测再定。
            var outcome = _buffer.Tick(minute, win, afk, _rules, Task.Group);
            _deficitSeconds = outcome.DeficitSeconds;

            // The rest wedge doesn't wait for completion to be drawn: **its starting point
            // is exactly the commitment arc's end** (DESIGN §4.5: "the moment the
            // commitment arc disappears is the moment focus is achieved"), so it's there
            // from the very start of the task, no need to wait for actual completion.
            // Recomputed every tick, no state kept -- the same principle as the judgment
            // engine itself (Principle 4).
            //
            // This is also a deliberate design for pain: while procrastinating, the deficit
            // doesn't shrink and ElapsedSeconds still adds +60 every tick, so the projected
            // rest start **retreats right along with it** -- not only does the grey arc grow
            // longer, the rest you've earned is also retreating in real time. On the exact
            // tick real completion happens, this projected value equals `minute` itself,
            // matching `RestFrom = done` once `_focusDoneAt` is set below -- the handoff
            // never jumps.
            RestFrom = _buffer.TaskStart.AddSeconds(_buffer.ElapsedSeconds + outcome.DeficitSeconds);

            if (outcome.SettledSeconds > 0)
            {
                // 归档把一小时移出了 buffer（§4.4）。**这里不再入账**（§11.2 重写）：
                // 这一小时会和本轮其余时间一起，在下次任务启动时由回填 fail-closed 地重新
                // 数一遍。这里只是把它记进本轮的实时显示项，免得表盘和图标在归档瞬间跳水。
                _settledSeconds += outcome.SettledSeconds;
                Log.Info($"Archived an hour; {outcome.SettledSeconds}s carried into this round's running total.");
            }

            var cells = _buffer.ToMinuteCells();
            Cells = cells; // #11: doesn't disappear once focus completes, the arc stays underneath the rest wedge

            // The real (already-ticked) minutes come first in `cells`, the commitment arc's
            // grey projection is tacked on after them (see LastCompletedMinute's doc) -- so
            // the minute that just completed is at index ElapsedSeconds/60 - 1, never `[^1]`.
            var realMinutes = _buffer.ElapsedSeconds / 60;
            if (realMinutes > 0) LastCompletedMinute = cells[realMinutes - 1];

            Updated?.Invoke();

            Log.Info($"{FocusedSeconds() / 60.0,5:F1}/{Task.FocusMinutes} min  " +
                     $"cells {cells.Count}  deficit {outcome.DeficitSeconds}s  " +
                     $"settled {_settledSeconds}s");

            if (outcome.Completed && _focusDoneAt is null)
            {
                // The completion moment **is this very tick**, never derived retroactively
                // from the ledger (DESIGN §4.5 / DECISIONS H5).
                _focusDoneAt = minute;
                focusDone = true;
                Log.Info($"Focus completed at {minute:HH:mm:ss}, " +
                         $"wall-clock {(minute - Task.StartedAt).TotalMinutes:F1} min");
            }
            else if (cells.Count > 0 && cells.Count != _lastCellCount)
            {
                _lastCellCount = cells.Count;

                // 2026-08-27 修复：这里原来写的是 `cells[^1]`，跟上面那条注释字面矛盾
                // （"the minute that just completed is at index ElapsedSeconds/60 - 1,
                // never `[^1]`"）——`[^1]` 绝大多数时候是承诺弧那截灰色投影，`OffTaskSeconds`
                // 恒为 0，导致这条日志**在生产环境里一次都没触发过**（翻遍现有 itami.log
                // 零命中，含 2026-08-23 那段实打实跑偏 13 分钟的记录）。改用上面已经算对的
                // `LastCompletedMinute`，同一个值，不重新算一遍索引。
                if (LastCompletedMinute is { OffTaskSeconds: >= NudgeFloorSeconds } cell)
                {
                    // 归因只用于诊断，绝不反哺判定（§8.1a 同一条原则，跟 AwStaleSeconds
                    // 那两条警告一样）：算的是"大概率是谁"，不是又一次判定。
                    var culprit = OffTaskAttribution.Attribute(win, cell.Start, _rules, Task.Group);
                    Log.Info(culprit is null
                        ? $"The minute just past had {cell.OffTaskSeconds}s off-task"
                        : $"The minute just past had {cell.OffTaskSeconds}s off-task: {culprit}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("ActivityWatch query failed this tick; skipping it", ex);
        }
        finally { _busy = false; }

        if (focusDone) Interrupted?.Invoke(Interrupt.FocusDone);
        else if (idleNudge) Interrupted?.Invoke(Interrupt.Idle);
    }

    public void Abandon()
    {
        if (Finished) return;
        Finished = true;
        Task = Task with { Status = RecordStatus.Abandoned, AbandonedAt = DateTimeOffset.Now };
        Log.Info($"Task abandoned. Focused {FocusedSeconds() / 60.0:F1}/{Task.FocusMinutes} min");
    }

    public void Dispose() => _aw.Dispose();
}
