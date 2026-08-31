namespace ItamiTimer.Core;

/// <summary>
/// 镜像里的一秒：一个判定码，外加**是哪个窗口**造成的（跑偏时写日志要用，见
/// <see cref="OffTaskAttribution"/>）。
///
/// <see cref="App"/>/<see cref="Title"/> 在 <see cref="JudgmentCode.Focused"/>、
/// <see cref="JudgmentCode.OffTask"/> 上一定有值；<see cref="JudgmentCode.Afk"/> 上保留
/// 覆盖前那个窗口（人不在时屏幕上停着的还是它，写日志有用）；
/// <see cref="JudgmentCode.AwOffline"/> 上是 null。
/// </summary>
public readonly record struct MirrorSecond(JudgmentCode Code, string? App, string? Title);

/// <summary>
/// **把 ActivityWatch 的「区间事件」摊平成「每一秒一个判定」的内存镜像**（DESIGN §7.5）。
///
/// AW 记的是 `[start, start+duration)` 这样的区间，而这个程序从判定到画表盘，处处都是
/// **按秒**的（<see cref="JudgmentBuffer"/> 就是一个秒级数组）。今天这个转换在每一次
/// 查询之后重做一遍（<see cref="Judgment.Paint"/>），代价是**每查一次就要把 AW 那边的
/// 事件重新拉一遍**——而 <see cref="AwClient.FetchEventsAsync"/> 为了绕开 T1 会统一往前
/// 放宽 6 小时，实测一次 337 条 / 65.7 KB。查询频率一提高就撞墙。
///
/// 镜像把这个转换**做一次、留下来**：以后任何人想知道"某一秒你在干什么"，都是内存里
/// 一次数组下标，零成本。
///
/// **纯逻辑，不碰网络也不碰时钟**——`now` 和事件都是参数，跟 <see cref="Judgment"/>、
/// <see cref="GroupRules"/> 一样可以直接单测。拉数据那半边在 App 层。
///
/// ## 生命周期
///
/// **点 Start 时创建，任务结束时丢弃**（用户 2026-08-29）。理由不是省内存，是
/// <see cref="JudgmentCode.Focused"/>/<see cref="JudgmentCode.OffTask"/>
/// **相对于选中的小目标才有意义**——而小目标恰好在点 Start 那一刻锁定（那之后单选框
/// 就禁用了）。任务期内目标不可能变，镜像里存的判定码全程有效。
///
/// ## 三条规则
///
/// 1. **从新事件自己的 <c>start</c> 开始重写**，不是从某个固定窗口。AW 的滞后（T3，实测
///    6~12 秒）和 afk 的回溯写入（T5，起身 3 分钟后才写、start 回填到 3 分钟前）**用同
///    一条规则一起吃掉**：事件自带 start，重写范围由数据决定，不需要任何常数。
/// 2. **推进即清空**：写第 S 秒时，把「上次写到的秒 → S」之间跳过的槽位全部填成
///    <see cref="JudgmentCode.AwOffline"/>。睡眠、卡顿、系统时间被改，全部由这一条兜住
///    ——否则环里躺着的旧数据会被当成睡眠期间的观测（跟 §15.6 同一类事故）。
/// 3. **空隙用预测填**：AW 的事件之间**不是首尾相接的**（实测：切窗口时留 ~1 秒空隙，
///    因为 watcher 一秒一轮询、duration 只覆盖到最后一次确认的轮询），末尾还有 3~10 秒
///    是 AW 还没吐出来的。这些秒一律**沿用前一秒的状态**（用户 2026-08-29 定：预测错了
///    无所谓，最多反色错一下）。预测天然有上限——环只有 <see cref="Capacity"/> 秒。
/// </summary>
public sealed class AwMirror
{
    /// <summary>账本每分钟重画的窗口宽度（<see cref="JudgmentBuffer.QueryWindowSeconds"/>），镜像至少要装下它。</summary>
    public const int WindowSeconds = JudgmentBuffer.QueryWindowSeconds;

