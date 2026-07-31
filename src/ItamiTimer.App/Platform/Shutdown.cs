using System.Diagnostics;
using System.Text.Json;

namespace ItamiTimer.App;

/// <summary>
/// 闹钟命中时执行 rules.json 里预设的命令（ISSUE_FIX #8）。
/// 不再硬编码关机——读 <c>executeCommand</c> 字段，自动选当前 OS 的命令。
/// </summary>
public static class Command
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>执行 rules.json 里的 executeCommand.{os}。</summary>
    public static void Execute()
    {
        try
        {
            var cmd = LoadForCurrentOs();
            if (string.IsNullOrWhiteSpace(cmd)) return;
            Process.Start(new ProcessStartInfo(
                OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? $"/c {cmd}" : $"-c \"{cmd}\"")
            { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Log.Error("Failed to execute command from rules.json", e);
        }
    }

    private static string? LoadForCurrentOs()
    {
        try
        {
            var path = AppData.RulesPath();
            if (!File.Exists(path)) { Log.Warn("rules.json not found; no command to execute"); return null; }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("executeCommand", out var cmd)) return null;

            var key = OperatingSystem.IsWindows() ? "windows" : "macos";
            return cmd.TryGetProperty(key, out var v) ? v.GetString() : null;
        }
        catch (Exception e)
        {
            Log.Error("Failed to read executeCommand from rules.json", e);
            return null;
        }
    }
}
