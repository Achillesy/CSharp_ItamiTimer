using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class TaskRecordTests
{
    private static TaskRecord WithFocus(int minutes) => new()
    {
        StartedAt = new DateTimeOffset(2026, 7, 27, 10, 9, 0, TimeSpan.FromHours(8)),
        FocusMinutes = minutes,
        Group = "学习经济学",
    };

    /// <summary>
    /// DESIGN §6.1：休息 = ⌈专注 ÷ 5⌉。正式量程全是 5 的倍数，所以就是精确的五分之一。
    /// </summary>
    [Theory]
    [InlineData(10, 2)]
    [InlineData(15, 3)]
    [InlineData(25, 5)]
    [InlineData(50, 10)]
    public void 休息时长是精确的五分之一(int focus, int expectedRest)
    {
        Assert.Equal(expectedRest, WithFocus(focus).RestMinutes);
    }

    /// <summary>
    /// **护栏测试的取值必须覆盖滑块实际能出的值**（DESIGN §6.1 的警示）。
    ///
    /// 老的那条 `最坏的发现延迟也吃不掉名义休息` 只验了 10/25/50——全是 5 的倍数，
    /// 所以 `⌊f/5⌋+1` 在它眼里永远成立；而 2026-07-31 把 Debug 滑块改成步进 1 之后，
    /// 那个公式在 3~10 这 8 个取值里破了 6 个，测试一声不吭。
    ///
    /// 现在改成对**每一个正整数**都验：既不为 0，也不多给。
    /// </summary>
    [Fact]
    public void 任意正整数时长都给出向上取整的五分之一且绝不为零()
    {
        for (var focus = 1; focus <= 120; focus++)
        {
            var rest = WithFocus(focus).RestMinutes;
            Assert.Equal((int)Math.Ceiling(focus / 5.0), rest);
            Assert.True(rest >= 1, $"专注 {focus} 分钟算出了 {rest} 分钟休息");
        }
    }

    /// <summary>
    /// DECISIONS H6：休息**只读提交时锁定的 FocusMinutes**，跟这一轮拖了多久无关。
    /// 归档扣减的是「剩余目标」，那是 JudgmentBuffer 里的另一个量——拿它算休息的话
    /// 拖得越久歇得越少，激励方向就反了。
    /// </summary>
    [Fact]
    public void 休息时长与实际拖了多久无关()
    {
        var task = WithFocus(50);
        var buf = new JudgmentBuffer(task.StartedAt, task.FocusMinutes);

        // 模拟：整整两小时都在别的应用上 → 一秒都不计入 → 跑满 2 小时触发归档
        var tick = task.StartedAt;
        for (var i = 0; i < 125; i++)
        {
            tick = tick.AddMinutes(1);
            var win = new List<AwEvent>
            {
                new(tick.AddSeconds(-JudgmentBuffer.QueryWindowSeconds),
                    JudgmentBuffer.QueryWindowSeconds, "chrome", "摸鱼", null),
            };
            buf.Tick(tick, win, [], Rules, "学习经济学");
        }

        Assert.True(buf.ArchivedSeconds > 0, "两小时之后应该归档过");
        Assert.Equal(10, task.RestMinutes);          // 仍然是 ⌈50/5⌉，没被剩余目标带跑
    }

    private static readonly GroupRules Rules =
        GroupRules.Parse("""{ "groups": { "学习经济学": { "rules": [ { "app": "^econ$" } ] } } }""");

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
    public void 新任务默认是已提交()
    {
        var task = WithFocus(25);
        Assert.Equal(RecordStatus.Committed, task.Status);
        Assert.Null(task.AbandonedAt);
    }
}
