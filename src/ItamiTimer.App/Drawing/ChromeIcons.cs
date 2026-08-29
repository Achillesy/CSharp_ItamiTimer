using Avalonia.Controls;
using Avalonia.Media;

// ImplicitUsings also brings in System.IO, which has its own Path. Path in this file is
// always the graphics one; the alias pins that down so the full name doesn't need writing
// out everywhere.
using Path = Avalonia.Controls.Shapes.Path;

namespace ItamiTimer.App;

/// <summary>
/// The two instant-toggle icons in the top-right corner: the speaker (tick sound), the pin
/// (always on top).
///
/// **Why these aren't font glyphs anymore**: these two used to be Segoe Fluent Icons
/// glyphs, and that font only ships preinstalled on Windows -- two neat tofu boxes on
/// macOS.
///
/// Switching to vector drawing isn't just "making do for cross-platform" -- it actually
/// fits this project better to begin with: the dial, the tomato, the dominoes, the exe
/// icon are all drawn in code, and **the repository holds no bitmaps**. An icon font was
/// the one external art dependency on that rule; removing it actually completed the rule
/// -- and the two platforms now look identical.
///
/// State is still expressed through **the shape itself plus opacity, doubled up**, never
/// text (there isn't a single word above the divider):
///
/// | | Off | On |
/// |---|---|---|
/// | Speaker | A diagonal slash | Two sound waves |
/// | Pin | Hollow (outline only) | Filled |
///
/// The pin's off state **deliberately avoids an "X"**: at full opacity an X reads as
/// "pinning is disabled", which is the opposite of "currently pinned". A slashed speaker
/// has no such ambiguity -- everyone reads a slashed speaker as muted.
/// </summary>
public static class ChromeIcons
{
    /// <summary>
    /// 墨色 + 光晕，两支笔都从当前主题的表盘调色板里取（v3.0.0）。
    ///
    /// ⚠️ **这里原来是两个写死的常量**（深墨 <c>#1B222A</c> + 白光晕），当时的理由是
    /// "窗口背景固定浅灰、不跟随主题"——**v3.0.0 起这个前提作废**：主题成了用户可切的
    /// 两态，这三个图标又恰好坐在卡片**以外**、直接压在表盘和骨牌上，夜面下深墨糊在
    /// 深灰盘面上就只剩白光晕在撑形状了。
    ///
    /// 取值不新增色号：墨 = <c>Palette.Ink</c>（表盘上数字和指针用的同一支笔），
    /// 光晕 = <c>Palette.Face</c>（盘面色，日面素白正是原来那个白光晕，夜面深灰自然
    /// 就是深光晕）。传 null 退回日面，让还没改到的调用点照常编译、照常是原来的样子。
    /// </summary>
    private static (IBrush Ink, IBrush Halo) Pens(DialPalette? p)
    {
        p ??= DialPalette.Light;
        return (new SolidColorBrush(p.Ink), new SolidColorBrush(p.Face));
    }

    // 描边光晕这一层为什么存在：主窗口整体无边框透明之后（DECISIONS K），这几个图标
    // 可能直接叠在任意桌面壁纸上——只有墨色一个颜色时，遇到明暗接近的壁纸区域会看不
    // 清，而且原来只在悬停时才明显（`Button.chrome:pointerover` 把 Opacity 从 0.40 提到
    // 0.85），静止状态几乎"隐形"。每个形状先拿光晕色描一圈更粗的轮廓垫底，再叠正常的
    // 墨色——跟提示条的双层文字（DECISIONS J12）同一个思路，不是加背景色块（用户
    // 2026-08-08 明确要求）。这几个图标坐在 Start 按钮**以上**的表盘/骨牌区域，不在
    // 后来加的那张半透明卡片范围内（Start 按钮以下才有卡片），所以描边这一层继续留着，
    // 没有跟着 `HaloTextBlock`（文字那边）一起撤销。