    /// <summary>
    /// 差一余量。⚠️ **不是可有可无的冗余**：`Cover` 要的是 `[整分钟-240s, 整分钟)`，
    /// 而镜像覆盖的是 `[now-240s, now]`，`now` 总是**略晚于**那个整分钟（分钟节拍在边界后
    /// ≤33ms 触发，赶上 GC 或忙碌就是几秒）——不留余量的话，`整分钟-240s` 那一秒刚好
    /// 掉出环外，**每分钟固定丢最老一秒**，而且方向是 fail-open（白送），不报错。
    /// </summary>
    public const int Padding = 5;

    public const int Capacity = WindowSeconds + Padding;

    private readonly JudgmentCode[] _code = new JudgmentCode[Capacity];
    private readonly string?[] _app = new string?[Capacity];
    private readonly string?[] _title = new string?[Capacity];

    private readonly GroupRules _rules;
    private readonly string? _group;

    /// <summary>已经写到的最新一秒（含）。</summary>
    public DateTimeOffset Newest { get; private set; }

    /// <summary>
    /// **真实事件画到的最新一秒**（不含预测）。`Newest` 与它之间那一段，是 AW 还没吐出来、
    /// 只能外推的部分——`Predict` 第 ② 步每拍拿它重铺一次。<c>default</c> = 还没画过任何事件。
    /// </summary>
    private DateTimeOffset _observedThrough;

    /// <summary>环里最老的那一秒（含）。</summary>
    public DateTimeOffset Oldest => Newest.AddSeconds(-(Capacity - 1));

    /// <param name="now">创建时刻，一般就是任务的起点。</param>
    /// <param name="rules">编译好的规则。</param>
    /// <param name="selectedGroup">这一轮锁定的小目标；null 时一切都算跑偏（跟 <see cref="Judgment.Paint"/> 一致）。</param>
    public AwMirror(DateTimeOffset now, GroupRules rules, string? selectedGroup)
    {
        _rules = rules;
        _group = selectedGroup;
        Newest = Floor(now);
        Array.Fill(_code, JudgmentCode.AwOffline);
    }

    /// <summary>这一秒的观测。落在环外（太老或还没到）一律 <see cref="JudgmentCode.AwOffline"/>——「我没有记录」跟「AW 没有记录」在账本眼里本来就是同一件事（§3.1）。</summary>
    public MirrorSecond At(DateTimeOffset second)
    {
        var s = Floor(second);
        if (s > Newest || s < Oldest) return new MirrorSecond(JudgmentCode.AwOffline, null, null);
        var i = Index(s);
        return new MirrorSecond(_code[i], _app[i], _title[i]);
    }

    /// <summary><c>[from, to)</c> 这一段，每秒一个。范围外的秒同 <see cref="At"/>。</summary>
    public List<MirrorSecond> Slice(DateTimeOffset from, DateTimeOffset to)
    {
        var a = Floor(from);
        var n = (int)Math.Max(0, (Floor(to) - a).TotalSeconds);
        var list = new List<MirrorSecond>(n);
        for (var i = 0; i < n; i++) list.Add(At(a.AddSeconds(i)));
        return list;
    }

    /// <summary>
    /// AW 这一拍没答应（连不上、超时）：**只推进，不预测**。跳过的秒留在
    /// <see cref="JudgmentCode.AwOffline"/> 上——「查询失败」和「AW 确实没有这一秒的记录」
    /// 对账本是同一件事（都算专注，§3.1 的知情 fail-open），区别只写进日志，不编码进状态
    /// （用户 2026-08-29：复用 `AwOffline`，不新增码）。
    ///
    /// ⚠️ **不新增码是有硬理由的**：<see cref="JudgmentCode"/> 的数值大小是「覆盖优先级」，
    /// 而且「算专注 ⟺ <c>&gt;= Focused</c>」——`AwOffline = 5 &gt; Focused = 4` 正是靠这个
    /// 才让「无记录算专注」成立。新插一个码，插在哪儿都会动到这两条规则（DECISIONS H1）。
    /// </summary>
    public void MarkUnavailable(DateTimeOffset now) => Advance(Floor(now), carryForward: false);

