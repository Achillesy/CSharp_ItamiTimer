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
/// 专注达成            弹出【不置顶】，给账单，进入休息
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
    private bool _awReady;

    private TaskSession? _session;
    private bool _popped;          // 当前是不是因为提醒而弹出来的

    public MainWindow()
    {
        Log.Info($"启动。日志：{Log.Path_}");

        InitializeComponent();
        ApplyTheme();
        Icon = TomatoIcon.Make();   // 空闲时是番茄；任务进行中换成进度色环（§8.3.2）

        LoadRules();
        RefreshStartButton();

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

    /// <summary>秒针只在窗口真的看得见时才重绘（§8.2.6）。</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized && IsVisible)
            F<DialControl>("Dial").InvalidateVisual();
    }

    /// <summary>
    /// §6.2：AW 访问不了就直接说无法工作。这里的"说"不是弹一句话，而是**把分割线
    /// 以下整块变灰**——用户一眼看出这个程序此刻只能当钟用，不会以为它在计时。
    /// </summary>
    private async Task CheckAwAsync()
    {
        try
        {
            using var aw = new AwClient();
            await aw.ProbeAsync();
            await aw.FindBucketIdAsync(AwClient.WindowBucketType);
            await aw.FindBucketIdAsync(AwClient.AfkBucketType);   // 缺 afk 同样不算就绪（§6.1.1）
            _awReady = true;
            Log.Info("AW 就绪，两个 bucket 都在。");
        }
        catch (Exception e)
        {
            _awReady = false;
            Log.Error("连不上 ActivityWatch，分割线以下已置灰", e);
        }
        F<StackPanel>("Controls").IsEnabled = _awReady;
        RefreshStartButton();
    }

    private void LoadRules()
    {
        try
        {
            _rules = GroupRules.Load("rules.json");
            foreach (var name in _rules.SelectableGroups)
            {
                var box = new CheckBox { Content = name };
                box.IsCheckedChanged += OnGoalToggled;
                _goalBoxes.Add(box);
            }
            F<ItemsControl>("Goals").ItemsSource = _goalBoxes;
            if (_goalBoxes.Count == 1) _goalBoxes[0].IsChecked = true;
            Log.Info($"rules.json 已加载，小目标：{string.Join("、", _rules.SelectableGroups)}");
        }
        catch (Exception e)
        {
            // fail-closed（§5.2）：规则读不了就不让开始。界面不解释，原因进日志。
            _rules = null;
            Log.Error("rules.json 读不了，开始按钮已置灰", e);
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

    private void RefreshStartButton()
    {
        var btn = F<Button>("StartBtn");
        if (_session is { Finished: false })
        {
            btn.Content = _session.InRest ? "开始新一轮" : "放弃";
            btn.IsEnabled = true;
            return;
        }
        btn.Content = "开始";
        btn.IsEnabled = _awReady && _rules is not null && Picked().Count > 0;
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

        var picked = Picked();
        if (_rules is null || picked.Count == 0) return;

        // §14.1（2026-07-27 改）：**截断**到当前这个整分钟，不是进位。
        // 23:13:10 点的开始 → 23:13:00 起算。代价是点击前最多 59 秒也算进来，
        // 换来的是点完立刻开始、不用干等。
        var task = new TaskRecord
        {
            StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
            FocusMinutes = (int)F<Slider>("Minutes").Value,
            Groups = picked,
        };

        _session = new TaskSession(task, _rules);
        _session.Updated += OnSessionUpdated;
        _session.Interrupted += OnInterrupted;
        _session.Retract += OnRetract;

        // 点下按钮的那一刻盘面就要有东西：整段灰弧立刻摆上去，不等第一次 AW 回来
        var dial = F<DialControl>("Dial");
        dial.StartedAt = task.StartedAt;
        dial.Cells = [];
        dial.RemainingMinutes = task.FocusMinutes;
        dial.RingOpacity = 1;
        dial.InvalidateVisual();

        F<TextBlock>("BillText").IsVisible = false;
        RefreshStartButton();
        // §8.3 原本要求"任务一开始就收进任务栏"，用户 2026-07-27 改成**留在原地**。
        // 连带：回到正轨时也只撤销置顶、不再缩起来（见 OnRetract）。
    }

    private void OnSessionUpdated()
    {
        if (_session is not { } s) return;

        var dial = F<DialControl>("Dial");
        dial.StartedAt = s.Task.StartedAt;
        dial.Cells = s.Cells;
        dial.RemainingMinutes = s.RemainingMinutes;
        dial.RingOpacity = s.RingOpacity;
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
    /// 回到正轨就**撤销置顶**（§0.5 问题 3）：用户已经用行动回应了提醒，继续压在
    /// 最上面是在惩罚正确行为。
    ///
    /// 只撤销置顶、**不最小化** —— 用户 2026-07-27 定的"开始之后窗口就放那儿"。
    /// 从来没缩起来过，回落时自然也不该缩。
    ///
    /// **用户正在看这个窗口时不动它**：窗口是用 SW_SHOWNOACTIVATE 弹的、本来拿不到
    /// 焦点，所以它一旦成了前台窗口，就说明是用户自己点进来的。
    /// </summary>
    private void OnRetract()
    {
        if (!_popped) return;
        if (Win32Topmost.IsForeground(this)) return;
        _popped = false;
        Win32Topmost.ClearTopmost(this);
    }

    private void OnInterrupted(TaskSession.Interrupt why)
    {
        if (_session is not { } s) return;

        switch (why)
        {
            case TaskSession.Interrupt.Deviated:
            case TaskSession.Interrupt.Idle:
                // 置顶但**绝不抢焦点**：用户在切走的那个应用里继续打字，字要落在那边（§13 第 6 条）
                _popped = true;
                Pop();
                break;

            case TaskSession.Interrupt.FocusDone:
                // 账单在【达成】这一刻给，不在休息结束时给（§8.4.3）
                _popped = false;
                Win32Topmost.ClearTopmost(this);
                ShowBill(Bill.Render(s.Task, s.State!));
                Pop();
                RefreshStartButton();
                break;

            case TaskSession.Interrupt.RestDone:
                Win32Topmost.ClearTopmost(this);
                Pop();
                EndSession();
                break;
        }
    }

    private void Pop()
    {
        WindowState = WindowState.Normal;
        CenterOnPrimary();
        Win32Topmost.ShowNoActivate(this);
    }

    /// <summary>任务终结：回到空盘。**色环 = 当前任务的投影，没有任务就没有色环**（§8.4.5a）。</summary>
    private void EndSession()
    {
        _session?.Dispose();
        _session = null;
        _popped = false;

        var dial = F<DialControl>("Dial");
        dial.Cells = [];
        dial.StartedAt = null;
        dial.RemainingMinutes = 0;
        dial.RingOpacity = 1;
        dial.InvalidateVisual();

        Icon = TomatoIcon.Make();
        RefreshStartButton();
    }

    private void ShowBill(string text)
    {
        var b = F<TextBlock>("BillText");
        b.Text = text;
        b.IsVisible = true;
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

        // **先撤销置顶再问**。偏离提醒会把主窗口设成 HWND_TOPMOST，而确认框是普通窗口
        // —— 不撤销的话它会被主窗口整个盖住，用户看到的就是"点了 × 什么都没发生"。
        // （测试时踩到过：对话框确实创建了、也是前台窗口，但屏幕上看不见。）
        _popped = false;
        Win32Topmost.ClearTopmost(this);

        if (await Confirm.AskAsync(this, "任务尚未完成，你确定退出？"))
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
    /// 不再把账单摆出来 —— 用户明确说这里不需要那些总结。账单只在专注达成那一刻给（§8.4.3）。
    /// </summary>
    private async Task AskAbandonAsync()
    {
        // 先撤销置顶：偏离提醒会把主窗口设成 HWND_TOPMOST，普通的确认框会被它整个盖住
        _popped = false;
        Win32Topmost.ClearTopmost(this);

        if (!await Confirm.AskAsync(this, "任务尚未完成，你确定放弃？")) return;
        _session?.Abandon();
        EndSession();
    }

    /// <summary>
    /// 每次弹出前摆回**主屏正中**。双屏实测：用户把窗口拖到副屏之后，后续每次弹出都
    /// 出现在副屏——提醒弹到你没在看的那块屏上，等于没提醒（§8.5）。
    /// Position 是物理像素而 Width/Height 是 DIP，高 DPI 下必须乘 Scaling。
    /// </summary>
    private void CenterOnPrimary()
    {
        var s = Screens.Primary ?? (Screens.ScreenCount > 0 ? Screens.All[0] : null);
        if (s is null) return;
        var w = (int)(Bounds.Width * s.Scaling);
        var h = (int)(Bounds.Height * s.Scaling);
        Position = new PixelPoint(
            s.WorkingArea.X + (s.WorkingArea.Width - w) / 2,
            s.WorkingArea.Y + (s.WorkingArea.Height - h) / 2);
    }
}
