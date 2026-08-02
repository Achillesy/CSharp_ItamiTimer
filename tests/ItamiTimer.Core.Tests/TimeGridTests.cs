using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// 整分钟对齐。看着琐碎，但**整个引擎的坐标系就建在它上面**：
/// 起点截断到整分钟 → 每格恒为完整 60 秒 → 写入偏移恒为整数（DECISIONS H9）。
///
/// 2026-08-02 删掉了 `CeilToMinute` 那几条——那个函数到最后只剩这里在用，
/// 而「为什么选截断不选进位」（A6）的理由留在 `FloorToMinute` 的注释里就够了。
/// </summary>
public class TimeGridTests
{
    private static DateTimeOffset At(int h, int m, int s, int ms = 0)
        => new(2026, 7, 27, h, m, s, ms, TimeSpan.FromHours(8));

    [Fact]
    public void 向下取整_抹掉秒和毫秒()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.FloorToMinute(At(10, 8, 37, 500)));
    }

    [Fact]
    public void 已经在整分钟上则原样返回()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.FloorToMinute(At(10, 8, 0)));
    }

    /// <summary>
    /// A6 知情接受的代价：截断会把点击「开始」**之前**最多 59 秒也算进任务。
    /// 用户选了这个——不想点完干等。这条测试把那 59 秒钉住，免得哪天被当成 bug「修掉」。
    /// </summary>
    [Fact]
    public void 截断的结果绝不晚于输入_最多把之前59秒算进来()
    {
        var t = At(10, 8, 59, 999);
        var floored = TimeGrid.FloorToMinute(t);

        Assert.True(floored <= t);
        Assert.True((t - floored).TotalSeconds < 60);
    }

    [Fact]
    public void 取整保留原来的时区偏移()
    {
        var t = new DateTimeOffset(2026, 7, 27, 10, 8, 37, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(8), TimeGrid.FloorToMinute(t).Offset);
    }
}
