using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// 一个小目标的累计专注时间，以及<b>这份累计值算到了哪一刻</b>。
///
/// <code>
/// %LOCALAPPDATA%\ItamiTimer\during.json
/// { "goals": { "Economics": { "seconds": 12345, "recordedThrough": "2026-08-06T09:00:00+08:00" } } }
/// </code>
///
/// <b>⚠️ 1.1.0 起它不再是「账本」，而是一个 checkpoint（§11.2 重写）。</b>
///
/// 1.0.x 的模型是「每一秒专注只入账一次」：归档时结算一小时，任务终结时结算余下那部分，
/// 每次入账立刻写盘。那套机制解决的是「这一秒现在不记下来就永远没了」，代价是三条终结
/// 路径都得记得调、崩溃就丢、还得靠一个 <c>_banked</c> 幂等标志防重复入账。
///
/// 新模型把这个前提整个拆掉了：<b>AW 是地面真相，秒数随时可以重新推导出来</b>
/// （<see cref="ItamiTimer.Core.Backfill"/>）。于是：
///
/// <list type="bullet">
///   <item><b>任务运行期间一秒都不落盘。</b>界面上的数字 = <c>已落盘 + 本轮实时</c>，
///         纯显示，不写。</item>
///   <item><b>只在任务启动那一刻写一次</b>：回填 <c>[recordedThrough, 本次 StartedAt)</c>
///         这一段（覆盖了上一场任务全程 + 中间的空隙），加进 <c>seconds</c>，
///         把 <c>recordedThrough</c> 推到本次 <c>StartedAt</c>。</item>
///   <item><b>推进 checkpoint 这个动作本身就是成功的唯一证明</b>：回填失败（启动时 AW
///         正好不在）就不写，下次启动自然重试同一个窗口。不需要任何重试或恢复逻辑，
///         崩溃安全是白捡的。</item>
/// </list>
///
/// 顺带把 fail-open 的水分自动洗掉了：运行期 <c>AwOffline</c> 计入专注（H2），那份宽松
/// 只喂表盘和完成判定、从来没落过盘；同一段时间下次启动会被 fail-closed 地重新数一遍
/// 才进这个文件。<b>两者不需要对账，因为宽松那次的结果从来没进过账本。</b>
///
/// <b>为什么是独立文件，不并进 settings.json、也不写回 rules.json：</b>
/// <list type="bullet">
///   <item><c>rules.json</c> 是<b>用户手写的</b>。程序一旦写它，用户的注释就全没了——
///         `JsonCommentHandling.Skip` 读的时候就扔了，序列化时没有任何东西可以还原
///         （`JsonNode` 也保不住，注释在建树之前就没了）。这条链是只读的（§8.1）。</item>
///   <item><c>settings.json</c> 程序随时整份重写，而累计时长是<b>唯一一个丢了就补不回来</b>
///         的数据（AW 的保留期之外就真没了）。单独一个文件更好备份、也更好手动清零。</item>
/// </list>
///
/// 读写失败一律吞掉：<b>记不上时间绝不能把程序搞崩。</b>
/// </summary>
public sealed class GoalTime
{
    /// <summary>
    /// 累计专注秒数。<b>整数</b>——数的是 buffer / 回填 span 里的格子数，不是 AW 事件的
    /// <c>duration</c>，所以永远没有小数部分。每一处 <c>/ 60</c> 都得写成 <c>60.0</c>，
    /// 否则整数除法会把小数位悄悄吃掉（DECISIONS G）。
    /// </summary>
    [JsonPropertyName("seconds")]
    public long Seconds { get; set; }

    /// <summary>
    /// <see cref="Seconds"/> 已经算到了哪一刻——下一次回填的左端。
    ///
    /// <b><c>null</c> = 让下次回填去 AW 全部历史里数一遍</b>（DECISIONS I3 的机制）。
    /// ⚠️ **`During.EnsureSeeded` 2026-08-13 起不再自动播出这个值**（DECISIONS I7）：
    /// 自动补种一律填挂钟时间，`null` 变成了纯手动的选项——想让某个目标找回历史，去
    /// during.json 里把这一条手动改成 `null` 再点 Start。
    /// </summary>
    [JsonPropertyName("recordedThrough")]
    public DateTimeOffset? RecordedThrough { get; set; }
}