    /// <summary>
    /// 把这一批事件吸收进来，并把镜像推进到 <paramref name="now"/>。
    ///
    /// 顺序是**窗口 → afk → 预测**：
    /// <list type="number">
    ///   <item>窗口事件按 start 升序画，后画的覆盖先画的（同一秒里以**最新打开的那个**为准，
    ///         用户 2026-08-29 定的简化；实测 3 小时 10795 秒里含两个窗口的秒是 <b>0</b> 条
    ///         ——AW 的事件之间有 ~1 秒空隙，根本不重叠，所以这个简化的代价是 0）。</item>
    ///   <item>afk 画在最后、覆盖一切（跟 <see cref="Judgment.Paint"/> 同一条覆盖顺序）。
    ///         ⚠️ afk 桶里有 <b>start 相同、duration 不同的重叠事件</b>（实测），所以这里
    ///         **把所有 afk 事件都画上去**，不能只取最新那条——取到较短的那条会把在座时间
    ///         算多。</item>
    ///   <item>剩下的 <see cref="JudgmentCode.AwOffline"/> 秒用**前一秒**填（空隙 + 末尾的
    ///         预测）。</item>
    /// </list>
    ///
    /// 事件列表可以宽于镜像，自己裁；重复吸收同一条事件是幂等的。
    /// </summary>
    public void Apply(IReadOnlyList<AwEvent> windowEvents, IReadOnlyList<AwEvent> afkEvents, DateTimeOffset now)
    {
        var before = _observedThrough;
        Advance(Floor(now), carryForward: true);

        foreach (var e in windowEvents.OrderBy(e => e.Start))
        {
            var code = _group is not null && _rules.GroupMatches(_group, e.App ?? "", e.Title ?? "")
                ? JudgmentCode.Focused
                : JudgmentCode.OffTask;
            PaintOne(e, code, e.App, e.Title);
        }

        foreach (var e in afkEvents)
            if (e.Status == "afk")
                PaintAfk(e);

        // 取到新数据了才重算；没取到就什么都不做——前沿已经由 Advance 沿用着。
        if (_observedThrough > before) Predict();
    }

    /// <summary>
    /// 把镜像的 <c>[from, to)</c> 这一段**还原成事件**，喂给现有的
    /// <see cref="Judgment.Paint"/> / <see cref="JudgmentBuffer.Cover"/>。
    ///
    /// **账本因此一个字都不用改**：它拿到的仍然是"窗口事件 + afk 事件"两个列表，
    /// 只是数据源从"再查一次 AW"换成了"从镜像里读"。相邻相同的秒合并成一条事件，
    /// 时长正好是那一段的秒数——`Paint` 的覆盖口径是 <c>floor(start) … ceil(end)-1</c>，
    /// 整秒进整秒出，round-trip 不会差一秒。
    ///
    /// <see cref="JudgmentCode.Afk"/> 的秒**只吐 afk 事件、不吐窗口事件**：`Paint` 里
    /// afk 画在最后覆盖一切，这样还原出来的结果跟镜像里存的完全一致。
    /// <see cref="JudgmentCode.AwOffline"/> 的秒什么都不吐——"没有记录"本来就是用
    /// "没有事件"表达的（§4.3 第 (1) 步）。
    /// </summary>
    public (List<AwEvent> Window, List<AwEvent> Afk) EventsIn(DateTimeOffset from, DateTimeOffset to)
    {
        var win = new List<AwEvent>();
        var afk = new List<AwEvent>();

        var a = Floor(from);
        var end = Floor(to);

        var runStart = a;
        MirrorSecond? run = null;

        void Flush(DateTimeOffset stop)
        {
            if (run is not { } r) return;
            var seconds = (stop - runStart).TotalSeconds;
            if (seconds <= 0) return;

            if (r.Code == JudgmentCode.Afk)
                afk.Add(new AwEvent(runStart, seconds, null, null, "afk"));
            else if (r.Code is JudgmentCode.Focused or JudgmentCode.OffTask)
                win.Add(new AwEvent(runStart, seconds, r.App, r.Title, null));
        }

        for (var s = a; s < end; s = s.AddSeconds(1))
        {
            var cur = At(s);
            if (run is { } r && r.Code == cur.Code && r.App == cur.App && r.Title == cur.Title) continue;

            Flush(s);
            run = cur;
            runStart = s;
        }
        Flush(end);

        return (win, afk);
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    /// <summary>整秒归一。所有的下标都从这里出发，绝不掺亚秒零头（跟 §4.2 / DECISIONS H9 同一条纪律）。</summary>
    private static DateTimeOffset Floor(DateTimeOffset t)
        => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Offset);

