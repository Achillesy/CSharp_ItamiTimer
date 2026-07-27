using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// 主窗口（DESIGN.md §8 模块 8）。
///
/// **这一层只负责把 TaskState 渲染出来 + 收集用户的提交**，判定和核算全在 Core。
///
/// 版面（2026-07-27 用户定）：**「开始」按钮就是那条分割线**——它以上是表盘和骨牌，
/// 给眼睛的；它以下是取值控件和小目标列表，给手的。分割线以下**一个提示字都没有**，
/// 让用户自己猜；窗口高度随 rules.json 里的小目标个数变。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>秒针要亚秒连续重绘（§8.2.6）。仅窗口可见时跑——不可见就停，这是收进任务栏白捡的好处。</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<CheckBox> _goalBoxes = [];
    private bool _awReady;

    public MainWindow()
    {
        Log.Info($"启动。日志：{Log.Path_}");

        InitializeComponent();
        ApplyTheme();

        // 空闲时的图标就是那颗番茄；任务进行中会换成 RingIcon 的进度色环（§8.3.2）
        Icon = TomatoIcon.Make();

        LoadRules();
        RefreshStartButton();

        _frame.Tick += (_, _) => this.FindControl<DialControl>("Dial")!.InvalidateVisual();
        _frame.Start();

        _ = CheckAwAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>§8.2.7：盘面跟随主题——白天素白、夜里深灰，是同一套东西的日面与夜面。</summary>
    private void ApplyTheme()
    {
        var palette = ActualThemeVariant == ThemeVariant.Dark ? DialPalette.Dark : DialPalette.Light;
        this.FindControl<DialControl>("Dial")!.Palette = palette;
        var row = this.FindControl<DominoRow>("Dominoes")!;
        row.Palette = palette;
        row.Fallen = DominoRow.FallenForToday(DateTime.Now);
    }

    /// <summary>
    /// §6.2：AW 访问不了就直接说无法工作。这里的"说"不是弹一句话，而是**把分割线
    /// 以下整块变灰**——用户一眼就看出这个程序此刻只能当钟用，不会以为它在计时。
    /// </summary>
    private async Task CheckAwAsync()
    {
        try
        {
            using var aw = new AwClient();
            await aw.ProbeAsync();
            // 两个 bucket 都必需，缺 afk 同样不算就绪（§6.1.1）
            await aw.FindBucketIdAsync(AwClient.WindowBucketType);
            await aw.FindBucketIdAsync(AwClient.AfkBucketType);
            _awReady = true;
            Log.Info("AW 就绪，两个 bucket 都在。");
        }
        catch (Exception e)
        {
            _awReady = false;
            // 界面上只是灰掉，一个字都不说；**原因写进日志**，否则就成了黑箱
            Log.Error("连不上 ActivityWatch，分割线以下已置灰", e);
        }

        this.FindControl<StackPanel>("Controls")!.IsEnabled = _awReady;
        RefreshStartButton();
    }

    private void LoadRules()
    {
        var items = this.FindControl<ItemsControl>("Goals")!;
        try
        {
            _rules = GroupRules.Load("rules.json");
            foreach (var name in _rules.SelectableGroups)
            {
                var box = new CheckBox { Content = name };
                box.IsCheckedChanged += (_, _) => RefreshStartButton();
                _goalBoxes.Add(box);
            }
            items.ItemsSource = _goalBoxes;
            if (_goalBoxes.Count == 1) _goalBoxes[0].IsChecked = true;
            Log.Info($"rules.json 已加载，小目标：{string.Join("、", _rules.SelectableGroups)}");
        }
        catch (Exception e)
        {
            // fail-closed（§5.2）：规则读不了就不让开始，不静默放行。
            // 界面上不解释（按钮灰着，用户自己去看），**但日志必须写清是哪一条坏了** ——
            // GroupRules 抛的异常里带着组名和那条正则。
            _rules = null;
            Log.Error("rules.json 读不了，开始按钮已置灰", e);
        }
    }

    /// <summary>没勾选任何小目标（或 AW 没就绪、规则没读到）就不让开始。</summary>
    private void RefreshStartButton()
        => this.FindControl<Button>("StartBtn")!.IsEnabled =
            _awReady && _rules is not null && _goalBoxes.Any(b => b.IsChecked == true);

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        var picked = _goalBoxes.Where(b => b.IsChecked == true)
                               .Select(b => (string)b.Content!).ToList();
        if (picked.Count == 0) return;

        // §14.1：进位到下一个整分钟。绝不向后取整——那会把点「开始」之前的时间也算进来。
        var startedAt = TimeGrid.CeilToMinute(DateTimeOffset.Now);
        var minutes = (int)this.FindControl<Slider>("Minutes")!.Value;
        Log.Info($"提交任务：{string.Join("、", picked)}  专注 {minutes} 分钟  起算 {startedAt:HH:mm:ss}");
        // TODO 任务循环（§8.3.5）还没接上
    }
}
