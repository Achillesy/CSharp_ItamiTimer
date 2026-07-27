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

        Save(outDir, "01-跨整点-两格-弧应在外圈", t2359,
            [Cell(0, t2359, 29, 31), Cell(1, t2359, 60, 0)], remaining: 5);

        Save(outDir, "02-同样两格但不跨整点-作为对照", t1010,
            [Cell(0, t1010, 29, 31), Cell(1, t1010, 60, 0)], remaining: 5);

        Save(outDir, "03-刚点开始-零格-整段灰弧", t2359, [], remaining: 5);

        Save(outDir, "04-四种结局各一格", t1010, [
            Cell(0, t1010, 60, 0),            // 全绿
            Cell(1, t1010, 30, 30),           // 一半偷懒
            Cell(2, t1010, 0, 60),            // 全红
            Cell(3, t1010, 0, 0, absent: 60), // 离开：灰
            Cell(4, t1010, 0, 0, gap: 60),    // 无数据：虚线
        ], remaining: 8);

        // 跨圈：58 格已走完，承诺弧还剩 6 分钟 —— 必须在第 58→60 分钟处切到内圈
        var many = new List<MinuteCell>();
        for (var i = 0; i < 58; i++) many.Add(Cell(i, t1010, i % 7 == 0 ? 20 : 60, i % 7 == 0 ? 40 : 0));
        Save(outDir, "05-承诺弧跨圈-末尾应内缩到第二圈", t1010, many, remaining: 6);

        Save(outDir, "06-休息中-色环淡出到三成", t1010,
            [Cell(0, t1010, 60, 0), Cell(1, t1010, 60, 0), Cell(2, t1010, 60, 0)],
            remaining: 0, ringOpacity: 0.3);

        Console.WriteLine($"表盘样张已写入 {outDir}");
    }

    private static MinuteCell Cell(int i, DateTimeOffset t0, double counted, double off,
                                   double absent = 0, double gap = 0)
        => new(i, t0.AddMinutes(i), counted, off, absent, gap);

    private static void Save(string dir, string name, DateTimeOffset startedAt,
                             IReadOnlyList<MinuteCell> cells, double remaining, double ringOpacity = 1.0)
    {
        var dial = new DialControl
        {
            Width = Size,
            Height = Size,
            Palette = DialPalette.Light,
            StartedAt = startedAt,
            Cells = cells,
            RemainingMinutes = remaining,
            RingOpacity = ringOpacity,
        };
        dial.Measure(new Size(Size, Size));
        dial.Arrange(new Rect(0, 0, Size, Size));

        using var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        bmp.Render(dial);
        bmp.Save(Path.Combine(dir, name + ".png"));
    }
}
