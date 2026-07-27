using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// 钟下方的七块多米诺骨牌 —— **星期几**。倒下几块就是周几（周一 1 … 周日 7）。
/// **界面上一个字都没有**，扫一眼就知道，不需要读。
///
/// 纯矢量绘制，没有图片：跟着 DPI 缩放、跟着主题变色。
///
/// ── 几何（用户 2026-07-27 定的硬规格）────────────────────────────
///
/// **比例 6 : 3 : 1**（高 : 宽面 : 厚），**间距 = 高的一半**。
///
/// **倒向右边，压在还没倒的那块身上**（参考实物图：手指在左边推，波往右传）。
/// 每块绕**右下角**顺时针转。
///
/// **倒角是算出来的，不是画上去的。** 两块的情形有解析解：左边那块的右上角
/// 画一段半径 h 的弧，够到右边那块的左面时停住，水平距离恰好是间距，所以
/// `sin θ = gap / h = 1/2 → θ = 30°`。三块以上接触面不再竖直，改用
/// 「从 0° 起转、凸多边形求交、第一次接触即停」扫掠求解（解析式容易取到伪根）。
///
/// ── 两条省事的性质 ─────────────────────────────────────────
///
/// 1. **倒 1~6 块共用一个数组的后缀**（见 <see cref="Cascade"/>）。物理原因是这条
///    链**右对齐**：最右那块永远靠着还立着的那块，姿态与左边有几块无关。所以周一到
///    周六右端完全不变，只是左边不断长出更平的一块。
/// 2. **周日是特例**：七块全倒就没有靠山了，最右那块只能躺平 90°。
///
/// 数值可以互相印证：递推收敛到 75.52°，而「面贴面摞起来」的极限角满足
/// `cos θ = t / pitch = 1/4`，`arccos(0.25) = 75.52°`。两条路算出同一个数。
///
/// ── 渲染近似 ───────────────────────────────────────────
///
/// 相机在**右前方稍高处**：立着的牌在**右边**多出一个侧面和一条顶面；倒下的牌
/// 转到了侧向，那个面看不见了，所以**只画侧面矩形 + 接触影**。
///
/// 光源与表盘统一在**左上**：顶面最亮 → 正面居中 → 右侧面背光最暗。
///
/// ── 性能 ──────────────────────────────────────────────
///
/// 布局一天只变一次、总共只有 7 种，所以**几何算好就缓存**（<see cref="_cache"/>），
/// 重绘时只是顺序画一遍，运行时一次三角函数都不算。
/// </summary>
public class DominoRow : Control
{
    public static readonly StyledProperty<int> FallenProperty =
        AvaloniaProperty.Register<DominoRow, int>(nameof(Fallen));

    public static readonly StyledProperty<DialPalette> PaletteProperty =
        AvaloniaProperty.Register<DominoRow, DialPalette>(nameof(Palette), DialPalette.Light);

