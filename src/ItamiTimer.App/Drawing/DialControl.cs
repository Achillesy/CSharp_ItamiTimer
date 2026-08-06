using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// The dial.
///
/// **This is a pure rendering layer**: it only consumes a <see cref="MinuteCell"/> list and
/// a handful of scalars, and does no judgment of its own, never touches ActivityWatch, and
/// holds no accumulated value. This is where §8's fourth rule — "state and rendering are
/// separate" — lands: the CLI renders the very same list as ANSI colour blocks, this class
/// renders it as a coloured ring.
///
/// All geometry is normalized to rFace = 1.0 (§8.2.1).
///
/// 2026-07-27: the user pointed at a photo of a real wooden wall clock and noted that what
/// the first version was missing wasn't geometry but **material and light**. Four layers of
/// "physical feel" were added in response: the clock's cast shadow on the wall, the bezel
/// gradient, the bezel's inner shadow cast onto the face, and the hands' own drop shadows.
/// The light source is assumed to be uniformly in the **upper-left**.
/// </summary>
public class DialControl : Control
{
    // ---- §8.2.1 layers and radii (normalized to rFace = 1.0)
    private const double RBezelOut = 1.075;  // Outer edge of the wooden bezel. 1.13 in v1 was too thick, looked like a toy
    private const double RNumerals = 0.745;  // 0.795 crowds the ticks, 0.70 sits too far from them; this is the middle ground
    private const double RTickMinor = 0.918, RTickMajor = 0.893, RTickOuter = 0.955;
    private const double RHour = 0.55, RMinute = 0.775, RSecond = 0.88;
    private const double RAlarm = 0.62;    // Alarm's yellow hand: shorter than the minute hand, slightly longer than the hour hand
    private const double RHub = 0.035;

    /// <summary>
    /// Alarms 清单的小红圈（DESIGN §17）：靠近木框的空白区，介于刻度终点（0.955）和
    /// 表盘边缘（1.0）之间——跟 OffTask 色环（0.50~0.68）、闹钟黄针（0.62）都不在同一层，
    /// 不会被看成同一件事。
    /// </summary>
    private const double RAlarmsDot = 0.98;
    private const double RAlarmsDotRadius = 0.018;

    /// <summary>Outer edge of the rest wedge. Sits inside the tick ring so it doesn't cover the numerals (§8.4.4).</summary>
    private const double RestWedgeOuter = 0.70;

    /// <summary>
    /// The minimum height of the barrel-stave short plank (as a fraction of the band's
    /// radial width). **Half height** (user, 2026-07-28).
    ///
    /// At real-world size a cell's radial span is only about 25px; at 0.18 the shortest
    /// plank would be down to 4-5px — after looking at it, the user's verdict was "don't
    /// let the red short plank get so small it's invisible". At 1/2, a minute at zero
    /// purity is still as prominent as half a plank, and there's still a full one-plank
    /// gap between the fullest and the shortest, so the unevenness is still legible at a
    /// glance.
    ///
    /// **Must never be 0**: "absent" now draws nothing at all (§8.2.3a), and a zero height
    /// would collide with that — and that's exactly the one pair that must never be
    /// confused: one isn't your fault, the other one entirely is.
    /// </summary>
    private const double StaveFloor = 0.5;

    /// <summary>
    /// §8.3: the spiral has **only two turns**. The buffer's drawable span is exactly
    /// 120 minutes (= two turns); beyond that, archiving takes over (§4.4) — once every
    /// hour, the inner turn's content jumps out to the outer turn as a whole and the inner
    /// turn is cleared.
    ///
    /// ⚠️ There used to be a third turn, `(0.14, 0.26)`, but `ToMinuteCells` yields at most
    /// 120 cells and `Index/60` maxes out at 1, so that turn was **never reachable**.
    /// Removed 2026-08-02.
    /// </summary>
    private static readonly (double In, double Out)[] Lanes =
    [
        (0.50, 0.68),   // Minutes 0-60
        (0.31, 0.46),   // 60-120
    ];

    public static readonly StyledProperty<DialPalette> PaletteProperty =
        AvaloniaProperty.Register<DialControl, DialPalette>(nameof(Palette), DialPalette.Light);

    /// <summary>The ring's content. An empty list = an empty dial = the invitation for the next round (§8.4.5a).</summary>
    public static readonly StyledProperty<IReadOnlyList<MinuteCell>> CellsProperty =
        AvaloniaProperty.Register<DialControl, IReadOnlyList<MinuteCell>>(nameof(Cells), []);

