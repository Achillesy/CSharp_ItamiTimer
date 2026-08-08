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
        "replay" => await ReplayPastAsync(),
        "backfill" => await BackfillAsync(),
        "bench" => Bench(),
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
    var group = opt.GetValueOrDefault("group")
                ?? throw new ArgumentException("A goal is required: --group <name from rules.json>");

    var rules = LoadRules();
    if (!rules.SelectableGroups.Contains(group))
        throw new ArgumentException($"No enabled goal \"{group}\" in rules.json. Available: {string.Join(", ", rules.SelectableGroups)}");

    using var aw = new AwClient();
    var winId = await aw.FindBucketIdAsync(AwClient.WindowBucketType);
    var afkId = await aw.FindBucketIdAsync(AwClient.AfkBucketType);

    // §14.1: truncated to the current whole minute
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

    // Ctrl+C corresponds to the GUI's "click close": §9 requires showing the report before quitting, not silently dropping it.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n\n=== Task abandoned ===\n");
        Console.WriteLine(Renderer.Bill(task, buf, settled, DateTimeOffset.Now, null));
        Console.WriteLine("This round is void.\n");
        Environment.Exit(130);
    };

    // ---- The single loop from §8.3.5. The tick is anchored to a whole minute (§4.2), not to the moment it started.
    while (true)
    {
        var minute = TimeGrid.FloorToMinute(DateTimeOffset.Now);

        // Both ends of the query interval are whole minutes, so the write offset is always an integer (DECISIONS H9)
        var queryStart = minute.AddSeconds(-JudgmentBuffer.QueryWindowSeconds);
        var win = await aw.FetchEventsAsync(winId, queryStart, minute);
        var afk = await aw.FetchEventsAsync(afkId, queryStart, minute);

        var outcome = buf.Tick(minute, win, afk, rules, group);
        settled += outcome.SettledSeconds;
        if (outcome.SettledSeconds > 0)
            Console.WriteLine($"       ⏳ Two hours in — banked {outcome.SettledSeconds / 60} min "
                            + $"and rolled the ring over.");

        var cells = buf.ToMinuteCells();

        // The tick is 60 seconds, so each round gets its own line rather than overwriting
        // in place with \r -- overwriting is designed for the 3-second tick, and mixing it
        // with interleaved warnings would smear together. One line per minute is a
        // readable log as-is.
        Console.WriteLine($"{Renderer.Clock(minute, "HH:mm")}  {Renderer.Cells(cells)}  " +
                          $"{(settled + buf.FocusedSeconds) / 60.0:F1}/{task.FocusMinutes} min");

        // Uses **the minute that just finished** as the trigger condition, not **what's
        // happening right now**. Otherwise a brief switch-away-and-back like 10:00:10 to
        // 10:00:50 would slip through the notification entirely -- even though it's
        // plainly red on the coloured cells.
        var last = cells.LastOrDefault(c => c.FocusSeconds + c.OffTaskSeconds + c.AfkSeconds > 0);
        if (last.OffTaskSeconds >= NudgeFloorSeconds)
            Console.WriteLine($"       ⚠ The minute just past had {last.OffTaskSeconds}s off-task.");

        // Completion is this very tick (§4.5): never derived retroactively from an earlier
        // moment in the ledger, so rest can never be retroactively eaten into (that's
        // exactly how §15.1 happened).
        if (outcome.Completed) return Rest(task, buf, settled, minute);

        await Task.Delay(TickSeconds * 1000);
    }
}

