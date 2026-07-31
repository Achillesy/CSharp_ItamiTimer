using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

namespace ItamiTimer.App;

/// <summary>
/// 设置窗口。三条通知音（任务结束 / 休息结束 / 键鼠空闲）各带开关和音色，
/// 外加闹钟（响铃 + 到点关机）和滴答的**音量**。照 Windows 时钟应用的
/// 「专注时段」设置页排版。
///
/// **滴答的开关不在这里** —— 它在钟的右上角那个喇叭上（DECISIONS C4）。
/// 滴答是钟本身的功能，跟督促学习那三声不是一类东西。
///
/// **一个字的说明都没有**（DECISIONS D6）：标题 + 控件，剩下的自己猜。
///
/// 没有「确定 / 取消」：改一下存一下，跟系统设置一个路数。选中音色立刻试听一次，
/// **且不看对应开关是否打开**：挑铃声本身就是想听个响，跟"这声以后会不会真的响"
/// 是两回事。
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

        // 三张「开关 + 音色」卡，接线完全同构。
        WireSoundCard("FocusOn", "FocusSound", names,
            () => settings.FocusDoneEnabled, v => settings.FocusDoneEnabled = v,
            () => settings.FocusDoneSound, v => settings.FocusDoneSound = v);
        WireSoundCard("RestOn", "RestSound", names,
            () => settings.RestDoneEnabled, v => settings.RestDoneEnabled = v,
            () => settings.RestDoneSound, v => settings.RestDoneSound = v);
        WireSoundCard("IdleOn", "IdleSound", names,
            () => settings.IdleEnabled, v => settings.IdleEnabled = v,
            () => settings.IdleSound, v => settings.IdleSound = v);

        // Command 卡：Execute on → 执行命令、音色变灰。Execute off → 响铃、可选音色。
        {
            var toggle = this.FindControl<ToggleSwitch>("ExecuteOn")!;
            var combo = this.FindControl<ComboBox>("CommandSound")!;
            combo.ItemsSource = names;
            toggle.IsChecked = settings.CommandEnabled;
            combo.SelectedItem = settings.CommandSound;
            combo.IsEnabled = !settings.CommandEnabled; // 互斥：开 Execute 则禁用音色

            toggle.IsCheckedChanged += (_, _) =>
            {
                settings.CommandEnabled = toggle.IsChecked == true;
                combo.IsEnabled = !settings.CommandEnabled;
                Persist();
            };
            combo.SelectionChanged += (_, _) =>
            {
                settings.CommandSound = combo.SelectedItem as string;
                if (!_loading) Sound.Play(settings.CommandSound);
                Persist();
            };
        }

        var forceOn = this.FindControl<ToggleSwitch>("ForceOn")!;
        forceOn.IsChecked = settings.ForceTicking;
        forceOn.IsCheckedChanged += (_, _) =>
        {
            settings.ForceTicking = forceOn.IsChecked == true;
            Persist();
        };

        var tickVol = this.FindControl<Slider>("TickVol")!;
        tickVol.Value = settings.TickVolume;
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

        _loading = false;
    }

    /// <summary>
    /// 一张「开关 + 音色下拉框」卡的全部接线：初值、开关联动、选中即试听即保存。
    /// <paramref name="comboFollowsToggle"/> 控制下拉框是否随开关禁用——
    /// 闹钟那张卡传 false：关着也能挑铃声。
    /// </summary>
    private void WireSoundCard(
        string toggleName, string comboName, IReadOnlyList<string> names,
        Func<bool> getEnabled, Action<bool> setEnabled,
        Func<string?> getSound, Action<string?> setSound,
        bool comboFollowsToggle = true)
    {
        var toggle = this.FindControl<ToggleSwitch>(toggleName)!;
        var combo = this.FindControl<ComboBox>(comboName)!;

        combo.ItemsSource = names;
        toggle.IsChecked = getEnabled();
        combo.SelectedItem = getSound();
        if (comboFollowsToggle) combo.IsEnabled = getEnabled();

        toggle.IsCheckedChanged += (_, _) =>
        {
            setEnabled(toggle.IsChecked == true);
            if (comboFollowsToggle) combo.IsEnabled = getEnabled();
            Persist();
        };
        combo.SelectionChanged += (_, _) =>
        {
            setSound(combo.SelectedItem as string);
            if (!_loading) Sound.Play(getSound());
            Persist();
        };
    }

    /// <summary>改一下存一下。<c>_loading</c> 挡住构造期间那几次赋值触发的回调。</summary>
    private void Persist()
    {
        if (_loading) return;
        _settings.Save();
    }
}
