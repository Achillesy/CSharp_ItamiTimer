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
/// ── 渲染近似（用户 2026-07-27 定）─────────────────────────
///
/// **整排是镜像的。** 几何仍然按「向右倒」算（那样递推最好写），但画到屏幕时把 x
/// 翻过来：于是**第一块倒下的在右边、往左倒**，影子自然投向右侧，跟表盘那个
/// **左上**光源就一致了——不用改表盘，也不用改光源。递减的侧面因此落在左边，
/// 正好是受光面。
///
/// **看不到顶面。** 相机高度在骨牌顶端一线，能看见顶面就意味着俯视，跟平视的
/// 消失点矛盾。这一条同时决定了地上的影子只能是很薄的一条带子。
///
/// **侧面宽度线性递减**：屏幕上最左那块是完整的梯形，往右依次 3/4、1/2、1/4，
/// 第五块起省略。**侧面是向光面，正对屏幕的正面反而略暗。**
///
/// **倒下的牌没有侧面**：它转到了侧向，那个面已经看不见了。
///
/// **影子是连成一片的一条带子，大小固定**，不随倒下块数变化。先画影子后画骨牌，
/// 并且**骨牌整体下移、底部压住影子的上沿**——不压住的话骨牌会看着像浮在影子上方。
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

    /// <summary>
    /// 露出的侧面宽度，按**屏幕上从左数第几块**给：完整 → 3/4 → 1/2 → 1/4 → 没有。
    ///
    /// 原来用逐块减半（1/2、1/4、1/8……），**衰减太快**——第三块起就只剩零点几个
    /// 像素，等于只有最左一块看得出厚度。改成线性递减之后，前四块都还读得出来，
    /// 后面省略掉反而更干净。
    /// </summary>
    private static readonly double[] SideSchedule = [1.00, 0.75, 0.50, 0.25];

    private static double SideWidth(int i)
    {
        var j = Count - 1 - i;        // 镜像之后，屏幕上从左数第几块（0 起）
        return j < SideSchedule.Length ? T * SideSchedule[j] : 0;
    }

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
                    if (a < 1e-6) maxX = Math.Max(maxX, q.X + SideWidth(i));   // 立着的还有右侧面
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

        // 缩放七天共用（以并集包围盒为准），否则周一的骨牌会明显比周日的大。
        var scale = Math.Min(k.W / (MaxX - MinX), k.H * 0.94 / MaxY);

        // 转成数组：ReadOnlySpan 不能被局部函数捕获。只有 7 个元素，而 Build
        // 一天才跑一次，这点开销无所谓。
        var ang = Angles(k.Fallen).ToArray();
        double AngleAt(int i) => i < ang.Length ? ang[i] : 0;

        // **左对齐**：锚点取【本布局自己】的最大世界 x（镜像后就是屏幕最左那一点），
        // 而不是七天并集的。用并集会让周一到周六左边空出一大块——那块是留给周日
        // 倒平的牌的，可周一根本没有。
        // 周日因此有自己的锚点，但那天一块立着的都没有，看不出位置变过（用户说
        // 周日是特例、位置随便）。
        var anchor = double.MinValue;
        for (var i = 0; i < Count; i++)
            foreach (var q in Corners(i * Pitch + T, AngleAt(i)))
                anchor = Math.Max(anchor, q.X);

        // 影子的跨度：左端 = 最左那块的左下角（= 屏幕上的 padX）；
        // **右端 = 最右那块「右下顶点」在 X 轴上的投影**（用户 2026-07-27 修正）。
        //
        // 屏幕最右那块是世界索引 0。它的【屏幕右下顶点】对应世界的左下角，
        // 倒下之后这个角会翘离地面，所以取它的 x 投到地面上：
        //     世界 x = T·(1 − cos θ₀)
        // θ₀ 是索引 0 那块的倒角，而它**每天都不一样**（30° → 54° → … → 75°），
        // 所以每天的影子长度会差一点点。用户认为影子居中之后看不出来。
        var theta0 = AngleAt(0) * Math.PI / 180;
        var shadowSpan = (anchor - T * (1 - Math.Cos(theta0))) * scale;
        var padX = (k.W - shadowSpan) / 2;

        // 骨牌整体下移，脚跟压住影子，否则会看着像浮在影子上方
        var baseY = k.H * 0.965;
        Point S(Point w) => new(padX + (anchor - w.X) * scale, baseY - w.Y * scale);

        // ---- 第一遍：一整条影子。**连成一片、大小固定**，不随倒下块数变化。
        //      范围只取【七块都立着时】的那一段（倒下的牌伸出去的部分不带影子，
        //      因为影子大小是固定的），光在左上所以往右多探一截。
        //      上下沿都用渐变淡出：硬边会读成一条独立的灰带，骨牌就像浮在它上面。
        {
            // **执行标准（用户 2026-07-27）：最左那块骨牌的左下角 = 影子的左下角。**
            // 所以带子的左边界就是 padX、底边就是脚跟线 baseY，往上、往右铺开。
            // 光从左边来 → 左边不该有影子；地面近乎侧看 → 越远越高，所以往上长。
            //
            // 右端跟着最右那块走：它一旦倒下，影子也跟着缩到斜面落下来的位置附近，
            // 所以右端锚在它的支点上，而不是固定的满宽。
            var left = padX;
            var right = padX + shadowSpan;
            var top = baseY - T * scale * 1.15;
            var bottom = baseY;
            list.Add((Quad(new Point(left, top), new Point(right, top),
                           new Point(right, bottom), new Point(left, bottom)),
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.00),
                        new GradientStop(Color.FromArgb(0x18, 0, 0, 0), 0.55),
                        new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 1.00),
                    }
                }));
        }

        // ---- 第二遍：骨牌。从右往左，左边倒下的那块压在右邻身上（在上层）
        for (var i = Count - 1; i >= 0; i--)
        {
            var a = AngleAt(i);
            var c = Corners(i * Pitch + T, a);
            var (bl, br, tr, tl) = (S(c[0]), S(c[1]), S(c[2]), S(c[3]));

            // 立着的牌在右边多出一个侧面，宽度逐块减半（消失点在左）。
            // **没有顶面**——相机在骨牌顶端一线，看得见顶面就意味着俯视，
            // 跟平视的消失点矛盾。
            if (a < 1e-6)
            {
                // 镜像之后侧面落在【左】边，正对左上的光 —— 它是向光面。
                //
                // **不是上下等宽**：视平线就在骨牌顶端（所以看不到顶面），纵深方向的
                // 线全都朝那里收，于是这个面越往上越窄、到顶几乎并成一条。远端的
                // 底角同时要抬起来一点——它比近端离视平线更近。
                var d = SideWidth(i) * scale;
                if (d > 0.4)
                {
                    // 消失点在**画面之外**、大致在半高的位置，所以远端那条边
                    // **上下各收一截**（而不是只收顶上）。收多少跟露出的宽度成正比。
                    var shrink = d * 0.55;
                    list.Add((Quad(br,
                                   new Point(br.X - d, br.Y - shrink),
                                   new Point(tr.X - d, tr.Y + shrink),
                                   tr),
                              new SolidColorBrush(p.DominoSide)));
                }
            }

            list.Add((Quad(tl, tr, br, bl), new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Lighten(p.DominoFace, 0.12), 0.0),
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
