using ItamiTimer.App;

namespace ItamiTimer.App.Tests;

/// <summary>
/// 闹钟模型的边界测试（DESIGN.md §10、DECISIONS E1~E5）。
/// 全部是纯函数/纯状态，now 是参数——不需要等真实时间。
/// </summary>
public class AlarmClockTests
{
    private static DateTime At(int h, int m, int s = 0) => new(2026, 7, 30, h, m, s);

    // ---- NextRing：用户 2026-07-30 给的三个算例 ----

    [Fact]
    public void 现在2005_黄针905_那就是今晚2105响()
    {
        // 黄针领先时针 30° = 1 小时
        Assert.Equal(At(21, 05), AlarmClock.NextRing(At(20, 05), 9 * 60 + 5));
    }

    [Fact]
    public void 现在2005_黄针205_过了下午场_只能等明天凌晨205()
    {
        var ring = AlarmClock.NextRing(At(20, 05), 2 * 60 + 5);
        Assert.Equal(new DateTime(2026, 7, 31, 2, 5, 0), ring);
    }

    [Fact]
    public void 现在0805_黄针205_上午场过了_下午1405响()
    {
        Assert.Equal(At(14, 05), AlarmClock.NextRing(At(8, 05), 2 * 60 + 5));
    }

    // ---- 严格小于：恰好重合 = 12 小时后，绝不当场响（DECISIONS E2）----

    [Fact]
    public void 恰好重合_意思是12小时后_不是现在()
    {
        Assert.Equal(At(21, 00), AlarmClock.NextRing(At(9, 00), 9 * 60));
    }

    [Fact]
    public void 恰好重合在下午场_等明天上午()
    {
        var ring = AlarmClock.NextRing(At(21, 00), 9 * 60);
        Assert.Equal(new DateTime(2026, 7, 31, 9, 0, 0), ring);
    }

    [Fact]
    public void 差一秒也算还没到_今天就响()
    {
        // 08:59:59 拨到 09:00 → 一秒后就该响，不该跳到 21:00
        Assert.Equal(At(9, 00), AlarmClock.NextRing(At(8, 59, 59), 9 * 60));
    }

    // ---- 跨午夜边界 ----

    [Fact]
    public void 黄针在12点位置_深夜拨它_响在明天正午前的午夜后半()
    {
        // 黄针 0 分钟 = 钟面 12 点 = 面时刻 00:00。
        // 23:30 时：今天 00:00 已过，00:00+12=12:00 也已过 → 明天 00:00。
        var ring = AlarmClock.NextRing(At(23, 30), 0);
        Assert.Equal(new DateTime(2026, 7, 31, 0, 0, 0), ring);
    }

    [Fact]
    public void 凌晨拨黄针到1155_上午1155响()
    {
        Assert.Equal(At(11, 55), AlarmClock.NextRing(At(0, 10), 11 * 60 + 55));
    }

    [Fact]
    public void 响铃时刻永远严格晚于拨针时刻()
    {
        // 穷举 144 个格子 × 一天里每 7 分钟一个 now：不变量是 FireAt > now
        for (var slot = 0; slot < 144; slot++)
            for (var minuteOfDay = 0; minuteOfDay < 24 * 60; minuteOfDay += 7)
            {
                var now = new DateTime(2026, 7, 30).AddMinutes(minuteOfDay);
                var ring = AlarmClock.NextRing(now, slot * 5);
                Assert.True(ring > now, $"slot={slot} now={now:HH:mm} ring={ring}");
                // 而且不会超过 24 小时——三级判断的上界
                Assert.True(ring <= now.AddHours(24), $"slot={slot} now={now:HH:mm} ring={ring}");
            }
    }

    // ---- Bump / ShouldFire / MarkFired ----

