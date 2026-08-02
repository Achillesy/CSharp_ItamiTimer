using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ItamiTimer.App;

/// <summary>
/// The row of seven dominoes below the clock — **the day of the week**. However many have
/// fallen is the day (Monday = 1 ... Sunday = 7). **Not a single word on screen** — one
/// glance is enough, nothing to read.
///
/// Pure vector drawing, no images: scales with DPI, recolours with the theme.
///
/// -- Geometry (hard spec set by the user, 2026-07-27) --------------------
///
/// **Ratio 6 : 3 : 1** (height : face width : thickness), **spacing = half the height**.
///
/// **Falls to the right, leaning on the still-standing domino next to it** (per the
/// reference photo: a finger pushes from the left, the wave travels right). Each domino
/// rotates clockwise about its **bottom-right corner**.
///
/// **The tilt angle is computed, not eyeballed.** With two dominoes there's a closed-form
/// solution: the left domino's top-right corner sweeps an arc of radius h, stopping the
/// instant it reaches the left face of the domino to its right; the horizontal distance is
/// exactly the spacing, so `sin θ = gap / h = 1/2 → θ = 30°`. With three or more, the
/// contact surface is no longer vertical, so it's solved by sweeping instead ("rotate from
/// 0°, test convex-polygon intersection, stop at first contact" — the closed form is prone
/// to picking up spurious roots).
///
/// -- Two shortcuts worth noting -------------------------------------------
///
/// 1. **Falling 1 through 6 dominoes shares one array's suffix** (see
///    <see cref="Cascade"/>). The physical reason is that the chain is **right-aligned**:
///    the rightmost domino always leans against the one still standing, and its pose
///    doesn't depend on how many are down to its left. So Monday through Saturday leave the
///    right end completely unchanged; only the left side keeps growing another, flatter
///    domino.
/// 2. **Sunday is a special case**: with all seven down there's nothing left to lean on, so
///    the rightmost one just lies flat at 90°.
///
/// The numbers cross-check each other: the recurrence converges to 75.52°, and the limiting
/// angle for "faces stacked flush" satisfies `cos θ = t / pitch = 1/4`, i.e.
/// `arccos(0.25) = 75.52°`. Two different derivations land on the same number.
///
/// -- Rendering approximation (set by the user, 2026-07-27) ------------------
///
/// **The whole row is mirrored.** The geometry is still computed as "falling to the right"
/// (that's the easiest way to write the recurrence), but x is flipped when drawn to screen:
/// so **the first domino to fall is on the right, falling leftward**, and its shadow
/// naturally falls to the right, matching the dial's own **upper-left** light source — no
/// need to change the dial, and no need to change the light source. The narrowing side face
/// therefore ends up on the left, which happens to be the lit face.
///
/// **The top face is never visible.** The camera height sits level with the top edge of the
/// dominoes; being able to see the top face would imply looking down, which contradicts an
/// eye-level vanishing point. This same fact also dictates that the shadow on the ground can
/// only ever be a thin strip.
///
/// **Side-face width is pinned in pixels**: counting from the left on screen, 6, 5, 4, 3, 2,
/// 1 pixels, and nothing from the seventh domino onward.
/// **The side face catches the light; the front face, facing the screen, is actually
/// slightly darker.**
///
/// **A fallen domino has no side face**: it has rotated sideways, so that face is no longer
/// visible at all.
///
/// **The shadow is one continuous strip of fixed size**, unaffected by how many have
/// fallen. The shadow is drawn first, the dominoes after, and **the whole row is shifted
/// down so its base overlaps the shadow's top edge** — without that overlap the dominoes
/// would look like they're floating above the shadow.
///
/// -- Performance --------------------------------------------------------
///
/// The layout only changes once a day and there are only 7 possible layouts total, so
/// **the geometry is computed once and cached** (<see cref="_cache"/>); repainting just
/// replays the cached list — not a single trig call at runtime.
/// </summary>
public class DominoRow : Control
{
    public static readonly StyledProperty<int> FallenProperty =
        AvaloniaProperty.Register<DominoRow, int>(nameof(Fallen));

    public static readonly StyledProperty<DialPalette> PaletteProperty =
        AvaloniaProperty.Register<DominoRow, DialPalette>(nameof(Palette), DialPalette.Light);

    /// <summary>Number fallen = day of week. 0-7.</summary>
    public int Fallen { get => GetValue(FallenProperty); set => SetValue(FallenProperty, value); }
    public DialPalette Palette { get => GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }

    static DominoRow()
    {
        AffectsRender<DominoRow>(FallenProperty, PaletteProperty);
        (MinX, MaxX, MaxY) = MeasureAllLayouts();
    }

    public const int Count = 7;

    // ---- Domino units: height 6, thickness 1, spacing 3 (= height/2), pitch 4
    private const double H = 6, T = 1, Pitch = 4;

