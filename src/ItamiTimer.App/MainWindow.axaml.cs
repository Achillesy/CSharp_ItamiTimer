using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ItamiTimer.Core;
using ItamiTimer;

namespace ItamiTimer.App;

/// <summary>
/// 主窗口（DESIGN.md §8 模块 8）。
///
/// **这一层只负责把 <see cref="TaskState"/> 渲染出来 + 收集用户的提交**，判定和核算
/// 全在 Core，节拍在 <see cref="TaskSession"/>。
///
/// 版面：**「开始」按钮就是那条分割线**——它以上是表盘和骨牌，给眼睛的；它以下是
/// 取值控件和小目标列表，给手的。分割线以下一个提示字都没有（§8.6），出错的原因
/// 只进日志（§8.1a）。
///
/// 可见状态（§8.3.1）：
/// <code>
/// 空闲                正常显示，空盘就是下一轮的邀请
/// 进行中·守规矩       收进任务栏，只剩色环图标
/// 进行中·偏离         置顶弹出，不抢焦点；回到正轨后自动缩回
/// 超过 60 秒没动键鼠  同上，赶在 AW 判 afk 之前叫醒
/// 专注达成            弹出【不置顶】，进入休息。不给账单——盘面自己会说话
/// 休息中              色环按分钟淡出，纯本地计时
/// 休息结束            弹出【不置顶】，纯提示，停在这里等用户
/// </code>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>秒针要亚秒连续重绘（§8.2.6）。**仅窗口可见时跑**——收进任务栏就停，白捡的省电。</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<CheckBox> _goalBoxes = [];
    /// <summary>启动时定一次，整个会话不再变（§11.1 第 2 条，见 <see cref="AppMode"/>）。</summary>
    private AppMode _mode = AppMode.Constrained;
    private readonly Settings _settings = Settings.Load();

    private TaskSession? _session;

    public MainWindow()
    {
        Log.Info($"Started. Log: {Log.Path_}");

        InitializeComponent();
        ApplyTheme();
        Icon = TomatoIcon.Make();   // 空闲时是番茄；任务进行中换成进度色环（§8.3.2）

        LoadRules();
        RefreshStartButton();
        var gear = F<Button>("SettingsBtn");
        gear.Content = ChromeIcons.Gear();     // 矢量，不是字形（macOS 上没有那个字体）
        gear.Click += OnSettings;
        F<Button>("MuteBtn").Click += (_, _) => { _settings.TickEnabled = !_settings.TickEnabled; ApplyChrome(); _settings.Save(); };
        F<Button>("PinBtn").Click += (_, _) => { _settings.Pinned = !_settings.Pinned; ApplyChrome(); _settings.Save(); };
        ApplyChrome();

        _frame.Tick += OnFrame;
        _frame.Start();
        Closing += OnClosing;

        _ = CheckAwAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private T F<T>(string name) where T : Control => this.FindControl<T>(name)!;

    /// <summary>§8.2.7：盘面跟随主题——白天素白、夜里深灰，是同一套东西的日面与夜面。</summary>
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
    /// 33ms 一帧。两件事：让秒针的跳变及时（延迟 ≤33ms），以及**在秒边界上放一声滴答**。
    ///
    /// **窗口收起来照样响**（用户 2026-07-28）：滴答是钟本身在走，跟你有没有在看它
    /// 没关系。只有重绘会因为看不见而跳过 —— 那纯粹是省电，不影响声音。
    ///
    /// 滴答挂在这个已有的定时器上，不另起一个：它天然对齐墙钟、不会漂，而 33ms 的
    /// 抖动对一声 35ms 的"咔"完全听不出来。另一条路是做个整 1 秒的缓冲交给
    /// `SND_LOOP` 循环 —— 零 CPU，但音频时钟会跟系统时钟慢慢漂开，一小时后
    /// 秒针和声音就对不上了。
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var visible = WindowState != WindowState.Minimized && IsVisible;
        if (visible) F<DialControl>("Dial").InvalidateVisual();

        var sec = DateTime.Now.Second;
        if (sec == _tickedSecond) return;
        _tickedSecond = sec;
        if (_settings.TickEnabled) Tick.Play(sec, _settings.TickVolume);
    }

    /// <summary>
    /// 把右上角两个开关的图标和窗口的置顶状态刷成设置里的样子。
    ///
    /// **喇叭只管滴答声**（用户 2026-07-28）：滴答是钟本身的功能，跟督促学习那三声
    /// 通知无关，所以它不是"总静音"。那三声各有各的开关，在设置窗口里。
    ///
    /// 图标是**矢量画的**（<see cref="ChromeIcons"/>），不是字体字形。原来这两个用的是
    /// Segoe Fluent Icons 的 `E767` / `E74F` / `E840` / `E718`，而那个字体只有 Windows
    /// 装机自带 —— macOS 上是两个豆腐块。状态仍然靠图形本身和明度双重表达，
    /// 不用文字（分割线以上一个字都没有）：
    ///
    /// | | 关 | 开 |
    /// |---|---|---|
    /// | 喇叭 | 划一道斜杠 | 带两道声波 |
    /// | 图钉 | 空心（只描边） | 实心 |
    ///
    /// 图钉那一格**刻意不用"打叉"**：打叉在满不透明度下会读成"置顶被禁用"，
    /// 跟"已置顶"正好相反。喇叭划斜杠没有这个歧义 —— 划掉的喇叭全世界都认得
    /// 是静音。
    /// </summary>
    private void ApplyChrome()
    {
        var mute = F<Button>("MuteBtn");
        var pin = F<Button>("PinBtn");

        mute.Content = ChromeIcons.Speaker(_settings.TickEnabled);
        mute.Classes.Set("on", _settings.TickEnabled);
        pin.Content = ChromeIcons.Pin(_settings.Pinned);
        pin.Classes.Set("on", _settings.Pinned);

        if (!_settings.TickEnabled) Tick.Stop();   // 掐断正在响的那一声，别等它自己完
        WindowPin.Set(this, _settings.Pinned);
    }

    /// <summary>
    /// §6.2：AW 访问不了就直接说无法工作。这里的"说"不是弹一句话，而是**把分割线
    /// 以下整块变灰**——用户一眼看出这个程序此刻只能当钟用，不会以为它在计时。
    /// </summary>
    private async Task CheckAwAsync()
    {
        try
        {
            using var aw = new AwClient(_settings.AwBaseUrl);
            await aw.ProbeAsync();
            await aw.FindBucketIdAsync(AwClient.WindowBucketType);
            await aw.FindBucketIdAsync(AwClient.AfkBucketType);   // 缺 afk 同样不算就绪（§6.1.1）
            Log.Info("ActivityWatch ready; both buckets present.");
        }
        catch (Exception e)
        {
            // §11.1：连不上不是"停摆"，是**退化成纯番茄钟**。界面不解释，原因进日志。
            _mode = AppMode.Pomodoro;
            Log.Error("Cannot reach ActivityWatch; falling back to plain pomodoro mode", e);
        }

        // rules.json 读不了同样退化（§11.1 第 4 条）。原来的行为是把开始按钮永久
        // 灰掉、程序基本不可用 —— 那比退化成番茄钟糟得多。
        if (_rules is null)
        {
            _mode = AppMode.Pomodoro;
            Log.Warn("rules.json unavailable; falling back to plain pomodoro mode");
        }

        ApplyMode();
    }

    private void LoadRules()
    {
        try
        {
            _rules = GroupRules.Load(AppData.RulesPath());
            foreach (var name in _rules.SelectableGroups)
            {
                var box = new CheckBox { Content = name };
                box.IsCheckedChanged += OnGoalToggled;
                _goalBoxes.Add(box);
            }
            F<ItemsControl>("Goals").ItemsSource = _goalBoxes;
            if (_goalBoxes.Count == 1) _goalBoxes[0].IsChecked = true;
            Log.Info($"rules.json loaded. Goals: {string.Join(", ", _rules.SelectableGroups)}");
        }
        catch (Exception e)
        {
            // 读不了不再是"不让开始"，而是退化成纯番茄钟（§11.1 第 4 条）——
            // 由 CheckAwAsync 统一裁决，这里只负责把 _rules 置空并记账。
            _rules = null;
            Log.Error("Cannot read rules.json", e);
        }
    }

    private void OnGoalToggled(object? sender, RoutedEventArgs e)
    {
        // 任务进行中补勾一个小目标 → 追溯整段历史生效（§5.4）
        if (_session is { Finished: false }) _session.SetGroups(Picked());
        RefreshStartButton();
    }

    private List<string> Picked()
        => _goalBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content!).ToList();

    /// <summary>
    /// 把界面调成本次会话的模式（§11.1 第 3 条）。启动时调用一次，此后不再变。
    ///
    /// 番茄钟模式下**整个隐藏**小目标列表，不是变灰 —— 它此时没有意义，而且
    /// "列表整个消失"正是让用户看出自己没在被监管的那个信号（§11.1 的判据一节：
    /// 点击「开始」才表示他希望被监管）。
    ///
    /// 也**不执行** §6.2 那套"分割线以下整块变灰" —— 那是给"AW 本该在但连不上"
    /// 用的，而这里根本没打算用 AW。
    /// </summary>
    private void ApplyMode()
    {
        F<ItemsControl>("Goals").IsVisible = _mode == AppMode.Constrained;
        F<StackPanel>("Controls").IsEnabled = true;
        Log.Info($"Mode: {_mode}");
        RefreshStartButton();
    }

    private void RefreshStartButton()
    {
        var btn = F<Button>("StartBtn");
        if (_session is { Finished: false })
        {
            btn.Content = _session.InRest ? "New round" : "Give up";
            // 只有「放弃」染红：它作废整轮。「开始新一轮」是专注已达成之后开下一轮，
            // 不危险，保持绿色（样式见 MainWindow.axaml 的 Button.start.danger）。
            btn.Classes.Set("danger", !_session.InRest);
            btn.IsEnabled = true;
            return;
        }
        btn.Content = "Start";
        btn.Classes.Set("danger", false);
        // 番茄钟模式下没有小目标可勾，自然也不能拿"勾了没有"当启用条件（§11.1 第 3 条）。
        btn.IsEnabled = _mode == AppMode.Pomodoro || (_rules is not null && Picked().Count > 0);
    }

    // ---------------------------------------------------------------- 任务

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        // 进行中点它 = 放弃；休息中点它 = 开新一轮
        // （§8.4.6：不需要单独的「跳过休息」按钮，新建任务本身就是跳过休息）
        if (_session is { Finished: false })
        {
            if (_session.InRest) EndSession();
            else { _ = AskAbandonAsync(); return; }
        }

        // 番茄钟模式：没有小目标可勾，Groups 就是空的；约束模式仍然要求至少勾一个。
        var pomodoro = _mode == AppMode.Pomodoro;
        var picked = pomodoro ? [] : Picked();
        if (!pomodoro && (_rules is null || picked.Count == 0)) return;

        // §14.1（2026-07-27 改）：**截断**到当前这个整分钟，不是进位。
        // 23:13:10 点的开始 → 23:13:00 起算。代价是点击前最多 59 秒也算进来，
        // 换来的是点完立刻开始、不用干等。
        var task = new TaskRecord
        {
            StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
            FocusMinutes = (int)F<Slider>("Minutes").Value,
            Groups = picked,
        };

        // 点击 Start 时补充检查今天星期几，更新骨牌数目。
        // 这样跨过午夜点击时，骨牌会反映新的日期；检查密度低，不会太明显。
        F<DominoRow>("Dominoes").Fallen = DominoRow.FallenForToday(DateTime.Now);

        // 番茄钟模式下 rules 可能压根没读出来（§11.1 第 4 条），给一份空的即可 ——
        // 合成事件靠自身豁免命中 Neutral，不经过任何用户规则。
        _session = new TaskSession(task, _rules ?? GroupRules.Empty, _mode, _settings.AwBaseUrl);
        _session.Updated += OnSessionUpdated;
        _session.Interrupted += OnInterrupted;

        // 点下按钮的那一刻盘面就要有东西：整段灰弧立刻摆上去，不等第一次 AW 回来
        var dial = F<DialControl>("Dial");
        dial.StartedAt = task.StartedAt;
        dial.Cells = [];
        dial.RemainingMinutes = task.FocusMinutes;
        dial.RestFrom = null;
        dial.InvalidateVisual();

        RefreshStartButton();
        // §8.3 原本要求"任务一开始就收进任务栏"，用户 2026-07-27 改成**留在原地**。
        // 连带：回到正轨时也只撤销置顶、不再缩起来（见 OnFrame 里的看门狗）。
    }

    private void OnSessionUpdated()
    {
        if (_session is not { } s) return;

        var dial = F<DialControl>("Dial");
        dial.StartedAt = s.Task.StartedAt;
        dial.Cells = s.Cells;
        dial.RemainingMinutes = s.RemainingMinutes;
        dial.RestFrom = s.RestFrom;
        dial.RestMinutes = s.Task.RestMinutes;
        dial.InvalidateVisual();

        // §8.3.2：任务栏图标是【聚合投影】——角度 = 完成度，颜色 = 整体纯度。
        // 16px 上一圈只有约 41px 弧长，逐分钟色块物理上画不出来。
        if (s.State is { } st)
        {
            var progress = Math.Clamp(st.FocusedSeconds / (s.Task.FocusMinutes * 60.0), 0, 1);
            var elapsed = Math.Max(1, (st.Now - s.Task.StartedAt).TotalSeconds);
            Icon = RingIcon.Make(progress, Math.Clamp(1 - st.FocusedSeconds / elapsed, 0, 1));
        }

    }

    /// <summary>
    /// 三件事会响一声系统音，每件都能在设置里单独关（用户 2026-07-28）。
    ///
    /// 整套"置顶但不抢焦点"已经删干净了 —— 用户原话：「不要再纠结窗口置顶这种事情了。
    /// 逻辑混乱，又容易出错。」它确实一直在出错：先是达成那次根本没置顶（不抢焦点
    /// 又不置顶等于没弹），修好之后又变成永远撤不掉。声音没有这些状态。
    ///
    /// 跑偏不再有任何提醒，**只写日志**。表盘上那格是红的、灰弧往前滑了一截，
    /// 自己看，自己猜 —— 跟不给账单是同一条思路。键鼠空闲则不同：它提醒的是
    /// 一段**即将被 AW 判成 afk 而作废**的时间，晚了就救不回来（§8.3.5）。
    /// </summary>
    private void OnInterrupted(TaskSession.Interrupt why)
    {
        if (_session is null) return;

        switch (why)
        {
            case TaskSession.Interrupt.FocusDone:
                if (_settings.FocusDoneEnabled) Sound.Play(_settings.FocusDoneSound);
                RefreshStartButton();
                break;

            case TaskSession.Interrupt.RestDone:
                if (_settings.RestDoneEnabled) Sound.Play(_settings.RestDoneSound);
                EndSession();
                break;

            case TaskSession.Interrupt.Idle:
                if (_settings.IdleEnabled) Sound.Play(_settings.IdleSound);
                break;
        }
    }

    /// <summary>任务终结：回到空盘。**色环 = 当前任务的投影，没有任务就没有色环**（§8.4.5a）。</summary>
    private void EndSession()
    {
        _session?.Dispose();
        _session = null;

        var dial = F<DialControl>("Dial");
        dial.Cells = [];
        dial.StartedAt = null;
        dial.RemainingMinutes = 0;
        dial.RestFrom = null;
        dial.InvalidateVisual();

        Icon = TomatoIcon.Make();
        RefreshStartButton();
    }

    // ---------------------------------------------------------------- 退出 = 放弃（§9）

    private bool _closeApproved;

    /// <summary>
    /// §9：**专注中关窗口 = 放弃任务**，所以要问一次。标题栏的 ×、任务栏右键「关闭
    /// 窗口」、Alt+F4 都会走到这里。
    ///
    /// 没有任务、或者已经在休息（专注已达成、账也给过了）→ **直接退出，不问**。
    ///
    /// Closing 是同步事件，没法在里面 await 对话框，所以先取消关闭、异步问完再关。
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;
        if (_session is not { Finished: false, InRest: false }) return;

        e.Cancel = true;

        if (await Confirm.AskAsync(this, "The task isn't finished. Quit anyway?"))
        {
            _session?.Abandon();
            _closeApproved = true;
            Close();
        }
    }

    /// <summary>
    /// 界面里点「放弃」→ 跟关窗口用**同一个确认框**，只是措辞不同，而且**只放弃这一轮、
    /// 不退出程序**（用户 2026-07-27）。
    ///
    /// 不摆账单 —— 界面**任何时候**都不给账单（用户 2026-07-27），达成时也不给。
    /// </summary>
    private async Task AskAbandonAsync()
    {
        if (!await Confirm.AskAsync(this, "The task isn't finished. Give up?")) return;
        _session?.Abandon();
        EndSession();
    }

    /// <summary>齿轮：打开设置。两条声音，改一下存一下（见 <see cref="SettingsWindow"/>）。</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await new SettingsWindow(_settings).ShowDialog(this); }
        catch (Exception ex) { Log.Error("Failed to open the settings window", ex); }
    }
}
