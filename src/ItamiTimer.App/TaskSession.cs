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

    private readonly GroupRules _rules;
    private readonly AwClient _aw;
    private readonly DispatcherTimer _tick = new();
    private readonly JudgmentBuffer _buffer;
    private string? _winBucket, _afkBucket;
    private bool _busy;

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
    /// Focus seconds so far this round = already settled by archiving + still sitting in
    /// the buffer.
    ///
    /// **An integer** -- it counts cells in the buffer, not ActivityWatch event durations,
    /// so it never has a fractional part (user, 2026-08-02). Every division by 60 must
    /// therefore write <c>60.0</c>, otherwise integer division silently swallows the
    /// decimal places (DECISIONS G).
    /// </summary>
    public int FocusedSeconds() => _settledSeconds + _buffer.FocusedSeconds;

    /// <summary>
    /// Archiving settled a stretch of focus time (§4.4). The caller must credit it into
    /// during immediately (§11.2) -- that hour is about to be evicted from the buffer, and
    /// not recording it now means it's gone for good.
    /// </summary>
    public event Action<int>? Settled;

    /// <summary>
    /// Takes the "not yet credited" portion of focus seconds (= still sitting in the
    /// buffer) and voids it; called once when a task ends.
    ///
    /// Idempotent: repeated calls return 0. All three paths -- abandon, close the window,
    /// rest ending -- land here, and <b>double-crediting is harder to spot than a missed
    /// credit</b>, so it's guarded here rather than trusting every caller to be careful on
    /// their own.
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
        // The dial needs something to show the instant the button is pressed: the whole
        // grey arc is already laid out the moment the buffer is constructed (§4.5), so one
        // projection is all it takes -- no waiting for the first ActivityWatch response, and
        // the UI layer doesn't need to compute a separate copy.
        Cells = _buffer.ToMinuteCells();
        // The rest wedge gets its preview at the same instant: the deficit is still the
        // full commitment right now, and projecting it lands exactly on start + focus length.
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
                _tick.Stop();
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

            List<AwEvent> win, afk;
            try
            {
                _winBucket ??= await _aw.FindBucketIdAsync(AwClient.WindowBucketType);
                _afkBucket ??= await _aw.FindBucketIdAsync(AwClient.AfkBucketType);
                win = await _aw.FetchEventsAsync(_winBucket, queryStart, queryEnd);
                afk = await _aw.FetchEventsAsync(_afkBucket, queryStart, queryEnd);
            }
            catch (AwUnavailableException ex)
            {
                // §3.1's knowing fail-open: "the whole tick couldn't connect" and
                // "connected fine but this second has no record" are the same thing
                // (DESIGN §4.3) -- feed in empty event lists, and the judgment model will
                // fill this minute in as AwOffline on its own.
                //
                // **This does not skip the tick** -- skipping would freeze ElapsedSeconds
                // in place along with the commitment arc and the rest-wedge projection,
                // which is a "pause", not a fail-open, and doesn't match the design intent
                // (found by the user on 2026-08-02 during a real-machine test, stopping the
                // ActivityWatch server: the dial froze grey instead of turning the green it
                // should have).
                Log.Warn($"ActivityWatch unreachable this tick, treating as no data (fail-open): {ex.Message}");
                win = [];
                afk = [];
            }

            // This only produces a diagnostic log entry, it never changes judgment: when a
            // watcher quietly dies, AwOffline still counts as focus -- §3.1's knowing
            // fail-open, a cost the user explicitly accepted on 2026-08-02: if something's
            // wrong, the user checks the log themselves.
            if (!HasRecentEvent(win, queryEnd))
                Log.Warn($"No fresh window events in the last {AwStaleSeconds}s — aw-watcher-window may be stuck (or the machine just woke up)");
            if (!HasRecentEvent(afk, queryEnd))
                Log.Warn($"No fresh afk events in the last {AwStaleSeconds}s — aw-watcher-afk may be stuck (or the machine just woke up)");
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
                // Archiving = one ignore event (§11.2): that hour is about to be evicted from the buffer, credit it on the spot.
                _settledSeconds += outcome.SettledSeconds;
                Settled?.Invoke(outcome.SettledSeconds);
                Log.Info($"Archived an hour; {outcome.SettledSeconds}s banked into during.");
            }

            var cells = _buffer.ToMinuteCells();
            Cells = cells; // #11: doesn't disappear once focus completes, the arc stays underneath the rest wedge
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

    /// <summary>As long as a watcher is alive its heartbeat keeps going, and an event's End
    /// keeps advancing right up against now -- regardless of whether windows switch or
    /// whether anyone's present. Checking this avoids a separate query against
    /// ActivityWatch's bucket metadata.</summary>
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
