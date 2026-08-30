using ItamiTimer;
using ItamiTimer.App;   // Command / Log / AppData：csproj 里 link 进来的同一份源文件
using ItamiTimer.Cli;
using ItamiTimer.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cmd = args.Length > 0 ? args[0] : "help";
var opt = ParseOptions(args);

// §8.3.5: the whole program has exactly one tick.
const int TickSeconds = 60;
// §8.3.6: must be **less than** aw-watcher-afk's timeout (180 seconds by default). That
// value lives in another program's config file, unreadable from this API -- changing it
// there won't raise any error here.
// Only nudge if the deviation over the minute just past exceeds this floor, filtering out
// noise like a notification briefly stealing focus.
const int NudgeFloorSeconds = 5;

try
{
    return cmd switch
    {
        "start" => await StartAsync(),
        "backfill" => await BackfillAsync(),
        "commands" => await CommandsAsync(),
        _ => Help(),
    };
}
catch (AwUnavailableException e)
{
    // §6.2: if ActivityWatch can't be reached, say so plainly and don't show any made-up progress numbers.
    Console.Error.WriteLine($"\n{e.Message}\n");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"\nSomething went wrong: {e.Message}\n");
    return 1;
}

// ---------------------------------------------------------------- Commands

/// <summary>
/// Submits a task and runs it to completion.
///
/// **A task only lives in this process, never written to disk (§2).** So there's no
/// watch / status / stop subcommand -- quitting this process is exactly abandoning the
/// task. Ctrl+C prints the report before exiting, matching the GUI's "confirm before
/// quitting" in §9.
/// </summary>
async Task<int> StartAsync()
{
    var minutes = int.Parse(opt.GetValueOrDefault("minutes", "25"));
    // 门槛只挡手滑，不是业务约束：界面 Release 的滑块是 10~50，Debug 放宽到 3~10（为了
    // 测短任务），所以 CLI 拦在 >2 —— 界面能产生的任务它全都陪得了，而 --minutes 0
    // 那种（缺口一开始就是 0、第一拍直接"达成"）挡在门外。
    if (minutes <= 2)
        throw new ArgumentException($"--minutes must be greater than 2 (got {minutes}).");

    var rules = LoadRules();

    // --group 不给就进交互选择；**给了但拼错照旧报错**，不进交互（沿用 L18 那条：
    // 只有精确合法的形式才做事，含糊的输入不该被"猜"成某个目标）。
    var group = opt.GetValueOrDefault("group") is { Length: > 0 } g
        ? g
        : PickGoal(rules);
    if (!rules.SelectableGroups.Contains(group))
        throw new ArgumentException($"No enabled goal \"{group}\" in rules.json. Available: {string.Join(", ", rules.SelectableGroups)}");

    // AW 地址跟界面读同一份 settings.json（§11.1）——CLI 唯一会读的设置项就是它
    using var aw = new AwClient(Settings.ReadRaw().AwBaseUrl);

    // §14.1: truncated to the current whole minute
    var task = new TaskRecord
    {
        StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
        FocusMinutes = minutes,
        Group = group,
    };

    Console.WriteLine($"\nGoal: {group}");
    Console.WriteLine($"Focus {minutes} min. No break — this verifies the engine, not the session.");
    Console.WriteLine($"Started at {Renderer.Clock(task.StartedAt)} (floored to the minute)\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine($"Judgment ticks every {TickSeconds}s; the mirror refreshes every second (ActivityWatch polled every {MirrorFeed.PollSeconds}s)");
    Console.WriteLine("Ctrl+C = abandon the task");
    Console.WriteLine("(the first row lands on the next whole minute)");
    Console.WriteLine();

    var buf = new JudgmentBuffer(task.StartedAt, minutes);
    var settled = 0;

    // Ctrl+C corresponds to the GUI's "click close": §9 requires showing the report before quitting, not silently dropping it.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n\n=== Task abandoned ===\n");
        Console.WriteLine(Renderer.Bill(task, buf, settled, DateTimeOffset.Now, null));
        Console.WriteLine("This round is void.\n");
        Environment.Exit(130);
    };

    // ---- 镜像：跟界面**同一份驱动**（Core 的 MirrorFeed）。§15.7 要的"验证工具和被验证
    // 对象是同一个引擎、同一个节拍"，从这一版起连"怎么取数"都统一了——干跑不再自己
    // 查 AW，而是跟 App 一样每秒喂镜像、每分钟从镜像读。
    var feed = new MirrorFeed(aw, task.StartedAt, rules, group)
    {
        OnInitialized = (w, a) => Console.WriteLine($"Mirror initialized: {AwMirror.Capacity}s, {w} window / {a} afk events"),
        OnUnavailable = why => Console.WriteLine($"       ⚠ ActivityWatch unreachable (fail-open, counts as focus): {why}"),
        OnRestored = () => Console.WriteLine("       ✓ ActivityWatch is back"),
    };

    // ---- The single loop from §8.3.5. **秒节拍**：镜像每秒推进（预测靠它），
    // 而真正的判定仍然锚在整分钟上（§4.2 / DECISIONS H9）。
    // ⚠️ **从任务起点那一分钟开始算「已经处理过」**，跟界面一致：`TaskSession` 里是
    // `_lastAwMinute = task.StartedAt`，所以第一次判定落在**起点后的第一个整分钟**。
    // 初始化成 MinValue 会在起点分钟多跑一拍——账不会错（那一拍 Cover 画的是任务开始
    // 之前的 padding 区，ElapsedSeconds 仍是 0），但会凭空多打一行「整整一分钟的承诺弧」，
    // 看着像已经过去了一分钟。用户 2026-08-30 实测报的就是这个。
    var lastMinute = task.StartedAt;
    while (true)
    {
        await feed.RefreshAsync(DateTimeOffset.Now);

        var minute = TimeGrid.FloorToMinute(DateTimeOffset.Now);
        if (minute == lastMinute)
        {
            await Task.Delay(1000);
            continue;
        }
        lastMinute = minute;

        // Both ends of the query interval are whole minutes, so the write offset is always an integer (DECISIONS H9)
        var queryStart = minute.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);
        var (win, afk) = feed.Mirror.EventsIn(queryStart, minute);

        var outcome = buf.Tick(minute, win, afk, rules, group);
        settled += outcome.SettledSeconds;
        if (outcome.SettledSeconds > 0)
            Console.WriteLine($"  Two hours in — banked {outcome.SettledSeconds / 60} min "
                            + $"and rolled the ring over.");

        var cells = buf.ToMinuteCells();

        // 每分钟一个 block：时间/进度 → （有话才打的）说明 → 两行 60 格画布。
        // 两行严格对应表盘的两圈（§8.3：圈号 = cell.Index / 60，可绘制跨度 120 分钟），
        // 所以第 N 列每分钟都指同一格，位置不会跳。
        Console.WriteLine($"{Renderer.Clock(minute, "HH:mm")}   "
                        + $"{(settled + buf.FocusedSeconds) / 60.0:F1}/{task.FocusMinutes} min");

        // 跟界面用**同一个取法**（JudgmentBuffer.LastCompleted）和**同一个归因函数**
        // （OffTaskAttribution，Core 里的纯函数）——这两处原来各写各的，其中取法那处
        // 已经不等价了（长睡之后 CLI 会报到更早的一分钟去）。
        // **没话就不打**：闪烁般的空行是噪音，而这条 block 结构不靠空行断句。
        if (JudgmentBuffer.LastCompleted(cells, buf.ElapsedSeconds) is { OffTaskSeconds: >= NudgeFloorSeconds } last)
        {
            var culprit = OffTaskAttribution.Attribute(win, last.Start, rules, group);
            Console.WriteLine(culprit is null
                ? $"  The minute just past had {last.OffTaskSeconds}s off-task."
                : $"  The minute just past had {last.OffTaskSeconds}s off-task: {culprit}");
        }

        var rows = Renderer.Rows(cells);
        Console.WriteLine($"    0  {rows[0]}");
        Console.WriteLine($"   60  {rows[1]}");
        Console.WriteLine();

        // **达成即收尾，不做休息**（2026-08-30）：itami start 验的是引擎，不是会话。
        // 休息期本来就零 AW 访问、纯本地倒计时，在这里除了让人多等五分钟没有任何验证价值；
        // 而表盘那条"拖延时休息起点跟着后退"的痛感设计是会话层的事，CLI 从来也没有。
        if (outcome.Completed)
        {
            Console.WriteLine("\n");
            Console.WriteLine(Renderer.Bill(task, buf, settled, minute, minute));
            Console.WriteLine("Focus achieved — the engine is done here. (No break: itami start verifies the engine, not the session.)\n");
            return 0;
        }

        await Task.Delay(1000);
    }
}

