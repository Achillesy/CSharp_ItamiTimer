using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// 每个小目标上累计花掉的专注秒数（DESIGN.md §11.2，用户 2026-08-02 定）。
///
/// <code>
/// %LOCALAPPDATA%\ItamiTimer\during.json
/// { "accumulatedSeconds": { "学习经济学": 12345.0 } }
/// </code>
///
/// <b>为什么单独一个文件，不并进 settings.json，更不写回 rules.json：</b>
///
/// <list type="bullet">
///   <item><c>rules.json</c> 是<b>用户手写</b>的。程序一旦写它，那十几行中文注释就全没了
///         ——`JsonCommentHandling.Skip` 在读的时候直接把注释扔了，序列化时没有任何东西
///         可以还原（`JsonNode` 也保不住，注释在建树之前就没了）。这条链子只读不写（§8.1）。</item>
///   <item><c>settings.json</c> 程序随时整份重写，而累计时长是<b>唯一一个丢了就补不回来</b>
///         的数据。单独一个文件更好备份，也更好手动清零。</item>
/// </list>
///
/// <b>累加点只有一个原则：每一秒专注时间只入账一次。</b>
/// 归档（§4.4）时入账即将被移出 buffer 的那一小时，任务终结时入账 buffer 里剩下的那部分。
/// 「终结」包括三件事，用户把它们统称为 ignore：点 Give up、任务运行中关掉程序、
/// 以及 2 小时那次归档塌缩——它本来就等价于「1 小时前放弃、又立刻重开」。
///
/// 读写失败一律吞掉：<b>记不了账绝不能把程序搞挂。</b>
/// </summary>
public sealed class During
{
    /// <summary>
    /// 每个小目标的累计专注秒数。**整数**——统计的是 buffer 里的格子数，
    /// 不是 AW 事件的 duration，永远不会有小数（用户 2026-08-02）。
    /// </summary>
    [JsonPropertyName("accumulatedSeconds")]
    public Dictionary<string, long> AccumulatedSeconds { get; set; } = [];

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "during.json");


    public long this[string goal] => AccumulatedSeconds.GetValueOrDefault(goal, 0);

    /// <summary>把一段专注时间记到某个小目标名下，并立刻落盘。</summary>
    /// <remarks>
    /// <b>每次入账就写一次盘</b>，不等退出（退出还有崩溃、关机、进程被杀三条路走不到）。
    /// 一轮任务最多写两三次，成本可以忽略。
    /// </remarks>
    public void Add(string goal, long seconds)
    {
        if (seconds <= 0) return;
        AccumulatedSeconds[goal] = this[goal] + seconds;
        Save();
    }

    public static During Load()
    {
        // 文件不存在时**不在这里建**——建之前要先知道有哪些小目标（见 EnsureSeeded）。
        if (!File.Exists(Path_)) return new During();

        var text = "";
        try
        {
            text = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<During>(text) ?? new During();
        }
        catch (Exception e)
        {
            // 秒数 2026-08-02 从 double 改成 long。老文件里可能还留着小数（38253.4），
            // 那样直接反序列化会抛。**这是唯一一个丢了就补不回来的数据**，
            // 值得为它多写一个宽容的回退，而不是一句 Log.Error 就归零。
            try
            {
                var loose = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(text);
                if (loose?.TryGetValue("accumulatedSeconds", out var d) == true)
                {
                    var recovered = new During
                    {
                        AccumulatedSeconds = d.ToDictionary(kv => kv.Key, kv => (long)Math.Round(kv.Value)),
                    };
                    Log.Warn($"during.json had fractional seconds; rounded {recovered.AccumulatedSeconds.Count} entries to whole seconds");
                    recovered.Save();
                    return recovered;
                }
            }
            catch { /* 回退也失败，走下面归零 */ }

            Log.Error("Failed to read during.json; starting from zero", e);
            return new During();
        }
    }

    /// <summary>
    /// 文件还不存在时，按当前的小目标名建一份**全 0** 的（用户 2026-08-02）。
    ///
    /// 界面上那个数字没有单位、不解释（D6），用户只能去数据目录里翻。翻到一个 <c>{}</c>
    /// 什么也学不到，翻到 <c>{ "番茄钟": 0 }</c> 就一眼明白这表是按小目标名索引的秒数。
    /// 跟 rules.json 「给例子不给注释」是同一条思路。
    ///
    /// **只在创建那一次播种，之后绝不再同步。** 状态文件不该去镜像配置文件：
    /// 后来改了目标名，这儿留一条对不上的零就留着——它不显示、不参与任何判定，
    /// 而为了清掉它去跟 rules.json 对账，才是真的把两个文件绑死。
    /// </summary>
    public void EnsureSeeded(IReadOnlyList<string> goals)
    {
        if (File.Exists(Path_)) return;
        foreach (var g in goals) AccumulatedSeconds[g] = 0;
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
            Log.Error("Failed to write during.json; this round lives only in memory", e);
        }
    }
}