    /// <summary>倒下的块数 = 星期几。0~7。</summary>
    public int Fallen { get => GetValue(FallenProperty); set => SetValue(FallenProperty, value); }
    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }

    static DominoRow()
    {
        AffectsRender<DominoRow>(FallenProperty, PaletteProperty);
        (MinX, MaxX, MaxY) = MeasureAllLayouts();
    }

    public const int Count = 7;

    // ---- 骨牌单位：高 6、厚 1、间距 3（= 高/2）、中心距 4
    private const double H = 6, T = 1, Pitch = 4;

    /// <summary>立牌向右后方的纵深偏移（骨牌单位）。相机在右前方稍高处。</summary>
    private const double DX = 0.90, DY = 0.50;

    /// <summary>
    /// 倒 1~6 块的角度（度）。**取这个数组的后 N 个**就是倒 N 块的布局。
    /// 由 6:3:1 + 「间距 = 高/2」扫掠算出，见类注释。
    /// </summary>
    private static readonly double[] Cascade = [75.13, 74.41, 72.43, 67.12, 54.25, 30.00];

    /// <summary>周日：七块全倒，没有靠山，最右那块躺平。</summary>
    private static readonly double[] Sunday = [75.52, 75.52, 75.52, 75.52, 75.52, 75.96, 90.00];

    /// <summary>统一包围盒。**七天共用一个缩放**，否则周一的骨牌会明显比周日的大。</summary>
    private static readonly double MinX, MaxX, MaxY;

    /// <summary>今天该倒几块。周一 1 … 周日 7。</summary>
    public static int FallenForToday(DateTime now)
        => now.DayOfWeek == DayOfWeek.Sunday ? Count : (int)now.DayOfWeek;

    public static ReadOnlySpan<double> Angles(int fallen) => fallen switch
    {
        <= 0 => [],
        >= Count => Sunday,
        _ => Cascade.AsSpan(Cascade.Length - fallen),
    };

    // ---- 缓存
    private readonly record struct CacheKey(double W, double H, int Fallen, DialPalette? Palette);
    private CacheKey _key;
    private List<(Geometry Geo, IBrush Brush)> _cache = [];

    public override void Render(DrawingContext ctx)
    {
        var key = new CacheKey(Bounds.Width, Bounds.Height, Math.Clamp(Fallen, 0, Count), Palette);
        if (key != _key) { _cache = Build(key); _key = key; }
        foreach (var (geo, brush) in _cache) ctx.DrawGeometry(brush, null, geo);
    }

    /// <summary>把七天所有姿态都走一遍，取并集包围盒，用来定死那个共用的缩放。</summary>
    private static (double, double, double) MeasureAllLayouts()
    {
        double minX = double.MaxValue, maxX = double.MinValue, maxY = 0;
        for (var fallen = 0; fallen <= Count; fallen++)
        {
            var ang = Angles(fallen);
            for (var i = 0; i < Count; i++)
            {
                var a = i < ang.Length ? ang[i] : 0;
                foreach (var q in Corners(i * Pitch + T, a))
                {
                    minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                    maxY = Math.Max(maxY, q.Y);
                    if (a < 1e-6)   // 立着的还有纵深
                    {
                        maxX = Math.Max(maxX, q.X + DX);
                        maxY = Math.Max(maxY, q.Y + DY);
                    }
                }
            }
        }
        return (minX, maxX, maxY);
    }

    /// <summary>骨牌四角（世界坐标，y 向上）。绕右下角 (px,0) 顺时针倒 angleDeg。</summary>
    private static Point[] Corners(double px, double angleDeg)
    {
        var r = angleDeg * Math.PI / 180;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        Point Rot(double u, double v) => new(px + u * c + v * s, -u * s + v * c);
        return [Rot(-T, 0), Rot(0, 0), Rot(0, H), Rot(-T, H)];   // 左下、右下、右上、左上
    }

    private static List<(Geometry, IBrush)> Build(CacheKey k)
    {
        var list = new List<(Geometry, IBrush)>();
        if (k.W <= 0 || k.H <= 0 || k.Palette is not { } p) return list;

        // 七天共用的缩放：以并集包围盒为准
        var scale = Math.Min(k.W / (MaxX - MinX), k.H * 0.94 / MaxY);
        var padX = (k.W - (MaxX - MinX) * scale) / 2;
        var baseY = k.H * 0.97;
        Point S(Point w) => new(padX + (w.X - MinX) * scale, baseY - w.Y * scale);

        var ang = Angles(k.Fallen);

        // 从右往左：右邻先画，左边倒下的那块压在它身上（在上层）
        for (var i = Count - 1; i >= 0; i--)
        {
            var a = i < ang.Length ? ang[i] : 0;
            var c = Corners(i * Pitch + T, a);
            var (bl, br, tr, tl) = (S(c[0]), S(c[1]), S(c[2]), S(c[3]));

            // 接触影：躺得越平摊得越开
            var t = a / 90.0;
            var shW = T * scale * (1.1 + t * 7.5);
            var shX = (bl.X + br.X) / 2 + t * H * scale * 0.42;
            list.Add((Ellipse(new Point(shX, baseY + T * scale * 0.16), shW, T * scale * 0.55),
                new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x3C, 0, 0, 0), 0.0),
                        new GradientStop(Color.FromArgb(0x1A, 0, 0, 0), 0.55),
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0),
                    }
                }));

            if (a < 1e-6)
            {
                // 立着：右边多出一个侧面和一条顶面（相机在右前方稍高处）
                Point Back(Point q) => new(q.X + DX * scale, q.Y - DY * scale);
                list.Add((Quad(br, Back(br), Back(tr), tr), new SolidColorBrush(p.DominoSide)));
                list.Add((Quad(tl, tr, Back(tr), Back(tl)), new SolidColorBrush(p.DominoTop)));
            }
            // 倒下的：那个面已经转到侧向看不见了，只画侧面矩形

            list.Add((Quad(tl, tr, br, bl), new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Lighten(p.DominoFace, 0.35), 0.0),   // 左上受光
                    new GradientStop(p.DominoFace, 1.0),
                }
            }));
        }
        return list;
    }

    private static Color Lighten(Color c, double t)
    {
        static byte L(byte v, double t) => (byte)Math.Round(v + (255 - v) * t);
        return Color.FromRgb(L(c.R, t), L(c.G, t), L(c.B, t));
    }

    private static Geometry Ellipse(Point c, double rx, double ry)
        => new EllipseGeometry(new Rect(c.X - rx, c.Y - ry, rx * 2, ry * 2));

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