    /// <summary>
    /// Visible side-face width, **pinned directly in pixels** (user, 2026-07-27): the
    /// position (counting from the left) of a still-standing domino determines its base
    /// width — 6, 5, 4, 3, 2, 1 — with nothing for the seventh slot or for any fallen
    /// domino.
    ///
    /// Deliberately not scaled with the layout. This face is only a narrow edge hinting at
    /// thickness in the first place — scaling it proportionally would make it too thin to
    /// see in a small window and as wide as another whole domino in a large one; absolute
    /// pixels are more stable. (Avalonia draws in DIPs, so it still scales with the system
    /// at high DPI — it won't turn into a hairline.)
    /// </summary>
    private static readonly double[] SidePx = [6, 5, 4, 3, 2, 1.5];

    /// <summary>
    /// The lit face's width also has to shrink as <paramref name="fallen"/> grows
    /// (originally ISSUE #12 / DESIGN §16.1): the leftmost domino is **the same physical
    /// domino** the whole week (its world coordinate is always <c>Count-1</c>) — it doesn't
    /// fall until Sunday — so looking it up purely by screen position would keep it fixed
    /// at 6px all week.
    ///
    /// Instead it's windowed by "how many are still standing"
    /// (<c>shift = fallen − 1</c>, clamped to 0): on Monday (fallen=1) the original table
    /// 6...1 is used as-is; each further fall narrows the visible slice by one more tier;
    /// on its own last standing day (Saturday, fallen=6) the width lands exactly at 1px
    /// instead of hitting zero early — **there's no window where it "hasn't fallen yet but
    /// already has no side face"**: running out of width and falling happen on the same day.
    /// </summary>
    private static double SideWidthPx(int i, int fallen)
    {
        var j = Count - 1 - i;                 // After mirroring, position counting from the left on screen (0-based)
        var index = j + Math.Max(0, fallen - 1);
        return index < SidePx.Length ? SidePx[index] : 0;
    }

    /// <summary>
    /// Angles (in degrees) for 1 through 6 fallen. **Taking the last N entries** of this
    /// array is the layout for N fallen. Computed by sweeping 6:3:1 plus "spacing =
    /// height/2" — see the class doc comment.
    /// </summary>
    private static readonly double[] Cascade = [75.13, 74.41, 72.43, 67.12, 54.25, 30.00];

    /// <summary>Sunday: all seven down, nothing left to lean on, the rightmost lies flat.</summary>
    private static readonly double[] Sunday = [75.52, 75.52, 75.52, 75.52, 75.52, 75.96, 90.00];

    /// <summary>Shared bounding box. **All seven days share one scale**, otherwise Monday's dominoes would look noticeably bigger than Sunday's.</summary>
    private static readonly double MinX, MaxX, MaxY;

    /// <summary>How many should be down today. Monday = 1 ... Sunday = 7.</summary>
    public static int FallenForToday(DateTime now)
        => now.DayOfWeek == DayOfWeek.Sunday ? Count : (int)now.DayOfWeek;

    public static ReadOnlySpan<double> Angles(int fallen) => fallen switch
    {
        <= 0 => [],
        >= Count => Sunday,
        _ => Cascade.AsSpan(Cascade.Length - fallen),
    };

    // ---- Cache
    private readonly record struct CacheKey(double W, double H, int Fallen, DialPalette? Palette);
    private CacheKey _key;
    private List<(Geometry Geo, IBrush Brush)> _cache = [];

    public override void Render(DrawingContext ctx)
    {
        var key = new CacheKey(Bounds.Width, Bounds.Height, Math.Clamp(Fallen, 0, Count), Palette);
        if (key != _key) { _cache = Build(key); _key = key; }
        foreach (var (geo, brush) in _cache) ctx.DrawGeometry(brush, null, geo);
    }

