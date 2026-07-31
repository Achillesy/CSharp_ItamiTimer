using System.Text;
using ItamiTimer.Core;

namespace ItamiTimer.Cli;

/// <summary>
/// 把 MinuteCell 列表画成终端色块。
///
/// 这是 DESIGN.md §8 第四条纪律的落点：**Core 只吐秒数，不吐颜色**。上色在这里，
/// 表盘的上色在 App 里，两边消费同一个列表。
/// </summary>
public static class Renderer
{
    // §8.2.3 的语义色，取自 design/dial-specimens.html 的浅色 token
    private static readonly (int R, int G, int B) Focus = (0x2F, 0xA3, 0x6B);
    private static readonly (int R, int G, int B) Amber = (0xE0, 0xA0, 0x3A);
    private static readonly (int R, int G, int B) Slack = (0xD6, 0x45, 0x3F);
    private static readonly (int R, int G, int B) AbsentC = (0x8A, 0x94, 0xA0);

    private static string Fg((int R, int G, int B) c) => $"[38;2;{c.R};{c.G};{c.B}m";
    private const string Reset = "[0m";
    private const string Dim = "[2m";

    /// <summary>
    /// **所有时刻都必须经过这里再显示。**
    ///
    /// 2026-07-27 踩过：账单把「专注已达成于」打成了 06:40:45，实际是 14:40:45。
    /// 原因是同一份账单里混了两个时区 —— StartedAt 来自 DateTimeOffset.Now（本地
    /// 偏移），而 FocusCompletedAt 是从 AW 事件推导来的；AW 返回 UTC，
    /// DateTimeOffset.Parse 保留 +00:00，直接格式化出来就是 UTC 时钟。
    ///
    /// 指望每个显示点都记得写 .ToLocalTime() 是靠不住的（就是这么漏的），
    /// 所以收口到一个函数。新增任何显示时刻的地方都走它。
    /// </summary>
    public static string Clock(DateTimeOffset t, string fmt = "HH:mm:ss") => t.ToLocalTime().ToString(fmt);

