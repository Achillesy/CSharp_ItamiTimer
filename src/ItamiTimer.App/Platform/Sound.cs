using System.Runtime.InteropServices;

namespace ItamiTimer.App;

/// <summary>
/// 提示音。**只用系统自带的音**（用户 2026-07-28：「直接使用系统提供的声音」），
/// 不打包任何音频资源 —— 跟表盘不用位图是同一条纪律。
///
/// 这是整个程序**唯一**的提醒手段了。原来那套"置顶但不抢焦点"被用户否掉：
/// 「不要再纠结窗口置顶这种事情了。逻辑混乱，又容易出错。」
///
/// 平台差异只有两条，**都在这个文件里收口**：去哪儿找、拿什么放。
///
/// <code>
///            音库                                放音
/// Windows    C:\Windows\Media\*.wav              winmm 的 PlaySound
/// macOS      ~/Library/Sounds                    AudioToolbox（见 MacAudio）
///            /Library/Sounds
///            /System/Library/Sounds  \*.aiff
/// </code>
///
/// macOS 那三个目录按**优先级从高到低**排 —— 这是系统自己的约定，用户放在
/// <c>~/Library/Sounds</c> 里的同名文件盖住系统那份。所以列举时要去重，先来的赢。
/// </summary>
public static class Sound
{
    private const string WindowsDir = @"C:\Windows\Media";
    private const string WindowsExt = ".wav";

    /// <summary>macOS 的音库，优先级从高到低。系统自带的那 14 个在最后一档。</summary>
    private static readonly string[] MacDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Sounds"),
        "/Library/Sounds",
        "/System/Library/Sounds",
    ];
    private const string MacExt = ".aiff";

    private const int SND_ASYNC = 0x0001;      // 立刻返回，别把界面卡住
    private const int SND_FILENAME = 0x00020000;
    private const int SND_NODEFAULT = 0x0002;  // 找不到就安静，别退化成"叮"

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? name, IntPtr mod, int flags);

    private static (string[] Dirs, string Ext) Library => OperatingSystem.IsWindows()
        ? ([WindowsDir], WindowsExt)
        : (MacDirs, MacExt);

    /// <summary>系统自带的可选音色，按名字排序。名字就是文件名去掉扩展名。</summary>
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
                        names.Add(n);   // 高优先级目录先走，同名的后面那份自然进不来
            }
            catch (Exception e)
            {
                Log.Error($"Failed to enumerate system sounds in {dir}", e);
            }
        }

        return [.. names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>名字 → 完整路径。按优先级取第一个存在的，都没有就返回 null。</summary>
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
    /// 按名字放一声。名字为空、文件不存在、播放失败 —— 一律安静收场。
    /// **提示音绝不能把程序搞挂**，跟日志同一个原则。
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

    /// <summary>从候选里挑第一个装机自带的，挑不到就退回列表里的第一个。</summary>
    public static string? PreferredOrFirst(params string[] wanted)
    {
        var all = Available();
        foreach (var w in wanted)
            if (all.Contains(w, StringComparer.OrdinalIgnoreCase)) return w;
        return all.Count > 0 ? all[0] : null;
    }
}