    private static int Index(DateTimeOffset second)
    {
        var s = second.ToUnixTimeSeconds();
        return (int)(((s % Capacity) + Capacity) % Capacity);
    }

    /// <summary>规则 2「推进即清空」：跳过的槽位一律回到 <see cref="JudgmentCode.AwOffline"/>，绝不让旧数据冒充新观测。</summary>
    /// <summary>
    /// 把环推进到 <paramref name="to"/>。**每秒 O(1)**：指针挪一格，新格按
    /// <paramref name="carryForward"/> 决定写什么。
    ///
    /// <list type="bullet">
    ///   <item><b>carryForward = true</b>（正常一拍）：新格**沿用前一格**。外推本身就是
    ///         这么发生的——两次取数之间没有任何新信息，前沿只能是"还是刚才那样"。</item>
    ///   <item><b>carryForward = false</b>（<see cref="MarkUnavailable"/>）：新格写
    ///         <see cref="JudgmentCode.AwOffline"/>。**AW 连不上时绝不能外推**，否则
    ///         钟面会拿着掉线前的判定一直闪下去（DECISIONS N8：AW 出问题一律回底色）。</item>
    /// </list>
    ///
    /// ⚠️ 无论哪种模式，新格都被**显式写过**——绝不让环里 245 秒前的旧数据冒充新观测
    /// （DECISIONS O4）。O4 原话是"一律回 AwOffline"，这里把它放宽成"一律显式重写"：
    /// 守的东西（旧数据不许冒充新观测）一点没少，而"新的一秒沿用前一秒"正是外推的定义。
    /// </summary>
    private void Advance(DateTimeOffset to, bool carryForward)
    {
        if (to <= Newest) return;

        var gap = (to - Newest).TotalSeconds;
        if (gap >= Capacity)
        {
            // 睡了一觉回来：整个环都过期了，连"最后一个真实观测"也失效
            Array.Fill(_code, JudgmentCode.AwOffline);
            Array.Clear(_app);
            Array.Clear(_title);
            _observedThrough = default;
        }
        else
        {
            for (var s = Newest.AddSeconds(1); s <= to; s = s.AddSeconds(1))
            {
                var i = Index(s);
                if (carryForward)
                {
                    var p = Index(s.AddSeconds(-1));
                    _code[i] = _code[p];
                    _app[i] = _app[p];
                    _title[i] = _title[p];
                }
                else
                {
                    _code[i] = JudgmentCode.AwOffline;
                    _app[i] = _title[i] = null;
                }
            }
        }
        Newest = to;
    }

    /// <summary>
    /// 一条事件覆盖哪些秒：<b>它碰到的每一秒</b>，即 <c>floor(start) … ceil(end)-1</c>，
    /// 零时长事件也占满它落在的那一秒——跟 <see cref="Judgment"/> 的 <c>PaintOne</c> 逐字
    /// 相同的口径（T4：标题每秒变的窗口会产生一堆 duration=0 的事件，实测占 18.5%）。
    /// </summary>
    private (DateTimeOffset From, DateTimeOffset To) Span(AwEvent e)
    {
        var from = Floor(e.Start);
        var to = Floor(e.End);
        if (e.End > to) to = to.AddSeconds(1);      // ceil
        if (to <= from) to = from.AddSeconds(1);    // 零时长：至少占一秒
        return (from, to);
    }

    private void PaintOne(AwEvent e, JudgmentCode code, string? app, string? title)
    {
        var (from, to) = Span(e);
        for (var s = from; s < to; s = s.AddSeconds(1))
        {
            if (s > Newest || s < Oldest) continue;
            if (s > _observedThrough) _observedThrough = s;
            var i = Index(s);
            _code[i] = code;
            _app[i] = app;
            _title[i] = title;
        }
    }

