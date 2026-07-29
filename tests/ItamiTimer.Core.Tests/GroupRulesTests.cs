using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class GroupRulesTests
{
    /// <summary>用户 2026-07-27 建的真实 rules.json：一个小目标，一条只写 title 的规则。</summary>
    private const string RealJson = """
        {
          "groups": {
            "学习经济学": { "rules": [ { "title": "经济学" } ] }
          },
          "ignore": [ "^explorer\\.exe$", "^unknown$" ]
        }
        """;

    private static readonly string[] Econ = ["学习经济学"];

    private static GroupRules Real() => GroupRules.Parse(RealJson);

    // ---- 只写 title 的规则要跨应用生效（§5.2），这是这条规则存在的全部理由

    [Theory]
    [InlineData("SumatraPDF.exe", "曼昆经济学原理第五版.pdf - SumatraPDF")]
    [InlineData("chrome.exe", "经济学原理 公开课 - Google Chrome")]
    [InlineData("vlc.exe", "曼昆经济学 第03讲.mp4")]
    public void 标题含经济学就算_不管用什么应用(string app, string title)
    {
        Assert.Equal(IntervalKind.OnTask, Real().Classify(app, title, Econ, out _));
    }

    [Fact]
    public void 同一个应用_标题不含关键词就是偷懒()
    {
        Assert.Equal(IntervalKind.OffTask, Real().Classify("chrome.exe", "斗破苍穹 第1章 - 起点中文网", Econ, out _));
    }

    // ---- §5.3 的四步顺序

    [Fact]
    public void 规则文件里没有的应用算偷懒_fail_closed()
    {
        Assert.Equal(IntervalKind.OffTask, Real().Classify("Weixin.exe", "微信", Econ, out _));
    }

    [Fact]
    public void ignore名单里的应用算中性_计入但不提醒()
    {
        Assert.Equal(IntervalKind.Neutral, Real().Classify("explorer.exe", "下载 - 文件资源管理器", Econ, out _));
    }

    /// <summary>
    /// §5.3 第 1 步：自身豁免必须硬编码。用户点提醒窗口或自己来看进度时，
    /// ItamiTimer 就是活跃窗口；判成 OffTask 会导致「提醒 → 用户看提醒 → 又违规」死循环。
    /// </summary>
    [Theory]
    [InlineData("ItamiTimer.exe")]
    [InlineData("itami.exe")]
    public void 自身永远是中性_不走配置也不会死循环(string app)
    {
        // 注意：rules.json 的 ignore 名单里【没有】这两个，靠的就是硬编码
        Assert.Equal(IntervalKind.Neutral, Real().Classify(app, "一袋米要扛几楼", Econ, out _));
    }

    [Fact]
    public void 没勾选任何小目标时_经济学也不算数()
    {
        Assert.Equal(IntervalKind.OffTask, Real().Classify("SumatraPDF.exe", "曼昆经济学.pdf", [], out _));
    }

    // ---- 组内是「或」，不是「与」（§5.2，2026-07-27 修订）

    [Fact]
    public void 组内多条规则是或的关系()
    {
        var rules = GroupRules.Parse("""
            {
              "groups": {
                "学习经济学": { "rules": [
                  { "title": "经济学" },
                  { "app": "^EconReader\\.exe$" }
                ] }
              }
            }
            """);
        // 第一条命中：标题有关键词，应用随便
        Assert.Equal(IntervalKind.OnTask, rules.Classify("SumatraPDF.exe", "曼昆经济学.pdf", Econ, out _));
        // 第二条命中：专门的应用，标题完全无关
        Assert.Equal(IntervalKind.OnTask, rules.Classify("EconReader.exe", "未命名文档", Econ, out _));
        // 两条都不命中
        Assert.Equal(IntervalKind.OffTask, rules.Classify("notepad.exe", "购物清单", Econ, out _));
    }

    [Fact]
    public void 单条规则里app和title都写则是与的关系()
    {
        var rules = GroupRules.Parse("""
            { "groups": { "网页学习": { "rules": [ { "app": "^chrome\\.exe$", "title": "教程" } ] } } }
            """);
        string[] g = ["网页学习"];
        Assert.Equal(IntervalKind.OnTask, rules.Classify("chrome.exe", "Blender 教程", g, out _));
        Assert.Equal(IntervalKind.OffTask, rules.Classify("chrome.exe", "微博热搜", g, out _));   // app 对、title 不对
        Assert.Equal(IntervalKind.OffTask, rules.Classify("msedge.exe", "Blender 教程", g, out _)); // title 对、app 不对
    }

    // ---- fail-closed：宁可拒绝加载，也不要静默放行（§5.2）

    [Fact]
    public void 空规则必须拒绝加载_否则勾上它等于关掉约束()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "什么都算": { "rules": [ {} ] } } }"""));
        Assert.Contains("match everything", e.Message);
    }

    [Fact]
    public void 空的规则数组也必须拒绝加载()
    {
        Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "空组": { "rules": [] } } }"""));
    }

    [Fact]
    public void 正则写错要报清楚是哪一条()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "坏的": { "rules": [ { "title": "[未闭合" } ] } } }"""));
        Assert.Contains("坏的", e.Message);
    }

    // ---- disabled（§5.2.1）

    [Fact]
    public void 屏蔽掉的小目标不出现在勾选列表里_也永不命中()
    {
        var rules = GroupRules.Parse("""
            {
              "groups": {
                "学习经济学": { "rules": [ { "title": "经济学" } ] },
                "上季度的目标": { "disabled": true, "rules": [ { "title": "Blender" } ] }
              }
            }
            """);
        Assert.Equal(["学习经济学"], rules.SelectableGroups);
        Assert.Equal(IntervalKind.OffTask, rules.Classify("blender.exe", "Blender 教程", ["上季度的目标"], out _));
    }

    [Fact]
    public void 规则文件允许写注释和结尾逗号()
    {
        var rules = GroupRules.Parse("""
            {
              // 这是注释
              "groups": { "学习经济学": { "rules": [ { "title": "经济学" }, ] }, },
            }
            """);
        Assert.Equal(IntervalKind.OnTask, rules.Classify("SumatraPDF.exe", "经济学.pdf", Econ, out _));
    }
}
