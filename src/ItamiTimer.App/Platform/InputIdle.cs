using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ItamiTimer;

/// <summary>
/// System-wide keyboard/mouse idle time.
///
/// **This signal only decides when to nudge the user, and never participates in any
/// accounting.** Judgment's input is still exclusively ActivityWatch's two buckets
/// (Principle 0). So this belongs to layer 9, and lives here rather than in Core -- Core's
/// net10.0 blocks UI frameworks, but not P/Invoke; that half is enforced by discipline
/// alone.
///
/// The file lives in App (§8.5: platform-specific code belongs to this layer), and gets
/// linked into ItamiTimer.Cli.csproj with &lt;Compile Link&gt; -- the same source compiled
/// on both sides, not copied. That's why the namespace is the neutral ItamiTimer, not tied
/// to either project.
///
/// Why not just treat this as the presence signal and drop afk entirely: **ActivityWatch
/// keeps recording even while ItamiTimer is closed; this program's own sampling doesn't**.
/// Principle 3 requires that closing the UI not affect the outcome -- if presence data came
/// from this program's own polling, closing it would leave a permanent hole (unlike
/// ActivityWatch, which can be queried retroactively).
/// </summary>
public static class InputIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    // Uses the classic DllImport rather than LibraryImport: the latter requires the whole
    // project to turn on AllowUnsafeBlocks, not worth it for this one call.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    /// <summary>
    /// The macOS side. <c>kCGEventSourceStateCombinedSessionState</c> = 0 means "input
    /// across the entire login session", matching the same scope as
    /// <c>GetLastInputInfo</c> (not limited to this process); <c>kCGAnyInputEventType</c>
    /// = 0xFFFFFFFF means "any kind of input event".
    ///
    /// **Doesn't need Accessibility permission** -- it only asks "how long since the last
    /// input", installs no event hook, and can't read any input content. Verified on
    /// macOS 26.5.2.
    /// </summary>
    [DllImport(ApplicationServices)]
    private static extern double CGEventSourceSecondsSinceLastEventType(uint stateId, uint eventType);

    /// <summary>How long since the last keyboard/mouse input. Returns <see cref="TimeSpan.Zero"/> if it can't be determined (treated as just moved -- better not to nudge than to nudge wrongly).</summary>
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
            // If it can't be read, treat it as just moved -- better not to nudge than to
            // nudge out of nowhere (§8.3.5: this beep is a rescue, not a report; a false
            // positive is worse than a missed one).
            //
            // **Deliberately doesn't log anything here**: this file needs to be linkable
            // into ItamiTimer.Cli unchanged via <Compile Link> (see the class doc comment),
            // so it can't depend on the App layer's Log.
            return TimeSpan.Zero;
        }
    }

    [SupportedOSPlatform("windows")]
    private static TimeSpan WindowsElapsed()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // Both are 32-bit millisecond counters that wrap every 49.7 days. Unsigned
        // subtraction still gives the correct difference across the wraparound.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
