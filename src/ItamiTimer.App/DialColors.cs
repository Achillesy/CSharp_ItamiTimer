using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// DESIGN.md §8.2.7 的两套配色。取自 `design/dial-specimens.html` 里调好的 token。
///
/// **盘面跟随主题**（§0.4 修正后的第 1 项）：白天素白、夜里深灰，是同一套东西的
/// 日面与夜面，不是二选一。
/// </summary>
public sealed record DialPalette(
    Color Face, Color Edge, Color Ink, Color Tick,
    Color Focus, Color Amber, Color Slack, Color Absent, Color Pending, Color Sweep)
{
    public static readonly DialPalette Light = new(
        Face: Color.FromRgb(0xFC, 0xFC, 0xFD),
        Edge: Color.FromRgb(0xB7, 0xC1, 0xCC),
        Ink: Color.FromRgb(0x1B, 0x22, 0x2A),
        Tick: Color.FromRgb(0x8E, 0x9A, 0xA6),
        Focus: Color.FromRgb(0x2F, 0xA3, 0x6B),
        Amber: Color.FromRgb(0xE0, 0xA0, 0x3A),
        Slack: Color.FromRgb(0xD6, 0x45, 0x3F),
        Absent: Color.FromRgb(0x8A, 0x94, 0xA0),
        Pending: Color.FromRgb(0x3B, 0x7D, 0xD8),
        // §8.2.6：秒针【不能】用 slack 红。红是"偷懒"的语义色，色带全红时秒针会
        // 消失，而且会教育眼睛"红 = 正常"。独立 token。
        Sweep: Color.FromRgb(0x3F, 0x5D, 0x80));

    public static readonly DialPalette Dark = new(
        Face: Color.FromRgb(0x20, 0x27, 0x2F),
        Edge: Color.FromRgb(0x3C, 0x47, 0x53),
        Ink: Color.FromRgb(0xE4, 0xE9, 0xEF),
        Tick: Color.FromRgb(0x6B, 0x78, 0x85),
        Focus: Color.FromRgb(0x46, 0xBE, 0x84),
        Amber: Color.FromRgb(0xED, 0xB2, 0x55),
        Slack: Color.FromRgb(0xE9, 0x63, 0x5C),
        Absent: Color.FromRgb(0x6E, 0x7A, 0x87),
        Pending: Color.FromRgb(0x5C, 0x97, 0xE8),
        Sweep: Color.FromRgb(0xC9, 0xD4, 0xE0));

    /// <summary>
    /// §8.2.3 的三段过渡：focus → amber → slack。
    ///
    /// 为什么不 RGB 直插（§0.4 选项 A）：直插在 50% 处会出现发脏的橄榄绿，看着像
    /// 画错了。经琥珀走一趟就干净，而且自带明度变化——红绿是最常见的色盲混淆对
    /// （约 8% 男性），明度差是颜色之外的第二个信号。
    /// </summary>
    public Color Ramp(double impurity)
    {
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));

        impurity = Math.Clamp(impurity, 0, 1);
        return impurity <= 0.5
            ? Lerp(Focus, Amber, impurity / 0.5)
            : Lerp(Amber, Slack, (impurity - 0.5) / 0.5);
    }
}