/// <summary>
/// 没给 `--group` 时，把 rules.json 里的目标列出来让人选一个。
///
/// 交互这套在 `commands --select` 里早就有先例（`Console.ReadKey`），这里照抄同一个
/// 形状。⚠️ **只有完全不给 `--group` 才进这里**：给了但拼错照旧报错并列出可用的，
/// 不去"猜"用户想要哪个（L18 那条"只有精确合法的形式才做事"）。
/// </summary>
string PickGoal(GroupRules rules)
{
    var goals = rules.SelectableGroups;
    if (goals.Count == 0)
        throw new ArgumentException("No enabled goals in rules.json.");
    if (goals.Count == 1) return goals[0];

    Console.WriteLine("\nGoals in rules.json:\n");
    for (var i = 0; i < goals.Count; i++)
        Console.WriteLine($"  {i}  {goals[i]}");

    while (true)
    {
        Console.Write($"\nPick one [0-{goals.Count - 1}], or q to quit: ");
        // ⚠️ 输入被重定向时（管道、脚本、CI）`ReadKey` 直接抛"没有控制台"，所以退回
        // `ReadLine`。选目标这件事本来就该能写进脚本里。
        string answer;
        if (Console.IsInputRedirected)
        {
            answer = (Console.ReadLine() ?? "q").Trim();
            Console.WriteLine(answer);
        }
        else
        {
            answer = Console.ReadKey(intercept: true).KeyChar.ToString();
            Console.WriteLine(answer);
        }

        if (answer is "q" or "Q" or "") Environment.Exit(0);
        if (int.TryParse(answer, out var n) && n >= 0 && n < goals.Count)
            return goals[n];
    }
}

