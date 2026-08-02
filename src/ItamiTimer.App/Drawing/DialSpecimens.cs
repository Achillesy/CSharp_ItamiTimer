using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// Renders the dial **off-screen** into PNGs across a handful of key states, for a human
/// to eyeball the geometry.
///
/// Why this exists: the dial lives in the App layer, out of reach of Core's test suite --
/// that's exactly how the 2026-07-28 "commitment arc jumps a lap across a whole hour" bug
/// slipped through, ultimately spotted by the user during a real run. Some geometry bugs
/// (radius, angle, layering order) really can only be caught by looking at the picture.
///
/// Usage: <c>ItamiTimer.exe --dial-specimens &lt;output dir&gt;</c>, renders and exits
/// immediately, no window. This is a **debug exit, not a product feature**: the normal
/// startup path isn't touched at all.
/// </summary>
internal static class DialSpecimens
{
    private const int Size = 480;

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // The scene of the bug the user reported: starting at 23:59:00, crossing past 00:00
        var t2359 = new DateTimeOffset(2026, 7, 27, 23, 59, 0, TimeSpan.FromHours(8));
        var t1010 = new DateTimeOffset(2026, 7, 28, 10, 10, 0, TimeSpan.FromHours(8));

        Save(outDir, "01-crosses-the-hour-two-cells-arc-on-outer-ring", t2359,
            [Cell(0, t2359, 29, 31), Cell(1, t2359, 60, 0)], remaining: 5);

        Save(outDir, "02-same-two-cells-within-the-hour-control", t1010,
            [Cell(0, t1010, 29, 31), Cell(1, t1010, 60, 0)], remaining: 5);

        Save(outDir, "03-just-started-zero-cells-full-grey-arc", t2359, [], remaining: 5);

        Save(outDir, "04-one-cell-of-each-outcome", t1010, [
            Cell(0, t1010, 60, 0),            // fully green
            Cell(1, t1010, 30, 30),           // half off-task
            Cell(2, t1010, 0, 60),            // fully red
            Cell(3, t1010, 0, 0, absent: 60), // away: a hollow dashed box (no longer "draws nothing" since 2026-08-02)
            Cell(4, t1010, 0, 0, init: 60),   // never painted: draws nothing (the dashes were handed off to Afk)
        ], remaining: 8);

        // Crossing a lap: 58 cells elapsed, 6 minutes still left in the commitment arc -- must cut to the inner lap right at minute 58->60
        var many = new List<MinuteCell>();
        for (var i = 0; i < 58; i++) many.Add(Cell(i, t1010, i % 7 == 0 ? 20 : 60, i % 7 == 0 ? 40 : 0));
        // Barrel: purity varies continuously from full to zero, checking whether the "short plank" reading holds up (§8.2.3a)
        var barrel = new List<MinuteCell>();
        for (var i = 0; i < 20; i++)
        {
            var counted = 60.0 * (1 - i / 19.0);
            barrel.Add(Cell(i, t1010, counted, 60 - counted));
        }
        Save(outDir, "09-barrel-purity-from-full-to-zero", t1010, barrel, remaining: 6);

        // A more realistic look: most minutes full, a few short planks scattered in
        var real = new List<MinuteCell>();
        double[] mix = [60, 60, 60, 31, 60, 60, 60, 60, 12, 60, 60, 47, 60, 60, 60, 0, 60, 60];
        for (var i = 0; i < mix.Length; i++) real.Add(Cell(i, t1010, mix[i], 60 - mix[i]));
        Save(outDir, "10-barrel-mostly-full-with-a-few-short-staves", t1010, real, remaining: 7);

        Save(outDir, "05-arc-wraps-past-full-circle-tail-spirals-inward", t1010, many, remaining: 6);

        // Rest: **the coloured ring is already gone** (user, 2026-07-28: once the task
        // ends, ActivityWatch isn't queried anymore, so there's nothing to draw), leaving
        // only a wedge on the dial = the time you earned (§8.4.4)
        Save(outDir, "06-on-break-only-the-rest-wedge-remains", t1010,
            [], remaining: 0, restFrom: t1010.AddMinutes(25), restMinutes: 5);

        Save(outDir, "07-rest-wedge-crosses-twelve", t2359,
            [], remaining: 0, restFrom: t2359.AddMinutes(1), restMinutes: 5);

