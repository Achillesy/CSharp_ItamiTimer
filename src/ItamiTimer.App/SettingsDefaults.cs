namespace ItamiTimer.App;

/// <summary>
/// 首次运行时挑默认提示音——**从 <see cref="Settings.Load"/> 里拆出来的**（2026-08-30）。
///
/// 拆分的理由不是"这段代码不好"，而是**位置**：它是"首次启动的行为"，不是"读文件"。
/// 混在 `Settings.Load` 里的后果是 `Settings.cs` 被 <see cref="Sound"/> 钉死在 App 层，
/// 而 `itami` 需要读同一个文件里的 `awBaseUrl`——link 进去就得把整个平台音频层一起
/// 拖进控制台工具，每次启动还要扫一遍系统音效目录。
///
/// 拆完之后 `Settings.cs` 零平台依赖，跟 `AppData.cs` / `Log.cs` / `Command.cs` 一样
/// 可以 `&lt;Compile Include&gt;` 进 CLI（DECISIONS L25 那条"同一份源码"）。
/// </summary>
public sealed partial class Settings
{
    /// <summary>
    /// 界面用的完整加载：读文件 + 强制复位 Execute + 补默认提示音。
    ///
    /// ⚠️ **补默认值这一步必须留在 `Load` 内部**，不能让调用方自己记得再调一次
    /// （F5：凡是"必须记得做"的手工步骤，迟早会被忘掉——忘了的症状是提示音全变成
    /// null = 静音，而且不报错）。
    /// </summary>
    public static Settings Load()
    {
        var s = ReadRaw();

        // Shutting down at the due time **doesn't persist across sessions** (user,
        // 2026-07-30): forced back to off on every launch. An unexpired alarm still fires
        // after a restart, but it will never run the shutdown command again -- shutting
        // down requires re-enabling the toggle in the current session. This lives in Load
        // rather than being cleared on exit: a crash gets covered too (fail-safe).
        s.CommandEnabled = false;

        FillSounds(s);
        return s;
    }

    /// <summary>没设过的音色补上系统自带的；一个都找不到就是 null = 静音。</summary>
    private static void FillSounds(Settings s)
    {
        // On first run (or if the file doesn't set it), pick a sound the OS already ships
        // with. If none can be found it's null = silent.
        //
        // The two platforms' sound libraries don't share a single name, so the candidate
        // lists are written out separately per platform. Picks among macOS's 14 aiff
        // sounds: Glass for completion (bright, carries a sense of "done"), Submarine for
        // rest ending (a low single note, announcing rather than urging), Tink for idle --
        // it's the lightest of the 14.
        if (OperatingSystem.IsMacOS())
        {
            s.FocusDoneSound ??= Sound.PreferredOrFirst("Glass", "Hero", "Blow");
            s.RestDoneSound ??= Sound.PreferredOrFirst("Submarine", "Bottle", "Purr");
            s.IdleSound ??= Sound.PreferredOrFirst("Tink", "Pop", "Morse");
            // The alarm needs to be loud: Sosumi is the most "alarm-like" of the 14 system sounds.
            // ⚠️ Don't copy the names from the Windows branch -- Alarm01/Alarm02 don't
            // exist on macOS, and PreferredOrFirst would silently fall back to whichever
            // comes first alphabetically (Basso, a dull thud).
            s.CommandSound ??= Sound.PreferredOrFirst("Sosumi", "Glass", "Ping");
            // Alarms 清单跟闹钟共用同一分钟时靠音色分清是哪一路（DESIGN §17），必须是
            // 跟 CommandSound 不同的候选列表，否则两边极大概率选中同一个文件。
            s.AlarmsListSound ??= Sound.PreferredOrFirst("Funk", "Pop", "Tink");
        }
        else
        {
            s.FocusDoneSound ??= Sound.PreferredOrFirst(
                "Windows Notify System Generic", "Windows Notify", "Alarm01", "chimes");
            s.RestDoneSound ??= Sound.PreferredOrFirst(
                "Windows Notify Calendar", "Windows Proximity Notification", "Alarm02", "notify");
            // The idle sound needs to be **light**: it might fire twice a minute, and what
            // it's asking is "are you still there", not "you're done"
            s.IdleSound ??= Sound.PreferredOrFirst(
                "Windows Message Nudge", "Windows Balloon", "Windows Background", "ding");
            // This used to be missing a default for AlarmSound, so the first run was always null = silent
            s.CommandSound ??= Sound.PreferredOrFirst("Alarm02", "Alarm01", "Ring01");
            // Alarms 清单跟闹钟共用同一分钟时靠音色分清是哪一路（DESIGN §17），必须是
            // 跟 CommandSound 不同的候选列表，否则两边极大概率选中同一个文件。
            s.AlarmsListSound ??= Sound.PreferredOrFirst("Windows Notify Messaging", "notify", "chimes");
        }
    }
}
