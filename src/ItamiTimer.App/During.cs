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
    /// <b><c>null</c> = 从没被统计过</b>，首次启动时会从 AW 历史的开头一路数过来
    /// （DECISIONS I3）。这个哨兵值是有意留成 null 而不是在播种时填一个具体日期的：
    /// 播种发生在程序启动路径上，那一刻 AW 可能连不上，「已播种但起点未知」这个状态
    /// 无论如何都省不掉。一个状态能表达的事，不用两个。
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
    /// 文件还不存在时，按当前的小目标名播一份全 0 的（用户 2026-08-02）。
    ///
    /// 界面上那个数字没有单位、不解释（D6），用户只能去数据目录里翻。翻到 <c>{}</c>
    /// 什么也学不到，翻到 <c>{ "Economics": { "seconds": 0, "recordedThrough": null } }</c>
    /// 一眼就明白这表是什么——跟 rules.json「给例子不给注释」同一条思路，而且顺带把
    /// <c>recordedThrough: null</c> 这个哨兵摆在明面上。
    ///
    /// <b>只在创建那一次播种，之后绝不再同步。</b>状态文件不该去镜像配置文件：后来改了
    /// 目标名，这儿留一条对不上的零就留着。反正没播种过的名字走
    /// <see cref="this[string]"/> / <see cref="RecordedThrough"/> 得到的也是同一对
    /// <c>(0, null)</c>，行为完全一致。
    /// </summary>
    public void EnsureSeeded(IReadOnlyList<string> goals)
    {
        if (File.Exists(Path_)) return;
        foreach (var g in goals) Goals[g] = new GoalTime();
        Save();
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
