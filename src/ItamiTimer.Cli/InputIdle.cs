using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ItamiTimer.Cli;

/// <summary>
/// 全系统键鼠空闲时间（DESIGN.md §8.3.6）。
///
/// **这个信号只用来决定什么时候催用户，绝不参与任何核算。** 判定输入仍然只有
/// AW 的两个 bucket（原则 0）。所以它属于第 9 层，放在这里而不是 Core——
/// Core 的 net10.0 挡得住 UI 框架，但挡不住 P/Invoke，那半条靠纪律。
///
/// 为什么不干脆拿它当在座信号、彻底不要 afk：**AW 在 ItamiTimer 关掉时照样在
/// 记，本程序自己的采样不会**。原则 3 要求关掉界面不影响结果——在座数据若来自
/// 本程序轮询，一关就是个永久的洞（不像 AW 那样事后能补查）。
/// </summary>
public static class InputIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    // 用经典的 DllImport 而不是 LibraryImport：后者要求整个项目开
    // AllowUnsafeBlocks，为这一个调用不值得。
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>距离最后一次键鼠输入过了多久。拿不到就返回 <see cref="TimeSpan.Zero"/>（当作刚动过，宁可不催）。</summary>
    public static TimeSpan Elapsed()
    {
        if (!OperatingSystem.IsWindows()) return TimeSpan.Zero;
        return WindowsElapsed();
    }

    [SupportedOSPlatform("windows")]
    private static TimeSpan WindowsElapsed()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // 两个都是 32 位毫秒计数、49.7 天回绕。无符号减法在回绕时依然给出正确的差值。
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
