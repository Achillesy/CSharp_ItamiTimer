using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// 表盘（DESIGN.md §8.2 的正式规格）。
///
/// **这是纯渲染层**：只吃 <see cref="MinuteCell"/> 列表和几个标量，不做任何判定、
/// 不碰 AW、不持有累加值。§8 第四条纪律「状态与渲染分开」的落点——命令行把同一份
/// 列表渲染成 ANSI 色块，这里渲染成色环。
///
/// 几何全部归一化到 rFace = 1.0（§8.2.1）。
///
/// 2026-07-27：用户拿真实木质挂钟的照片指出，第一版差的不是几何而是**材质和光**。
/// 于是加了四层"实物感"：钟底落影、边框渐变、边框投在盘面上的内影、指针投影。
/// 光源统一假定在**左上**。
/// </summary>
public class DialControl : Control
{
    // ---- §8.2.1 分层与半径（归一化到 rFace = 1.0）
    private const double RBezelOut = 1.075;  // 木框外缘。第一版 1.13 太厚，像玩具
    private const double RNumerals = 0.745;  // 0.795 挤刻度、0.70 离刻度太远，取中
    private const double RTickMinor = 0.918, RTickMajor = 0.893, RTickOuter = 0.955;
    private const double RHour = 0.55, RMinute = 0.775, RSecond = 0.88;
    private const double RHub = 0.035;

    /// <summary>休息扇形的外缘。压在刻度圈里侧，别盖住数字（§8.4.4）。</summary>
    private const double RestWedgeOuter = 0.70;

    /// <summary>§8.2.5 螺旋三圈。超过 180 分钟不再内缩，在 lane 2 上原地覆盖。</summary>
    private static readonly (double In, double Out)[] Lanes =
    [
        (0.50, 0.68),   // 0–60 分钟
        (0.31, 0.46),   // 60–120
        (0.14, 0.26),   // 120–180
    ];

    public static readonly StyledProperty<DialPalette> PaletteProperty =
        AvaloniaProperty.Register<DialControl, DialPalette>(nameof(Palette), DialPalette.Light);

    /// <summary>色环内容。空列表 = 空盘 = 下一轮的邀请（§8.4.5a）。</summary>
    public static readonly StyledProperty<IReadOnlyList<MinuteCell>> CellsProperty =
        AvaloniaProperty.Register<DialControl, IReadOnlyList<MinuteCell>>(nameof(Cells), []);

    /// <summary>任务开始时刻，决定色环从盘面哪个分钟刻度起画（§8.2.2 分针即写入头）。</summary>
    public static readonly StyledProperty<DateTimeOffset?> StartedAtProperty =
        AvaloniaProperty.Register<DialControl, DateTimeOffset?>(nameof(StartedAt));

