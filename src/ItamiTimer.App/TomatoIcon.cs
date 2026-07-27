using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ItamiTimer.App;

/// <summary>
/// 折纸番茄 —— 应用图标（用户 2026-07-27 给的参考实物：一张折纸番茄）。
///
/// 一个番茄钟用番茄做图标是本分。**任务进行中**任务栏图标换成 <see cref="RingIcon"/>
/// 的进度色环（§8.3.2）；**空闲时**就是这颗番茄。
///
/// 跟表盘和骨牌一样是纯矢量：几个多边形，没有位图资源，任何尺寸都清晰。
///
/// **配色约束（用户定）：标准红 + 标准绿，总用色不超过 8 种。**
/// 实际用了 6 种：红三档（受光/本体/暗部）、绿三档。**不描边**——折纸是纯色块
/// 拼出来的，加黑线就变成插画了。折纸的层次感靠
/// **面与面的明暗差**做出来，不用渐变——渐变在 16px 的任务栏图标上会糊成一团，
/// 而纯色块缩到再小也还能看出是个番茄。
///
/// 光源与表盘、骨牌统一在**左上**：左半边受光，右半边压暗。
/// </summary>
public static class TomatoIcon
{
    // ---- 7 种颜色，一个不多
    private static readonly Color RedLit = Color.FromRgb(0xE8, 0x5A, 0x4A);
    private static readonly Color Red = Color.FromRgb(0xD6, 0x33, 0x27);
    private static readonly Color RedDark = Color.FromRgb(0xA3, 0x20, 0x16);
    private static readonly Color GreenLit = Color.FromRgb(0x5C, 0xC4, 0x6E);
    private static readonly Color Green = Color.FromRgb(0x2E, 0x9E, 0x4F);
    private static readonly Color GreenDark = Color.FromRgb(0x1B, 0x6B, 0x36);

    public static WindowIcon Make(int size = 128) => RingIcon.ToIcon(Render(size));

    public static RenderTargetBitmap Render(int size = 128)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            double S(double v) => v * size;
            Point P(double x, double y) => new(S(x), S(y));

            void Poly(Color fill, params (double X, double Y)[] pts)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(P(pts[0].X, pts[0].Y), true);
                    for (var i = 1; i < pts.Length; i++) g.LineTo(P(pts[i].X, pts[i].Y));
                    g.EndFigure(true);
                }
                // 不描边：折纸是纯色块拼的，黑线一加就变成插画而不是折纸
                ctx.DrawGeometry(new SolidColorBrush(fill), null, geo);
            }

            // ---- 果身：先整块画满，绿色压在上面。
            // 这样从结构上就不可能露白 —— 之前按「顶部咬一个 V」去描边界，
            // 折叶和果身之间总会差出一条缝。
            // 顶上两个肩收进绿叶的轮廓【以内】—— 露在绿色外面的红角会让它看着像
            // 一顶帽子扣在球上，而不是一个从萼片下长出来的果子
            Poly(RedLit,
                (0.20, 0.31), (0.50, 0.27), (0.50, 0.88), (0.19, 0.88), (0.04, 0.66), (0.06, 0.42));
            Poly(Red,
                (0.80, 0.31), (0.94, 0.42), (0.96, 0.66), (0.81, 0.88), (0.50, 0.88), (0.50, 0.27));
            // 底部一道暗，让它坐得住
            Poly(RedDark, (0.19, 0.88), (0.81, 0.88), (0.72, 0.94), (0.28, 0.94));

            // ---- 萼片：从顶上垂下的 V，压在果身上。顶端削平一小段，不做成尖角
            Poly(GreenLit, (0.44, 0.09), (0.50, 0.09), (0.50, 0.52), (0.33, 0.29), (0.14, 0.28));
            Poly(Green,    (0.56, 0.09), (0.50, 0.09), (0.50, 0.52), (0.67, 0.29), (0.86, 0.28));
            // 折过来的两片，比萼片本体暗一档 —— 这是「折」读出来的关键
            Poly(GreenDark, (0.14, 0.28), (0.33, 0.29), (0.30, 0.47), (0.11, 0.38));
            Poly(GreenDark, (0.86, 0.28), (0.67, 0.29), (0.70, 0.47), (0.89, 0.38));
        }
        return rtb;
    }
}
