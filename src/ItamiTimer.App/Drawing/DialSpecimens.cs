using Avalonia;
using Avalonia.Media.Imaging;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// 把表盘在几个关键状态下**离屏**渲染成 PNG，供人眼核对几何。
///
/// 存在的理由：表盘在 App 层，Core 那套测试碰不到它 —— 2026-07-28 的"承诺弧跨整点
/// 跳圈"就是这么漏出去的，最后是用户在真实运行里肉眼发现的。有些几何错误（半径、
/// 角度、叠放次序）也确实只有看图才发现得了。
///
/// 用法：<c>ItamiTimer.exe --dial-specimens &lt;输出目录&gt;</c>，渲染完直接退出，不开窗口。
/// 这是**调试出口，不是产品功能**：正常启动的路径一个字节都没碰。
/// </summary>
internal static class DialSpecimens
{
    private const int Size = 480;

    public static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // 用户报的那个 bug 的现场：23:59:00 起算，走过 00:00
        var t2359 = new DateTimeOffset(2026, 7, 27, 23, 59, 0, TimeSpan.FromHours(8));
        var t1010 = new DateTimeOffset(2026, 7, 28, 10, 10, 0, TimeSpan.FromHours(8));

        Save(outDir, "01-crosses-the-hour-two-cells-arc-on-outer-ring", t2359,
            [Cell(0, t2359, 29, 31), Cell(1, t2359, 60, 0)], remaining: 5);

        Save(outDir, "02-same-two-cells-within-the-hour-control", t1010,
            [Cell(0, t1010, 29, 31), Cell(1, t1010, 60, 0)], remaining: 5);

        Save(outDir, "03-just-started-zero-cells-full-grey-arc", t2359, [], remaining: 5);

        Save(outDir, "04-one-cell-of-each-outcome", t1010, [
            Cell(0, t1010, 60, 0),            // 全绿
            Cell(1, t1010, 30, 30),           // 一半偷懒
            Cell(2, t1010, 0, 60),            // 全红
            Cell(3, t1010, 0, 0, absent: 60), // 离开：虚线空心框（2026-08-02 起不再是「什么都不画」）
            Cell(4, t1010, 0, 0, init: 60),   // 没画过：什么都不画（虚线已经让给 Afk 了）
        ], remaining: 8);

        // 跨圈：58 格已走完，承诺弧还剩 6 分钟 —— 必须在第 58→60 分钟处切到内圈
        var many = new List<MinuteCell>();
        for (var i = 0; i < 58; i++) many.Add(Cell(i, t1010, i % 7 == 0 ? 20 : 60, i % 7 == 0 ? 40 : 0));
        // 木桶：纯度从满到零连续变化，看"短板"这个读法成不成立（§8.2.3a）
        var barrel = new List<MinuteCell>();
        for (var i = 0; i < 20; i++)
        {
            var counted = 60.0 * (1 - i / 19.0);
            barrel.Add(Cell(i, t1010, counted, 60 - counted));
        }
        Save(outDir, "09-barrel-purity-from-full-to-zero", t1010, barrel, remaining: 6);

        // 真实一点的样子：多数分钟满格，偶尔几块短板
        var real = new List<MinuteCell>();
        double[] mix = [60, 60, 60, 31, 60, 60, 60, 60, 12, 60, 60, 47, 60, 60, 60, 0, 60, 60];
        for (var i = 0; i < mix.Length; i++) real.Add(Cell(i, t1010, mix[i], 60 - mix[i]));
        Save(outDir, "10-barrel-mostly-full-with-a-few-short-staves", t1010, real, remaining: 7);

        Save(outDir, "05-arc-wraps-past-full-circle-tail-spirals-inward", t1010, many, remaining: 6);

        // 休息：**色环已经撤掉**（用户 2026-07-28：任务结束就不查 AW 了，不用画），
        // 盘面上只剩一块扇形 = 你挣来的时间（§8.4.4）
        Save(outDir, "06-on-break-only-the-rest-wedge-remains", t1010,
            [], remaining: 0, restFrom: t1010.AddMinutes(25), restMinutes: 5);

        Save(outDir, "07-rest-wedge-crosses-twelve", t2359,
            [], remaining: 0, restFrom: t2359.AddMinutes(1), restMinutes: 5);

        Save(outDir, "08-rest-wedge-dark-face", t1010,
            [], remaining: 0, restFrom: t1010.AddMinutes(25), restMinutes: 5,
            palette: DialPalette.Dark);

        Console.WriteLine($"Dial specimens written to {outDir}");
    }

    private static MinuteCell Cell(int i, DateTimeOffset t0, double counted, double off,
                                   double absent = 0, double init = 0)
        => new(i, t0.AddMinutes(i), (int)counted, (int)off, (int)absent, 0, (int)init);

    private static void Save(string dir, string name, DateTimeOffset startedAt,
                             IReadOnlyList<MinuteCell> cells, double remaining,
                             DateTimeOffset? restFrom = null, double restMinutes = 0,
                             DialPalette? palette = null)
    {
        // 承诺弧不再是一个标量——它就是 buffer 里那段 Gray 格子（§4.5），
        // 所以样张也照着接：已走过的格子后面再挂 remaining 个满格 Gray。
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
        // Avalonia 12 把无参 Save 标了过时，新重载要一个编码器选项对象。
        // 这里是调试出口，默认 PNG 就够，不值得为它引一层。
#pragma warning disable CS0618
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }
}
