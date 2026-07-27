using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace ItamiTimer.App;

/// <summary>
/// DESIGN.md §8.3.3 —— **唯一的平台特定模块**（模块 9）。
///
/// 要做到的事：窗口跳到最前、盖住别人（包括浏览器/播放器的无边框全屏），
/// **但绝不抢键盘焦点**。用户在切走的那个应用里继续打字，字要落在那个应用。
/// 这是 §13 第 6 条验证的内容，也是这个产品设计上刻意的约束，不是遗漏。
///
/// **绝不**调用 SetForegroundWindow / SetFocus / Activate()。
///
/// 2026-07-27 实测通过（§8.5）：从**最小化**状态弹出，盖住浏览器 F11 全屏视频
/// 和 **MAME 全屏游戏**，两种情况下键盘输入都仍然落在原来那个程序里。
/// 原先担心的"独占全屏盖不住"没有出现。
/// </summary>
[SupportedOSPlatform("windows")]
public static class Win32Topmost
{
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static IntPtr Handle(Window w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>显示并置顶，不抢焦点。</summary>
    public static void ShowNoActivate(Window w)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        ShowWindow(h, SW_SHOWNOACTIVATE);
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>撤销置顶（回到正常层级），窗口仍然可见。</summary>
    public static void ClearTopmost(Window w)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 最小化。§8.3 的真实场景是"任务进行中窗口收在任务栏，偏离时才跳出来"，
    /// 所以要验的恰恰是【从最小化状态弹出】能不能不抢焦点。
    /// </summary>
    public static void Minimize(Window w)
    {
        var h = Handle(w);
        if (h != IntPtr.Zero) ShowWindow(h, SW_MINIMIZE);
    }

    private const int SW_MINIMIZE = 6;

    /// <summary>此刻拿着键盘焦点的是不是本窗口。</summary>
    public static bool IsForeground(Window w) => Handle(w) != IntPtr.Zero && GetForegroundWindow() == Handle(w);

    /// <summary>前台窗口的句柄，用来看焦点到底落在谁身上。</summary>
    public static IntPtr Foreground() => GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// 前台窗口属于哪个进程。**这才是"有没有抢焦点"的硬证据**——
    /// 靠"输入框是不是空的"来判断是不可靠的：那个输入框只有在本窗口自己
    /// 拿到键盘焦点时才收得到字，所以它为空可能只是因为焦点在别的控件上。
    /// </summary>
    public static string ForegroundProcess()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(无)";
        GetWindowThreadProcessId(h, out var pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return $"pid:{pid}"; }
    }
}
