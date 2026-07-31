using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ItamiTimer.Core;

/// <summary>rules.json 里的一条匹配规则（DESIGN.md §5.2）。app 和 title 至少要有一个。</summary>
public sealed class MatchRule
{
    public string? App { get; init; }
    public string? Title { get; init; }
}

/// <summary>一个小目标。组内**任一**条规则命中即算命中（§5.2）。</summary>
public sealed class GoalGroup
{
    /// <summary>过期的小目标屏蔽而不是删掉（§5.2.1）。缺省即启用。</summary>
    public bool Disabled { get; init; }

    public IReadOnlyList<MatchRule> Rules { get; init; } = [];

    /// <summary>该组历史上累计的专注总时长（秒）。跨任务持久化，每次任务结束时更新。</summary>
    public double AccumulatedSeconds { get; set; }
}

/// <summary>rules.json 的原始形状。</summary>
public sealed class RulesFile
{
    public Dictionary<string, GoalGroup> Groups { get; init; } = [];
}

/// <summary>
/// 编译好的规则（ISSUE_FIX.md §7）。纯逻辑，不碰时间也不碰网络。
///
/// 分类逻辑（简化自旧 §5.3，去掉了 Neutral / ignore / 自身豁免）：
/// <list type="number">
///   <item>匹配选中 group → OnTask</item>
///   <item>其余 → OffTask（fail-closed）</item>
/// </list>
/// </summary>
public sealed class GroupRules
{
    private sealed record CompiledRule(Regex? App, Regex? Title);
    private sealed record CompiledGroup(string Name, bool Disabled, IReadOnlyList<CompiledRule> Rules);

    private readonly IReadOnlyList<CompiledGroup> _groups;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private GroupRules(IReadOnlyList<CompiledGroup> groups)
    {
        _groups = groups;
    }

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

        return new GroupRules(groups);
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

    /// <summary>界面上勾选列表要显示的小目标（已屏蔽的不出现）。</summary>
    public IReadOnlyList<string> SelectableGroups
        => _groups.Where(g => !g.Disabled).Select(g => g.Name).ToList();

    /// <summary>单条规则：app 和 title 都写则求与；缺省的那一边不做限制（§5.2）。</summary>
    private static bool RuleMatches(CompiledRule r, string app, string title)
        => (r.App is null || r.App.IsMatch(app))
        && (r.Title is null || r.Title.IsMatch(title));

    /// <summary>组内**任一**条规则命中即命中。已屏蔽的组永不命中（§5.2.1）。</summary>
    public bool GroupMatches(string groupName, string app, string title)
    {
        var g = _groups.FirstOrDefault(x => x.Name == groupName);
        if (g is null || g.Disabled) return false;
        return g.Rules.Any(r => RuleMatches(r, app, title));
    }

    /// <summary>
    /// 两步判定：
    ///   1. 命中选中 group → OnTask
    ///   2. 其余 → OffTask（fail-closed）
    /// </summary>
    public IntervalKind Classify(string app, string title, string? selectedGroup, out string? groupName)
    {
        if (selectedGroup is not null && GroupMatches(selectedGroup, app, title))
        {
            groupName = selectedGroup;
            return IntervalKind.OnTask;
        }

        groupName = null;
        return IntervalKind.OffTask;
    }
}
