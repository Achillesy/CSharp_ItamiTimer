using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// 用户设置。三条通知音（任务结束、休息结束、键鼠空闲）+ 滴答声的开关与音量，
/// 外加两个直接摆在窗口右上角的开关：总静音、窗口置顶。
///
/// 排版参照 Windows 时钟应用的「专注时段」设置页。
///
/// ⚠️ **这是本程序第二样会写盘的东西**，跟 DESIGN.md §8.1「完全不写盘」不冲突 ——
/// 那一条禁的是**任务状态**落盘（不要 current-task.json、不要累加值、退出即放弃），
/// 目的是让状态永远由 AW 历史推导出来。设置不是任务状态：它不参与任何判定，
/// 删掉它程序照样跑，只是回到默认音色。理由和日志那条例外（§8.1a）是同一个。
///
/// 放在 <c>%LOCALAPPDATA%\ItamiTimer\settings.json</c>，跟日志同一个目录。
/// 读写失败一律吞掉走默认值 —— 设置读不出来不该让程序打不开。
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("focusDoneEnabled")] public bool FocusDoneEnabled { get; set; } = true;
    [JsonPropertyName("focusDoneSound")] public string? FocusDoneSound { get; set; }
    [JsonPropertyName("restDoneEnabled")] public bool RestDoneEnabled { get; set; } = true;
    [JsonPropertyName("restDoneSound")] public string? RestDoneSound { get; set; }
    [JsonPropertyName("idleEnabled")] public bool IdleEnabled { get; set; } = true;
    [JsonPropertyName("idleSound")] public string? IdleSound { get; set; }
    /// <summary>闹钟响时的提示音。</summary>
    [JsonPropertyName("commandSound")] public string? CommandSound { get; set; }

    /// <summary>
    /// 闹钟命中时执行 rules.json 里预设的命令。
    /// 不跨会话——每次启动在 Load() 里强制复位。
    /// </summary>
    [JsonPropertyName("commandEnabled")] public bool CommandEnabled { get; set; }

    /// <summary>
    /// 闹钟唯一要持久化的东西：最后一次拨针算出的响铃时间点（退出时写，见
    /// <c>MainWindow.SaveAlarmOnExit</c>）。黄针位置由它对 12 小时取余推导
    /// （<see cref="AlarmClock.Position"/>），变没变、响没响都不用记——时间点
    /// 没过就有效，过了就只剩黄针位置这个残影（<see cref="AlarmClock.Restore"/>）。
    /// </summary>
    [JsonPropertyName("alarmFireAt")] public DateTime? AlarmFireAt { get; set; }

    /// <summary>
    /// 滴答声开关。**就是右上角那个喇叭**，设置窗口里没有它 —— 滴答是
    /// **钟本身**的功能，跟督促学习无关，所以开关摆在钟上，随手一点（§8.3.7）。
    /// 设置窗口只管它的音量。
    /// </summary>
    [JsonPropertyName("tickEnabled")] public bool TickEnabled { get; set; }

    /// <summary>强制滴答：开则主界面 Ticking 图标隐藏、不可手动关闭。</summary>
    /// <summary>上次选中的小目标名。启动时恢复选择。</summary>
    [JsonPropertyName("selectedGroup")] public string? SelectedGroup { get; set; }

    // 累计专注时长（during）不在这里——它在自己的 during.json 里（§11.2）。
    // 这份文件程序随时整份重写，而累计时长是唯一一个丢了就补不回来的数据。

    [JsonPropertyName("forceTicking")] public bool ForceTicking { get; set; }

    /// <summary>滴答音量 0~100。音色是合成的，没有可选项（<see cref="Tick"/>）。</summary>
    [JsonPropertyName("tickVolume")] public int TickVolume { get; set; } = 35;

    /// <summary>右上角那个图钉：窗口置顶。手动开关，没有任何自动收放（<see cref="WindowPin"/>）。</summary>
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    /// <summary>
    /// ActivityWatch 的地址（§11.1）。**用别的文本编辑器改这个文件**，设置窗口里没有它 ——
    /// 那一页只放声音（§8.3.2）。
    ///
    /// 在此之前这个值**根本没有配置**：`AwClient` 的默认参数写死成这个串，三个调用点
    /// 全是 `new AwClient()`，一处传参都没有。所以"读了配置恰好是默认值"和"压根没有
    /// 配置"看起来一样 —— 现在把它摆到文件里，至少改得动了。
    /// </summary>
    [JsonPropertyName("awBaseUrl")] public string AwBaseUrl { get; set; } = "http://127.0.0.1:5600";

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = File.Exists(Path_)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path_)) ?? new Settings()
                : new Settings();
        }
        catch (Exception e)
        {
            Log.Error("Failed to read settings; falling back to defaults", e);
            s = new Settings();
        }

        // 到点关机**不跨会话**（用户 2026-07-30）：每次启动强制复位。重启后未过期的
        // 闹钟会继续响，但绝不再执行关机——想关机就得本次会话里重新打开开关。
        // 放在 Load 里而不是退出时清除：崩溃退出也一样被覆盖到（fail-safe）。
        s.CommandEnabled = false;

        // 第一次运行（或文件里没写）时挑一个装机自带的音色。挑不到就是 null = 不出声。
        //
        // 两个平台的音库没有一个名字重合，所以候选列表整份分开写。macOS 那 14 个
        // aiff 的挑法：达成用 Glass（清亮、有"成了"的意思），休息结束用 Submarine
        // （低沉一声，通报而不催促），空闲用 Tink —— 它是这 14 个里最轻的一个。
        if (OperatingSystem.IsMacOS())
        {
            s.FocusDoneSound ??= Sound.PreferredOrFirst("Glass", "Hero", "Blow");
            s.RestDoneSound ??= Sound.PreferredOrFirst("Submarine", "Bottle", "Purr");
            s.IdleSound ??= Sound.PreferredOrFirst("Tink", "Pop", "Morse");
            // 闹钟要响亮：Sosumi 是 14 个系统音里最"闹钟"的一个。
            // ⚠️ 别抄 Windows 分支的名字——Alarm01/Alarm02 在 macOS 上不存在，
            // PreferredOrFirst 会静默退回字母序第一个（Basso，一声闷响）。
            s.CommandSound ??= Sound.PreferredOrFirst("Sosumi", "Glass", "Ping");
        }
        else
        {
            s.FocusDoneSound ??= Sound.PreferredOrFirst(
                "Windows Notify System Generic", "Windows Notify", "Alarm01", "chimes");
            s.RestDoneSound ??= Sound.PreferredOrFirst(
                "Windows Notify Calendar", "Windows Proximity Notification", "Alarm02", "notify");
            // 空闲那声要**轻**：它一分钟可能响两次，而且提醒的是"你还在吗"，不是"完成了"
            s.IdleSound ??= Sound.PreferredOrFirst(
                "Windows Message Nudge", "Windows Balloon", "Windows Background", "ding");
            // 之前这里漏了 AlarmSound 的默认值，第一次运行永远是 null = 不出声
            s.CommandSound ??= Sound.PreferredOrFirst("Alarm02", "Alarm01", "Ring01");
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            // 存不下就算了，本次运行内仍然生效。绝不能因为写不了设置把程序搞挂。
            Log.Error("Failed to write settings; this change lives only in memory", e);
        }
    }
}
