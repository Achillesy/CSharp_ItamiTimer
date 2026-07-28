using Avalonia.Threading;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// 一次任务的运行时（DESIGN.md §8.3.5 的单循环）。
///
/// **它只活在内存里**：没有 `current-task.json`、没有累加值，退出进程就是放弃任务
/// （§2）。任何时刻的状态都是 <c>纯函数(任务记录, AW 事件历史, now)</c> 重算出来的，
/// 这个类不持有"还剩多少秒"这种东西（原则 4）。
///
/// 整个程序只有一个节拍：**60 秒**。
///
/// <code>
/// 每 60 秒：
///   1. 查本地键鼠空闲时间（一个系统调用，不花钱）
///   2. 空闲 ≥ 60 秒 → 催用户动一下，本轮到此为止
///   3. 否则查 AW、重放整段 [startedAt, now)、更新色块
///        刚走完的那一格偏离超过 5 秒 → 提醒
/// </code>
///
/// **专注达成之后不再查 AW**（§8.4.4a）：休息阶段纯本地计时，只有色环按分钟淡出
/// 和一个到点提示。一个任务对 AW 的最后一次查询就发生在达成那一刻。
/// </summary>
public sealed class TaskSession : IDisposable
{
    /// <summary>为什么需要把窗口弹出来。</summary>
    /// <summary>
    /// 三件值得响一声的事。每一件都能在设置里单独关掉（§8.3.1）。
    ///
    /// **跑偏不在其中**：它只写日志，用户从表盘上的红格子和越滑越远的灰弧自己看出来
    /// —— 跟"不给账单"是同一条思路。整套"置顶但不抢焦点"已经删干净了（§8.3）。
    /// </summary>
    public enum Interrupt
    {
        /// <summary>专注达成，进入休息（§8.4.3）。响一声「任务结束」。</summary>
        FocusDone,
        /// <summary>休息结束，任务终结（§8.4.3）。响一声「休息结束」。</summary>
        RestDone,
        /// <summary>键鼠安静太久，赶在 AW 判 afk 之前叫醒（§8.3.5）。响一声「键鼠空闲」。</summary>
        Idle,
    }

    // §8.3.5 / §8.3.6 / §10
    public const int NudgeFloorSeconds = 5;

    /// <summary>安静多少秒开始叫（§8.3.5）。</summary>
    public const int IdleNudgeSeconds = 60;

    /// <summary>
    /// AW 判 afk 的阈值（`aw-watcher-afk.toml` 的默认值）。
    ///
    /// **超过这条线就不再叫了** —— 那时 AW 已经把这段时间回填成 afk（§14.4a T5），
    /// 叫醒也救不回来，继续响就成了纯噪音（人已经离开房间了）。
    /// </summary>
    private const int AwAfkTimeoutSeconds = 180;

    private readonly GroupRules _rules;
    private readonly AwClient _aw = new();
    private readonly DispatcherTimer _tick = new();
    private string? _winBucket, _afkBucket;
    private bool _busy;

