using System.Diagnostics;

namespace ItamiTimer.App;

/// <summary>
/// 到点关机。闹钟命中时调用系统自带的关机命令——跟 <see cref="Sound"/> 同一条纪律：
/// 只用系统自带的东西，不自己写倒计时提示、不弹确认框、出了错就吞掉写日志。
/// </summary>
public static class Shutdown
{
    public static void Now()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo(
                    "osascript", "-e \"tell application \\\"System Events\\\" to shut down\"")
                { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Error("Failed to invoke shutdown command", e);
        }
    }
}