/// <summary>
/// The rest phase (§8.4.4a): **purely local timing, zero ActivityWatch access**.
/// A task's last query to ActivityWatch happens at the exact moment focus is achieved.
/// </summary>
int Rest(TaskRecord task, JudgmentBuffer buf, double settled, DateTimeOffset completedAt)
{
    Console.WriteLine("\n");
    Console.WriteLine(Renderer.Bill(task, buf, settled, completedAt, completedAt));   // The report is shown at the moment of **completion**

    var rest = TimeSpan.FromMinutes(task.RestMinutes);
    var restEnds = completedAt + rest;
    Console.WriteLine($"Break for {task.RestMinutes} min. Where you go and what you do does not matter.\n");

    while (DateTimeOffset.Now < restEnds)
    {
        // Fades by 100%/rest-minutes each minute (§8.4.4). Not a fixed 10% -- that would
        // leave half a ring still hanging around when a 25-minute task's rest ends, which
        // conflicts with "no ring means invitation".
        var left = rest > TimeSpan.Zero ? 1 - (DateTimeOffset.Now - completedAt) / rest : 0;
        Console.Write($"\r☕ On a break — {Math.Max(0, left) * 100:F0}% of the ring left   ");
        Thread.Sleep(1000);
    }

    Console.WriteLine("\n\nBreak over. Start another round yourself — the program never does it for you.\n");
    return 0;
}

/// <summary>
/// Dry-runs real past history, no writing to disk, no waiting.
/// This is the fastest way to check whether your rules are written correctly.
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

    // Runs the live loop unchanged against real history -- **the same engine, the same
    // tick**, just with `now` fed in from outside. This way the report the CLI dry-run
    // produces matches exactly what the real machine would compute (§15.7).
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

/// <summary>
/// 干跑累计时长的回填（DESIGN §11.2）：数出一段历史里属于某个小目标的专注秒数。
///
/// **不写 during.json**——CLI 从来不碰那个文件，这里也一样。它是用来回答两个问题的：
/// 「我这条规则在真实历史上到底能捞到多少」，以及「界面上那个数字凭什么是这个数」。
///
/// 省略 `--since` 就是模拟**首次启动**：从 window bucket 的 `created` 一路数到现在，
/// 跟界面上第一次点 Start 走的是同一条路径。
/// </summary>
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

    DateTimeOffset? since = opt.TryGetValue("since", out var s) ? DateTimeOffset.Parse(s) : null;
    if (since is null)
    {
        since = await aw.FindBucketCreatedAsync(AwClient.WindowBucketType);
        Console.WriteLine(since is null
            ? "No --since given and the window bucket has no creation time; falling back to one year."
            : $"No --since given: walking the whole history, from the window bucket's creation ({since:yyyy-MM-dd HH:mm}).");
        since ??= until.AddYears(-1);
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
            Console.WriteLine($"  through {Renderer.Clock(through, "yyyy-MM-dd HH:mm")}   {running / 3600.0,8:F2} h");
        });

    Console.WriteLine($"\n{group}: {seconds} s = {seconds / 3600.0:F2} hours   ({sw.Elapsed.TotalSeconds:F1}s)\n");
    return 0;
}

