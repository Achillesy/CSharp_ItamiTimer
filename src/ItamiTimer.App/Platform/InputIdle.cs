using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ItamiTimer;

/// <summary>
/// 全系统键鼠空闲时间（DESIGN.md §8.3.6）。
///
/// **这个信号只用来决定什么时候催用户，绝不参与任何核算。** 判定输入仍然只有
/// AW 的两个 bucket（原则 0）。所以它属于第 9 层，放在这里而不是 Core——
/// Core 的 net10.0 挡得住 UI 框架，但挡不住 P/Invoke，那半条靠纪律。
///
/// 文件放在 App（§8.5：平台特定代码归这一层），再由 ItamiTimer.Cli.csproj 用
/// &lt;Compile Link&gt; 链接过去 —— 同一份源码两边编译，不复制。命名空间因此取中性的
/// ItamiTimer，不跟着任何一个项目走。
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

    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    /// <summary>
    /// macOS 这一侧。<c>kCGEventSourceStateCombinedSessionState</c> = 0 表示"整个登录
    /// 会话的输入"，跟 <c>GetLastInputInfo</c> 的口径一致（不是只看本进程）；
    /// <c>kCGAnyInputEventType</c> = 0xFFFFFFFF 表示"任何一种输入事件"。
    ///
    /// **不需要辅助功能权限** —— 它只问"上一次输入过了多久"，不装事件钩子、
    /// 读不到任何输入内容。已在 macOS 26.5.2 上实测。
    /// </summary>
    [DllImport(ApplicationServices)]
    private static extern double CGEventSourceSecondsSinceLastEventType(uint stateId, uint eventType);

    /// <summary>距离最后一次键鼠输入过了多久。拿不到就返回 <see cref="TimeSpan.Zero"/>（当作刚动过，宁可不催）。</summary>
    public static TimeSpan Elapsed()
    {
        if (OperatingSystem.IsWindows()) return WindowsElapsed();
        if (OperatingSystem.IsMacOS()) return MacElapsed();
        return TimeSpan.Zero;
    }

    [SupportedOSPlatform("macos")]
    private static TimeSpan MacElapsed()
    {
        const uint CombinedSessionState = 0;
        const uint AnyInputEventType = 0xFFFFFFFF;
        try
        {
            var seconds = CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType);
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
        catch
        {
            // 读不到就当作刚动过 —— 宁可不催，也不要凭空催一声（§8.3.5：这一声是
            // 补救，不是通报，误报比漏报更糟）。
            //
            // 这里**刻意不记日志**：本文件要能被 ItamiTimer.Cli 用 <Compile Link>
            // 原样链过去（见类注释），所以它不能依赖 App 层的 Log。
            return TimeSpan.Zero;
        }
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
