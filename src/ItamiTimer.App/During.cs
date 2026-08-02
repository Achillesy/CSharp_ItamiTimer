using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.App;

/// <summary>
/// Accumulated focus seconds spent on each goal (set by the user 2026-08-02).
///
/// <code>
/// %LOCALAPPDATA%\ItamiTimer\during.json
/// { "accumulatedSeconds": { "Economics": 12345.0 } }
/// </code>
///
/// <b>Why it's its own file, not merged into settings.json or written back into
/// rules.json:</b>
///
/// <list type="bullet">
///   <item><c>rules.json</c> is <b>hand-written by the user</b>. The moment the program
///         wrote to it, all its comments would be gone -- `JsonCommentHandling.Skip`
///         throws comments away while reading, and there's nothing to restore them from
///         when serializing back out (`JsonNode` doesn't preserve them either -- they're
///         already gone before the tree is even built). This chain is read-only (§8.1).</item>
///   <item><c>settings.json</c> gets rewritten wholesale by the program at any time, while
///         accumulated time is the <b>one piece of data that, once lost, can never be
///         recovered</b>. Its own file is easier to back up, and easier to manually reset
///         to zero.</item>
/// </list>
///
/// <b>There's exactly one rule for when to accumulate: every second of focus is credited
/// exactly once.</b> Archiving (§4.4) credits the hour about to be evicted from the
/// buffer; a task ending credits whatever's left in the buffer. "Ending" covers three
/// things, which the user collectively calls ignore: clicking Give up, closing the program
/// while a task is running, and the 2-hour archive collapse -- which is already equivalent
/// to "abandoned an hour ago, immediately restarted".
///
/// Read/write failures are always swallowed: <b>failing to record time must never crash
/// the program.</b>
/// </summary>
public sealed class During
{
    /// <summary>
    /// Accumulated focus seconds per goal. **An integer** -- it counts cells in the
    /// buffer, not ActivityWatch event durations, so it never has a fractional part (user,
    /// 2026-08-02).
    /// </summary>
    [JsonPropertyName("accumulatedSeconds")]
    public Dictionary<string, long> AccumulatedSeconds { get; set; } = [];

    private static string Path_ => System.IO.Path.Combine(AppData.Dir, "during.json");


    public long this[string goal] => AccumulatedSeconds.GetValueOrDefault(goal, 0);

    /// <summary>Credits a stretch of focus time to a goal, and saves to disk immediately.</summary>
    /// <remarks>
    /// <b>Written to disk on every credit</b>, not waiting for exit (exit has three paths
    /// that never get there anyway: crashing, shutting down, the process being killed). A
    /// single round writes at most two or three times, a negligible cost.
    /// </remarks>
    public void Add(string goal, long seconds)
    {
        if (seconds <= 0) return;
        AccumulatedSeconds[goal] = this[goal] + seconds;
        Save();
    }

    public static During Load()
    {
        // **Not** created here if the file doesn't exist -- creating it needs to know
        // which goals exist first (see EnsureSeeded).
        if (!File.Exists(Path_)) return new During();

        var text = "";
        try
        {
            text = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<During>(text) ?? new During();
        }
        catch (Exception e)
        {
            // The seconds field changed from double to long on 2026-08-02. An old file
            // might still have fractional values (38253.4), and deserializing straight
            // into long would throw. **This is the one piece of data that, once lost, can
            // never be recovered**, so it's worth writing an extra forgiving fallback for
            // it, rather than a single Log.Error and resetting to zero.
            try
            {
                var loose = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(text);
                if (loose?.TryGetValue("accumulatedSeconds", out var d) == true)
                {
                    var recovered = new During
                    {
                        AccumulatedSeconds = d.ToDictionary(kv => kv.Key, kv => (long)Math.Round(kv.Value)),
                    };
                    Log.Warn($"during.json had fractional seconds; rounded {recovered.AccumulatedSeconds.Count} entries to whole seconds");
                    recovered.Save();
                    return recovered;
                }
            }
            catch { /* The fallback also failed, fall through to resetting to zero below */ }

            Log.Error("Failed to read during.json; starting from zero", e);
            return new During();
        }
    }

    /// <summary>
    /// When the file doesn't exist yet, seeds a file of **all zeros** using the current set
    /// of goal names (user, 2026-08-02).
    ///
    /// The number in the UI has no unit and no explanation (D6); the user can only go dig
    /// through the data directory. Finding a bare <c>{}</c> teaches nothing, but finding
    /// <c>{ "Economics": 0 }</c> makes it immediately obvious that this table is seconds
    /// indexed by goal name -- the same philosophy as rules.json giving examples instead of
    /// comments.
    ///
    /// **Only seeded at the moment of creation, never synced again afterward.** A state
    /// file shouldn't mirror a config file: if the goal name changes later, a stale zero
    /// entry just sits there unused -- it's not displayed, doesn't participate in any
    /// judgment, and reconciling it against rules.json just to clean it up is exactly what
    /// would tie the two files together.
    /// </summary>
    public void EnsureSeeded(IReadOnlyList<string> goals)
    {
        if (File.Exists(Path_)) return;
        foreach (var g in goals) AccumulatedSeconds[g] = 0;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppData.Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, AppData.JsonOptions));
        }
        catch (Exception e)
        {
            Log.Error("Failed to write during.json; this round lives only in memory", e);
        }
    }
}
