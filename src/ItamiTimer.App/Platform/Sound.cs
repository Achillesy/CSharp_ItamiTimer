using System.Runtime.InteropServices;

namespace ItamiTimer.App;

/// <summary>
/// Notification sounds. **Only uses sounds the OS already ships with** (user, 2026-07-28:
/// "just use the sounds the system already provides"), no audio asset bundled at all --
/// the same rule as the dial not using bitmaps.
///
/// This is the program's **only** means of nudging you, full stop. The old "pinned but
/// never steals focus" scheme was rejected by the user: "Stop fussing over window pinning.
/// The logic is a mess and keeps breaking."
///
/// There are only two platform differences, **both funneled into this one file**: where to
/// look, and what plays it.
///
/// <code>
///            Library                              Playback
/// Windows    C:\Windows\Media\*.wav              winmm's PlaySound
/// macOS      ~/Library/Sounds                    AudioToolbox (see MacAudio)
///            /Library/Sounds
///            /System/Library/Sounds  \*.aiff
/// </code>
///
/// macOS's three directories are ordered **from highest to lowest priority** -- this is the
/// system's own convention, where a same-named file in <c>~/Library/Sounds</c> overrides
/// the system's copy. So enumerating them needs deduplication, first one wins.
/// </summary>
public static class Sound
{
    private const string WindowsDir = @"C:\Windows\Media";
    private const string WindowsExt = ".wav";

    /// <summary>macOS's sound libraries, highest to lowest priority. The 14 system-provided ones are the last tier.</summary>
    private static readonly string[] MacDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Sounds"),
        "/Library/Sounds",
        "/System/Library/Sounds",
    ];
    private const string MacExt = ".aiff";

    private const int SND_ASYNC = 0x0001;      // Returns immediately, don't block the UI
    private const int SND_FILENAME = 0x00020000;
    private const int SND_NODEFAULT = 0x0002;  // Stay quiet if not found, don't fall back to a generic "ding"

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? name, IntPtr mod, int flags);

    private static (string[] Dirs, string Ext) Library => OperatingSystem.IsWindows()
        ? ([WindowsDir], WindowsExt)
        : (MacDirs, MacExt);

    /// <summary>The system-provided sounds available to choose from, sorted by name. The name is the filename with its extension stripped.</summary>
    public static IReadOnlyList<string> Available()
    {
        var (dirs, ext) = Library;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*" + ext))
                    if (Path.GetFileNameWithoutExtension(f) is { Length: > 0 } n)
                        names.Add(n);   // Higher-priority directories go first, so a same-named later entry naturally can't get in
            }
            catch (Exception e)
            {
                Log.Error($"Failed to enumerate system sounds in {dir}", e);
            }
        }

        return [.. names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Name -> full path. Takes the first one that exists, in priority order; returns null if none do.</summary>
    private static string? Resolve(string name)
    {
        var (dirs, ext) = Library;
        foreach (var dir in dirs)
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Plays one sound by name. An empty name, a missing file, a playback failure -- all
    /// fail quietly. **A notification sound must never crash the program**, the same
    /// principle as logging.
    /// </summary>
    public static void Play(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (Resolve(name) is not { } path) { Log.Warn($"Sound file not found: {name}"); return; }

            if (OperatingSystem.IsWindows())
                PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
            else if (OperatingSystem.IsMacOS())
                MacAudio.Play(path);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to play sound: {name}", e);
        }
    }

    /// <summary>Picks the first candidate that's actually shipped with the system, falling back to the first entry in the list if none match.</summary>
    public static string? PreferredOrFirst(params string[] wanted)
    {
        var all = Available();
        foreach (var w in wanted)
            if (all.Contains(w, StringComparer.OrdinalIgnoreCase)) return w;
        return all.Count > 0 ? all[0] : null;
    }
}
