using ItamiTimer;
using ItamiTimer.Cli;
using ItamiTimer.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cmd = args.Length > 0 ? args[0] : "help";
var opt = ParseOptions(args);

// §8.3.5：整个程序只有这一个节拍。
const int TickSeconds = 60;
// §8.3.6：必须【小于】aw-watcher-afk 的 timeout（默认 180 秒）。那个值在另一个
// 程序的配置文件里，API 读不到，改了这边不会有任何报错。
// 刚走完那一分钟里偏离超过这个数才提醒，滤掉通知抢焦点之类的噪音。
const int NudgeFloorSeconds = 5;

try
{
    return cmd switch
    {
        "start" => await StartAsync(),
        "replay" => await ReplayPastAsync(),
        "bench" => Bench(),
        _ => Help(),
    };
}
catch (AwUnavailableException e)
{
    // §6.2：AW 访问不了就直接说无法工作，不显示任何编造的进度数字。
    Console.Error.WriteLine($"\n{e.Message}\n");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"\nSomething went wrong: {e.Message}\n");
    return 1;
}

// ---------------------------------------------------------------- 命令

/// <summary>
/// 提交任务并一直跑到结束。
///
/// **任务只活在这个进程里，不落盘（§2）。** 所以没有 watch / status / stop 这些
/// 子命令——退出这个进程就等于放弃任务。Ctrl+C 会先给账单再退出，对应 §9 里
/// GUI 版本"退出前弹窗确认"那条。
/// </summary>
async Task<int> StartAsync()
{
    var minutes = int.Parse(opt.GetValueOrDefault("minutes", "25"));
    var group = opt.GetValueOrDefault("group")
                ?? throw new ArgumentException("A goal is required: --group <name from rules.json>");

    var rules = LoadRules();
    if (!rules.SelectableGroups.Contains(group))
        throw new ArgumentException($"No enabled goal \"{group}\" in rules.json. Available: {string.Join(", ", rules.SelectableGroups)}");

    using var aw = new AwClient();
    var winId = await aw.FindBucketIdAsync(AwClient.WindowBucketType);
    var afkId = await aw.FindBucketIdAsync(AwClient.AfkBucketType);

    // §14.1：截断到当前整分钟
    var task = new TaskRecord
    {
        StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
        FocusMinutes = minutes,
        Group = group,
    };

    Console.WriteLine($"\nGoal: {group}");
    Console.WriteLine($"Focus {minutes} min, then a {task.RestMinutes} min break");
    Console.WriteLine($"Started at {Renderer.Clock(task.StartedAt)} (floored to the minute)\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine($"Tick every {TickSeconds}s");
    Console.WriteLine("Ctrl+C = abandon the task\n");

    var buf = new JudgmentBuffer(task.StartedAt, minutes);
    var settled = 0;

    // Ctrl+C 对应 GUI 的"点关闭"：§9 要求退出前把账摆出来，不能默默丢掉。
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n\n=== Task abandoned ===\n");
        Console.WriteLine(Renderer.Bill(task, buf, settled, DateTimeOffset.Now, null));
        Console.WriteLine("This round is void.\n");
        Environment.Exit(130);
    };

    // ---- §8.3.5 的单循环。节拍锚在整分钟（§4.2），不锚在启动时刻。
    while (true)
    {
        var minute = TimeGrid.FloorToMinute(DateTimeOffset.Now);

        // 查询区间的两端都是整分钟，写入偏移因此恒为整数（DECISIONS H9）
        var queryStart = minute.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);
        var win = await aw.FetchEventsAsync(winId, queryStart, minute);
        var afk = await aw.FetchEventsAsync(afkId, queryStart, minute);

        var outcome = buf.Tick(minute, win, afk, rules, group);
        settled += outcome.SettledSeconds;
        if (outcome.SettledSeconds > 0)
            Console.WriteLine($"       ⏳ Two hours in — banked {outcome.SettledSeconds / 60} min "
                            + $"and rolled the ring over.");

        var cells = buf.ToMinuteCells();

        // 节拍是 60 秒，所以每轮单独打一行，不用 \r 原地覆盖——覆盖是为 3 秒节拍设计
        // 的，跟穿插的警告混在一起会糊成一团。一分钟一行正好是一份可读的日志。
        Console.WriteLine($"{Renderer.Clock(minute, "HH:mm")}  {Renderer.Cells(cells)}  " +
                          $"{(settled + buf.FocusedSeconds) / 60.0:F1}/{task.FocusMinutes} min");

        // 用【刚走完的那一格】当触发条件，不是【此刻在干什么】。否则 10:00:10 切走、
        // 10:00:50 切回这种短切换会整个从提醒里溜掉——而它在色块上明明是红的。
        var last = cells.LastOrDefault(c => c.FocusSeconds + c.OffTaskSeconds + c.AfkSeconds > 0);
        if (last.OffTaskSeconds >= NudgeFloorSeconds)
            Console.WriteLine($"       ⚠ The minute just past had {last.OffTaskSeconds}s off-task.");

        // 达成就是这一拍（§4.5）：不回头去账本里推一个更早的时刻，
        // 所以休息永远不会被追溯消费掉（§15.1 就是这么来的）。
        if (outcome.Completed) return Rest(task, buf, settled, minute);

        await Task.Delay(TickSeconds * 1000);
    }
}

