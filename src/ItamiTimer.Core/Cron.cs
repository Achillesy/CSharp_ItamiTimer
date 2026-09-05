namespace ItamiTimer.Core;

/// <summary>
/// 一条标准 crontab 时间表达式（DESIGN §9.1）：五个字段 —— 分 时 日 月 周。
///
/// **严格照 Linux（Vixie/cronie）的语义**，不加方言、不加扩展、不做任何校验反馈：
/// 解析不了就返回 null，调用方当这一行不存在，**不记日志、不提示**（用户 2026-09-03
/// 定：写错了在 Linux 下也一样错，是自己的问题；反馈只有"成功触发时写一行历史"这一条
/// 正向渠道，见 DECISIONS）。因此这里**只判断能不能解析，从不解释为什么不能**。
///
/// 支持的语法就是 crontab(5) 那一套，一条不多：<c>*</c>、<c>5</c>、<c>1-5</c>、
/// <c>1,3,5</c>、<c>*/15</c>、<c>1-9/2</c>、<c>5/15</c>（= 5 到上限，步长 15），
/// 星期和月份认三字母名（<c>MON</c>/<c>JAN</c>，大小写不敏感），星期的 <c>0</c> 和
/// <c>7</c> 都是周日；<c>@daily</c> 那组别名照 crontab(5) 展开。
/// **<c>@reboot</c> 不支持**——它是唯一一个不表示时刻的别名，这里没有能自圆其说的
/// 语义（水位线本来就初始化成启动那一刻），跟写错的行同一个下场。
///
/// ⚠️ **日/周两栏的 OR 语义照抄 Vixie，不许"修正"成 AND**：两栏**都不以 <c>*</c>
/// 开头**时是 OR（`0 0 1 * MON` = 每月 1 号**或**每周一），只要有一栏以 <c>*</c> 开头
/// 就是 AND。这是 crontab(5) 里明文写着的行为，也是所有人都会被绊一次的地方——在一个
/// 号称"通用格式"的东西上偷偷改语义，比这个坑本身更坏。注意判据是**字段文本是不是以
/// `*` 开头**（`*/2` 也算），不是"掩码是不是全 1"，这一点跟 Vixie 的 `DOM_STAR`/
/// `DOW_STAR` 标志位完全一致。
///
/// 纯数据 + 纯函数，<see cref="Matches"/> 的时刻永远是参数，不读系统时钟。
/// </summary>
public sealed class Cron
{
    private readonly ulong _minute;   // 位 0-59
    private readonly ulong _hour;     // 位 0-23
    private readonly ulong _dom;      // 位 1-31
    private readonly ulong _month;    // 位 1-12
    private readonly ulong _dow;      // 位 0-6（7 已经归一成 0）
    private readonly bool _domStar;
    private readonly bool _dowStar;

    /// <summary>表达式原文（去掉首尾空白），只用来写进日志好让人反查是哪一行响的。</summary>
    public string Expression { get; }

    private Cron(string expression, ulong minute, ulong hour, ulong dom, ulong month, ulong dow,
                 bool domStar, bool dowStar)
    {
        Expression = expression;
        _minute = minute;
        _hour = hour;
        _dom = dom;
        _month = month;
        _dow = dow;
        _domStar = domStar;
        _dowStar = dowStar;
    }

    /// <summary>三字母月份名，下标 + 1 = 月份值。</summary>
    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>三字母星期名，下标 = 星期值（0 = 周日，跟 <see cref="DayOfWeek"/> 一致）。</summary>
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>
    /// 解析五个字段。<paramref name="fields"/> 必须正好 5 个，任何一个字段有问题就返回
    /// null——**不区分"哪个字段错了"**，调用方也不需要知道。
    /// </summary>
    public static Cron? TryParse(string minute, string hour, string dom, string month, string dow)
    {
        if (!TryField(minute, 0, 59, null, out var minuteMask)) return null;
        if (!TryField(hour, 0, 23, null, out var hourMask)) return null;
        if (!TryField(dom, 1, 31, null, out var domMask)) return null;
        if (!TryField(month, 1, 12, MonthNames, out var monthMask)) return null;
        if (!TryField(dow, 0, 7, DayNames, out var dowMask)) return null;

        // 星期 7 == 0（周日）：crontab(5) 两种写法都收，建好位再归一，这样 `5-7` 这种
        // 跨过 7 的范围也能正确落到 {5, 6, 0}。
        if ((dowMask & (1UL << 7)) != 0) dowMask = (dowMask | 1UL) & ~(1UL << 7);

        var expression = $"{minute} {hour} {dom} {month} {dow}";
        return new Cron(expression, minuteMask, hourMask, domMask, monthMask, dowMask,
                        dom.StartsWith('*'), dow.StartsWith('*'));
    }

