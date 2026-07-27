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
        if (cells.Count == 0) return $"{Dim}（还没有走完一整分钟）{Reset}";

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
        => $"{Fg(Focus)}█{Reset} 计入   {Fg(Amber)}█{Reset} 掺了偷懒   {Fg(Slack)}█{Reset} 基本在偷懒   "
         + $"{Fg(AbsentC)}▒{Reset} 离开   {Dim}░{Reset} AW 无数据";

    public static string PhaseText(TaskPhase p) => p switch
    {
        TaskPhase.NotStarted => "即将开始",
        TaskPhase.Focusing => $"{Fg(Focus)}● 专注中{Reset}",
        TaskPhase.Slacking => $"{Fg(Slack)}● 跑偏了{Reset}",
        TaskPhase.Away => $"{Fg(AbsentC)}● 人不在{Reset}",
        TaskPhase.NoData => $"{Dim}● AW 无数据{Reset}",
        TaskPhase.Resting => "☕ 休息中",
        TaskPhase.Completed => "✓ 已完成",
        _ => p.ToString(),
    };

    /// <summary>§7.1 的账单。因为一切都是重放算出来的，**这份报告不需要额外记账，是免费的**。</summary>
    public static string Bill(TaskRecord task, TaskState s)
    {
        var sb = new StringBuilder();
        var elapsed = (s.FocusCompletedAt ?? s.Now) - task.StartedAt;
        sb.AppendLine($"任务：{string.Join("、", task.Groups)}   {PhaseText(s.Phase)}");
        sb.AppendLine($"承诺专注 {task.FocusMinutes} 分钟，实际耗时 {elapsed.TotalMinutes:F1} 分钟");

        // 最重要的一个数字：已经攒了多少。没有它，用户看不出还差多远。
        var banked = s.FocusedSeconds / 60;
        if (s.FocusCompletedAt is null)
            sb.AppendLine($"**已专注 {banked:F1} / {task.FocusMinutes} 分钟，还差 {task.FocusMinutes - banked:F1} 分钟**");
        else
            sb.AppendLine($"专注已达成于 {s.FocusCompletedAt.Value:HH:mm:ss}");
        sb.AppendLine();

        if (s.Violations.Count > 0)
        {
            var total = s.OffTaskSecondsByApp.Values.Sum();
            sb.AppendLine($"  偷懒 {s.Violations.Count} 次，共 {total / 60:F1} 分钟");
            foreach (var (app, secs) in s.OffTaskSecondsByApp.OrderByDescending(x => x.Value))
                sb.AppendLine($"      {app,-24} {secs / 60:F1} 分钟");
        }
        else sb.AppendLine("  没有偷懒。");

        if (s.AbsentSeconds > 0) sb.AppendLine($"  离开              {s.AbsentSeconds / 60:F1} 分钟");
        if (s.GapSeconds > 0) sb.AppendLine($"  AW 无数据          {s.GapSeconds / 60:F1} 分钟（不计入）");
        foreach (var ch in task.GroupChanges)
            sb.AppendLine($"  中途添加小目标     {ch.At.ToLocalTime():HH:mm} → {string.Join("、", ch.Groups)}");
        return sb.ToString();
    }
}
