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
/// </summary>
public class DialControl : Control
{
    // ---- §8.2.1 分层与半径
    private const double RBezel = 1.02;
    private const double RNumerals = 0.755;
    private const double RTickMinor = 0.94, RTickMajor = 0.90, RTickOuter = 0.965;
    private const double RHour = 0.50, RMinute = 0.72, RSecond = 0.80;
    private const double RHub = 0.045;

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
    {
        AffectsRender<DialControl>(PaletteProperty, CellsProperty, StartedAtProperty,
                                   RemainingMinutesProperty, RingOpacityProperty);
    }

    // 12 点为 0°，顺时针，分钟 × 6°（§8.2）
    private static Point At(Point c, double r, double deg)
    {
        var rad = (deg - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    public override void Render(DrawingContext ctx)
    {
        var p = Palette;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var rFace = size / 2 / RBezel;
        double R(double n) => n * rFace;

        ctx.DrawEllipse(new SolidColorBrush(p.Face), null, c, R(1.0), R(1.0));
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(p.Edge), size * 0.017), c, R(RBezel) - size * 0.01, R(RBezel) - size * 0.01);

        DrawRing(ctx, c, R);
        DrawTicks(ctx, c, R, size);
        DrawNumerals(ctx, c, R, size);
        DrawHands(ctx, c, R, size);
    }

    /// <summary>§8.2.3 + §8.2.4 + §8.2.5：色块、承诺弧、截止线、螺旋。</summary>
    private void DrawRing(DrawingContext ctx, Point c, Func<double, double> R)
    {
        var cells = Cells;
        if (cells.Count == 0 || StartedAt is not { } start || RingOpacity <= 0.01) return;

        using var _ = ctx.PushOpacity(RingOpacity);
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
                // Gap：空心虚线描边，不填色
                var pen = new Pen(new SolidColorBrush(p.Tick), R(0.012))
                { DashStyle = new DashStyle([2, 2], 0) };
                ctx.DrawGeometry(null, pen, Annulus(c, R(rIn), R(rOut), d0 + 0.4, d1 - 0.4));
                continue;
            }

            var fill = cell.AbsentSeconds > cell.TotalSeconds / 2
                ? p.Absent                       // 离开：灰，不是红
                : p.Ramp(1 - cell.Purity);       // 绿 → 琥珀 → 红

            ctx.DrawGeometry(new SolidColorBrush(fill),
                new Pen(new SolidColorBrush(p.Face), R(0.006)),   // 格间细描边，保证 60 格边界可数
                Annulus(c, R(rIn), R(rOut), d0, d1));

            // 色盲的第二信号（§8.2.3）：重度偷懒的格子加一根径向短刻线，可以【数】出来
            if (cell.Purity < 0.34)
            {
                var mid = (d0 + d1) / 2;
                ctx.DrawLine(new Pen(new SolidColorBrush(p.Face), R(0.008)),
                    At(c, R(rIn + 0.02), mid), At(c, R(rOut - 0.02), mid));
            }
        }

        DrawPendingArc(ctx, c, R, m0 + cells.Count);
    }

    /// <summary>
    /// §8.2.4 承诺弧与截止线。截止线**必须画成一条线**——只靠蓝弧的边缘不够，
    /// 偷懒时它往前滑，正是"看着截止线离自己越来越远"那个痛感载体，眼睛得追得住。
    /// </summary>
    private void DrawPendingArc(DrawingContext ctx, Point c, Func<double, double> R, double headMinute)
    {
        if (RemainingMinutes <= 0.01) return;
        var lane = Math.Min((int)(headMinute / 60), Lanes.Length - 1);
        var (rIn, rOut) = Lanes[lane];
        var d0 = headMinute * 6;
        var d1 = d0 + RemainingMinutes * 6;

        using (ctx.PushOpacity(0.22))
            ctx.DrawGeometry(new SolidColorBrush(Palette.Pending), null, Annulus(c, R(rIn), R(rOut), d0, d1));

        ctx.DrawLine(new Pen(new SolidColorBrush(Palette.Pending), R(0.014)),
            At(c, R(rIn - 0.02), d1), At(c, R(rOut + 0.02), d1));
    }

    private void DrawTicks(DrawingContext ctx, Point c, Func<double, double> R, double size)
    {
        for (var i = 0; i < 60; i++)
        {
            var major = i % 5 == 0;
            var pen = new Pen(new SolidColorBrush(major ? Palette.Ink : Palette.Tick),
                              size * (major ? 0.005 : 0.002)) { LineCap = PenLineCap.Round };
            ctx.DrawLine(pen, At(c, R(major ? RTickMajor : RTickMinor), i * 6), At(c, R(RTickOuter), i * 6));
        }
    }

    private void DrawNumerals(DrawingContext ctx, Point c, Func<double, double> R, double size)
    {
        // 只画 12/3/6/9。基线圆 0.755 在色带 [0.50, 0.68] 之外——§8.2.1 把色带内移
        // 就是为了让数字不被色块压住（样板页那版会糊）。
        foreach (var (n, deg) in new[] { (12, 0), (3, 90), (6, 180), (9, 270) })
        {
            var ft = new FormattedText(n.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Georgia"), size * 0.105,
                new SolidColorBrush(Palette.Ink));
            var at = At(c, R(RNumerals), deg);
            ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        }
    }

    /// <summary>
    /// §8.2.6：角度是 <c>纯函数(now)</c>，**不用累加器**。原则 4 在渲染层同样成立，
    /// 而且这样天然免疫掉帧和系统休眠后的漂移。分针也带秒的小数一起平滑走——
    /// 秒针扫而分针跳会显得廉价。
    /// </summary>
    private void DrawHands(DrawingContext ctx, Point c, Func<double, double> R, double size)
    {
        var now = DateTime.Now;
        var sec = now.Second + now.Millisecond / 1000.0;
        var min = now.Minute + sec / 60.0;
        var hour = now.Hour % 12 + min / 60.0;

        void Hand(double deg, double len, double w, Color col)
            => ctx.DrawLine(new Pen(new SolidColorBrush(col), size * w) { LineCap = PenLineCap.Round },
                            At(c, -size * 0.055, deg), At(c, R(len), deg));

        Hand(hour * 30, RHour, 0.026, Palette.Ink);
        Hand(min * 6, RMinute, 0.017, Palette.Ink);
        Hand(sec * 6, RSecond, 0.008, Palette.Sweep);

        ctx.DrawEllipse(new SolidColorBrush(Palette.Ink), null, c, R(RHub), R(RHub));
        ctx.DrawEllipse(new SolidColorBrush(Palette.Face), null, c, R(RHub * 0.4), R(RHub * 0.4));
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
