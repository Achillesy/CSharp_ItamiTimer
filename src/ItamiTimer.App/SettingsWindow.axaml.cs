using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// The settings window. Three notification sounds (task done / rest done / keyboard-mouse
/// idle) each with a toggle and a sound choice, plus the alarm (ring + shut down at the due
/// time) and the tick's **volume**. Laid out after the Windows Clock app's "Focus
/// sessions" settings page.
///
/// **The tick's toggle isn't here** -- it lives on the speaker in the top-right corner of
/// the clock (DECISIONS C4). Ticking is the clock's own function, a different category
/// from the three nudges that push you back to work.
///
/// **Not a single word of explanation** (DECISIONS D6): a title plus the controls, the rest
/// is left to guess.
///
/// No "OK / Cancel": change it, it's saved, the same approach as the system's own settings.
/// Selecting a sound plays it once immediately, **regardless of whether its toggle is
/// on** -- picking a ringtone is about wanting to hear it, which is a separate matter from
/// "will this actually ring later".
///
/// 卡片顺序（2026-08-09 用户指定）：Force Ticking、Focus complete、Break over、
/// Idle Warning、Alarms、Command。**代码全部靠 <c>FindControl</c> 按名字取控件**，
/// 所以顺序完全由 axaml 说了算，重排卡片不用动这里。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private bool _loading = true;

    public SettingsWindow() : this(new Settings()) { }

    /// <param name="rules">
    /// 启动时那份 rules.json，只用来给 Command 卡最底下那行预览兜底——
    /// <see cref="Command.Preview"/> 会先重读文件，读不出来才用它（跟到点执行时一样）。
    /// </param>
    public SettingsWindow(Settings settings, GroupRules? rules = null)
    {
        _settings = settings;
        AvaloniaXamlLoader.Load(this);

        var names = Sound.Available();

        // Three "toggle + sound" cards, wired up identically.
        WireSoundCard("FocusOn", "FocusSound", names,
            () => settings.FocusDoneEnabled, v => settings.FocusDoneEnabled = v,
            () => settings.FocusDoneSound, v => settings.FocusDoneSound = v);
        WireSoundCard("RestOn", "RestSound", names,
            () => settings.RestDoneEnabled, v => settings.RestDoneEnabled = v,
            () => settings.RestDoneSound, v => settings.RestDoneSound = v);
        WireSoundCard("IdleOn", "IdleSound", names,
            () => settings.IdleEnabled, v => settings.IdleEnabled = v,
            () => settings.IdleSound, v => settings.IdleSound = v);

        // Alarms 清单卡（DESIGN §17）：跟三声通知同一种"开关+音色"结构，不是 Command 卡
        // 那种互斥关系——这个开关只管响不响，检查清单/弹通知这条主链路是强制的、不受
        // 它控制，跟 Execute 完全独立。
        WireSoundCard("AlarmsOn", "AlarmsSound", names,
            () => settings.AlarmsListEnabled, v => settings.AlarmsListEnabled = v,
            () => settings.AlarmsListSound, v => settings.AlarmsListSound = v);

        // The Command card: Execute on -> runs the command, sound picker greys out. Execute off -> rings, sound is selectable.
        {
            var toggle = this.FindControl<ToggleSwitch>("ExecuteOn")!;
            var combo = this.FindControl<ComboBox>("CommandSound")!;
            var preview = this.FindControl<TextBlock>("CommandPreview")!;
            combo.ItemsSource = names;
            toggle.IsChecked = settings.CommandEnabled;
            combo.SelectedItem = settings.CommandSound;
            combo.IsEnabled = !settings.CommandEnabled; // Mutually exclusive: turning on Execute disables the sound picker

            // 每次拨到 On 都重新问一遍，不是构造时算一次存着：`itami commands --select N`
            // 可能在程序开着的时候改过 rules.json，而到点执行时读的正是那时候的文件。
            void ShowPreview()
            {
                preview.IsVisible = settings.CommandEnabled;
                if (!preview.IsVisible) return;
                preview.Text = Command.Preview(rules)
                    ?? $"(no executeCommand.{Command.OsKey} in rules.json)";
            }
            ShowPreview();

            toggle.IsCheckedChanged += (_, _) =>
            {
                settings.CommandEnabled = toggle.IsChecked == true;
                combo.IsEnabled = !settings.CommandEnabled;
                ShowPreview();
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
            // Plays live while dragging: volume is the one setting that "can't be tuned
            // right without hearing it". This **ignores** TickEnabled -- the user is
            // adjusting the volume, which means they want to hear it, even if the speaker
            // is currently off.
            if (!_loading) Tick.Play(0, settings.TickVolume);
            Persist();
        };

        _loading = false;
    }

    /// <summary>
    /// All the wiring for one "toggle + sound dropdown" card: initial value, toggle
    /// linkage, playing a preview and saving on selection. <paramref name="comboFollowsToggle"/>
    /// controls whether the dropdown gets disabled along with the toggle -- the alarm's
    /// card passes false: a sound can be picked even while it's off.
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

    /// <summary>Change it, save it. <c>_loading</c> blocks the callbacks triggered by the initial assignments during construction.</summary>
    private void Persist()
    {
        if (_loading) return;
        _settings.Save();
    }
}
