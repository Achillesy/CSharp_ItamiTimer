using Avalonia.Threading;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// 一次任务的运行时（DESIGN.md §8.3.5 / ISSUE_FIX.md §7）。
///
/// 永远走约束模式——不再有 Pomodoro 退化（#6）。
/// </summary>
public sealed class TaskSession : IDisposable
{
    /// <summary>
    /// 三件值得响一声的事。每一件都能在设置里单独关掉（§8.3.1）。
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

    private readonly GroupRules _rules;
    private readonly AwClient _aw;
    private readonly DispatcherTimer _tick = new();
    private string? _winBucket, _afkBucket;
    private bool _busy;

    public TaskRecord Task { get; private set; }
    public TaskState? State { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    public DateTimeOffset? RestFrom { get; private set; }

    public double RemainingMinutes
    {
        get
        {
            if (State is not { } st) return Task.FocusMinutes;
            if (st.FocusCompletedAt is not null) return 0;
            var head = Task.StartedAt.AddMinutes(Cells.Count);
            return Math.Max(0, (Replay.ProjectedEnd(Task, st) - head).TotalMinutes);
        }
    }

    public bool InRest => State?.FocusCompletedAt is not null;
    public bool Finished { get; private set; }

    public event Action? Updated;
    public event Action<Interrupt>? Interrupted;

    public TaskSession(TaskRecord task, GroupRules rules, string awBaseUrl = "http://127.0.0.1:5600")
    {
        Task = task;
        _rules = rules;
        _aw = new AwClient(awBaseUrl);
        _lastAwMinute = task.StartedAt;
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += OnTick;
        _tick.Start();
        Log.Info($"Task started. Goal: {task.Group}  focus {task.FocusMinutes} min  " +
                 $"from {task.StartedAt:HH:mm:ss}  break {task.RestMinutes} min");
    }

    private DateTimeOffset _lastAwMinute;
    private int _lastCellCount = -1;

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || Finished) return;
        var now = DateTimeOffset.Now;

        // ---- 休息阶段：纯本地计时
        if (State?.FocusCompletedAt is { } done)
        {
            var rest = TimeSpan.FromMinutes(Task.RestMinutes);
            RestFrom = done;
            Updated?.Invoke();
            if (now >= done + rest)
            {
                Finished = true;
                RestFrom = null;
                _tick.Stop();
                Log.Info("Break over; task finished.");
                Interrupted?.Invoke(Interrupt.RestDone);
            }
            return;
        }

        // ---- 计时点：每整分钟一次
        var minute = TimeGrid.FloorToMinute(now);
        if (minute <= _lastAwMinute) return;
        _lastAwMinute = minute;

        // 1) 键鼠空闲
        var idle = InputIdle.Elapsed().TotalSeconds;
        var idleNudge = idle is >= IdleNudgeSeconds and < AwAfkTimeoutSeconds;
        if (idleNudge)
            Log.Info($"No input for {idle:F0}s, nudging (in another {AwAfkTimeoutSeconds - idle:F0}s this time is written off)");

        // 2) 查 AW、重放
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

            Cells = State.FocusCompletedAt is null ? cells : [];
            Updated?.Invoke();

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
            else if (cells.Count > 0 && cells.Count != _lastCellCount)
            {
                _lastCellCount = cells.Count;
                var last = cells[^1];
                if (last.OffTaskSeconds >= NudgeFloorSeconds)
                    Log.Info($"The minute just past had {last.OffTaskSeconds:F0}s off-task");
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