        Save(outDir, "08-rest-wedge-dark-face", t1010,
            [], remaining: 0, restFrom: t1010.AddMinutes(25), restMinutes: 5,
            palette: DialPalette.Dark);

        // Since 2026-08-02 the rest wedge no longer waits for completion to be drawn: its
        // starting point is the wall-clock moment corresponding to the commitment arc's
        // end, previewed from the very start of the task, retreating along with the grey
        // arc while procrastinating (§8.2 / TaskSession.RestFrom). Two comparison shots:
        // 20 minutes in, one is almost entirely focused (the grey arc has only 2 minutes
        // left), the other almost entirely off-task (the grey arc still has 18 minutes) --
        // the starting point, i.e. the wedge, gets pushed far out.
        var focused20 = new List<MinuteCell>();
        for (var i = 0; i < 20; i++) focused20.Add(Cell(i, t1010, 60, 0));
        Save(outDir, "12-rest-wedge-projected-nearly-on-time", t1010, focused20, remaining: 2,
            restFrom: t1010.AddMinutes(22), restMinutes: 5);

        var slacked20 = new List<MinuteCell>();
        for (var i = 0; i < 20; i++) slacked20.Add(Cell(i, t1010, 5, 55));
        Save(outDir, "13-rest-wedge-projected-pushed-back-by-slacking", t1010, slacked20, remaining: 18,
            restFrom: t1010.AddMinutes(38), restMinutes: 5);

        RenderDominoProgression(outDir);

        Console.WriteLine($"Dial specimens written to {outDir}");
    }

    /// <summary>
    /// Stacks the dominoes for all seven days of the week onto one image (Fallen 1-7), to
    /// check §16.1's shrinking lit face: the dial's `--dial-specimens` never covers
    /// <see cref="DominoRow"/>, so its geometry bugs can only be spotted this way.
    /// </summary>
    private static void RenderDominoProgression(string dir)
    {
        const int w = Size, rowH = 90;
        var panel = new StackPanel { Orientation = Orientation.Vertical, Width = w, Background = Avalonia.Media.Brushes.White };
        for (var fallen = 0; fallen <= DominoRow.Count; fallen++)
            panel.Children.Add(new DominoRow { Fallen = fallen, Palette = DialPalette.Light, Width = w, Height = rowH });

        var totalH = rowH * (DominoRow.Count + 1);
        panel.Measure(new Size(w, totalH));
        panel.Arrange(new Rect(0, 0, w, totalH));

        using var bmp = new RenderTargetBitmap(new PixelSize(w, totalH), new Vector(96, 96));
        bmp.Render(panel);
#pragma warning disable CS0618
        bmp.Save(Path.Combine(dir, "11-domino-week-progression.png"));
#pragma warning restore CS0618
    }

    private static MinuteCell Cell(int i, DateTimeOffset t0, double counted, double off,
                                   double absent = 0, double init = 0)
        => new(i, t0.AddMinutes(i), (int)counted, (int)off, (int)absent, 0, (int)init);

    private static void Save(string dir, string name, DateTimeOffset startedAt,
                             IReadOnlyList<MinuteCell> cells, double remaining,
                             DateTimeOffset? restFrom = null, double restMinutes = 0,
                             DialPalette? palette = null)
    {
        // The commitment arc is no longer a scalar -- it's just the span of Gray cells in
        // the buffer (§4.5), so the specimens follow the same convention: whatever's
        // elapsed, followed by `remaining` full Gray cells.
        var all = new List<MinuteCell>(cells);
        for (var i = 0; i < (int)remaining; i++)
        {
            var idx = cells.Count + i;
            all.Add(new MinuteCell(idx, startedAt.AddMinutes(idx), 0, 0, 0, 60, 0));
        }

        var dial = new DialControl
        {
            Width = Size,
            Height = Size,
            Palette = palette ?? DialPalette.Light,
            StartedAt = startedAt,
            Cells = all,
            RestFrom = restFrom,
            RestMinutes = restMinutes,
        };
        dial.Measure(new Size(Size, Size));
        dial.Arrange(new Rect(0, 0, Size, Size));

        using var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        bmp.Render(dial);
        // Avalonia 12 marks the parameterless Save as obsolete; the new overload wants an
        // encoder-options object. This is a debug exit, and default PNG is good enough --
        // not worth bringing in another layer for it.
#pragma warning disable CS0618
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }
}
