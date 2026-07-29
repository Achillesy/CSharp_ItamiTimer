namespace ItamiTimer.App;

/// <summary>
/// 闹钟模型（DESIGN.md §10）。**纯逻辑，不碰 UI、不碰时钟**——now 全部是参数，
/// 所以整个类可以直接单元测试（tests/ItamiTimer.App.Tests/AlarmClockTests.cs）。
///
/// 状态只有一个：<see cref="FireAt"/>，最后一次拨针算出的响铃时间点。
/// **黄针位置是它的推导值**（时间点对 12 小时取余，用户 2026-07-30 定）——
/// 不单独存位置、不记"变没变、响没响"，跟本项目「状态是推导出来的，
/// 不是攒出来的」（原则 4）一个路数。
///
/// 响铃判定是单调比较 <see cref="ShouldFire"/>，不做角度容差（0.5°/分钟，
/// 1.5° 的"容差"等于提前 3 分钟就响，DECISIONS E1）。
/// </summary>
public sealed class AlarmClock
{
    /// <summary>一格 5 分钟，表盘一圈 720 分钟（12 小时制），共 144 个停靠位。</summary>
    public const double SlotMinutes = 5;
    public const double FaceMinutes = 720;

    /// <summary>
    /// 最后一次拨针算出的响铃时间点。**响过也不清**——它还是黄针位置的来源；
    /// "已经响过"由 <see cref="_fired"/> 记。null = 从来没拨过针。
    /// </summary>
    public DateTime? FireAt { get; private set; }

    /// <summary>本轮时间点已经响过（或恢复进来时就已过期 = 无效）。</summary>
    private bool _fired;

    /// <summary>
    /// 黄针停在表盘上的位置（0~719 分钟）：**时间点对 12 小时取余**。
    /// 没拨过针时黄针停在 12 点（0）。
    /// </summary>
    public double Position => FireAt is { } at
        ? (at.Hour % 12) * 60 + at.Minute + at.Second / 60.0
        : 0;

    /// <summary>
    /// 把黄针拨 <paramref name="minutes"/> 分钟——正数顺时针、负数逆时针
    /// （滚轮：前滚逆时针、后滚顺时针）。拨完立刻用严格算法把「下一次几点响」
    /// 算死存住；悬浮提示直接读 <see cref="FireAt"/>——显示的和会响的是同一个值。
    ///
    /// <see cref="NextRing"/> 保持钟面位置不变（今天 T / T+12 / 明天 T 对 12 小时
    /// 取余相同），所以推导出的 <see cref="Position"/> 恰好就是拨完的新位置。
    /// </summary>
    public void Bump(double minutes, DateTime now)
    {
        // C# 的 % 对负数会给出负值，逆时针跨过 12 点要做真模运算才能环绕
        var pos = ((Position + minutes) % FaceMinutes + FaceMinutes) % FaceMinutes;
        FireAt = NextRing(now, pos);
        _fired = false;
    }

    /// <summary>到点了吗。单调比较，不重新判黄针位置。</summary>
    public bool ShouldFire(DateTime now) => !_fired && FireAt is { } at && now >= at;

    /// <summary>响过即撤——闹钟是一次性的，不是每日重复（DECISIONS E5）。时间点留着当黄针位置。</summary>
    public void MarkFired() => _fired = true;

    /// <summary>
    /// 从上次会话恢复（2026-07-30）：只需要那一个时间点。黄针位置自动从它推导
    /// 出来（每次打开都复位到 12 点会像闹钟被清了一样怪异）；时间点没过就有效、
    /// 过了就当已响——关着程序时错过的闹钟不补响。
    /// </summary>
    public void Restore(DateTime? fireAt, DateTime now)
    {
        FireAt = fireAt;
        _fired = fireAt is not { } at || at <= now;
    }

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
