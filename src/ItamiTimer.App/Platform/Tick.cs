using System.Runtime.InteropServices;

namespace ItamiTimer.App;

/// <summary>
/// 机芯的滴答声。**运行时合成，不带任何音频资源**（用户 2026-07-28 选的方案 A）。
///
/// 为什么必须合成：`C:\Windows\Media` 那七十个 wav 里**一个滴答都没有** —— 全是
/// 通知音和铃声（ding / chord / chimes / tada / Alarm / Ring / Windows Notify *）。
/// 所以 §8.3.1「只用系统自带的音」这条纪律在这里第一次走不通。
/// （macOS 那 14 个 aiff 更没有 —— Basso / Glass / Ping / Tink 全是提示音。）
///
/// 合成反而更合这个项目的路子：表盘、番茄、骨牌、图标全是代码画出来的，
/// **凡是能算出来的东西就算出来**。而滴答恰好是最好算的一类声音 —— 它就是一个
/// 短促的宽带脉冲。
///
/// 音色配方：白噪声 × 快速指数衰减（那个"咔"）+ 一个阻尼正弦（木头腔体的余韵）。
/// **「滴」和「答」的音色不一样**，单双秒交替放 —— 真钟的擒纵机构两边不对称，
/// 这一点是它听起来像钟而不像节拍器的关键。
///
/// **合成那一半是纯算术，两个平台一个字都不差**；分岔只在最后一步怎么把这段字节
/// 流交出去：Windows 有 `SND_MEMORY` 可以直接放内存里的 wav，macOS 的
/// AudioServices 只认文件 URL，所以那边要先落一次临时文件（见 <see cref="MacPath"/>）。
/// </summary>
public static class Tick
{
    private const int SampleRate = 44100;
    private const double DurationSec = 0.035;

    private const int SND_ASYNC = 0x0001;
    private const int SND_MEMORY = 0x0004;
    private const int SND_NODEFAULT = 0x0002;
    private const int SND_NOSTOP = 0x0010; // 通道有人占着就跳过——不打断 Alarm/通知

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundMem(byte[] data, IntPtr mod, int flags);

    private static byte[]? _tick, _tock;
    private static int _bakedVolume = -1;

    /// <summary>
    /// macOS 落临时 wav 的位置。**跟 §8.1「不写盘」不冲突** —— 那一条禁的是
    /// **任务状态**落盘（不要 current-task.json、不要累加值、退出即放弃），
    /// 目的是让状态永远由 AW 历史推导。这两个文件是放音管道的一段缓冲，
    /// 删掉它程序照样跑，下一秒自己又长出来。
    /// </summary>
    private static string MacPath(bool tock)
        => Path.Combine(Path.GetTempPath(), tock ? "itami-tock.wav" : "itami-tick.wav");

    /// <summary>
    /// 放一声。<paramref name="second"/> 的奇偶决定放「滴」还是「答」。
    /// <paramref name="volume"/> 是 0~100，变了就重新合成一遍（合成很便宜，1500 个采样点）。
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
    /// 把刚合成好的两声写进临时文件，并让 <see cref="MacAudio"/> 忘掉旧的 SoundID。
    ///
    /// **忘掉这一步不能省**：SystemSoundID 在创建那一刻就把音频数据吃进去了，
    /// 之后覆盖同名文件是不会生效的 —— 症状是拖动音量滑块毫无反应，一直放老音量。
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
    /// 立刻掐断正在响的那一声（静音、关窗口时用）。
    ///
    /// **macOS 上是空操作**：AudioServices 那套系统音接口没有"停"这个动作。
    /// 代价正好是 Windows 那边注释里早就写着的那句 —— 最多多响 35 毫秒。
    /// </summary>
    public static void Stop()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { PlaySoundMem(null!, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT); }
        catch { /* 掐不掉就算了，最多多响 35 毫秒 */ }
    }

    /// <summary>
    /// 合成一段 16bit 单声道 PCM，连 44 字节的 WAV 头一起写进内存 —— `SND_MEMORY`
    /// 要的就是一个完整的 wav 字节流，不落盘。
    /// </summary>
    /// <param name="freq">腔体共振频率。滴高一点、答低一点。</param>
    /// <param name="tauClick">"咔"那一下的衰减常数（秒）。越小越干脆。</param>
    /// <param name="tauBody">余韵的衰减常数（秒）。</param>
    /// <param name="seed">噪声种子。固定住，免得每一声听起来都不一样。</param>
    private static byte[] Render(double freq, double tauClick, double tauBody, int volume, int seed)
    {
        var n = (int)(SampleRate * DurationSec);
        var rnd = new Random(seed);
        var amp = Math.Clamp(volume, 0, 100) / 100.0 * 0.55;   // 0.55：留足余量，别削顶

        var pcm = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            var click = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t / tauClick);
            var body = Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t / tauBody);

            // 起头 0.5ms 淡入：直接从 0 跳到峰值会多出一个直流爆音
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
        w.Write(16);                        // fmt 块长度
        w.Write((short)1);                  // PCM
        w.Write((short)1);                  // 单声道
        w.Write(SampleRate);
        w.Write(SampleRate * 2);            // 字节率
        w.Write((short)2);                  // 块对齐
        w.Write((short)16);                 // 位深
        w.Write("data"u8);
        w.Write(n * 2);
        foreach (var s in pcm) w.Write(s);
        return bytes;
    }
}
