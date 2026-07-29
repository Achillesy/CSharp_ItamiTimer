namespace ItamiTimer.App;

/// <summary>
/// 闹钟模型（DESIGN.md §10）。**纯逻辑，不碰 UI、不碰时钟**——now 全部是参数，
/// 所以整个类可以直接单元测试（tests/ItamiTimer.App.Tests/AlarmClockTests.cs）。
///
/// 黄针只能停在表盘的 144 个格子上（0~719 分钟，5 分钟 = 2.5° 一格）。
/// **响铃时刻在拨针那一刻就算死**（<see cref="NextRing"/>），之后只拿
/// <see cref="ShouldFire"/> 单调比较——不做角度容差判定（0.5°/分钟，1.5° 的
/// "容差"等于提前 3 分钟就响，DECISIONS E1）。
/// </summary>
public sealed class AlarmClock
{
    /// <summary>一格 5 分钟，表盘一圈 720 分钟（12 小时制），共 144 个停靠位。</summary>
    public const double SlotMinutes = 5;
    public const double FaceMinutes = 720;

    /// <summary>黄针停在表盘上的位置（0~719 分钟，5 的倍数）。喂给 DialControl 画针。</summary>
    public double Position { get; private set; }

    /// <summary>下一次响铃的绝对时刻。null = 未设置（或已经响过）。</summary>
    public DateTime? FireAt { get; private set; }

    /// <summary>
    /// 把黄针拨 <paramref name="minutes"/> 分钟——正数顺时针、**负数逆时针**
    /// （2026-07-30：左键/前滚逆时针，右键/后滚顺时针）。拨完立刻用严格算法把
    /// 「下一次几点响」算死存住。悬浮提示直接读 <see cref="FireAt"/>——
    /// 显示的和会响的保证是同一个值。
    /// </summary>
    public void Bump(double minutes, DateTime now)
    {
        // C# 的 % 对负数会给出负值，逆时针跨过 12 点要做真模运算才能环绕
        Position = ((Position + minutes) % FaceMinutes + FaceMinutes) % FaceMinutes;
        FireAt = NextRing(now, Position);
    }

    /// <summary>到点了吗。单调比较，不重新判黄针位置。</summary>
    public bool ShouldFire(DateTime now) => FireAt is { } at && now >= at;

    /// <summary>响过即撤——闹钟是一次性的，不是每日重复（DECISIONS E5）。</summary>
    public void MarkFired() => FireAt = null;

    /// <summary>
    /// 黄针格子（12 小时制钟面时刻 T）→ 下一次会响的具体时刻。
    /// 三级判断**全部用严格小于**（用户 2026-07-30 定，DECISIONS E2）：
    ///
    /// <code>
    /// now &lt; 今天的 T       → 今天的 T（上午那一半）
    /// now &lt; 今天的 T + 12h → T + 12（下午那一半）
    /// 否则                  → 明天的 T
    /// </code>
    ///
    /// **故意不用「小于等于」**——now 恰好落在黄针那一格上（拨的瞬间正好和时针
    /// 重合）的意思是"12 小时后"，不是"现在"，不然拨着拨着突然就响了。
    /// </summary>
    public static DateTime NextRing(DateTime now, double faceMinutes)
    {
        var t = now.Date.AddMinutes(faceMinutes);
        var tPlus12 = t.AddHours(12);
        if (now < t) return t;
        if (now < tPlus12) return tPlus12;
        return t.AddDays(1);
    }
}
