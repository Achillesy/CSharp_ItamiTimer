using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ItamiTimer.App;

/// <summary>
/// 提示音。**只用系统自带的 wav**（用户 2026-07-28：「直接使用系统提供的声音」），
/// 不打包任何音频资源 —— 跟表盘不用位图是同一条纪律。
///
/// 这是整个程序**唯一**的提醒手段了。原来那套"置顶但不抢焦点"被用户否掉：
/// 「不要再纠结窗口置顶这种事情了。逻辑混乱，又容易出错。」
/// </summary>
[SupportedOSPlatform("windows")]
public static class Sound
{
    private const string MediaDir = @"C:\Windows\Media";

    private const int SND_ASYNC = 0x0001;      // 立刻返回，别把界面卡住
    private const int SND_FILENAME = 0x00020000;
    private const int SND_NODEFAULT = 0x0002;  // 找不到就安静，别退化成"叮"

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? name, IntPtr mod, int flags);

    /// <summary>系统自带的可选音色，按名字排序。名字就是文件名去掉 .wav。</summary>
    public static IReadOnlyList<string> Available()
    {
        try
        {
            return [.. Directory.EnumerateFiles(MediaDir, "*.wav")
                                .Select(Path.GetFileNameWithoutExtension)
                                .OfType<string>()
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e)
        {
            Log.Error("列举系统音失败，声音下拉框会是空的", e);
            return [];
        }
    }

    /// <summary>
    /// 按名字放一声。名字为空、文件不存在、播放失败 —— 一律安静收场。
    /// **提示音绝不能把程序搞挂**，跟日志同一个原则。
    /// </summary>
    public static void Play(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = Path.Combine(MediaDir, name + ".wav");
            if (!File.Exists(path)) { Log.Warn($"提示音文件不在：{path}"); return; }
            PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
        }
        catch (Exception e)
        {
            Log.Error($"播放提示音失败：{name}", e);
        }
    }

    /// <summary>从候选里挑第一个装机自带的，挑不到就退回列表里的第一个。</summary>
    public static string? PreferredOrFirst(params string[] wanted)
    {
        var all = Available();
        foreach (var w in wanted)
            if (all.Contains(w, StringComparer.OrdinalIgnoreCase)) return w;
        return all.Count > 0 ? all[0] : null;
    }
}