    /// <summary>Walks every one of the seven days' poses and takes the union bounding box, used to pin the shared scale.</summary>
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
                }
            }
        }
        return (minX, maxX, maxY);
    }

    /// <summary>The four corners of a domino (world coordinates, y up). Rotated angleDeg clockwise about its bottom-right corner (px,0).</summary>
    private static Point[] Corners(double px, double angleDeg)
    {
        var r = angleDeg * Math.PI / 180;
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        Point Rot(double u, double v) => new(px + u * c + v * s, -u * s + v * c);
        return [Rot(-T, 0), Rot(0, 0), Rot(0, H), Rot(-T, H)];   // bottom-left, bottom-right, top-right, top-left
    }

    private static List<(Geometry, IBrush)> Build(CacheKey k)
    {
        var list = new List<(Geometry, IBrush)>();
        if (k.W <= 0 || k.H <= 0 || k.Palette is not { } p) return list;

        // The scale is shared across all seven days (based on the union bounding box), otherwise Monday's dominoes would look noticeably bigger than Sunday's.
        var scale = Math.Min(k.W / (MaxX - MinX), k.H * 0.94 / MaxY);

        // Converted to an array: a ReadOnlySpan can't be captured by a local function. Only
        // 7 elements, and Build only runs once a day, so the overhead doesn't matter.
        var ang = Angles(k.Fallen).ToArray();
        double AngleAt(int i) => i < ang.Length ? ang[i] : 0;

        // **Left-aligned**: the anchor is this layout's **own** maximum world x (after
        // mirroring, that's the leftmost point on screen), not the union across all seven
        // days. Using the union would leave a big empty gap on the left from Monday through
        // Saturday — that gap is reserved for Sunday's dominoes lying flat, which Monday
        // doesn't have at all.
        // Sunday therefore has its own anchor, but that day has no domino standing at all,
        // so no shift in position is visible (per the user, Sunday is a special case and its
        // position doesn't matter).
        var anchor = double.MinValue;
        for (var i = 0; i < Count; i++)
            foreach (var q in Corners(i * Pitch + T, AngleAt(i)))
                anchor = Math.Max(anchor, q.X);

        // The shadow's span: left end = the leftmost domino's bottom-left corner (= padX on
        // screen); **right end = the rightmost domino's contact point, not a pixel more**
        // (the user's final call, 2026-07-27).
        //
        // A fallen domino only still touches the ground at its bottom-right corner, which is
        // its pivot, always at world coordinate T. This was once changed to "the bottom-right
        // vertex's projection onto the X axis" — that point sits further right than the
        // pivot, so the shadow would poke out slightly past the domino, and with nothing
        // covering that little stretch it exposed a right angle. Real shadows don't end like
        // that. Pulling it back to the pivot means the end is exactly covered by the domino
        // itself.
        var shadowSpan = (anchor - T) * scale;
        var padX = (k.W - shadowSpan) / 2;

        // The whole row shifts down so its heel overlaps the shadow, otherwise it would look like it's floating above the shadow
        var baseY = k.H * 0.965;
        Point S(Point w) => new(padX + (anchor - w.X) * scale, baseY - w.Y * scale);

        // ---- Pass one: the single shadow strip. **One continuous piece, fixed size**,
        //      unaffected by how many have fallen. Its span only covers the stretch from
        //      when **all seven are standing** (a fallen domino's reach doesn't extend the
        //      shadow, since the shadow's size is fixed), and it extends a bit further right
        //      since the light is in the upper-left.
        //      Both top and bottom edges fade out via gradient: a hard edge would read as an
        //      independent grey band, making the dominoes look like they're floating above it.
        {
            // **Governing rule (user, 2026-07-27): the leftmost domino's bottom-left corner
            // = the shadow's bottom-left corner.** So the strip's left boundary is padX, its
            // bottom edge is the heel line baseY, and it extends upward and rightward.
            // Light comes from the left -> there shouldn't be shadow on the left; the ground
            // is seen nearly edge-on -> the further away, the higher up, hence it grows
            // upward.
            //
            // The right end tracks the rightmost domino: once it falls, the shadow shrinks
            // back to around where its sloped face lands, so the right end is anchored to
            // its pivot rather than a fixed full width.
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
                        // **Lighter at the top, darker at the bottom**, but the whole thing
                        // toned down (the user's final call, 2026-07-27).
                        //
                        // What matters isn't the direction but that **the darkest stop must
                        // not land on the bottom edge** — landing there would necessarily cut
                        // a hard edge. So the peak sits at 88%, fading back out over the last
                        // stretch, so the bottom edge itself is never the darkest point and
                        // the boundary smears itself away.
                        // The peak was also lowered from 0x30 to 0x1C: the shadow only needs
                        // to "exist", not to be "heavy".
                        //
                        // Flipping the whole gradient upside down (dark on top, light on
                        // bottom) was tried along the way — the hard edge really did go away,
                        // but it read wrong: the shadow should press down at the feet, not
                        // float around the dominoes' waist.
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.00),
                        new GradientStop(Color.FromArgb(0x0C, 0, 0, 0), 0.50),
                        new GradientStop(Color.FromArgb(0x1C, 0, 0, 0), 0.88),
                        new GradientStop(Color.FromArgb(0x0E, 0, 0, 0), 1.00),
                    }
                }));
        }

        // ---- Pass two: the dominoes. Right to left, so a fallen domino on the left overlaps its right neighbour (drawn on top).
        for (var i = Count - 1; i >= 0; i--)
        {
            var a = AngleAt(i);
            var c = Corners(i * Pitch + T, a);
            var (bl, br, tr, tl) = (S(c[0]), S(c[1]), S(c[2]), S(c[3]));

            // A standing domino gets an extra side face on the right, its width halving
            // domino by domino (vanishing point to the left). **No top face** — the camera
            // sits level with the top edge of the dominoes, so a visible top face would
            // imply looking down, contradicting an eye-level vanishing point.
            if (a < 1e-6)
            {
                // After mirroring, the side face ends up on the **left**, facing the
                // upper-left light source — it's the lit face.
                //
                // **Not the same width top and bottom**: the eye level sits right at the top
                // edge of the dominoes (hence no visible top face), and every line running in
                // depth converges toward it, so this face narrows going upward, nearly
                // meeting at the top. The far bottom corner also has to lift slightly — it's
                // closer to eye level than the near corner.
                var d = SideWidthPx(i, k.Fallen);
                if (d > 0.4)
                {
                    // The vanishing point sits **off-canvas**, roughly at half height, so the
                    // far edge **pulls in at both top and bottom** (not just at the top). How
                    // much it pulls in is proportional to the visible width.
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
