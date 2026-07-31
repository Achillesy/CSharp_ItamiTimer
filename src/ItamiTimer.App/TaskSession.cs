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
    private readonly JudgmentBuffer _buffer;
    private string? _winBucket, _afkBucket;
    private bool _busy;

    public TaskRecord Task { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    public DateTimeOffset? RestFrom { get; private set; }

    public double RemainingMinutes
    {
        get
        {
            if (_buffer.IsFocusComplete) return 0;
            var focused = (int)_buffer.DuringSeconds + _buffer.CountFocused();
            var shouldSeconds = (DateTimeOffset.Now - Task.StartedAt).TotalSeconds;
            var shortfall = Math.Max(0, shouldSeconds - focused);
            var makeUpMinutes = Math.Ceiling(shortfall / 60.0);
            var projected = Task.StartedAt.AddMinutes(Task.FocusMinutes + makeUpMinutes);
            var head = Task.StartedAt.AddMinutes(Cells.Count);
            return Math.Max(0, (projected - head).TotalMinutes);
        }
    }

    public bool InRest => _buffer.IsFocusComplete && _focusDoneAt is not null;
    public bool Finished { get; private set; }
    public double FocusedSeconds() => _buffer.DuringSeconds + _buffer.CountFocused();

    public event Action? Updated;
    public event Action<Interrupt>? Interrupted;

    private DateTimeOffset? _focusDoneAt;

    public TaskSession(TaskRecord task, GroupRules rules, string awBaseUrl = "http://127.0.0.1:5600")
    {
        Task = task;
        _rules = rules;
        _aw = new AwClient(awBaseUrl);
        _buffer = new JudgmentBuffer(task.StartedAt, task.FocusMinutes);
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
        if (_focusDoneAt is { } done)
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

        // 2) 查 AW、更新 buffer（4 分钟窗口）
        var focusDone = false;

        _busy = true;
        try
        {
            _winBucket ??= await _aw.FindBucketIdAsync(AwClient.WindowBucketType);
            _afkBucket ??= await _aw.FindBucketIdAsync(AwClient.AfkBucketType);
            var queryStart = now.AddMinutes(-4);
            var win = await _aw.FetchEventsAsync(_winBucket, queryStart, now);
            var afk = await _aw.FetchEventsAsync(_afkBucket, queryStart, now);

            var classified = Judgment.ClassifySeconds(win, afk, _rules, Task.Group, queryStart, now);
            var bufferOffset = (int)(queryStart - _buffer.WallClock).TotalSeconds;
            _buffer.Write(bufferOffset, classified);
            _buffer.TryArchive();

            var cells = _buffer.ToMinuteCells();
            Cells = cells; // #11：专注完成后不消失，圆弧保留在休息扇形下层
            Updated?.Invoke();

            var focused = (int)_buffer.DuringSeconds + _buffer.CountFocused();
            Log.Info($"{focused / 60,5:F1}/{Task.FocusMinutes} min  " +
                     $"cells {cells.Count}  during {_buffer.DuringSeconds:F0}s");

            if (_buffer.IsFocusComplete && _focusDoneAt is null)
            {
                _focusDoneAt = _buffer.FocusCompletedAt();
                focusDone = true;
                Log.Info($"Focus completed at {_focusDoneAt?.ToLocalTime():HH:mm:ss}, " +
                         $"wall-clock {(_focusDoneAt?.Subtract(Task.StartedAt))?.TotalMinutes ?? 0:F1} min");
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
        var focused = (int)_buffer.DuringSeconds + _buffer.CountFocused();
        Log.Info($"Task abandoned. Focused {focused / 60:F1}/{Task.FocusMinutes} min  during={_buffer.DuringSeconds:F0}s");
    }

    public void Dispose()
    {
        _tick.Stop();
        _aw.Dispose();
    }
}
