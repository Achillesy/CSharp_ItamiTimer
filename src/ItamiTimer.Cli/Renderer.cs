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
    private static readonly (int R, int G, int B) FocusC = (0x2F, 0xA3, 0x6B);
    private static readonly (int R, int G, int B) AmberC = (0xE0, 0x9F, 0x3E);
    private static readonly (int R, int G, int B) SlackC = (0xD6, 0x45, 0x3F);
    private static readonly (int R, int G, int B) GrayC = (0x99, 0x99, 0x99);

    private static string Fg((int R, int G, int B) c) => $"[38;2;{c.R};{c.G};{c.B}m";
    private const string Reset = "[0m";
    private const string Dim = "[2m";
    private const string Bold = "[1m";

    /// <summary>
    /// **Every displayed moment must go through this function.**
    ///
    /// Hit on 2026-07-27: a report printed "Focus achieved at" as 06:40:45 when it was
    /// actually 14:40:45. The cause was two time zones mixed in the same report --
    /// StartedAt comes from DateTimeOffset.Now (local offset), while the other moment was
    /// derived from an ActivityWatch event; ActivityWatch returns UTC,
    /// DateTimeOffset.Parse keeps +00:00, and formatting it directly prints a UTC clock.
    ///
    /// Trusting every display site to remember `.ToLocalTime()` is unreliable (that's
    /// exactly how this slipped through), so it's funneled into one function instead. Any
    /// new place that displays a moment must go through it.
    /// </summary>
    public static string Clock(DateTimeOffset t, string fmt = "HH:mm:ss") => t.ToLocalTime().ToString(fmt);

    /// <summary>
    /// One cell -> one character. **Only maps "tier -> character"** -- "what this cell
    /// should be read as" belongs to the judgment layer (<see cref="MinuteCell.Tier"/>,
    /// §4.6), not rewritten here.
    /// </summary>
    public static string CellChar(MinuteCell c) => c.Tier switch
    {
        CellTier.FocusFull => $"{Fg(FocusC)}█{Reset}",
        CellTier.FocusMid => $"{Fg(AmberC)}█{Reset}",
        CellTier.FocusLow => $"{Fg(AmberC)}▒{Reset}",
        CellTier.OffTask => $"{Fg(SlackC)}█{Reset}",
        CellTier.Away => $"{Dim}□{Reset}",
        CellTier.Pending => $"{Fg(GrayC)}█{Reset}",
        _ => $"{Dim}·{Reset}",
    };

    /// <summary>One character per minute, wrapping every 60 (exactly one lap).</summary>
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

    /// <summary>Prints a buffer summary: the colour-block strip plus statistics. Used by bench.</summary>
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
