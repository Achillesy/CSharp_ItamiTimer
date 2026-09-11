using System.Text.Json;
using ItamiTimer.App;

namespace ItamiTimer.App.Tests;

/// <summary>
/// 窗口的两套尺寸（DESIGN §8.10，3.8.0）。
///
/// ⚠️ **这里只准碰 <see cref="WindowLayout.Parse"/> 和 <see cref="WindowLayout.Of"/>**：
/// 它们是纯函数。<see cref="WindowLayout.Mode"/> / <see cref="WindowLayout.Current"/> 会去读
/// 用户真实配置目录下的 `layout.json` 并写日志——单元测试碰它就等于让结果取决于跑测试那台
/// 机器上有没有那个文件（`Mode` 特意用 <c>Lazy</c> 就是为了让这里不被牵连，DECISIONS I5
/// 那类事故的形状）。
/// </summary>
public class WindowLayoutTests
{
    // ---------------------------------------------------------------- 认字

    [Fact]
    public void 只认得compact这一个值()
    {
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse(Json("compact")));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse(Json("standard")));
    }

    [Fact]
    public void 键名和值都不分大小写_值的首尾空白也不管()
    {
        // PropertyNameCaseInsensitive 管键名，Trim + OrdinalIgnoreCase 管值。
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse("""{ "Layout": "Compact" }"""));
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse("""{ "LAYOUT": "COMPACT" }"""));
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse("""{ "layout": "  compact  " }"""));
    }

    [Fact]
    public void 手写的json_注释和尾逗号都要能过()
    {
        // ⚠️ 这是这个文件唯一真正危险的地方：说明书里带着注释，少了
        // JsonCommentHandling.Skip 就整个解析失败——§15.4 那个「写了注释就静默失效」。
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse("""
            {
              // standard 或 compact
              "layout": "compact",
            }
            """));
    }

    [Fact]
    public void 空的_没这个键_值写错_一律标准档()
    {
        // 一个可选的外观开关，不值得为它拒绝启动，也不值得弹任何东西。
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse(null));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse(""));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse("   "));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse("{}"));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse("""{ "other": "compact" }"""));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse(Json("紧凑")));
        Assert.Equal(LayoutMode.Standard, WindowLayout.Parse(Json("compactx")));   // 不做前缀匹配
    }

    [Fact]
    public void json语法坏了要抛_不在Parse里吞掉()
    {
        // 「值写了个错别字」和「整个文件语法坏了」是两回事：后者值得在日志里留一行，
        // 由 WindowLayout.Load 兜住（那边照旧退回标准档）。
        Assert.ThrowsAny<JsonException>(() => WindowLayout.Parse("{ oops"));
    }

    private static string Json(string value) => $$"""{ "layout": "{{value}}" }""";

    // ---------------------------------------------------------------- 不透明度（3.9.0）

    private const double Default = WindowLayout.DefaultOpacityPercent / 100.0;

    [Fact]
    public void 百分数直接换算成零到一()
    {
        Assert.Equal(0.50, WindowLayout.ParseOpacity("""{ "opacity": 50 }"""), 6);
        Assert.Equal(0.75, WindowLayout.ParseOpacity("""{ "opacity": 75 }"""), 6);
    }

    [Fact]
    public void 上下界都是闭区间()
    {
        Assert.Equal(0.10, WindowLayout.ParseOpacity("""{ "opacity": 10 }"""), 6);
        Assert.Equal(1.00, WindowLayout.ParseOpacity("""{ "opacity": 100 }"""), 6);
    }

    [Fact]
    public void 超出范围一律强制默认值_不是夹到边界()
    {
        // 用户 2026-09-08 定的语义：不对就是 90，不要"帮他"夹成 10 或 100
        Assert.Equal(Default, WindowLayout.ParseOpacity("""{ "opacity": 9 }"""), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("""{ "opacity": 101 }"""), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("""{ "opacity": 0 }"""), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("""{ "opacity": -50 }"""), 6);
    }

    [Fact]
    public void 没写这个键_空文件_都用默认值()
    {
        Assert.Equal(Default, WindowLayout.ParseOpacity(null), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("   "), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("{}"), 6);
        Assert.Equal(Default, WindowLayout.ParseOpacity("""{ "layout": "compact" }"""), 6);
    }

    [Fact]
    public void 带引号的数字也认_手写json常见写法()
        => Assert.Equal(0.40, WindowLayout.ParseOpacity("""{ "opacity": "40" }"""), 6);

    [Fact]
    public void 小数也认()
        => Assert.Equal(0.425, WindowLayout.ParseOpacity("""{ "opacity": 42.5 }"""), 6);

    [Fact]
    public void 键名不分大小写_跟layout那一档一个待遇()
        => Assert.Equal(0.30, WindowLayout.ParseOpacity("""{ "Opacity": 30 }"""), 6);

    [Fact]
    public void 注释和尾逗号照样过()
        => Assert.Equal(0.50, WindowLayout.ParseOpacity("""
            {
              // 百分数，10~100
              "opacity": 50,
            }
            """), 6);

    [Fact]
    public void 写成别的类型时安静退回默认值_而且不牵连layout那一档()
    {
        // ⚠️ 这条是 Opacity 声明成 JsonElement 而不是 double? 的全部理由：
        // 手写文件里一个字段写错，不该把另一个字段也带走。
        const string json = """{ "layout": "compact", "opacity": true }""";
        Assert.Equal(Default, WindowLayout.ParseOpacity(json), 6);
        Assert.Equal(LayoutMode.Compact, WindowLayout.Parse(json));
    }

    [Fact]
    public void 下限不为零_零会连命中测试一起废掉()
    {
        // Avalonia 里 Opacity=0 的控件点不中：表盘拖不动、右键菜单也出不来，
        // 而窗口还占着位置——等于把自己锁在外面。
        Assert.True(WindowLayout.MinOpacityPercent > 0);
    }

    [Fact]
    public void Of把两档接起来()
    {
        Assert.Same(WindowLayout.Standard, WindowLayout.Of(LayoutMode.Standard));
        Assert.Same(WindowLayout.Compact, WindowLayout.Of(LayoutMode.Compact));
    }

    // ---------------------------------------------------------------- 尺寸的不变量

    /// <summary>
    /// 提示条 headless 实测的高度（2026-09-05）：结构照 <c>MainWindow.axaml</c> 搭出来，
    /// <c>StackPanel Spacing=2</c> + 18 号加粗时间行 + 16 号正文，量 <c>DesiredSize</c>。
    /// 这两个数是下面那条护栏的依据，**不是估的**。
    /// </summary>
    private const double BannerOneLine = 48, BannerTwoLines = 69;

    [Fact]
    public void 骨牌行必须装得下它那一档的提示条()
    {
        // ⚠️ 这是这次改动里唯一会"安静坏掉"的地方：提示条撑高那一行 → 把卡片顶下去 →
        // 骨牌那个负边距（按骨牌高度死算，DECISIONS K16）当场作废 → 整个窗口为了一条
        // 提示条跳一分钟。编译不会报，跑起来也要等到真有闹钟到点才看得见。
        static double Need(LayoutMetrics m) => m.BannerMaxLines >= 2 ? BannerTwoLines : BannerOneLine;

        foreach (var m in new[] { WindowLayout.Standard, WindowLayout.Compact })
            Assert.True(m.DominoRowHeight >= Need(m),
                $"骨牌行只有 {m.DominoRowHeight}px，装不下 {Need(m)}px 的提示条");
    }

    // ---------------------------------------------------------------- 拨针标签（3.9.4）

    /// <summary>
    /// <c>14:30</c> 18 号加粗的 <c>DesiredSize</c>，**headless 实测**（2026-09-11，
    /// 跟上面 <see cref="BannerOneLine"/> 那两个数同一个来路）：47.0 × 18.0。
    /// <c>23:59</c> 一样宽（数字等宽），所以一个数就够。
    /// </summary>
    private const double ScrubWidth = 47, ScrubHeight = 18;

    /// <summary>
    /// ⚠️ 这三个是 <c>DialControl</c> **私有常量的副本**：<c>RBezelOut = 1.075</c>、
    /// <c>rFace = box / 2 / (RBezelOut + 0.10)</c>、圆心 y 上移 <c>rFace * 0.03</c>。
    /// 它们在另一个类里而且是 private，测试拿不到——**那边改了这里必须跟着改**，
    /// 这条注释是 §15.4「同一个量两处定义」在这里唯一的兜底。
    /// </summary>
    private const double RBezelOut = 1.075, FaceDivisor = 1.175, CenterYLift = 0.03;

    [Fact]
    public void 左上角那块空白要放得下拨针标签_两档都要()
    {
        // 拨针时刻显示在表盘那一行的左上角（圆盘之外）。⚠️ 这是会"安静坏掉"的一类：
        // 表盘再缩一点、或者字号再调大一点，标签就压到木框上了——编译不报，
        // 而且只有真去滚轮拨针才看得见。
        foreach (var (name, m) in new[] { ("标准", WindowLayout.Standard), ("紧凑", WindowLayout.Compact) })
        {
            var contentW = m.WindowWidth - 36;            // Grid.Margin 左右各 18
            var box = Math.Min(contentW, m.DialHeight);   // DialControl 的 box
            var rFace = box / 2 / FaceDivisor;
            var cx = contentW / 2;
            var cy = m.DialHeight / 2 - rFace * CenterYLift;
            var bezel = rFace * RBezelOut;

            // 标签贴着左上角 (0,0)，离圆心最近的是它的右下角
            var dx = ScrubWidth - cx;
            var dy = ScrubHeight - cy;
            var gap = Math.Sqrt(dx * dx + dy * dy) - bezel;

            Assert.True(gap > 0,
                $"{name}档：{ScrubWidth}×{ScrubHeight} 的标签压到木框上了（还差 {-gap:0.0}px）");
        }
    }

    [Fact]
    public void 紧凑档的表盘正好等于内容宽度()
    {
        // 表盘的 box = Math.Min(内容宽, 高)。两边相等时它刚好填满那一行、左右不留空隙；
        // 若表盘高 > 内容宽，多出来的高度是白给的（box 被宽度卡住），窗口平白长高。
        var m = WindowLayout.Compact;
        Assert.Equal(m.WindowWidth - 36, m.DialHeight);
    }

    [Fact]
    public void 紧凑档每一项都不大于标准档()
    {
        var s = WindowLayout.Standard;
        var c = WindowLayout.Compact;
        Assert.True(c.WindowWidth < s.WindowWidth);
        Assert.True(c.DialHeight < s.DialHeight);
        Assert.True(c.DominoHeight < s.DominoHeight);
        Assert.True(c.DominoRowHeight < s.DominoRowHeight);
        Assert.True(c.BannerMaxWidth < s.BannerMaxWidth);
        Assert.True(c.BannerMaxLines <= s.BannerMaxLines);
    }

    [Fact]
    public void 骨牌的负边距要跟着骨牌高度按比例走()
    {
        // 这个负值不是视觉微调：它抵掉的是 DominoRow 自己在底部留的死区
        // （baseY = k.H * 0.965，即高度的 3.5%），所以必须跟着骨牌高度缩放。
        foreach (var m in new[] { WindowLayout.Standard, WindowLayout.Compact })
        {
            var deadZone = m.DominoHeight * 0.035;
            var eaten = -m.DominoMargin.Bottom;
            Assert.InRange(eaten, deadZone, deadZone + 1.5);
        }
    }
}
