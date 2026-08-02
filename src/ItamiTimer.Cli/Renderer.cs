using System.Text;
using ItamiTimer.Core;

namespace ItamiTimer.Cli;

/// <summary>
/// 把 <see cref="MinuteCell"/> 列表画成终端色块。
///
/// 这是 DESIGN.md §8 第四条纪律的落点：**Core 只吐秒数，不吐颜色**。上色在这里，
/// 表盘的上色在 App 里，两边消费同一个列表——而且**分档规则是渲染层的事**（§4.6）：
/// 终端用离散的三档，表盘将来想回到连续编码时原始计数还在。
/// </summary>
public static class Renderer
{
    private static readonly (int R, int G, int B) FocusC = (0x2F, 0xA3, 0x6B);
    private static readonly (int R, int G, int B) AmberC = (0xE0, 0x9F, 0x3E);
    private static readonly (int R, int G, int B) SlackC = (0xD6, 0x45, 0x3F);
    private static readonly (int R, int G, int B) GrayC = (0x99, 0x99, 0x99);

    private static string Fg((int R, int G, int B) c) => $"[38;2;{c.R};{c.G};{c.B}m";
    private const string Reset = "[0m";
    private const string Dim = "[2m";
    private const string Bold = "[1m";

    /// <summary>
    /// **所有时刻都必须经过这里再显示。**
    ///
    /// 2026-07-27 踩过：账单把「专注已达成于」打成了 06:40:45，实际是 14:40:45。
    /// 原因是同一份账单里混了两个时区 —— StartedAt 来自 DateTimeOffset.Now（本地
    /// 偏移），而另一个时刻是从 AW 事件推导来的；AW 返回 UTC，
    /// DateTimeOffset.Parse 保留 +00:00，直接格式化出来就是 UTC 时钟。
    ///
    /// 指望每个显示点都记得写 .ToLocalTime() 是靠不住的（就是这么漏的），
    /// 所以收口到一个函数。新增任何显示时刻的地方都走它。
    /// </summary>
    public static string Clock(DateTimeOffset t, string fmt = "HH:mm:ss") => t.ToLocalTime().ToString(fmt);

    /// <summary>
    /// 一格（一分钟）→ 一个字符，规则见 DESIGN.md §4.6。
    ///
    /// 有 focus 就按 <c>&gt;40 / &gt;20 / &gt;0</c> 分三档；一秒 focus 都没有时，在
    /// Init/Gray/Afk/OffTask 里取<b>计数最大</b>的那个，平局取<b>码值大</b>的（fail-closed）。
    /// </summary>
    public static string CellChar(MinuteCell c)
    {
        if (c.FocusSeconds > 40) return $"{Fg(FocusC)}█{Reset}";
        if (c.FocusSeconds > 20) return $"{Fg(AmberC)}█{Reset}";
        if (c.FocusSeconds > 0) return $"{Fg(AmberC)}▒{Reset}";

        // argmax；平局取码值大的 → OffTask(3) > Afk(2) > Gray(1) > Init(0)
        var best = c.InitSeconds; var pick = JudgmentCode.Init;
        if (c.GraySeconds >= best) { best = c.GraySeconds; pick = JudgmentCode.Gray; }
        if (c.AfkSeconds >= best) { best = c.AfkSeconds; pick = JudgmentCode.Afk; }
        if (c.OffTaskSeconds >= best) pick = JudgmentCode.OffTask;

        return pick switch
        {
            JudgmentCode.OffTask => $"{Fg(SlackC)}█{Reset}",
            JudgmentCode.Afk => $"{Dim}□{Reset}",
            JudgmentCode.Gray => $"{Fg(GrayC)}█{Reset}",
            _ => $"{Dim}·{Reset}",
        };
    }

