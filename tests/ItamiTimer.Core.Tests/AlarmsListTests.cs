using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// Alarms 清单（DESIGN §9.1）的解析和挑选规则。全是纯函数，`now` 永远是参数，跟
/// <c>AlarmClockTests</c> 一样的路数。3.7.0 起数据源是一份标准 crontab
/// （<c>alarms.cron</c>），不再是 Markdown 清单。
/// </summary>
public class AlarmsListTests
{
    private static DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0);

    // ---------------------------------------------------------------- Parse

    [Fact]
    public void 五个字段加提醒文字被认成一条()
    {
        var entries = AlarmsList.Parse("0 14 * * * 吃药");
        Assert.Single(entries);
        Assert.Equal("吃药", entries[0].Text);
        Assert.True(entries[0].Schedule.Matches(At(2026, 1, 1, 14, 0)));
    }

    [Fact]
    public void 井号注释和空行一律跳过()
    {
        var text = """
            # ItamiTimer alarms
            # 分 时 日 月 周   提醒文字

            0 14 * * * 吃药
            # 0 7 * * *  这条暂时不响
            """;
        var entries = AlarmsList.Parse(text);
        Assert.Single(entries);
        Assert.Equal("吃药", entries[0].Text);
    }

    [Fact]
    public void 写错的表达式安静跳过_不拖累同一份文件里别的行()
    {
        var text = """
            60 14 * * * 分钟越界
            0 14 * * * 吃药
            """;
        var entries = AlarmsList.Parse(text);
        Assert.Single(entries);
        Assert.Equal("吃药", entries[0].Text);
    }

    [Fact]
    public void 字段不够五个的行跳过()
        => Assert.Empty(AlarmsList.Parse("0 14 * *"));

    [Fact]
    public void 只有表达式没有文字的行跳过_没有文字的提醒没有意义()
        => Assert.Empty(AlarmsList.Parse("0 14 * * *"));

    [Fact]
    public void 文字一直取到行尾_空格和标点都原样保留()
    {
        var entries = AlarmsList.Parse("0 9 * * * 复诊，带上上次的报告");
        Assert.Equal("复诊，带上上次的报告", entries[0].Text);
    }

    [Fact]
    public void 字段之间多打几个空格也认()
    {
        var entries = AlarmsList.Parse("  30   21   *  *  1-5    晚间打卡");
        Assert.Single(entries);
        Assert.Equal("晚间打卡", entries[0].Text);
        Assert.True(entries[0].Schedule.Matches(At(2026, 1, 5, 21, 30)));
    }

    [Fact]
    public void 别名行也认()
    {
        var entries = AlarmsList.Parse("@daily 每日回顾");
        Assert.Single(entries);
        Assert.Equal("每日回顾", entries[0].Text);
        Assert.True(entries[0].Schedule.Matches(At(2026, 1, 1, 0, 0)));
    }

    [Fact]
    public void 别名后面没有文字也跳过()
        => Assert.Empty(AlarmsList.Parse("@daily"));

    [Fact]
    public void reboot那一行跟写错的行同一个下场()
        => Assert.Empty(AlarmsList.Parse("@reboot 开机提醒"));

    // ---------------------------------------------------------------- Due

    [Fact]
    public void 只返回严格晚于水位线的分钟_防止同一分钟重复触发()
    {
        var entries = AlarmsList.Parse("0 14 * * * 吃药");
        var after = new DateTime(2026, 1, 1, 14, 0, 0);    // 水位线正好落在这一分钟
        Assert.Empty(AlarmsList.Due(entries, after, new DateTime(2026, 1, 1, 14, 0, 30)));
    }

    [Fact]
    public void 水位线在上一分钟时这一分钟照常到点()
    {
        var entries = AlarmsList.Parse("0 14 * * * 吃药");
        var due = AlarmsList.Due(entries,
            new DateTime(2026, 1, 1, 13, 59, 30), new DateTime(2026, 1, 1, 14, 0, 2));
        Assert.Single(due);
        Assert.Equal(At(2026, 1, 1, 14, 0), due[0].At);
    }

    [Fact]
    public void 同一分钟多条按文件顺序返回_一条都不合并()
    {
        var text = """
            0 14 * * * 吃药
            0 14 * * * 月度对账
            0 14 * * * 周报提交
            """;
        var due = AlarmsList.Due(AlarmsList.Parse(text),
            At(2026, 1, 1, 13, 59), At(2026, 1, 1, 14, 0));
        Assert.Equal(["吃药", "月度对账", "周报提交"], due.Select(e => e.Text));
    }

    [Fact]
    public void 一次扫到多分钟时按时间排序()
    {
        var text = """
            5 14 * * * 晚
            0 14 * * * 早
            """;
        var due = AlarmsList.Due(AlarmsList.Parse(text),
            At(2026, 1, 1, 13, 0), At(2026, 1, 1, 15, 0));
        Assert.Equal(["早", "晚"], due.Select(e => e.Text));
    }

    [Fact]
    public void 关机期间错过的一律不补响_水位线定在启动那一刻()
    {
        // 程序关闭期间错过的条目：重新打开后水位线定在启动那一刻，之前的直接跳过
        var entries = AlarmsList.Parse("0 9 * * * 关机期间错过的");
        var startedAt = new DateTime(2026, 1, 1, 10, 0, 0);
        Assert.Empty(AlarmsList.Due(entries, startedAt, At(2026, 1, 1, 10, 1)));
    }

    [Fact]
    public void 回溯封顶十二小时_一次休眠唤醒不会吐出成百上千条()
    {
        // 每小时一次的规则 + 落后两天的水位线：只补最近 12 小时那 12 个整点
        var entries = AlarmsList.Parse("0 * * * * 整点");
        var due = AlarmsList.Due(entries, At(2025, 12, 30, 12, 0), At(2026, 1, 1, 12, 0));
        Assert.Equal(12, due.Count);
        Assert.Equal(At(2026, 1, 1, 1, 0), due[0].At);
        Assert.Equal(At(2026, 1, 1, 12, 0), due[^1].At);
    }

    [Fact]
    public void 空清单不会到点()
        => Assert.Empty(AlarmsList.Due([], At(2026, 1, 1, 0, 0), At(2026, 1, 1, 12, 0)));

    // ---------------------------------------------------------------- NextDue / Next

    [Fact]
    public void 下一条返回那一分钟上的全部条目()
    {
        var text = """
            0 14 * * * 吃药
            0 14 * * * 月度对账
            """;
        var next = AlarmsList.NextDue(AlarmsList.Parse(text), At(2026, 1, 1, 8, 0));
        Assert.Equal(["吃药", "月度对账"], next.Select(e => e.Text));
        Assert.All(next, e => Assert.Equal(At(2026, 1, 1, 14, 0), e.At));
    }

    [Fact]
    public void 下一条命中即停_更晚的那一分钟不算在内()
    {
        var text = """
            0 15 * * * 晚
            0 14 * * * 早
            """;
        var next = AlarmsList.NextDue(AlarmsList.Parse(text), At(2026, 1, 1, 8, 0));
        Assert.Equal(["早"], next.Select(e => e.Text));
    }

    [Fact]
    public void 视野是十二小时_更远的安排根本不找()
    {
        // 每年 1 月 1 日 03:00：从 6 月看过去远在十二小时之外
        var entries = AlarmsList.Parse("0 3 1 1 * 年度备份");
        Assert.Empty(AlarmsList.NextDue(entries, At(2026, 6, 1, 8, 0)));
        Assert.Null(AlarmsList.Next(entries, At(2026, 6, 1, 8, 0)));
    }

    [Fact]
    public void 当前这一分钟不算下一条()
    {
        var entries = AlarmsList.Parse("0 14 * * * 吃药");
        // 14:00 这一拍刚响过，"下一条"应该是明天的 14:00 —— 已经在十二小时之外
        Assert.Empty(AlarmsList.NextDue(entries, At(2026, 1, 1, 14, 0)));
    }

    [Fact]
    public void 空清单没有下一条()
    {
        Assert.Empty(AlarmsList.NextDue([], At(2026, 1, 1, 8, 0)));
        Assert.Null(AlarmsList.Next([], At(2026, 1, 1, 8, 0)));
    }

    // ---------------------------------------------------------------- DotPosition

    [Fact]
    public void 红圈角度跟黄针同一个换算_对十二小时取余()
    {
        var next = new AlarmEntry(At(2026, 8, 6, 14, 30), "吃药");
        var now = At(2026, 8, 6, 8, 0);   // 6.5 小时以内
        Assert.Equal((14 % 12) * 60 + 30, AlarmsList.DotPosition(next, now));
    }

    [Fact]
    public void 超过十二小时不画红圈()
    {
        // 现在 8 点，下一条是 5 天后的 14 点 —— mod-12 会画出"2 点钟方向"这种误导性的位置
        var next = new AlarmEntry(At(2026, 8, 11, 14, 0), "5 天后");
        Assert.Null(AlarmsList.DotPosition(next, At(2026, 8, 6, 8, 0)));
    }

    [Fact]
    public void 正好十二小时也不画()
    {
        var now = At(2026, 8, 6, 8, 0);
        Assert.Null(AlarmsList.DotPosition(new AlarmEntry(now.AddHours(12), "正好 12 小时后"), now));
    }

    [Fact]
    public void 没有下一条就不画()
        => Assert.Null(AlarmsList.DotPosition(null, At(2026, 8, 6, 8, 0)));
}
