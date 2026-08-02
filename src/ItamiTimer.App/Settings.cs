using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// User settings. Three notification sounds (task done, rest done, keyboard/mouse idle)
/// plus the tick sound's toggle and volume, plus two toggles sitting directly in the
/// window's top-right corner: master mute, window pin.
///
/// The layout follows the "Focus sessions" settings page of the Windows Clock app.
///
/// ⚠️ **This is the second thing this program writes to disk**, and it doesn't conflict
/// with this project's "never write to disk" rule -- that rule forbids **task state** being
/// persisted (no current-task.json, no accumulators, quitting = abandoning), so that state
/// is always derivable from ActivityWatch history. Settings aren't task state: they don't
/// participate in any judgment, and deleting this file still leaves the program running
/// fine, just back to default sounds. Same reasoning as the log's exception (§8.1a).
///
/// Lives at <c>%LOCALAPPDATA%\ItamiTimer\settings.json</c>, the same directory as the log.
/// Read/write failures are always swallowed and fall back to defaults -- failing to read
/// settings shouldn't stop the program from starting.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("focusDoneEnabled")] public bool FocusDoneEnabled { get; set; } = true;
    [JsonPropertyName("focusDoneSound")] public string? FocusDoneSound { get; set; }
    [JsonPropertyName("restDoneEnabled")] public bool RestDoneEnabled { get; set; } = true;
    [JsonPropertyName("restDoneSound")] public string? RestDoneSound { get; set; }
    [JsonPropertyName("idleEnabled")] public bool IdleEnabled { get; set; } = true;
    [JsonPropertyName("idleSound")] public string? IdleSound { get; set; }
    /// <summary>The sound played when the alarm fires.</summary>
    [JsonPropertyName("commandSound")] public string? CommandSound { get; set; }

    /// <summary>
    /// Runs the command preset in rules.json when the alarm fires.
    /// Doesn't persist across sessions -- forced back to off in Load() on every launch.
    /// </summary>
    [JsonPropertyName("commandEnabled")] public bool CommandEnabled { get; set; }

    /// <summary>
    /// The one and only thing the alarm needs to persist: the ring time computed from the
    /// last time the hand was moved (written on exit, see
    /// <c>MainWindow.SaveAlarmOnExit</c>). The yellow hand's position is derived from it
    /// mod 12 hours (<see cref="AlarmClock.Position"/>) -- whether it changed or fired
    /// doesn't need recording separately: the time point is valid as long as it hasn't
    /// passed, and once it has, all that's left is the yellow hand's position as an
    /// afterimage (<see cref="AlarmClock.Restore"/>).
    /// </summary>
    [JsonPropertyName("alarmFireAt")] public DateTime? AlarmFireAt { get; set; }

    /// <summary>
    /// The tick toggle. **It's exactly the speaker icon in the top-right corner** -- it
    /// isn't in the settings window at all, because ticking is the **clock's own**
    /// function, unrelated to nudging you back to work, so its toggle sits on the clock,
    /// one click away (§8.3.7). The settings window only controls its volume.
    /// </summary>
    [JsonPropertyName("tickEnabled")] public bool TickEnabled { get; set; }

    /// <summary>Force ticking: when on, the main window's ticking icon is hidden and can't be manually turned off.</summary>
    /// <summary>The last selected goal's name. Restored on startup.</summary>
    [JsonPropertyName("selectedGroup")] public string? SelectedGroup { get; set; }

    // Accumulated focus time (during) isn't here -- it lives in its own during.json (§11.2).
    // This file gets rewritten wholesale by the program at any time, while accumulated
    // time is the one piece of data that, once lost, can never be recovered.

    [JsonPropertyName("forceTicking")] public bool ForceTicking { get; set; }

    /// <summary>Tick volume, 0-100. The timbre is synthesized, no options to choose from (<see cref="Tick"/>).</summary>
    [JsonPropertyName("tickVolume")] public int TickVolume { get; set; } = 35;

    /// <summary>The pin icon in the top-right corner: window pinning. A manual toggle with no automatic pinning/unpinning of any kind (<see cref="WindowPin"/>).</summary>
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    /// <summary>
    /// ActivityWatch's address (§11.1). **Edit this file in a text editor** -- it isn't in
    /// the settings window, which only has room for sounds (§8.3.2).
    ///
    /// Before this existed, this value **wasn't configurable at all**: `AwClient`'s default
    /// parameter was hard-coded to this string, and all three call sites used
    /// `new AwClient()` with nothing passed in. So "the config happens to read as the
    /// default value" and "there was no config at all" looked identical -- now that it's a
    /// field in the file, at least it can be changed.
    /// </summary>
    [JsonPropertyName("awBaseUrl")] public string AwBaseUrl { get; set; } = "http://127.0.0.1:5600";

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "settings.json");


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

        // Shutting down at the due time **doesn't persist across sessions** (user,
        // 2026-07-30): forced back to off on every launch. An unexpired alarm still fires
        // after a restart, but it will never run the shutdown command again -- shutting
        // down requires re-enabling the toggle in the current session. This lives in Load
        // rather than being cleared on exit: a crash gets covered too (fail-safe).
        s.CommandEnabled = false;

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
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, AppData.JsonOptions));
        }
        catch (Exception e)
        {
            // Couldn't save it, fine -- it's still in effect for this run. The program
            // must never crash just because it couldn't write settings.
            Log.Error("Failed to write settings; this change lives only in memory", e);
        }
    }
}
