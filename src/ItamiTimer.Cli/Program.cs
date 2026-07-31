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
    var groups = opt.GetValueOrDefault("group")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                 ?? throw new ArgumentException("A goal is required: --group <name from rules.json>");

    var rules = LoadRules();
    foreach (var g in groups)
        if (!rules.SelectableGroups.Contains(g))
            throw new ArgumentException($"No enabled goal \"{g}\" in rules.json. Available: {string.Join(", ", rules.SelectableGroups)}");

    // §6.2：提交任务时 AW 不可达 → 拒绝提交。不允许开始一个从一开始就没法核算的任务。
    using var aw = new AwClient();
    var host = await aw.ProbeAsync();
    var winId = await aw.FindBucketIdAsync(AwClient.WindowBucketType);
    var afkId = await aw.FindBucketIdAsync(AwClient.AfkBucketType);   // 缺 afk 同样拒绝，不降级（§6.1.1）

    // §14.1（2026-07-27 改）：**截断**到当前这个整分钟，不是进位。
    var task = new TaskRecord
    {
        StartedAt = TimeGrid.FloorToMinute(DateTimeOffset.Now),
        FocusMinutes = minutes,
        Groups = groups,
    };

    Console.WriteLine($"\nAW: {host}   task submitted (in memory only — quitting abandons it)");
    Console.WriteLine($"Goals: {string.Join(", ", groups)}");
    Console.WriteLine($"Focus {minutes} min, then a {task.RestMinutes} min break");
    Console.WriteLine($"Started at {Renderer.Clock(task.StartedAt)} (floored to the minute)\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine($"Tick every {TickSeconds}s");
    Console.WriteLine("Ctrl+C = abandon the task (you get the tally first)\n");

    // Ctrl+C 对应 GUI 的"点关闭"：§9 要求退出前把账摆出来，不能默默丢掉。
    TaskState? last = null;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n\n=== Task abandoned ===\n");
        if (last is not null) Console.WriteLine(Renderer.Bill(task, last));
        Console.WriteLine("This round is void.\n");
        Environment.Exit(130);
    };

    // ---- §8.3.5 的单循环
    while (true)
    {
        var now = DateTimeOffset.Now;

        // 3：查 AW、重放
        var win = await aw.FetchEventsAsync(winId, task.StartedAt, now);
        var afk = await aw.FetchEventsAsync(afkId, task.StartedAt, now);
        var state = Replay.Run(task, rules, win, afk, now);
        last = state;
        var cells = Replay.ToMinuteCells(task, state);

        // 节拍是 60 秒，所以每轮单独打一行，不用 \r 原地覆盖——覆盖是为 3 秒节拍设计
        // 的，跟穿插的警告混在一起会糊成一团。一分钟一行正好是一份可读的日志。
        Console.WriteLine($"{Renderer.Clock(now, "HH:mm")}  {Renderer.Cells(cells)}  " +
                          $"{Renderer.PhaseText(state.Phase)}  " +
                          $"{state.FocusedSeconds / 60:F1}/{task.FocusMinutes} min");

        // 用【刚走完的那一格】当触发条件，不是【此刻在干什么】。否则 10:00:10 切走、
        // 10:00:50 切回这种短切换会整个从提醒里溜掉——而它在色块上明明是红的。
        if (cells.Count > 0 && cells[^1].OffTaskSeconds >= NudgeFloorSeconds)
            Console.WriteLine($"       ⚠ The minute just past had {cells[^1].OffTaskSeconds:F0}s off-task.");

        if (state.FocusCompletedAt is { } done) return Rest(task, state, done);

        await Task.Delay(TickSeconds * 1000);
    }
}

/// <summary>
/// 休息阶段（§8.4.4a）：**纯本地计时，零 AW 访问**。
/// 一个任务对 AW 的最后一次查询就发生在专注达成那一刻。
/// </summary>
int Rest(TaskRecord task, TaskState state, DateTimeOffset completedAt)
{
    Console.WriteLine("\n");
    Console.WriteLine(Renderer.Bill(task, state));   // 账单在【达成】这一刻给，不在休息结束时给

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
    var groups = opt.GetValueOrDefault("group")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
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
        StartedAt = TimeGrid.CeilToMinute(since),
        FocusMinutes = minutes,
        Groups = groups,
    };
    var state = Replay.Run(task, rules, win, afk, until);

    Console.WriteLine($"\nDry run: {Renderer.Clock(task.StartedAt, "MM-dd HH:mm")} → {Renderer.Clock(until, "HH:mm")}   goals: {string.Join(", ", groups)}");
    Console.WriteLine($"{win.Count} window events, {afk.Count} afk events\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine(Renderer.Cells(Replay.ToMinuteCells(task, state)));
    Console.WriteLine();
    Console.WriteLine(Renderer.Bill(task, state));
    return 0;
}

