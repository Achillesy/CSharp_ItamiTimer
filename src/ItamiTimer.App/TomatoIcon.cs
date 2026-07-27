using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// 番茄 —— 应用图标。
///
/// 一个番茄钟用番茄做图标是本分。**任务进行中**任务栏图标换成 <see cref="RingIcon"/>
/// 的进度色环（§8.3.2）；**空闲时**就是这颗番茄。
///
/// 跟表盘和骨牌一样是纯矢量：几个多边形和贝塞尔，没有位图资源，任何尺寸都清晰。
///
/// **配色约束（用户定）：标准红 + 标准绿，总用色不超过 8 种。** 实际 6 种。
/// **不描边**——加黑线就变成插画了。层次靠面与面的明暗差，不用渐变：渐变在 16px
/// 的任务栏图标上会糊成一团，而纯色块缩到再小也还能看出是个番茄。
///
/// 2026-07-27 重画：第一版照着「折纸番茄」做了一个很大的绿色罩子扣在果身上，
/// 那是常识性错误 —— 真实番茄顶上的萼片是**小而尖的几片叶子**，外加一小截梗，
/// 绝不会大到盖住果肩。
/// </summary>
public static class TomatoIcon
{
    // ---- 6 种颜色，一个不多
    private static readonly Color Red = Color.FromRgb(0xE0, 0x1B, 0x24);
    private static readonly Color RedDark = Color.FromRgb(0xB3, 0x11, 0x18);
    private static readonly Color RedLit = Color.FromRgb(0xF2, 0x55, 0x5B);
    private static readonly Color GreenDark = Color.FromRgb(0x1A, 0x66, 0x2C);
    private static readonly Color Green = Color.FromRgb(0x2C, 0x8B, 0x3C);
    private static readonly Color GreenLit = Color.FromRgb(0x4F, 0xAD, 0x5C);

    public static WindowIcon Make(int size = 128) => RingIcon.ToIcon(Render(size));

    public static RenderTargetBitmap Render(int size = 128)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            Point P(double x, double y) => new(x * size, y * size);
            void Fill(Color c, Geometry g) => ctx.DrawGeometry(new SolidColorBrush(c), null, g);

            // ---- 果身。先画暗的一整个，再把亮的往左上错开一点点盖上去，
            //      右下就自然露出一弯暗部 —— 两个形状做出体积，不用渐变。
            Fill(RedDark, Body(P, 0.512, 0.572));
            Fill(Red, Body(P, 0.492, 0.556));

            // 左上的高光：一道弯月形的亮红
            Fill(RedLit, Highlight(P));

            // ---- 萼片：小而尖的几片叶子，从果顶中心朝外散开、略微下垂。
            //      左右不完全对称才像真的。
            var hub = P(0.50, 0.250);
            foreach (var (tx, ty, w) in new[]
                     {
                         (0.150, 0.248, 0.026),   // 最左，几乎平伸
                         (0.252, 0.146, 0.024),
                         (0.388, 0.108, 0.022),
                         (0.618, 0.102, 0.022),
                         (0.762, 0.150, 0.024),
                         (0.858, 0.262, 0.026),   // 最右
                     })
                Fill(GreenDark, Leaf(hub, P(tx, ty), w * size));

            // 中间两片压在上层、亮一档，做出叶子相互叠压的层次
            foreach (var (tx, ty, w) in new[] { (0.318, 0.186, 0.028), (0.692, 0.182, 0.028) })
                Fill(Green, Leaf(hub, P(tx, ty), w * size));

            // ---- 梗：一小截，稍微朝右斜
            Fill(Green, Quad(P(0.470, 0.268), P(0.524, 0.268), P(0.558, 0.078), P(0.512, 0.072)));
            Fill(GreenLit, Quad(P(0.512, 0.072), P(0.558, 0.078), P(0.564, 0.048), P(0.518, 0.042)));
        }
        return rtb;
    }

    /// <summary>果身：圆润、略扁、下部更饱满，接近实物那种「圆角方」的轮廓。</summary>
    private static Geometry Body(Func<double, double, Point> P, double cx, double cy)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(P(cx, cy - 0.300), true);
        g.CubicBezierTo(P(cx - 0.290, cy - 0.300), P(cx - 0.440, cy - 0.140), P(cx - 0.440, cy + 0.040));
        g.CubicBezierTo(P(cx - 0.440, cy + 0.250), P(cx - 0.265, cy + 0.378), P(cx, cy + 0.378));
        g.CubicBezierTo(P(cx + 0.265, cy + 0.378), P(cx + 0.440, cy + 0.250), P(cx + 0.440, cy + 0.040));
        g.CubicBezierTo(P(cx + 0.440, cy - 0.140), P(cx + 0.290, cy - 0.300), P(cx, cy - 0.300));
        g.EndFigure(true);
        return geo;
    }

    /// <summary>左上那道弯月形高光。</summary>
    private static Geometry Highlight(Func<double, double, Point> P)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(P(0.208, 0.650), true);
        g.CubicBezierTo(P(0.132, 0.505), P(0.180, 0.372), P(0.292, 0.306));
        g.CubicBezierTo(P(0.336, 0.348), P(0.326, 0.362), P(0.306, 0.382));
        g.CubicBezierTo(P(0.230, 0.444), P(0.210, 0.540), P(0.258, 0.648));
        g.EndFigure(true);
        return geo;
    }

    /// <summary>一片叶子：从中心朝 tip 的细长三角，腰略鼓、尖端收成一点。</summary>
    private static Geometry Leaf(Point hub, Point tip, double halfWidth)
    {
        var dx = tip.X - hub.X;
        var dy = tip.Y - hub.Y;
        var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        var nx = -dy / len * halfWidth;
        var ny = dx / len * halfWidth;

        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(new Point(hub.X + nx, hub.Y + ny), true);
        g.QuadraticBezierTo(new Point(hub.X + dx * 0.55 + nx * 0.85, hub.Y + dy * 0.55 + ny * 0.85), tip);
        g.QuadraticBezierTo(new Point(hub.X + dx * 0.55 - nx * 0.85, hub.Y + dy * 0.55 - ny * 0.85),
                            new Point(hub.X - nx, hub.Y - ny));
        g.EndFigure(true);
        return geo;
    }

    private static Geometry Quad(Point a, Point b, Point c, Point d)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(a, true);
        g.LineTo(b); g.LineTo(c); g.LineTo(d);
        g.EndFigure(true);
        return geo;
    }
}
