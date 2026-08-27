using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

public class OffTaskAttributionTests
{
    private const string Reading = "Reading";
    private static readonly DateTimeOffset MinuteStart = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    private static GroupRules Rules(string json) => GroupRules.Parse(json);

    private static readonly GroupRules TitleOnly = Rules("""
        { "groups": { "Reading": { "rules": [ { "title": "Book" } ] } } }
        """);

    [Fact]
    public void 命中当前目标的事件不算跑偏元凶()
    {
        var win = new[]
        {
            new AwEvent(MinuteStart, 60, "Reader.exe", "Book.pdf - Reader", null),
        };
        Assert.Null(OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, Reading));
    }

    [Fact]
    public void 单条不命中的事件报出app和标题()
    {
        var win = new[]
        {
            new AwEvent(MinuteStart, 60, "Chat.exe", "General - Chat", null),
        };
        Assert.Equal("Chat.exe \"General - Chat\"", OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, Reading));
    }

    [Fact]
    public void 多个不命中的窗口取占用时间最长的那个()
    {
        var win = new[]
        {
            new AwEvent(MinuteStart, 10, "Chat.exe", "General - Chat", null),
            new AwEvent(MinuteStart.AddSeconds(10), 45, "Video.exe", "Cat compilation - Video", null),
        };
        var result = OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, Reading);
        Assert.Equal("Video.exe \"Cat compilation - Video\"", result);
    }

    [Fact]
    public void 同一标题的多条心跳事件累加而不是各自独立比较()
    {
        // aw-watcher-window 的心跳会把同一个窗口切成好几条短事件，不是一条长的。
        // Chat.exe 三条心跳加起来 15s，Video.exe 单条 20s——单条更长，Video 赢。
        var win = new[]
        {
            new AwEvent(MinuteStart, 5, "Chat.exe", "General - Chat", null),
            new AwEvent(MinuteStart.AddSeconds(5), 5, "Chat.exe", "General - Chat", null),
            new AwEvent(MinuteStart.AddSeconds(10), 5, "Chat.exe", "General - Chat", null),
            new AwEvent(MinuteStart.AddSeconds(15), 20, "Video.exe", "Something - Video", null),
        };
        Assert.Equal("Video.exe \"Something - Video\"", OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, Reading));

        // 反过来：再补一条 Chat.exe 心跳，累加总数 (15s+10s=25s) 反超 Video 的 20s，
        // 确认真的是按 (app,title) 分组累加，不是简单取"最长的一条事件"。
        var win2 = win.Append(new AwEvent(MinuteStart.AddSeconds(35), 10, "Chat.exe", "General - Chat", null)).ToArray();
        Assert.Equal("Chat.exe \"General - Chat\"", OffTaskAttribution.Attribute(win2, MinuteStart, TitleOnly, Reading));
    }

    [Fact]
    public void 跨出分钟边界的事件只算重叠的那一段()
    {
        // 上一分钟遗留、这一分钟才结束的事件：事件本身 70 秒，但只有最后 10 秒落在
        // [MinuteStart, MinuteStart+60) 里，不该整段 70 秒都算进这一分钟。
        var win = new[]
        {
            new AwEvent(MinuteStart.AddSeconds(-60), 70, "Chat.exe", "General - Chat", null),
        };
        // 覆盖率只有 10 秒，另一个窗口 20 秒完全落在分钟内，应该赢。
        var win2 = win.Append(new AwEvent(MinuteStart, 20, "Video.exe", "Something - Video", null)).ToArray();
        Assert.Equal("Video.exe \"Something - Video\"", OffTaskAttribution.Attribute(win2, MinuteStart, TitleOnly, Reading));
    }

    [Fact]
    public void 没有小目标被选中时一律当作跑偏()
    {
        // 跟 Judgment.Paint 的空值语义一致：selectedGroup is null -> 永不命中。
        var win = new[] { new AwEvent(MinuteStart, 60, "Reader.exe", "Book.pdf - Reader", null) };
        Assert.Equal("Reader.exe \"Book.pdf - Reader\"", OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, null));
    }

    [Fact]
    public void 完全没有事件覆盖这一分钟时返回null()
    {
        Assert.Null(OffTaskAttribution.Attribute([], MinuteStart, TitleOnly, Reading));
    }

    [Fact]
    public void 标题为空时只显示app名()
    {
        var win = new[] { new AwEvent(MinuteStart, 60, "SomeApp.exe", null, null) };
        Assert.Equal("SomeApp.exe", OffTaskAttribution.Attribute(win, MinuteStart, TitleOnly, Reading));
    }
}