/// <summary>
/// 休息阶段（§8.4.4a）：**纯本地计时，零 AW 访问**。
/// 一个任务对 AW 的最后一次查询就发生在专注达成那一刻。
/// </summary>
int Rest(TaskRecord task, JudgmentBuffer buf, double settled, DateTimeOffset completedAt)
{
    Console.WriteLine("\n");
    Console.WriteLine(Renderer.Bill(task, buf, settled, completedAt, completedAt));   // 账单在【达成】这一刻给

    var rest = TimeSpan.FromMinutes(task.RestMinutes);
    var restEnds = completedAt + rest;
    Console.WriteLine($"Break for {task.RestMinutes} min. Where you go and what you do does not matter.\n");

    while (DateTimeOffset.Now < restEnds)
    {
        // 每分钟淡掉 100%/休息分钟数（§8.4.4）。不是固定 10%——那样 25 分钟的任务
        // 休息结束时盘上还挂着半个色环，跟「没有色环就是邀请」打架。
        var left = rest > TimeSpan.Zero ? 1 - (DateTimeOffset.Now - completedAt) / rest : 0;
        Console.Write($"\r☕ On a break — {Math.Max(0, left) * 100:F0}% of the ring left   ");
        Thread.Sleep(1000);
    }

    Console.WriteLine("\n\nBreak over. Start another round yourself — the program never does it for you.\n");
    return 0;
}