async Task<int> BackfillAsync()
{
    var group = opt.GetValueOrDefault("group")
                ?? throw new ArgumentException("A goal is required: --group <name from rules.json>");
    var until = opt.TryGetValue("until", out var u)
        ? DateTimeOffset.Parse(u)
        : TimeGrid.FloorToMinute(DateTimeOffset.Now);

    var rules = LoadRules();
    if (!rules.SelectableGroups.Contains(group))
        throw new ArgumentException($"No enabled goal \"{group}\" in rules.json. Available: {string.Join(", ", rules.SelectableGroups)}");

    using var aw = new AwClient(timeoutSeconds: Backfill.ClientTimeoutSeconds);

    // 起点跟界面**完全一样**（§11.2 的 checkpoint 模型）：先看 during.json 里这个目标
    // 记到哪儿了，只有"第一次跑这个目标"（没有 checkpoint）才回退到 bucket 创建时刻。
    //
    // ⚠️ 2026-08-30 修：原来这里**无条件**走回退那条，也就是干跑永远在模拟"首次全量"，
    // 而界面在算"上次 checkpoint 到现在"——两个数根本没有可比性，而这个子命令存在的
    // 全部意义就是"界面现在会算出多少"。跟 O13（rules.json 路径）同一类毛病。
    //
    // ⚠️ **只读，绝不推进 checkpoint**：推进它是界面点 Start 那一刻唯一的写入点（§11.2），
    // 而"推进 checkpoint 这个动作本身就是回填成功的唯一证明"——干跑写一下就等于替
    // 界面把那段历史签收了，界面下次启动就再也不会去数它。
    DateTimeOffset? since = opt.TryGetValue("since", out var s) ? DateTimeOffset.Parse(s) : null;
    if (since is null)
    {
        since = During.Load().RecordedThrough(group);
        if (since is not null)
        {
            Console.WriteLine($"No --since given: starting from during.json's checkpoint for this goal "
                            + $"({since:yyyy-MM-dd HH:mm}) — exactly where the GUI would start.");
        }
        else
        {
            since = await aw.FindBucketCreatedAsync(AwClient.WindowBucketType);
            Console.WriteLine(since is null
                ? "No checkpoint for this goal and the window bucket has no creation time; falling back to one year."
                : $"No checkpoint for this goal yet (never started) — walking the whole history from the "
                + $"window bucket's creation ({since:yyyy-MM-dd HH:mm}), which is what the GUI does the first time.");
            since ??= until.AddYears(-1);
        }
    }

    if (since >= until)
    {
        Console.WriteLine($"\nCheckpoint is already at {Renderer.Clock(since.Value, "yyyy-MM-dd HH:mm")} — "
                        + "no new ground to walk. (The GUI would just re-align the checkpoint here.)\n");
        return 0;
    }

    Console.WriteLine($"\nBackfill dry run: {Renderer.Clock(since.Value, "yyyy-MM-dd HH:mm")} → " +
                      $"{Renderer.Clock(until, "yyyy-MM-dd HH:mm")}   goal: {group}");
    Console.WriteLine($"Span: {(until - since.Value).TotalDays:F1} days. Nothing is written to disk.\n");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var lastPrinted = -1L;
    var seconds = await Backfill.CountAsync(
        aw, since.Value, until, rules, group,
        (through, running) =>
        {
            if (running == lastPrinted) return;   // 空片不刷屏——按天切，一年就是 365 片
            lastPrinted = running;
            Console.WriteLine($"  through {Renderer.Clock(through, "yyyy-MM-dd HH:mm")}   "
                            + $"{running,9:N0} s = {running / 3600.0,7:F2} h");
        });

    Console.WriteLine($"\n{group}: {seconds} s = {seconds / 3600.0:F2} hours   ({sw.Elapsed.TotalSeconds:F1}s)\n");
    return 0;
}

