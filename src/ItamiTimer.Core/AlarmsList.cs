namespace ItamiTimer.Core;

/// <summary>
/// 一次到点：整分钟的时刻 + 原样进提示条和系统通知的文字，外加**命中的那条表达式原文**。
///
/// <paramref name="Expression"/> 只进日志（<c>Alarms list fired 23:55 [55 23 1 * *] 月度对账</c>）
/// ——那行历史是唯一的反馈渠道，多条规则时不带表达式就只知道"响了"、不知道是哪一行响的。
/// 默认空串：表盘红圈那条路（<see cref="AlarmsList.DotPosition"/>）不关心它。
/// </summary>
public readonly record struct AlarmEntry(DateTime At, string Text, string Expression = "");

/// <summary>清单里的一行：一条 crontab 时间表达式 + 它的提醒文字。</summary>
/// <remarks>
/// ⚠️ <see cref="Text"/> **永远只是文字，永远不会被执行**（3.7.0，DECISIONS）。这份文件
/// 长得跟一份真 crontab 一模一样，而这个程序另有一条真会跑命令的路（`executeCommand`，
/// 内容多半是关机），两者离得太近——一旦合流，一份看着人畜无害的提醒文件就能关机器。
/// 将来若要向 Linux 看齐做成可执行，必须单独设计、单独确认，不许"顺手统一"。
/// </remarks>
public sealed record CronEntry(Cron Schedule, string Text);

/// <summary>
/// Alarms 清单（DESIGN §9.1）：解析 <c>alarms.cron</c>、挑出该响哪一条。**纯函数，
/// <c>now</c> 永远是参数**，跟 <see cref="AlarmClock"/> 一样的路数，不用等真实时间就能测。
///
/// 3.7.0 起数据源从 Markdown 清单（一堆展开好的绝对时间戳）换成一份**标准 crontab**
/// ——用了一段时间之后发现重复的事情占绝大多数，而"上游会把每天 14:00 铺成一串具体
/// 日期"那个上游从来没存在过（推翻 DECISIONS J2/J3/J4，理由记在那里）。
///
/// **视野统一成 12 小时**：<see cref="Next"/> / <see cref="NextDue"/> 只在
/// <c>(now, now+12h]</c> 里往前找，跟表盘红圈的门槛（<see cref="DotPosition"/>）是同一个
/// 数。好处是"永不成立的表达式（比如 2 月 30 日）要能停下来"这类顾虑天然消失——720 次
/// 谓词测试封顶，不需要另设搜索上限。代价（知情）：钟面之外没有任何地方能看到更远的
/// 安排，想看去翻文件。
///
/// 程序**只读**这份文件，从不回写、不清理、不生成——"勾选跳过"那套随 Markdown 一起
/// 没了，"这条暂时不响"现在就是 crontab 自己的注释语法：行首加 <c>#</c>。
/// </summary>
public static class AlarmsList
{
    /// <summary>视野 = 12 小时，见类注释。跟 <see cref="DotPosition"/> 的门槛是同一个数。</summary>
    public const int HorizonMinutes = 12 * 60;