/// <summary>
/// 拿过去的真实历史干跑一遍，不写盘、不需要等。
/// 这是验证规则写得对不对最快的办法。
/// </summary>
async Task<int> ReplayPastAsync()
{
    var minutes = int.Parse(opt.GetValueOrDefault("minutes", "25"));
    var group = opt.GetValueOrDefault("group")
                ?? throw new ArgumentException("A goal is required: --group <name from rules.json>");
    var since = DateTimeOffset.Parse(opt.GetValueOrDefault("since")
                 ?? throw new ArgumentException("A start time is required: --since \"2026-07-26 20:00\""));
    var until = opt.TryGetValue("until", out var u) ? DateTimeOffset.Parse(u) : since.AddHours(3);

    var rules = LoadRules();
    using var aw = new AwClient();
    var win = await aw.FetchEventsAsync(await aw.FindBucketIdAsync(AwClient.WindowBucketType), since, until);
    var afk = await aw.FetchEventsAsync(await aw.FindBucketIdAsync(AwClient.AfkBucketType), since, until);

    var task = new TaskRecord
    {
        StartedAt = TimeGrid.FloorToMinute(since),
        FocusMinutes = minutes,
        Group = group,
    };

    // 拿真实历史把在线那套循环原样跑一遍——**同一个引擎、同一个节拍**，
    // 只是 now 是喂进去的。这样 CLI 干跑出来的账才等于实机会算出来的账（§15.7）。
    var buf = new JudgmentBuffer(task.StartedAt, minutes);
    var settled = 0;
    DateTimeOffset? completedAt = null;

    for (var minute = task.StartedAt.AddMinutes(1); minute <= until; minute = minute.AddMinutes(1))
    {
        var outcome = buf.Tick(minute, win, afk, rules, group);
        settled += outcome.SettledSeconds;
        if (outcome.Completed) { completedAt = minute; break; }
    }

    Console.WriteLine($"\nDry run: {Renderer.Clock(task.StartedAt, "MM-dd HH:mm")} → {Renderer.Clock(until, "HH:mm")}   goal: {group}");
    Console.WriteLine($"{win.Count} window events, {afk.Count} afk events\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine(Renderer.Cells(buf.ToMinuteCells()));
    Console.WriteLine();
    Console.WriteLine(Renderer.Bill(task, buf, settled, completedAt ?? until, completedAt));
    return 0;
}

int Help()
{
    Console.WriteLine("""

        ItamiTimer (一袋米要扛几楼) — command-line layer

          itami start  --minutes 25 --group <goal>
          itami replay --since "2026-07-26 20:00" [--until ...] --minutes 25 --group <goal>
          itami bench  --minutes 25 [--pattern focused|mixed|slack]

        A task lives only in this process and is never written to disk: quitting itami
        abandons the current task (DESIGN.md §2).
        The rules file defaults to ./rules.json; override it with --rules <path>.

        """);
    return 0;
}

// ---------------------------------------------------------------- bench

/// <summary>
/// 用合成数据干跑新判定模型（JudgmentBuffer + Judgment）。
/// 不碰 AW，不写盘——纯验证 buffer 初始化和状态转换。
/// </summary>
int Bench()
{
    var minutes = int.Parse(opt.GetValueOrDefault("minutes", "25"));
    var pattern = opt.GetValueOrDefault("pattern", "mixed");

    Console.WriteLine($"\n══════════════════════════════════════════════");
    Console.WriteLine($"  Judgment Buffer Bench — {minutes} min focus, pattern: {pattern}");
    Console.WriteLine($"══════════════════════════════════════════════\n");

    // 1. 初始化
    var now = new DateTimeOffset(2026, 7, 31, 9, 1, 0, TimeSpan.FromHours(8));
    var taskStart = TimeGrid.FloorToMinute(now); // 09:01:00
    var buf = new JudgmentBuffer(taskStart, minutes);
    var rules = GroupRules.Parse("""{ "groups": { "bench": { "rules": [ { "app": "^goal$" } ] } } }""");

    Console.WriteLine($"Task start: {Renderer.Clock(taskStart)}");
    Console.WriteLine($"WallClock:  {Renderer.Clock(buf.WallClock)}  (buffer[0])");
    Console.WriteLine($"Buffer[0..{JudgmentBuffer.PaddingSeconds}) = padding, "
                    + $"[{JudgmentBuffer.PaddingSeconds}..{JudgmentBuffer.TotalSize}) = draw zone");
    Console.WriteLine($"Focus: {minutes} min ({buf.RemainingTargetSeconds}s)\n");
    Renderer.BufferSummary(buf);

    // 2. 每分钟喂一次合成的 AW 事件（上限: 目标分钟数 + 1h 的 slack + 归档预留）
    // 跑到「目标时长 + 一小时的余量」，或者至少跨过一次归档（2h + 10min）
    var maxElapsed = Math.Max(JudgmentBuffer.DrawSeconds + 600, minutes * 60 + 3600);
    var elapsed = 0;
    var tick = 0;
    var settled = 0;
    while (elapsed < maxElapsed && !buf.IsFocusComplete)
    {
        tick++;
        elapsed += 60;
        var queryEnd = taskStart.AddSeconds(elapsed);
        var queryStart = queryEnd.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);

        var (win, afk) = SyntheticEvents(queryStart, queryEnd, taskStart, pattern);
        var outcome = buf.Tick(queryEnd, win, afk, rules, "bench");
        settled += outcome.SettledSeconds;

        if (outcome.SettledSeconds > 0)
        {
            Console.WriteLine($"\n--- ARCHIVE at tick {tick} (elapsed {elapsed}s = {elapsed / 60}min) ---");
            Console.WriteLine($"  settled += {outcome.SettledSeconds}s  remaining target → {buf.RemainingTargetSeconds}s");
            Console.WriteLine($"  task start → {Renderer.Clock(buf.TaskStart)}");
        }

        if (tick % 5 == 0 || outcome.SettledSeconds > 0 || outcome.Completed)
            Renderer.BufferSummary(buf);
    }

    // 3. 终局
    Console.WriteLine($"\n══════════════════════════════════════════════");
    Console.WriteLine($"  Done. Ticks: {tick}  Elapsed: {elapsed}s ({elapsed / 60}min)");
    Console.WriteLine($"  Settled into during: {settled}s ({settled / 3600.0:F2} hours)");
    Console.WriteLine($"  Focus complete: {buf.IsFocusComplete}");
    Console.WriteLine($"══════════════════════════════════════════════\n");

    return 0;
}

