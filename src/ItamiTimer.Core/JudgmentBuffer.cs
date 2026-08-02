namespace ItamiTimer.Core;

/// <summary>本拍做完之后的结果（DESIGN.md §4.2 的第 2~4 步）。</summary>
/// <param name="SettledSeconds">本拍归档结算掉的专注秒数。调用方要把它 <c>+=</c> 进 during（§11.2）。</param>
/// <param name="DeficitSeconds">还欠多少专注秒（已向上取整到整分钟）。0 = 达成。</param>
/// <param name="Completed">本拍是否达成。<b>达成时刻就是这一拍</b>，不要回头去推。</param>
public readonly record struct TickOutcome(int SettledSeconds, int DeficitSeconds, bool Completed);

/// <summary>
/// 秒级专注存储空间（DESIGN.md §4，第二版）。
///
/// <c>7380 = 180 秒 padding + 7200 秒绘制区</c>（120 分钟 = 表盘两圈）。
/// <c>buffer[i]</c> 对应绝对时刻 <c>WallClock + i 秒</c>，<c>buffer[180]</c> = 任务起点。
///
/// <list type="bullet">
///   <item><b>padding 的唯一用途</b>：第一拍要查起点前 3 分钟，那段数据得有地方落。
///         它<b>永不计入、永不绘制</b>。</item>
///   <item><b>绘制区 7200 秒不是内存考虑，是画图考虑</b>：钟面一圈 60 分钟，螺旋只留两圈，
///         所以能画的就是 120 分钟。超出部分靠归档滚动（<see cref="TryArchive"/>）。</item>
/// </list>
///
/// 每个整分钟做五件事，前四件在这里（第五件染色是渲染层的事）：
/// <code>
/// 1. 覆盖   Cover()        把 [整分钟−4min, 整分钟) 重画一遍
/// 2. 归档   TryArchive()   ElapsedSeconds ≥ 7200 才做
/// 3. Gray   RefreshGray()  重算承诺弧
/// 4. 达成   缺口 ≤ 0 就是达成，时刻 = 这一拍
/// </code>
/// <see cref="Tick"/> 把这四步串好，正常调用它就行。
/// </summary>
public sealed class JudgmentBuffer
{
    public const int PaddingSeconds = 180;
    public const int DrawSeconds = 7200;
    public const int TotalSize = PaddingSeconds + DrawSeconds;   // 7380

    /// <summary>AW 查询窗口固定 4 分钟：afk 默认 180 秒才出结论并回填，取 4 分钟必然覆盖。</summary>
    public const int QueryWindowSeconds = 240;

    /// <summary>一次归档滚动掉多少秒。</summary>
    public const int ArchiveSeconds = 3600;

    private readonly JudgmentCode[] _buf = new JudgmentCode[TotalSize];

    /// <summary><c>buffer[0]</c> 对应的绝对时刻 = 任务起点 − 180 秒。归档时 +3600。</summary>
    public DateTimeOffset WallClock { get; private set; }

    /// <summary>任务起点 = <see cref="WallClock"/> + 180 秒。<b>归档后它会往前走一小时</b>（§4.4）。</summary>
    public DateTimeOffset TaskStart => WallClock.AddSeconds(PaddingSeconds);

    /// <summary>从（当前的）任务起点算起已流逝的秒数 = 已写入覆盖过的最大偏移。归档时 −3600。</summary>
    public int ElapsedSeconds { get; private set; }

    /// <summary>写入头在 buffer 里的下标。</summary>
    public int Head => PaddingSeconds + ElapsedSeconds;

    /// <summary>
    /// <b>剩余</b>目标秒数——达成判定和承诺弧长度的唯一依据，归档时扣减（§4.4）。
    ///
    /// ⚠️ 它<b>不是</b>休息时长的依据。休息只读提交时锁定的 <c>TaskRecord.FocusMinutes</c>，
    /// 跟这一轮拖了多久毫无关系（DECISIONS H6：拖得越久歇得越少，激励方向就反了）。
    /// </summary>
    public int RemainingTargetSeconds { get; private set; }

    /// <summary>本轮已经归档掉的秒数（每次归档 +3600）。圈号之外的地方一般用不到。</summary>
    public int ArchivedSeconds { get; private set; }

    public JudgmentBuffer(DateTimeOffset taskStart, int focusMinutes)
    {
        WallClock = taskStart.AddSeconds(-PaddingSeconds);
        RemainingTargetSeconds = focusMinutes * 60;
        // 开局那段灰弧用的就是每拍那套算法，不另写一份初始化（§4.5）。
        RefreshGray();
    }

    public JudgmentCode this[int index] => _buf[index];

    // 给测试和 CLI 直接看内部的只读视图
    public ReadOnlySpan<JudgmentCode> Raw => _buf;
    public ReadOnlySpan<JudgmentCode> DrawSpan => _buf.AsSpan(PaddingSeconds, DrawSeconds);

