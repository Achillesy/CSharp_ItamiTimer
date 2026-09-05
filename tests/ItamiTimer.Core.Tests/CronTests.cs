using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// crontab 表达式（DESIGN §9.1）。严格照 Linux 的语义，所以这里的每一条都可以拿去
/// 跟真机上的 <c>cron</c> 对答案——**尤其是日/周两栏的 OR/AND 那几条**。
/// </summary>
public class CronTests
{
    private static Cron Parse(string expression)
    {
        var f = expression.Split(' ');
        var cron = Cron.TryParse(f[0], f[1], f[2], f[3], f[4]);
        Assert.NotNull(cron);
        return cron;
    }

    private static DateTime At(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0);

    // ---------------------------------------------------------------- 基本字段

    [Fact]
    public void 每天固定时刻只在那一分钟命中()
    {
        var cron = Parse("0 14 * * *");
        Assert.True(cron.Matches(At(2026, 1, 1, 14, 0)));
        Assert.False(cron.Matches(At(2026, 1, 1, 14, 1)));
        Assert.False(cron.Matches(At(2026, 1, 1, 13, 0)));
    }

    [Fact]
    public void 秒不参与判断_crontab的粒度就是分钟()
    {
        var cron = Parse("0 14 * * *");
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 14, 0, 37)));
    }

    [Fact]
    public void 逗号列表里的每个值都命中()
    {
        var cron = Parse("0,30 9 * * *");
        Assert.True(cron.Matches(At(2026, 1, 1, 9, 0)));
        Assert.True(cron.Matches(At(2026, 1, 1, 9, 30)));
        Assert.False(cron.Matches(At(2026, 1, 1, 9, 15)));
    }

    [Fact]
    public void 范围包含两个端点()
    {
        var cron = Parse("0 9 * * 1-5");
        Assert.True(cron.Matches(At(2026, 1, 5, 9, 0)));    // 周一
        Assert.True(cron.Matches(At(2026, 1, 9, 9, 0)));    // 周五
        Assert.False(cron.Matches(At(2026, 1, 10, 9, 0)));  // 周六
    }

    [Fact]
    public void 星号加步长从字段下限起跳()
    {
        var cron = Parse("*/15 * * * *");
        foreach (var minute in new[] { 0, 15, 30, 45 })
            Assert.True(cron.Matches(At(2026, 1, 1, 10, minute)));
        Assert.False(cron.Matches(At(2026, 1, 1, 10, 20)));
    }

    [Fact]
    public void 单值加步长等价于从这个值到上限()
    {
        var cron = Parse("5/15 * * * *");
        foreach (var minute in new[] { 5, 20, 35, 50 })
            Assert.True(cron.Matches(At(2026, 1, 1, 10, minute)));
        Assert.False(cron.Matches(At(2026, 1, 1, 10, 0)));
    }

    [Fact]
    public void 单值不带步长就只是它自己()
    {
        var cron = Parse("5 * * * *");
        Assert.True(cron.Matches(At(2026, 1, 1, 10, 5)));
        Assert.False(cron.Matches(At(2026, 1, 1, 10, 20)));
    }

    // ---------------------------------------------------------------- 名字

    [Fact]
    public void 星期认三字母名且大小写不敏感()
    {
        var cron = Parse("30 21 * * MON-fri");
        Assert.True(cron.Matches(At(2026, 1, 5, 21, 30)));    // 周一
        Assert.False(cron.Matches(At(2026, 1, 4, 21, 30)));   // 周日
    }

    [Fact]
    public void 月份认三字母名()
    {
        var cron = Parse("0 0 1 JAN *");
        Assert.True(cron.Matches(At(2026, 1, 1, 0, 0)));
        Assert.False(cron.Matches(At(2026, 2, 1, 0, 0)));
    }

    [Fact]
    public void 星期的七和零都是周日()
    {
        Assert.True(Parse("0 0 * * 7").Matches(At(2026, 1, 4, 0, 0)));
        Assert.True(Parse("0 0 * * 0").Matches(At(2026, 1, 4, 0, 0)));
    }

    [Fact]
    public void 跨过七的星期范围要绕回周日()
    {
        var cron = Parse("0 0 * * 5-7");
        Assert.True(cron.Matches(At(2026, 1, 9, 0, 0)));    // 周五
        Assert.True(cron.Matches(At(2026, 1, 10, 0, 0)));   // 周六
        Assert.True(cron.Matches(At(2026, 1, 4, 0, 0)));    // 周日（7 归一成 0）
        Assert.False(cron.Matches(At(2026, 1, 5, 0, 0)));   // 周一
    }

    // ---------------------------------------------------------------- 日/周两栏的 OR/AND

    [Fact]
    public void 日和周两栏都不以星号开头时是或_这是Vixie的语义不许改成与()
    {
        var cron = Parse("0 0 1 * MON");
        Assert.True(cron.Matches(At(2026, 1, 1, 0, 0)));    // 1 号，周四 —— 日命中
        Assert.True(cron.Matches(At(2026, 1, 5, 0, 0)));    // 5 号，周一 —— 周命中
        Assert.False(cron.Matches(At(2026, 1, 2, 0, 0)));   // 都不命中
    }

    [Fact]
    public void 有一栏以星号开头就是与_星号加步长也算星号()
    {
        var cron = Parse("0 0 */2 * MON");   // 日字段以 * 开头 -> AND
        Assert.True(cron.Matches(At(2026, 1, 5, 0, 0)));    // 周一 且 5 号（*/2 从 1 起跳，取奇数日）
        Assert.False(cron.Matches(At(2026, 1, 12, 0, 0)));  // 周一 但 12 号是偶数日
        Assert.False(cron.Matches(At(2026, 1, 7, 0, 0)));   // 7 号是奇数日 但不是周一
    }

    // ---------------------------------------------------------------- 别名

    [Theory]
    [InlineData("@daily")]
    [InlineData("@midnight")]
    public void 每日别名等价于零点整(string alias)
    {
        var cron = Cron.TryParseAlias(alias);
        Assert.NotNull(cron);
        Assert.True(cron.Matches(At(2026, 3, 17, 0, 0)));
        Assert.False(cron.Matches(At(2026, 3, 17, 0, 1)));
    }

    [Fact]
    public void 每小时别名在每个整点命中()
    {
        var cron = Cron.TryParseAlias("@hourly")!;
        Assert.True(cron.Matches(At(2026, 3, 17, 13, 0)));
        Assert.False(cron.Matches(At(2026, 3, 17, 13, 1)));
    }

    [Fact]
    public void 每周别名是周日零点()
    {
        var cron = Cron.TryParseAlias("@weekly")!;
        Assert.True(cron.Matches(At(2026, 1, 4, 0, 0)));    // 周日
        Assert.False(cron.Matches(At(2026, 1, 5, 0, 0)));
    }

    [Fact]
    public void 每月别名是每月一号零点()
    {
        var cron = Cron.TryParseAlias("@monthly")!;
        Assert.True(cron.Matches(At(2026, 5, 1, 0, 0)));
        Assert.False(cron.Matches(At(2026, 5, 2, 0, 0)));
    }

    [Theory]
    [InlineData("@yearly")]
    [InlineData("@annually")]
    public void 每年别名是一月一号零点(string alias)
    {
        var cron = Cron.TryParseAlias(alias)!;
        Assert.True(cron.Matches(At(2026, 1, 1, 0, 0)));
        Assert.False(cron.Matches(At(2026, 2, 1, 0, 0)));
    }

    [Fact]
    public void reboot不支持_它是唯一一个不表示时刻的别名()
        => Assert.Null(Cron.TryParseAlias("@reboot"));

    [Fact]
    public void 不认识的别名返回空()
        => Assert.Null(Cron.TryParseAlias("@fortnightly"));

    // ---------------------------------------------------------------- 解析失败

    [Theory]
    [InlineData("60 * * * *")]      // 分钟越界
    [InlineData("* 24 * * *")]      // 小时越界
    [InlineData("* * 0 * *")]       // 日从 1 开始
    [InlineData("* * 32 * *")]      // 日越界
    [InlineData("* * * 13 *")]      // 月越界
    [InlineData("* * * * 8")]       // 周越界
    [InlineData("x * * * *")]       // 不是数字也不是名字
    [InlineData("*/0 * * * *")]     // 步长为零
    [InlineData("5-1 * * * *")]     // 范围反着写
    [InlineData("0 14 * * MONDAY")] // 只认三字母名
    public void 写错的表达式一律解析失败_不区分是哪个字段错了(string expression)
    {
        var f = expression.Split(' ');
        Assert.Null(Cron.TryParse(f[0], f[1], f[2], f[3], f[4]));
    }

    [Fact]
    public void 表达式原文被留下来好让日志能反查是哪一行响的()
        => Assert.Equal("30 21 * * 1-5", Parse("30 21 * * 1-5").Expression);
}