/// <summary>
/// 合成一个 4 分钟查询窗口的 AW 事件，模拟 AW 的返回。
///
/// 三种模式：focused（全在目标应用）、mixed（穿插偷懒和 AFK）、slack（大量偷懒）。
/// 事件切成 10 秒一条，最后 10 秒<b>故意不给</b>——模拟 T3 那 6~12 秒滞后，
/// 看它会不会被判成 AwOffline（应该会，而且下一拍自愈）。
/// </summary>
static (List<AwEvent> Win, List<AwEvent> Afk) SyntheticEvents(
    DateTimeOffset qStart, DateTimeOffset qEnd, DateTimeOffset taskStart, string pattern)
{
    var win = new List<AwEvent>();
    var afk = new List<AwEvent>();
    var n = (int)(qEnd - qStart).TotalSeconds;

    for (var i = 0; i + 10 <= n - 10; i += 10)
    {
        var t = qStart.AddSeconds(i);
        if (t < taskStart) continue;               // 任务开始前不造数据

        var slot = (int)(t - taskStart).TotalSeconds / 10;
        var (app, isAfk) = pattern switch
        {
            "focused" => ("goal", false),
            "slack" => (slot % 3 == 0 ? "goal" : "chrome", false),
            _ => slot % 15 == 3 ? ("goal", true)   // 偶尔起身走开
               : slot % 5 == 0 ? ("chrome", false) // 每 50 秒偷懒一组
               : ("goal", false),
        };

        win.Add(new AwEvent(t, 10, app, $"{app} window", null));
        if (isAfk) afk.Add(new AwEvent(t, 10, null, null, "afk"));
    }
    return (win, afk);
}

// ---------------------------------------------------------------- 杂项

GroupRules LoadRules()
{
    var path = opt.GetValueOrDefault("rules") ?? "rules.json";
    if (!File.Exists(path))
        throw new FileNotFoundException($"Rules file not found at {Path.GetFullPath(path)}. Use --rules to point at one.");
    return GroupRules.Load(path);
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var d = new Dictionary<string, string>();
    for (var i = 1; i < args.Length; i++)
        if (args[i].StartsWith("--") && i + 1 < args.Length)
            d[args[i][2..]] = args[++i];
    return d;
}
