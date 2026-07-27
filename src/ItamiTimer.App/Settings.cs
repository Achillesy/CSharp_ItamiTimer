using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// 用户设置。三条声音：任务结束、休息结束、键鼠空闲（参照 Windows 时钟应用的
/// 「专注时段」设置页排版）。第三条是用户 2026-07-28 想过之后加回来的 ——
/// 空闲检测本身不该砍，砍的只是它原来那个"置顶提醒"的表达方式。
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

    private static string Path_ => System.IO.Path.Combine(Log.Directory, "settings.json");

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
            Log.Error("读设置失败，改用默认值", e);
            s = new Settings();
        }

        // 第一次运行（或文件里没写）时挑一个装机自带的音色。挑不到就是 null = 不出声。
        s.FocusDoneSound ??= Sound.PreferredOrFirst(
            "Windows Notify System Generic", "Windows Notify", "Alarm01", "chimes");
        s.RestDoneSound ??= Sound.PreferredOrFirst(
            "Windows Notify Calendar", "Windows Proximity Notification", "Alarm02", "notify");
        // 空闲那声要**轻**：它一分钟可能响两次，而且提醒的是"你还在吗"，不是"完成了"
        s.IdleSound ??= Sound.PreferredOrFirst(
            "Windows Message Nudge", "Windows Balloon", "Windows Background", "ding");
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            // 存不下就算了，本次运行内仍然生效。绝不能因为写不了设置把程序搞挂。
            Log.Error("写设置失败，本次改动只在内存里生效", e);
        }
    }
}