    /// <summary>The task's start time, deciding which minute mark on the dial the ring starts drawing from (§8.2.2: the minute hand IS the write head).</summary>
    public static readonly StyledProperty<DateTimeOffset?> StartedAtProperty =
        AvaloniaProperty.Register<DialControl, DateTimeOffset?>(nameof(StartedAt));

    /// <summary>The rest wedge's starting point (= the moment focus was achieved). Null = not resting, draw nothing.</summary>
    public static readonly StyledProperty<DateTimeOffset?> RestFromProperty =
        AvaloniaProperty.Register<DialControl, DateTimeOffset?>(nameof(RestFrom));

    /// <summary>Rest length in minutes = commitment length ÷ 5.</summary>
    public static readonly StyledProperty<double> RestMinutesProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(RestMinutes));

    /// <summary>The alarm time, as total minutes from 0:00 (0-719, 12-hour clock).</summary>
    public static readonly StyledProperty<double> AlarmMinutesProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(AlarmMinutes));

    /// <summary>
    /// Alarms 清单下一条触发时间的角度位置（0-719 分钟），null = 不画。**由调用方每次都
    /// 整个重算**（<see cref="AlarmsList.DotPosition"/>），跟表盘上其它一切一样，不需要
    /// 任何"清除"逻辑——上一拍没有满足条件，这一拍自然不画。
    /// </summary>
    public static readonly StyledProperty<double?> AlarmsDotMinutesProperty =
        AvaloniaProperty.Register<DialControl, double?>(nameof(AlarmsDotMinutes));

    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public IReadOnlyList<MinuteCell> Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public DateTimeOffset? StartedAt { get => GetValue(StartedAtProperty); set => SetValue(StartedAtProperty, value); }
    public DateTimeOffset? RestFrom { get => GetValue(RestFromProperty); set => SetValue(RestFromProperty, value); }
    public double RestMinutes { get => GetValue(RestMinutesProperty); set => SetValue(RestMinutesProperty, value); }
    public double AlarmMinutes { get => GetValue(AlarmMinutesProperty); set => SetValue(AlarmMinutesProperty, value); }
    public double? AlarmsDotMinutes { get => GetValue(AlarmsDotMinutesProperty); set => SetValue(AlarmsDotMinutesProperty, value); }

    static DialControl()
        => AffectsRender<DialControl>(PaletteProperty, CellsProperty, StartedAtProperty,
                                      RestFromProperty, RestMinutesProperty,
                                      AlarmMinutesProperty, AlarmsDotMinutesProperty);

    // 12 o'clock is 0°, clockwise, minute × 6° (§8.2)
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

        // Leave headroom for the drop shadow, otherwise it gets clipped by the control's bounds
        var rFace = box / 2 / (RBezelOut + 0.10);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 - rFace * 0.03);
        double R(double n) => n * rFace;

        DrawDropShadow(ctx, c, R, rFace);
        DrawBezel(ctx, c, R);
        DrawFace(ctx, c, R);
        DrawRestWedge(ctx, c, R);   // **Below** the ring: it's a patch of light on the background, not a record
        DrawRing(ctx, c, R);
        DrawTicks(ctx, c, R, rFace);
        DrawNumerals(ctx, c, R, rFace);
        DrawHands(ctx, c, R, rFace);
        DrawAlarmsDot(ctx, c, R);
    }

    /// <summary>
    /// Alarms 清单下一条触发时间的小红圈（DESIGN §17）。<see cref="AlarmsDotMinutes"/> 为
    /// null 就什么都不画——不存在"清除上一次画的圆"这回事，跟表盘上其它一切一样，每一拍
    /// 整个重画，条件不满足这一拍的结果直接就是"不画"。
    /// </summary>
    private void DrawAlarmsDot(DrawingContext ctx, Point c, Func<double, double> R)
    {
        if (AlarmsDotMinutes is not { } minutes) return;
        var deg = (minutes % AlarmClock.FaceMinutes) / 2.0;   // 720 分钟 = 360°，跟黄针同一个换算
        var at = At(c, R(RAlarmsDot), deg);
        ctx.DrawEllipse(new SolidColorBrush(Palette.AlarmsDot), null, at, R(RAlarmsDotRadius), R(RAlarmsDotRadius));
    }

    /// <summary>The clock's cast shadow on the wall. Squashed, offset downward, fading from black to transparent.</summary>
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

    /// <summary>The wooden bezel. A linear gradient lit from the upper-left and dark toward the lower-right, so it reads as a physical ring with thickness.</summary>
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

        // The rounded wooden bezel gets a bright rim at its very outer edge (the curved-away
        // surface is still catching light) — without this rim the whole ring reads as "a
        // flat brown disc" instead of "a ring of wood with thickness"
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(A(p.BezelLit, 0x70)), R(0.012)),
            c, R(RBezelOut) - R(0.008), R(RBezelOut) - R(0.008));

        // A dark inner line separating the bezel from the face
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(A(Shadow, 0x44)), R(0.014)), c, R(1.008), R(1.008));
    }

    /// <summary>The face: a very faint upper-left highlight (glass feel) plus the bezel's inner shadow cast onto it.</summary>
    private void DrawFace(DrawingContext ctx, Point c, Func<double, double> R)
    {
        var p = Palette;

        // The face itself, slightly brighter at the centre, slightly darker at the rim
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

        // The shadow the bezel casts onto the face: only a ring at the inner edge, deepening toward the rim
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

    /// <summary>§8.2.3 + §8.2.4 + §8.2.5: the coloured cells, the commitment arc, the deadline line, the spiral.</summary>
    private void DrawRing(DrawingContext ctx, Point c, Func<double, double> R)
    {
        if (StartedAt is not { } start) return;

        var cells = Cells;
        var p = Palette;
        // The angular position of the start time on the dial (in minutes). startedAt is
        // already truncated to a whole minute (§14.1), so Second is always 0; this term is
        // kept in case the start point is no longer aligned in the future, so it doesn't
        // silently render wrong.
        var m0 = start.Minute + start.Second / 60.0;

        foreach (var cell in cells)
        {
            var lane = Math.Min(cell.Index / 60, Lanes.Length - 1);
            var (rIn, rOut) = Lanes[lane];
            var d0 = (m0 + cell.Index) * 6;
            var d1 = d0 + 6;

            // ---- §4.6 colouring. **Only maps "tier -> how to draw it"**: what a cell
            //      should be read as is decided by cell.Tier (the judgment layer, the rule
            //      is written in exactly one place) — this only cares about appearance.
            //      The colour blocks are purely cosmetic, not the ledger — what actually
            //      drives judgment is the deficit in §4.5.
            //
            // Height follows colour, encoding the same quantity: one is for normal vision,
            // the other is for everyone (the barrel-stave short-plank idea from D1, just
            // switched from continuous to discrete). Floor is 1/2, never 0 (D2) — a zero
            // height would collide with "don't draw it", and that's the one pair that must
            // never be confused.
            switch (cell.Tier)
            {
                case CellTier.FocusFull: Stave(ctx, c, R, rIn, rOut, d0, d1, p.Focus, 1.00); break;
                case CellTier.FocusMid: Stave(ctx, c, R, rIn, rOut, d0, d1, p.Ramp(0.5), 0.80); break;
                case CellTier.FocusLow: Stave(ctx, c, R, rIn, rOut, d0, d1, p.Ramp(0.8), 0.60); break;
                case CellTier.OffTask: Stave(ctx, c, R, rIn, rOut, d0, d1, p.Slack, 0.50); break;

                // Away: a hollow dashed box, full height. Brought back on 2026-08-02
                // (reversing D3) — it used to draw nothing at all; now it gets a **hollow**
                // box: a shape with no fill, reading as "this stretch of time exists, but
                // belongs to neither side". The `Absent` token has been sitting there as a
                // semantic placeholder since 07-28, waiting for this day.
                case CellTier.Away:
                    ctx.DrawGeometry(null,
                        new Pen(new SolidColorBrush(p.Absent), R(0.012))
                        { DashStyle = new DashStyle([2, 2], 0) },
                        Annulus(c, R(rIn), R(rOut), d0 + 0.4, d1 - 0.4));
                    break;

                // The commitment arc: **no longer computed separately** — it's just the
                // Gray span in the buffer (§4.5), riding the same projection and the same
                // coordinates as the coloured cells. Full-height grey happens to double as
                // the reference line for "this minute is completely full".
                case CellTier.Pending:
                    using (ctx.PushOpacity(0.30))
                        ctx.DrawGeometry(new SolidColorBrush(p.Tick), null,
                            Annulus(c, R(rIn), R(rOut), d0, d1));
                    break;

                    // CellTier.NotDrawn: draw nothing. Only two things land here — a hole
                    // left by a missed tick, or blank space beyond the commitment arc.
                    // Neither deserves any visual real estate.
            }
        }
    }

    /// <summary>
    /// One plank: grown from the inner edge outward, its height being
    /// <paramref name="height"/>. The inner ring stays a clean circle; the uneven edge
    /// faces the tick marks.
    /// </summary>
    private void Stave(DrawingContext ctx, Point c, Func<double, double> R,
                       double rIn, double rOut, double d0, double d1, Color tint, double height)
    {
        var top = rIn + (rOut - rIn) * Math.Max(StaveFloor, height);
        ctx.DrawGeometry(new SolidColorBrush(tint),
            new Pen(new SolidColorBrush(A(Palette.Face, 0xCC)), R(0.005)),
            Annulus(c, R(rIn), R(top), d0, d1));
    }

    // §8.2.4's commitment arc used to be computed separately here (`RemainingMinutes` plus
    // slicing by lap). Removed 2026-08-02: the commitment arc is now just the Gray span in
    // the buffer (§4.5), riding the same projection as the coloured cells. One quantity
    // computed two different ways always drifts apart eventually — and this time it really
    // did (§15.1).

    /// <summary>
    /// §8.4.4 the rest wedge: **the block of time you earned**.
    ///
    /// Since 2026-08-02 it no longer waits for completion to be drawn — its starting point
    /// is <see cref="RestFrom"/>, which is a projected value while the task is in progress
    /// (the wall-clock time corresponding to the commitment arc's end, computed in
    /// <c>TaskSession</c>), and only locks to the actual moment once focus is achieved. So
    /// there's a preview from the very start of the task; while you procrastinate it
    /// retreats along with the commitment arc — a deliberate design for that sense of pain.
    ///
    /// The user replaced the old "ring fades out linearly" scheme on 2026-07-28: that
    /// approach changed opacity every second, carried state, had to line up exactly with
    /// the rest length, and in practice was never actually visible (under the debug range
    /// at the time, `RestMinutes = 2/5 = 0`, so there was no rest phase at all). It's now a
    /// single static wedge.
    ///
    /// **Why a solid wedge cut from the centre, instead of yet another thin ring band**:
    /// everything else on the dial is a thin band (the coloured cells, the commitment arc);
    /// adding another one would need colour to tell them apart, and colour's four tiers
    /// (green / amber / red / grey) are already fully spoken for, each with its own
    /// meaning. **Shape is all that's left.** A solid wedge cut from the centre reads as
    /// "this piece is now yours" — categorically different from "a record", so it can't be
    /// mistaken for one.
    ///
    /// **Why it doesn't shrink, fade, or show a countdown**: the minute hand is already
    /// sweeping. The instant the minute hand has swept this wedge away, the rest is over.
    /// The countdown comes **for free**, with no extra animation state needed — this is the
    /// second time "the minute hand IS the write head" (§8.2.2) gives us something for
    /// nothing.
    ///
    /// **Why not grey**: grey already carries meaning on this dial — `Absent` (not present)
    /// and the commitment arc (time still owed) are both grey. Drawing a reward in grey
    /// would make it look like a debt. Green / amber / red each already have an owner, and
    /// **blue is the only hue still free** — and it naturally reads as "take a break".
    /// (Blue, `Pending`, was originally proposed for the commitment arc and then rejected,
    /// on the grounds that "time still owed shouldn't carry emotion" — whereas the rest
    /// wedge is exactly where that emotion belongs.)
    ///
    /// Uses a **radial gradient**: dense near the rim, fading to nothing toward the centre.
    /// This avoids obscuring the hand pivot and the hour hand, and makes it read as a beam
    /// of light falling on the face rather than a patch slapped on top.
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

        // A slightly denser edge at the rim: "closes off" the wedge, otherwise the
        // gradient's outer edge would smear into a blur
        ctx.DrawGeometry(new SolidColorBrush(A(tint, 0x88)), null,
            Annulus(c, R(RestWedgeOuter - 0.035), rOut, d0, d1));
    }

    /// <summary>A wedge [d0, d1) cut from the centre.</summary>
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
        // All twelve numerals are drawn. §8.2.1 originally called for only 12/3/6/9
        // (copied from an abandoned mockup page), but the physical reference photo the
        // user provided has all twelve, and four numerals against a dense tick ring would
        // look sparse. The coloured band sits at [0.50, 0.68] and the numerals at 0.795, so
        // the two don't collide — this change doesn't affect any already-settled geometry.
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
    /// §8.2.6: the angle is a <c>pure function(now)</c>, **no accumulator**. Principle 4
    /// holds in the rendering layer too, and this makes it naturally immune to dropped
    /// frames and drift after the system sleeps. The minute hand also moves smoothly with
    /// the seconds' fractional part — a sweeping second hand next to a jumping minute hand
    /// would look cheap.
    ///
    /// Each hand is first drawn once, offset, in translucent black — that's its shadow cast
    /// onto the face (light source in the upper-left).
    /// </summary>
    private void DrawHands(DrawingContext ctx, Point c, Func<double, double> R, double rFace)
    {
        var now = DateTime.Now;
        // **The second hand jumps once a second** (user, 2026-07-28). It used to sweep
        // continuously at sub-second resolution (§8.2.6), but a clock whose second hand
        // sweeps doesn't tick — sweeping comes from a continuously-driven movement, ticking
        // comes from a step escapement, and in the real world these two are mutually
        // exclusive. Since we want ticking, the second hand has to step along with it,
        // otherwise the sound and the picture would be telling two different stories. The
        // 33ms repaint stays: its job now is just keeping the jump timely (≤33ms latency).
        var sec = (double)now.Second;
        var min = now.Minute + sec / 60.0;
        var hour = now.Hour % 12 + min / 60.0;

        // Shadow offset: light source in the upper-left, shadow falls to the lower-right
        var shift = Matrix.CreateTranslation(rFace * 0.014, rFace * 0.018);
        var shadowBrush = new SolidColorBrush(A(Shadow, 0x38));

        // Alarm's yellow hand: angle computed from AlarmMinutes (720 minutes = 360°)
        var alarmDeg = (AlarmMinutes % 720) / 2.0;
        var alarmGeo = Taper(c, alarmDeg, R(RAlarm), rFace * 0.024, rFace * 0.006, rFace * 0.06);

        var hourGeo = Taper(c, hour * 30, R(RHour), rFace * 0.030, rFace * 0.013, rFace * 0.10);
        var minGeo = Taper(c, min * 6, R(RMinute), rFace * 0.022, rFace * 0.008, rFace * 0.10);
        var secPen = new Pen(new SolidColorBrush(Palette.Sweep), rFace * 0.008) { LineCap = PenLineCap.Round };
        var secShadowPen = new Pen(shadowBrush, rFace * 0.008) { LineCap = PenLineCap.Round };
        var tail = At(c, -rFace * 0.16, sec * 6);
        var tip = At(c, R(RSecond), sec * 6);

        // Draw the three hands' shadows plus the alarm hand's shadow together
        using (ctx.PushTransform(shift))
        {
            ctx.DrawGeometry(shadowBrush, null, alarmGeo);
            ctx.DrawGeometry(shadowBrush, null, hourGeo);
            ctx.DrawGeometry(shadowBrush, null, minGeo);
            ctx.DrawLine(secShadowPen, tail, tip);
        }

        // The yellow hand is drawn **before** the hour hand, so it being covered by the hour hand = due time reached
        ctx.DrawGeometry(new SolidColorBrush(Palette.Alarm), null, alarmGeo);
        ctx.DrawGeometry(new SolidColorBrush(Palette.Ink), null, hourGeo);
        ctx.DrawGeometry(new SolidColorBrush(Palette.Ink), null, minGeo);
        // Second hand: thin, light, its own colour. It's decoration and shouldn't compete with the hour/minute hands (§8.2.6)
        ctx.DrawLine(secPen, tail, tip);

        ctx.DrawEllipse(new SolidColorBrush(Palette.Ink), null, c, R(RHub), R(RHub));
        ctx.DrawEllipse(new SolidColorBrush(A(Palette.Face, 0x99)), null, c, R(RHub * 0.34), R(RHub * 0.34));
    }

    /// <summary>A tapered hand: wide at the base, narrow at the tip, with a small counterweight tail.</summary>
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

    /// <summary>An annular sector [d0, d1).</summary>
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
