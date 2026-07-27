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
    /// 验证每跑一次要枯坐 10 分钟。
    /// </summary>
    [Fact]
    public void Core_接受滑块范围之外的短时长_用于验证()
    {
        Assert.Equal(1, WithFocus(1).RestMinutes);
    }

    /// <summary>
    /// 休息是**向上**取整（用户 2026-07-28）。原来是整除，于是 FocusMinutes ≤ 4
    /// 全部算出 0 分钟休息 —— 调试量程（2~10）下最常用的那几档根本没有休息阶段，
    /// 休息扇形（§8.4.4）就永远看不见。方向也说得通：休息是奖励，零头该给用户。
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(11, 3)]
    public void 不满五分钟的零头也给一分钟休息_绝不算出零(int focus, int rest)
    {
        Assert.Equal(rest, WithFocus(focus).RestMinutes);
        Assert.True(WithFocus(focus).RestMinutes >= 1, "任何非零时长的任务都该有休息");
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
