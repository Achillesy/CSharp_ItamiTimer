using Avalonia;

namespace ItamiTimer.App;

/// <summary>标准（一直以来的样子）/ 紧凑（3.8.0 新增）。</summary>
public enum LayoutMode { Standard, Compact }

/// <summary>
/// 一档的全部尺寸。**六个数字的唯一定义处**——XAML 里一个都不写死，免得同一个量
/// 两处定义（§15.4 那类事故的形状）。
/// </summary>
/// <param name="WindowWidth">窗口宽度。内容宽 = 它 − 36（<c>Grid.Margin</c> 左右各 18）。</param>
/// <param name="DialHeight">表盘控件高度。</param>
/// <param name="DominoHeight">骨牌控件高度。</param>
/// <param name="DominoMargin">骨牌行的外边距，**下边是负的**（DECISIONS K16）。</param>
/// <param name="BannerMaxLines">提示条最多列几条，同时也是 <c>TextBlock.MaxLines</c>。</param>
/// <param name="BannerMaxWidth">提示条正文的折行宽度。</param>
public sealed record LayoutMetrics(
    double WindowWidth,
    double DialHeight,
    double DominoHeight,
    Thickness DominoMargin,
    int BannerMaxLines,
    double BannerMaxWidth)
{
    /// <summary>骨牌那一行实际占多高——提示条能不能塞进去，比的就是这个数。</summary>
    public double DominoRowHeight => DominoHeight + DominoMargin.Top + DominoMargin.Bottom;
}

/// <summary>
/// 窗口的两套尺寸（DESIGN §8.10，3.8.0）。
///
/// 起因：在 2560×1440 @123% 上，标准档实测 467×841 物理像素，**占了工作区高度的 61%**
/// ——用户嫌它太占地方。紧凑档把表盘和骨牌缩到约 3/4，窗口宽度跟着缩，**控件和字号
/// 一个都不动**（用户 2026-09-05 明确要求，包括右上角那四个图标）。
///
/// ## 为什么只缩表盘和骨牌
///
/// 卡片那 250px 是**地板**：控件和字号不缩，它就一分都省不掉。就算把表盘和骨牌全砍光，
/// 窗口也还有 280px。所以能压的只有上面那两块，压完 684 → 597，占屏 61% → 53%。
///
/// ⚠️ **宽度必须跟着缩**：只缩表盘会让表盘变成一个小圆浮在 344 宽的行里两侧留空，
/// 而且**拖窗口是绑在表盘 <c>Bounds</c> 上的**（DECISIONS K10），可拖区域会跟着缩水。
///
/// ## 为什么是「文件」而不是设置项、也不是命令行开关
///
/// 用户 2026-09-05 定的：**单独一个 <see cref="FileName"/> 文件，只在启动时读一次**，
/// 运行中改了要下次启动才生效。
///
/// 刻意不放进 <c>settings.json</c>——那个文件是「程序随时整份重写」的（DESIGN §11），
/// 手改会被覆盖掉。这个文件跟 <c>alarms.cron</c> 同一类契约：**用户写、程序只读、
/// 程序永不回写**。
///
/// 也刻意没做成命令行开关：Windows 上加个快捷方式参数很自然，但 **macOS 的 <c>.app</c>
/// 双击由 Finder 启动、根本不传参数**，绕法（第二个 bundle / <c>open --args</c> /
/// Automator）每条都有硬代价，两个平台会一边顺一边别扭。文件在两边一模一样。
/// </summary>
public static class WindowLayout
{
    /// <summary>配置目录下的文件名。没有扩展名，内容就一行。</summary>
    public const string FileName = "layout";

    /// <summary>
    /// 标准档：一直以来的样子，3.8.0 一个像素都没动。
    ///
    /// 骨牌行 76+2−4 = 74px，装得下两行提示条（实测 69px），**余量只有 5px**
    /// ——比 3.7.0 那段注释估的还紧。
    /// </summary>
    public static readonly LayoutMetrics Standard = new(
        WindowWidth: 380, DialHeight: 330, DominoHeight: 76,
        DominoMargin: new Thickness(0, 2, 0, -4),
        BannerMaxLines: 2, BannerMaxWidth: 280);