    /// <summary>已计入的专注秒数 = <c>[180, 7380)</c> 里码 ≥ Focused 的个数。<b>不含 padding。</b></summary>
    public int FocusedSeconds => CountFocused(PaddingSeconds, TotalSize);

    /// <summary>专注是否已达成。等价于「承诺弧为空」。</summary>
    public bool IsFocusComplete => RemainingTargetSeconds - FocusedSeconds <= 0;

    /// <summary>一拍的完整流程：覆盖 → 归档 → 重算承诺弧 → 判达成（§4.2）。</summary>
    public TickOutcome Tick(
        DateTimeOffset now,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        var settled = Cover(now, windowEvents, afkEvents, rules, selectedGroup);
        settled += TryArchive();
        var deficit = RefreshGray();
        return new TickOutcome(settled, deficit, deficit <= 0);
    }

    /// <summary>
    /// 第 1 步·覆盖（§4.3）。查询窗口是 <c>[FloorToMinute(now) − 4min, FloorToMinute(now))</c>。
    ///
    /// <b>一切都从整分钟算，绝不掺 <c>now</c> 的亚秒零头</b>（DECISIONS H9）：否则每拍相位
    /// 不同，同一个 buffer 秒会被相差近 1 秒的两个采样点各写一次、边界秒来回翻面；
    /// AW 响应慢 10 秒时写入位置还会整段错位。
    ///
    /// 返回：为了给这一拍腾地方而强制归档结算掉的专注秒数（正常情况恒为 0）。
    /// </summary>
    public int Cover(
        DateTimeOffset now,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string? selectedGroup)
    {
        var minute = TimeGrid.FloorToMinute(now);
        var settled = 0;

        // §15.6：机器睡眠/挂起超过绘制区容量时，写入偏移会永久越界 → ElapsedSeconds
        // 冻住 → 归档条件再也不成立 → 会话死锁（表盘停住、达成永远不来）。
        // 先滚动到这一拍落得进 buffer 为止。64 次 = 64 小时，够任何一次挂起。
        for (var guard = 0; guard < 64; guard++)
        {
            if (OffsetOf(minute) <= TotalSize) break;   // 窗口末端（= 这一拍）落得进 buffer
            settled += Archive();
        }

        var offset = OffsetOf(minute) - QueryWindowSeconds;   // 窗口起点相对 buffer[0]
        var from = Math.Max(offset, 0);
        var to = Math.Min(offset + QueryWindowSeconds, TotalSize);
        if (to <= from) return settled;

        // ① 初始化「新地盘」= [上次写入头, 这次写入头) ∩ 本次查询窗口，填 AwOffline。
        //
        // 交这一刀是**故意的**：正常运行时新地盘就是最后那一分钟，跟「AW 连不上只写
        // 上一分钟」是同一条规则（H4）；漏了三拍以内也全在窗口里、会被事件重画。
        // 而漏得更久（机器睡了两小时）时，窗口之外的那些分钟**保持 Init、不计入**——
        // 不能把「我根本没查过」当成「AW 没记录」白送出去，否则睡一觉就能刷满任务。
        var head = Head;
        if (head < from)
        {
            // 漏得比查询窗口还久（机器睡了两小时）：窗口**之外**那段既没查过、也永远
            // 不会查了，清成 Init —— 不计入、不绘制。不能把「我根本没查过」当成
            // 「AW 没记录」白送出去，否则睡一觉就能刷满任务。
            // 也不能不管：上一拍的承诺弧还铺在那儿，不清就会被染成「还没走到」。
            _buf.AsSpan(head, from - head).Fill(JudgmentCode.Init);
        }
        var newFrom = Math.Max(head, from);
        if (newFrom < to) _buf.AsSpan(newFrom, to - newFrom).Fill(JudgmentCode.AwOffline);

        // ②③④ 分层覆盖。前 3 分钟只被**重画**、不被清空：一秒一旦被判成
        // Afk/OffTask/Focused 就不会再退回 AwOffline，只会被后来的真实数据改判。
        // 别为了「一致」把整个 4 分钟都清成 AwOffline 再重画——AW 万一返回一份不完整
        // 的响应，那就等于把已经判红的秒一次性抹绿。
        Judgment.Paint(_buf.AsSpan(from, to - from), WallClock.AddSeconds(from),
                       windowEvents, afkEvents, rules, selectedGroup);

        var elapsed = to - PaddingSeconds;
        if (elapsed > ElapsedSeconds) ElapsedSeconds = elapsed;
        return settled;
    }

    /// <summary>
    /// 第 2 步·归档（§4.4）：buffer 写满 2 小时就滚动一次。返回结算掉的专注秒数（0 = 没归档）。
    ///
    /// <b>第一次在满 2 小时，之后每 1 小时一次</b>（归档后 <see cref="ElapsedSeconds"/> 回到 3600）。
    /// </summary>
    public int TryArchive() => ElapsedSeconds < DrawSeconds ? 0 : Archive();

