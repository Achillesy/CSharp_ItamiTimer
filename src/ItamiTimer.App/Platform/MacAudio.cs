using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// The macOS playback base. Both <see cref="Sound"/> (system notification sounds) and
/// <see cref="Tick"/> (the synthesized tick) go through here -- it's this side's
/// counterpart to winmm's <c>PlaySound</c>.
///
/// **Why AudioToolbox and not <c>afplay</c>**: the tick fires **once a second**. Playing it
/// via a child process would mean forking 3,600 times an hour, and afplay's own startup
/// latency is tens of milliseconds -- that much jitter on top of a 35-millisecond "click"
/// falls apart audibly, and the clock would start to sound like it's gasping. AudioServices
/// is an in-process call; a SystemSoundID is created once and kept around, and playing it
/// is just one line.
///
/// **No NuGet audio package pulled in**: AudioToolbox is a framework the OS already ships
/// with, the same rule as the Windows side only using winmm -- the dial uses no bitmaps,
/// notification sounds bundle no wav files, playback pulls in nothing third-party.
///
/// Verified on macOS 26.5.2 / arm64: <c>AudioServicesCreateSystemSoundID</c> returns 0,
/// sound plays correctly.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacAudio
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(
        IntPtr allocator, byte[] path, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(AudioToolbox)]
    private static extern int AudioServicesCreateSystemSoundID(IntPtr fileUrl, out uint soundId);

    [DllImport(AudioToolbox)]
    private static extern void AudioServicesPlaySystemSound(uint soundId);

    [DllImport(AudioToolbox)]
    private static extern int AudioServicesDisposeSystemSoundID(uint soundId);

    /// <summary>
    /// Path -> SystemSoundID. **Created once and kept**: the creation step reads the
    /// entire file in and decodes it, so rebuilding it every second would mean reading from
    /// disk every second. There are only two tick sounds and three notification sounds, so
    /// this cache never grows large.
    /// </summary>
    private static readonly Dictionary<string, uint> Ids = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// Plays one sound. **Fails quietly if it can't play** -- the same idea as the winmm
    /// side's <c>SND_NODEFAULT</c>: if the file can't be found, better no sound at all than
    /// falling back to an out-of-place system "ding".
    /// </summary>
    public static void Play(string path)
    {
        try
        {
            uint id;
            lock (Gate)
            {
                if (!Ids.TryGetValue(path, out id))
                {
                    id = Create(path);
                    if (id == 0) return;
                    Ids[path] = id;
                }
            }
            AudioServicesPlaySystemSound(id);
        }
        catch (Exception e)
        {
            Log.Error($"Playback failed: {path}", e);
        }
    }

    /// <summary>
    /// Forgets a file's already-created SoundID. **Must be called when the volume
    /// changes** -- a SystemSoundID ingests the audio data the moment it's created, and
    /// overwriting the same-named file afterward has no effect (it would keep playing at
    /// the old volume forever).
    /// </summary>
    public static void Forget(string path)
    {
        lock (Gate)
        {
            if (!Ids.Remove(path, out var id)) return;
            try { AudioServicesDisposeSystemSoundID(id); } catch { /* Doesn't matter if releasing it fails */ }
        }
    }

    private static uint Create(string path)
    {
        // CFURL wants the file-system representation (UTF-8 bytes, doesn't need a trailing 0, length is passed explicitly)
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
        if (url == IntPtr.Zero) { Log.Warn($"Could not create CFURL for {path}"); return 0; }

        try
        {
            var status = AudioServicesCreateSystemSoundID(url, out var id);
            if (status != 0) { Log.Warn($"Could not create SystemSoundID (status {status}) for {path}"); return 0; }
            return id;
        }
        finally
        {
            CFRelease(url);
        }
    }
}
