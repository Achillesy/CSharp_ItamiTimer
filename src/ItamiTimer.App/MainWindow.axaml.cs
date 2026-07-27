using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// 现在接的是 §8.2 的表盘和 §8.4.2a 的滑块；任务循环（§8.3.5）还没接上。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>秒针要亚秒连续重绘（§8.2.6）。仅窗口可见时跑——不可见就停，这是收进任务栏白捡的好处。</summary>
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private GroupRules? _rules;
    private readonly List<CheckBox> _goalBoxes = [];

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme();
        LoadRules();
        this.FindControl<Slider>("Minutes")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) UpdateMinutesText();
        };
        UpdateMinutesText();

        _frame.Tick += (_, _) => this.FindControl<DialControl>("Dial")!.InvalidateVisual();
        _frame.Start();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>§8.2.7：盘面跟随主题——白天素白、夜里深灰，是同一套东西的日面与夜面。</summary>
    private void ApplyTheme()
        => this.FindControl<DialControl>("Dial")!.Palette =
            ActualThemeVariant == ThemeVariant.Dark ? DialPalette.Dark : DialPalette.Light;

    private void LoadRules()
    {
        var hint = this.FindControl<TextBlock>("Hint")!;
        try
        {
            _rules = GroupRules.Load("rules.json");
            var items = this.FindControl<ItemsControl>("Goals")!;
            foreach (var name in _rules.SelectableGroups)
            {
                var box = new CheckBox { Content = name };
                _goalBoxes.Add(box);
            }
            items.ItemsSource = _goalBoxes;
            if (_goalBoxes.Count == 1) _goalBoxes[0].IsChecked = true;
            hint.Text = "小目标来自 rules.json。要改匹配规则（app / 标题关键词）请直接编辑那个文件，改动对整段历史生效。";
        }
        catch (Exception e)
        {
            // fail-closed（§5.2）：规则读不了就不让开始，不要静默放行
            hint.Text = $"读不了 rules.json：{e.Message}";
            this.FindControl<Button>("StartBtn")!.IsEnabled = false;
        }
    }

    private void UpdateMinutesText()
    {
        var m = (int)this.FindControl<Slider>("Minutes")!.Value;
        // §8.4.2a：步进 5 保证休息恒为整数分钟，不需要取整规则
        this.FindControl<TextBlock>("MinutesText")!.Text = $"{m} 分 / 休 {m / 5}";
    }

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        var picked = _goalBoxes.Where(b => b.IsChecked == true)
                               .Select(b => (string)b.Content!).ToList();
        var status = this.FindControl<TextBlock>("Status")!;
        if (picked.Count == 0) { status.Text = "先勾一个小目标。"; return; }

        // §14.1：进位到下一个整分钟。绝不向后取整——那会把点「开始」之前的时间也算进来。
        var startedAt = TimeGrid.CeilToMinute(DateTimeOffset.Now);
        status.Text = $"{string.Join("、", picked)} · {(int)this.FindControl<Slider>("Minutes")!.Value} 分钟" +
                      $" · {startedAt:HH:mm:ss} 起算\n（任务循环还没接上）";
    }
}
