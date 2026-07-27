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
    public enum Interrupt
    {
        /// <summary>刚走完的那一分钟跑偏了（§8.3.5）。置顶、不抢焦点。</summary>
        Deviated,
        /// <summary>超过阈值没动键鼠，赶在 AW 判 afk 之前叫醒（§8.3.6）。置顶。</summary>
        Idle,
        /// <summary>专注达成，给账单、进入休息（§8.4.3）。**不置顶**。</summary>
        FocusDone,
        /// <summary>休息结束，任务终结（§8.4.3）。**不置顶**，纯提示。</summary>
        RestDone,
    }

    // §8.3.5 / §8.3.6 / §10
    public const int TickSeconds = 60;
    public const int IdleNudgeSeconds = 60;
    public const int NudgeFloorSeconds = 5;
    private const int AwAfkTimeoutSeconds = 180;

    private readonly GroupRules _rules;
    private readonly AwClient _aw = new();
    private readonly DispatcherTimer _tick = new();
    private string? _winBucket, _afkBucket;
    private bool _busy;

    public TaskRecord Task { get; private set; }
    public TaskState? State { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    /// <summary>专注阶段恒为 1；休息阶段按分钟线性淡到 0（§8.4.4）。</summary>
    public double RingOpacity { get; private set; } = 1;

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
            var head = Task.StartedAt.AddMinutes(Cells.Count);
            return Math.Max(0, (Replay.ProjectedEnd(Task, st) - head).TotalMinutes);
        }
    }

    public bool InRest => State?.FocusCompletedAt is not null;
    public bool Finished { get; private set; }

    /// <summary>每轮算完都会响一次，界面据此重画。</summary>
    public event Action? Updated;
    public event Action<Interrupt>? Interrupted;

    /// <summary>
    /// 可以缩回去了。
    ///
    /// **必须独立于 <see cref="Updated"/>**：因为键鼠督促那一支是在【查 AW 之前】就
    /// return 的，所以窗口一旦因为"没动键鼠"弹出来，Updated 就再也不响，缩回的判断
    /// 根本没机会执行 —— 这正是 2026-07-27 实机撞到的「动了鼠标窗口也不消失」。
    ///
    /// 现在两条路各走各的：
    /// <list type="bullet">
    /// <item>因空闲弹出的 → **一动键鼠就缩**，秒级、纯本地、不用等 AW</item>
    /// <item>因偏离弹出的 → 下一个 AW 节拍确认那一格干净了再缩（§0.5 问题 3）</item>
    /// </list>
    /// </summary>
    public event Action? Retract;

    public TaskSession(TaskRecord task, GroupRules rules)
    {
        Task = task;
        _rules = rules;
        _tick.Interval = TimeSpan.FromSeconds(1);   // 秒级只用来数休息和空闲，查 AW 仍是 60 秒一次
        _tick.Tick += OnTick;
        _tick.Start();
        Log.Info($"任务开始：{string.Join("、", task.Groups)}  专注 {task.FocusMinutes} 分钟  " +
                 $"起算 {task.StartedAt:HH:mm:ss}  休息 {task.RestMinutes} 分钟");
    }

    /// <summary>中途补勾小目标。§5.4：用当前集合的并集重放整段历史，**追溯生效**。</summary>
    public void SetGroups(IReadOnlyList<string> groups)
    {
        Task = Task with
        {
            Groups = groups,
            GroupChanges = [.. Task.GroupChanges, new GroupChange(DateTimeOffset.Now, groups)],
        };
        Log.Info($"中途改勾选：{string.Join("、", groups)}（追溯整段历史生效）");
        _lastAwAt = DateTimeOffset.MinValue;   // 下一拍立刻重算
    }

    private DateTimeOffset _lastAwAt = DateTimeOffset.MinValue;
    private int _lastCellCount = -1;
    private DateTimeOffset _lastIdleNudge = DateTimeOffset.MinValue;
    private bool _nudgedIdle;

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || Finished) return;
        var now = DateTimeOffset.Now;

        // ---- 休息阶段：**纯本地计时，零 AW 访问**（§8.4.4a）
        if (State?.FocusCompletedAt is { } done)
        {
            var rest = TimeSpan.FromMinutes(Task.RestMinutes);
            var left = rest > TimeSpan.Zero ? 1 - (now - done) / rest : 0;
            RingOpacity = Math.Clamp(left, 0, 1);
            Updated?.Invoke();
            if (now >= done + rest)
            {
                Finished = true;
                _tick.Stop();
                Log.Info("休息结束，任务终结。程序不会替用户开始下一轮（原则 1）。");
                Interrupted?.Invoke(Interrupt.RestDone);
            }
            return;
        }

        // ---- 1/2：键鼠空闲。必须在查 AW 之前，它决定本轮还要不要往下走。
        var idle = InputIdle.Elapsed().TotalSeconds;
        if (idle >= IdleNudgeSeconds)
        {
            // AW 要安静满 180 秒才翻成 afk，且事件起点会回填到最后一次输入（§14.4a T5）。
            // 必须赶在那条截止线【之前】把人叫醒 —— 事后再叫是救不回来的。
            if ((now - _lastIdleNudge).TotalSeconds >= TickSeconds)
            {
                _lastIdleNudge = now;
                _nudgedIdle = true;
                Log.Info($"{idle:F0} 秒没动键鼠，催一下（再过 {Math.Max(0, AwAfkTimeoutSeconds - idle):F0} 秒就白费）");
                Interrupted?.Invoke(Interrupt.Idle);
            }
            return;
        }

        // 人动了 —— 因空闲弹出的窗口立刻可以缩回去。这一步是秒级的、纯本地的，
        // **不等 AW**：等 AW 就要等满一个 60 秒节拍，用户会觉得"动了鼠标也不消失"。
        if (_nudgedIdle)
        {
            _nudgedIdle = false;
            Log.Info("键鼠恢复，收回督促窗");
            Retract?.Invoke();
        }

        // ---- 3：查 AW、重放。60 秒一次。
        if ((now - _lastAwAt).TotalSeconds < TickSeconds) return;
        _lastAwAt = now;

        _busy = true;
        try
        {
            _winBucket ??= await _aw.FindBucketIdAsync(AwClient.WindowBucketType);
            _afkBucket ??= await _aw.FindBucketIdAsync(AwClient.AfkBucketType);
            var win = await _aw.FetchEventsAsync(_winBucket, Task.StartedAt, now);
            var afk = await _aw.FetchEventsAsync(_afkBucket, Task.StartedAt, now);

            State = Replay.Run(Task, _rules, win, afk, now);
            Cells = Replay.ToMinuteCells(Task, State);
            Updated?.Invoke();

            // 每拍记一行。这是这个程序**唯一**能让人事后看出"它到底有没有在数"的地方
            // ——界面对用户是沉默的，日志就得把过程留下来。一分钟一行，一轮任务
            // 最多五十行，1MB 的滚动上限绰绰有余。
            Log.Info($"{State.FocusedSeconds / 60,5:F1}/{Task.FocusMinutes} 分钟  " +
                     $"{State.Phase}  格子 {Cells.Count}  " +
                     $"偷懒 {State.Violations.Count} 次 {State.OffTaskSecondsByApp.Values.Sum() / 60:F1} 分  " +
                     $"离开 {State.AbsentSeconds / 60:F1} 分  无数据 {State.GapSeconds / 60:F1} 分");

            if (State.FocusCompletedAt is not null)
            {
                RingOpacity = 1;
                Log.Info($"专注达成于 {State.FocusCompletedAt:HH:mm:ss}，" +
                         $"实际耗时 {(State.FocusCompletedAt.Value - Task.StartedAt).TotalMinutes:F1} 分钟");
                Interrupted?.Invoke(Interrupt.FocusDone);
                return;
            }

            // 用【刚走完的那一格】当触发条件，不是【此刻在干什么】。否则 10:00:10 切走、
            // 10:00:50 切回这种短切换会整个从提醒里溜掉 —— 而它在色块上明明是红的。
            if (Cells.Count > 0 && Cells.Count != _lastCellCount)
            {
                _lastCellCount = Cells.Count;
                var last = Cells[^1];
                if (last.OffTaskSeconds >= NudgeFloorSeconds)
                {
                    Log.Info($"刚过去那一分钟有 {last.OffTaskSeconds:F0} 秒跑偏");
                    Interrupted?.Invoke(Interrupt.Deviated);
                }
                else
                {
                    Retract?.Invoke();   // 那一格干净了，因偏离弹出的窗口可以缩回去
                }
            }
        }
        catch (Exception ex)
        {
            // §6.2：临时连不上不损坏任何东西 —— 历史在 aw-server 里，恢复后重查就补齐了。
            // 界面上不显示编造的进度，原因写进日志。
            Log.Error("本轮查询 AW 失败，本轮跳过（不影响最终结果，§6.2）", ex);
        }
        finally { _busy = false; }
    }

    /// <summary>放弃任务。退出程序等价于此（§2、§9）。</summary>
    public void Abandon()
    {
        if (Finished) return;
        Finished = true;
        _tick.Stop();
        Task = Task with { Status = RecordStatus.Abandoned, AbandonedAt = DateTimeOffset.Now };
        Log.Info($"放弃任务。已专注 {(State?.FocusedSeconds ?? 0) / 60:F1}/{Task.FocusMinutes} 分钟");
    }

    public void Dispose()
    {
        _tick.Stop();
        _aw.Dispose();
    }
}
