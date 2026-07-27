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
    /// §8.4.2：休息 = ⌊专注 ÷ 5⌋ + 1。滑块那九个档都是 5 的倍数，
    /// 所以就是"五分之一，再多一分钟"。
    /// </summary>
    [Theory]
    [InlineData(10, 3)]
    [InlineData(15, 4)]
    [InlineData(25, 6)]
    [InlineData(50, 11)]
    public void 休息时长是五分之一再加一分钟(int focus, int expectedRest)
    {
        Assert.Equal(expectedRest, WithFocus(focus).RestMinutes);
    }

    /// <summary>
    /// 那个 +1 的本职：补上"发现延迟"（§14.0a）。
    ///
    /// 专注在某个真实时刻攒够，但程序要到下一个整分钟的计时点才发现；休息却是从
    /// **真正达成那一刻**起算的。延迟被计时点间隔封死在 60 秒以内，所以补 1 分钟
    /// 之后，**用户实际能歇的时间永不少于名义的五分之一**。
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    public void 最坏的发现延迟也吃不掉名义休息(int focus)
    {
        var task = WithFocus(focus);
        const double worstDelayMinutes = 1.0;      // 计时点间隔 = 延迟上界
        var nominal = focus / 5.0;

        Assert.True(task.RestMinutes - worstDelayMinutes >= nominal,
            $"专注 {focus} 分钟：休息 {task.RestMinutes} 分钟减掉最坏延迟之后" +
            $"只剩 {task.RestMinutes - worstDelayMinutes}，不够名义的 {nominal}");
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
    /// +1 的第二个作用：**任何非零时长都有休息**。原来用整除，FocusMinutes ≤ 4
    /// 全部算出 0 分钟 —— 休息阶段整个不存在，休息扇形（§8.4.4）永远看不见。
    /// Core 必须接受任意时长（§13 的手动验证会用 1~2 分钟的任务跑）。
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
