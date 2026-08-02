using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// The coloured-ring icon on the taskbar button.
///
/// **This is a redrawn aggregate projection, not a shrunken copy of the dial.** A 16px
/// icon's ring has about 41px of usable arc length; 60 cells means 0.68px each --
/// minute-by-minute colour blocks are physically impossible to draw. So the mapping is
/// deliberately different:
///   angle = focus earned / focus committed (completion ratio, not clock time)
///   colour = the whole task's overall purity up to this moment
///
/// A contrasting outline is required: the Windows taskbar can be light or dark, and
/// without an outline it always smears into a blur under one theme or the other.
/// </summary>
public static class RingIcon
{
    /// <param name="progress">0-1, how much of the ring to draw.</param>
    /// <param name="impurity">0-1, 0 = fully green, 1 = fully red, transitioning through amber (§0.4 option B).</param>
    /// <param name="size">The bitmap's side length. The taskbar button actually displays 16-32; this draws a bit larger so scaling looks good.</param>
    public static WindowIcon Make(double progress, double impurity, int size = 64)
        => ToIcon(Render(progress, impurity, size));

    /// <summary>WindowIcon and the UI preview share the same bitmap, not drawn twice.</summary>
    public static WindowIcon ToIcon(RenderTargetBitmap bmp)
    {
        var ms = new MemoryStream();
        bmp.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    public static RenderTargetBitmap Render(double progress, double impurity, int size = 64)
    {
        var focus = Color.FromRgb(0x2F, 0xA3, 0x6B);
        var amber = Color.FromRgb(0xE0, 0xA0, 0x3A);
        var slack = Color.FromRgb(0xD6, 0x45, 0x3F);

        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        Color ring = impurity <= 0.5
            ? Color.FromRgb(Mix(focus.R, amber.R, impurity / 0.5), Mix(focus.G, amber.G, impurity / 0.5), Mix(focus.B, amber.B, impurity / 0.5))
            : Color.FromRgb(Mix(amber.R, slack.R, (impurity - .5) / .5), Mix(amber.G, slack.G, (impurity - .5) / .5), Mix(amber.B, slack.B, (impurity - .5) / .5));

        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var c = new Point(size / 2.0, size / 2.0);
            var r = size * 0.36;
            var thickness = size * 0.18;

            // The base track: makes "how much is left" visible under any theme
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0x94, 0xA0)), thickness), c, r, r);

            // The progress arc, clockwise from 12 o'clock
            if (progress > 0.001)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var sweep = Math.Min(progress, 0.999) * 360;
                    var start = new Point(c.X, c.Y - r);
                    var rad = (sweep - 90) * Math.PI / 180;
                    var end = new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
                    g.BeginFigure(start, false);
                    g.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                // Draws a dark outline ring first, then the coloured ring -- so it doesn't smear on a light taskbar
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x12, 0x17, 0x1D)), thickness + size * 0.06, lineCap: PenLineCap.Round), geo);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ring), thickness, lineCap: PenLineCap.Round), geo);
            }
        }

        return rtb;
    }
}