    public TaskRecord Task { get; private set; }
    public TaskState? State { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    /// <summary>
    /// 休息扇形的起点 = 专注达成那一刻；不在休息阶段就是 null。
    ///
    /// 配合 <see cref="TaskRecord.RestMinutes"/> 在表盘上画一块灰扇形（§8.4.4）。
    /// **扇形是静止的，不缩不淡** —— 倒计时由分针免费提供：分针扫出扇形，休息就完了。
    /// </summary>
    public DateTimeOffset? RestFrom { get; private set; }

    /// <summary>
    /// 灰弧要画多长：从**写入头**（已走完的整分钟数）一直到**预计结束时刻**
    /// （<see cref="Replay.ProjectedEnd"/>）。
    ///
    /// 所以偷懒一分钟，灰弧就往前长一分钟 —— 用户看着截止线离自己越来越远。
    /// </summary>
    public double RemainingMinutes
    {
        get
        {
            if (State is not { } st) return Task.FocusMinutes;
            // 达成之后什么都不欠了。这一条必须显式写：色环已经清空，
            // 写入头退回 StartedAt，不挡一下的话灰弧会整段重新冒出来。
            if (st.FocusCompletedAt is not null) return 0;
            var head = Task.StartedAt.AddMinutes(Cells.Count);
            return Math.Max(0, (Replay.ProjectedEnd(Task, st) - head).TotalMinutes);
        }
    }

    public bool InRest => State?.FocusCompletedAt is not null;
    public bool Finished { get; private set; }

    /// <summary>每轮算完都会响一次，界面据此重画。</summary>
    public event Action? Updated;
    public event Action<Interrupt>? Interrupted;


    public TaskSession(TaskRecord task, GroupRules rules)
    {
        Task = task;
        _rules = rules;
        // 起算时刻本身就是整分钟，把它当成"已经查过的那一分钟" ——
        // 于是点击这一刻不查 AW，第一次查询发生在下一个整分钟。
        _lastAwMinute = task.StartedAt;
        _tick.Interval = TimeSpan.FromSeconds(1);   // 秒级用来数休息和空闲；查 AW 只在整分钟
        _tick.Tick += OnTick;
        _tick.Start();
        Log.Info($"Task started. Goals: {string.Join(", ", task.Groups)}  focus {task.FocusMinutes} min  " +
                 $"from {task.StartedAt:HH:mm:ss}  break {task.RestMinutes} min");
    }

    /// <summary>中途补勾小目标。§5.4：用当前集合的并集重放整段历史，**追溯生效**。</summary>
    public void SetGroups(IReadOnlyList<string> groups)
    {
        Task = Task with
        {
            Groups = groups,
            GroupChanges = [.. Task.GroupChanges, new GroupChange(DateTimeOffset.Now, groups)],
        };
        Log.Info($"Goal selection changed mid-task: {string.Join(", ", groups)} (applies retroactively to the whole span)");
        _lastAwMinute = DateTimeOffset.MinValue;   // 下一拍立刻重算，不等整分钟
    }

    /// <summary>
    /// 已经查过的最后那个**整分钟**。
    ///
    /// 查询节拍锚在**整分钟**上，不是锚在点击时刻上（用户 2026-07-28 纠正）。
    /// 原来这里是 `_lastAwAt = MinValue` + 「距上次满 60 秒就查」，于是节拍跟着点击
    /// 时刻漂：23:59:43 点的开始 → 23:59:44 查一次、00:00:45 再查 —— 而计时点本该是
    /// 00:00:00。整整晚了 45 秒，用户看着表盘在整分钟毫无反应。
    ///
    /// 初值 = <c>Task.StartedAt</c>（已经是整分钟），所以**点击那一刻不查**：此刻要做的
    /// 只有一件事，把整段灰弧画上去。第一次查询发生在起算之后的第一个整分钟。
    /// </summary>
    private DateTimeOffset _lastAwMinute;
    private int _lastCellCount = -1;


    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || Finished) return;
        var now = DateTimeOffset.Now;

        // ---- 休息阶段：**纯本地计时，零 AW 访问**（§8.4.4a）
        if (State?.FocusCompletedAt is { } done)
        {
            var rest = TimeSpan.FromMinutes(Task.RestMinutes);
            RestFrom = done;
            Updated?.Invoke();
            if (now >= done + rest)
            {
                Finished = true;
                RestFrom = null;          // 扇形随休息一起消失
                _tick.Stop();
                Log.Info("Break over; task finished. The program never starts the next round for you (principle 1).");
                Interrupted?.Invoke(Interrupt.RestDone);
            }
            return;
        }

        // ---- 计时点：每跨过一个整分钟一次。**所有判断都在这里做**（用户 2026-07-28）。
        //
        // 1 秒的 tick 只用来盯休息什么时候走完；查 AW 与重放每整分钟一次，误差 ≤1 秒。
        var minute = TimeGrid.FloorToMinute(now);
        if (minute <= _lastAwMinute) return;
        _lastAwMinute = minute;

        // 1) 键鼠空闲。算在查 AW **之前**，这样 AW 连不上时也不会把它一起吞掉。
        //
        // 只在 [60, 180) 这个窗口里叫：AW 要安静满 180 秒才翻成 afk，而且事件起点会
        // 回填到最后一次输入（§14.4a T5）—— 必须赶在那条截止线【之前】把人叫醒，
        // 事后再叫是救不回来的。过了 180 秒就闭嘴，那时人多半真的离开了，
        // 每分钟响一声只是噪音。
        var idle = InputIdle.Elapsed().TotalSeconds;
        var idleNudge = idle is >= IdleNudgeSeconds and < AwAfkTimeoutSeconds;
        if (idleNudge)
            Log.Info($"No input for {idle:F0}s, nudging (in another {AwAfkTimeoutSeconds - idle:F0}s this time is written off)");

