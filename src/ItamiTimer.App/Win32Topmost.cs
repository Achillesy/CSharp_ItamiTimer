using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace ItamiTimer.App;

/// <summary>
/// 窗口置顶开关。
///
/// ⚠️ **这跟 2026-07-28 上午被砍掉的那套「置顶提醒」不是一回事**，别混。被砍掉的是
/// **自动**置顶：程序自己判断该顶、该撤，撤的条件还要跨越会话生命周期 —— 那套东西
/// 在实机上连塌三次，用户的评价是"逻辑混乱，又容易出错"（§8.3）。
///
/// 现在这个是**手动图钉**：用户点一下顶上去，再点一下放下来。**没有状态机，没有
/// "什么时候该撤"的判断**，所以那三类 bug 一个都不会回来。同一个 Win32 调用，
/// 完全不同的东西。
/// </summary>
[SupportedOSPlatform("windows")]
public static class Win32Topmost
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>置顶开 / 关。**绝不抢焦点**（§13 第 6 条）—— 用户按图钉时正在别处打字。</summary>
    public static void Set(Window w, bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (w.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
        SetWindowPos(h, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
