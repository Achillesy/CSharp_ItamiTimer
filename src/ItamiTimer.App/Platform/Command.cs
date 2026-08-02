using System.Diagnostics;
using ItamiTimer.Core;

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
/// 理由不能丢）。2026-08-02 之前这里有三条**静默**的 return，用户打开 Execute 开关、
/// 等到点、什么都没发生，而日志里一个字都没有——查不起。
///
/// ⚠️ **这里不读文件。** 命令表来自启动时 `GroupRules` 那一次解析（§15.4）：
/// 同一个文件曾经有两条读取路径、两套解析设置，靠人手动保持一致，咬过两次。
/// 现在整个 rules.json 只有一个类型模型、一个解析器。
/// </summary>
public static class Command
{
    /// <summary>
    /// 执行 <c>executeCommand.{os}</c> 的**第一条**。命令表来自启动时那一次解析
    /// （<see cref="GroupRules"/>），这里**不再自己读文件**。
    /// </summary>
    public static void Execute(GroupRules? rules)
    {
        var key = OperatingSystem.IsWindows() ? "windows" : "macos";

        if (rules is null)
        {
            Log.Warn("executeCommand: rules.json never loaded; nothing to run");
            return;
        }

        var list = rules.CommandsFor(key);
        if (list.Count == 0)
        {
            Log.Warn($"executeCommand: no \"{key}\" entry in rules.json; nothing to run");
            return;
        }

        // 列表里**永远只执行第一条**（DECISIONS E9）。这儿是一个常用命令的收藏夹：
        // 想换用哪条，就把它挪到第一位。没有界面去选——能猜的就让人猜（D6）。
        var cmd = list[0];
        if (string.IsNullOrWhiteSpace(cmd))
        {
            Log.Warn($"executeCommand: the first \"{key}\" entry is empty; nothing to run");
            return;
        }
        if (list.Count > 1)
            Log.Info($"executeCommand: {list.Count} entries under \"{key}\"; only the first one runs");

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
}
