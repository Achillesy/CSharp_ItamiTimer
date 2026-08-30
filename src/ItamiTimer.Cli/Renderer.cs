using System.Text;
using ItamiTimer.Core;

namespace ItamiTimer.Cli;

/// <summary>
/// Renders a <see cref="MinuteCell"/> list into terminal colour blocks.
///
/// This is where the project's rule about separating logic and presentation lands:
/// **Core only emits seconds, never colour**. Colouring happens here; the dial's colouring
/// lives in App, and both consume the same list -- and **the tiering rule belongs to the
/// rendering layer**: the
/// terminal uses three discrete tiers, while the dial keeps the raw counts around in case
/// it ever wants to go back to a continuous encoding.
/// </summary>
public static class Renderer
{
    /// <summary>
    /// 一圈 60 格，最多两圈——**跟表盘严格一一对应**：`JudgmentBuffer` 的可绘制跨度是
    /// `DrawSeconds = 7200` 秒 = 120 分钟，而圈号由 `cell.Index / 60` 决定（DESIGN §8.3），
    /// 所以第一行就是外圈、第二行就是内圈，第 N 列就是第 N 格。
    /// </summary>
    public const int Lap = 60;

    /// <summary>
    /// ⚠️ **这里一个 ANSI 转义码都不许有**（2026-08-30 全部拆掉）。
    ///
    /// 原来色环是 24 位色的 `\e[38;2;R;G;Bm`，用户实测**打出来是一串裸转义码**——不是终端
    /// 不支持，是这个程序从来没调用过 `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_PROCESSING)`：
    /// .NET 在 Windows 上不会替你开 VT 处理，Windows Terminal 自己会解释所以看着正常，
    /// 传统 conhost 就原样吐出来了。
    ///
    /// 与其补那行 P/Invoke，不如整个不用颜色：表盘本来就**用高度和颜色编同一个量**
    /// （§8.2.3a"木桶短板"），终端这边改用字符就是把那第二条通道单独拿出来用。白拿的
    /// 好处是输出能直接 grep、能重定向进文件，不用再剥一层转义码。
    /// </summary>
    public static string Clock(DateTimeOffset t, string fmt = "HH:mm:ss") => t.ToLocalTime().ToString(fmt);

    /// <summary>
    /// 一格 → 一个字符。**只做"档位 → 字符"的映射**——"这一格该读成什么"是判定层的事
    /// （<see cref="MinuteCell.Tier"/>，§4.6），不在这里重写一遍。
    ///
    /// 有专注的三档用字母（F/M/L，自带图例），其余一律不用字母，免得混淆：
    /// <code>
    /// F  41-60 秒专注      M  21-40 秒       L  1-20 秒
    /// #  跑偏（有窗口证据、零专注）          *  离开（不计入，不怪你）
    /// -  承诺弧，还欠着                      .  程序没在跑
    /// </code>
    ///
    /// ⚠️ `.` 那一档（<see cref="CellTier.NotDrawn"/>）是**程序自己没在查**的那段——
    /// 合上笔记本超过 4 分钟再打开，中间就是它。`Cover` 特意把那一段清成
    /// <see cref="JudgmentCode.Init"/> 而不是给 fail-open 的 `AwOffline`，否则"睡一整夜"
    /// 就能把一轮任务填满。它跟 `#` 是两回事：**不计入专注，也不算你的错**。
    ///
    /// ⚠️ 反过来，**AW 连不上的那一分钟会显示成满格 `F`**——查了但 AW 拿不出数据，
    /// 按 §3.1 那条知情的 fail-open 算作专注（`AwOffline(5) >= Focused(4)`）。反直觉，
    /// 但那正是设计：拿不出数据是 AW 的错，不该罚用户。
    /// </summary>
    public static char CellChar(MinuteCell c) => c.Tier switch
    {
        CellTier.FocusFull => 'F',
        CellTier.FocusMid => 'M',
        CellTier.FocusLow => 'L',
        CellTier.OffTask => '#',
        CellTier.Away => '*',
        CellTier.Pending => '-',
        _ => '.',
    };

    /// <summary>
    /// 把格子铺进**固定的两行 × 60 格**画布，右侧用空格补齐。
    ///
    /// 固定宽度是刻意的：位置绝对稳定，同一列每分钟都指同一格，盯着看不会跳；而且
    /// 60 列在任何终端都不会折行。空着的部分**不能用别的字符填**——承诺弧那截 `-`
    /// 会随着拖延**逐格后移**，补齐的空格数本来就不固定（用户 2026-08-30）。
    /// </summary>
    public static string[] Rows(IReadOnlyList<MinuteCell> cells)
    {
        var rows = new char[2][];
        for (var lap = 0; lap < 2; lap++)
        {
            rows[lap] = new char[Lap];
            Array.Fill(rows[lap], ' ');
        }

        for (var i = 0; i < cells.Count && i < 2 * Lap; i++)
            rows[i / Lap][i % Lap] = CellChar(cells[i]);

        return [new string(rows[0]), new string(rows[1])];
    }

    public static string Legend()
        => "F 41-60s focus   M 21-40s   L 1-20s   # off-task   * away   "
         + "- still owed   . not polled";

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
    /// The report. **Only the CLI ever shows a report** -- it has no dial to look at
    /// instead (B4: the UI never shows numbers, ever).
    ///
    /// Since everything is projected from the buffer, this report needs no extra
    /// bookkeeping of its own -- it comes for free.
    /// </summary>
    /// <param name="asOf">
    /// What time "now" is. **Must be supplied by the caller**, never read from
    /// <c>DateTimeOffset.Now</c> in here -- dry-running historical data would then compute
    /// "8080 minutes of wall-clock time" (caught during a real run on 2026-08-02).
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