    /// <summary>afk 覆盖一切，但**保留下面那个窗口的名字**——人不在时屏幕上停着的还是它，写日志时有用。</summary>
    private void PaintAfk(AwEvent e)
    {
        var (from, to) = Span(e);
        for (var s = from; s < to; s = s.AddSeconds(1))
        {
            if (s > Newest || s < Oldest) continue;
            if (s > _observedThrough) _observedThrough = s;
            _code[Index(s)] = JudgmentCode.Afk;
        }
    }

    /// <summary>
    /// 规则 3「空隙用预测填」：从最老一秒往前扫，凡是还停在
    /// <see cref="JudgmentCode.AwOffline"/> 的秒，一律沿用**前一秒**。
    ///
    /// 这同时吃掉两种洞：切窗口时事件之间那 ~1 秒空隙，以及末尾 3~10 秒 AW 还没吐出来的
    /// 部分。⚠️ 预测**只往后传**，不会跨过环的起点——所以 watcher 死掉时它最多把最后那个
    /// 窗口延续 <see cref="Capacity"/> 秒，不会像无上限外推那样画出几小时。
    /// </summary>
    /// <summary>
    /// 规则 3 的两步，**只在这一拍真的取到了新数据时才跑**（`Apply` 用
    /// <c>_observedThrough</c> 有没有前进来判断）。两次取数之间的那几秒，前沿由
    /// <see cref="Advance"/> 的"沿用前一格"维持着，什么都不用重算。
    ///
    /// ⚠️ **这里不认识"5 秒"这个数字**：节拍由 <see cref="MirrorFeed.PollSeconds"/> 决定，
    /// 改成 3 秒或 15 秒，这个类一个字都不用动（用户 2026-08-31 要的通用性）。
    /// </summary>
    private void Predict()
    {
        // ① 空隙：事件与事件之间那 ~1 秒的洞，以及初始化时历史事件之间的洞，
        //    沿用**前一秒**。（正常推进产生的秒已经由 Advance 沿用过了，这里只补
        //    真正没被任何事件画到、又还停在 AwOffline 上的格子。）
        for (var s = Oldest.AddSeconds(1); s <= Newest; s = s.AddSeconds(1))
        {
            var i = Index(s);
            if (_code[i] != JudgmentCode.AwOffline) continue;

            var p = Index(s.AddSeconds(-1));
            if (_code[p] == JudgmentCode.AwOffline) continue;

            _code[i] = _code[p];
            _app[i] = _app[p];
            _title[i] = _title[p];
        }

        // ② 末尾：真实画笔够不到的那 3~10 秒，拿 `_observedThrough` 那一格
        //    **把整段覆盖掉**——不是逐格继承，也不是"只填还空着的"。
        //
        // ⚠️ **必须是「覆盖」，写成「只填还空着的秒」就会炸**（2026-08-31 实机事故，
        // 用户定位并指定算法）：AW 的 ongoing 事件 duration 恒定落后实时 ~10 秒，环的
        // 最前沿**永远够不到真实画笔**，只能外推。若外推值写下去就锁死，切窗口那一刻
        // 外推的旧判定会被永久钉在前沿上——真实事件随后只能改正它身后那段，前沿始终
        // 差最后一两格，而**逐秒读前沿的正是钟面闪烁（§8.9）和滴答（§10）**。实机表现
        // 为进出跑偏晚 58 秒、113 秒，甚至整轮都不翻：延迟由 AW 提交周期(~10s) 与轮询
        // 周期(5s) 的拍频决定，**无上限**。账本不受影响（每个整分钟读四分钟窗口，末尾
        // 那十秒早被重画对了），所以现象是「计时完全正确、只有闪烁瞎」，极难往镜像上想。
        if (_observedThrough < Oldest || _observedThrough >= Newest) return;

        var src = Index(_observedThrough);
        if (_code[src] == JudgmentCode.AwOffline) return;   // 没有可外推的值

        for (var s = _observedThrough.AddSeconds(1); s <= Newest; s = s.AddSeconds(1))
        {
            var i = Index(s);
            _code[i] = _code[src];
            _app[i] = _app[src];
            _title[i] = _title[src];
        }
    }
}
