using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// 钟下方的七块多米诺骨牌 —— **星期几**。
///
/// 倒下几块就是周几：周一倒一块，周日倒七块。**界面上一个字都没有**，
/// 你看一眼就知道今天星期几，不需要读。
///
/// 跟表盘一样是纯矢量绘制（几何 + 明暗），没有图片：跟着 DPI 缩放、跟着主题
/// 变色，以后要做"倒下"的动画也是免费的。
///
/// 透视按用户给的实物参考定：**第 1 块正对相机，只看得见侧面（那条窄边）；
/// 越往右露出的正面越多，到第 7 块时那个面已经接近侧面的宽度**。所以侧面宽度
/// 随序号线性增长，不做真的相机投影——七块的跨度上两者看不出区别，而参数化的
/// 版本可控得多。
///
/// 光源与表盘统一：**左上**。所以顶面最亮、朝左的侧面次之、正面最暗。
/// </summary>
public class DominoRow : Control
{
    /// <summary>倒下的块数 = 星期几。0~7。</summary>
    public static readonly StyledProperty<int> FallenProperty =
        AvaloniaProperty.Register<DominoRow, int>(nameof(Fallen));

    public static readonly StyledProperty<DialPalette> PaletteProperty =
        AvaloniaProperty.Register<DominoRow, DialPalette>(nameof(Palette), DialPalette.Light);

    public int Fallen { get => GetValue(FallenProperty); set => SetValue(FallenProperty, value); }
    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }

    static DominoRow() => AffectsRender<DominoRow>(FallenProperty, PaletteProperty);

    private const int Count = 7;

    /// <summary>今天该倒几块。周一 1 … 周日 7。</summary>
    public static int FallenForToday(DateTime now)
        => now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek;

    public override void Render(DrawingContext ctx)
    {
        var p = Palette;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 立着的骨牌：高 h*0.80，正面宽 = 高的 0.30
        var dh = h * 0.80;
        var fw = dh * 0.30;
        var baseY = h * 0.92;                 // 桌面线
        // 间距按骨牌自身尺寸定并整体居中，不要摊满整行——摊开就散了，
        // 参考图里是紧挨着的一串
        var gap = fw * 2.25;
        // 倒下的骨牌绕左下角转，会向左伸出接近一个 dh，所以整排要预留这段余量再居中。
        // 否则周日（七块全倒）时最左那块会被控件边界切掉。
        var fallRoom = dh * 0.85;
        var x0 = (w - gap * (Count - 1) - fw + fallRoom) / 2;

        var fallen = Math.Clamp(Fallen, 0, Count);

        // 从右往左画：右边的立着、左边的倒着，倒下的要压在右邻的前面
        for (var i = Count - 1; i >= 0; i--)
        {
            var x = x0 + i * gap;

            // 侧面（露出的那个宽面）的宽度随序号增长：第 1 块几乎为 0，第 7 块接近正面宽
            var side = fw * 0.95 * i / (Count - 1.0);
            var lift = dh * 0.11;             // 相机略高于骨牌，所以能看见顶面

            // 倒下的角度：最左边那块躺得最平，越靠右倒得越浅（一串正在传导的连锁）
            double angle = 0;
            if (i < fallen)
            {
                var t = fallen == 1 ? 1.0 : (fallen - 1 - i) / (fallen - 1.0);
                angle = 26 + t * 44;          // 26°（刚被推倒）→ 70°（快躺平）
                                              // 不推到 80° 以上：太平就只剩一条缝，读不出是骨牌
            }

            DrawDomino(ctx, p, x, baseY, fw, dh, side, lift, angle);
        }
    }

    private static void DrawDomino(DrawingContext ctx, DialPalette p,
        double x, double baseY, double fw, double dh, double side, double lift, double angleDeg)
    {
        // 骨牌绕【左下角】向左倒。先在未旋转的坐标里算好四个角，再统一旋转。
        var pivot = new Point(x, baseY);
        var rad = -angleDeg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        Point Rot(double dx, double dy)
            => new(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos);

        // 正面四角（未旋转时：左下、右下、右上、左上）
        var fbl = Rot(0, 0);
        var fbr = Rot(fw, 0);
        var ftr = Rot(fw, -dh);
        var ftl = Rot(0, -dh);

        // 纵深方向（朝左后方）。倒下之后这个偏移要跟着缩，否则会看着像穿模
        var shrink = 1 - angleDeg / 110.0;
        var dx = -side * shrink;
        var dy = -lift * shrink;
        Point Back(Point q) => new(q.X + dx, q.Y + dy);

        var contact = new Point((fbl.X + fbr.X) / 2 + dx / 2, baseY);
        DrawContactShadow(ctx, contact, fw * (1.2 + angleDeg / 40.0), fw * 0.22);

        // 三个面。画序：侧面 → 正面 → 顶面，后画的压前面的接缝
        if (side > 0.5)
            ctx.DrawGeometry(new SolidColorBrush(p.DominoLit), null,
                Quad(fbl, ftl, Back(ftl), Back(fbl)));

        // 正面带一点纵向渐变：上亮下暗，白塑料才有体积
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Lighten(p.DominoFront, 0.20), 0.0),
                new GradientStop(p.DominoFront, 1.0),
            }
        }, null, Quad(ftl, ftr, fbr, fbl));

        ctx.DrawGeometry(new SolidColorBrush(p.DominoTop), null,
            Quad(ftl, ftr, Back(ftr), Back(ftl)));
    }

    /// <summary>骨牌与桌面的接触影。压得越平影子越长，跟表盘的落影同一个光源。</summary>
    private static void DrawContactShadow(DrawingContext ctx, Point at, double rx, double ry)
    {
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x4A, 0, 0, 0), 0.0),
                new GradientStop(Color.FromArgb(0x22, 0, 0, 0), 0.6),
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0),
            }
        }, null, new Point(at.X + ry * 0.6, at.Y + ry * 0.35), rx, ry);
    }

    private static Color Lighten(Color c, double t)
    {
        static byte L(byte v, double t) => (byte)Math.Round(v + (255 - v) * t);
        return Color.FromRgb(L(c.R, t), L(c.G, t), L(c.B, t));
    }

    private static StreamGeometry Quad(Point a, Point b, Point c, Point d)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(a, true);
        g.LineTo(b); g.LineTo(c); g.LineTo(d);
        g.EndFigure(true);
        return geo;
    }
}
