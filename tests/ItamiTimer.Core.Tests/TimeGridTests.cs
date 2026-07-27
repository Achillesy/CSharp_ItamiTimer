using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class TimeGridTests
{
    private static DateTimeOffset At(int h, int m, int s, int ms = 0)
        => new(2026, 7, 27, h, m, s, ms, TimeSpan.FromHours(8));

    [Fact]
    public void 进位_落在分钟中间时进到下一分钟()
    {
        Assert.Equal(At(10, 9, 0), TimeGrid.CeilToMinute(At(10, 8, 37)));
    }

    [Fact]
    public void 进位_只差一毫秒也要进位()
    {
        Assert.Equal(At(10, 9, 0), TimeGrid.CeilToMinute(At(10, 8, 59, 999)));
    }

    [Fact]
    public void 进位_已经在整分钟上则原样返回_不能白跳一分钟()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.CeilToMinute(At(10, 8, 0)));
    }

    /// <summary>
    /// §14.1 的核心约束：绝不向后取整。向后取整会把点击「开始」之前的时间
    /// 也算进任务，等于追溯发放专注时长。
    /// </summary>
    [Fact]
    public void 进位_结果绝不早于输入()
    {
        var t = At(10, 8, 37);
        Assert.True(TimeGrid.CeilToMinute(t) >= t);
    }

    [Fact]
    public void 向下取整_抹掉秒和毫秒()
    {
        Assert.Equal(At(10, 8, 0), TimeGrid.FloorToMinute(At(10, 8, 37, 500)));
    }

    [Fact]
    public void 取整保留原来的时区偏移()
    {
        var t = new DateTimeOffset(2026, 7, 27, 10, 8, 37, TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromHours(8), TimeGrid.CeilToMinute(t).Offset);
    }
}