/// <summary>
/// `executeCommand` 收藏夹的三个模式（用户 2026-08-08）。操作的是**程序真正在用的
/// 那一份 rules.json**（<see cref="AppData.RulesPath"/> 的三级查找链）——改命令的人
/// 要改的必然是生效的那一份。
///
/// （这里原来是**唯一**这么做的子命令，其余的默认读 `./rules.json`；2026-08-29 起
/// <see cref="LoadRules"/> 也改成同一条查找链了，全部子命令就此一致。）
/// </summary>
async Task<int> CommandsAsync()
{
    // `--rules` 后面没跟值时会是空串（布尔开关的形状），别把空串当路径用。
    var path = opt.GetValueOrDefault("rules") is { Length: > 0 } p ? p : AppData.RulesPath();

    // **只有精确合法的形式才做事，其余一律掉进 `--list`**（用户 2026-08-09，DECISIONS L18）：
    // 只读、不执行、不改文件，另外在清单前打一行 `ignored:` 说明是哪个参数不对。
    //
    // 为什么不是"报错就完事"：**唯一会执行东西和唯一会写文件的两条路都必须是精确形式**，
    // 这样打错一个字最多只是看到一份清单，而不是留下一个"再试一次"的模糊空间。
    // 那行 `ignored:` 也不能省——否则 `--slect 3` 会安安静静只列个清单，用户很可能以为
    // 已经选好了（跟 L12 拒绝内层 try 同一个理由：别把故障粉饰成正常）。
    var known = new[] { "list", "select", "execute", "rules", "yes" };
    string? bad =
        opt.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown ? $"--{unknown}"
        // `--execute` 不带下标，永远跑 #0：想跑别的先 `--select` 把它挪成 #0（L16）。
        : opt.GetValueOrDefault("execute") is { Length: > 0 } ex ? $"--execute {ex}"
        // `--yes` 只在 `--execute` 一起出现时有意义。
        : opt.ContainsKey("yes") && !opt.ContainsKey("execute") ? "--yes (only valid with --execute)"
        // `--select` 必须带一个数字（越界与否留给 CommandPicker.Select 判断，它认识清单长度）。
        : opt.ContainsKey("select") && opt.GetValueOrDefault("select") is not { Length: > 0 } ? "--select (needs a number)"
        : opt.TryGetValue("select", out var sel) && !int.TryParse(sel, out _) ? $"--select {sel}"
        // 裸 `commands`：什么都没给，也算不合法形式——掉清单。
        : opt.Keys.Any(k => k is "list" or "select" or "execute") ? null
        : "(no option)";

    if (bad is not null) return CommandPicker.ListWithNote(path, bad);

    if (opt.ContainsKey("execute")) return await CommandPicker.ExecuteFirstAsync(path, opt.ContainsKey("yes"));
    if (opt.ContainsKey("list")) return CommandPicker.List(path);
    return CommandPicker.Select(path, int.Parse(opt["select"]));
}