    /// <summary>
    /// 无条件滚动一次。语义上等价于<b>「在 1 小时前把任务放弃掉、又在同一刻用剩余目标
    /// 重新开始」</b>——这句话就是判断这段代码对不对的标准。
    ///
    /// 所以被结算掉的必须<b>正好是「上一个任务的全部时间」</b>，即 <c>[180, 3780)</c>：
    /// 旧起点到新起点，不多不少 3600 秒。
    ///
    /// ⚠️ 写成 <c>[0, 3600)</c> 就是 §15.5 那个 Bug——偏了 180 之后既把「点 Start 之前」
    /// 的专注秒算进账，又漏掉 <c>[3600, 3780)</c> 那 3 分钟，归档瞬间「还差多少」会跳一下，
    /// 正负都可能，跳负时快达成的任务会突然倒退且毫无提示。
    /// </summary>
    private int Archive()
    {
        var settled = CountFocused(PaddingSeconds, PaddingSeconds + ArchiveSeconds);

        RemainingTargetSeconds -= settled;
        if (RemainingTargetSeconds < 0) RemainingTargetSeconds = 0;

        // [3600, 7380) → [0, 3780)：
        //   旧 [3780,7380) 成为新 [180,3780)——新任务的第一小时
        //   旧 [3600,3780) 成为新 [0,180)  ——新任务的 padding，已结算过，不再统计
        Array.Copy(_buf, ArchiveSeconds, _buf, 0, TotalSize - ArchiveSeconds);
        Array.Fill(_buf, JudgmentCode.Init, TotalSize - ArchiveSeconds, ArchiveSeconds);

        WallClock = WallClock.AddSeconds(ArchiveSeconds);
        ArchivedSeconds += ArchiveSeconds;
        ElapsedSeconds = Math.Max(0, ElapsedSeconds - ArchiveSeconds);
        return settled;
    }

    /// <summary>
    /// 第 3 步·重算承诺弧（§4.5）。返回缺口秒数（已向上取整到整分钟），<c>0</c> = 达成。
    ///
    /// <b>每拍重算，不记状态。</b>「记住最后一个 Gray 的位置再往前推」是错的：
    /// afk 收缩（T5）把原本判 Afk 的几十秒改判 Focused 时，缺口减得比写入头前进得多，
    /// 弧的末端会<b>前移</b>——增量做法会在原地留下一截残渣，承诺弧比实际长。
    /// 所以先把 <c>[Head, 7380)</c> 清成 Init，再填 Gray。每分钟一次 7KB 的 Fill，
    /// 开销可以忽略，但省掉了所有「该清哪一段」的判断。
    /// </summary>
    public int RefreshGray()
    {
        var deficit = RemainingTargetSeconds - FocusedSeconds;
        if (deficit < 0) deficit = 0;
        deficit = (deficit + 59) / 60 * 60;                     // 向上取整到整分钟

        var head = Head;
        if (head >= TotalSize) return deficit;

        Array.Fill(_buf, JudgmentCode.Init, head, TotalSize - head);
        var grayEnd = Math.Min(head + deficit, TotalSize);      // 超出绘制区就裁掉
        if (grayEnd > head) Array.Fill(_buf, JudgmentCode.Gray, head, grayEnd - head);
        return deficit;
    }

    /// <summary>
    /// 投影成每分钟一格（§4.6）。范围是 <c>[180, Head + 缺口)</c>——<b>已走过 + 承诺弧</b>，
    /// 再往后全是 Init，不吐。只吐完整的 60 秒。
    /// </summary>
    public List<MinuteCell> ToMinuteCells()
    {
        var deficit = RemainingTargetSeconds - FocusedSeconds;
        if (deficit < 0) deficit = 0;
        deficit = (deficit + 59) / 60 * 60;

        var end = Math.Min(Head + deficit, TotalSize);
        var cells = new List<MinuteCell>();

        for (var i = 0; ; i++)
        {
            var bufStart = PaddingSeconds + i * 60;
            if (bufStart + 60 > end) break;

            int focus = 0, off = 0, afk = 0, gray = 0, init = 0;
            for (var s = 0; s < 60; s++)
            {
                switch (_buf[bufStart + s])
                {
                    case JudgmentCode.Focused:
                    case JudgmentCode.AwOffline: focus++; break;
                    case JudgmentCode.OffTask: off++; break;
                    case JudgmentCode.Afk: afk++; break;
                    case JudgmentCode.Gray: gray++; break;
                    default: init++; break;
                }
            }

            cells.Add(new MinuteCell(i, TaskStart.AddMinutes(i), focus, off, afk, gray, init));
        }
        return cells;
    }

    private int CountFocused(int from, int to)
    {
        var n = 0;
        for (var i = from; i < to; i++)
            if (_buf[i] >= JudgmentCode.Focused) n++;
        return n;
    }

    /// <summary>某个绝对时刻在 buffer 里的下标（可能越界，调用方自己裁）。</summary>
    private int OffsetOf(DateTimeOffset t) => (int)Math.Round((t - WallClock).TotalSeconds);
}
