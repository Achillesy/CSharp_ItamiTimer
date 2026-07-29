using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// DESIGN.md §8.3.2 —— 任务栏按钮上的色环图标。
///
/// **这是重画的聚合投影，不是把表盘缩小。** 16px 图标上一圈的可用弧长约 41px，
/// 60 格 → 每格 0.68px，逐分钟色块物理上画不出来。所以映射刻意不同：
///   角度 = 已获得专注 / 承诺专注（完成度，不是钟面时间）
///   颜色 = 整段任务到此刻的整体纯度
///
/// 必须加对比描边：Windows 任务栏可浅可深，没有描边总有一种主题下糊掉。
/// </summary>
public static class RingIcon
{
    /// <param name="progress">0~1，画多少圈。</param>
    /// <param name="impurity">0~1，0 全绿、1 全红，经琥珀过渡（§0.4 选项 B）。</param>
    /// <param name="size">位图边长。任务栏按钮实际显示 16~32，这里画大一点让缩放好看。</param>
    public static WindowIcon Make(double progress, double impurity, int size = 64)
        => ToIcon(Render(progress, impurity, size));

    /// <summary>WindowIcon 和界面预览共用同一张位图，不重复画两遍。</summary>
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

            // 底槽：让"还差多少"在任何主题下都看得出
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0x94, 0xA0)), thickness), c, r, r);

            // 进度弧，从 12 点顺时针
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
                // 先画一圈深色描边再画色环 —— 浅色任务栏上不至于糊成一团
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x12, 0x17, 0x1D)), thickness + size * 0.06, lineCap: PenLineCap.Round), geo);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ring), thickness, lineCap: PenLineCap.Round), geo);
            }
        }

        return rtb;
    }
}
