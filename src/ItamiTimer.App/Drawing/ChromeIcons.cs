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
    /// Ink colour. The window's background is pinned to a fixed light grey
    /// (MainWindow.axaml's <c>#D7DBE0</c>), not theme-following, so this uses a fixed dark
    /// ink too -- the same value as <see cref="DialPalette.Light"/>'s <c>Ink</c>, the same
    /// pen in both places.
    /// </summary>
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x2A));

    /// <summary>
    /// 描边光晕：主窗口整体无边框透明之后（DECISIONS K），这三个图标可能直接叠在任意
    /// 桌面壁纸上——只有 <see cref="Ink"/> 一个深色，遇到同样偏深的壁纸区域会看不清，
    /// 而且原来只在悬停时才明显（<c>Button.chrome:pointerover</c> 把 Opacity 从 0.40
    /// 提到 0.85），静止状态几乎"隐形"。每个形状先拿这个浅色描一圈更粗的轮廓垫底，
    /// 再叠正常的深色——跟提示条的双层文字（DECISIONS J12）同一个思路，不是加背景
    /// 色块（用户 2026-08-08 明确要求）。这三个图标坐在 Start 按钮**以上**的表盘/骨牌
    /// 区域，不在后来加的那张半透明卡片范围内（Start 按钮以下才有卡片），所以描边
    /// 这一层继续留着，没有跟着 <c>HaloTextBlock</c>（文字那边）一起撤销。
    /// </summary>
    private static readonly IBrush Halo = new SolidColorBrush(Colors.White);

    /// <summary>Icons are drawn in a 16x16 box, scaled and given opacity by the containing Button.</summary>
    private const double Box = 16;

    /// <summary>
    /// Speaker. The body plus the horn is one closed path; two arcs are added on the right
    /// when on, one diagonal slash when off.
    /// </summary>
    public static Control Speaker(bool on)
    {
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
            AddStroke(canvas, "M 11,5.5 A 4,4 0 0 1 11,10.5", 1.4);
            AddStroke(canvas, "M 12.8,3.4 A 7,7 0 0 1 12.8,12.6", 1.4);
        }
        else
        {
            // A diagonal slash. **Cutting from upper-left to lower-right**, straight across the horn -- unmistakably "muted" at a glance
            AddStroke(canvas, "M 11,4 L 15,12", 1.6);
        }

        return canvas;
    }

    /// <summary>
    /// Pin. The kind stuck in vertically: a round head plus a shaft, **filled when on,
    /// outline-only when off**. Opacity is handled by the containing Button's style
    /// (0.40 off / 0.95 on); this only handles the shape.
    /// </summary>
    public static Control Pin(bool on)
    {
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
    public static Control Gear()
    {
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

    /// <summary>光晕垫底 + 正常描边，两层叠在一起加进 canvas——光晕更粗、垫在下面，正常粗细的 Ink 描边叠在正上方（见 Halo 的注释）。</summary>
    private static void AddStroke(Canvas canvas, string data, double thickness)
    {
        var geo = Geometry.Parse(data);
        canvas.Children.Add(new Path { Data = geo, Stroke = Halo, StrokeThickness = thickness + 1.6, StrokeLineCap = PenLineCap.Round });
        canvas.Children.Add(new Path { Data = geo, Stroke = Ink, StrokeThickness = thickness, StrokeLineCap = PenLineCap.Round });
    }
}
