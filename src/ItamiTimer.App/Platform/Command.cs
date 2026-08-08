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
    public static Task ExecuteFreshAsync(GroupRules? fallback, CancellationToken ct = default)
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
        return ExecuteAsync(rules ?? fallback, ct: ct);
    }

    /// <summary>
    /// Runs one entry of <c>executeCommand.{os}</c> -- the **first** one unless
    /// <paramref name="index"/> says otherwise (`itami --test` / `--execute N` pick a
    /// specific one). The command table comes from an already-parsed
    /// <see cref="GroupRules"/> -- this **never reads the file itself**.
    /// </summary>
    public static async Task ExecuteAsync(GroupRules? rules, int index = 0, CancellationToken ct = default)
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

        // ⚠️ **不重定向 stdout/stderr**（2026-08-09 改，DECISIONS L19/L21）：子进程直接继承
        // 本进程的控制台，输出就落在用户眼前那个窗口里。
        //
        // 为什么这很重要，而不是"少写两行"：`shutdown /h` 在休眠被禁用的机器上**退出码 0、
        // stdout 空、stderr 空、机器纹丝不动**——它把"此系统上没有启用休眠 (126)"这句话
        // **直接写给控制台**，绕开了管道。重定向的那一版因此完全看不见失败（L17）。
        //
        // 附带好处：没有管道就没有 L15 那个"串行读 + 缓冲写满 = 死锁"的死角，也就不再需要
        // L14 那套分钟边界取消——那两条护栏都随之作废。
        //
        // `CreateNoWindow` 也不设了：这个方法现在只由 `itami`（控制台程序）调用，它本来就
        // 有窗口；App 那边不再走这里，改成起一个 shell 去跑 `itami commands --execute --yes`。
        var psi = new ProcessStartInfo(shell) { UseShellExecute = false };

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

    /// <summary>
    /// **App 到点时走的就是这一个方法**（2026-08-09，DECISIONS L19，规格见 DESIGN §9.3）：
    /// 起一个**带控制台窗口**的 shell，让它去跑 `itami commands --execute --yes`，然后
    /// 立刻返回。不重定向、不 await、不看退出码。
    ///
    /// ⚠️ 起作用的是**"有一个真实控制台"**，不是"换了个解释器"——命令本身仍然由
    /// <see cref="ExecuteAsync"/> 里的 `cmd.exe /c` / `sh -c` 解释，`rules.json` 里的条目
    /// 语义一个字没变（L21）。绕这一圈只为让 `shutdown /h` 那类**只讲给控制台听**的
    /// 失败信息能被人看见（L17）。
    ///
    /// ⚠️ `-NoExit` 由这里给，**`itami` 自己永远不停顿**（L20）：窗口活多久是启动方的事。
    /// `--yes` 只表示"不用等人按 y"，不表示"跑完就关窗"。
    ///
    /// 返回 false = 连 shell 都没起来（多半是找不到 `itami.exe`）。**这是新架构下我们
    /// 唯一还能真正判断的失败**，跟"命令跑得对不对"是两回事，所以它值得单独记一条 Error。
    /// </summary>
    public static bool LaunchInShell()
    {
        // 跟 ItamiTimer.exe 同目录。安装包必须把 CLI 一起打进去（L22）——在 2.2.0 之前
        // 打包脚本只 publish 了 App，装机的机器上根本没有这个文件。
        var exe = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "itami.exe" : "itami");

        if (!File.Exists(exe))
        {
            // `Log.Error` 要一个异常，这条路上没有——但级别本来也该是 Warn：文件不在
            // 是个可以看懂的状况（多半是从项目目录直接跑 App、CLI 没编），不是崩溃。
            Log.Warn($"executeCommand: cannot find the itami CLI next to the app ({exe}); nothing was run");
            return false;
        }

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe")
            {
                // `-NoExit` 让窗口留着；`&` 是 PowerShell 的调用运算符，路径带空格时必须有它。
                Arguments = $"-NoExit -Command \"& '{exe}' commands --execute --yes\"",
                UseShellExecute = true,   // true 才会真的开一个控制台窗口
            }
            // macOS 待做（用户 2026-08-09：那边单独做，思路一样，也只负责起 shell）。
            // 这里先按 Unix 的通用做法起一个 shell，**它不会弹出可见窗口**——所以 macOS
            // 上"能看见错误信息"这半个收益暂时拿不到，等那边单独实现 Terminal.app 那条路。
            : new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", $"'{exe}' commands --execute --yes" },
                UseShellExecute = false,
            };

        try
        {
            Log.Info($"Alarm fired; launching a shell to run: itami commands --execute --yes");
            Process.Start(psi);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("executeCommand: could not start the shell", e);
            return false;
        }
    }
}
