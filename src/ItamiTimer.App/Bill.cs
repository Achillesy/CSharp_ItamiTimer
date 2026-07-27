using System.Text;
using ItamiTimer.Core;

namespace ItamiTimer.App;

/// <summary>
/// §7.1 的最终账单。
///
/// **因为一切都是重放算出来的，这份报告不需要额外记账，是免费的**——没有任何一个
/// 数字是攒出来的，全部来自那一次 <see cref="Replay.Run"/>。
///
/// 它是这个产品"痛"的载体，所以给的时机也讲究：**在专注达成那一刻给**，不在休息
/// 结束时给（§8.4.3）。此刻账刚结清、记忆最新鲜；休息结束时人已经放松了，再甩一份
/// 账单既扫兴又没人看。
/// </summary>
public static class Bill
{
    public static string Render(TaskRecord task, TaskState s)
    {
        var sb = new StringBuilder();
        var end = s.FocusCompletedAt ?? s.Now;
        var elapsed = (end - task.StartedAt).TotalMinutes;

        sb.AppendLine($"{string.Join("、", task.Groups)}");
        sb.AppendLine($"承诺 {task.FocusMinutes} 分钟，实际耗时 {elapsed:F0} 分钟");
        sb.AppendLine();

        if (s.FocusCompletedAt is null)
            sb.AppendLine($"已专注 {s.FocusedSeconds / 60:F1} / {task.FocusMinutes} 分钟");

        if (s.Violations.Count > 0)
        {
            var total = s.OffTaskSecondsByApp.Values.Sum() / 60;
            sb.AppendLine($"偷懒 {s.Violations.Count} 次，共 {total:F0} 分钟");
            foreach (var (app, secs) in s.OffTaskSecondsByApp.OrderByDescending(x => x.Value).Take(5))
                sb.AppendLine($"    {app,-22} {secs / 60,4:F0} 分钟");
        }
        else sb.AppendLine("没有偷懒。");

        if (s.AbsentSeconds > 0) sb.AppendLine($"离开 {s.AbsentSeconds / 60:F0} 分钟");
        // §6.3：既不计入也不惩罚，但要如实报出来
        if (s.GapSeconds > 0) sb.AppendLine($"AW 无数据 {s.GapSeconds / 60:F0} 分钟（不计入）");

        // §5.4：纯审计，不参与判定。删掉它不影响任何数字
        foreach (var ch in task.GroupChanges)
            sb.AppendLine($"{ch.At.ToLocalTime():HH:mm} 加入了 {string.Join("、", ch.Groups)}");

        return sb.ToString().TrimEnd();
    }
}
