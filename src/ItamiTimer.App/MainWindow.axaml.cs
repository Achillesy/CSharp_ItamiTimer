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
/// 主窗口（DESIGN.md §8 模块 8）。
///
/// **这一层只负责把 <see cref="MinuteCell"/> 列表渲染出来 + 收集用户的提交**，
/// 判定和核算全在 Core，节拍在 <see cref="TaskSession"/>。
///
/// 版面：**「开始」按钮就是那条分割线**——它以上是表盘和骨牌，给眼睛的；它以下是
/// 取值控件和小目标列表，给手的。分割线以下一个提示字都没有（§8.6），出错的原因
/// 只进日志（§8.1a）。
///
/// 可见状态：
/// <code>
/// 空闲                空盘就是下一轮的邀请
/// 进行中              窗口留在原地（用户 2026-07-27 改），色块一分钟长一格
/// 超过 60 秒没动键鼠  响一声，赶在 AW 判 afk 之前叫醒（C3）
/// 专注达成            响一声，进入休息。不给账单——盘面自己会说话（B4）
/// 休息中              蓝色扇形，分针扫出它即结束
/// 休息结束            响一声，回到空盘，停在这里等用户（B2：绝不自动开下一轮）
/// </code>
///
/// ⚠️ 这张表里**没有任何「置顶 / 弹出 / 收进任务栏」**——整套自动置顶 2026-07-28
/// 已废弃（C1：「弹出来」和「绝不抢焦点」这两条约束本身矛盾）。右上角图钉是手动开关，
/// 跟被砍掉的那套不是一回事。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>秒针要亚秒连续重绘（§8.2.6）。**仅窗口可见时跑**——收进任务栏就停，白捡的省电。</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<RadioButton> _goalRadios = [];
    private readonly List<TextBlock> _duringLabels = [];
    private readonly Settings _settings = Settings.Load();

    /// <summary>每个小目标的累计专注时长（§11.2）。启动时读入，每次入账立刻落盘。</summary>
    private readonly During _during = During.Load();

    private TaskSession? _session;

    // 闹钟：模型全在 AlarmClock（可测的纯逻辑），这里只剩滚轮的接线。
    // 左右键点击已取消（2026-07-30，用户：免得影响以后的功能扩展）——只有滚轮。
    private readonly AlarmClock _alarm = new();
    // 调整期间抑制误触发。用「静默截止时刻」而不是布尔量：滚轮是连续离散事件，
    // 布尔 + Task.Delay 复位会互相踩（早来的复位把晚来的调整期掐断）。
    private DateTime _alarmQuietUntil = DateTime.MinValue;

    public MainWindow()
    {
        Log.Info($"Started. Log: {Log.Path_}");

        InitializeComponent();
        ApplyTheme();
        Icon = RingIcon.Make(0, 0);   // 空闲时灰环；任务进行中换成进度色环

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
        F<Button>("MuteBtn").Click += (_, _) => { _settings.TickEnabled = !_settings.TickEnabled; ApplyChrome(); _settings.Save(); };
        F<Button>("PinBtn").Click += (_, _) => { _settings.Pinned = !_settings.Pinned; ApplyChrome(); _settings.Save(); };
        ApplyChrome();

        _frame.Tick += OnFrame;
        _frame.Start();
        Closing += OnClosing;
        Closed += (_, _) => SaveAlarmOnExit();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 时长滑块的量程随构建配置变（DESIGN.md §6.2）。
    ///
    /// **axaml 里写的是正式量程**（10~50 / 步进 5 / 默认 25），Debug 在这里覆盖成
    /// 3~10 / 步进 1。方向是有意的：忘了改也只会把**正式**量程发出去。
    ///
    /// 反过来（axaml 写测试量程、Release 覆盖）已经出过事——2026-07-31 那次改动
    /// 就是无条件写死 3~10，`Value="25"` 还被静默钳到 10，而没有人发现 Release
    /// 也跟着变了。
    ///
    /// Core 不设范围（`TaskRecord` 接受任意时长），而且 `RestMinutes = ⌈f/5⌉` 对
    /// **所有整数**都成立——否则在 Debug 里验证的就不是 Release 的行为，
    /// 测试量程等于白设。
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
        // #5: Force on → 强制滴答，但学习+休息期间静音
        var ticking = _settings.ForceTicking
            ? _session is not { Finished: false }   // 无任务或任务已结束 → 滴答
            : _settings.TickEnabled;
        if (ticking) Tick.Play(sec, _settings.TickVolume);

        // 闹钟检测：到了拨黄针那一刻算死的目标时刻 → 响一次（调整中不触发）。
        // 检查节拍是**每秒一次**（上面那道秒边界的闸门），最多晚 1 秒。
        if (DateTime.Now >= _alarmQuietUntil && _alarm.ShouldFire(DateTime.Now))
        {
            _alarm.MarkFired();   // 一次性——响过就撤，不是每天重复的闹钟。
                                  // 内存里清掉就够了，退出时 SaveAlarmOnExit 自会写成 null。
            if (_settings.CommandEnabled) Command.Execute();
            else Sound.Play(_settings.CommandSound);
        }
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

        // #5: Force on → Ticking 图标隐藏，用户不可手动关
        mute.IsVisible = !_settings.ForceTicking;
        mute.Content = ChromeIcons.Speaker(_settings.TickEnabled);
        mute.Classes.Set("on", _settings.TickEnabled);
        pin.Content = ChromeIcons.Pin(_settings.Pinned);
        pin.Classes.Set("on", _settings.Pinned);

        if (!_settings.TickEnabled) Tick.Stop();
        WindowPin.Set(this, _settings.Pinned);
    }

    private void LoadRules()
    {
        try
        {
            _rules = GroupRules.Load(AppData.RulesPath());

            // 控件**一次性**建好，之后永不重建（§15.2）。rules.json 只在启动时读一次，
            // 小目标列表在一次运行里根本不会变——每次任务结束重建一遍布局既没必要，
            // 又正是那个 bug 的来源。
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
            F<ItemsControl>("Goals").ItemsSource = rows;   // 也只设这一次
            RefreshGoalItems();
            // 恢复上次选中的 goal，找不到就选第一个
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
    /// 刷新累计时长那几个数字。**只改 Text，绝不碰控件树**（§15.2）。
    ///
    /// 原来这里每次都新建一批 `DockPanel` 再把同一批 `RadioButton` / `TextBlock`
    /// 塞进去，然后整份换掉 `ItemsSource`。Avalonia 不允许一个控件有两个视觉父，于是
    /// 抛 `already has a visual parent DockPanel`——而它是在 `EndSession` 里抛的，
    /// 后面的 `RefreshStartButton()` 被整段跳过，**按钮就永远停在 "Give up" 上**。
    ///
    /// 当时试过「加入前先从旧父 Remove」，只对 RadioButton 做了、漏了 TextBlock，
    /// 所以看起来「试过没生效」。但那本来就是在补一个不该存在的洞：
    /// **拿活控件当 `ItemsSource` 就是错的**。列表在一次运行里根本不会变
    /// （`rules.json` 只在启动时读一次），一次建好就完了。
    /// </summary>
    private void RefreshGoalItems()
    {
        for (var i = 0; i < _goalRadios.Count && i < _duringLabels.Count; i++)
        {
            var name = (string)_goalRadios[i].Content!;
            _duringLabels[i].Text = (_during[name] / 3600.0).ToString("F2");
        }
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
        btn.IsEnabled = _rules is not null && Picked() is not null;
        btn.InvalidateVisual();
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

        // Radio 单选，Start 后锁定不可改
        var picked = Picked();
        if (_rules is null || picked is null) return;

        // §14.1（2026-07-27 改）：**截断**到当前这个整分钟，不是进位。
        // 23:13:10 点的开始 → 23:13:00 起算。代价是点击前最多 59 秒也算进来，
        // 换来的是点完立刻开始、不用干等。
        var task = new TaskRecord
        {
            StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
            FocusMinutes = (int)F<Slider>("Minutes").Value,
            Group = picked,
        };

        // 点击 Start 时补充检查今天星期几，更新骨牌数目。
        // 这样跨过午夜点击时，骨牌会反映新的日期；检查密度低，不会太明显。
        F<DominoRow>("Dominoes").Fallen = DominoRow.FallenForToday(DateTime.Now);

        _session = new TaskSession(task, _rules!, _settings.AwBaseUrl);
        _session.Updated += OnSessionUpdated;
        _session.Interrupted += OnInterrupted;
        // 归档 = 一次 ignore（§11.2）：那一小时马上要被移出 buffer，当场入账
        _session.Settled += seconds => Bank(task.Group, seconds);
        foreach (var r in _goalRadios) r.IsEnabled = false;   // Start 后锁定选择

        // 点下按钮的那一刻盘面就要有东西：整段灰弧立刻摆上去，不等第一次 AW 回来
        var dial = F<DialControl>("Dial");
        dial.StartedAt = task.StartedAt;
        dial.Cells = _session.Cells;   // 承诺弧就是 buffer 里那段 Gray，构造时就有了（§4.5）
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
        dial.RestFrom = s.RestFrom;
        dial.RestMinutes = s.Task.RestMinutes;
        dial.InvalidateVisual();

        // §8.3.2：任务栏图标是【聚合投影】——角度 = 完成度，颜色 = 整体纯度。
        var focused = s.FocusedSeconds();
        var progress = Math.Clamp(focused / (s.Task.FocusMinutes * 60.0), 0, 1);
        var elapsed = Math.Max(1, (DateTimeOffset.Now - s.Task.StartedAt).TotalSeconds);
        Icon = RingIcon.Make(progress, Math.Clamp(1 - focused / elapsed, 0, 1));

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

    /// <summary>
    /// 把一段专注时间记到小目标名下（§11.2）。<b>每一秒只入账一次</b>：归档那一小时在
    /// <see cref="TaskSession.Settled"/> 里当场入账，剩下的由
    /// <see cref="TaskSession.TakeUnbankedSeconds"/> 幂等地取走。
    /// </summary>
    private void Bank(string? goal, double seconds)
    {
        if (goal is null || seconds <= 0) return;
        _during.Add(goal, seconds);
    }

    /// <summary>任务终结（完成 / 放弃 / 关窗口）：把还没入账的那部分记上。</summary>
    private void BankRemainder()
    {
        if (_session is { } s) Bank(s.Task.Group, s.TakeUnbankedSeconds());
    }

    /// <summary>任务终结：回到空盘。</summary>
    private void EndSession()
    {
        BankRemainder();

        var old = _session;
        _session = null;
        try { old?.Dispose(); } catch { /* 关不掉就算了，状态已经清空 */ }

        foreach (var r in _goalRadios) r.IsEnabled = true;
        // 恢复之前选中的 radio，确保 Picked() 不为 null
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
        RefreshGoalItems();
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

        // 休息中关窗口不问——专注已经达成了。但**账要记**（§11.2：关掉程序也是一次 ignore）。
        if (_session is { Finished: false, InRest: true }) { BankRemainder(); return; }
        if (_session is not { Finished: false }) return;

        e.Cancel = true;

        if (await Confirm.AskAsync(this, "The task isn't finished. Quit anyway?"))
        {
            _session?.Abandon();
            BankRemainder();     // 放弃了，但花掉的时间是事实
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
    private bool _abandoning;

    private async Task AskAbandonAsync()
    {
        if (_abandoning) return;
        _abandoning = true;
        try
        {
            if (!await Confirm.AskAsync(this, "The task isn't finished. Give up?")) return;
            var session = _session;          // 拿稳引用，防中途被清
            session?.Abandon();
            EndSession();
        }
        finally { _abandoning = false; }
    }

    // ================================================================
    //  闹钟按钮交互
    // ================================================================
    /// <summary>
    /// 闹钟**只在退出这一刻落盘**，而且只落一个值：响铃时间点（用户 2026-07-30）。
    /// 黄针位置是它对 12 小时取余的推导值，变没变、响没响都不用记。
    /// Shutdown 标记这里不管——启动时 Settings.Load 一律复位为关。
    /// </summary>
    private void SaveAlarmOnExit()
    {
        _settings.AlarmFireAt = _alarm.FireAt;
        _settings.Save();
    }

    /// <summary>连续滚动的计数与上一次滚动的时刻。见 <see cref="OnAlarmWheel"/>。</summary>
    private DateTime _lastWheelAt = DateTime.MinValue;
    private int _wheelStreak;

    /// <summary>滚轮连成一串的最大间隔。超过这个数就重新从 1 格算起。</summary>
    private const int WheelStreakGapMs = 300;

    /// <summary>
    /// 滚轮拨针：前滚（远离自己）逆时针，后滚顺时针。慢滚 1 分钟/格，**连续快滚加速**。
    ///
    /// ⚠️ **加速依据是滚动的节奏，不是单次幅度**（2026-08-02 改）。原来写的是
    /// <c>Math.Abs(e.Delta.Y) / 120</c>——120 是 <b>Win32 `WM_MOUSEWHEEL` 的单位</b>，
    /// 而 Avalonia 的 <c>Delta.Y</c> **一格就是 1.0**。于是那个除法恒等于 0.008、
    /// 被 `Math.Max(1, …)` 拉回 1，档位 switch 永远落在第一档：**E3 记的那套加速
    /// 一次都没跑起来过**，每次滚轮恒定走 1 分钟。
    ///
    /// 而且快滚在 Avalonia 里表现为**事件更密**而不是 Delta 更大，所以正确的做法是
    /// 数「连续多少格」——间隔超过 <see cref="WheelStreakGapMs"/> 毫秒就断串、
    /// 重新从 1 分钟/格开始。这样慢滚仍然能一分钟一分钟地微调，快滚能一口气跨小时。
    /// </summary>
    private void OnAlarmWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;

        var now = DateTime.Now;
        _wheelStreak = (now - _lastWheelAt).TotalMilliseconds <= WheelStreakGapMs
            ? _wheelStreak + 1
            : 1;
        _lastWheelAt = now;

        // 一串滚下来：1 → 3 → 8 → 15 → 30 分钟/格。
        // 12 小时一圈 = 720 格，靠 30 分钟/格大约二十几下能跨完，而松手一停就回到
        // 1 分钟/格，微调不受影响。
        var step = _wheelStreak switch
        {
            <= 2 => 1,
            <= 5 => 3,
            <= 10 => 8,
            <= 20 => 15,
            _ => 30,
        };

        // 高精度触控板可能一次报多格，照样乘进去
        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));
        var direction = e.Delta.Y > 0 ? -1 : +1;

        Bump(direction * notches * step * AlarmClock.SlotMinutes);
        _alarmQuietUntil = now.AddSeconds(2);
        e.Handled = true;
    }

    /// <summary>拨针 + 刷新黄针和悬浮提示。提示直接读 <see cref="AlarmClock.FireAt"/>——显示的和会响的是同一个值。</summary>
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

    /// <summary>齿轮：打开设置。两条声音，改一下存一下（见 <see cref="SettingsWindow"/>）。</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await new SettingsWindow(_settings).ShowDialog(this);
            // #2.6：开启 Execute 时激活闹钟
            if (_settings.CommandEnabled)
                _alarm.Activate(DateTime.Now);
            ApplyChrome();  // #5：Force Ticking 可能变了，刷新喇叭图标
        }
        catch (Exception ex) { Log.Error("Failed to open the settings window", ex); }
    }
}
