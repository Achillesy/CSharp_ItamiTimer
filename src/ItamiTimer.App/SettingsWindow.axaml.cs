using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ItamiTimer.App;

/// <summary>
/// 设置窗口。**只有两条**：任务结束声音、休息结束声音（用户 2026-07-28「极致简化」，
/// 照 Windows 时钟应用的「专注时段」设置页排版）。
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
        var focusSound = this.FindControl<ComboBox>("FocusSound")!;
        var restSound = this.FindControl<ComboBox>("RestSound")!;

        focusSound.ItemsSource = names;
        restSound.ItemsSource = names;
        focusOn.IsChecked = settings.FocusDoneEnabled;
        restOn.IsChecked = settings.RestDoneEnabled;
        focusSound.SelectedItem = settings.FocusDoneSound;
        restSound.SelectedItem = settings.RestDoneSound;
        focusSound.IsEnabled = settings.FocusDoneEnabled;
        restSound.IsEnabled = settings.RestDoneEnabled;

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
