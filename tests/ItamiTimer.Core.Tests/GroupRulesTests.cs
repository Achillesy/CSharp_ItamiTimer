using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class GroupRulesTests
{
    /// <summary>A real rules.json the user set up on 2026-07-27: one goal, one rule that only writes a title.</summary>
    private const string RealJson = """
        {
          "groups": {
            "Economics": { "rules": [ { "title": "Econ" } ] }
          }
        }
        """;

    private const string Econ = "Economics";

    private static GroupRules Real() => GroupRules.Parse(RealJson);

    // ---- A title-only rule must work across apps (§5.2)

    [Theory]
    [InlineData("SumatraPDF.exe", "Mankiw's Principles of Econ 5th Edition.pdf - SumatraPDF")]
    [InlineData("chrome.exe", "Econ Principles Open Course - Google Chrome")]
    [InlineData("vlc.exe", "Mankiw Econ Lecture 03.mp4")]
    public void ATitleContainingTheKeywordCounts_RegardlessOfApp(string app, string title)
    {
        Assert.True(Real().GroupMatches(Econ, app, title));
    }

    [Fact]
    public void SameApp_TitleWithoutTheKeywordIsOffTask()
    {
        Assert.False(Real().GroupMatches(Econ, "chrome.exe", "Anime Episode 1 - Some Streaming Site"));
    }

    // ---- fail-closed

    [Fact]
    public void AnAppNotInTheRulesFileIsOffTask_FailClosed()
    {
        Assert.False(Real().GroupMatches(Econ, "Messenger.exe", "Messenger"));
    }

    /// <summary>
    /// A group name that isn't in the rules file never matches. The case of "nothing
    /// selected" is blocked one layer up -- <see cref="Judgment.Paint"/> doesn't draw
    /// Focused at all when `selectedGroup is null`.
    /// </summary>
    [Fact]
    public void ANonexistentGroupNameNeverMatches()
    {
        Assert.False(Real().GroupMatches("NoSuchGroup", "SumatraPDF.exe", "Mankiw Econ.pdf"));
    }

    // ---- Rules within a group are OR, not AND (§5.2)

    [Fact]
    public void MultipleRulesWithinAGroupAreOred()
    {
        var rules = GroupRules.Parse("""
            {
              "groups": {
                "Economics": { "rules": [
                  { "title": "Econ" },
                  { "app": "^EconReader\\.exe$" }
                ] }
              }
            }
            """);
        Assert.True(rules.GroupMatches(Econ, "SumatraPDF.exe", "Mankiw Econ.pdf"));
        Assert.True(rules.GroupMatches(Econ, "EconReader.exe", "Untitled Document"));
        Assert.False(rules.GroupMatches(Econ, "notepad.exe", "Shopping list"));
    }

    [Fact]
    public void WhenARuleHasBothAppAndTitleTheyAreAnded()
    {
        var rules = GroupRules.Parse("""
            { "groups": { "WebStudy": { "rules": [ { "app": "^chrome\\.exe$", "title": "Tutorial" } ] } } }
            """);
        const string g = "WebStudy";
        Assert.True(rules.GroupMatches(g, "chrome.exe", "Blender Tutorial"));
        Assert.False(rules.GroupMatches(g, "chrome.exe", "Trending on Weibo"));
        Assert.False(rules.GroupMatches(g, "msedge.exe", "Blender Tutorial"));
    }

    // ---- fail-closed: better to refuse to load than to silently let everything through (§5.2)

    [Fact]
    public void AnEmptyRuleMustBeRejectedAtLoad_OtherwiseCheckingItTurnsOffTheConstraint()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "MatchesEverything": { "rules": [ {} ] } } }"""));
        Assert.Contains("match everything", e.Message);
    }

    [Fact]
    public void AnEmptyRulesArrayMustAlsoBeRejectedAtLoad()
    {
        Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "EmptyGroup": { "rules": [] } } }"""));
    }

    [Fact]
    public void ABadRegexReportsClearlyWhichOneItWas()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            GroupRules.Parse("""{ "groups": { "Broken": { "rules": [ { "title": "[unterminated" } ] } } }"""));
        Assert.Contains("Broken", e.Message);
    }

    // ---- disabled (§5.2.1)

    [Fact]
    public void ADisabledGoalDoesNotAppearInTheSelectableList_AndNeverMatches()
    {
        var rules = GroupRules.Parse("""
            {
              "groups": {
                "Economics": { "rules": [ { "title": "Econ" } ] },
                "Last quarter": { "disabled": true, "rules": [ { "title": "Blender" } ] }
              }
            }
            """);
        Assert.Equal(["Economics"], rules.SelectableGroups);
        Assert.False(rules.GroupMatches("Blender Tutorial", "Last quarter", "blender.exe"));
    }

    [Fact]
    public void TheRulesFileAllowsCommentsAndTrailingCommas()
    {
        var rules = GroupRules.Parse("""
            {
              // this is a comment
              "groups": { "Economics": { "rules": [ { "title": "Econ" }, ] }, },
            }
            """);
        Assert.True(rules.GroupMatches(Econ, "SumatraPDF.exe", "Econ.pdf"));
    }
}
