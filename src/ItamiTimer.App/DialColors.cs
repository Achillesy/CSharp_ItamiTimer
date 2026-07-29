using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// DESIGN.md §8.2.7 的两套配色。
///
/// **盘面跟随主题**（§0.4 修正后的第 1 项）：白天素白、夜里深灰，是同一套东西的
/// 日面与夜面，不是二选一。
///
/// 2026-07-27 补：光是几何对了还不够。用户拿一张真实木质挂钟的照片指出，差别在
/// **材质和光**——边框的渐变、钟底的落影、边框投在盘面上的影、指针投在盘面上的影。
/// 所以这里除了语义色，还带了一组画"实物感"用的颜色。
/// </summary>
public sealed record DialPalette(
    Color Face, Color FaceRim, Color Ink, Color Tick,
    Color BezelLit, Color BezelMid, Color BezelDark,
    Color Focus, Color Amber, Color Slack, Color Absent, Color Pending, Color Sweep, Color Rest,
    Color DominoTop, Color DominoFace, Color DominoSide,
    Color Alarm)
{
    /// <summary>日面：素白盘 + 木质边框（对着用户给的那张实物照片调的）。</summary>
    public static readonly DialPalette Light = new(
        Face: Color.FromRgb(0xFF, 0xFF, 0xFF),
        FaceRim: Color.FromRgb(0xF2, 0xF3, 0xF5),   // 盘面边缘，被边框挡住光的地方
        Ink: Color.FromRgb(0x1B, 0x22, 0x2A),
        Tick: Color.FromRgb(0x5A, 0x63, 0x6D),
        // 木框：左上受光 → 右下背光
        BezelLit: Color.FromRgb(0xB5, 0x7C, 0x4C),
        BezelMid: Color.FromRgb(0x8C, 0x58, 0x30),
        BezelDark: Color.FromRgb(0x5A, 0x35, 0x1C),
        Focus: Color.FromRgb(0x2F, 0xA3, 0x6B),
        Amber: Color.FromRgb(0xE0, 0xA0, 0x3A),
        Slack: Color.FromRgb(0xD6, 0x45, 0x3F),
        // ⚠️ 2026-07-28 起没有任何地方在用：「人不在」的格子改成什么都不画
        // （§8.2.3）。留着这个 token 是因为它是四种结局之一的语义位置，
        // 哪天想把离开重新画出来时不必再调一次色。
        Absent: Color.FromRgb(0x8A, 0x94, 0xA0),
        Pending: Color.FromRgb(0x3B, 0x7D, 0xD8),
        // §8.2.6：秒针【不能】用 slack 红。红是"偷懒"的语义色，色带全红时秒针
        // 会消失，而且会教育眼睛"红 = 正常"。独立 token，而且要【轻】——
        // 它是装饰，不该跟时分针抢。
        Sweep: Color.FromRgb(0x33, 0x40, 0x4B),
        // §8.4.4 休息扇形。**不能是灰**：灰在这个盘面上已经是"不计入"的意思
        // （离开、还欠着的时间），拿它画奖励等于让奖励长得像欠账。
        // 蓝是唯一还空着的色相，而且它天然读作"歇一歇"。
        Rest: Color.FromRgb(0x4E, 0x8C, 0xC8),
        // 骨牌：**木质**（用户 2026-07-27 定），比表盘的木框浅一档，免得抢。
        // 镜像之后侧面落在【左】边正对左上的光，所以侧面是向光面 ——
        // 但**只需要稍微亮一点**，明暗拉太开就不像同一块木头了。
        // DominoTop 保留但不再使用：相机在骨牌顶端一线，看不到顶面。
        DominoTop: Color.FromRgb(0xE2, 0xC6, 0xA4),
        DominoFace: Color.FromRgb(0xC4, 0x9E, 0x74),
        DominoSide: Color.FromRgb(0xE6, 0xC8, 0xA4),
        // 闹钟指针：暖黄，老式闹钟那种。比分针短、比时针粗。
        Alarm: Color.FromRgb(0xF0, 0xC0, 0x40));

    /// <summary>夜面：深灰盘 + 深色金属边框。</summary>
    public static readonly DialPalette Dark = new(
        Face: Color.FromRgb(0x20, 0x27, 0x2F),
        FaceRim: Color.FromRgb(0x14, 0x19, 0x1F),
        Ink: Color.FromRgb(0xE4, 0xE9, 0xEF),
        Tick: Color.FromRgb(0x8A, 0x97, 0xA4),
        BezelLit: Color.FromRgb(0x6E, 0x7A, 0x88),
        BezelMid: Color.FromRgb(0x44, 0x4E, 0x5A),
        BezelDark: Color.FromRgb(0x1E, 0x24, 0x2B),
        Focus: Color.FromRgb(0x46, 0xBE, 0x84),
        Amber: Color.FromRgb(0xED, 0xB2, 0x55),
        Slack: Color.FromRgb(0xE9, 0x63, 0x5C),
        Absent: Color.FromRgb(0x6E, 0x7A, 0x87),
        Pending: Color.FromRgb(0x5C, 0x97, 0xE8),
        Sweep: Color.FromRgb(0xB8, 0xC4, 0xD0),
        Rest: Color.FromRgb(0x63, 0xA6, 0xE0),      // 夜面提亮一档，见 §8.4.4
        DominoTop: Color.FromRgb(0x8A, 0x6E, 0x50),
        DominoFace: Color.FromRgb(0x6E, 0x56, 0x3C),
        DominoSide: Color.FromRgb(0x93, 0x77, 0x57),
        Alarm: Color.FromRgb(0xF5, 0xD0, 0x50));

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
