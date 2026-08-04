using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Linq;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// Main window.
///
/// **This layer only renders the <see cref="MinuteCell"/> list and collects the user's
/// submission** — judgment and accounting live entirely in Core, the tick lives in
/// <see cref="TaskSession"/>.
///
/// Layout: **the "Start" button is the dividing line itself** — above it is the dial and
/// the dominoes, for the eyes; below it are the value controls and the goal list, for the
/// hand. Nothing below the divider ever explains itself (§8.6); the reasons for a failure
/// only go to the log (§8.1a).
///
/// Visible states:
/// <code>
/// Idle                  An empty dial is the invitation for the next round
/// In progress           The window stays put (user, 2026-07-27); the coloured cells grow by one per minute
/// No input for 60s      One beep, to wake you up before ActivityWatch marks you afk (C3)
/// Focus completed       One beep, into rest. No report — the dial speaks for itself (B4)
/// Resting                A blue wedge; the minute hand sweeps it away when it's over
/// Rest over             One beep, back to the empty dial, waiting for the user (B2: never auto-starts the next round)
/// </code>
///
/// ⚠️ This table has **no "pin / pop up / minimize to tray" anywhere** — the whole
/// auto-pinning scheme was abandoned on 2026-07-28 (C1: "pop up" and "never steal focus"
/// contradict each other by definition). The pin icon in the top-right corner is a manual
/// toggle; it isn't the same mechanism as the one that got cut.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>The second hand needs sub-second continuous repaint (§8.2.6). **Only runs while the window is visible** — stops when minimized to the tray, a free power saving.</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<RadioButton> _goalRadios = [];
    private readonly List<TextBlock> _duringLabels = [];
    private readonly Settings _settings = Settings.Load();

    /// <summary>Accumulated focus time per goal (§11.2). Read in at startup; saved to disk immediately on every credit.</summary>
    private readonly During _during = During.Load();

    private TaskSession? _session;

    // Alarm: the whole model lives in AlarmClock (testable pure logic); this is just the
    // scroll-wheel wiring. Left/right click was removed (2026-07-30, user: "so it doesn't
    // box in future features") — the wheel is the only input.
    private readonly AlarmClock _alarm = new();
    // Suppress spurious triggers during adjustment. Uses a "quiet-until deadline" rather
    // than a bool: the wheel produces a continuous stream of discrete events, and a
    // bool + Task.Delay reset would race itself (an earlier reset cancelling a later
    // adjustment window).
    private DateTime _alarmQuietUntil = DateTime.MinValue;

    // How many times the alarm rings when it fires (user, 2026-08-03). A single system
    // sound is one or two seconds long — short enough to lose to a passing thought, and
    // the alarm is the one sound with no second chance: the three notification sounds are
    // tied to a state that's still on screen afterward, while the alarm leaves nothing
    // behind (the yellow hand doesn't move, nothing pops up — §8.1a: the program never
    // interrupts).
    //
    // ⚠️ This is **not** DECISIONS E5's "daily repeating alarm": still one firing, still
    // one-shot, MarkFired above still runs before the first ring. Only the ringing itself
    // got longer.
    private const int AlarmRings = 4;

    // The three notification sounds ring twice (user, 2026-08-03) — the same reasoning as
    // above, weaker: each of them lands on something that stays on screen afterward (a
    // finished dial, a blue wedge, cells that stopped growing), so a missed beep can still
    // be recovered by looking. Two is enough to say "that was for you", four would turn
    // every completed pomodoro into an event.
    //
    // Fewer than the alarm on purpose, and the gap is what enforces it: these fire off the
    // whole-minute tick, and Idle can fire on two consecutive minutes — so a sequence has
    // to comfortably finish inside a minute even with the longest system sound picked
    // (Ring05 is 12.8s: twice is 26s, four times would overrun into the next nudge).
    private const int NotifyRings = 2;

    public MainWindow()
    {
        Log.Info($"Started. Log: {Log.Path_}");

        InitializeComponent();
        ApplyTheme();
        Icon = RingIcon.Make(0, 0);   // Grey ring when idle; swapped for the progress ring while a task is running

        ApplySliderRange();
        LoadRules();
        RefreshStartButton();
        var gear = F<Button>("SettingsBtn");
        gear.Content = ChromeIcons.Gear();
        gear.Click += OnSettings;
        var dial = F<DialControl>("Dial");
        dial.PointerWheelChanged += OnAlarmWheel;
        _alarm.Restore(_settings.AlarmFireAt, DateTime.Now);
        dial.AlarmMinutes = _alarm.Position;
        // Turning the tick off cuts off the one that's mid-play; turning it on doesn't touch
        // anything (the next second boundary starts it). Deliberately **not** in ApplyChrome
        // — see the note there.
        F<Button>("MuteBtn").Click += (_, _) =>
        {
            _settings.TickEnabled = !_settings.TickEnabled;
            if (!_settings.TickEnabled) Tick.Stop();
            ApplyChrome();
            _settings.Save();
        };
        F<Button>("PinBtn").Click += (_, _) => { _settings.Pinned = !_settings.Pinned; ApplyChrome(); _settings.Save(); };
        ApplyChrome();

        _frame.Tick += OnFrame;
        _frame.Start();
        Closing += OnClosing;
        Closed += (_, _) => SaveAlarmOnExit();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The focus-length slider's range depends on the build configuration.
    ///
    /// **The axaml carries the real Release range** (10–50, step 5, default 25); Debug
    /// overrides it here to 3–10, step 1. The direction is deliberate: forgetting to change
    /// this only ever ships the **real** range.
    ///
    /// The other direction (axaml carries the test range, Release overrides it) has already
    /// caused an incident — the 2026-07-31 change hard-coded 3–10 unconditionally,
    /// `Value="25"` got silently clamped to 10, and nobody noticed Release had changed too.
    ///
    /// Core sets no bound (`TaskRecord` accepts any duration), and `RestMinutes = ⌈f/5⌉`
    /// must hold for **every integer** — otherwise what gets verified in Debug isn't
    /// Release's actual behaviour, and the test range would be pointless.
    /// </summary>
    private void ApplySliderRange()
    {
#if DEBUG
        var s = F<Slider>("Minutes");
        s.Minimum = 3;
        s.Maximum = 10;
        s.TickFrequency = 1;
        s.Value = 3;
#endif
    }

    private T F<T>(string name) where T : Control => this.FindControl<T>(name)!;

    /// <summary>§8.2.7: the dial follows the theme — plain white by day, deep grey by night; it's the day and night face of the same thing.</summary>
    private void ApplyTheme()
    {
        var palette = ActualThemeVariant == ThemeVariant.Dark ? DialPalette.Dark : DialPalette.Light;
        F<DialControl>("Dial").Palette = palette;
        var row = F<DominoRow>("Dominoes");
        row.Palette = palette;
        row.Fallen = DominoRow.FallenForToday(DateTime.Now);
    }

    private int _tickedSecond = -1;

    /// <summary>
    /// One frame every 33ms. Two jobs: keeping the second hand's jump timely (≤33ms
    /// latency), and **placing one tick sound on the second boundary**.
    ///
    /// **It still ticks when the window is minimized** (user, 2026-07-28): the tick is the
    /// clock itself running, independent of whether you're watching it. Only the repaint
    /// skips when it's not visible — that's purely a power saving, it doesn't affect sound.
    ///
    /// The tick rides this existing timer rather than starting a new one: it's naturally
    /// aligned to the wall clock and never drifts, and 33ms of jitter is inaudible on a 35ms
    /// "tock". The alternative — a whole 1-second buffer handed to `SND_LOOP` — costs zero
    /// CPU, but the audio clock slowly drifts apart from the system clock, and within an
    /// hour the second hand and the sound would no longer line up.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var visible = WindowState != WindowState.Minimized && IsVisible;
        if (visible) F<DialControl>("Dial").InvalidateVisual();

        var sec = DateTime.Now.Second;
        if (sec == _tickedSecond) return;
        _tickedSecond = sec;
        // #5: Force on -> force ticking, but mute during focus + rest
        var ticking = _settings.ForceTicking
            ? _session is not { Finished: false }   // No task, or the task has ended -> tick
            : _settings.TickEnabled;
        if (ticking) Tick.Play(sec, _settings.TickVolume);

        // Alarm check: the target moment is fixed the instant the yellow hand was set ->
        // fire once (not while it's being adjusted). The check runs **once per second**
        // (the second-boundary gate above), so it's at most 1 second late.
        if (DateTime.Now >= _alarmQuietUntil && _alarm.ShouldFire(DateTime.Now))
        {
            _alarm.MarkFired();   // One-shot — fires once and is done, not a daily repeating alarm.
                                  // Clearing it in memory is enough; SaveAlarmOnExit writes it back to null on exit.
            if (_settings.CommandEnabled) Command.Execute(_rules);
            else Sound.Repeat(_settings.CommandSound, AlarmRings);
        }
    }

    /// <summary>
    /// Refreshes the two top-right toggle icons and the window's pinned state to match
    /// what's in Settings.
    ///
    /// **The speaker only controls the tick** (user, 2026-07-28): the tick is the clock's
    /// own function, unrelated to the three notification sounds that nudge you back to
    /// work, so it isn't a "mute everything" switch. Those three sounds each have their own
    /// toggle in the settings window.
    ///
    /// The icons are **drawn as vectors** (<see cref="ChromeIcons"/>), not font glyphs.
    /// These two used to be Segoe Fluent Icons' `E767` / `E74F` / `E840` / `E718`, and that
    /// font only ships preinstalled on Windows — two tofu boxes on macOS. State is still
    /// expressed through the shape itself plus opacity, never text (there isn't a single
    /// word above the divider):
    ///
    /// | | Off | On |
    /// |---|---|---|
    /// | Speaker | A diagonal slash | Two sound waves |
    /// | Pin | Hollow (outline only) | Filled |
    ///
    /// The pin's off state **deliberately avoids an "X"**: at full opacity an X reads as
    /// "pinning is disabled", which is the opposite of "currently pinned". A slashed
    /// speaker has no such ambiguity — everyone reads a slashed speaker as muted.
    /// </summary>
    private void ApplyChrome()
    {
        var mute = F<Button>("MuteBtn");
        var pin = F<Button>("PinBtn");

        // #5: Force on -> hide the ticking icon, the user can't manually turn it off
        mute.IsVisible = !_settings.ForceTicking;
        mute.Content = ChromeIcons.Speaker(_settings.TickEnabled);
        mute.Classes.Set("on", _settings.TickEnabled);
        pin.Content = ChromeIcons.Pin(_settings.Pinned);
        pin.Classes.Set("on", _settings.Pinned);

        // ⚠️ Cutting off the tick that's already playing does **not** belong here (moved out
        // 2026-08-03, DECISIONS E13). ApplyChrome runs on four paths — construction, the
        // speaker, **the pin**, and closing Settings — and on Windows `Tick.Stop()` is
        // `PlaySound(null)`, which stops whatever this process has on winmm's single
        // channel, no matter who started it. Sitting here, it meant clicking the pin
        // silenced the alarm ring that happened to be playing. Stopping the tick is the
        // speaker's business, so it lives in the speaker's handler.
        WindowPin.Set(this, _settings.Pinned);
    }

    private void LoadRules()
    {
        try
        {
            _rules = GroupRules.Load(AppData.RulesPath());

            // Controls are built **once** and never rebuilt afterward (§15.2). rules.json
            // is only read once at startup, and the goal list never changes during a single
            // run — rebuilding the layout every time a task ends is both unnecessary and
            // exactly where that bug came from.
            var rows = new List<DockPanel>();
            foreach (var name in _rules.SelectableGroups)
            {
                var radio = new RadioButton
                {
                    Content = name,
                    GroupName = "Goals",
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Left,
                };
                radio.IsCheckedChanged += OnGoalToggled;
                _goalRadios.Add(radio);

                var label = new TextBlock
                {
                    FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.6,
                    [DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
                };
                _duringLabels.Add(label);

                rows.Add(new DockPanel
                {
                    LastChildFill = true,
                    Children = { radio, label, new TextBlock() },
                });
            }
            F<ItemsControl>("Goals").ItemsSource = rows;   // Also set only this once

            // First run: seed a during.json of all zeros for the current set of goals (§11.2)
            _during.EnsureSeeded(_rules.SelectableGroups);
            RefreshGoalItems();
            // Restore the previously selected goal, or pick the first one if it's gone
            var saved = _goalRadios.FirstOrDefault(r =>
                r.Content?.ToString() == _settings.SelectedGroup);
            if (saved is not null) saved.IsChecked = true;
            else if (_goalRadios.Count > 0) _goalRadios[0].IsChecked = true;
            Log.Info($"rules.json loaded. Goals: {string.Join(", ", _rules.SelectableGroups)}");
        }
        catch (Exception e)
        {
            _rules = null;
            Log.Error("Cannot read rules.json", e);
        }
    }

    private void OnGoalToggled(object? sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked is not null)
        {
            _settings.SelectedGroup = picked;
            _settings.Save();
        }
        RefreshStartButton();
    }

    private string? Picked()
        => _goalRadios.FirstOrDefault(r => r.IsChecked == true)?.Content?.ToString();

    /// <summary>
    /// Refreshes the accumulated-time numbers. **Only touches Text, never the control
    /// tree** (§15.2).
    ///
    /// This used to build a fresh batch of `DockPanel`s every time, stuff the same batch of
    /// `RadioButton` / `TextBlock` instances into them, and swap the whole `ItemsSource`.
    /// Avalonia doesn't allow a control to have two visual parents, so it threw
    /// `already has a visual parent DockPanel` — and because that was thrown inside
    /// `EndSession`, the `RefreshStartButton()` call after it got skipped entirely, and
    /// **the button stayed stuck on "Give up" forever**.
    ///
    /// "Remove from the old parent before re-adding" was tried at the time, but only for
    /// the RadioButton, missing the TextBlock — which is why it looked like the fix "didn't
    /// take". But that was patching a hole that shouldn't have existed in the first place:
    /// **treating live controls as an `ItemsSource` was the actual mistake**. The list never
    /// changes during a single run (`rules.json` is only read once at startup), so building
    /// it once is all that's needed.
    /// </summary>
    private void RefreshGoalItems()
    {
        for (var i = 0; i < _goalRadios.Count && i < _duringLabels.Count; i++)
        {
            var name = (string)_goalRadios[i].Content!;

            // What's on disk, plus this round's not-yet-credited seconds (§11.2). A pure
            // display sum — nothing is written, and the credit still happens only when
            // archiving or the task ends. The sum doesn't move at that handoff, so the
            // number doesn't jump when this term crosses over into during.json.
            //
            // It **can go backwards**: every tick repaints the last 4 minutes (§4.2),
            // because ActivityWatch backfills its afk verdict 180 seconds late. Walk away
            // and a minute already counted gets revoked. Accepted, knowingly (DECISIONS
            // D9) — the dial's cells and deadline arc already retreat the same way, and
            // holding a "highest seen this round" to keep it monotonic would be a piece of
            // UI state that lies (Principle 4: state is derived, not accumulated).
            var live = _session is { } s && s.Task.Group == name ? s.UnbankedSeconds : 0;
            _duringLabels[i].Text = ((_during[name] + live) / 3600.0).ToString("F2");
        }
    }

    private void RefreshStartButton()
    {
        var btn = F<Button>("StartBtn");
        if (_session is { Finished: false })
        {
            btn.Content = _session.InRest ? "New round" : "Give up";
            // Only "Give up" is coloured red: it voids the whole round. "New round" starts
            // the next task after focus was already achieved, so it isn't dangerous and
            // stays green (see the Button.start.danger style in MainWindow.axaml).
            btn.Classes.Set("danger", !_session.InRest);
            btn.IsEnabled = true;
            return;
        }
        btn.Content = "Start";
        btn.Classes.Set("danger", false);
        btn.IsEnabled = _rules is not null && Picked() is not null;
        btn.InvalidateVisual();
    }

    // ---------------------------------------------------------------- Task lifecycle

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        // Clicking it mid-task = give up; clicking it during rest = start a new round
        // (§8.4.6: no separate "skip rest" button is needed — starting a new task already
        // is skipping the rest)
        if (_session is { Finished: false })
        {
            if (_session.InRest) EndSession();
            else { _ = AskAbandonAsync(); return; }
        }

        // Radio buttons, single-select, locked once Start is pressed
        var picked = Picked();
        if (_rules is null || picked is null) return;

        // §14.1 (changed 2026-07-27): **truncate** to the current whole minute, don't round
        // up. Starting at 23:13:10 -> counted from 23:13:00. The cost is up to 59 seconds
        // before the click are counted for free; the benefit is starting immediately on
        // click instead of waiting.
        var task = new TaskRecord
        {
            StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
            FocusMinutes = (int)F<Slider>("Minutes").Value,
            Group = picked,
        };

        // Re-check the day of week when Start is clicked, and update the domino count.
        // This way, clicking across midnight reflects the new date; the check is cheap
        // enough that it's not noticeable.
        F<DominoRow>("Dominoes").Fallen = DominoRow.FallenForToday(DateTime.Now);

        _session = new TaskSession(task, _rules!, _settings.AwBaseUrl);
        _session.Updated += OnSessionUpdated;
        _session.Interrupted += OnInterrupted;
        // Archiving = one ignore event (§11.2): that hour is about to be evicted from the
        // buffer, so credit it immediately
        _session.Settled += seconds => Bank(task.Group, seconds);
        foreach (var r in _goalRadios) r.IsEnabled = false;   // Lock the selection once Start is pressed

        // The dial must show something the instant the button is pressed: the whole grey
        // arc goes up immediately, without waiting for the first ActivityWatch response.
        // Same for the rest wedge — it's a projected value now, already computed the
        // moment TaskSession is constructed (start + focus length).
        var dial = F<DialControl>("Dial");
        dial.StartedAt = task.StartedAt;
        dial.Cells = _session.Cells;   // The commitment arc is just the Gray span in the buffer, already there at construction time (§4.5)
        dial.RestFrom = _session.RestFrom;
        dial.RestMinutes = task.RestMinutes;
        dial.InvalidateVisual();

        RefreshStartButton();
        // §8.3 originally required "minimize to tray the instant a task starts"; the user
        // changed this to **stay put** on 2026-07-27. Related: returning to on-task only
        // un-pins now, it no longer minimizes (see the watchdog in OnFrame).
    }

    /// <summary>Whole minute the taskbar icon was last redrawn for — see the throttle in <see cref="OnSessionUpdated"/>.</summary>
    private DateTimeOffset? _lastIconMinute;

    private void OnSessionUpdated()
    {
        if (_session is not { } s) return;

        var dial = F<DialControl>("Dial");
        dial.StartedAt = s.Task.StartedAt;
        dial.Cells = s.Cells;
        dial.RestFrom = s.RestFrom;
        dial.RestMinutes = s.Task.RestMinutes;
        dial.InvalidateVisual();

        // §8.3.2: the taskbar icon is an **aggregate projection** — angle = completion
        // ratio, colour = overall purity. Only worth redrawing once a whole minute has
        // passed: during rest this method is called once a second, but the projection
        // barely moves tick to tick, so repainting that often was pure churn (user,
        // 2026-08-04) — same cadence as the dial's own per-minute judgment tick.
        var minute = TimeGrid.FloorToMinute(DateTimeOffset.Now);
        if (minute != _lastIconMinute)
        {
            _lastIconMinute = minute;
            var focused = s.FocusedSeconds();
            var progress = Math.Clamp(focused / (s.Task.FocusMinutes * 60.0), 0, 1);
            var elapsed = Math.Max(1, (DateTimeOffset.Now - s.Task.StartedAt).TotalSeconds);
            Icon = RingIcon.Make(progress, Math.Clamp(1 - focused / elapsed, 0, 1));
        }

        // The goal list's running total follows the same tick — no timer of its own, because
        // the number it shows only ever changes on the whole-minute tick anyway (during rest
        // this fires once a second, but nothing behind it moves, so it's a no-op repaint).
        RefreshGoalItems();
    }

    /// <summary>
    /// Three events each get one system beep, and each can be individually turned off in
    /// Settings (user, 2026-07-28).
    ///
    /// The whole "pinned but never steals focus" scheme has been cleaned out entirely —
    /// the user's own words: "Stop fussing over window pinning. The logic is a mess and
    /// keeps breaking." And it really did keep breaking: first the completion moment
    /// didn't pin at all (never-steal-focus plus never-pin means no pop-up happens), then
    /// after the fix it could never be un-pinned again. Sound has none of these states.
    ///
    /// Drifting off task no longer gets any notification, **only a log line**. The cell on
    /// the dial is red, the grey arc has slid forward a bit — you look, you guess, same
    /// philosophy as giving no report. Idle input is different: it's warning about a
    /// stretch of time **about to be voided by ActivityWatch marking you afk**, and once
    /// it's too late there's no getting it back (§8.3.5).
    /// </summary>
    private void OnInterrupted(TaskSession.Interrupt why)
    {
        if (_session is null) return;

        switch (why)
        {
            case TaskSession.Interrupt.FocusDone:
                if (_settings.FocusDoneEnabled) Sound.Repeat(_settings.FocusDoneSound, NotifyRings);
                RefreshStartButton();
                break;

            case TaskSession.Interrupt.RestDone:
                if (_settings.RestDoneEnabled) Sound.Repeat(_settings.RestDoneSound, NotifyRings);
                EndSession();
                break;

            case TaskSession.Interrupt.Idle:
                if (_settings.IdleEnabled) Sound.Repeat(_settings.IdleSound, NotifyRings);
                break;
        }
    }

    /// <summary>
    /// Credits a stretch of focus time to a goal (§11.2). <b>Every second is credited
    /// exactly once</b>: the hour that gets archived is credited on the spot in
    /// <see cref="TaskSession.Settled"/>, and whatever's left is taken idempotently by
    /// <see cref="TaskSession.TakeUnbankedSeconds"/>.
    /// </summary>
    private void Bank(string? goal, long seconds)
    {
        if (goal is null || seconds <= 0) return;
        _during.Add(goal, seconds);
    }

    /// <summary>Task ended (completed / abandoned / window closed): credit whatever hasn't been credited yet.</summary>
    private void BankRemainder()
    {
        if (_session is { } s) Bank(s.Task.Group, s.TakeUnbankedSeconds());
    }

    /// <summary>Task ended: back to the empty dial.</summary>
    private void EndSession()
    {
        BankRemainder();

        var old = _session;
        _session = null;
        try { old?.Dispose(); } catch { /* couldn't close it, doesn't matter — state is already cleared */ }

        foreach (var r in _goalRadios) r.IsEnabled = true;
        // Restore the previously selected radio, making sure Picked() isn't null
        var saved = _goalRadios.FirstOrDefault(r =>
            r.Content?.ToString() == _settings.SelectedGroup);
        if (saved is not null) saved.IsChecked = true;
        else if (_goalRadios.Count > 0) _goalRadios[0].IsChecked = true;

        var dial = F<DialControl>("Dial");
        dial.Cells = [];
        dial.StartedAt = null;
        dial.RestFrom = null;
        dial.InvalidateVisual();

        Icon = RingIcon.Make(0, 0);
        _lastIconMinute = null;
        RefreshGoalItems();
        RefreshStartButton();
    }

    // ---------------------------------------------------------------- Quitting = abandoning (§9)

    private bool _closeApproved;

    /// <summary>
    /// §9: **closing the window mid-focus = abandoning the task**, so it has to ask once.
    /// The title-bar ×, the taskbar's right-click "Close window", and Alt+F4 all land here.
    ///
    /// No task, or already resting (focus already achieved and already credited) →
    /// **quit right away, no question asked**.
    ///
    /// Closing is a synchronous event, so it can't await a dialog inline — cancel the
    /// close first, ask asynchronously, then close for real.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // No question when closing during rest — focus was already achieved. But it
        // still has to be **credited** (§11.2: quitting the program is also an ignore event).
        if (_session is { Finished: false, InRest: true }) { BankRemainder(); return; }
        if (_session is not { Finished: false }) return;

        e.Cancel = true;

        if (await Confirm.AskAsync(this, "The task isn't finished. Quit anyway?"))
        {
            _session?.Abandon();
            BankRemainder();     // Abandoned, but the time spent is still a fact
            _closeApproved = true;
            Close();
        }
    }

    /// <summary>
    /// Clicking "Give up" in the UI → **the same confirmation dialog** as closing the
    /// window, just different wording, and it **only abandons this round, it doesn't quit
    /// the program** (user, 2026-07-27).
    ///
    /// No report is shown — the UI **never** shows a report (user, 2026-07-27), not even
    /// on completion.
    /// </summary>
    private bool _abandoning;

    private async Task AskAbandonAsync()
    {
        if (_abandoning) return;
        _abandoning = true;
        try
        {
            if (!await Confirm.AskAsync(this, "The task isn't finished. Give up?")) return;
            var session = _session;          // Hold a steady reference in case it gets cleared mid-await
            session?.Abandon();
            EndSession();
        }
        finally { _abandoning = false; }
    }

    // ================================================================
    //  Alarm button interaction
    // ================================================================
    /// <summary>
    /// The alarm is **only ever written to disk at the moment of exit**, and only one
    /// value: the ring time (user, 2026-07-30). The yellow hand's position is a derived
    /// value — its time point mod 12 hours — so whether it changed or fired doesn't need
    /// recording separately. The Shutdown flag isn't handled here — Settings.Load always
    /// resets it to off at startup.
    /// </summary>
    private void SaveAlarmOnExit()
    {
        _settings.AlarmFireAt = _alarm.FireAt;
        _settings.Save();
    }

    /// <summary>Streak count and timestamp of the last scroll. See <see cref="OnAlarmWheel"/>.</summary>
    private DateTime _lastWheelAt = DateTime.MinValue;
    private int _wheelStreak;

    /// <summary>Maximum gap for scroll events to count as one streak. Beyond this it restarts from 1 notch.</summary>
    private const int WheelStreakGapMs = 300;

    /// <summary>
    /// Scroll wheel sets the alarm: scrolling forward (away from you) moves the hand
    /// counter-clockwise, scrolling back moves it clockwise. A slow scroll is 1 minute per
    /// notch; **a continuous fast scroll accelerates**.
    ///
    /// ⚠️ **The acceleration is based on scrolling rhythm, not the magnitude of a single
    /// event** (changed 2026-08-02). It used to read
    /// <c>Math.Abs(e.Delta.Y) / 120</c> — 120 is the unit of **Win32's
    /// `WM_MOUSEWHEEL`**, whereas Avalonia's <c>Delta.Y</c> is **1.0 per notch**. So that
    /// division was always 0.008, got pulled back up to 1 by `Math.Max(1, …)`, and the tier
    /// switch always landed on the first tier: **the acceleration ladder recorded in E3
    /// never actually ran once** — every scroll always moved exactly 1 minute.
    ///
    /// And fast scrolling in Avalonia shows up as **denser events**, not a bigger Delta, so
    /// the correct approach is to count "how many consecutive notches" — a gap longer than
    /// <see cref="WheelStreakGapMs"/> milliseconds breaks the streak and restarts at
    /// 1 minute per notch. This way a slow scroll still fine-tunes minute by minute, while a
    /// fast scroll can jump across hours in one motion.
    /// </summary>
    private void OnAlarmWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;

        var now = DateTime.Now;
        _wheelStreak = (now - _lastWheelAt).TotalMilliseconds <= WheelStreakGapMs
            ? _wheelStreak + 1
            : 1;
        _lastWheelAt = now;

        // Over the course of a streak: 1 -> 3 -> 8 -> 15 -> 30 minutes per notch.
        // A full 12-hour sweep is 720 notches; at 30 minutes per notch that's roughly
        // twenty-odd flicks, while letting go instantly returns to 1 minute per notch so
        // fine adjustment is unaffected.
        var step = _wheelStreak switch
        {
            <= 2 => 1,
            <= 5 => 3,
            <= 10 => 8,
            <= 20 => 15,
            _ => 30,
        };

        // High-precision trackpads may report multiple notches at once; multiply that in too
        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));
        var direction = e.Delta.Y > 0 ? -1 : +1;

        Bump(direction * notches * step * AlarmClock.SlotMinutes);
        _alarmQuietUntil = now.AddSeconds(2);
        e.Handled = true;
    }

    /// <summary>Moves the hand and refreshes the yellow hand plus the tooltip. The tooltip reads <see cref="AlarmClock.FireAt"/> directly — what's shown and what will actually fire are the same value.</summary>
    private void Bump(double minutes)
    {
        _alarm.Bump(minutes, DateTime.Now);
        var dial = F<DialControl>("Dial");
        dial.AlarmMinutes = _alarm.Position;
        if (_alarm.FireAt is { } at)
        {
            ToolTip.SetTip(dial, at.ToString("HH:mm"));
            ToolTip.SetIsOpen(dial, true);
        }
    }

    /// <summary>Gear icon: opens Settings. Two sounds, change one, save one (see <see cref="SettingsWindow"/>).</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await new SettingsWindow(_settings).ShowDialog(this);
            // #2.6: activate the alarm when Execute is turned on
            if (_settings.CommandEnabled)
                _alarm.Activate(DateTime.Now);
            ApplyChrome();  // #5: Force Ticking may have changed, refresh the speaker icon
        }
        catch (Exception ex) { Log.Error("Failed to open the settings window", ex); }
    }
}
