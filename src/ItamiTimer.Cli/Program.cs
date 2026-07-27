using ItamiTimer.Cli;
using ItamiTimer.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cmd = args.Length > 0 ? args[0] : "help";
var opt = ParseOptions(args);

try
{
    return cmd switch
    {
        "start" => await StartAsync(),
        "watch" => await WatchAsync(),
        "status" => await StatusAsync(),
        "replay" => await ReplayPastAsync(),
        "stop" => Stop(),
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
    await aw.FindBucketIdAsync(AwClient.WindowBucketType);
    await aw.FindBucketIdAsync(AwClient.AfkBucketType);   // 缺 afk 同样拒绝，不降级（§6.1.1）

    var store = new TaskStore();
    if (store.LoadCurrent() is { Status: RecordStatus.Committed })
        throw new InvalidOperationException("已经有一个进行中的任务。先 `itami stop` 放弃它。");

    // §14.1：进位到下一个整分钟。绝不向后取整——那会把点击「开始」之前的时间也算进来。
    var task = new TaskRecord
    {
        StartedAt = TimeGrid.CeilToMinute(DateTimeOffset.Now),
        FocusMinutes = minutes,
        Groups = groups,
    };
    store.SaveCurrent(task);

    Console.WriteLine($"\nAW: {host}   任务已提交");
    Console.WriteLine($"小目标：{string.Join("、", groups)}");
    Console.WriteLine($"专注 {minutes} 分钟，之后休息 {task.RestMinutes} 分钟");
    Console.WriteLine($"开始时刻：{task.StartedAt:HH:mm:ss}（进位到整分钟）\n");
    return await WatchAsync();
}

async Task<int> WatchAsync()
{
    var store = new TaskStore();
    var task = store.LoadCurrent() ?? throw new InvalidOperationException("没有进行中的任务。先 `itami start`。");
    var rules = LoadRules();
    using var aw = new AwClient();

    var winId = await aw.FindBucketIdAsync(AwClient.WindowBucketType);
    var afkId = await aw.FindBucketIdAsync(AwClient.AfkBucketType);

    Console.WriteLine(Renderer.Legend());
    Console.WriteLine("Ctrl+C 退出监视（任务不受影响——关掉界面不影响计时，原则 3）\n");

    while (true)
    {
        var now = DateTimeOffset.Now;
        var win = await aw.FetchEventsAsync(winId, task.StartedAt, now);
        var afk = await aw.FetchEventsAsync(afkId, task.StartedAt, now);
        var state = Replay.Run(task, rules, win, afk, now);
        var cells = Replay.ToMinuteCells(task, state);

        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
        Console.Write($"{Renderer.Cells(cells)}  {Renderer.PhaseText(state.Phase)}  " +
                      $"{state.FocusedSeconds / 60:F1}/{task.FocusMinutes} 分钟");

        if (state.Phase == TaskPhase.Completed)
        {
            Console.WriteLine("\n");
            Console.WriteLine(Renderer.Bill(task, state));
            store.Archive(task with { Status = RecordStatus.Completed });
            Console.WriteLine("休息结束。要再来一轮就自己再点一次 —— 程序不会替你开始。\n");
            return 0;
        }

        // §8.3.5 / §14.3：3 秒是**提醒**的节奏，不是核算精度。哪怕 60 秒查一次，
        // 最终算出的专注时长依然精确到 AW 自己的事件粒度（§2）。
        await Task.Delay(3000);
    }
}

async Task<int> StatusAsync()
{
    var store = new TaskStore();
    var task = store.LoadCurrent();
    if (task is null) { Console.WriteLine("没有进行中的任务。"); return 0; }

    var rules = LoadRules();
    using var aw = new AwClient();
    var now = DateTimeOffset.Now;
    var win = await aw.FetchEventsAsync(await aw.FindBucketIdAsync(AwClient.WindowBucketType), task.StartedAt, now);
    var afk = await aw.FetchEventsAsync(await aw.FindBucketIdAsync(AwClient.AfkBucketType), task.StartedAt, now);
    var state = Replay.Run(task, rules, win, afk, now);

    Console.WriteLine();
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine(Renderer.Cells(Replay.ToMinuteCells(task, state)));
    Console.WriteLine();
    Console.WriteLine(Renderer.Bill(task, state));
    return 0;
}

/// <summary>
/// 拿过去的真实历史干跑一遍，不写盘、不需要等。
/// 这是验证规则写得对不对最快的办法 —— §0.1 那类决定就该这么定。
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

    Console.WriteLine($"\n干跑：{task.StartedAt:MM-dd HH:mm} → {until:HH:mm}   小目标：{string.Join("、", groups)}");
    Console.WriteLine($"窗口事件 {win.Count} 条，afk 事件 {afk.Count} 条\n");
    Console.WriteLine(Renderer.Legend());
    Console.WriteLine(Renderer.Cells(Replay.ToMinuteCells(task, state)));
    Console.WriteLine();
    if (state.FocusCompletedAt is { } done)
        Console.WriteLine($"专注达成于 {done:HH:mm:ss}\n");
    Console.WriteLine(Renderer.Bill(task, state));
    return 0;
}

int Stop()
{
    var store = new TaskStore();
    var task = store.LoadCurrent();
    if (task is null) { Console.WriteLine("没有进行中的任务。"); return 0; }
    store.Archive(task with { Status = RecordStatus.Abandoned, AbandonedAt = DateTimeOffset.Now });
    Console.WriteLine("已放弃当前任务。");
    return 0;
}

int Help()
{
    Console.WriteLine("""

        一袋米要扛几楼 —— 命令行原子层

          itami start  --minutes 5 --group 学习经济学     提交任务并开始监视
          itami watch                                    继续监视进行中的任务
          itami status                                   看一眼当前进度和账单
          itami stop                                     放弃当前任务
          itami replay --since "2026-07-26 20:00" [--until ...] --minutes 25 --group 学习经济学
                                                         拿过去的真实历史干跑，不写盘

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
