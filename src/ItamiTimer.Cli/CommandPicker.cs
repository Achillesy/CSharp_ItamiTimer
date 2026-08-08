using ItamiTimer.App;
using ItamiTimer.Core;

namespace ItamiTimer.Cli;

/// <summary>
/// `itami commands` 的三个模式：列出 / 换第一条 / 试跑一条。
///
/// **为什么这件事值得有个命令行工具**（用户 2026-08-08 提的）：`executeCommand` 是一份
/// 常用命令收藏夹，闹钟永远只跑第一条（DECISIONS E9），想换就得手工编辑 rules.json 挪行；
/// 而"这条命令到底管不管用"在此之前**只能靠等闹钟真到点**才知道——2026-08-08 那个引号
/// bug（DECISIONS L1）能活到用户手上，正是因为没有任何办法单独试跑一条命令。
///
/// ⚠️ **试跑走的是 <see cref="Command.ExecuteAsync"/>，跟闹钟到点走的是同一份源码**
/// （csproj 里 link 进来的，不是抄的）。这是这个工具全部的价值所在：如果这里自己写一遍
/// "怎么调 shell"，那 CLI 测过了 App 照样可能坏——L1 那个 bug 就恰好只在 App 那条路上犯。
/// 别为了"CLI 不该依赖 App"这种洁癖把它拆成两份实现。
/// </summary>
public static class CommandPicker
{
    /// <summary>选中一条之后干什么。</summary>
    public enum Mode
    {
        /// <summary>只看，不动文件。</summary>
        List,
        /// <summary>选中的挪到第一位（= 换掉闹钟到点会跑的那条），写回 rules.json。</summary>
        Promote,
        /// <summary>立刻试跑选中的那条，文件不动。</summary>
        Test,
    }

    public static async Task<int> RunAsync(string rulesPath, Mode mode)
    {
        if (!File.Exists(rulesPath))
        {
            Console.Error.WriteLine($"\nrules.json not found at {rulesPath}\n");
            return 2;
        }

        var os = Command.OsKey;
        var list = GroupRules.Load(rulesPath).CommandsFor(os);

        // 路径永远打出来。这个项目里"到底在用哪一份 rules.json"本身就是个反复要查的问题
        // （三级查找链，见 AppData.RulesPath）——问的人多半正是要改它。
        Console.WriteLine($"\n  {rulesPath}");
        Console.WriteLine($"  executeCommand.{os} — {list.Count} entr{(list.Count == 1 ? "y" : "ies")}, "
                          + "the alarm always runs #1\n");

        if (list.Count == 0)
        {
            Console.WriteLine($"  (nothing configured under \"{os}\")\n");
            return 0;
        }

        // 非交互（管道/重定向/CI）就老老实实打一份清单走人，不要去读一个根本不存在的键盘。
        if (mode == Mode.List || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            for (var i = 0; i < list.Count; i++) Console.WriteLine($"  {i}  {list[i]}");
            Console.WriteLine();
            if (mode != Mode.List)
                Console.Error.WriteLine("  (not a terminal — use --execute N to run one non-interactively)\n");
            return 0;
        }

        var picked = Select(list, mode);
        if (picked < 0) { Console.WriteLine("  cancelled\n"); return 0; }

        return mode == Mode.Promote ? Promote(rulesPath, os, picked, list) : await TestAsync(list, picked);
    }

    // ---------------------------------------------------------------- 选择

    /// <summary>↑/↓（或 k/j）移动，Enter 选中，Esc/q 取消。返回下标，取消返回 -1。</summary>
    private static int Select(IReadOnlyList<string> list, Mode mode)
    {
        var hint = mode == Mode.Promote ? "move to #1" : "run it";
        var cursor = 0;
        var firstDraw = true;

        while (true)
        {
            if (!firstDraw) Console.Write($"\e[{list.Count + 2}A");   // 回到列表顶端重画
            firstDraw = false;

            for (var i = 0; i < list.Count; i++)
            {
                // \e[K 清到行尾：上一轮画的比这一轮长时，不清会留下尾巴。
                var mark = i == 0 ? "*" : " ";
                Console.WriteLine(i == cursor
                    ? $"\e[7m> {mark} {i}  {list[i]}\e[0m\e[K"
                    : $"    {mark} {i}  {list[i]}\e[K");
            }
            Console.WriteLine($"\e[K");
            Console.WriteLine($"  ↑/↓ move · Enter {hint} · Esc cancel\e[K");

            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.K: cursor = (cursor - 1 + list.Count) % list.Count; break;
                case ConsoleKey.DownArrow or ConsoleKey.J: cursor = (cursor + 1) % list.Count; break;
                case ConsoleKey.Enter: return cursor;
                case ConsoleKey.Escape or ConsoleKey.Q: return -1;
            }
        }
    }

    // ---------------------------------------------------------------- 挪到第一位

    /// <summary>
    /// 改写 rules.json。走 <see cref="RulesText.MoveToFront"/> 做**文本搬运**而不是 JSON
    /// 往返序列化——用户的注释和缩进必须原样活下来（见那边的注释）。写之前先留一份
    /// <c>.bak</c>：这是用户手写的文件，不是程序自己的状态。
    /// </summary>
    private static int Promote(string path, string os, int index, IReadOnlyList<string> list)
    {
        if (index == 0)
        {
            Console.WriteLine("\n  already #1 — nothing to do\n");
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

        Console.WriteLine($"\n  #1 is now: {list[index]}");
        Console.WriteLine($"  backup:    {backup}");
        // 值得说一句：程序不用重启。到点那一刻 Command.ExecuteFreshAsync 会重读这个文件。
        Console.WriteLine("  A running ItamiTimer picks this up on its own — the alarm re-reads rules.json.\n");
        return 0;
    }

    // ---------------------------------------------------------------- 试跑

    /// <summary>
    /// 试跑一条。**先确认再执行**（用户 2026-08-08 拍板）：这份清单里躺着 restart /
    /// shut down / log out，光标停错一行按下 Enter 就是真关机，没有回头路。一个按键的
    /// 摩擦换掉一次不可逆的误操作，值。
    /// </summary>
    private static async Task<int> TestAsync(IReadOnlyList<string> list, int index)
    {
        Console.WriteLine($"\n  about to run:\n\n    {list[index]}\n");
        Console.Write("  run it? [y/N] ");
        var answer = Console.ReadKey(intercept: true).KeyChar;
        Console.WriteLine();

        if (answer is not ('y' or 'Y')) { Console.WriteLine("\n  cancelled\n"); return 0; }

        // 这一行就是闹钟到点走的那一行（同一份源码，csproj link 进来的）。
        // await 到命令真正结束才返回——退出码和输出这时已经写进日志了，不用再靠
        // 猜一个 sleep 时长去等（那样等短了什么都看不到，等长了白白卡着）。
        await Command.ExecuteAsync(GroupRulesOf(list), index);

        Console.WriteLine($"  done. Exit code and output are in the log:\n    {Log.Path_}\n");
        return 0;
    }

    /// <summary>
    /// 把这份清单包回一个 <see cref="GroupRules"/>，好让 <see cref="Command.ExecuteAsync"/>
    /// 拿到它——那个方法的入口是 GroupRules，而这里已经把清单读出来了。
    /// </summary>
    private static GroupRules GroupRulesOf(IReadOnlyList<string> list)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Groups"] = new Dictionary<string, object>(),
            ["executeCommand"] = new Dictionary<string, IReadOnlyList<string>> { [Command.OsKey] = list },
        });
        return GroupRules.Parse(json);
    }
}
