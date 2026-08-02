using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ItamiTimer.App;

/// <summary>
/// 单实例限制（DESIGN.md §16.4，原 ISSUE #12 的另一条）。
///
/// **命名 Mutex 判活，不是进程扫描**：扫进程名要处理「同名但不是这个程序」「僵尸进程
/// 占着名字」这类误判，命名 Mutex 是操作系统自己维护的、进程退出必然释放，不会有
/// 「上次崩溃后名字没释放」这种事。
///
/// **第二个实例发现占用后不弹错误、不退出了事**：把已经在跑的那个窗口拉到前台，
/// 自己安静退出——用户的动作（双击图标/热键）应该总是有效果，而不是变成一次没有
/// 任何反馈的空点击。
///
/// **只有 Windows 有「拉到前台」这一步**：`FindWindow` + `SetForegroundWindow` 是
/// Win32 API，macOS 没有对应的零依赖等价物。macOS 上退化成「静默拒绝第二个实例」——
/// 单实例这条硬约束仍然成立，只是少了「顺手把已有窗口调出来」这个体验糖。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "ItamiTimer-SingleInstance";

    // Windows 上窗口按标题找（跟 MainWindow.axaml 的 Title 对上）——
    // CLAUDE.md：界面文字英文，窗口标题中文是产品名，这里就该用同一个字符串。
    private const string WindowTitle = "一袋米要扛几楼";

    // 持有引用防止被 GC 回收——Mutex 一旦被回收就等于释放，第二个实例就会误判自己是第一个。
    // 进程退出时操作系统会自动释放，不需要手动 Dispose。
    private static Mutex? _mutex;

    /// <summary>
    /// 抢占单实例锁。<c>true</c> = 这是第一个实例，正常往下启动；
    /// <c>false</c> = 已经有一个在跑，调用方应该直接退出（Windows 上顺手把老窗口拉到前台）。
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return true;

        if (OperatingSystem.IsWindows()) ActivateExistingWindow();
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void ActivateExistingWindow()
    {
        var hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return;   // 找不到也没办法，安静退出就是了

        const int SW_RESTORE = 9;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowTitle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
