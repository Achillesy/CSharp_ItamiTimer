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
}

/// <summary>rules.json 的原始形状。</summary>
public sealed class RulesFile
{
    public Dictionary<string, GoalGroup> Groups { get; init; } = [];
    public List<string> Ignore { get; init; } = [];
}

/// <summary>
/// 编译好的规则（DESIGN.md §8 模块 2）。纯逻辑，不碰时间也不碰网络。
/// </summary>
public sealed class GroupRules
{
    private sealed record CompiledRule(Regex? App, Regex? Title);
    private sealed record CompiledGroup(string Name, bool Disabled, IReadOnlyList<CompiledRule> Rules);

    private readonly IReadOnlyList<CompiledGroup> _groups;
    private readonly IReadOnlyList<Regex> _ignore;

    /// <summary>
    /// 硬编码的自身豁免（§5.3 第 1 步）。**绝不能挪进配置文件**：
    /// 用户点提醒窗口、或自己来看进度时，ItamiTimer 就成了活跃窗口，AW 会如实
    /// 上报它；要是被判成 OffTask，就会「提醒 → 用户看提醒 → 又违规 → 再提醒」
    /// 死循环。配置写漏一行就炸掉整个程序，所以这条不进配置。
    /// </summary>
    private static readonly string[] SelfApps = ["ItamiTimer.exe", "ItamiTimer", "itami.exe", "itami"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 让用户能在 rules.json 里写注释和结尾逗号（§5.2）
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 一份什么都没有的规则。给纯番茄钟模式用（§11.1 第 4 条）—— 那时 rules.json
    /// 可能压根读不出来，而合成事件靠**自身豁免**命中 <c>Neutral</c>，本来就不经过
    /// 任何用户规则，所以给一份空的正好。
    /// </summary>
    public static readonly GroupRules Empty = new([], []);

    private GroupRules(IReadOnlyList<CompiledGroup> groups, IReadOnlyList<Regex> ignore)
    {
        _groups = groups;
        _ignore = ignore;
    }

    public static GroupRules Load(string path) => Parse(File.ReadAllText(path));

    public static GroupRules Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RulesFile>(json, JsonOpts)
                   ?? throw new InvalidDataException("rules.json is empty.");

        var groups = new List<CompiledGroup>();
        foreach (var (name, g) in file.Groups)
        {
            // fail-closed（§5.2）：空组匹配一切，勾上它等于把约束整个关掉。
            // 宁可拒绝加载并报错，也不要静默放行。
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

        var ignore = file.Ignore.Select(p => Compile(p, "ignore")!).ToList();
        return new GroupRules(groups, ignore);
    }

    private static Regex? Compile(string? pattern, string where)
    {
        if (pattern is null) return null;
        try
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"Invalid regex in \"{where}\": {pattern}  ({e.Message})", e);
        }
    }

    /// <summary>界面上勾选列表要显示的小目标（已屏蔽的不出现）。</summary>
    public IReadOnlyList<string> SelectableGroups
        => _groups.Where(g => !g.Disabled).Select(g => g.Name).ToList();

    /// <summary>
    /// 单条规则：app 和 title 都写则求与；缺省的那一边不做限制（§5.2）。
    /// 正则用 IsMatch（不要求全串命中）。
    /// </summary>
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
    /// §5.3 的四步判定顺序：
    ///   1. 是 ItamiTimer 自己？          → Neutral（硬编码，不走配置）
    ///   2. 命中任一【当前已勾选】的组？    → OnTask
    ///   3. 命中 ignore 名单？             → Neutral
    ///   4. 其它（含规则文件里没有的应用）  → OffTask（fail-closed）
    ///
    /// 第 2 步**只对已勾选的组求值**，而不是"先解析出它属于哪个组、再看那组勾没勾"。
    /// 后者有个隐蔽 bug：某应用同时命中 A 组（未勾）和 B 组（已勾），按文件顺序先
    /// 匹配到 A 就被误判成违规。按现在这样写，组之间的先后顺序完全不影响结果。
    ///
    /// <paramref name="activeGroups"/> 是全段统一的并集，不随时刻变（§5.4）。
    /// </summary>
    public IntervalKind Classify(string app, string title, IReadOnlyCollection<string> activeGroups)
    {
        if (SelfApps.Contains(app, StringComparer.OrdinalIgnoreCase))
            return IntervalKind.Neutral;

        foreach (var name in activeGroups)
            if (GroupMatches(name, app, title))
                return IntervalKind.OnTask;

        if (_ignore.Any(re => re.IsMatch(app)))
            return IntervalKind.Neutral;

        return IntervalKind.OffTask;
    }
}
