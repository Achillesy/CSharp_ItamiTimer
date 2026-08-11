using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// Tomato -- the app icon.
///
/// A pomodoro timer using a tomato as its icon is fitting. **This is the only icon there
/// is**: it goes into the exe's resources (csproj `ApplicationIcon`) and the .app's .icns,
/// both by way of <see cref="IconExport"/>. There used to be a `RingIcon` that swapped in a
/// live progress ring while a task ran; it was deleted on 2026-08-10 because nothing
/// displayed it any more (DECISIONS D11).
///
/// Pure vector, like the dial and the dominoes: a handful of polygons and Beziers, no
/// bitmap asset, crisp at any size.
///
/// **Colour constraint (set by the user): standard red + standard green, no more than 8
/// colours total.** Actually 6. **No outline** -- adding a black line would turn it into
/// an illustration. Depth comes from the brightness difference between faces, not
/// gradients: a gradient smears into a blur at a 16px taskbar icon size, while flat colour
/// blocks still read as a tomato even scaled down further.
///
/// Redrawn on 2026-07-27: the first version modeled a "paper-tomato" look with a big green
/// cap sitting over the whole body, which was a plain factual error -- a real tomato's
/// sepals are **a few small, pointed leaves**, plus a short stem, never large enough to
/// cover the shoulders of the fruit.
/// </summary>
public static class TomatoIcon
{
    // ---- 6 colours, not one more
    private static readonly Color Red = Color.FromRgb(0xE0, 0x1B, 0x24);
    private static readonly Color RedDark = Color.FromRgb(0xB3, 0x11, 0x18);
    private static readonly Color RedLit = Color.FromRgb(0xF2, 0x55, 0x5B);
    private static readonly Color GreenDark = Color.FromRgb(0x1A, 0x66, 0x2C);
    private static readonly Color Green = Color.FromRgb(0x2C, 0x8B, 0x3C);
    private static readonly Color GreenLit = Color.FromRgb(0x4F, 0xAD, 0x5C);

    public static RenderTargetBitmap Render(int size = 128)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            Point P(double x, double y) => new(x * size, y * size);
            void Fill(Color c, Geometry g) => ctx.DrawGeometry(new SolidColorBrush(c), null, g);

            // ---- The fruit body. Draws the dark version whole first, then offsets the
            //      bright version slightly toward the upper-left on top of it, so the
            //      lower-right naturally shows a crescent of shadow -- two shapes create
            //      volume without any gradient.
            Fill(RedDark, Body(P, 0.512, 0.572));
            Fill(Red, Body(P, 0.492, 0.556));

            // The upper-left highlight: a crescent of bright red
            Fill(RedLit, Highlight(P));

            // ---- Sepals: a few small, pointed leaves fanning outward from the top of the
            //      fruit, drooping slightly. Not quite symmetric left to right, which is
            //      what makes it look real.
            var hub = P(0.50, 0.250);
            foreach (var (tx, ty, w) in new[]
                     {
                         (0.150, 0.248, 0.026),   // Leftmost, nearly horizontal
                         (0.252, 0.146, 0.024),
                         (0.388, 0.108, 0.022),
                         (0.618, 0.102, 0.022),
                         (0.762, 0.150, 0.024),
                         (0.858, 0.262, 0.026),   // Rightmost
                     })
                Fill(GreenDark, Leaf(hub, P(tx, ty), w * size));

            // The middle two overlap on top, one shade brighter, giving the leaves a sense of layering
            foreach (var (tx, ty, w) in new[] { (0.318, 0.186, 0.028), (0.692, 0.182, 0.028) })
                Fill(Green, Leaf(hub, P(tx, ty), w * size));

            // ---- Stem: a short piece, tilted slightly to the right
            Fill(Green, Quad(P(0.470, 0.268), P(0.524, 0.268), P(0.558, 0.078), P(0.512, 0.072)));
            Fill(GreenLit, Quad(P(0.512, 0.072), P(0.558, 0.078), P(0.564, 0.048), P(0.518, 0.042)));
        }
        return rtb;
    }

    /// <summary>The fruit body: rounded, slightly flattened, fuller toward the bottom, close to the real thing's "rounded square" silhouette.</summary>
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

    /// <summary>The crescent highlight in the upper-left.</summary>
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

    /// <summary>One leaf: a slender triangle from the hub toward the tip, slightly bulged at the waist, tapering to a point.</summary>
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
