using ItamiTimer;
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
        "bench" => Bench(),
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

int Help()
{
    Console.WriteLine("""

        ItamiTimer (一袋米要扛几楼) — command-line layer

          itami start  --minutes 25 --group <goal>
          itami replay --since "2026-07-26 20:00" [--until ...] --minutes 25 --group <goal>
          itami bench  --minutes 25 [--pattern focused|mixed|slack]

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
    Console.WriteLine($"  Settled into during: {settled}s ({settled / 3600.0:F2} hours)");
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

static Dictionary<string, string> ParseOptions(string[] args)
{
    var d = new Dictionary<string, string>();
    for (var i = 1; i < args.Length; i++)
        if (args[i].StartsWith("--") && i + 1 < args.Length)
            d[args[i][2..]] = args[++i];
    return d;
}
