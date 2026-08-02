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

    // 累计专注时长曾经在这里，2026-08-02 移到 App 的 during.json（§11.2）。
    // rules.json 是**用户手写**的，程序只读不写——写一次注释就全没了。
}

/// <summary>rules.json 的原始形状。</summary>
public sealed class RulesFile
{
    public Dictionary<string, GoalGroup> Groups { get; init; } = [];
}

/// <summary>
/// 编译好的规则（DESIGN.md §5）。纯逻辑，不碰时间也不碰网络。
///
/// 只回答一个问题：<b>这个 (app, title) 命中那个小目标吗？</b>
/// 判定的其余部分（命中算 <c>Focused</c>、其余一律 <c>OffTask</c>、afk 盖住一切）
/// 在 <see cref="Judgment.Paint"/> 里，靠覆盖顺序表达，这里不掺和。
///
/// `Neutral` / `ignore` 名单 / 自身豁免全部已删除——**其余一律 OffTask，fail-closed**。
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

    // `Classify` 和 `IntervalKind` 曾经在这里——那是 `Replay` 区间模型的接口。
    // 2026-08-02 删除：生产路径只用 GroupMatches（Judgment.Paint 分层覆盖时调），
    // 而 Classify 到最后只剩测试在用——**护栏守着一段不跑的代码**，跟 Replay 同一个毛病。
}
