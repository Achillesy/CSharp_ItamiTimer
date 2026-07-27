using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class TaskStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ItamiTimerTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static TaskRecord Sample => new()
    {
        StartedAt = new DateTimeOffset(2026, 7, 27, 12, 45, 0, TimeSpan.FromHours(8)),
        FocusMinutes = 25,
        Groups = ["学习经济学"],
    };

    [Fact]
    public void 存了再读回来要一模一样()
    {
        var store = new TaskStore(_dir);
        store.SaveCurrent(Sample);
        var back = store.LoadCurrent();

        Assert.NotNull(back);
        Assert.Equal(Sample.StartedAt, back.StartedAt);
        Assert.Equal(25, back.FocusMinutes);
        Assert.Equal(["学习经济学"], back.Groups);
        Assert.Equal(RecordStatus.Committed, back.Status);
    }

    /// <summary>这个文件用户会去看甚至手改，所以状态要写成人话、中文组名不能变成 \uXXXX。</summary>
    [Fact]
    public void 落盘的JSON要能给人看()
    {
        var store = new TaskStore(_dir);
        store.SaveCurrent(Sample);
        var json = File.ReadAllText(store.CurrentPath);

        Assert.Contains("\"Committed\"", json);      // 不是 0
        Assert.Contains("学习经济学", json);          // 不是 学...
        Assert.DoesNotContain("RestMinutes", json);  // 推导值不落盘
    }

    [Fact]
    public void 归档之后当前任务就没了_历史里多一行()
    {
        var store = new TaskStore(_dir);
        store.SaveCurrent(Sample);
        store.Archive(Sample with { Status = RecordStatus.Completed });

        Assert.Null(store.LoadCurrent());
        Assert.Single(File.ReadAllLines(store.HistoryPath));
    }
}
