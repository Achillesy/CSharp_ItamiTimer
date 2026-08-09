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
    /// 跑 <c>executeCommand.{os}</c> 的 **#0**（`itami commands --execute` 用这条）。
    /// 命令表来自已经解析好的 <see cref="GroupRules"/>，**这里从不自己读文件**。
    ///
    /// 没有下标参数：闹钟永远只跑 #0，想跑别的先 `--select`（DECISIONS L14）。
    /// </summary>
    public static async Task ExecuteAsync(GroupRules? rules, CancellationToken ct = default)
    {
        var cmd = FirstCommand(rules);
        if (cmd is null) return;   // 原因已经在 FirstCommand 里记过日志了

        var psi = BuildShell(cmd, redirect: false);

        // Log "about to run" before actually running it: the command is quite possibly a
        // shutdown, and there may be no chance to log anything afterward.
        Log.Info($"Running executeCommand: {cmd}");

        try
        {
            var p = Process.Start(psi);
            if (p is null) { Log.Warn("executeCommand: the shell process did not start"); return; }

            // 等它跑完再返回。**没有管道了，所以这里不可能再卡在读流上**——唯一的等待是
            // 进程本身退出，而关机/重启等不到结果才是正常情况（进程被系统杀掉，下面这
            // 几行根本不会执行）。
            await p.WaitForExitAsync(ct);

            // 退出码仍然记一笔，但**别把它当成"命令成功了"的证据**：`shutdown /h` 在休眠
            // 被禁用时就是退出码 0（L17）。真正的失败信息在屏幕上，不在这行日志里。
            Log.Info($"executeCommand exited with {p.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"executeCommand: stopped waiting (still running): {cmd}");
            throw;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to run executeCommand ({cmd})", e);
        }
    }

    // ---------------------------------------------------------------- 两边共用的挑选与校验

    /// <summary>
    /// 挑出 <c>executeCommand.{os}</c> 的 **#0**，顺带把"为什么没得跑"记进日志。
    /// 拿不到就返回 null——**每一条 null 路径都必须先说清楚原因**（§8.1a）：2026-08-02
    /// 之前这里有三个**静默** return，用户开着 Execute、时间到了、什么都没发生，日志里
    /// 一个字都没有，根本没法查。
    ///
    /// CLI 和 App 共用这一份，跟 <see cref="BuildShell"/> 同一个道理（L25）。
    /// </summary>
    private static string? FirstCommand(GroupRules? rules)
    {
        var key = OsKey;

        if (rules is null)
        {
            Log.Warn("executeCommand: rules.json never loaded; nothing to run");
            return null;
        }

        var list = rules.CommandsFor(key);
        if (list.Count == 0)
        {
            Log.Warn($"executeCommand: no \"{key}\" entry in rules.json; nothing to run");
            return null;
        }

        var cmd = list[0];
        if (string.IsNullOrWhiteSpace(cmd))
        {
            Log.Warn($"executeCommand: entry #0 under \"{key}\" is empty; nothing to run");
            return null;
        }

        // **闹钟永远只跑 #0**（DECISIONS E9）：这是一份常用命令的收藏夹，想换哪条生效就把
        // 它挪到最前面——`itami commands --select N` 干的正是这件事。
        if (list.Count > 1)
            Log.Info($"executeCommand: {list.Count} entries under \"{key}\"; only #0 runs");

        return cmd;
    }

    // ---------------------------------------------------------------- 两边共用的 shell 构造

    /// <summary>
    /// **CLI 和 App 用的是同一个 psi 构造**（2026-08-09，DECISIONS L25）。抽出来只为一件事：
    /// 让"命令行里测得通、到了界面跑不动"在结构上不可能发生——shell 是哪个、参数怎么递、
    /// 命令串长什么样，两条路只有这一处定义。别为了图省事在任何调用方另写一遍。
    ///
    /// 唯一允许不同的是 <paramref name="redirect"/>：CLI 有真控制台，让子进程直接继承
    /// （Windows 的 `shutdown /h` 只把失败讲给控制台听，重定向就看不见了，L17）；
    /// App 没有控制台，必须重定向才能把输出捞进日志。
    /// </summary>
    private static ProcessStartInfo BuildShell(string cmd, bool redirect)
    {
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
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
            // 而**闹钟那边一切正常**（触发了、也调用了），只有命令自己安静地失败。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cmd);
        }
        return psi;
    }

    // ---------------------------------------------------------------- App 到点时走的路

    /// <summary>
    /// **macOS 上 App 到点时走的就是这一个方法**（2026-08-09 用户定，DECISIONS L26，
    /// 规格见 DESIGN §9.3）：直接把命令跑起来，**起完就返回**，输出交给后台任务收进日志。
    ///
    /// 为什么 macOS 不跟 Windows 一样去开一个控制台窗口：Windows 那套的**唯一理由**是
    /// `shutdown /h` 会绕过管道、只把失败讲给控制台听（L17）。**macOS 上实测没有这回事**
    /// ——`pmset -x`、`shutdown`、`open /nonexistent`、找不到的命令，失败信息全部老老实实
    /// 走 stdout/stderr，管道都抓得到。既然抓得到，就没必要为了看一眼错误去套
    /// `open → Terminal → 临时 .command → exec $SHELL -i` 那四层（用户 2026-08-09：
    /// "这么一层套一层，其实我很不喜欢"）。
    ///
    /// ⚠️ **已知代价，用户知情接受**：走 Terminal 时 Apple 事件的授权记在 Terminal 头上
    /// （实测它已获 System Events 授权）；直接执行则记在 ItamiTimer 头上，而它**不在
    /// 授权列表里**、且 ad-hoc 签名每次重装身份都变。所以 `osascript ... System Events`
    /// 那几条**第一次会弹授权框**，而到点时多半没人在键盘前——表现是命令卡着不动，
    /// 60 秒后日志里出现 "still running"。点一次"允许"就好了。
    /// 不碰 System Events 的命令（`pmset`、`open`）不受影响。
    ///
    /// ⚠️ **绝不 await**：分钟序列调完这里必须立刻往下走。命令挂死（比如就卡在那个授权框上）
    /// 也伤不到任何东西——等待和读流全在后台任务里，那边卡住只是线程池里停着一个任务。
    /// </summary>
    public static void LaunchDetached(GroupRules? fallback)
    {
        GroupRules? rules = null;
        try { rules = GroupRules.Load(AppData.RulesPath()); }
        catch (Exception e)
        {
            Log.Error("executeCommand: could not re-read rules.json; falling back to the copy loaded at startup", e);
        }

        var cmd = FirstCommand(rules ?? fallback);
        if (cmd is null) return;   // 原因已经在 FirstCommand 里记过日志了

        try
        {
            var p = Process.Start(BuildShell(cmd, redirect: true));
            if (p is null) { Log.Warn("executeCommand: the shell process did not start"); return; }

            Log.Info($"Alarm fired; running executeCommand: {cmd}");
            _ = Task.Run(() => CollectAsync(p, cmd));   // 起完就走，绝不等
        }
        catch (Exception e)
        {
            Log.Error($"Failed to run executeCommand ({cmd})", e);
        }
    }

    /// <summary>
    /// 后台收尾：读输出、等退出、写日志。**这里卡多久都不影响主程序**——调用方早就返回了。
    ///
    /// ⚠️ 两个流必须 <c>Task.WhenAll</c> **并发**读，不能先读完 stdout 再读 stderr
    /// （2026-08-09 实测，DECISIONS L27）：串行读时子进程往另一根管子写满 64KB 缓冲区就
    /// 阻塞在写上，我们阻塞在读上，双方互等——实测 300KB 的 stderr 必死锁。
    /// </summary>
    private static async Task CollectAsync(Process p, string cmd)
    {
        try
        {
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();

            // 软看门狗：只记一行，**不杀进程**。命令合法地跑很久（关机流程）跟卡在授权框上
            // 从外面分辨不了，杀错了比等着糟。这行日志是那种"卡住了"唯一的线索。
            var done = Task.WhenAll(so, se);
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(60))) != done)
                Log.Warn($"executeCommand: still running after 60s (a permission prompt may be waiting): {cmd}");

            await done;
            await p.WaitForExitAsync();

            // 退出码记一笔，但**别当成"命令成功了"的证据**：`shutdown /h` 在休眠被禁用时
            // 就是退出码 0（L17）。真正的失败信息在下面两行输出里。
            Log.Info($"executeCommand exited with {p.ExitCode}");
            if (!string.IsNullOrWhiteSpace(so.Result)) Log.Info($"  stdout: {so.Result.Trim()}");
            if (!string.IsNullOrWhiteSpace(se.Result)) Log.Warn($"  stderr: {se.Result.Trim()}");
        }
        catch (Exception e) { Log.Error("executeCommand: failed to collect output", e); }
    }

    /// <summary>
    /// **Windows 上 App 到点时走的路**（2026-08-09，DECISIONS L19，规格见 DESIGN §9.3）：
    /// 起一个**带控制台窗口**的 shell 去跑 `itami commands --execute --yes`，立刻返回。
    ///
    /// ⚠️ 起作用的是**"有一个真实控制台"**，不是"换了个解释器"——命令本身仍然由
    /// <see cref="BuildShell"/> 里的 `cmd.exe /c` 解释，`rules.json` 里的条目语义一个字
    /// 没变（L21）。绕这一圈只为让 `shutdown /h` 那类**只讲给控制台听**的失败信息能被
    /// 人看见（L17）。**macOS 上没有这个病，所以那边不走这条路**（L26）。
    ///
    /// ⚠️ `-NoExit` 由这里给，**`itami` 自己永远不停顿**（L20）。
    /// </summary>
    public static bool LaunchInShell()
    {
        // 跟 ItamiTimer.exe 同目录。安装包必须把 CLI 一起打进去（L22）。
        var exe = Path.Combine(AppContext.BaseDirectory, "itami.exe");

        if (!File.Exists(exe))
        {
            Log.Warn($"executeCommand: cannot find the itami CLI next to the app ({exe}); nothing was run");
            return false;
        }

        try
        {
            Log.Info("Alarm fired; launching a shell to run: itami commands --execute --yes");
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                // `-NoExit` 让窗口留着；`&` 是 PowerShell 的调用运算符，路径带空格时必须有它。
                Arguments = $"-NoExit -Command \"& '{exe}' commands --execute --yes\"",
                UseShellExecute = true,   // true 才会真的开一个控制台窗口
            });
            return true;
        }
        catch (Exception e)
        {
            Log.Error("executeCommand: could not start the shell", e);
            return false;
        }
    }
}
