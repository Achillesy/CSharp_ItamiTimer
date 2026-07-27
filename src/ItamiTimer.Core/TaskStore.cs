using System.Text.Json;

namespace ItamiTimer.Core;

/// <summary>
/// 当前任务的存放（DESIGN.md §8 模块 5）。
///
/// **只在三个时刻写盘：提交任务、改勾选、任务终结**（§8.1）。轮询过程中什么都不写——
/// 没有可变累加值需要落盘。这带来两个好处：写入极少、天然抗崩溃。
/// </summary>
public sealed class TaskStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文组名不要转成 \uXXXX
        // 状态写成 "Committed" 而不是 0 —— 这个文件用户会去看，甚至会手改
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>history.jsonl 一行一条，所以归档时不能缩进。</summary>
    private static readonly JsonSerializerOptions CompactOpts = new(Opts) { WriteIndented = false };

    public string Directory { get; } = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ItamiTimer");

    public string CurrentPath => Path.Combine(Directory, "current-task.json");
    public string HistoryPath => Path.Combine(Directory, "history.jsonl");

    public TaskRecord? LoadCurrent()
        => File.Exists(CurrentPath)
            ? JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(CurrentPath), Opts)
            : null;

    public void SaveCurrent(TaskRecord task)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(CurrentPath, JsonSerializer.Serialize(task, Opts));
    }

    /// <summary>任务终结：追加进 history.jsonl 并清掉当前任务。</summary>
    public void Archive(TaskRecord task)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.AppendAllText(HistoryPath, JsonSerializer.Serialize(task, CompactOpts) + "\n");
        if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
    }
}
