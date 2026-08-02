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

    /// <summary>
    /// 诊断阈值，不参与判定（DESIGN §16.5）：某个 bucket 心跳只要活着就会持续推进，
    /// 一拍里连一条贴近 <c>now</c> 的事件都没有，多半是 watcher 死了（也可能是机器刚睡醒——
    /// 两者在这个信号下看着一样，不去猜，只记日志，用户自己去查）。
    /// </summary>
    private const int AwStaleSeconds = 60;

    private readonly GroupRules _rules;
    private readonly AwClient _aw;
    private readonly DispatcherTimer _tick = new();
    private readonly JudgmentBuffer _buffer;
    private string? _winBucket, _afkBucket;
    private bool _busy;

    public TaskRecord Task { get; private set; }
    public IReadOnlyList<MinuteCell> Cells { get; private set; } = [];

    /// <summary>
    /// 休息扇形的起点。**专注达成之前是投影值**（= 承诺弧末端对应的墙钟时刻，
    /// 每拍跟着缺口重算），**达成之后锁定为实际达成时刻**——两者在缺口归零那一拍
    /// 是同一个数，交接不跳变。任务一构造完就有值，不用等第一拍 AW 回来。
    /// </summary>
    public DateTimeOffset? RestFrom { get; private set; }

    public bool InRest => _focusDoneAt is not null;
    public bool Finished { get; private set; }

    /// <summary>
    /// 本轮到目前为止的专注秒数 = 已归档结算的 + 还留在 buffer 里的。
    ///
    /// **整数**——它数的是 buffer 里的格子，不是 AW 事件的 duration，永远不会有小数
    /// （用户 2026-08-02）。所以除以 60 的地方一律要写 <c>60.0</c>，
    /// 否则整数除法会把小数位悄悄吃掉（DECISIONS G）。
    /// </summary>
    public int FocusedSeconds() => _settledSeconds + _buffer.FocusedSeconds;

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
    public int TakeUnbankedSeconds()
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
        // 休息扇形同一刻就有预告：此时缺口还是整段承诺，投影出来正好是 起点+专注时长。
        RestFrom = _buffer.TaskStart.AddSeconds(_buffer.ElapsedSeconds + _deficitSeconds);
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
            //
            // 这里只做诊断日志，不改判定：watcher 悄悄死掉时 AwOffline 计入专注，
            // §3.1 的知情 fail-open，代价用户 2026-08-02 明确接受——出问题让用户自己查日志。
            if (!HasRecentEvent(win, queryEnd))
                Log.Warn($"No fresh window events in the last {AwStaleSeconds}s — aw-watcher-window may be stuck (or the machine just woke up)");
            if (!HasRecentEvent(afk, queryEnd))
                Log.Warn($"No fresh afk events in the last {AwStaleSeconds}s — aw-watcher-afk may be stuck (or the machine just woke up)");
            var outcome = _buffer.Tick(minute, win, afk, _rules, Task.Group);
            _deficitSeconds = outcome.DeficitSeconds;

            // 休息扇形不等达成才画：**它的起点就是承诺弧的末端**（DESIGN §4.5：
            // 「承诺弧消失的那一刻 = 专注达成的那一刻」），任务一开始就有了，不用等
            // 真达成。每拍重算、不记状态——跟判定引擎同一条原则（原则 4）。
            //
            // 这也是故意的痛感设计：拖延时缺口不减、ElapsedSeconds 照样 +60，
            // 投影出来的休息起点跟着**一起往后挪**——不只是灰弧变长，连挣来的休息
            // 也在实时后退。真正达成那一拍，这个投影值恰好等于 `minute` 本身，
            // 跟下面 `_focusDoneAt` 落定后 `RestFrom = done` 是同一个数，交接不跳变。
            RestFrom = _buffer.TaskStart.AddSeconds(_buffer.ElapsedSeconds + outcome.DeficitSeconds);

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

            Log.Info($"{FocusedSeconds() / 60.0,5:F1}/{Task.FocusMinutes} min  " +
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

    /// <summary>watcher 只要活着就会持续心跳，事件的 End 会一直贴着 now 往前挪——
    /// 不管窗口切不切换、人在不在。查这个就不用另外去问 AW 的 bucket 元数据。</summary>
    private static bool HasRecentEvent(IReadOnlyList<AwEvent> events, DateTimeOffset now)
    {
        var cutoff = now.AddSeconds(-AwStaleSeconds);
        foreach (var e in events)
            if (e.End >= cutoff) return true;
        return false;
    }

    public void Abandon()
    {
        if (Finished) return;
        Finished = true;
        _tick.Stop();
        Task = Task with { Status = RecordStatus.Abandoned, AbandonedAt = DateTimeOffset.Now };
        Log.Info($"Task abandoned. Focused {FocusedSeconds() / 60.0:F1}/{Task.FocusMinutes} min");
    }

    public void Dispose()
    {
        _tick.Stop();
        _aw.Dispose();
    }
}
