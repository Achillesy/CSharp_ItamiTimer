using System.Text;

namespace ItamiTimer.App;

/// <summary>
/// Diagnostic logging.
///
/// **The UI is silent toward the user** (not a single word below the divider explains
/// itself, set by the user 2026-07-27), but silent doesn't mean the reason gets thrown
/// away -- **the cause of a failure must be recorded somewhere**, otherwise a broken
/// program becomes a black box: the user just sees a greyed-out button, and nobody can say
/// why.
///
/// ⚠️ **This is the one thing this program writes to disk**, which doesn't conflict with
/// this project's "never write to disk" rule: that rule forbids **task state** being
/// persisted (no current-task.json, no accumulators, quitting = abandoning), so that state
/// is always derivable from ActivityWatch history. The log is a different category of
/// thing -- it's only ever appended to, never read back to participate in any judgment,
/// and deleting it doesn't change the program's behaviour at all.
///
/// If it can't be written, fine: **logging itself must never crash the program**.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1 * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string Directory => AppData.Dir;

    public static string Path_ => System.IO.Path.Combine(Directory, "itami.log");

    /// <summary>
    /// <b>Both configurations write</b> (user, evening of 2026-07-28: "have the Release
    /// build write logs too").
    ///
    /// This <b>reverses a decision from earlier the same day</b>: "the log file is only
    /// written in Debug, Release doesn't need to". That approach used
    /// <c>[Conditional("DEBUG")]</c> to make the whole call, arguments included, vanish in
    /// Release, saving a once-a-minute string interpolation; but the cost -- already spelled
    /// out in this class's own comment -- is that <b>the UI is silent toward the user</b>
    /// (not a word below the divider explains itself), and the log is the <b>only</b> place
    /// anyone can find out "what actually went wrong" after the fact (§8.1a). If something
    /// fails in Release (ActivityWatch unreachable, a broken rules.json, an exception
    /// thrown), all the screen shows is one greyed-out button, and nobody can investigate.
    ///
    /// The overhead saved wasn't worth mentioning anyway: one line a minute, at most fifty lines for a whole round.
    ///
    /// Write volume still has a backstop -- past 1MB it rolls over once, keeping only one
    /// old copy (see <see cref="Roll"/>), so leaving it running for a long time doesn't eat
    /// up the disk.
    ///
    /// To go back to a middle ground of "only leave a trace when something breaks": add
    /// <c>[Conditional("DEBUG")]</c> back to <see cref="Info"/> and <see cref="Warn"/>,
    /// leaving <see cref="Error"/> without it. A normal round then writes nothing at all,
    /// while a failure still leaves evidence.
    /// </summary>
    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>Logs an error. **The exception's full detail must go in** -- there's no UI message to fall back on.</summary>
    public static void Error(string what, Exception e)
        => Write("ERROR", $"{what}: {e.GetType().Name}: {e.Message}"
                          + (e.InnerException is { } inner ? $"  <- {inner.GetType().Name}: {inner.Message}" : ""));

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Roll();
                File.AppendAllText(Path_,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {level}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Couldn't write the log, fine. Logging must never crash the program --
            // that would turn a minor hiccup into a real failure.
        }
    }

    /// <summary>Rolls over past 1MB, keeping only one old copy. Leaving it running for a long time doesn't eat up the disk.</summary>
    private static void Roll()
    {
        var f = new FileInfo(Path_);
        if (!f.Exists || f.Length < MaxBytes) return;
        var old = Path_ + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(Path_, old);
    }
}
