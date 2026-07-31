namespace ItamiTimer.Core;

/// <summary>
/// 色环上的一格 = 一分钟（DESIGN.md §8.2.3）。
///
/// **这是重放结果的一个投影，不是一个逐分钟累加的数组**（§0.4.2）。判据：
/// 关掉界面再打开，每一格的颜色都要能原样重建。
///
/// 因为 StartedAt 进位到了整分钟（§14.1），每格恒为完整的 60 秒——四个秒数
/// 字段加起来总是 60。唯一的例外是最后一格：专注达成时刻不进位（§14.4），
/// 所以末格可能不足 60 秒，色环上按实际长度画短一截。
///
/// **刻意只给原始秒数，不给颜色。** 怎么上色是渲染层的事（§8 第四条纪律）：
/// 命令行渲染成 ANSI 色块、表盘渲染成色环，两边消费同一个列表。
/// </summary>
/// <param name="Index">从 0 开始，第 i 格覆盖 [StartedAt + i 分钟, +1 分钟)。</param>
/// <param name="Start">这一格的起始时刻，天然落在整分钟上——所以「分针就是写入头」（§8.2.2）。</param>
/// <param name="CountedSeconds">OnTask，即计入累计专注时长的部分。</param>
public readonly record struct MinuteCell(
    int Index,
    DateTimeOffset Start,
    double CountedSeconds,
    double OffTaskSeconds,
    double AbsentSeconds,
    double GapSeconds)
{
    /// <summary>这一格实际覆盖的秒数。除末格外恒为 60。</summary>
    public double TotalSeconds => CountedSeconds + OffTaskSeconds + AbsentSeconds + GapSeconds;

    /// <summary>
    /// 纯度 0~1：这一格里有多大比例是算数的。
    ///
    /// 分母是 <see cref="TotalSeconds"/> 而**不是**固定的 60——这样末格那种
    /// 不足一分钟的情况不会被算成"偷懒了大半分钟"（§14.1 说的就是这个坑，
    /// 只不过 StartedAt 进位之后它只可能出现在末格）。
    /// </summary>
    public double Purity => TotalSeconds <= 0 ? 0 : CountedSeconds / TotalSeconds;
}
