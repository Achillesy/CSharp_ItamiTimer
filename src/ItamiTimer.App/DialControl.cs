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

    /// <summary>色环整体不透明度。休息阶段按分钟线性淡出到 0（§8.4.4）。</summary>
    public static readonly StyledProperty<double> RingOpacityProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(RingOpacity), 1.0);

    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public IReadOnlyList<MinuteCell> Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public DateTimeOffset? StartedAt { get => GetValue(StartedAtProperty); set => SetValue(StartedAtProperty, value); }
    public double RemainingMinutes { get => GetValue(RemainingMinutesProperty); set => SetValue(RemainingMinutesProperty, value); }
    public double RingOpacity { get => GetValue(RingOpacityProperty); set => SetValue(RingOpacityProperty, value); }

    static DialControl()
        => AffectsRender<DialControl>(PaletteProperty, CellsProperty, StartedAtProperty,
                                      RemainingMinutesProperty, RingOpacityProperty);

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
        if (StartedAt is not { } start || RingOpacity <= 0.01) return;

        using var _ = ctx.PushOpacity(RingOpacity);
        var cells = Cells;
        var p = Palette;
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

        DrawPendingArc(ctx, c, R, m0 + cells.Count);
    }

    /// <summary>
    /// §8.2.4 承诺弧与截止线。
    ///
    /// **点下「开始」的那一刻就要看得见**（用户 2026-07-27）：从开始时刻到预定的结束
    /// 时刻画一整段灰弧。此前它被画在「有色块」的前提之下，所以任务刚开始、一格都还
    /// 没走完时盘面是空的 —— 看着像没在跑。
    ///
    /// 截止线**必须画成一条线**：只靠弧的边缘不够。偷懒时它往前滑，正是"看着截止线
    /// 离自己越来越远"那个痛感载体，眼睛得追得住。
    /// </summary>
    private void DrawPendingArc(DrawingContext ctx, Point c, Func<double, double> R, double headMinute)
    {
        if (RemainingMinutes <= 0.01) return;
        var lane = Math.Min((int)(headMinute / 60), Lanes.Length - 1);
        var (rIn, rOut) = Lanes[lane];
        var d1 = (headMinute + RemainingMinutes) * 6;

        // 灰色，不是蓝色：这段是"还欠着的时间"，它不该有任何情绪
        var grey = Palette.Tick;
        using (ctx.PushOpacity(0.30))
            ctx.DrawGeometry(new SolidColorBrush(grey), null,
                Annulus(c, R(rIn), R(rOut), headMinute * 6, d1));

        ctx.DrawLine(new Pen(new SolidColorBrush(A(grey, 0xCC)), R(0.014)),
            At(c, R(rIn - 0.02), d1), At(c, R(rOut + 0.02), d1));
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
        var sec = now.Second + now.Millisecond / 1000.0;
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