        // 2) 查 AW、重放。
        var focusDone = false;

        _busy = true;
        try
        {
            _winBucket ??= await _aw.FindBucketIdAsync(AwClient.WindowBucketType);
            _afkBucket ??= await _aw.FindBucketIdAsync(AwClient.AfkBucketType);
            var win = await _aw.FetchEventsAsync(_winBucket, Task.StartedAt, now);
            var afk = await _aw.FetchEventsAsync(_afkBucket, Task.StartedAt, now);

            State = Replay.Run(Task, _rules, win, afk, now);
            var cells = Replay.ToMinuteCells(Task, State);

            // **达成之后色环就撤掉**（用户 2026-07-28）：任务已经结束、不再查 AW，
            // 那一圈记录没有继续画的理由了。盘面只留休息扇形（§8.4.4）——
            // 空出来的盘面本身就是"这一块归你了"的底子。
            Cells = State.FocusCompletedAt is null ? cells : [];
            Updated?.Invoke();

            // 每拍记一行。这是这个程序**唯一**能让人事后看出"它到底有没有在数"的地方
            // ——界面对用户是沉默的，日志就得把过程留下来。一分钟一行，一轮任务
            // 最多五十行，1MB 的滚动上限绰绰有余。
            Log.Info($"{State.FocusedSeconds / 60,5:F1}/{Task.FocusMinutes} min  " +
                     $"{State.Phase}  cells {cells.Count}  " +
                     $"off-task {State.Violations.Count}x {State.OffTaskSecondsByApp.Values.Sum() / 60:F1}min  " +
                     $"away {State.AbsentSeconds / 60:F1}min  no-data {State.GapSeconds / 60:F1}min");

            if (State.FocusCompletedAt is not null)
            {
                focusDone = true;
                Log.Info($"Focus completed at {State.FocusCompletedAt.Value.ToLocalTime():HH:mm:ss}, " +
                         $"wall-clock {(State.FocusCompletedAt.Value - Task.StartedAt).TotalMinutes:F1} min");
            }
            // 用【刚走完的那一格】当触发条件，不是【此刻在干什么】。否则 10:00:10 切走、
            // 10:00:50 切回这种短切换会整个从提醒里溜掉 —— 而它在色块上明明是红的。
            else if (cells.Count > 0 && cells.Count != _lastCellCount)
            {
                _lastCellCount = cells.Count;
                var last = cells[^1];
                // 跑偏**只记一行日志**，不打断用户（用户 2026-07-28）。
                // 提醒的活交给表盘：那一格是红的，灰弧往前滑了一截。
                if (last.OffTaskSeconds >= NudgeFloorSeconds)
                    Log.Info($"The minute just past had {last.OffTaskSeconds:F0}s off-task");
            }
        }
        catch (Exception ex)
        {
            // §6.2：临时连不上不损坏任何东西 —— 历史在 aw-server 里，恢复后重查就补齐了。
            // 界面上不显示编造的进度，原因写进日志。
            Log.Error("ActivityWatch query failed this tick; skipping it (does not affect the final result, §6.2)", ex);
        }
        finally { _busy = false; }

        // 一个计时点最多响一声，达成优先。
        if (focusDone) Interrupted?.Invoke(Interrupt.FocusDone);
        else if (idleNudge) Interrupted?.Invoke(Interrupt.Idle);
    }

    /// <summary>放弃任务。退出程序等价于此（§2、§9）。</summary>
    public void Abandon()
    {
        if (Finished) return;
        Finished = true;
        _tick.Stop();
        Task = Task with { Status = RecordStatus.Abandoned, AbandonedAt = DateTimeOffset.Now };
        Log.Info($"Task abandoned. Focused {(State?.FocusedSeconds ?? 0) / 60:F1}/{Task.FocusMinutes} min");
    }

    public void Dispose()
    {
        _tick.Stop();
        _aw.Dispose();
    }
}