    /// <summary>还欠多少分钟专注。§8.2.4 的承诺弧与截止线。</summary>
    public static readonly StyledProperty<double> RemainingMinutesProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(RemainingMinutes));

    /// <summary>休息扇形的起点（= 专注达成那一刻）。null = 不在休息，不画。</summary>
    public static readonly StyledProperty<DateTimeOffset?> RestFromProperty =
        AvaloniaProperty.Register<DialControl, DateTimeOffset?>(nameof(RestFrom));

    /// <summary>休息多少分钟 = 承诺时长 ÷ 5。</summary>
    public static readonly StyledProperty<double> RestMinutesProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(RestMinutes));


    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public IReadOnlyList<MinuteCell> Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public DateTimeOffset? StartedAt { get => GetValue(StartedAtProperty); set => SetValue(StartedAtProperty, value); }
    public double RemainingMinutes { get => GetValue(RemainingMinutesProperty); set => SetValue(RemainingMinutesProperty, value); }
    public DateTimeOffset? RestFrom { get => GetValue(RestFromProperty); set => SetValue(RestFromProperty, value); }
    public double RestMinutes { get => GetValue(RestMinutesProperty); set => SetValue(RestMinutesProperty, value); }

    static DialControl()
        => AffectsRender<DialControl>(PaletteProperty, CellsProperty, StartedAtProperty,
                                      RemainingMinutesProperty, RestFromProperty, RestMinutesProperty);

    // 12 点为 0°，顺时针，分钟 × 6°（§8.2）
    private static Point At(Point c, double r, double deg)
    {
        var rad = (deg - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
    private static readonly Color Shadow = Color.FromArgb(0xFF, 0, 0, 0);

    public override void Render(DrawingContext ctx)
    {
        var p = Palette;
        var box = Math.Min(Bounds.Width, Bounds.Height);
        if (box <= 0) return;

        // 留出画落影的余量，否则影子会被控件边界切掉
        var rFace = box / 2 / (RBezelOut + 0.10);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 - rFace * 0.03);
        double R(double n) => n * rFace;

        DrawDropShadow(ctx, c, R, rFace);
        DrawBezel(ctx, c, R);
        DrawFace(ctx, c, R);
        DrawRestWedge(ctx, c, R);   // 在色环【之下】：它是背景上的一块光，不是记录
        DrawRing(ctx, c, R);
        DrawTicks(ctx, c, R, rFace);
        DrawNumerals(ctx, c, R, rFace);
        DrawHands(ctx, c, R, rFace);
    }

    /// <summary>钟落在墙上的影子。压扁、下偏、由黑到透明。</summary>
    private static void DrawDropShadow(DrawingContext ctx, Point c, Func<double, double> R, double rFace)
    {
        var center = new Point(c.X, c.Y + rFace * 0.06);
        var rx = R(RBezelOut) * 1.06;
        var ry = R(RBezelOut) * 0.98;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(A(Shadow, 0x55), 0.72),
                new GradientStop(A(Shadow, 0x30), 0.88),
                new GradientStop(A(Shadow, 0x00), 1.0),
            }
        };
        ctx.DrawEllipse(brush, null, center, rx, ry);
    }

    /// <summary>木质边框。左上受光、右下背光的线性渐变，读起来才像一圈有厚度的实物。</summary>
    private void DrawBezel(DrawingContext ctx, Point c, Func<double, double> R)
    {
        var p = Palette;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.15, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.85, 1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(p.BezelLit, 0.0),
                new GradientStop(p.BezelMid, 0.55),
                new GradientStop(p.BezelDark, 1.0),
            }
        };
        ctx.DrawEllipse(brush, null, c, R(RBezelOut), R(RBezelOut));

        // 圆润的木框在最外缘会有一道亮边（转过去的那个面还在受光），
        // 少了这道边，整圈就读成"一块扁的褐色圆盘"而不是"一圈有厚度的木头"
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(A(p.BezelLit, 0x70)), R(0.012)),
            c, R(RBezelOut) - R(0.008), R(RBezelOut) - R(0.008));

        // 内缘一道暗线，把木框和盘面分开
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(A(Shadow, 0x44)), R(0.014)), c, R(1.008), R(1.008));
    }

    /// <summary>盘面：极淡的左上高光（玻璃感）+ 边框投上来的内影。</summary>
    private void DrawFace(DrawingContext ctx, Point c, Func<double, double> R)
    {
        var p = Palette;

        // 盘面本体，中心略亮、边缘略暗
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.36, 0.30, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(p.Face, 0.0),
                new GradientStop(p.Face, 0.62),
                new GradientStop(p.FaceRim, 1.0),
            }
        }, null, c, R(1.0), R(1.0));

        // 边框投在盘面上的影：只在内缘一圈，越靠外越深
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(A(Shadow, 0x00), 0.80),
                new GradientStop(A(Shadow, 0x0C), 0.95),
                new GradientStop(A(Shadow, 0x22), 1.0),
            }
        }, null, c, R(1.0), R(1.0));
    }

    /// <summary>§8.2.3 + §8.2.4 + §8.2.5：色块、承诺弧、截止线、螺旋。</summary>
    private void DrawRing(DrawingContext ctx, Point c, Func<double, double> R)
    {
        if (StartedAt is not { } start) return;

        var cells = Cells;
        var p = Palette;
        // 起算时刻在表盘上的角位置（单位分钟）。startedAt 已经截断到整分钟（§14.1），
        // 所以 Second 恒为 0；留着这一项是为了万一将来起点不再对齐时不至于悄悄画错。
        var m0 = start.Minute + start.Second / 60.0;

        foreach (var cell in cells)
        {
            var lane = Math.Min(cell.Index / 60, Lanes.Length - 1);
            var (rIn, rOut) = Lanes[lane];
            var d0 = (m0 + cell.Index) * 6;
            var d1 = d0 + Math.Max(0.1, cell.TotalSeconds / 60.0) * 6;

            // 四种结局分开画（§0.4.1）。把"起身离开"画成红色等于冤枉自己。
            if (cell.GapSeconds > cell.TotalSeconds / 2)
            {
                var pen = new Pen(new SolidColorBrush(p.Tick), R(0.012))
                { DashStyle = new DashStyle([2, 2], 0) };
                ctx.DrawGeometry(null, pen, Annulus(c, R(rIn), R(rOut), d0 + 0.4, d1 - 0.4));
                continue;
            }

            var fill = cell.AbsentSeconds > cell.TotalSeconds / 2
                ? p.Absent                       // 离开：灰，不是红
                : p.Ramp(1 - cell.Purity);       // 绿 → 琥珀 → 红

            ctx.DrawGeometry(new SolidColorBrush(fill),
                new Pen(new SolidColorBrush(A(p.Face, 0xCC)), R(0.005)),
                Annulus(c, R(rIn), R(rOut), d0, d1));

            // 色盲的第二信号（§8.2.3）：重度偷懒的格子加一根径向短刻线，可以【数】出来
            if (cell.Purity < 0.34)
            {
                var mid = (d0 + d1) / 2;
                ctx.DrawLine(new Pen(new SolidColorBrush(A(p.Face, 0xDD)), R(0.008)),
                    At(c, R(rIn + 0.02), mid), At(c, R(rOut - 0.02), mid));
            }
        }

        DrawPendingArc(ctx, c, R, m0 + cells.Count, cells.Count);
    }

    /// <summary>
    /// §8.2.4 承诺弧与截止线。
    ///
    /// **点下「开始」的那一刻就要看得见**（用户 2026-07-27）：从开始时刻到预定的结束
    /// 时刻画一整段灰弧。此前它被画在「有色块」的前提之下，所以任务刚开始、一格都还
    /// 没走完时盘面是空的 —— 看着像没在跑。
    ///
    /// **不画截止线**（用户 2026-07-27）。§8.2.4 原本要求在弧的末端画一条径向实线，
    /// 理由是"偷懒时它往前滑，眼睛得追得住"；实际画出来那一小段线是多余的 ——
    /// 弧自己的末端已经把位置说清楚了。
    /// </summary>
    /// <param name="headDial">
    /// 写入头在**表盘上**的位置，单位分钟（0~60 一圈）。= 起算时刻的分钟数 + 已走完的格数。
    /// </param>
    /// <param name="elapsedMinutes">
    /// 任务**已经走了多少分钟**。圈号只能从这个数来。
    ///
    /// ⚠️ 2026-07-28 的 bug 就出在这里：原来写的是 `(int)(headDial / 60)`，拿表盘上的
    /// 绝对分钟数当圈号。于是 23:59 起算的任务一走过 00:00，`60/60 = 1`，承诺弧就跳到
    /// 第二圈去了，而色块（用的是 `cell.Index / 60`，口径正确）还在第一圈 —— 角度对、
    /// 半径错，看着就是"弧线错位"。任何整点都会犯，零点只是碰巧也是整点。
    /// </param>
    private void DrawPendingArc(DrawingContext ctx, Point c, Func<double, double> R,
                                double headDial, int elapsedMinutes)
    {
        if (RemainingMinutes <= 0.01) return;

        // 承诺弧本身也可能跨过一圈的边界（50 分钟的任务加上补时很容易超过 60 分钟），
        // 所以按圈切段分别画，跟色块的螺旋内缩对齐（§8.2.5）。
        var from = (double)elapsedMinutes;
        var to = from + RemainingMinutes;

        while (from < to - 0.01)
        {
            var lap = Math.Min((int)(from / 60), Lanes.Length - 1);
            // 最后一圈不再往里缩，剩下的全画在这一圈上
            var segEnd = lap == Lanes.Length - 1 ? to : Math.Min(to, (lap + 1) * 60.0);
            var (rIn, rOut) = Lanes[lap];

            var d0 = (headDial + (from - elapsedMinutes)) * 6;
            var d1 = (headDial + (segEnd - elapsedMinutes)) * 6;

            // 灰色，不是蓝色：这段是"还欠着的时间"，它不该有任何情绪
            using (ctx.PushOpacity(0.30))
                ctx.DrawGeometry(new SolidColorBrush(Palette.Tick), null,
                    Annulus(c, R(rIn), R(rOut), d0, d1));

            from = segEnd;
        }
    }

    /// <summary>
    /// §8.4.4 休息扇形：**你挣来的那块时间**。
    ///
    /// 用户 2026-07-28 换掉了原来的"色环线性淡出"：那套每秒改一次不透明度、
    /// 有状态、还得跟休息时长精确对齐，而且实际用起来根本看不见（当时的调试量程下
    /// <c>RestMinutes = 2/5 = 0</c>，压根没有休息阶段）。现在换成一块静止的扇形。
    ///
    /// **为什么是从圆心出发的整块扇形，而不是又一条细环带**：盘面上所有别的东西
    /// 都是细带（色块、承诺弧），再加一条就要靠颜色去区分，而颜色四个档位
    /// （绿 / 琥珀 / 红 / 灰）已经全部占满、各有含义。**留给我们的只有形状。**
    /// 一整块从圆心切出来的扇形，读起来就是"这一块归你了"—— 跟"记录"根本不是
    /// 一类东西，不会看错。
    ///
    /// **为什么不缩、不淡、不画倒计时**：分针本来就在扫。分针扫出这块扇形的那一刻
    /// 就是休息结束。倒计时是**免费**的，不需要任何额外的动画状态 —— 这正是
    /// "分针即写入头"（§8.2.2）那条几何约定第二次白送东西。
    ///
    /// **为什么不是灰**：灰在这个盘面上已经有含义了 —— `Absent`（人不在）和承诺弧
    /// （还欠着的时间）都是灰。拿灰画奖励，等于让奖励长得像欠账。绿 / 琥珀 / 红
    /// 各有其主，**蓝是唯一还空着的色相**，而且它天然读作"歇一歇"。
    /// （`Pending` 那个蓝当初为承诺弧定义过又被否掉，理由是"还欠着的时间不该有情绪"
    /// —— 而休息扇形要的正是情绪。）
    ///
    /// 用**径向渐变**：靠盘沿浓、往圆心淡到没有。一来避免糊住指针轴和时针，
    /// 二来让它看起来像一束光落在盘面上，而不是一块补丁。
    /// </summary>
    private void DrawRestWedge(DrawingContext ctx, Point c, Func<double, double> R)
    {
        if (RestFrom is not { } from || RestMinutes <= 0.001) return;

        var d0 = (from.Minute + from.Second / 60.0) * 6;
        var d1 = d0 + RestMinutes * 6;
        var rOut = R(RestWedgeOuter);

        var tint = Palette.Rest;
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(c, RelativeUnit.Absolute),
            GradientOrigin = new RelativePoint(c, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(rOut, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(rOut, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(A(tint, 0x00), 0.00),
                new GradientStop(A(tint, 0x1E), 0.55),
                new GradientStop(A(tint, 0x4E), 1.00),
            },
        };
        ctx.DrawGeometry(brush, null, Wedge(c, rOut, d0, d1));

        // 盘沿那道稍实的边：把扇形"收口"，不然渐变的外缘会糊成一团
        ctx.DrawGeometry(new SolidColorBrush(A(tint, 0x88)), null,
            Annulus(c, R(RestWedgeOuter - 0.035), rOut, d0, d1));
    }

    /// <summary>一块从圆心切出去的扇形 [d0, d1)。</summary>
    private static StreamGeometry Wedge(Point c, double rOut, double d0, double d1)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        var large = d1 - d0 > 180;
        s.BeginFigure(c, true);
        s.LineTo(At(c, rOut, d0));
        s.ArcTo(At(c, rOut, d1), new Size(rOut, rOut), 0, large, SweepDirection.Clockwise);
        s.LineTo(c);
        s.EndFigure(true);
        return g;
    }

    private void DrawTicks(DrawingContext ctx, Point c, Func<double, double> R, double rFace)
    {
        for (var i = 0; i < 60; i++)
        {
            var major = i % 5 == 0;
            var pen = new Pen(new SolidColorBrush(major ? Palette.Ink : Palette.Tick),
                              rFace * (major ? 0.026 : 0.0105))
            { LineCap = PenLineCap.Flat };
            ctx.DrawLine(pen, At(c, R(major ? RTickMajor : RTickMinor), i * 6), At(c, R(RTickOuter), i * 6));
        }
    }

    private void DrawNumerals(DrawingContext ctx, Point c, Func<double, double> R, double rFace)
    {
        // 十二个数字全画。§8.2.1 原本只画 12/3/6/9（照抄已作废的样板页），但用户给的
        // 实物参考是十二个齐全的，四个数字配一圈密刻度会显得空。色带在 [0.50,0.68]，
        // 数字在 0.795，两边不打架，所以这个改动不影响任何已定的几何。
        foreach (var (n, deg) in Enumerable.Range(1, 12).Select(n => (n, n * 30)))
        {
            var ft = new FormattedText(n.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI", weight: FontWeight.Bold), rFace * 0.185,
                new SolidColorBrush(Palette.Ink));
            var at = At(c, R(RNumerals), deg);
            ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        }
    }

    /// <summary>
    /// §8.2.6：角度是 <c>纯函数(now)</c>，**不用累加器**。原则 4 在渲染层同样成立，
    /// 而且这样天然免疫掉帧和系统休眠后的漂移。分针也带秒的小数一起平滑走——
    /// 秒针扫而分针跳会显得廉价。
    ///
    /// 每根指针先在偏移位置画一遍半透明黑，就是它投在盘面上的影（光源在左上）。
    /// </summary>
    private void DrawHands(DrawingContext ctx, Point c, Func<double, double> R, double rFace)
    {
        var now = DateTime.Now;
        // **秒针一秒一跳**（用户 2026-07-28）。原来是亚秒连续扫（§8.2.6），
        // 但扫秒针的钟是不会响的 —— 扫来自连续驱动的机芯，滴答来自步进擒纵，
        // 真实世界里这两件事互斥。既然要滴答，秒针就得跟着步进，否则声音和
        // 画面各说各话。33ms 的重绘保留：它现在的作用是让跳变及时（延迟 ≤33ms）。
        var sec = (double)now.Second;
        var min = now.Minute + sec / 60.0;
        var hour = now.Hour % 12 + min / 60.0;

        // 影子偏移：光源在左上，影子落右下
        var shift = Matrix.CreateTranslation(rFace * 0.014, rFace * 0.018);
        var shadowBrush = new SolidColorBrush(A(Shadow, 0x38));

        var hourGeo = Taper(c, hour * 30, R(RHour), rFace * 0.030, rFace * 0.013, rFace * 0.10);
        var minGeo = Taper(c, min * 6, R(RMinute), rFace * 0.022, rFace * 0.008, rFace * 0.10);
        var secPen = new Pen(new SolidColorBrush(Palette.Sweep), rFace * 0.008) { LineCap = PenLineCap.Round };
        var secShadowPen = new Pen(shadowBrush, rFace * 0.008) { LineCap = PenLineCap.Round };
        var tail = At(c, -rFace * 0.16, sec * 6);
        var tip = At(c, R(RSecond), sec * 6);

        // 三根指针的影子一起画，只推一次变换
        using (ctx.PushTransform(shift))
        {
            ctx.DrawGeometry(shadowBrush, null, hourGeo);
            ctx.DrawGeometry(shadowBrush, null, minGeo);
            ctx.DrawLine(secShadowPen, tail, tip);
        }

        ctx.DrawGeometry(new SolidColorBrush(Palette.Ink), null, hourGeo);
        ctx.DrawGeometry(new SolidColorBrush(Palette.Ink), null, minGeo);
        // 秒针：细、轻、独立色。它是装饰，不该跟时分针抢（§8.2.6）
        ctx.DrawLine(secPen, tail, tip);

        ctx.DrawEllipse(new SolidColorBrush(Palette.Ink), null, c, R(RHub), R(RHub));
        ctx.DrawEllipse(new SolidColorBrush(A(Palette.Face, 0x99)), null, c, R(RHub * 0.34), R(RHub * 0.34));
    }

    /// <summary>一根带锥度的指针：根部宽、尖端窄，另有一小截尾针。</summary>
    private static StreamGeometry Taper(Point c, double deg, double len, double wBase, double wTip, double tail)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var perp = deg + 90;
        Point P(double r, double w, int sign) => At(At(c, r, deg), w * sign, perp);

        g.BeginFigure(P(-tail, wBase * 0.7, 1), true);
        g.LineTo(P(len, wTip, 1));
        g.LineTo(P(len, wTip, -1));
        g.LineTo(P(-tail, wBase * 0.7, -1));
        g.EndFigure(true);
        return geo;
    }

    /// <summary>一段环形扇区 [d0, d1)。</summary>
    private static StreamGeometry Annulus(Point c, double rIn, double rOut, double d0, double d1)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        var large = d1 - d0 > 180;
        g.BeginFigure(At(c, rOut, d0), true);
        g.ArcTo(At(c, rOut, d1), new Size(rOut, rOut), 0, large, SweepDirection.Clockwise);
        g.LineTo(At(c, rIn, d1));
        g.ArcTo(At(c, rIn, d0), new Size(rIn, rIn), 0, large, SweepDirection.CounterClockwise);
        g.EndFigure(true);
        return geo;
    }
}
