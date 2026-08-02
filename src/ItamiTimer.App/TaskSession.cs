using Avalonia.Threading;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// 一次任务的运行时：每整分钟驱动 <see cref="JudgmentBuffer"/> 走一拍（DESIGN.md §4.2），
/// 把投影出来的格子交给界面，并在三件事上响一声。
///
/// **永远走约束模式**——退化成纯番茄钟那条路 2026-07-31 整个删掉了（DECISIONS B3）：
/// AW 不可用由判定模型自己吸收（§3.1），界面不随 AW 在不在而变形。
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

    public bool InRest => _focusDoneAt is not null;
    public bool Finished { get; private set; }

    /// <summary>本轮到目前为止的专注秒数 = 已归档结算的 + 还留在 buffer 里的。只用于显示。</summary>
    public double FocusedSeconds() => _settledSeconds + _buffer.FocusedSeconds;

    /// <summary>
    /// 归档结算掉了一段专注时间（§4.4）。调用方要立刻把它记进 during（§11.2）——
    /// 那一小时马上就要被移出 buffer 了，不记就永远没了。
    /// </summary>
    public event Action<int>? Settled;

    /// <summary>
    /// 取走「还没入账」的那部分专注秒数（= 仍留在 buffer 里的）并作废，任务终结时调一次。
    ///
    /// 幂等：重复调用返回 0。放弃、关窗口、休息结束三条路都会走到这里，
    /// <b>重复入账比漏账更难查</b>，所以在这里挡死而不是在调用方各自小心。
    /// </summary>
    public double TakeUnbankedSeconds()
    {
        if (_banked) return 0;
        _banked = true;
        return _buffer.FocusedSeconds;
    }

    private bool _banked;
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
        _deficitSeconds = task.FocusMinutes * 60;
        _lastAwMinute = task.StartedAt;
        // 点下按钮那一刻盘面就要有东西：整段灰弧在构造 buffer 时就已经铺好了（§4.5），
        // 投影一次即可，不用等第一次 AW 回来，也不用界面层另算一份。
        Cells = _buffer.ToMinuteCells();
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
            // 查询区间锚在整分钟上，绝不掺 now 的亚秒零头（DESIGN §4.2 / DECISIONS H9）
            var queryEnd = minute;
            var queryStart = queryEnd.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);
            var win = await _aw.FetchEventsAsync(_winBucket, queryStart, queryEnd);
            var afk = await _aw.FetchEventsAsync(_afkBucket, queryStart, queryEnd);

            // AW 连不上不会走到这里（上面已经抛了）—— 那条路在 catch 里，
            // 事件列表为空即可，判定模型自己会把这一分钟填成 AwOffline。
            var outcome = _buffer.Tick(minute, win, afk, _rules, Task.Group);
            _deficitSeconds = outcome.DeficitSeconds;
            if (outcome.SettledSeconds > 0)
            {
                // 归档 = 一次 ignore（§11.2）：那一小时马上要被移出 buffer，当场入账。
                _settledSeconds += outcome.SettledSeconds;
                Settled?.Invoke(outcome.SettledSeconds);
                Log.Info($"Archived an hour; {outcome.SettledSeconds}s banked into during.");
            }

            var cells = _buffer.ToMinuteCells();
            Cells = cells; // #11：专注完成后不消失，圆弧保留在休息扇形下层
            Updated?.Invoke();

            Log.Info($"{FocusedSeconds() / 60,5:F1}/{Task.FocusMinutes} min  " +
                     $"cells {cells.Count}  deficit {outcome.DeficitSeconds}s  " +
                     $"settled {_settledSeconds}s");

            if (outcome.Completed && _focusDoneAt is null)
            {
                // 达成时刻**就是这一拍**，不回头去账本里推（DESIGN §4.5 / DECISIONS H5）。
                _focusDoneAt = minute;
                focusDone = true;
                Log.Info($"Focus completed at {minute:HH:mm:ss}, " +
                         $"wall-clock {(minute - Task.StartedAt).TotalMinutes:F1} min");
            }
            else if (cells.Count > 0 && cells.Count != _lastCellCount)
            {
                _lastCellCount = cells.Count;
                var last = cells[^1];
                if (last.OffTaskSeconds >= NudgeFloorSeconds)
                    Log.Info($"The minute just past had {last.OffTaskSeconds}s off-task");
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
        Log.Info($"Task abandoned. Focused {FocusedSeconds() / 60:F1}/{Task.FocusMinutes} min");
    }

    public void Dispose()
    {
        _tick.Stop();
        _aw.Dispose();
    }
}
