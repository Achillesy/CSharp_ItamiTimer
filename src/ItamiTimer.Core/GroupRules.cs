using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ItamiTimer.Core;

/// <summary>One match rule in rules.json. At least one of app or title must be set.</summary>
public sealed class MatchRule
{
    public string? App { get; init; }
    public string? Title { get; init; }
}

/// <summary>One goal. Matching **any** rule within the group counts as a match (§5.2).</summary>
public sealed class GoalGroup
{
    /// <summary>A stale goal is disabled rather than deleted (§5.2.1). Enabled by default.</summary>
    public bool Disabled { get; init; }

    public IReadOnlyList<MatchRule> Rules { get; init; } = [];

    // Accumulated focus time used to live here; moved to the App's during.json on
    // 2026-08-02 (§11.2). rules.json is **hand-written by the user**, the program only
    // reads it, never writes -- write to it once and every comment is gone.
}

/// <summary>
/// The raw shape of rules.json. **The one and only type model for the entire file** --
/// whatever the file can contain, this class must have a field for.
///
/// ⚠️ Before 2026-08-02, <c>executeCommand</c> wasn't here; the App's <c>Command</c> read
/// it again separately using a bare <c>JsonDocument</c>. **One file, two read paths, two
/// sets of parsing options, kept in sync only by a human remembering to** -- and it bit
/// twice: once because that path didn't turn on `Skip` (writing a comment made goals work
/// fine while Execute silently failed), once because `TryGetProperty` is case-sensitive
/// while this path isn't (the same symptom). Both times: "half the file works, the other
/// half silently doesn't, and the program starts up as if nothing were wrong".
///
/// Now everything is parsed in one pass. **Adding a new section to this file means adding
/// a field to this class**, not starting a second read path.
/// </summary>
public sealed class RulesFile
{
    public Dictionary<string, GoalGroup> Groups { get; init; } = [];

    /// <summary>The command table the alarm's Execute uses, keyed by OS (§9). A value can be a single string or a list.</summary>
    [JsonConverter(typeof(CommandTableConverter))]
    public Dictionary<string, IReadOnlyList<string>>? ExecuteCommand { get; init; }
}

/// <summary>
/// How <c>executeCommand</c> is read: a value **accepts either a single string or a
/// list**, normalized into a list. OS names (windows / macos) are case-insensitive --
/// matching the rest of this file.
/// </summary>
internal sealed class CommandTableConverter : JsonConverter<Dictionary<string, IReadOnlyList<string>>>
{
    public override Dictionary<string, IReadOnlyList<string>> Read(
        ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var table = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("executeCommand must be an object keyed by OS.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var os = reader.GetString()!;
            reader.Read();
            var list = new List<string>();
            if (reader.TokenType == JsonTokenType.String)
            {
                list.Add(reader.GetString()!);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (reader.TokenType == JsonTokenType.String)
                        list.Add(reader.GetString()!);
            }
            else throw new JsonException($"executeCommand.{os} must be a string or an array of strings.");

            table[os] = list;
        }
        return table;
    }

    public override void Write(Utf8JsonWriter w, Dictionary<string, IReadOnlyList<string>> v, JsonSerializerOptions o)
        => throw new NotSupportedException("The program never writes rules.json.");
}

/// <summary>
/// Compiled rules. Pure logic, touches neither time nor the network.
///
/// Answers exactly one question: <b>does this (app, title) match that goal?</b>
/// The rest of judgment (a match counts as <c>Focused</c>, everything else is
/// <c>OffTask</c>, afk overrides everything) lives in <see cref="Judgment.Paint"/>,
/// expressed through covering order -- this class stays out of it.
///
/// The `Neutral` category / `ignore` list / self-exemption have all been removed --
/// **everything else is OffTask, fail-closed**.
/// </summary>
public sealed class GroupRules
{
    private sealed record CompiledRule(Regex? App, Regex? Title);
    private sealed record CompiledGroup(string Name, bool Disabled, IReadOnlyList<CompiledRule> Rules);

    private readonly IReadOnlyList<CompiledGroup> _groups;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _commands;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private GroupRules(IReadOnlyList<CompiledGroup> groups,
                       IReadOnlyDictionary<string, IReadOnlyList<string>> commands)
    {
        _groups = groups;
        _commands = commands;
    }

    /// <summary>
    /// The command table for one OS (§9). **Callers should only ever use entry 0** -- this
    /// is a collection of frequently-used commands, and switching commands is done by
    /// reordering the list, not through the UI (DECISIONS E9). An empty list if nothing's configured.
    /// </summary>
    public IReadOnlyList<string> CommandsFor(string os)
        => _commands.TryGetValue(os, out var list) ? list : [];

    public static GroupRules Load(string path) => Parse(File.ReadAllText(path));

    public static GroupRules Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RulesFile>(json, JsonOpts)
                   ?? throw new InvalidDataException("rules.json is empty.");

        var groups = new List<CompiledGroup>();
        foreach (var (name, g) in file.Groups)
        {
            if (g.Rules.Count == 0)
                throw new InvalidDataException($"Goal \"{name}\" has no rules. An empty group matches everything, which disables the constraint.");

            var rules = new List<CompiledRule>();
            foreach (var r in g.Rules)
            {
                if (r.App is null && r.Title is null)
                    throw new InvalidDataException($"Goal \"{name}\" has a rule with neither app nor title; it would match everything.");
                rules.Add(new CompiledRule(Compile(r.App, name), Compile(r.Title, name)));
            }
            groups.Add(new CompiledGroup(name, g.Disabled, rules));
        }

        return new GroupRules(groups,
            file.ExecuteCommand ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
    }

    private static Regex? Compile(string? pattern, string where)
    {
        if (pattern is null) return null;
        try { return new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"Invalid regex in \"{where}\": {pattern}  ({e.Message})", e);
        }
    }

    /// <summary>The goals shown in the UI's selectable list (disabled ones don't appear).</summary>
    public IReadOnlyList<string> SelectableGroups
        => _groups.Where(g => !g.Disabled).Select(g => g.Name).ToList();

    /// <summary>One rule: if both app and title are set, both must match; whichever side is unset places no constraint (§5.2).</summary>
    private static bool RuleMatches(CompiledRule r, string app, string title)
        => (r.App is null || r.App.IsMatch(app))
        && (r.Title is null || r.Title.IsMatch(title));

    /// <summary>Matching **any** rule within the group is a match. A disabled group never matches (§5.2.1).</summary>
    public bool GroupMatches(string groupName, string app, string title)
    {
        var g = _groups.FirstOrDefault(x => x.Name == groupName);
        if (g is null || g.Disabled) return false;
        return g.Rules.Any(r => RuleMatches(r, app, title));
    }

    // `Classify` and `IntervalKind` used to live here -- the interface for the `Replay`
    // interval model. Removed 2026-08-02: the production path only uses GroupMatches
    // (called by Judgment.Paint's layered covering), and Classify had ended up used only
    // by tests -- **a guardrail watching over code that never runs**, the same disease as Replay.
}
