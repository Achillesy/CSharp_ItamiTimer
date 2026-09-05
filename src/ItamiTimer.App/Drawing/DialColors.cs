using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// The two colour schemes for the dial.
///
/// **The dial follows the theme** (item 1 of §0.4's revision): plain white by day, deep
/// grey by night, the day and night face of the same thing, not an either/or choice.
///
/// Added 2026-07-27: getting the geometry right wasn't enough. The user pointed at a photo
/// of a real wooden wall clock and noted the difference was in **material and light** --
/// the bezel's gradient, the clock's cast shadow on the wall, the bezel's shadow cast onto
/// the face, the hands' shadows on the face. So besides the semantic colours, this also
/// carries a set of colours purely for painting "physical feel".
/// </summary>
public sealed record DialPalette(
    Color Face, Color FaceRim, Color Ink, Color Tick,
    Color BezelLit, Color BezelMid, Color BezelDark,
    Color Focus, Color Amber, Color Slack, Color Absent, Color Pending, Color Sweep, Color Rest,
    Color DominoTop, Color DominoFace, Color DominoSide,
    Color Alarm, Color AlarmsDot, Color AlarmsDotOuter)
{
    /// <summary>Day face: a plain white face plus a wooden bezel (tuned against the reference photo the user provided).</summary>
    public static readonly DialPalette Light = new(
        Face: Color.FromRgb(0xFF, 0xFF, 0xFF),
        FaceRim: Color.FromRgb(0xF2, 0xF3, 0xF5),   // The face's rim, where the bezel blocks the light
        Ink: Color.FromRgb(0x1B, 0x22, 0x2A),
        Tick: Color.FromRgb(0x5A, 0x63, 0x6D),
        // The wooden bezel: lit upper-left -> unlit lower-right
        BezelLit: Color.FromRgb(0xB5, 0x7C, 0x4C),
        BezelMid: Color.FromRgb(0x8C, 0x58, 0x30),
        BezelDark: Color.FromRgb(0x5A, 0x35, 0x1C),
        Focus: Color.FromRgb(0x2F, 0xA3, 0x6B),
        Amber: Color.FromRgb(0xE0, 0xA0, 0x3A),
        Slack: Color.FromRgb(0xD6, 0x45, 0x3F),
        // ⚠️ Unused anywhere since 2026-07-28: "not present" cells changed to drawing
        // nothing at all (§8.2.3). This token stays because it's the semantic slot for one
        // of the four possible outcomes -- if being away is ever drawn again, no colour
        // needs picking a second time.
        Absent: Color.FromRgb(0x8A, 0x94, 0xA0),
        Pending: Color.FromRgb(0x3B, 0x7D, 0xD8),
        // §8.2.6: the second hand must NOT use slack red. Red is the semantic colour for
        // "off-task", and if the band is entirely red the second hand would disappear, and
        // it would also train the eye that "red = normal". A separate token, and it needs
        // to be LIGHT -- it's decoration and shouldn't compete with the hour/minute hands.
        Sweep: Color.FromRgb(0x33, 0x40, 0x4B),
        // §8.4.4's rest wedge. **Must not be grey**: grey already means "not counted" on
        // this dial (being away, time still owed), so drawing a reward in grey would make
        // it look like a debt. Blue is the only hue still free, and it naturally reads as
        // "take a break".
        Rest: Color.FromRgb(0x4E, 0x8C, 0xC8),
        // The dominoes: **wood** (set by the user, 2026-07-27), a shade lighter than the
        // dial's wooden bezel so it doesn't compete. After mirroring, the side face ends
        // up on the LEFT, facing the upper-left light, so it's the lit face -- but it only
        // needs to be **slightly** brighter, too much of a gap and it stops reading as the
        // same piece of wood. DominoTop is kept but no longer used: the camera sits level
        // with the top of the dominoes, so the top face is never visible.
        DominoTop: Color.FromRgb(0xE2, 0xC6, 0xA4),
        DominoFace: Color.FromRgb(0xC4, 0x9E, 0x74),
        DominoSide: Color.FromRgb(0xE6, 0xC8, 0xA4),
        // The alarm hand: warm yellow, like an old-fashioned alarm clock. Shorter than the minute hand, thicker than the hour hand.
        Alarm: Color.FromRgb(0xF0, 0xC0, 0x40),
        // Alarms 清单的小红圈（DESIGN §17）：独立的一个色号，即便数值上跟 Slack 相近，
        // 也不共用同一个 token——以后想单独调其中一个都不会牵动另一个。
        AlarmsDot: Color.FromRgb(0xD6, 0x45, 0x3F),
        // 同一分钟不止一条时，外圈换成**橙色**、中心那点仍是上面的红（3.7.0，用户
        // 2026-09-03：两圈同色看不出是两个）。同理另起一个 token 而不是复用 Amber
        // (#E0A03A)——那是色环"21-40 秒专注"那一档的语义色，跟提醒没关系。
        // 取值偏"艳橙"不是随手挑的：这个标记的圆心压在木框上（半径 1.0），淡一点的橙
        // 会糊进木色（#B57C4C）里，红色本来就是靠对比度选的，橙色得把这份对比度接住。
        AlarmsDotOuter: Color.FromRgb(0xE8, 0x6A, 0x16));

    /// <summary>
    /// Night face: a deep grey face, **the same wooden bezel**.
    ///
    /// ⚠️ 边框原来是"深色金属"（`#6E7A88` / `#444E5A` / `#1E242B` 三档灰）。3.0.0 改成
    /// 跟日面**一模一样的木色**（用户 2026-08-29：钟的木制边框不要变）——理由跟骨牌
    /// 维持木色是同一条（DECISIONS M5/M10）：换主题换的是**表盘的照明**，不是把这只钟
    /// 换成另一只钟。木头白天晚上都是木头；边框跟着变成金属，读起来就成了两件不同的
    /// 物件，而这个项目从一开始就在照着一张真实木壳挂钟的照片调"材质和光"（见本文件
    /// 顶部那段）。
    ///
    /// 三档灰的值没有留在注释里做备份：真要回去 `git log` 里就有，留着反而像个还能选
    /// 的选项。
    /// </summary>
    public static readonly DialPalette Dark = new(
        Face: Color.FromRgb(0x20, 0x27, 0x2F),
        FaceRim: Color.FromRgb(0x14, 0x19, 0x1F),
        Ink: Color.FromRgb(0xE4, 0xE9, 0xEF),
        Tick: Color.FromRgb(0x8A, 0x97, 0xA4),
        // 跟 Light 逐字相同，别"顺手"调暗一档：那正是这条决策要挡住的事
        BezelLit: Color.FromRgb(0xB5, 0x7C, 0x4C),
        BezelMid: Color.FromRgb(0x8C, 0x58, 0x30),
        BezelDark: Color.FromRgb(0x5A, 0x35, 0x1C),
        Focus: Color.FromRgb(0x46, 0xBE, 0x84),
        Amber: Color.FromRgb(0xED, 0xB2, 0x55),
        Slack: Color.FromRgb(0xE9, 0x63, 0x5C),
        Absent: Color.FromRgb(0x6E, 0x7A, 0x87),
        Pending: Color.FromRgb(0x5C, 0x97, 0xE8),
        Sweep: Color.FromRgb(0xB8, 0xC4, 0xD0),
        Rest: Color.FromRgb(0x63, 0xA6, 0xE0),      // Brightened a tier for the night face, see §8.4.4
        DominoTop: Color.FromRgb(0x8A, 0x6E, 0x50),
        DominoFace: Color.FromRgb(0x6E, 0x56, 0x3C),
        DominoSide: Color.FromRgb(0x93, 0x77, 0x57),
        Alarm: Color.FromRgb(0xF5, 0xD0, 0x50),
        AlarmsDot: Color.FromRgb(0xE9, 0x63, 0x5C),
        AlarmsDotOuter: Color.FromRgb(0xFA, 0x8A, 0x3C));

    /// <summary>
    /// 跑偏反色用的**半反色调色板**（DESIGN §8.9）：只把**钟面、刻度、指针**换成另一档，
    /// 其余原样保留。
    ///
    /// 翻的五个：<see cref="Face"/>、<see cref="FaceRim"/>、<see cref="Ink"/>（数字 + 时针
    /// 分针 + 轴心）、<see cref="Tick"/>、<see cref="Sweep"/>（秒针）。
    ///
    /// **不翻的，以及为什么**：
    /// - 木边框（<c>Bezel*</c>）——换主题换的是表盘的照明，不是换一只钟（DECISIONS M10）；
    /// - 色环的绿/黄/红（<c>Focus/Amber/Slack</c>）、休息蓝扇形、闹钟黄针、Alarms 小红圈
    ///   ——**那是账本本身**，反了就把"绿=专注、红=偷懒"这套语义拆了；
    /// - 骨牌——它压根不在表盘上。
    ///
    /// 卡片、控件、右上角四个图标同样不跟：它们由用户设定的底色决定，跟这个方法无关
    /// （反色只作用于 <c>DialControl.Palette</c> 一处）。
    /// </summary>
    public DialPalette WithFaceFrom(DialPalette other) => this with
    {
        Face = other.Face,
        FaceRim = other.FaceRim,
        Ink = other.Ink,
        Tick = other.Tick,
        Sweep = other.Sweep,
    };

    /// <summary>
    /// §8.2.3's three-stop transition: focus -> amber -> slack.
    ///
    /// Why not interpolate RGB directly (§0.4's option A): a direct interpolation lands on
    /// a muddy olive green at 50%, which looks like a mistake. Routing through amber stays
    /// clean, and comes with a brightness change built in for free -- red/green is the most
    /// common colour-blindness confusion pair (about 8% of men), so a brightness
    /// difference is a second signal beyond colour alone.
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
