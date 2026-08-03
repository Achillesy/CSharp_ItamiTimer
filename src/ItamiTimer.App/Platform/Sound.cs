using System.Runtime.InteropServices;
using System.Text;

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

    /// <summary>Used as the spacing between repeats when the header can't be read. Longer than any of the system sounds on either platform, so the worst case is a slightly slack rhythm, never a truncated one.</summary>
    private static readonly TimeSpan FallbackGap = TimeSpan.FromSeconds(3);

    /// <summary>A little silence added on top of the measured length: timer jitter goes both ways, and being 120ms late is inaudible while being 20ms early clips the tail on Windows.</summary>
    private static readonly TimeSpan Cushion = TimeSpan.FromMilliseconds(120);

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
        if (Resolve(name) is not { } path) { Log.Warn($"Sound file not found: {name}"); return; }
        PlayFile(path);
    }

    /// <summary>
    /// Plays one sound <paramref name="times"/> times back to back. Every sound this
    /// program makes on purpose goes through here: the alarm rings 4 times, the three
    /// notifications ring 2 (one firing, several rings -- **not** a repeating alarm,
    /// DECISIONS E5/E11). Only the Settings preview stays a single <see cref="Play"/>: it's
    /// there to audition a timbre, not to rehearse the interruption.
    ///
    /// **The spacing has to be the file's own length**, which is why <see cref="Duration"/>
    /// exists at all: winmm's <c>PlaySound</c> owns a single channel, so a second call that
    /// lands early doesn't stack -- it cuts the first one off mid-note, and four rings turn
    /// into one stutter. macOS mixes instead of cutting, so the same early call would pile
    /// the copies on top of each other. Neither one is "played four times".
    ///
    /// Unparseable header -> <see cref="FallbackGap"/>. Wrong by a bit is a slightly odd
    /// rhythm; getting it wrong here can't silence the alarm.
    ///
    /// **No lock, and nothing to lock** (asked and settled 2026-08-03, DECISIONS E12): this
    /// path touches no shared mutable state -- the only such state in the audio layer is
    /// <see cref="MacAudio"/>'s SoundID cache, which has its own gate. What a lock would
    /// actually buy is "sounds never overlap", and that's a policy, not a correctness fix:
    /// it can only be paid for by dropping a beep (the important one can lose to the
    /// unimportant one) or by queueing it (arrives late, and needs state that outlives Give
    /// Up and window close). The three notifications already can't collide -- they come off
    /// one whole-minute tick and TaskSession picks at most one of them per tick.
    /// </summary>
    public static void Repeat(string? name, int times)
    {
        if (string.IsNullOrWhiteSpace(name) || times <= 0) return;
        if (Resolve(name) is not { } path) { Log.Warn($"Sound file not found: {name}"); return; }
        if (times == 1) { PlayFile(path); return; }

        var gap = (Duration(path) ?? FallbackGap) + Cushion;
        _ = RingLoop(path, times, gap);
    }

    /// <summary>
    /// The repeat loop. Deliberately **not awaited** by the caller: the alarm check runs on
    /// the UI frame tick, and blocking it for the length of four sounds would freeze the
    /// second hand. Nothing here touches the UI, so it doesn't need the dispatcher either.
    /// </summary>
    private static async Task RingLoop(string path, int times, TimeSpan gap)
    {
        try
        {
            for (var i = 0; i < times; i++)
            {
                if (i > 0) await Task.Delay(gap).ConfigureAwait(false);
                PlayFile(path);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to repeat sound: {path}", e);
        }
    }

    /// <summary>The one place playback actually happens. Failure is quiet -- see <see cref="Play"/>.</summary>
    private static void PlayFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
            else if (OperatingSystem.IsMacOS())
                MacAudio.Play(path);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to play sound: {path}", e);
        }
    }

    /// <summary>
    /// How long a sound file plays for, read out of its header -- **no decoding, no
    /// playback, no third-party library** (the same rule as the rest of this project: what
    /// can be computed gets computed, D5).
    ///
    /// Only the two formats this program ever enumerates are handled, and each has exactly
    /// one shape worth knowing:
    ///
    /// <code>
    /// .wav  (RIFF, little-endian)   fmt   byteRate            duration = dataBytes / byteRate
    /// .aiff (FORM, big-endian)      COMM  frames, sampleRate  duration = frames / sampleRate
    /// </code>
    ///
    /// Both formats are chunked, and **chunks can come in any order** with padding to an
    /// even length -- so this walks them rather than reading fixed offsets. `C:\Windows\Media`
    /// does ship wavs with a `LIST` chunk sitting before `data`.
    ///
    /// Returns null for anything unrecognised (compressed wav with byteRate 0, AIFC,
    /// truncated file, unreadable file). The caller falls back; it never throws.
    /// </summary>
    public static TimeSpan? Duration(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            using var r = new BinaryReader(s);
            var form = Id(r);
            r.ReadInt32();                              // Container size -- unreliable in the wild, and not needed
            var kind = Id(r);

            if (form == "RIFF" && kind == "WAVE") return Wave(r, s);
            if (form == "FORM" && kind == "AIFF") return Aiff(r, s);
            return null;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not read the length of {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// A four-character chunk id. Read as **raw bytes**, not <c>BinaryReader.ReadChars</c>:
    /// that one decodes through UTF-8, where a single byte above 0x7F swallows the bytes
    /// after it and silently shifts every following chunk offset. Ids are ASCII by spec, so
    /// this only ever matters on a damaged file -- and there it turns a desync into a null.
    /// </summary>
    private static string Id(BinaryReader r) => Encoding.Latin1.GetString(r.ReadBytes(4));

    /// <summary>RIFF/WAVE: `fmt `'s byteRate plus `data`'s size. Both chunks are needed, and either order is legal.</summary>
    private static TimeSpan? Wave(BinaryReader r, Stream s)
    {
        int byteRate = 0, dataBytes = 0;

        while (s.Position + 8 <= s.Length)
        {
            var id = Id(r);
            var size = r.ReadInt32();
            if (size < 0) return null;
            var next = s.Position + size + (size & 1);   // Chunks are padded to an even length; the pad isn't counted in size

            if (id == "fmt " && size >= 16)
            {
                r.ReadInt16();                          // Format tag
                r.ReadInt16();                          // Channels
                r.ReadInt32();                          // Sample rate
                byteRate = r.ReadInt32();               // Bytes per second -- exactly the number needed, no arithmetic of our own
            }
            else if (id == "data")
            {
                dataBytes = size;
            }

            if (byteRate > 0 && dataBytes > 0) return TimeSpan.FromSeconds((double)dataBytes / byteRate);
            if (next > s.Length) break;
            s.Position = next;
        }

        return null;
    }

    /// <summary>AIFF: `COMM`'s frame count / sample rate. Big-endian throughout, and the sample rate is an 80-bit extended float (see <see cref="Extended80"/>).</summary>
    private static TimeSpan? Aiff(BinaryReader r, Stream s)
    {
        while (s.Position + 8 <= s.Length)
        {
            var id = Id(r);
            var size = BitConverter.ToInt32([.. r.ReadBytes(4).Reverse()]);
            if (size < 0) return null;
            var next = s.Position + size + (size & 1);

            if (id == "COMM" && size >= 18)
            {
                r.ReadBytes(2);                                         // Channels
                var frames = BitConverter.ToUInt32([.. r.ReadBytes(4).Reverse()]);
                r.ReadBytes(2);                                         // Bit depth
                var rate = Extended80(r.ReadBytes(10));
                return rate > 0 ? TimeSpan.FromSeconds(frames / rate) : null;
            }

            if (next > s.Length) break;
            s.Position = next;
        }

        return null;
    }

    /// <summary>
    /// The 80-bit IEEE 754 extended float AIFF stores its sample rate in -- a format no
    /// .NET type maps to, so it gets unpacked by hand: 1 sign bit, 15 exponent bits (bias
    /// 16383), then **64 explicit mantissa bits** (unlike float/double, the leading 1 is not
    /// implied). Always positive here, so the sign bit is simply masked off.
    /// </summary>
    private static double Extended80(byte[] b)
    {
        if (b.Length < 10) return 0;
        var exponent = ((b[0] & 0x7F) << 8) | b[1];
        var mantissa = BitConverter.ToUInt64([.. b[2..10].Reverse()]);
        if (exponent == 0 && mantissa == 0) return 0;
        return Math.ScaleB((double)mantissa, exponent - 16383 - 63);
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