    [Fact]
    public void 拨一格黄针挪五分钟_环绕不越界()
    {
        var a = new AlarmClock();
        a.Bump(715, At(10, 0));
        Assert.Equal(715, a.Position);
        a.Bump(5, At(10, 0));
        Assert.Equal(0, a.Position);   // 719 → 环绕回 0
    }

    [Fact]
    public void 逆时针拨过十二点_环绕到另一头_不出负数()
    {
        var a = new AlarmClock();
        a.Bump(-5, At(10, 0));         // 0 → 逆时针一格
        Assert.Equal(715, a.Position);
        a.Bump(-30, At(10, 0));
        Assert.Equal(685, a.Position);
    }

    [Fact]
    public void 顺拨再逆拨回到原位_响铃时刻也回到原值()
    {
        var a = new AlarmClock();
        var now = At(8, 58);
        a.Bump(5, now);
        var first = a.FireAt;
        a.Bump(30, now);
        a.Bump(-30, now);
        Assert.Equal(5, a.Position);
        Assert.Equal(first, a.FireAt);
    }

    [Fact]
    public void 没拨过针就永远不响()
    {
        var a = new AlarmClock();
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void 到点就响_响过即撤_不是每日闹钟()
    {
        var a = new AlarmClock();
        a.Bump(5, At(8, 58));           // 黄针 00:05 → 面时刻 00:05；08:58 → 今天 12:05
        Assert.Equal(At(12, 05), a.FireAt);

        Assert.False(a.ShouldFire(At(12, 04, 59)));
        Assert.True(a.ShouldFire(At(12, 05)));

        a.MarkFired();
        Assert.False(a.ShouldFire(At(23, 59)));
        Assert.Equal(5, a.Position);    // 时间点留着——它还是黄针位置的来源
    }

    // ---- Restore：只存一个时间点，黄针位置是它对 12 小时取余的推导值（2026-07-30）----

    [Fact]
    public void 恢复未过期的闹钟_黄针位置由时间点推导_重启后照样响那一次()
    {
        var a = new AlarmClock();
        a.Restore(At(21, 05), At(20, 00));
        Assert.Equal((21 % 12) * 60 + 5, a.Position);   // 21:05 → 表盘 9:05 的位置
        Assert.Equal(At(21, 05), a.FireAt);
        Assert.False(a.ShouldFire(At(21, 04, 59)));
        Assert.True(a.ShouldFire(At(21, 05)));
    }

    [Fact]
    public void 过期的时间点不补响_黄针只剩位置这个残影()
    {
        var a = new AlarmClock();
        a.Restore(At(9, 05), At(20, 00));   // 存的响铃时刻已过
        Assert.Equal(9 * 60 + 5, a.Position);
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void 从没拨过针_黄针停在12点_永不响()
    {
        var a = new AlarmClock();
        a.Restore(null, At(10, 00));
        Assert.Equal(0, a.Position);
        Assert.Null(a.FireAt);
        Assert.False(a.ShouldFire(At(23, 59)));
    }

    [Fact]
    public void 恢复的残影上继续拨针_从残影位置起步()
    {
        var a = new AlarmClock();
        a.Restore(At(9, 05), At(20, 00));   // 过期残影在 545
        a.Bump(5, At(20, 00));              // 拨一格 → 550 = 面时刻 9:10
        Assert.Equal(550, a.Position);
        Assert.Equal(At(21, 10), a.FireAt); // 20:00 < 21:10（9:10+12h）
    }

    [Fact]
    public void 连续拨针_每次都重算响铃时刻_显示与响铃永远一致()
    {
        var a = new AlarmClock();
        var now = At(20, 05);
        for (var i = 0; i < 12; i++) a.Bump(5, now);   // 20:05 拨到黄针 01:00
        Assert.Equal(60, a.Position);
        // 面时刻 01:00：20:05 > 01:00、> 13:00 → 明天 01:00
        Assert.Equal(new DateTime(2026, 7, 31, 1, 0, 0), a.FireAt);
    }

}