    /// <summary>
    /// 解析整份文件。**每一行独立**：不认识的行（空行、<c>#</c> 注释、写错的表达式、
    /// <c>@reboot</c>）一律安静跳过，不拖累整份文件，也**不记日志、不提示**
    /// ——见 <see cref="Cron"/> 的类注释。
    ///
    /// 行的形状就是 crontab 的：五个空白分隔的字段（或者一个 <c>@</c> 别名），
    /// **剩下的到行尾全是提醒文字**。文字为空的行跳过：没有文字的提醒没有意义。
    /// </summary>
    public static IReadOnlyList<CronEntry> Parse(string text)
    {
        var result = new List<CronEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var (schedule, label) = line[0] == '@' ? ParseAlias(line) : ParseFields(line);
            if (schedule is null || label.Length == 0) continue;

            result.Add(new CronEntry(schedule, label));
        }
        return result;
    }

    /// <summary><c>@daily 每日回顾</c>：第一个词是别名，剩下的是文字。</summary>
    private static (Cron?, string) ParseAlias(string line)
    {
        var cut = line.IndexOfAny([' ', '\t']);
        if (cut < 0) return (null, "");   // 只有别名没有文字
        return (Cron.TryParseAlias(line[..cut]), line[(cut + 1)..].Trim());
    }

    /// <summary><c>0 14 * * * 吃药</c>：前五个词是字段，剩下的到行尾是文字。</summary>
    private static (Cron?, string) ParseFields(string line)
    {
        var fields = new string[5];
        var at = 0;
        for (var i = 0; i < 5; i++)
        {
            while (at < line.Length && (line[at] == ' ' || line[at] == '\t')) at++;
            var start = at;
            while (at < line.Length && line[at] != ' ' && line[at] != '\t') at++;
            if (at == start) return (null, "");   // 不够 5 个字段
            fields[i] = line[start..at];
        }
        return (Cron.TryParse(fields[0], fields[1], fields[2], fields[3], fields[4]),
                line[at..].Trim());
    }

    /// <summary>
    /// <c>(after, now]</c> 区间内到点的条目，按时间排序、同一分钟内按文件顺序。
    /// **调用方自己推进 <paramref name="after"/> 这个水位线**（本类不持有任何状态）
    /// ——纯内存、一次性，程序一关就没了也无所谓：已经定了"不补响、只看未来"，重启后
    /// 本来就该从"此刻往后"重新数（DECISIONS J7）。
    ///
    /// 回溯封顶 <see cref="HorizonMinutes"/> 分钟：正常情况这个窗口就是 1 分钟，但休眠
    /// 唤醒或者 <c>_minuteBusy</c> 跳拍之后可能落后很多，而 crontab 规则是**无限**的
    /// （<c>*/5</c> 一天就是 288 次），不封顶等于让一次唤醒吐出成百上千条。
    /// </summary>
    public static IReadOnlyList<AlarmEntry> Due(
        IReadOnlyList<CronEntry> entries, DateTime after, DateTime now)
    {
        var due = new List<AlarmEntry>();
        if (entries.Count == 0) return due;

        var last = Truncate(now);
        var first = Truncate(after).AddMinutes(1);   // 水位线那一分钟本身不再算（严格大于）
        var floor = last.AddMinutes(-(HorizonMinutes - 1));
        if (first < floor) first = floor;

        for (var m = first; m <= last; m = m.AddMinutes(1))
            foreach (var entry in entries)
                if (entry.Schedule.Matches(m))
                    due.Add(new AlarmEntry(m, entry.Text, entry.Schedule.Expression));

        return due;
    }

    /// <summary>
    /// 下一个会响的分钟上的**全部**条目（按文件顺序），12 小时以内没有就是空。
    ///
    /// 返回一整组而不是一条，是因为同一分钟多条这件事在 crontab 下变得寻常，而
    /// 表盘红圈要据此画成双圈（≥2 条）、点红圈要一次列全（DESIGN §9.1）。
    /// **命中即停**——有日常规则时通常几十次谓词就出结果，只有 12 小时内一件事都没有
    /// 时才会走满 720 分钟。
    /// </summary>
    public static IReadOnlyList<AlarmEntry> NextDue(IReadOnlyList<CronEntry> entries, DateTime now)
    {
        if (entries.Count == 0) return [];

        var m = Truncate(now).AddMinutes(1);
        for (var i = 0; i < HorizonMinutes; i++, m = m.AddMinutes(1))
        {
            List<AlarmEntry>? hit = null;
            foreach (var entry in entries)
                if (entry.Schedule.Matches(m))
                    (hit ??= []).Add(new AlarmEntry(m, entry.Text, entry.Schedule.Expression));

            if (hit is not null) return hit;
        }
        return [];
    }

    /// <summary>最早的一条未来条目；12 小时内什么都没有时返回 null。</summary>
    public static AlarmEntry? Next(IReadOnlyList<CronEntry> entries, DateTime now)
    {
        var next = NextDue(entries, now);
        return next.Count > 0 ? next[0] : null;
    }

    /// <summary>
    /// 表盘小红圈的角度位置（0-719 分钟，跟 <see cref="AlarmClock.Position"/> 同一个换算：
    /// 时间点对 12 小时取余）。**只在下一条落在未来 12 小时以内时才返回非 null**——
    /// 黄针的 mod-12 换算从不骗人，前提是被取余的数从没超过 12 小时（<see cref="AlarmClock.NextRing"/>
    /// 保证这一点）；Alarms 清单的"下一条"没有这个保证，直接取余会把"5 天后 14 点"画成
    /// "2 点钟方向"、看着像"再等 6 小时"，这个误导正是要避开的（DECISIONS J8）。
    ///
    /// 顺带一个不明显但很有用的性质：**在这个 12 小时窗口内，mod-12 是双射**（窗口内
    /// 任意两个时刻相差必然小于 12 小时，不可能取余到同一个角度）。所以表盘上两条重叠
    /// 当且仅当它们**真的在同一分钟**——而那一种由双圈来表示，不会被误读成"两件事撞在
    /// 一个角度上"。
    /// </summary>
    public static double? DotPosition(AlarmEntry? next, DateTime now)
    {
        if (next is not { } n) return null;
        if (n.At - now >= TimeSpan.FromHours(12)) return null;
        return (n.At.Hour % 12) * 60 + n.At.Minute;
    }

    /// <summary>砍掉秒和更细的部分：crontab 的粒度就是分钟。</summary>
    private static DateTime Truncate(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
}
