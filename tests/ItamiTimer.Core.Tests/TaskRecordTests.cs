using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class TaskRecordTests
{
    private static TaskRecord WithFocus(int minutes) => new()
    {
        StartedAt = new DateTimeOffset(2026, 7, 27, 10, 9, 0, TimeSpan.FromHours(8)),
        FocusMinutes = minutes,
        Groups = ["学习经济学"],
    };

    /// <summary>
    /// §8.4.2a：滑块步进 5 保证休息时长恒为整数分钟，不需要任何取整规则。
    /// 这条测试就是那个论证的守卫——哪天有人把步进改成 3，它会失败。
    /// </summary>
    [Theory]
    [InlineData(10, 2)]
    [InlineData(15, 3)]
    [InlineData(25, 5)]
    [InlineData(50, 10)]
    public void 休息时长是专注的五分之一_滑块的九个档都是整数分钟(int focus, int expectedRest)
    {
        Assert.Equal(expectedRest, WithFocus(focus).RestMinutes);
    }

    /// <summary>
    /// §8.4.2a：范围约束属于 UI 层，Core 必须接受任意时长——否则 §13 的手动
    /// 验证每跑一次要枯坐 10 分钟。1 分钟任务算出 0 分钟休息，专注达成即
    /// Completed，测试时正好方便。
    /// </summary>
    [Fact]
    public void Core_接受滑块范围之外的短时长_用于验证()
    {
        Assert.Equal(0, WithFocus(1).RestMinutes);
    }

    [Fact]
    public void 新任务默认是已提交且没有变更记录()
    {
        var task = WithFocus(25);
        Assert.Equal(RecordStatus.Committed, task.Status);
        Assert.Empty(task.GroupChanges);
        Assert.Null(task.AbandonedAt);
    }
}
