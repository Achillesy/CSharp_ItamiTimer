using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

namespace ItamiTimer.App;

/// <summary>
/// 设置窗口。三条通知音（任务结束 / 休息结束 / 键鼠空闲）各带开关和音色，
/// 外加滴答的**音量**。照 Windows 时钟应用的「专注时段」设置页排版。
///
/// **滴答的开关不在这里** —— 它在钟的右上角那个喇叭上（用户 2026-07-28）。
/// 滴答是钟本身的功能，跟督促学习那三声不是一类东西，混在一页里会让人以为
/// 它也是任务的一部分。
///
/// **一个字的说明都没有**（用户 2026-07-28）：标题 + 控件，剩下的自己猜。
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
        tickVol.Value = settings.TickVolume;

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
        tickVol.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            settings.TickVolume = (int)Math.Round(tickVol.Value);
            // 拖动时实时试听：音量是唯一一个"不听见就调不准"的设置。
            // 这里【不看】TickEnabled —— 用户正在调音量，就是要听见，
            // 哪怕此刻喇叭是关着的。
            if (!_loading) Tick.Play(0, settings.TickVolume);
            Persist();
        };
        idleOn.IsCheckedChanged += (_, _) =>
        {
            settings.IdleEnabled = idleOn.IsChecked == true;
            idleSound.IsEnabled = settings.IdleEnabled;
        tickVol.Value = settings.TickVolume;
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
