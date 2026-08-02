using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ItamiTimer.App;

/// <summary>
/// The single-instance limit.
///
/// **A named Mutex decides liveness, not a process scan**: scanning process names has to
/// handle cases like "same name but a different program" or "a zombie process still
/// holding the name", while a named Mutex is maintained by the OS itself -- a process
/// exiting always releases it, so there's no such thing as "the name wasn't released after
/// last time's crash".
///
/// **When a second instance finds the lock taken, it doesn't pop an error or just quit**:
/// it brings the already-running window to the foreground and quits quietly itself -- the
/// user's action (double-clicking the icon / a hotkey) should always have some effect,
/// rather than turning into a click with no feedback at all.
///
/// **Only Windows has the "bring to foreground" step**: `FindWindow` +
/// `SetForegroundWindow` are Win32 APIs, and macOS has no zero-dependency equivalent. On
/// macOS this degrades to "silently reject the second instance" -- the single-instance
/// guarantee itself still holds, it just loses the nicety of bringing the existing window
/// forward.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "ItamiTimer-SingleInstance";

    // Looked up by window title on Windows (matching MainWindow.axaml's Title) --
    // UI text is English, but the window title is the product name in Chinese,
    // so the same string is used here.
    private const string WindowTitle = "一袋米要扛几楼";

    // Held to prevent GC from collecting it -- once the Mutex is collected it's released,
    // and a second instance would then wrongly think it's the first.
    // The OS releases it automatically when the process exits; no manual Dispose needed.
    private static Mutex? _mutex;

    /// <summary>
    /// Attempts to claim the single-instance lock. <c>true</c> = this is the first
    /// instance, proceed with normal startup; <c>false</c> = one is already running, the
    /// caller should quit right away (bringing the old window to the foreground on Windows).
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
        if (hwnd == IntPtr.Zero) return;   // Nothing to be done if it can't be found, just quit quietly

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
