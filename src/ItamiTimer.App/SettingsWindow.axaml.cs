using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

namespace ItamiTimer.App;

/// <summary>
/// 设置窗口。三条声音：任务结束、休息结束、键鼠空闲（照 Windows 时钟应用的
/// 「专注时段」设置页排版）。
///
/// 没有「确定 / 取消」：改一下存一下，跟系统设置一个路数。选中音色立刻试听一次 ——
/// 从一串文件名里盲选是选不出来的。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private bool _loading = true;

    public SettingsWindow() : this(new Settings()) { }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        AvaloniaXamlLoader.Load(this);

        var names = Sound.Available();

        var focusOn = this.FindControl<ToggleSwitch>("FocusOn")!;
        var restOn = this.FindControl<ToggleSwitch>("RestOn")!;
        var idleOn = this.FindControl<ToggleSwitch>("IdleOn")!;
        var focusSound = this.FindControl<ComboBox>("FocusSound")!;
        var restSound = this.FindControl<ComboBox>("RestSound")!;
        var idleSound = this.FindControl<ComboBox>("IdleSound")!;
        var tickOn = this.FindControl<ToggleSwitch>("TickOn")!;
        var tickVol = this.FindControl<Slider>("TickVol")!;

        focusSound.ItemsSource = names;
        restSound.ItemsSource = names;
        idleSound.ItemsSource = names;
        focusOn.IsChecked = settings.FocusDoneEnabled;
        restOn.IsChecked = settings.RestDoneEnabled;
        idleOn.IsChecked = settings.IdleEnabled;
        focusSound.SelectedItem = settings.FocusDoneSound;
        restSound.SelectedItem = settings.RestDoneSound;
        idleSound.SelectedItem = settings.IdleSound;
        focusSound.IsEnabled = settings.FocusDoneEnabled;
        restSound.IsEnabled = settings.RestDoneEnabled;
        idleSound.IsEnabled = settings.IdleEnabled;
        tickOn.IsChecked = settings.TickEnabled;
        tickVol.Value = settings.TickVolume;
        tickVol.IsEnabled = settings.TickEnabled;

        focusOn.IsCheckedChanged += (_, _) =>
        {
            settings.FocusDoneEnabled = focusOn.IsChecked == true;
            focusSound.IsEnabled = settings.FocusDoneEnabled;
            Persist();
        };
        restOn.IsCheckedChanged += (_, _) =>
        {
            settings.RestDoneEnabled = restOn.IsChecked == true;
            restSound.IsEnabled = settings.RestDoneEnabled;
            Persist();
        };
        tickOn.IsCheckedChanged += (_, _) =>
        {
            settings.TickEnabled = tickOn.IsChecked == true;
            tickVol.IsEnabled = settings.TickEnabled;
            if (!_loading && settings.TickEnabled) Tick.Play(0, settings.TickVolume);
            Persist();
        };
        tickVol.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            settings.TickVolume = (int)Math.Round(tickVol.Value);
            // 拖动时实时试听：音量是唯一一个"不听见就调不准"的设置
            if (!_loading && settings.TickEnabled) Tick.Play(0, settings.TickVolume);
            Persist();
        };
        idleOn.IsCheckedChanged += (_, _) =>
        {
            settings.IdleEnabled = idleOn.IsChecked == true;
            idleSound.IsEnabled = settings.IdleEnabled;
        tickOn.IsChecked = settings.TickEnabled;
        tickVol.Value = settings.TickVolume;
        tickVol.IsEnabled = settings.TickEnabled;
            Persist();
        };
        idleSound.SelectionChanged += (_, _) =>
        {
            settings.IdleSound = idleSound.SelectedItem as string;
            if (!_loading) Sound.Play(settings.IdleSound);
            Persist();
        };
        focusSound.SelectionChanged += (_, _) =>
        {
            settings.FocusDoneSound = focusSound.SelectedItem as string;
            if (!_loading) Sound.Play(settings.FocusDoneSound);
            Persist();
        };
        restSound.SelectionChanged += (_, _) =>
        {
            settings.RestDoneSound = restSound.SelectedItem as string;
            if (!_loading) Sound.Play(settings.RestDoneSound);
            Persist();
        };

        _loading = false;
    }

    /// <summary>改一下存一下。<c>_loading</c> 挡住构造期间那几次赋值触发的回调。</summary>
    private void Persist()
    {
        if (_loading) return;
        _settings.Save();
    }
}