    /// <summary>一分钟一个字符，60 个换一行（正好一圈）。</summary>
    public static string Cells(IReadOnlyList<MinuteCell> cells)
    {
        if (cells.Count == 0) return $"{Dim}(no full minute has elapsed yet){Reset}";

        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0 && i % 60 == 0) sb.Append('\n');
            sb.Append(CellChar(cells[i]));
        }
        return sb.ToString();
    }

    public static string Legend()
        => $"{Fg(FocusC)}█{Reset} on-task   {Fg(AmberC)}█{Reset} partly off-task   "
         + $"{Fg(SlackC)}█{Reset} off-task   {Dim}□{Reset} away   "
         + $"{Fg(GrayC)}█{Reset} still owed   {Dim}·{Reset} no data";

    /// <summary>打印 buffer 摘要：色块条 + 统计。bench 用。</summary>
    public static void BufferSummary(JudgmentBuffer buf)
    {
        var cells = buf.ToMinuteCells();
        if (cells.Count == 0) { Console.WriteLine("  (buffer empty)\n"); return; }

        Console.WriteLine("  " + Cells(cells).Replace("\n", "\n  "));

        var (focus, off, afk, gray, init) = Totals(cells);
        var elapsed = (double)(focus + off + afk + init);
        var pct = elapsed <= 0 ? 0 : focus / elapsed * 100;

        Console.WriteLine($"  {Bold}{focus / 60.0:F1}min focused{Reset}  "
                        + $"{Fg(SlackC)}{off / 60.0:F1}min slack{Reset}  "
                        + $"afk {afk / 60.0:F1}min  gray {gray / 60.0:F1}min  init {init / 60.0:F1}min  "
                        + $"→ {pct:F0}% counted");
        Console.WriteLine($"  remaining={buf.RemainingTargetSeconds}s  focused={buf.FocusedSeconds}s  "
                        + $"archived={buf.ArchivedSeconds}s  complete={buf.IsFocusComplete}\n");
    }

    private static (int Focus, int Off, int Afk, int Gray, int Init) Totals(
        IReadOnlyList<MinuteCell> cells)
    {
        int focus = 0, off = 0, afk = 0, gray = 0, init = 0;
        foreach (var c in cells)
        {
            focus += c.FocusSeconds; off += c.OffTaskSeconds;
            afk += c.AfkSeconds; gray += c.GraySeconds; init += c.InitSeconds;
        }
        return (focus, off, afk, gray, init);
    }

    /// <summary>
    /// 账单。**只有 CLI 才给账单**——它没有表盘可看（B4：界面任何时候都不给数字）。
    ///
    /// 因为一切都是从 buffer 投影出来的，这份报告不需要额外记账，是免费的。
    /// </summary>
    /// <param name="asOf">
    /// 「现在」是几点。**必须由调用方给**，不能在这里读 <c>DateTimeOffset.Now</c>——
    /// 干跑历史数据时那会算出「8080 分钟的墙钟时间」（2026-08-02 实跑抓到）。
    /// </param>
    public static string Bill(TaskRecord task, JudgmentBuffer buf, double settledSeconds,
                              DateTimeOffset asOf, DateTimeOffset? completedAt)
    {
        var cells = buf.ToMinuteCells();
        var (focus, off, afk, _, init) = Totals(cells);
        var banked = settledSeconds + focus;

        var sb = new StringBuilder();
        sb.AppendLine($"Task: {task.Group ?? "(none)"}");
        var elapsed = asOf - task.StartedAt;
        sb.AppendLine($"Committed to {task.FocusMinutes} min of focus; "
                    + $"{elapsed.TotalMinutes:F1} min of wall-clock spent");

        if (completedAt is { } done)
            sb.AppendLine($"Focus completed at {Clock(done)}");
        else
            sb.AppendLine($"**Focused {banked / 60.0:F1} / {task.FocusMinutes} min — "
                        + $"{(task.FocusMinutes * 60 - banked) / 60.0:F1} min to go**");
        sb.AppendLine();

        if (off > 0) sb.AppendLine($"  Off-task          {off / 60.0:F1} min");
        else sb.AppendLine("  No off-task time.");
        if (afk > 0) sb.AppendLine($"  Away              {afk / 60.0:F1} min (not blamed, not counted)");
        if (init > 0) sb.AppendLine($"  Never polled      {init / 60.0:F1} min (not counted)");
        if (settledSeconds > 0) sb.AppendLine($"  Archived earlier  {settledSeconds / 60.0:F1} min");
        return sb.ToString();
    }
}
