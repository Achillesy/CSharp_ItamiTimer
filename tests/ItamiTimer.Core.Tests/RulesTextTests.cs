using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// <see cref="RulesText.MoveToFront"/>：`itami --list` 选中一条之后就是靠它改写文件的。
///
/// 这里几乎每一条测试问的都是同一件事——**除了顺序，别的什么都不许变**。rules.json 是
/// 用户手写的文件，注释、缩进、其它键的顺序都是用户的东西，改顺序时顺手抹掉它们属于
/// "不报错、只是安静地毁掉用户数据"，正是这个项目最该防的那一类。
/// </summary>
public class RulesTextTests
{
    private const string Sample = """
        {
          "Groups": { "学习": { "Rules": [ { "App": "^Code$" } ] } },
          "executeCommand": {
            "windows": [
              "shutdown /s /t 0",
              "explorer"
            ],
            "macos": [
              "osascript -e 'tell application \"System Events\" to restart'",
              "open /System/Library/CoreServices/Finder.app",
              "pmset displaysleepnow"
            ]
          }
        }
        """;

    [Fact]
    public void PromotingAnEntryPutsItFirstAndKeepsTheOthersInOrder()
    {
        var after = RulesText.MoveToFront(Sample, "macos", 2)!;
        var list = GroupRules.Parse(after).CommandsFor("macos");

        Assert.Equal("pmset displaysleepnow", list[0]);
        Assert.Equal("osascript -e 'tell application \"System Events\" to restart'", list[1]);
        Assert.Equal("open /System/Library/CoreServices/Finder.app", list[2]);
    }

    [Fact]
    public void TheOtherOsArrayIsLeftCompletelyUntouched()
    {
        var after = RulesText.MoveToFront(Sample, "macos", 1)!;
        var win = GroupRules.Parse(after).CommandsFor("windows");

        Assert.Equal(["shutdown /s /t 0", "explorer"], win);
    }

    [Fact]
    public void IndentationAndSurroundingTextSurviveByteForByte()
    {
        var after = RulesText.MoveToFront(Sample, "macos", 1)!;

        // 只有 macos 数组内部的三行换了顺序：行数不变、缩进不变、groups 那段原样还在。
        Assert.Equal(Sample.Split('\n').Length, after.Split('\n').Length);
        Assert.Contains("""  "Groups": { "学习": { "Rules": [ { "App": "^Code$" } ] } },""", after);
        Assert.Contains("      \"open /System/Library/CoreServices/Finder.app\",", after);
    }

    [Fact]
    public void AnEmbeddedDoubleQuoteInsideACommandIsNotDisturbed()
    {
        // 正是 2026-08-08 那个 bug 的形状（DECISIONS L1）：命令自己带双引号。
        // 搬运走的是原文，转义序列必须一个字符都不动。
        var after = RulesText.MoveToFront(Sample, "macos", 2)!;
        Assert.Contains(@"\""System Events\""", after);
        Assert.Equal(
            "osascript -e 'tell application \"System Events\" to restart'",
            GroupRules.Parse(after).CommandsFor("macos")[1]);
    }

    [Fact]
    public void PromotingTheFirstEntryIsANoOp()
        => Assert.Equal(Sample, RulesText.MoveToFront(Sample, "macos", 0));

    [Fact]
    public void AnOutOfRangeIndexRefusesInsteadOfGuessing()
    {
        Assert.Null(RulesText.MoveToFront(Sample, "macos", 9));
        Assert.Null(RulesText.MoveToFront(Sample, "macos", -1));
    }

    [Fact]
    public void AMissingOsSectionRefuses()
        => Assert.Null(RulesText.MoveToFront(Sample, "linux", 1));

    [Fact]
    public void CommentsInsideTheArrayMakeItRefuseRatherThanRiskLosingThem()
    {
        var withComment = """
            {
              "executeCommand": {
                "macos": [
                  // 关机，别手滑
                  "osascript -e 'shut down'",
                  "pmset displaysleepnow"
                ]
              }
            }
            """;
        Assert.Null(RulesText.MoveToFront(withComment, "macos", 1));
    }

    [Fact]
    public void TheOsKeyIsMatchedCaseInsensitively_SameAsTheParser()
    {
        // §15.4 那次事故：一边区分大小写、一边不区分，文件一半好用一半安静失效。
        var upper = Sample.Replace("\"macos\"", "\"macOS\"");
        var after = RulesText.MoveToFront(upper, "macos", 2)!;
        Assert.Equal("pmset displaysleepnow", GroupRules.Parse(after).CommandsFor("macos")[0]);
    }

    [Fact]
    public void ASingleLineArrayAlsoWorks()
    {
        var inline = """{ "executeCommand": { "macos": ["a", "b", "c"] } }""";
        var after = RulesText.MoveToFront(inline, "macos", 2)!;
        Assert.Equal(["c", "a", "b"], GroupRules.Parse(after).CommandsFor("macos"));
    }
}
