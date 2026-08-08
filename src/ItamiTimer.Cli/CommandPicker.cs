using ItamiTimer.App;
using ItamiTimer.Core;

namespace ItamiTimer.Cli;

/// <summary>
/// `itami commands` 的三个模式：`--list` 看 / `--select` 选 / `--execute` 跑。
///
/// **为什么这件事值得有个命令行工具**（用户 2026-08-08 提的）：`executeCommand` 是一份
/// 常用命令收藏夹，闹钟永远只跑 #0（DECISIONS E9），想换就得手工编辑 rules.json 挪行；
/// 而"这条命令到底管不管用"在此之前**只能靠等闹钟真到点**才知道——2026-08-08 那个引号
/// bug（DECISIONS L1）能活到用户手上，正是因为没有任何办法单独试跑一条命令。
///
/// ⚠️ **三个模式之间只有一个共享概念：#0**（2026-08-09 用户重新定的边界，DECISIONS L14）。
/// `--select` 只改文件不执行，`--execute` 只跑 #0 不带下标——想试哪条就先把它选成 #0。
/// 这样"我到底在测哪一条"这个问题**在设计上就不存在**了；上一版 `--test N` / `--execute N`
/// 各自带下标，测的那条和闹钟真正会跑的那条可以是两回事，那正是最容易骗过自己的地方。
///
/// ⚠️ **执行走的是 <see cref="Command.ExecuteAsync"/>，跟闹钟到点走的是同一份源码**
/// （csproj 里 link 进来的，不是抄的）。这是这个工具全部的价值所在：如果这里自己写一遍
/// "怎么调 shell"，那 CLI 测过了 App 照样可能坏——L1 那个 bug 就恰好只在 App 那条路上犯。
/// 别为了"CLI 不该依赖 App"这种洁癖把它拆成两份实现。
/// </summary>
public static class CommandPicker
{
    /// <summary>`--list`：只看，不动文件、不跑任何东西。</summary>
    public static int List(string rulesPath)
    {
        if (!TryLoad(rulesPath, out var os, out var list)) return 2;
        if (list.Count == 0) return Empty(os);

        Print(list);
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// 不合法的形式统统落到这里（DECISIONS L18）：**只打清单，什么都不做**，但先说一句
    /// 是哪个参数被忽略了——不说的话 `--slect 3`（打错字）会安安静静只列个清单，用户
    /// 很可能以为已经选好了。退出码非 0，脚本能察觉；屏幕上不摆错误脸。
    /// </summary>
    public static int ListWithNote(string rulesPath, string ignored)
    {
        if (!TryLoad(rulesPath, out var os, out var list)) return 2;

        Console.WriteLine($"  ignored: {ignored}");
        Console.WriteLine("  valid:   --list | --select N | --execute [--yes]\n");

        if (list.Count > 0) { Print(list); Console.WriteLine(); }
        else Empty(os);
        return 1;
    }

    /// <summary>
    /// `--select N`：把第 N 条挪到 #0，**只改 rules.json，不执行任何东西**。
    /// N 越界也掉清单（L18）——只有精确合法的形式才做事。
    /// </summary>
    public static int Select(string rulesPath, int index)
    {
        if (!TryLoad(rulesPath, out var os, out var list)) return 2;
        if (list.Count == 0) return Empty(os);

        if (index < 0 || index >= list.Count)
        {
            // 跟 ListWithNote 打一样的两行——越界跟"参数写错了"是同一类事，输出不该长得不一样。
            Console.WriteLine($"  ignored: --select {index} (out of range, {list.Count} available)");
            Console.WriteLine("  valid:   --list | --select N | --execute [--yes]\n");
            Print(list);
            Console.WriteLine();
            return 1;
        }

        return Promote(rulesPath, os, index, list);
    }

    /// <summary>
    /// `--execute`：跑 **#0**，没有下标参数——想跑别的先 `--select`（用户 2026-08-09）。
    ///
    /// **先确认再执行**（沿用用户 2026-08-08 对上一版 `--test` 的拍板）：这份清单里躺着
    /// restart / shut down / log out，而 `--execute` 现在不带任何参数、最容易从命令行历史
    /// 里翻出来再敲一次。一个按键的摩擦换掉一次不可逆的误操作，值。
    /// </summary>
    public static async Task<int> ExecuteFirstAsync(string rulesPath, bool yes)
    {
        if (!TryLoad(rulesPath, out var os, out var list)) return 2;
        if (list.Count == 0) return Empty(os);

        Console.WriteLine($"  about to run #0:\n\n    {list[0]}\n");

        // `--yes` = "不用等我按 y"。**闹钟到点那条路走的就是它**——App 起一个 shell 窗口
        // 跑 `commands --execute --yes`，那时候没有人在键盘前（DECISIONS L19/L20）。
        // 它**不表示"跑完就关窗"**：窗口留不留是启动方用 `-NoExit` 决定的，itami 自己
        // 永远不停顿，否则你在自己终端里手动跑也要被迫按一次键。
        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("  (not a terminal — pass --yes to run unattended)\n");
                return 1;
            }

            Console.Write("  run it? [y/N] ");
            var answer = Console.ReadKey(intercept: true).KeyChar;
            Console.WriteLine();
            if (answer is not ('y' or 'Y')) { Console.WriteLine("\n  cancelled\n"); return 0; }
        }