/// <summary>
/// `executeCommand` 收藏夹的三个模式（用户 2026-08-08）。默认操作的是**程序真正在用的
/// 那一份 rules.json**（<see cref="AppData.RulesPath"/> 的三级查找链），不是当前目录下的
/// ——其它子命令沿用旧的 `./rules.json` 默认值，这里刻意不同：改命令的人要改的必然是
/// 生效的那一份。
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

          itami start    --minutes 25 --group <goal>
          itami replay   --since "2026-07-26 20:00" [--until ...] --minutes 25 --group <goal>
          itami backfill --group <goal> [--since ...] [--until ...]
          itami bench    --minutes 25 [--pattern focused|mixed|slack]
          itami commands [--list | --select [N] | --execute]

        commands works on executeCommand in the rules.json the app actually uses. The alarm
        always runs entry #0 (marked * in the listing), and re-reads the file when it fires
        — so selecting an entry takes effect immediately, without restarting ItamiTimer.

          --list       just print them, change nothing
          --select N   move entry N to #0   (rewrites rules.json, keeps a .bak)
          --select     same, but print the list and ask for the number
          (no flag)    same as --select
          --execute    run #0 now, after a y/N confirm

        --execute takes no number on purpose: to try a different entry, --select it first.
        That way the entry you tested and the entry the alarm will actually run are the
        same one, by construction. It runs through exactly the same code the alarm uses,
        so "it worked here" actually means something.

        backfill dry-runs the accumulated-time count over real history (fail-closed: only
        what ActivityWatch can actually prove). Omit --since to walk the whole history,
        which is what the GUI does the first time you start that goal. It writes nothing.

        A task lives only in this process and is never written to disk: quitting itami
        abandons the current task.
        The rules file defaults to ./rules.json; override it with --rules <path>.

        """);
    return 0;
}

// ---------------------------------------------------------------- bench

/// <summary>
/// Dry-runs the judgment model (JudgmentBuffer + Judgment) against synthetic data.
/// Doesn't touch ActivityWatch, doesn't write to disk -- purely verifies buffer
/// initialization and state transitions.
/// </summary>
int Bench()
{
    var minutes = int.Parse(opt.GetValueOrDefault("minutes", "25"));
    var pattern = opt.GetValueOrDefault("pattern", "mixed");

    Console.WriteLine($"\n══════════════════════════════════════════════");
    Console.WriteLine($"  Judgment Buffer Bench — {minutes} min focus, pattern: {pattern}");
    Console.WriteLine($"══════════════════════════════════════════════\n");

    // 1. Initialization
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

    // 2. Feed a synthetic ActivityWatch event once a minute (cap: target minutes + 1h of slack + archiving headroom)
    // Runs until "target length + one hour of slack", or at least crosses one archive (2h + 10min)
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

    // 3. Final results
    Console.WriteLine($"\n══════════════════════════════════════════════");
    Console.WriteLine($"  Done. Ticks: {tick}  Elapsed: {elapsed}s ({elapsed / 60}min)");
    Console.WriteLine($"  Archived out of the buffer: {settled}s ({settled / 3600.0:F2} hours)");
    Console.WriteLine($"  Focus complete: {buf.IsFocusComplete}");
    Console.WriteLine($"══════════════════════════════════════════════\n");

    return 0;
}

/// <summary>
/// Synthesizes ActivityWatch events for one 4-minute query window, imitating what
/// ActivityWatch would return.
///
/// Three patterns: focused (entirely on the goal app), mixed (interleaved off-task and
/// AFK stretches), slack (mostly off-task). Events are chopped into 10-second pieces, and
/// the last 10 seconds are <b>deliberately left empty</b> -- simulating T3's 6-12 second
/// lag, to see whether it gets judged AwOffline (it should, and self-heals next tick).
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
        if (t < taskStart) continue;               // Don't fabricate data before the task started

        var slot = (int)(t - taskStart).TotalSeconds / 10;
        var (app, isAfk) = pattern switch
        {
            "focused" => ("goal", false),
            "slack" => (slot % 3 == 0 ? "goal" : "chrome", false),
            _ => slot % 15 == 3 ? ("goal", true)   // Occasionally step away
               : slot % 5 == 0 ? ("chrome", false) // A stretch of off-task every 50 seconds
               : ("goal", false),
        };

        win.Add(new AwEvent(t, 10, app, $"{app} window", null));
        if (isAfk) afk.Add(new AwEvent(t, 10, null, null, "afk"));
    }
    return (win, afk);
}

// ---------------------------------------------------------------- Misc

GroupRules LoadRules()
{
    var path = opt.GetValueOrDefault("rules") ?? "rules.json";
    if (!File.Exists(path))
        throw new FileNotFoundException($"Rules file not found at {Path.GetFullPath(path)}. Use --rules to point at one.");
    return GroupRules.Load(path);
}

/// <summary>
/// `--key value` 成对参数，外加**不带值的布尔开关**（`--list` / `--test`）。
///
/// 开关的判定是"下一个参数不存在、或者它自己也是 `--` 开头"——2026-08-08 加 `commands`
/// 时发现原来的版本会把结尾的 `--list` **整个丢掉**（它要求后面必须跟一个值），于是
/// `itami commands --list` 悄悄退化成了默认的"挪到第一位"模式：**不报错，只是干了另一件事**，
/// 而且是会写文件的那件。现有的 `--minutes 25` 这类调用不受影响，它们的值从不以 `--` 开头。
/// </summary>
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