    /// <summary>
    /// 紧凑档。
    ///
    /// **256 正好等于内容宽度**，所以表盘的 <c>box = Math.Min(宽, 高)</c> 两边相等，
    /// 刚好填满那一行、左右不留空隙（标准档反而是 330 高、344 宽，左右各空 7px）。
    /// 表盘一切都从 <c>Bounds</c> 推导（<c>rFace = box / 2.35</c>），所以改这一个数就
    /// 等比缩放，**绘制代码一行不用动**。唯一有硬门槛的是秒针宽度 <c>rFace × 0.008</c>：
    /// 256 档是 0.87 DIP，100% 缩放的屏幕上会略虚，125%/Retina 上是 1 像素以上。
    ///
    /// 骨牌 56 ≈ 76 × 256/344，**它自己的规格一个字没动**（6:3:1、间距 = 高的一半）。
    /// ⚠️ 中途考虑过「保持骨牌像素尺寸不变、只压间距和倾角」，**已否决**：要在 256px
    /// 宽里塞下 7 块原尺寸骨牌，节距得从 4 压到 2.65，末块倾角会从 30° 掉到 16°——
    /// 「倒了」这个信息当场变弱，而那是这一排唯一的意义；而且倾角表是从「间距 = 高的
    /// 一半」解出来的，改了要整张重解。换来的高度只有 17px，不值。
    ///
    /// **上边距 8 不是 2**：这一行的高度 = 骨牌 + 上下边距，而提示条跟骨牌叠在同一个
    /// <c>Auto</c> 格子里，撑高了就把卡片顶下去。56+2−3 = 55 连一行（48px）都贴边；
    /// 改成 8 之后 61px，装一行还剩 13px。用户 2026-09-05 在「上边距 +16 装两行」和
    /// 「+6 装一行」之间选了后者——多出来的条数照旧缀在时间行末尾（<c>23:55 +2</c>）。
    /// </summary>
    public static readonly LayoutMetrics Compact = new(
        WindowWidth: 292, DialHeight: 256, DominoHeight: 56,
        DominoMargin: new Thickness(0, 8, 0, -3),
        BannerMaxLines: 1, BannerMaxWidth: 220);

    public static LayoutMetrics Of(LayoutMode mode) => mode == LayoutMode.Compact ? Compact : Standard;

    /// <summary>
    /// 这一次启动用哪一档。**第一次读取时去看文件，之后再改文件也不看**——这就是用户
    /// 要的语义，也顺带免掉了「运行中换档要重新夹回屏幕、要重算负边距、提示条正显示着
    /// 怎么办」的一整类边界情况。
    ///
    /// 用 <see cref="Lazy{T}"/> 而不是字段初始化器：后者会在**类型初始化**时就去碰文件
    /// 系统和日志，那样单元测试只要碰一下 <see cref="Of"/> 就被拖下水。
    /// </summary>
    public static LayoutMode Mode => LazyMode.Value;

    /// <summary>这一次启动的尺寸。<see cref="MainWindow"/> 只读这一个。</summary>
    public static LayoutMetrics Current => Of(Mode);

    private static readonly Lazy<LayoutMode> LazyMode = new(Load);

    private static LayoutMode Load()
    {
        var path = Path.Combine(AppData.Dir, FileName);
        try
        {
            var exists = File.Exists(path);
            var mode = Parse(exists ? File.ReadAllText(path) : null);
            Log.Info($"Layout: {mode.ToString().ToLowerInvariant()}"
                   + (exists ? $" (from {path})" : " (no layout file)"));
            return mode;
        }
        catch (Exception e)
        {
            // 读不到就用标准档，绝不因为一个可选的外观开关起不来。
            Log.Error($"Failed to read {path}; using the standard layout", e);
            return LayoutMode.Standard;
        }
    }

    /// <summary>
    /// 认字。**只认得 <c>compact</c> 一个词**：不分大小写、忽略首尾空白、只看第一行。
    /// 空文件、写错、整个文件不存在——一律标准档。
    ///
    /// 跟 <c>alarms.cron</c> 那套「不认识的行安静跳过」同一个路数：一个可选的外观开关，
    /// 不值得为它拒绝启动，也不值得弹任何东西。**纯函数，文件读取在外面**，所以能测。
    /// </summary>
    public static LayoutMode Parse(string? text)
    {
        var first = text?.Split('\n', 2)[0].Trim();
        return string.Equals(first, "compact", StringComparison.OrdinalIgnoreCase)
            ? LayoutMode.Compact
            : LayoutMode.Standard;
    }
}