        // 这一行就是闹钟到点走的那一行（同一份源码，csproj link 进来的），而且跑的是
        // 同一条 #0 —— CLI 试通了对界面才是有意义的结论。
        // await 到命令真正结束才返回，退出码和输出这时已经写进日志了。
        await Command.ExecuteAsync(GroupRules.Load(rulesPath));

        Console.WriteLine($"\n  done. Exit code and output are in the log:\n    {Log.Path_}\n");
        return 0;
    }

    // ---------------------------------------------------------------- 共用

    /// <summary>读文件 + 打表头。路径永远打出来——"到底在用哪一份 rules.json"在这个项目里本身就是个反复要查的问题（三级查找链，见 <see cref="AppData.RulesPath"/>），问的人多半正是要改它。</summary>
    private static bool TryLoad(string rulesPath, out string os, out IReadOnlyList<string> list)
    {
        os = Command.OsKey;
        list = [];

        if (!File.Exists(rulesPath))
        {
            Console.Error.WriteLine($"\nrules.json not found at {rulesPath}\n");
            return false;
        }

        list = GroupRules.Load(rulesPath).CommandsFor(os);
        Console.WriteLine($"\n  {rulesPath}");
        Console.WriteLine($"  executeCommand.{os} — {list.Count} entr{(list.Count == 1 ? "y" : "ies")}, "
                          + "the alarm always runs #0\n");
        return true;
    }

    private static int Empty(string os)
    {
        Console.WriteLine($"  (nothing configured under \"{os}\")\n");
        return 0;
    }

    /// <summary>清单。**`*` 标的就是 #0**，也就是闹钟到点真正会跑的那条——星号和表头那句话说的是同一件事，看清单时不用回头数。</summary>
    private static void Print(IReadOnlyList<string> list)
    {
        for (var i = 0; i < list.Count; i++)
            Console.WriteLine($"  {(i == 0 ? "*" : " ")} {i}  {list[i]}");
    }

    // ---------------------------------------------------------------- 挪到 #0

    /// <summary>
    /// 改写 rules.json。走 <see cref="RulesText.MoveToFront"/> 做**文本搬运**而不是 JSON
    /// 往返序列化——用户的注释和缩进必须原样活下来（见那边的注释）。写之前先留一份
    /// <c>.bak</c>：这是用户手写的文件，不是程序自己的状态。
    /// </summary>
    private static int Promote(string path, string os, int index, IReadOnlyList<string> list)
    {
        if (index == 0)
        {
            Console.WriteLine("  already #0 — nothing to do\n");
            return 0;
        }

        var before = File.ReadAllText(path);
        var after = RulesText.MoveToFront(before, os, index);
        if (after is null)
        {
            Console.Error.WriteLine(
                "\n  Won't rewrite this file automatically — it isn't the simple shape this tool\n"
                + "  can move lines around in safely (comments inside the array, or an unexpected\n"
                + $"  layout). Move entry #{index} to the top of \"{os}\" by hand.\n");
            return 1;
        }

        var backup = path + ".bak";
        File.Copy(path, backup, overwrite: true);
        File.WriteAllText(path, after);

        Console.WriteLine($"\n  #0 is now: {list[index]}");
        Console.WriteLine($"  backup:    {backup}");
        // 值得说一句：程序不用重启。到点那一刻 Command.ExecuteFreshAsync 会重读这个文件。
        Console.WriteLine("  A running ItamiTimer picks this up on its own — the alarm re-reads rules.json.\n");
        return 0;
    }
}
