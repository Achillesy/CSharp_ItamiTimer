using Avalonia.Controls;
using Avalonia.Media;

// ImplicitUsings 把 System.IO 也带进来了，那里同样有个 Path。这个文件里的 Path
// 一律是图形那个，用别名钉死，免得每处都写全名。
using Path = Avalonia.Controls.Shapes.Path;

namespace ItamiTimer.App;

/// <summary>
/// 右上角那两个即时开关的图标：喇叭（滴答声）、图钉（置顶）。
///
/// **为什么不再用字体字形**：原来这两个是 Segoe Fluent Icons 的 `` 一类，
/// 而那个字体只有 Windows 装机自带 —— 在 macOS 上整整齐齐两个豆腐块。
///
/// 改成矢量绘制不只是"为了跨平台凑合一下"，它本来就更合这个项目：表盘、番茄、
/// 骨牌、exe 图标全是代码画出来的，**仓库里不放位图**。图标字体是那条纪律上
/// 唯一一个外部美术依赖，去掉它反而把纪律补齐了 —— 而且两个平台从此长得一模一样。
///
/// 状态仍然靠**图形本身和明度双重表达**，不用文字（分割线以上一个字都没有）：
///
/// | | 关 | 开 |
/// |---|---|---|
/// | 喇叭 | 划一道斜杠 | 带两道声波 |
/// | 图钉 | 空心（只描边） | 实心 |
///
/// 图钉那一格**刻意不用"打叉"**：打叉在满不透明度下会读成"置顶被禁用"，
/// 跟"已置顶"正好相反。喇叭划斜杠没有这个歧义 —— 划掉的喇叭全世界都认得是静音。
/// </summary>
public static class ChromeIcons
{
    /// <summary>
    /// 墨色。窗口底色是钉死的浅灰（MainWindow.axaml 的 <c>#D7DBE0</c>），
    /// 不跟随主题，所以这里也用固定的深墨 —— 跟 <see cref="DialPalette.Light"/>
    /// 的 <c>Ink</c> 同一个值，两处是同一支笔。
    /// </summary>
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x2A));

    /// <summary>图标画在 16×16 的格子里，由外面的 Button 缩放和调透明度。</summary>
    private const double Box = 16;

    /// <summary>
    /// 喇叭。箱体 + 号角是一条闭合路径；开着时右边加两道弧，关着时压一道斜杠。
    /// </summary>
    public static Control Speaker(bool on)
    {
        // 箱体（左边那个小方）+ 号角（向右张开的梯形），一笔画完
        const string Body = "M 2,6 L 5,6 L 9,2.5 L 9,13.5 L 5,10 L 2,10 Z";

        var g = new GeometryGroup { FillRule = FillRule.NonZero };
        g.Children.Add(Geometry.Parse(Body));

        var shape = new Path { Data = g, Fill = Ink };

        var canvas = new Canvas { Width = Box, Height = Box };
        canvas.Children.Add(shape);

        if (on)
        {
            // 两道声波。半径拉开一档，读起来才是"在响"而不是"一个圆环"
            canvas.Children.Add(Stroke("M 11,5.5 A 4,4 0 0 1 11,10.5", 1.4));
            canvas.Children.Add(Stroke("M 12.8,3.4 A 7,7 0 0 1 12.8,12.6", 1.4));
        }
        else
        {
            // 斜杠。**从左上压到右下**，正好横穿号角，一眼就是"划掉了"
            canvas.Children.Add(Stroke("M 11,4 L 15,12", 1.6));
        }

        return canvas;
    }

    /// <summary>
    /// 图钉。竖直插着的那种：圆头 + 针身，**开着时填实、关着时只描边**。
    /// 明度由外面的 Button 样式管（关 0.40 / 开 0.95），这里只管形状。
    /// </summary>
    public static Control Pin(bool on)
    {
        // 头（上面那个梯形帽）+ 颈 + 针尖，竖直向下
        const string Data = "M 5.5,2 L 10.5,2 L 9.5,7 L 11.5,9 L 8.6,9 L 8,14.5 " +
                            "L 7.4,9 L 4.5,9 L 6.5,7 Z";

        var shape = new Path
        {
            Data = Geometry.Parse(Data),
            Fill = on ? Ink : null,
            Stroke = on ? null : Ink,
            StrokeThickness = 1.2,
            StrokeJoin = PenLineJoin.Round,
        };

        var canvas = new Canvas { Width = Box, Height = Box };
        canvas.Children.Add(shape);
        return canvas;
    }



    /// <summary>
    /// 齿轮（设置）。八齿 + 中心孔，用 EvenOdd 填充规则把孔挖出来。
    ///
    /// 跟喇叭、图钉一样是**算出来的**，不是字形：齿廓那 32 个点由外径 / 内径 / 齿宽
    /// 三个数推出来（见本文件顶部注释 —— Segoe Fluent Icons 在 macOS 上是豆腐块）。
    ///
    /// 它没有开 / 关两态，所以不吃 <c>bool</c>：设置窗口开着的时候按钮本来就被
    /// 模态挡住了，没有"当前打开着"这个状态要表达。
    /// </summary>
    public static Control Gear()
    {
        const string Outer = "M 14.91,6.85 L 14.91,9.15 L 12.98,9.49 L 12.58,10.47 L 13.70,12.07 L 12.07,13.70 L 10.47,12.58 L 9.49,12.98 L 9.15,14.91 L 6.85,14.91 L 6.51,12.98 L 5.53,12.58 L 3.93,13.70 L 2.30,12.07 L 3.42,10.47 L 3.02,9.49 L 1.09,9.15 L 1.09,6.85 L 3.02,6.51 L 3.42,5.53 L 2.30,3.93 L 3.93,2.30 L 5.53,3.42 L 6.51,3.02 L 6.85,1.09 L 9.15,1.09 L 9.49,3.02 L 10.47,3.42 L 12.07,2.30 L 13.70,3.93 L 12.58,5.53 L 12.98,6.51 Z";
        const string Hole = "M 5.70,8.00 A 2.30,2.30 0 1 0 10.30,8.00 A 2.30,2.30 0 1 0 5.70,8.00 Z";

        var geo = Geometry.Parse(Outer + " " + Hole);
        if (geo is PathGeometry pg) pg.FillRule = FillRule.EvenOdd;

        var canvas = new Canvas { Width = Box, Height = Box };
        canvas.Children.Add(new Path { Data = geo, Fill = Ink });
        return canvas;
    }

    private static Path Stroke(string data, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = Ink,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round,
    };
}
