using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// macOS 的放音底座。<see cref="Sound"/>（系统提示音）和 <see cref="Tick"/>（合成的
/// 滴答）都走这里 —— 它是 winmm 那个 <c>PlaySound</c> 在 macOS 这一侧的对应物。
///
/// **为什么是 AudioToolbox 而不是 <c>afplay</c>**：滴答是**每秒一次**的。拿子进程放
/// 就是一小时 fork 三千六百次，而且 afplay 自己的启动延迟有几十毫秒 —— 那点抖动
/// 加在一声 35 毫秒的"咔"上，听感就散了，钟会变得像在喘。AudioServices 是进程内
/// 调用，SystemSoundID 建一次就一直留着，放的时候只是一句话的事。
///
/// **不引入任何 NuGet 音频包**：AudioToolbox 是系统自带的框架，跟 Windows 那边只用
/// winmm 是同一条纪律 —— 表盘不用位图、提示音不打包 wav、放音不引第三方。
///
/// 已在 macOS 26.5.2 / arm64 上实测：<c>AudioServicesCreateSystemSoundID</c> 返回 0，
/// 出声正常。
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
    /// 路径 → SystemSoundID。**建一次就留着**：创建那一步要把整个文件读进来解码，
    /// 每秒重建一遍等于每秒读一次盘。滴答只有两个音、提示音只有三条，缓存不会长大。
    /// </summary>
    private static readonly Dictionary<string, uint> Ids = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// 放一声。**放不出来就安静收场** —— 跟 winmm 那边的 <c>SND_NODEFAULT</c> 同一个
    /// 意思：找不到文件宁可没声音，也不要退化成一个突兀的系统"叮"。
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
    /// 忘掉某个文件已经建好的 SoundID。**改音量时必须叫一次** —— SystemSoundID 在创建
    /// 那一刻就把音频数据吃进去了，之后覆盖同名文件是不会生效的（会一直放老音量）。
    /// </summary>
    public static void Forget(string path)
    {
        lock (Gate)
        {
            if (!Ids.Remove(path, out var id)) return;
            try { AudioServicesDisposeSystemSoundID(id); } catch { /* 释放失败无所谓 */ }
        }
    }

    private static uint Create(string path)
    {
        // CFURL 要的是文件系统表示（UTF-8 字节，不带结尾 0 也行，长度是显式传的）
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