public sealed class During
{
    [JsonPropertyName("goals")]
    public Dictionary<string, GoalTime> Goals { get; set; } = [];

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "during.json");

    /// <summary>已落盘的累计秒数。没有这条记录就是 0。</summary>
    public long this[string goal] => Goals.TryGetValue(goal, out var g) ? g.Seconds : 0;

    /// <summary>累计值算到了哪一刻；<c>null</c> = 从没统计过，下次回填走完整段历史。</summary>
    public DateTimeOffset? RecordedThrough(string goal)
        => Goals.TryGetValue(goal, out var g) ? g.RecordedThrough : null;

    /// <summary>
    /// 回填成功之后推进 checkpoint，并立刻写盘。<b>这是这个文件唯一的写入点</b>
    /// （<see cref="EnsureSeeded"/> 那次播种除外）。
    ///
    /// <paramref name="through"/> 只进不退：万一同一分钟内连开两次任务，
    /// checkpoint 不会被推回去，那一段也就不会被数第二遍。
    /// </summary>
    public void Advance(string goal, long addSeconds, DateTimeOffset through)
    {
        Apply(goal, addSeconds, through);
        Save();
    }

    /// <summary>
    /// <see cref="Advance"/> 的纯内存部分——不落盘。
    ///
    /// ⚠️ **拆开是因为测试绝不能碰 `%LOCALAPPDATA%`**（DECISIONS I5）：`Save()` 写的是
    /// 用户真实的 during.json，单元测试里调一次 `Advance` 就会把用户的累计时长覆盖成测试
    /// 数据，而且悄无声息——2026-08-06 已经栽过一次，真把用户的账本冲掉了。凡是「纯逻辑 +
    /// 写盘」的方法，一律拆成两层，测试只调纯的那层（同 <see cref="FromLegacy"/> /
    /// <c>Migrate</c>）。
    /// </summary>
    public void Apply(string goal, long addSeconds, DateTimeOffset through)
    {
        if (!Goals.TryGetValue(goal, out var g))
        {
            g = new GoalTime();
            Goals[goal] = g;
        }

        if (addSeconds > 0) g.Seconds += addSeconds;
        if (g.RecordedThrough is null || through > g.RecordedThrough) g.RecordedThrough = through;
    }

    public static During Load()
    {
        // 文件不存在时**不在这里建**——建之前得先知道有哪些小目标，见 EnsureSeeded。
        if (!File.Exists(Path_)) return new During();

        var text = "";
        try
        {
            text = File.ReadAllText(Path_);
            var loaded = JsonSerializer.Deserialize<During>(text);
            // 1.0.x 的文件反序列化成新类型不会抛，只会得到一个空的 goals——所以不能只看
            // 有没有异常，得看有没有真读出东西来。
            if (loaded is { Goals.Count: > 0 }) return loaded;
            return Migrate(text) ?? loaded ?? new During();
        }
        catch (Exception e)
        {
            var migrated = Migrate(text);
            if (migrated is not null) return migrated;

            Log.Error("Failed to read during.json; starting from zero", e);
            return new During();
        }
    }

    /// <summary>
    /// 从 1.0.x 的 <c>{ "accumulatedSeconds": { "X": 12345 } }</c> 迁过来。
    ///
    /// <b>关键在于 <c>recordedThrough</c> 必须填「迁移这一刻」，不能留 null。</b>
    /// 老账本的语义就是「到此为止全部已入账」，留 null 会让下一次回填从 AW 历史的开头
    /// 重数一遍再加上去——同一段时间算两遍，数字直接翻倍。
    ///
    /// 秒数字段在 2026-08-02 从 <c>double</c> 收成过 <c>long</c>，更老的文件里可能还留着
    /// 小数（38253.4），所以这里按 <c>double</c> 读再四舍五入。<b>这是唯一一个丢了就补不
    /// 回来的数据</b>，值得多写一段宽容的兜底，而不是一句 Log.Error 就归零。
    /// </summary>
    private static During? Migrate(string text)
    {
        var migrated = FromLegacy(text, DateTimeOffset.Now);
        if (migrated is null) return null;

        Log.Warn($"during.json migrated from the 1.0.x ledger: {migrated.Goals.Count} goals, " +
                 $"counted through {migrated.Goals.Values.First().RecordedThrough:yyyy-MM-dd HH:mm}");
        migrated.Save();
        return migrated;
    }

    /// <summary>
    /// 迁移的纯函数部分（不落盘、不读时钟）——<see cref="Migrate"/> 负责写盘和日志。
    /// 拆开是为了能测：<paramref name="now"/> 漏填成 null 这件事必须有护栏。
    /// 返回 null = 这不是一份 1.0.x 的文件。
    /// </summary>
    public static During? FromLegacy(string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var loose = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(text);
            if (loose is null || !loose.TryGetValue("accumulatedSeconds", out var old) || old is not { Count: > 0 })
                return null;

            return new During
            {
                Goals = old.ToDictionary(
                    kv => kv.Key,
                    kv => new GoalTime
                    {
                        Seconds = (long)Math.Round(kv.Value),
                        RecordedThrough = now,
                    }),
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 每次启动都跑一遍：rules.json 里有、`Goals` 里还没有的目标名，各补种一条
    /// <c>(0, 补种这一刻)</c>。**这是这个文件除 <see cref="Advance"/> 之外唯一的写入点**。
    ///
    /// <b>2026-08-13 用户推翻 I3（DECISIONS I7）：种子不再是 <c>null</c>，是 <c>DateTimeOffset.Now</c>。</b>
    /// 旧版只在文件从没存在过的那一次播种，且故意留 <c>null</c>——那意味着这个目标第一次
    /// 点 Start 时会去 AW 全部历史里回填，新目标不丢掉之前已经做过的部分。用户现在要的是
    /// 反过来：**新目标默认不背历史包袱，只从被发现这一刻算起**——`"编程"` 这种后来才加进
    /// rules.json 的目标，不该因为之前用 Claude Code / VS Code 干过别的事，一点 Start
    /// 就平白多出几十小时。
    ///
    /// <c>null</c> 这个哨兵**没有被删掉，只是不再是自动路径**：<see cref="MainWindow"/> 的
    /// `BackfillAsync` 见到 `null` 仍然会走全历史回填（DECISIONS I3 的机制原样保留）。想让
    /// 某个目标找回历史，手动把 during.json 里那一条的 `recordedThrough` 改成 `null`（或者
    /// 改成任意更早的日期）再点 Start 就行——跟 rules.json 一样，这个文件也是可以手改的，
    /// 不需要专门的界面。**同一套逻辑也是清零的办法**：把某个目标的整条记录从 during.json
    /// 里删掉，重启程序，它会被当成"没见过的目标"重新补种成 <c>(0, 重启这一刻)</c>——不需要
    /// 另写一个"重置"功能。
    ///
    /// 不查 AW 的 bucket 创建时间（`created`）来定种子——那是 I3 权衡过、专门否掉的路：
    /// ① 是派生值，会随 AW 重装 / 迁移漂移；② 播种发生在程序启动路径上，那一刻 AW 可能
    /// 还没连上，查询会失败或者拖慢启动。直接用挂钟时间 `Now` 完全不需要问 AW，这两条顾虑
    /// 天然不存在。
    ///
    /// **只补种缺失的，不覆盖已有的**——已经有真实累计值的目标不会被这里碰到；已经改过名字
    /// 的旧条目也不会被这里清理，状态文件依然不镜像配置文件，只是"发现新目标"这一动作从
    /// "仅一次"变成了"每次启动都查一遍差集"。
    /// </summary>
    public void EnsureSeeded(IReadOnlyList<string> goals)
    {
        if (ApplySeed(goals, DateTimeOffset.Now)) Save();
    }

    /// <summary>
    /// <see cref="EnsureSeeded"/> 的纯内存部分——不落盘（同 <see cref="Apply"/> /
    /// <see cref="FromLegacy"/> 那条拆分规矩，DECISIONS I5）。返回是否真的新种了点什么，
    /// 让调用方决定要不要写盘。
    /// </summary>
    public bool ApplySeed(IReadOnlyList<string> goals, DateTimeOffset now)
    {
        var added = false;
        foreach (var g in goals)
        {
            if (Goals.ContainsKey(g)) continue;
            Goals[g] = new GoalTime { RecordedThrough = now };
            added = true;
        }
        return added;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, AppData.JsonOptions));
        }
        catch (Exception e)
        {
            Log.Error("Failed to write during.json; this checkpoint lives only in memory", e);
        }
    }
}
