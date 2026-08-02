using System.Diagnostics;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// Runs the command preset in rules.json when the alarm fires.
/// Nothing is hard-coded for shutdown -- reads the <c>executeCommand</c> field and picks
/// one entry for the current OS:
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
/// **Does nothing if it isn't configured** -- but must always say why in the log (§8.1a:
/// the UI can be silent, but the reason can't disappear). Before 2026-08-02 there were
/// three **silent** returns here: the user turns on Execute, the time comes, nothing
/// happens, and there's not a single word about it in the log -- impossible to investigate.
///
/// ⚠️ **This doesn't read the file.** The command table comes from `GroupRules`'s single
/// parse at startup (§15.4): the same file used to have two read paths and two sets of
/// parsing options, kept in sync only by a human remembering to, and it bit twice. Now the
/// whole of rules.json has exactly one type model, one parser.
/// </summary>
public static class Command
{
    /// <summary>
    /// Runs the **first** entry of <c>executeCommand.{os}</c>. The command table comes
    /// from the parse done at startup (<see cref="GroupRules"/>) -- this **no longer reads
    /// the file itself**.
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

        // **Always only the first entry** of the list ever runs (DECISIONS E9). This is a
        // collection of frequently-used commands: to switch which one runs, move it to the
        // front. There's no UI to pick from -- if it can be guessed, let people guess (D6).
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

        // Log "about to run" before actually running it: the command is quite possibly a
        // shutdown, and there may be no chance to log anything afterward.
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

            // Captures the output into the log -- **the only place to check afterward
            // whether the command actually ran**. Awaited in the background, never
            // blocking the UI thread; if it never comes back, that's fine too (a shutdown
            // command would take this process down with it anyway).
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
