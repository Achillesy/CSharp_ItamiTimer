using System.Runtime.InteropServices;

namespace ItamiTimer.App;

/// <summary>
/// The movement's tick sound. **Synthesized at runtime, no audio asset of any kind**
/// (the user's choice of option A, 2026-07-28).
///
/// Why it has to be synthesized: none of the seventy wav files in `C:\Windows\Media`
/// **contains a single tick** -- they're all notification and ringtone sounds (ding /
/// chord / chimes / tada / Alarm / Ring / Windows Notify *). So §8.3.1's rule of "only use
/// sounds the OS already ships with" hits a wall here for the first time.
/// (macOS's 14 aiff sounds have even less to offer -- Basso / Glass / Ping / Tink are all
/// notification chimes.)
///
/// Synthesizing actually fits this project's whole approach better: the dial, the tomato,
/// the dominoes, the icon are all drawn in code -- **whatever can be computed, gets
/// computed**. And a tick happens to be one of the easiest sounds to compute -- it's just
/// a short broadband pulse.
///
/// The recipe: white noise x fast exponential decay (the "click") + a damped sine (the
/// wooden cavity's resonance). **"Tick" and "tock" have different timbres**, alternating on
/// odd and even seconds -- a real movement's escapement is asymmetric between its two
/// sides, and that asymmetry is exactly what makes it sound like a clock instead of a
/// metronome.
///
/// **The synthesis half is pure arithmetic, identical on both platforms**; the divergence
/// is only in the last step, how that byte stream gets handed off: Windows has
/// `SND_MEMORY` to play a wav straight out of memory, while macOS's AudioServices only
/// accepts a file URL, so that side has to write a temp file first (see
/// <see cref="MacPath"/>).
/// </summary>
public static class Tick
{
    private const int SampleRate = 44100;
    private const double DurationSec = 0.035;

    private const int SND_ASYNC = 0x0001;
    private const int SND_MEMORY = 0x0004;
    private const int SND_NODEFAULT = 0x0002;
    private const int SND_NOSTOP = 0x0010; // Skip if the channel is busy -- don't interrupt an alarm/notification

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundMem(byte[] data, IntPtr mod, int flags);

    private static byte[]? _tick, _tock;
    private static int _bakedVolume = -1;

    /// <summary>
    /// Where the temp wav lands on macOS. **Doesn't conflict with §8.1's "never write to
    /// disk"** -- that rule forbids **task state** being persisted (no
    /// current-task.json, no accumulators, quitting = abandoning), so that state is always
    /// derivable from ActivityWatch history. These two files are just a buffer in the
    /// playback pipeline -- delete them and the program runs the same, they just grow back
    /// a second later.
    /// </summary>
    private static string MacPath(bool tock)
        => Path.Combine(Path.GetTempPath(), tock ? "itami-tock.wav" : "itami-tick.wav");

    /// <summary>
    /// Plays one sound. Whether <paramref name="second"/> is odd or even decides "tick" or
    /// "tock". <paramref name="volume"/> is 0-100; changing it re-synthesizes (synthesis is
    /// cheap, 1500 sample points).
    /// </summary>
    public static void Play(int second, int volume)
    {
        if (volume <= 0) return;
        try
        {
            if (_bakedVolume != volume)
            {
                _tick = Render(2800, 0.0045, 0.011, volume, seed: 1);
                _tock = Render(2300, 0.0055, 0.014, volume, seed: 2);
                _bakedVolume = volume;
                if (OperatingSystem.IsMacOS()) BakeMacFiles();
            }

            var tock = second % 2 != 0;

            if (OperatingSystem.IsWindows())
                PlaySoundMem(tock ? _tock! : _tick!, IntPtr.Zero,
                    SND_ASYNC | SND_MEMORY | SND_NODEFAULT | SND_NOSTOP);
            else if (OperatingSystem.IsMacOS())
                MacAudio.Play(MacPath(tock));
        }
        catch (Exception e)
        {
            Log.Error("Tick failed; skipping this one", e);
        }
    }

    /// <summary>
    /// Writes the two just-synthesized sounds to temp files, and tells
    /// <see cref="MacAudio"/> to forget the old SoundID.
    ///
    /// **This forgetting step can't be skipped**: a SystemSoundID ingests the audio data at
    /// the moment it's created, and overwriting the same-named file afterward has no
    /// effect -- the symptom is dragging the volume slider doing nothing, still playing at
    /// the old volume forever.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void BakeMacFiles()
    {
        foreach (var tock in (bool[])[false, true])
        {
            var path = MacPath(tock);
            MacAudio.Forget(path);
            File.WriteAllBytes(path, tock ? _tock! : _tick!);
        }
    }

    /// <summary>
    /// Cuts off whatever's currently playing (used when muting, or closing the window).
    ///
    /// **A no-op on macOS**: the AudioServices system-sound API has no "stop" action of any
    /// kind. The cost is exactly what the Windows-side comment already noted -- at most 35
    /// extra milliseconds of sound.
    /// </summary>
    public static void Stop()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { PlaySoundMem(null!, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT); }
        catch { /* Couldn't cut it off, fine -- at most 35 extra milliseconds of sound */ }
    }

    /// <summary>
    /// Synthesizes one span of 16-bit mono PCM, writing it into memory together with the
    /// 44-byte WAV header -- `SND_MEMORY` wants exactly one complete wav byte stream, no
    /// disk involved.
    /// </summary>
    /// <param name="freq">The cavity's resonant frequency. Tick is a bit higher, tock is a bit lower.</param>
    /// <param name="tauClick">The decay constant (seconds) of the "click". Smaller = crisper.</param>
    /// <param name="tauBody">The decay constant (seconds) of the resonant tail.</param>
    /// <param name="seed">The noise seed. Fixed, so every tick doesn't sound different.</param>
    private static byte[] Render(double freq, double tauClick, double tauBody, int volume, int seed)
    {
        var n = (int)(SampleRate * DurationSec);
        var rnd = new Random(seed);
        var amp = Math.Clamp(volume, 0, 100) / 100.0 * 0.55;   // 0.55: leaves headroom, avoids clipping

        var pcm = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            var click = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t / tauClick);
            var body = Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t / tauBody);

            // 0.5ms fade-in at the start: jumping straight from 0 to peak would add a DC pop
            var fadeIn = Math.Min(1.0, t / 0.0005);
            var v = (0.62 * click + 0.38 * body) * fadeIn * amp;
            pcm[i] = (short)(Math.Clamp(v, -1, 1) * short.MaxValue);
        }

        var bytes = new byte[44 + n * 2];
        using var ms = new MemoryStream(bytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + n * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);                        // fmt chunk length
        w.Write((short)1);                  // PCM
        w.Write((short)1);                  // Mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);            // Byte rate
        w.Write((short)2);                  // Block align
        w.Write((short)16);                 // Bit depth
        w.Write("data"u8);
        w.Write(n * 2);
        foreach (var s in pcm) w.Write(s);
        return bytes;
    }
}
