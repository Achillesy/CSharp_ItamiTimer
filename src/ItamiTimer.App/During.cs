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
    [JsonPropertyName("accumulatedSeconds")]
    public Dictionary<string, double> AccumulatedSeconds { get; set; } = [];

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "during.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public double this[string goal] => AccumulatedSeconds.GetValueOrDefault(goal, 0);

    /// <summary>把一段专注时间记到某个小目标名下，并立刻落盘。</summary>
    /// <remarks>
    /// <b>每次入账就写一次盘</b>，不等退出（退出还有崩溃、关机、进程被杀三条路走不到）。
    /// 一轮任务最多写两三次，成本可以忽略。
    /// </remarks>
    public void Add(string goal, double seconds)
    {
        if (seconds <= 0) return;
        AccumulatedSeconds[goal] = this[goal] + seconds;
        Save();
    }

    public static During Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new During();
            return JsonSerializer.Deserialize<During>(File.ReadAllText(Path_)) ?? new During();
        }
        catch (Exception e)
        {
            // 读不出来就从零开始。绝不能因为一个统计文件让程序打不开。
            Log.Error("Failed to read during.json; starting from zero", e);
            return new During();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Error("Failed to write during.json; this round lives only in memory", e);
        }
    }
}
