using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace ItamiTimer.App;

/// <summary>
/// The window pin toggle (the pin icon in the top-right corner).
///
/// ⚠️ **Not the same thing as the "pinning nudge" scheme that got cut on the morning of
/// 2026-07-28** -- don't conflate them. What got cut was **automatic** pinning: the
/// program deciding on its own when to pin and unpin, with the unpinning condition also
/// needing to survive across session lifetimes -- that scheme fell over three times on
/// real machines, and the user's verdict was "the logic is a mess and keeps breaking"
/// (§8.3).
///
/// This one is a **manual pin**: the user clicks once to pin, clicks again to unpin.
/// **No state machine, no "when should it unpin" logic to get wrong**, so none of those
/// three categories of bugs can come back.
///
/// **Why Windows doesn't use Avalonia's built-in <c>Window.Topmost</c>**: that property
/// activates the window as a side effect on Windows, and §13 point 6 requires **never
/// stealing focus** -- the user is probably typing somewhere else when they click the pin.
/// <c>SWP_NOACTIVATE</c> is where that rule actually lands.
///
/// **macOS is the other way around**: <c>Window.Topmost</c> goes through NSWindow's window
/// level, which carries no activation semantics to begin with -- so it can just be used
/// directly, no reason to pull in another P/Invoke for it. The class was renamed from
/// <c>Win32Topmost</c> to this neutral name precisely because it's no longer only a Win32
/// path.
/// </summary>
public static class WindowPin
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Pin on / off. **Never steals focus** (§13 point 6) -- the user is probably typing somewhere else when they click the pin.</summary>
    public static void Set(Window w, bool on)
    {
        if (OperatingSystem.IsWindows()) SetWindows(w, on);
        else w.Topmost = on;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(Window w, bool on)
    {
        if (w.TryGetPlatformHandle()?.Handle is not { } h || h == IntPtr.Zero) return;
        SetWindowPos(h, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