int Help()
{
    Console.WriteLine("""

        ItamiTimer (一袋米要扛几楼) — command-line layer

          itami start    [--group <goal>] [--minutes 25]
          itami backfill --group <goal> [--since ...] [--until ...]
          itami commands [--list | --select [N] | --execute]

        start dry-runs **the engine** against live ActivityWatch, on the same mirror and the
        same judgment code the window runs (DESIGN §7.5). Every minute it prints a block: the
        time and progress, a note if the minute just past went off task, then a fixed 2x60
        canvas -- the dial's two laps, one column per minute, plain ASCII:

          F 41-60s focus   M 21-40s   L 1-20s
          # off-task       * away     - still owed   . not polled

        Omit --group and it lists the goals in rules.json and asks. --minutes must be > 2.

        It verifies the engine, not the session: no break phase, no idle nudge, no rest-wedge
        projection. Focus achieved = it prints the bill and exits.

        commands works on executeCommand in the rules.json the app actually uses. The alarm
        always runs entry #0 (marked * in the listing), and re-reads the file when it fires
        — so selecting an entry takes effect immediately, without restarting ItamiTimer.

          --list       just print them, change nothing
          --select N   move entry N to #0   (rewrites rules.json, keeps a .bak)
          --execute    run #0 now, after a y/N confirm

        Anything else -- a bare --select, a misspelled switch, a number that isn't in the
        list -- prints the list with an "ignored:" note and changes nothing. Only those
        exact forms do anything, so a typo can never run or rewrite something by accident.
        (--execute also refuses piped input: pass --yes to run unattended, deliberately.)

        --execute takes no number on purpose: to try a different entry, --select it first.
        That way the entry you tested and the entry the alarm will actually run are the
        same one, by construction. It runs through exactly the same code the alarm uses,
        so "it worked here" actually means something.

        backfill dry-runs the accumulated-time count over real history (fail-closed: only
        what ActivityWatch can actually prove). Omit --since to walk the whole history,
        which is what the GUI does the first time you start that goal. It writes nothing.

        A task lives only in this process and is never written to disk: quitting itami
        abandons the current task. start and backfill only read; commands --execute writes
        to itami.log, on purpose -- that log line is the whole point of running it.
        The rules file defaults to the one the app actually uses; override it with --rules <path>.

        """);
    return 0;
}

// ---------------------------------------------------------------- bench

static Dictionary<string, string> ParseOptions(string[] args)
{
    var d = new Dictionary<string, string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        d[key] = hasValue ? args[++i] : "";
    }
    return d;
}

/// <summary>
/// 默认读**程序真正在用的那一份 rules.json**（<see cref="AppData.RulesPath"/> 的三级
/// 查找链），`--rules` 仍然可以指向任意文件。
///
/// ⚠️ **2026-08-29 改的，原来默认是当前目录下的 `./rules.json`**。改的理由是这个工具
/// 存在的意义：`itami start` 是"界面到底会怎么判"的干跑，`backfill` 算的是界面要写进
/// during.json 的同一个数——**判的规则跟界面不是同一份，这两件事就都不成立**
/// （§15.7：验证工具和被验证对象必须是同一个）。
/// </summary>
GroupRules LoadRules()
{
    var path = opt.GetValueOrDefault("rules") is { Length: > 0 } p ? p : AppData.RulesPath();
    if (!File.Exists(path))
        throw new FileNotFoundException($"Rules file not found at {Path.GetFullPath(path)}. Use --rules to point at one.");
    return GroupRules.Load(path);
}