    /// <summary>Icons are drawn in a 16x16 box, scaled and given opacity by the containing Button.</summary>
    private const double Box = 16;

    /// <summary>
    /// Speaker. The body plus the horn is one closed path; two arcs are added on the right
    /// when on, one diagonal slash when off.
    /// </summary>
    public static Control Speaker(bool on, DialPalette? palette = null)
    {
        var (Ink, Halo) = Pens(palette);

        // The body (the small square on the left) plus the horn (a trapezoid opening to the right), drawn in one stroke
        const string Body = "M 2,6 L 5,6 L 9,2.5 L 9,13.5 L 5,10 L 2,10 Z";
        var geo = Geometry.Parse(Body);

        var canvas = new Canvas { Width = Box, Height = Box };
        // 光晕垫底（见 Halo 的注释）：**两个分开的 Path**，不是同一个 Path 上下叠 Fill/Stroke——
        // 单个 Path 上 Fill+Stroke 同时设置时描边是描在填充**之上**的，会啃掉半圈填充的边缘；
        // 分开画，浅色描边先垫底、深色纯填充盖在上面，只留外侧那一圈光晕
        canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round });
        canvas.Children.Add(new Path { Data = geo, Fill = Ink });

        if (on)
        {
            // Two sound waves. Their radii are spread apart a tier so it reads as "playing", not "one ring"
            AddStroke(canvas, "M 11,5.5 A 4,4 0 0 1 11,10.5", 1.4, Ink, Halo);
            AddStroke(canvas, "M 12.8,3.4 A 7,7 0 0 1 12.8,12.6", 1.4, Ink, Halo);
        }
        else
        {
            // A diagonal slash. **Cutting from upper-left to lower-right**, straight across the horn -- unmistakably "muted" at a glance
            AddStroke(canvas, "M 11,4 L 15,12", 1.6, Ink, Halo);
        }

        return canvas;
    }

    /// <summary>
    /// Pin. The kind stuck in vertically: a round head plus a shaft, **filled when on,
    /// outline-only when off**. Opacity is handled by the containing Button's style
    /// (0.40 off / 0.95 on); this only handles the shape.
    /// </summary>
    public static Control Pin(bool on, DialPalette? palette = null)
    {
        var (Ink, Halo) = Pens(palette);

        // The head (the trapezoid cap on top) plus the neck plus the point, running straight down
        const string Data = "M 5.5,2 L 10.5,2 L 9.5,7 L 11.5,9 L 8.6,9 L 8,14.5 " +
                            "L 7.4,9 L 4.5,9 L 6.5,7 Z";
        var geo = Geometry.Parse(Data);

        var canvas = new Canvas { Width = Box, Height = Box };

        // 光晕垫底（见 Halo 的注释）：填实时（on）光晕描边够粗就能从深色填充边缘露出来；
        // 空心轮廓时（off）光晕是更粗的一圈浅色描边，深色描边细一圈叠在正上方。
        canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = on ? 2.2 : 3.0, StrokeJoin = PenLineJoin.Round });

        var shape = new Path
        {
            Data = geo,
            Fill = on ? Ink : null,
            Stroke = on ? null : Ink,
            StrokeThickness = 1.2,
            StrokeJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(shape);
        return canvas;
    }



    /// <summary>
    /// Gear (settings). Eight teeth plus a centre hole, using the EvenOdd fill rule to cut
    /// the hole out.
    ///
    /// **Computed**, like the speaker and the pin, not a font glyph: the tooth outline's 32
    /// points are derived from three numbers -- outer radius, inner radius, tooth width
    /// (see the note at the top of this file -- Segoe Fluent Icons renders as tofu boxes on
    /// macOS).
    ///
    /// It has no on/off state, so it doesn't take a <c>bool</c>: while the settings window
    /// is open, this button is already blocked by the modal dialog, so there's no "is it
    /// currently open" state that needs expressing.
    /// </summary>
    public static Control Gear(DialPalette? palette = null)
    {
        var (Ink, Halo) = Pens(palette);

        const string Outer = "M 14.91,6.85 L 14.91,9.15 L 12.98,9.49 L 12.58,10.47 L 13.70,12.07 L 12.07,13.70 L 10.47,12.58 L 9.49,12.98 L 9.15,14.91 L 6.85,14.91 L 6.51,12.98 L 5.53,12.58 L 3.93,13.70 L 2.30,12.07 L 3.42,10.47 L 3.02,9.49 L 1.09,9.15 L 1.09,6.85 L 3.02,6.51 L 3.42,5.53 L 2.30,3.93 L 3.93,2.30 L 5.53,3.42 L 6.51,3.02 L 6.85,1.09 L 9.15,1.09 L 9.49,3.02 L 10.47,3.42 L 12.07,2.30 L 13.70,3.93 L 12.58,5.53 L 12.98,6.51 Z";
        const string Hole = "M 5.70,8.00 A 2.30,2.30 0 1 0 10.30,8.00 A 2.30,2.30 0 1 0 5.70,8.00 Z";

        var geo = Geometry.Parse(Outer + " " + Hole);
        if (geo is PathGeometry pg) pg.FillRule = FillRule.EvenOdd;

        var canvas = new Canvas { Width = Box, Height = Box };
        // 光晕垫底（见 Halo 的注释，Speaker 那边有更完整的说明为什么要分两个 Path）：
        // 这份 EvenOdd 几何体的描边会把齿轮外沿和中间挖空的孔洞边缘一起描到，两个孔都
        // 会有光晕
        canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = 1.4, StrokeJoin = PenLineJoin.Round });
        canvas.Children.Add(new Path { Data = geo, Fill = Ink });
        return canvas;
    }

    /// <summary>
    /// 关闭（表盘右键菜单里那一项）。两笔交叉的斜线，跟系统任务栏"关闭窗口"那一项
    /// 的图标同一个形状。
    ///
    /// **不加光晕描边**，跟上面三个不一样：这个图标只出现在右键菜单里，菜单自己有
    /// 不透明背景，不存在直接叠在桌面壁纸上的问题（Halo 那条注释里说的正是这个前提）。
    ///
    /// ⚠️ 这里的 X **跟 <see cref="Pin"/> 那条注释里"刻意不用 X"是两回事**：那条说的是
    /// 别拿 X 表示"图钉关着"（会读成"禁用置顶"，正好相反）；这里 X 就是它字面的意思
    /// ——关闭，没有歧义。
    /// </summary>
    public static Control Close(DialPalette? palette = null)
    {
        var (Ink, _) = Pens(palette);

        var canvas = new Canvas { Width = Box, Height = Box };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 4,4 L 12,12 M 12,4 L 4,12"),
            Stroke = Ink,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
        });
        return canvas;
    }

    /// <summary>
    /// 主题（v3.0.0 新增的第四个图标，右上角 2×2 的最后一格）。
    ///
    /// **画的是当前所处的状态，不是"点了会变成什么"**——跟喇叭和图钉一致：图标即状态，
    /// 这一排四个按钮读法统一（用户 2026-08-29 拍板）。日间是太阳（实心圆 + 八根光芒），
    /// 夜间是月亮——**一块饼干被咬掉一口**的形状，斜着躺（见下面那段注释：细弓月、
    /// 凸月各试过版本，都被否掉了，原因逐条记在那儿）。
    ///
    /// 跟其它三个一样是**算出来的矢量**，不是字体字形（D5：Segoe Fluent Icons 在 macOS
    /// 上是豆腐块）。
    /// </summary>
    public static Control Theme(bool dark, DialPalette? palette = null)
    {
        var (Ink, Halo) = Pens(palette);
        var canvas = new Canvas { Width = Box, Height = Box };

        if (dark)
        {
            // 月亮 = **一块饼干被咬掉一口**（用户 2026-08-29 定的形，"要大一点、要有
            // 斜度、躺下一点"）：一个几乎占满 16 格的大圆（圆心 8,8 半径 5.7），
            // EvenOdd 减掉右上角一个明显更小的圆（圆心 11.96,4.04 半径 3.4），咬口
            // 的轴线走 45°——所以它是躺着的上弦/下弦，不是立着的一弯。
            //
            // ⚠️ **咬口跨过外沿，所以不能用"两个圆 + EvenOdd"那一手**（齿轮挖中间那个
            // 孔可以，是因为孔整个在里面）：小圆露在大圆外面的那半边，EvenOdd 数下来
            // 交叉次数是奇数，**照样会被填上**——屏幕上就是两个圆叠在一起，正是前两版
            // 被否掉的那个样子。这里改成**一条闭合路径描一圈轮廓**：
            //   ① 从咬口的上角 (6.40,2.53) 出发，沿饼干外沿（圆心 8,8 半径 5.7）
            //      绕远路（左、下、右，237°）走到咬口的下角 (13.47,9.60)；
            //   ② 再沿"嘴"那条弧（圆心 16.06,-0.06 半径 10）切回来，收回上角。
            //
            // **嘴比饼干大得多**（半径 10 : 5.7，用户 2026-08-29 定的）——这是切口平滑
            // 的来源：弧越大越接近一条直线，咬痕就越像"切"而不是"啃"。咬得也深：切口
            // 最深处离饼干中心只有 1.4（d − r₂ = 11.4 − 10），弦长 10、几乎横跨整块饼干
            // （直径 11.4）。轴线走 45°，所以它躺着，不是立着的一弯。
            var geo = Geometry.Parse(
                "M 6.40,2.53 A 5.7,5.7 0 1 0 13.47,9.60 A 10,10 0 0 1 6.40,2.53 Z");
            canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = 1.4, StrokeJoin = PenLineJoin.Round });
            canvas.Children.Add(new Path { Data = geo, Fill = Ink });
        }
        else
        {
            // 太阳：实心的日轮 + 八根光芒。光芒是描边，跟喇叭的声波同一个画法。
            var disc = Geometry.Parse("M 4.4,8 A 3.6,3.6 0 1 0 11.6,8 A 3.6,3.6 0 1 0 4.4,8 Z");
            canvas.Children.Add(new Path { Data = disc, Stroke = Halo, StrokeThickness = 1.5, StrokeJoin = PenLineJoin.Round });
            canvas.Children.Add(new Path { Data = disc, Fill = Ink });

            // 四正 + 四斜，长度一致：内端 5.3、外端 7.1（相对圆心 8,8 的半径）
            const double In = 5.3, Out = 7.1;
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                // 不变文化格式化：Geometry.Parse 只认小数点，跟着系统区域设置走会在
                // 用逗号做小数点的地区安静画错（这类错正是这个项目栽过的那一类）。
                AddStroke(canvas, FormattableString.Invariant(
                        $"M {8 + In * cos:0.##},{8 + In * sin:0.##} L {8 + Out * cos:0.##},{8 + Out * sin:0.##}"),
                    1.3, Ink, Halo);
            }
        }

        return canvas;
    }

    /// <summary>光晕垫底 + 正常描边，两层叠在一起加进 canvas——光晕更粗、垫在下面，正常粗细的墨色描边叠在正上方（见上面那段关于光晕的注释）。</summary>
    private static void AddStroke(Canvas canvas, string data, double thickness, IBrush Ink, IBrush Halo)
    {
        var geo = Geometry.Parse(data);
        canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = thickness + 1.6, StrokeLineCap = PenLineCap.Round });
        canvas.Children.Add(new Path { Data = geo, Stroke = Ink, StrokeThickness = thickness, StrokeLineCap = PenLineCap.Round });
    }
}