    /// <summary>
    /// 解析 <c>@daily</c> 那组别名，照 crontab(5) 展开成等价的五字段。不认识的
    /// （含 <c>@reboot</c>）返回 null。
    /// </summary>
    public static Cron? TryParseAlias(string alias) => alias.ToLowerInvariant() switch
    {
        "@yearly" or "@annually" => TryParse("0", "0", "1", "1", "*"),
        "@monthly"               => TryParse("0", "0", "1", "*", "*"),
        "@weekly"                => TryParse("0", "0", "*", "*", "0"),
        "@daily" or "@midnight"  => TryParse("0", "0", "*", "*", "*"),
        "@hourly"                => TryParse("0", "*", "*", "*", "*"),
        _                        => null,
    };

    /// <summary>
    /// 这个时刻命中没有。**只看到分钟为止**——秒和更细的部分完全不参与判断，因为
    /// crontab 的最小粒度就是分钟，而这个程序的节拍也正好是每整分钟一次（DESIGN §9.2）。
    /// </summary>
    public bool Matches(DateTime t)
    {
        if (!Bit(_minute, t.Minute)) return false;
        if (!Bit(_hour, t.Hour)) return false;
        if (!Bit(_month, t.Month)) return false;

        var domOk = Bit(_dom, t.Day);
        var dowOk = Bit(_dow, (int)t.DayOfWeek);

        // 见类注释：有一栏以 `*` 开头就 AND，两栏都不是就 OR（Vixie 的 DOM_STAR/DOW_STAR）
        return _domStar || _dowStar ? domOk && dowOk : domOk || dowOk;
    }

    private static bool Bit(ulong mask, int n) => (mask & (1UL << n)) != 0;

    /// <summary>
    /// 一个字段（可以是逗号分隔的一串）。<paramref name="names"/> 非 null 时额外认
    /// 三字母名。任何形式问题——非数字、越界、步长 ≤ 0、范围反着写——一律 false。
    /// </summary>
    private static bool TryField(string text, int min, int max, string[]? names, out ulong mask)
    {
        mask = 0;
        if (text.Length == 0) return false;

        foreach (var item in text.Split(','))
        {
            if (!TryItem(item, min, max, names, out var bits)) return false;
            mask |= bits;
        }
        return mask != 0;
    }

    /// <summary>逗号里的一项：<c>*</c> / <c>a</c> / <c>a-b</c>，后面都可以再跟 <c>/step</c>。</summary>
    private static bool TryItem(string item, int min, int max, string[]? names, out ulong bits)
    {
        bits = 0;
        if (item.Length == 0) return false;

        var step = 1;
        var slash = item.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(item[(slash + 1)..], out step) || step <= 0) return false;
            item = item[..slash];
            if (item.Length == 0) return false;
        }

        int lo, hi;
        if (item == "*")
        {
            lo = min;
            hi = max;
        }
        else
        {
            var dash = item.IndexOf('-', 1);   // 从 1 开始找：这些字段没有负数，`-` 只可能是范围
            if (dash >= 0)
            {
                if (!TryValue(item[..dash], min, max, names, out lo)) return false;
                if (!TryValue(item[(dash + 1)..], min, max, names, out hi)) return false;
                if (hi < lo) return false;
            }
            else
            {
                if (!TryValue(item, min, max, names, out lo)) return false;
                // crontab(5)：`a/step` 等价于 `a-上限/step`（单值 + 步长才这样，
                // 单值不带步长就只是它自己）
                hi = slash >= 0 ? max : lo;
            }
        }

        for (var v = lo; v <= hi; v += step) bits |= 1UL << v;
        return true;
    }

    /// <summary>一个具体的值：数字，或者（月/周字段）三字母名。</summary>
    private static bool TryValue(string text, int min, int max, string[]? names, out int value)
    {
        if (int.TryParse(text, out value)) return value >= min && value <= max;

        if (names is not null)
        {
            var lower = text.ToLowerInvariant();
            var index = Array.IndexOf(names, lower);
            if (index >= 0)
            {
                // 月份名的下标要 +1（数组从 jan 开始，值从 1 开始）；星期名下标就是值
                value = names == MonthNames ? index + 1 : index;
                return true;
            }
        }

        value = 0;
        return false;
    }
}