int Help()
{
    Console.WriteLine("""

        ItamiTimer (一袋米要扛几楼) — command-line layer

          itami start  --minutes 25 --group <goal>       submit a task and run it to the end
          itami replay --since "2026-07-26 20:00" [--until ...] --minutes 25 --group <goal>
                                                        dry-run over real past history
          itami bench  --minutes 25 [--pattern focused|mixed|slack]
                                                        test the new judgment buffer with synthetic data

        <goal> is a group name from rules.json.

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

    Console.WriteLine($"Task start: {Renderer.Clock(taskStart)}");
    Console.WriteLine($"WallClock:  {Renderer.Clock(buf.WallClock)}  (buffer[0])");
    Console.WriteLine($"Buffer[0..180) = padding, [180..7380) = draw zone");
    Console.WriteLine($"Focus: {minutes} min ({buf.FocusSeconds}s)\n");
    Renderer.BufferSummary(buf);

    // 2. 每分钟喂数据（上限: 目标分钟数 + 1h 的 slack + 归档预留）
    var maxElapsed = Math.Max(7800, minutes * 60 + 3600);
    var elapsed = 0;
    var tick = 0;
    while (elapsed < maxElapsed && !buf.IsFocusComplete)
    {
        tick++;
        elapsed += 60;
        var queryNow = taskStart.AddSeconds(elapsed);

        // 合成 240 秒的分类数据
        var queryStart = queryNow.AddSeconds(-240);
        var classified = SyntheticClassify(queryStart, queryNow, taskStart, pattern, tick);
        var bufferOffset = (int)(queryStart - buf.WallClock).TotalSeconds;
        buf.Write(bufferOffset, classified);

        // 检查归档
        var archived = buf.TryArchive();
        if (archived)
        {
            Console.WriteLine($"\n--- ARCHIVE at tick {tick} (elapsed {elapsed}s = {elapsed / 60}min) ---");
            Console.WriteLine($"  during += {buf.DuringSeconds:F0}s, StartedAt → {Renderer.Clock(taskStart.AddSeconds(buf.ArchivedSeconds))}");
        }

        // 每 5 分钟打印一次状态
        if (tick % 5 == 0 || archived || buf.IsFocusComplete)
            Renderer.BufferSummary(buf);
    }

    // 3. 终局
    Console.WriteLine($"\n══════════════════════════════════════════════");
    Console.WriteLine($"  Done. Ticks: {tick}  Elapsed: {elapsed}s ({elapsed / 60}min)");
    Console.WriteLine($"  During: {buf.DuringSeconds:F0}s ({buf.DuringSeconds / 3600:F2} hours)");
    Console.WriteLine($"  Focus complete: {buf.IsFocusComplete}");
    Console.WriteLine($"══════════════════════════════════════════════\n");

    return 0;
}

/// <summary>
/// 合成 240 秒的分类数据，模拟 AW 查询的返回结果。
/// 三种模式：focused（全绿）、mixed（80% 专注穿插偷懒）、slack（大量偷懒）。
/// </summary>
static byte[] SyntheticClassify(DateTimeOffset qStart, DateTimeOffset qEnd, DateTimeOffset taskStart, string pattern, int tick)
{
    var n = (int)(qEnd - qStart).TotalSeconds;
    var result = new byte[n];

    for (var i = 0; i < n; i++)
    {
        var t = qStart.AddSeconds(i);
        if (t < taskStart)
        {
            // 任务开始前：保持当前值（Init 或已覆盖）
            result[i] = JudgmentBuffer.Init;
            continue;
        }

        result[i] = pattern switch
        {
            "focused" => JudgmentBuffer.Focused,
            "slack" => (i / 10) % 3 == 0 ? JudgmentBuffer.Focused : JudgmentBuffer.OffTask,
            _ => // mixed: 80% focused, occasional off-task bursts
                (i / 30) % 5 == 0 ? JudgmentBuffer.OffTask      // 每 30 秒偷懒一组
                : (i / 60) % 7 == 3 ? JudgmentBuffer.Afk         // 偶尔 AFK
                : JudgmentBuffer.Focused,
        };
    }

    return result;
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
