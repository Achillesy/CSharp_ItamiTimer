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
const int IdleNudgeSeconds = 60;
// 刚走完那一分钟里偏离超过这个数才提醒，滤掉通知抢焦点之类的噪音。
const int NudgeFloorSeconds = 5;
const int AwAfkTimeoutSeconds = 180;

try
{
    return cmd switch
    {
        "start" => await StartAsync(),
        "replay" => await ReplayPastAsync(),
        "idle" => Idle(),
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
    Console.Error.WriteLine($"\n出错了：{e.Message}\n");
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
                 ?? throw new ArgumentException("要指定小目标：--group 学习经济学");

    var rules = LoadRules();
    foreach (var g in groups)
        if (!rules.SelectableGroups.Contains(g))
            throw new ArgumentException($"rules.json 里没有启用的小目标「{g}」。可选：{string.Join("、", rules.SelectableGroups)}");

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

    Console.WriteLine($"\nAW: {host}   任务已提交（只在内存里，退出即放弃）");
    Console.WriteLine($"小目标：{string.Join("、", groups)}");
    Console.WriteLine($"专注 {minutes} 分钟，之后休息 {task.RestMinutes} 分钟");
    Console.WriteLine($"开始时刻：{Renderer.Clock(task.StartedAt)}（进位到整分钟）\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine($"节拍 {TickSeconds} 秒；超过 {IdleNudgeSeconds} 秒没动键鼠会催你一下");
    Console.WriteLine("Ctrl+C = 放弃任务（会先给你看账单）\n");

    // Ctrl+C 对应 GUI 的"点关闭"：§9 要求退出前把账摆出来，不能默默丢掉。
    TaskState? last = null;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n\n=== 放弃任务 ===\n");
        if (last is not null) Console.WriteLine(Renderer.Bill(task, last));
        Console.WriteLine("这一轮作废了。\n");
        Environment.Exit(130);
    };

    // ---- §8.3.5 的单循环
    while (true)
    {
        var now = DateTimeOffset.Now;

        // 1/2：键鼠空闲。必须在查 AW 之前——它决定本轮还要不要往下走。
        var idle = InputIdle.Elapsed().TotalSeconds;
        if (idle >= IdleNudgeSeconds)
        {
            // AW 要安静满 180 秒才翻成 afk，且事件起点会回填到最后一次输入（§14.4a T5）。
            // 所以必须赶在那条截止线【之前】把人叫醒——事后再叫是救不回来的。
            Console.WriteLine($"{Renderer.Clock(now, "HH:mm")}  ⚠ {idle:F0} 秒没动键鼠了，动一下——" +
                              $"再过 {Math.Max(0, AwAfkTimeoutSeconds - idle):F0} 秒这段时间就白费了。");
            await Task.Delay(TickSeconds * 1000);
            continue;
        }

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
                          $"{state.FocusedSeconds / 60:F1}/{task.FocusMinutes} 分钟");

        // 用【刚走完的那一格】当触发条件，不是【此刻在干什么】。否则 10:00:10 切走、
        // 10:00:50 切回这种短切换会整个从提醒里溜掉——而它在色块上明明是红的。
        if (cells.Count > 0 && cells[^1].OffTaskSeconds >= NudgeFloorSeconds)
            Console.WriteLine($"       ⚠ 刚过去那一分钟有 {cells[^1].OffTaskSeconds:F0} 秒跑偏了。");

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
    Console.WriteLine($"进入休息 {task.RestMinutes} 分钟。这段时间去哪、干什么都不重要。\n");

    while (DateTimeOffset.Now < restEnds)
    {
        // 每分钟淡掉 100%/休息分钟数（§8.4.4）。不是固定 10%——那样 25 分钟的任务
        // 休息结束时盘上还挂着半个色环，跟「没有色环就是邀请」打架。
        var left = rest > TimeSpan.Zero ? 1 - (DateTimeOffset.Now - completedAt) / rest : 0;
        Console.Write($"\r☕ 休息中，色环还剩 {Math.Max(0, left) * 100:F0}%   ");
        Thread.Sleep(1000);
    }

    Console.WriteLine("\n\n休息结束。要再来一轮就自己再开一次 —— 程序不会替你开始。\n");
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
                 ?? throw new ArgumentException("要指定小目标：--group 学习经济学");
    var since = DateTimeOffset.Parse(opt.GetValueOrDefault("since")
                 ?? throw new ArgumentException("要指定起点：--since \"2026-07-26 20:00\""));
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

    Console.WriteLine($"\n干跑：{Renderer.Clock(task.StartedAt, "MM-dd HH:mm")} → {Renderer.Clock(until, "HH:mm")}   小目标：{string.Join("、", groups)}");
    Console.WriteLine($"窗口事件 {win.Count} 条，afk 事件 {afk.Count} 条\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine(Renderer.Cells(Replay.ToMinuteCells(task, state)));
    Console.WriteLine();
    Console.WriteLine(Renderer.Bill(task, state));
    return 0;
}

/// <summary>盯着键鼠空闲读数看，用来给 §8.3.6 的阈值找一个真实合适的值。</summary>
int Idle()
{
    Console.WriteLine($"\n{IdleNudgeSeconds} 秒催你，{AwAfkTimeoutSeconds} 秒（AW 默认）之后这段时间就白费了。Ctrl+C 退出。\n");
    while (true)
    {
        var s = InputIdle.Elapsed().TotalSeconds;
        var mark = s >= AwAfkTimeoutSeconds ? "已白费" : s >= IdleNudgeSeconds ? "该催了" : "";
        Console.Write($"\r空闲 {s,6:F1} 秒  {mark,-8}");
        Thread.Sleep(500);
    }
}

int Help()
{
    Console.WriteLine("""

        一袋米要扛几楼 —— 命令行原子层

          itami start  --minutes 5 --group 学习经济学     提交任务，跑到结束
          itami replay --since "2026-07-26 20:00" [--until ...] --minutes 25 --group 学习经济学
                                                         拿过去的真实历史干跑
          itami idle                                     盯着键鼠空闲读数看

        任务只活在进程里，不落盘：退出 itami 就等于放弃当前任务（DESIGN.md §2）。
        规则文件默认找 ./rules.json，可用 --rules <路径> 指定。

        """);
    return 0;
}

// ---------------------------------------------------------------- 杂项

GroupRules LoadRules()
{
    var path = opt.GetValueOrDefault("rules") ?? "rules.json";
    if (!File.Exists(path))
        throw new FileNotFoundException($"找不到规则文件 {Path.GetFullPath(path)}，用 --rules 指定路径。");
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
