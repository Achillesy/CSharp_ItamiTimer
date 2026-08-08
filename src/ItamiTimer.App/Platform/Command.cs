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
    /// <summary>当前平台在 rules.json 里对应的键。CLI 那边（`itami --list`）也用它。</summary>
    public static string OsKey => OperatingSystem.IsWindows() ? "windows" : "macos";

    /// <summary>
    /// 闹钟到点走的就是这条：**每次都重新读一遍 rules.json**，而不是用启动时的快照
    /// （用户 2026-08-08：「闹钟到的时候应该重新读入，因为用户可能改过」）。
    /// `itami --list` 把某条挪到第一位之后，正在运行的程序立刻就跟着变，不用重启。
    ///
    /// ⚠️ 这**不是** §15.4 警告的"第二条读取路径"：那条护栏说的是"一个类型模型、一个
    /// 解析器"，当年的事故是两处各自用不同的 JSON 设置（一边不跳注释、一边区分大小写），
    /// 导致文件一半好用一半安静失效。这里读的仍然是 <see cref="GroupRules.Load"/>，
    /// 同一个解析器、同一套设置，只是读的时机从"启动一次"变成"每次到点"。
    ///
    /// 读失败（用户把文件改坏了）时**退回启动时的快照**并大声记日志：到点什么都不做
    /// 对一个可能是关机的命令来说更糟，而"为什么用的是旧的"必须能在日志里查到（§8.1a）。
    /// </summary>
    public static Task ExecuteFreshAsync(GroupRules? fallback)
    {
        GroupRules? rules = null;
        try
        {
            rules = GroupRules.Load(AppData.RulesPath());
        }
        catch (Exception e)
        {
            Log.Error("executeCommand: could not re-read rules.json; falling back to the copy loaded at startup", e);
        }
        return ExecuteAsync(rules ?? fallback);
    }

    /// <summary>
    /// Runs one entry of <c>executeCommand.{os}</c> -- the **first** one unless
    /// <paramref name="index"/> says otherwise (`itami --test` / `--execute N` pick a
    /// specific one). The command table comes from an already-parsed
    /// <see cref="GroupRules"/> -- this **never reads the file itself**.
    /// </summary>
    public static async Task ExecuteAsync(GroupRules? rules, int index = 0)
    {
        var key = OsKey;

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

        // **闹钟永远只跑第一条**（DECISIONS E9）。这是一份常用命令的收藏夹：想换哪条生效，
        // 就把它挪到最前面——`itami --list` 干的正是这件事。界面上不给选择器，能猜的就让人猜（D6）。
        // index 不为 0 只出现在 CLI 的 `--test` / `--execute N`，那是"试一下这条什么效果"，
        // 跟闹钟该跑哪条是两回事。
        if (index < 0 || index >= list.Count)
        {
            Log.Warn($"executeCommand: no entry #{index} under \"{key}\" ({list.Count} available); nothing to run");
            return;
        }

        var cmd = list[index];
        if (string.IsNullOrWhiteSpace(cmd))
        {
            Log.Warn($"executeCommand: entry #{index} under \"{key}\" is empty; nothing to run");
            return;
        }
        if (index == 0 && list.Count > 1)
            Log.Info($"executeCommand: {list.Count} entries under \"{key}\"; only the first one runs");

        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (OperatingSystem.IsWindows())
        {
            // `cmd.exe /c` 有它自己一套引号规则，跟 ArgumentList 用的 C 运行库转义规则
            // **对不上**，别顺手把这条也改成 ArgumentList——Windows 这条路真机验证过。
            psi.Arguments = $"/c {cmd}";
        }
        else
        {
            // ⚠️ 脚本必须作为**单独一个 argv 元素**交给 `sh -c`，绝不能拼成
            // `-c "{cmd}"` 那样一整条 Arguments 字符串（2026-08-08 修，DECISIONS L1）。
            // 拼字符串时 .NET 还要按 C 运行库规则把它切回 argv，而 rules.json 里的命令
            // 自己就带双引号（`osascript -e 'tell application "System Events" to ...'`），
            // 内层的 " 会把外层引号提前闭合，切出来是这样：
            //   argv = [ "-c",
            //            "osascript -e 'tell application System",  ← 脚本被截断
            //            "Events to restart'" ]                    ← 剩下的成了 $0
            // 结果 sh 报 `unexpected EOF while looking for matching '`、退出码 2，
            // 而**闹钟那边一切正常**（触发了、也调用了），只有命令自己安静地失败——
            // 正是这个项目栽过好几次的那一类（H12/K21）。
            // 不带引号的命令（`open /System/.../Finder.app`）恰好不受影响，所以这条
            // 在只测过那种命令时看着是好的。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cmd);
        }

        // Log "about to run" before actually running it: the command is quite possibly a
        // shutdown, and there may be no chance to log anything afterward.
        Log.Info($"Alarm fired; running executeCommand: {cmd}");

        try
        {
            var p = Process.Start(psi);
            if (p is null) { Log.Warn("executeCommand: the shell process did not start"); return; }

            // **等它真正跑完再返回**（用户 2026-08-08）。调用方是 MainWindow.OnMinute 那段
            // 直线代码，这一分钟的其余事情要排在命令之后——命令多半是关机/重启，等不到
            // 结果才是正常情况（进程被系统杀掉，下面这几行根本不会执行）。
            //
            // 先读完两个流再 WaitForExit，顺序不能反：管道缓冲区写满时子进程会阻塞在写上，
            // 那样"等它退出"就成了等一件永远不会发生的事。
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            Log.Info($"executeCommand exited with {p.ExitCode}");
            if (!string.IsNullOrWhiteSpace(stdout)) Log.Info($"  stdout: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr)) Log.Warn($"  stderr: {stderr.Trim()}");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to run executeCommand ({cmd})", e);
        }
    }
}
