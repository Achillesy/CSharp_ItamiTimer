using System.Diagnostics;
using System.Text.Json;

namespace ItamiTimer.App;

/// <summary>
/// 闹钟命中时执行 rules.json 里预设的命令（DESIGN.md §9）。
/// 不硬编码关机——读 <c>executeCommand</c> 字段，按当前 OS 取一条：
///
/// <code>
/// {
///   "groups": { ... },
///   "executeCommand": {
///     "windows": "shutdown /s /t 0",
///     "macos":   "osascript -e 'tell application \"System Events\" to shut down'"
///   }
/// }
/// </code>
///
/// **没配就什么都不做**——但一定要在日志里说清楚为什么（§8.1a：界面可以沉默，
/// 理由不能丢）。2026-08-02 之前这里有三条**静默**的 return（没有键 / 当前 OS 没
/// 对应项 / 值是空串），用户打开 Execute 开关、等到点、什么都没发生，
/// 而日志里一个字都没有——查不起。
/// </summary>
public static class Command
{
    /// <summary>执行 rules.json 里的 <c>executeCommand.{os}</c>。</summary>
    public static void Execute()
    {
        var cmd = LoadForCurrentOs();
        if (string.IsNullOrWhiteSpace(cmd)) return;   // 原因已经在 LoadForCurrentOs 里记过

        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArgs = OperatingSystem.IsWindows() ? $"/c {cmd}" : $"-c \"{cmd}\"";

        // 先记「即将执行」再执行：命令很可能是关机，之后未必还有机会写日志。
        Log.Info($"Alarm fired; running executeCommand: {cmd}");

        try
        {
            var p = Process.Start(new ProcessStartInfo(shell, shellArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) { Log.Warn("executeCommand: the shell process did not start"); return; }

            // 输出捞进日志——**这是唯一能事后查「命令到底跑没跑」的地方**。
            // 放到后台等，绝不阻塞 UI 线程；等不到就算了（关机命令会把我们自己带走）。
            _ = Task.Run(async () =>
            {
                try
                {
                    var stdout = await p.StandardOutput.ReadToEndAsync();
                    var stderr = await p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();

                    Log.Info($"executeCommand exited with {p.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(stdout)) Log.Info($"  stdout: {stdout.Trim()}");
                    if (!string.IsNullOrWhiteSpace(stderr)) Log.Warn($"  stderr: {stderr.Trim()}");
                }
                catch (Exception e) { Log.Error("executeCommand: failed to collect output", e); }
            });
        }
        catch (Exception e)
        {
            Log.Error($"Failed to run executeCommand ({cmd})", e);
        }
    }

    private static string? LoadForCurrentOs()
    {
        var key = OperatingSystem.IsWindows() ? "windows" : "macos";
        try
        {
            var path = AppData.RulesPath();
            if (!File.Exists(path))
            {
                Log.Warn($"executeCommand: rules.json not found at {path}; nothing to run");
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("executeCommand", out var cmd))
            {
                Log.Warn($"executeCommand: no \"executeCommand\" section in {path}; nothing to run");
                return null;
            }
            if (!cmd.TryGetProperty(key, out var v) || string.IsNullOrWhiteSpace(v.GetString()))
            {
                Log.Warn($"executeCommand: no \"{key}\" entry under executeCommand; nothing to run");
                return null;
            }
            return v.GetString();
        }
        catch (Exception e)
        {
            Log.Error("executeCommand: failed to read rules.json", e);
            return null;
        }
    }
}