    /// <summary>
    /// 经琥珀色的三段过渡（§0.4 选项 B）。RGB 直插在 50% 处会出现发脏的橄榄绿，
    /// 看着像画错了；经琥珀走一趟就干净，而且自带明度变化 —— 红绿色盲也能分辨。
    /// </summary>
    private static (int R, int G, int B) Ramp(double impurity)
    {
        static int Mix(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
        if (impurity <= 0.5)
        {
            var t = impurity / 0.5;
            return (Mix(Focus.R, Amber.R, t), Mix(Focus.G, Amber.G, t), Mix(Focus.B, Amber.B, t));
        }
        var u = (impurity - 0.5) / 0.5;
        return (Mix(Amber.R, Slack.R, u), Mix(Amber.G, Slack.G, u), Mix(Amber.B, Slack.B, u));
    }

    /// <summary>一分钟一个字符，60 个换一行。</summary>
    public static string Cells(IReadOnlyList<MinuteCell> cells)
    {
        if (cells.Count == 0) return $"{Dim}(no full minute has elapsed yet){Reset}";

        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0 && i % 60 == 0) sb.Append('\n');
            var c = cells[i];

            // 四种结局分开画（§0.4.1）。把"起身离开"画成红色等于冤枉自己。
            if (c.GapSeconds > c.TotalSeconds / 2)
                sb.Append($"{Dim}░{Reset}");                       // AW 无数据：空心
            else if (c.AbsentSeconds > c.TotalSeconds / 2)
                sb.Append($"{Fg(AbsentC)}▒{Reset}");               // 离开：灰，不是红
            else
                sb.Append($"{Fg(Ramp(1 - c.Purity))}█{Reset}");    // 绿→琥珀→红
        }
        return sb.ToString();
    }

    public static string Legend()
        => $"{Fg(Focus)}█{Reset} on-task   {Fg(Amber)}█{Reset} partly off-task   {Fg(Slack)}█{Reset} mostly off-task   "
         + $"{Fg(AbsentC)}▒{Reset} away   {Dim}░{Reset} no AW data";

    public static string PhaseText(TaskPhase p) => p switch
    {
        TaskPhase.NotStarted => "about to start",
        TaskPhase.Focusing => $"{Fg(Focus)}● focusing{Reset}",
        TaskPhase.Slacking => $"{Fg(Slack)}● off-task{Reset}",
        TaskPhase.Away => $"{Fg(AbsentC)}● away{Reset}",
        TaskPhase.NoData => $"{Dim}● no AW data{Reset}",
        TaskPhase.Resting => "☕ on a break",
        TaskPhase.Completed => "✓ completed",
        _ => p.ToString(),
    };

    // ---- 新判定模型（JudgmentBuffer）的终端渲染 ----

    private static readonly (int R, int G, int B) GreenC = (0x2F, 0xA3, 0x6B);  // Focused / AwOffline
    private static readonly (int R, int G, int B) RedC = (0xD6, 0x45, 0x3F);    // OffTask
    private static readonly (int R, int G, int B) GrayC = (0x99, 0x99, 0x99);   // Gray (预计)
    private const string Bold = "[1m";

    /// <summary>buffer 状态码 → 终端色块。</summary>
    private static string StatusChar(byte s) => s switch
    {
        JudgmentBuffer.Gray       => $"{Fg(GrayC)}█{Reset}",
        JudgmentBuffer.Focused    => $"{Fg(GreenC)}█{Reset}",
        JudgmentBuffer.AwOffline  => $"{Fg(GreenC)}▓{Reset}",
        JudgmentBuffer.Afk        => " ",
        JudgmentBuffer.OffTask    => $"{Fg(RedC)}█{Reset}",
        _                         => $"{Dim}·{Reset}",  // Init
    };

    /// <summary>打印 buffer 摘要：前几格 + 绘制区概览 + 统计。</summary>
    public static void BufferSummary(JudgmentBuffer buf)
    {
        var draw = buf.DrawSpan;
        var focused = 0; var off = 0; var afk = 0; var gray = 0; var offline = 0; var init = 0;
        for (var i = 0; i < draw.Length; i++)
        {
            switch (draw[i])
            {
                case JudgmentBuffer.Focused: focused++; break;
                case JudgmentBuffer.AwOffline: offline++; break;
                case JudgmentBuffer.OffTask: off++; break;
                case JudgmentBuffer.Afk: afk++; break;
                case JudgmentBuffer.Gray: gray++; break;
                default: init++; break;
            }
        }
        var total = focused + offline + off + afk + gray + init;
        if (total == 0) { Console.WriteLine("  (buffer empty)\n"); return; }

        // 60s/cell 压缩显示（只显示前 60 格 = 前 1 小时
        var cells = Math.Min(60, draw.Length / 60);
        var sb = new StringBuilder("  ");
        for (var c = 0; c < cells; c++)
        {
            var f = 0; var o = 0; var a = 0; var g = 0; var ol = 0; var ii = 0;
            for (var s = 0; s < 60; s++)
            {
                switch (draw[c * 60 + s])
                {
                    case JudgmentBuffer.Focused: f++; break;
                    case JudgmentBuffer.AwOffline: ol++; break;
                    case JudgmentBuffer.OffTask: o++; break;
                    case JudgmentBuffer.Afk: a++; break;
                    case JudgmentBuffer.Gray: g++; break;
                    default: ii++; break;
                }
            }
            var dom = f + ol; // 主导地位
            if (dom >= 30)        sb.Append($"{Fg(GreenC)}█{Reset}");
            else if (o >= 30)     sb.Append($"{Fg(RedC)}█{Reset}");
            else if (a >= 30)     sb.Append(" ");
            else if (g >= 30)     sb.Append($"{Fg(GrayC)}█{Reset}");
            else                  sb.Append($"{Dim}·{Reset}");
        }
        Console.WriteLine(sb.ToString());

        // 统计行
        var pct = (double)(focused + offline) / total * 100;
        Console.WriteLine($"  {Bold}{(focused + offline) / 60}min focused + {offline / 60}min AW-offline{Reset}  "
                        + $"{Fg(RedC)}{off / 60}min slack{Reset}  "
                        + $"afk {afk / 60}min  gray {gray / 60}min  init {init / 60}min  "
                        + $"→ {pct:F0}% counted");
        Console.WriteLine($"  during={buf.DuringSeconds:F0}s  focusGoal={buf.FocusSeconds}s  complete={buf.IsFocusComplete}\n");
    }

    /// <summary>§7.1 的账单。因为一切都是重放算出来的，**这份报告不需要额外记账，是免费的**。</summary>
    public static string Bill(TaskRecord task, TaskState s)
    {
        var sb = new StringBuilder();
        var elapsed = (s.FocusCompletedAt ?? s.Now) - task.StartedAt;
        sb.AppendLine($"Task: {task.Group ?? "(none)"}   {PhaseText(s.Phase)}");
        sb.AppendLine($"Committed to {task.FocusMinutes} min of focus; {elapsed.TotalMinutes:F1} min of wall-clock spent");

        var banked = s.FocusedSeconds / 60;
        if (s.FocusCompletedAt is null)
            sb.AppendLine($"**Focused {banked:F1} / {task.FocusMinutes} min — {task.FocusMinutes - banked:F1} min to go**");
        else
            sb.AppendLine($"Focus completed at {Clock(s.FocusCompletedAt.Value)}");
        sb.AppendLine();

        if (s.Violations.Count > 0)
        {
            var total = s.OffTaskSecondsByApp.Values.Sum();
            sb.AppendLine($"  Off-task {s.Violations.Count}x, {total / 60:F1} min total");
            foreach (var (app, secs) in s.OffTaskSecondsByApp.OrderByDescending(x => x.Value))
                sb.AppendLine($"      {app,-24} {secs / 60:F1} min");
        }
        else sb.AppendLine("  No off-task time.");

        if (s.AbsentSeconds > 0) sb.AppendLine($"  Away              {s.AbsentSeconds / 60:F1} min");
        if (s.GapSeconds > 0) sb.AppendLine($"  No AW data        {s.GapSeconds / 60:F1} min (not counted)");
        return sb.ToString();
    }
}
