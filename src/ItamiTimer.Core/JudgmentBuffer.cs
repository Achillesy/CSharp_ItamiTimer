namespace ItamiTimer.Core;

/// <summary>
/// 秒级专注存储空间（ISSUE_FIX.md §7）。
///
/// 7380 秒 = 180 秒 padding（任务前 3 分钟，供 AW 4 分钟查询窗口对齐）
///        + 7200 秒绘制区（120 分钟螺旋表面）。
///
/// 状态码（ISSUE_FIX §7 状态码表）：
/// <list type="table">
///   <item><term>0 Init</term><description>已分配，AW 数据尚未到达</description></item>
///   <item><term>1 Gray</term><description>预计任务时间（灰色圆弧）</description></item>
///   <item><term>2 Focused</term><description>专注时间（绿色圆弧）</description></item>
///   <item><term>3 AwOffline</term><description>AW 脱机，默认算专注</description></item>
///   <item><term>4 Afk</term><description>AFK 记录的离线时间（空白）</description></item>
///   <item><term>5 OffTask</term><description>非专注时间（红色圆弧）</description></item>
/// </list>
/// </summary>
public class JudgmentBuffer
{
    public const int PaddingSeconds = 180;
    public const int DrawSeconds = 7200;
    public const int TotalSize = PaddingSeconds + DrawSeconds; // 7380

    // 状态码
    public const byte Init = 0;
    public const byte Gray = 1;
    public const byte Focused = 2;
    public const byte AwOffline = 3;
    public const byte Afk = 4;
    public const byte OffTask = 5;

    private readonly byte[] _buf = new byte[TotalSize];

    /// <summary>buffer[i] 对应的绝对时刻 = <see cref="WallClock"/> + i 秒。</summary>
    /// <remarks>
    /// buffer[0] = taskStart − 180s。buffer[180] = taskStart（第一个可绘制的秒）。
    /// </remarks>
    public DateTimeOffset WallClock { get; private set; }

    /// <summary>原始专注目标（秒）。不随归档变化。</summary>
    public int FocusTargetSeconds { get; }

    /// <summary>专注目标剩余秒数。归档后会用已归档的专注秒数抵扣。</summary>
    public int FocusSeconds { get; private set; }

    /// <summary>归入 during 的秒数。落盘时从此值开始累加。</summary>
    public double DuringSeconds { get; set; }

    /// <summary>本次任务中已归档的秒数（每次 2h 滚动时累加）。</summary>
    public int ArchivedSeconds { get; private set; }

    /// <summary>任务开始以来已流逝的秒数（已写入覆盖过的最大偏移）。</summary>
    public int ElapsedSeconds { get; private set; }

    public JudgmentBuffer(DateTimeOffset taskStart, int focusMinutes, double duringSeconds = 0)
    {
        WallClock = taskStart.AddSeconds(-PaddingSeconds);
        FocusTargetSeconds = focusMinutes * 60;
        FocusSeconds = FocusTargetSeconds;
        DuringSeconds = duringSeconds;

        // 初始化：(FocusMinutes + 3) × 60 秒填灰色，其余 0
        var grayLen = FocusSeconds + PaddingSeconds; // 专注秒数 + 前 3 分钟
        for (var i = 0; i < TotalSize; i++)
            _buf[i] = i < grayLen ? Gray : Init;
    }

    /// <summary>读 buffer[i]，不做边界检查。</summary>
    public byte this[int index] => _buf[index];

    /// <summary>取可绘制区的长度（从 PaddingSeconds 开始的有效秒数）。</summary>
    public int DrawableSeconds => TotalSize - PaddingSeconds;

    /// <summary>
    /// 把 taskStart 相对偏移处的一段秒级分类结果写进 buffer。
    /// <paramref name="bufferOffset"/> = (queryStart − WallClock).TotalSeconds。
    /// </summary>
    public void Write(int bufferOffset, ReadOnlySpan<byte> classifiedSeconds)
    {
        var len = Math.Min(classifiedSeconds.Length, TotalSize - bufferOffset);
        if (len <= 0) return;
        classifiedSeconds[..len].CopyTo(_buf.AsSpan(bufferOffset, len));
        // 跟踪任务已流逝秒数（去掉 padding）——用于判断是否需要归档
        var taskElapsed = bufferOffset + len - PaddingSeconds;
        if (taskElapsed > ElapsedSeconds) ElapsedSeconds = taskElapsed;
    }

    /// <summary>
    /// 2 小时滚动归档：前 1 小时 [0, 3600) → 统计 2+3 秒数 → 加到 DuringSeconds；
    /// [3600, 7380) → 左移到 [0, 3780)；[3780, 7380) 留给新数据。
    ///
    /// 返回：是否实际执行了归档（elapsed > 7200 才执行）。
    /// </summary>
    public bool TryArchive()
    {
        if (ArchiveStart < 0) return false;

        // 归档区 [0, 3600)：统计 2(Focused)+3(AwOffline) 秒数
        var focus = 0;
        for (var i = 0; i < 3600; i++)
            if (_buf[i] is Focused or AwOffline)
                focus++;

        DuringSeconds += focus;
        ArchivedSeconds += 3600;

        // 左移：[3600, 7380) → [0, 3780)
        Array.Copy(_buf, 3600, _buf, 0, 3780);

        // 新空间 [3780, 7380) 归零
        Array.Fill(_buf, Init, 3780, 3600);

        // 时间推进：WallClock + 3600s；ElapsedSeconds 对应缩短
        FocusSeconds -= focus;
        if (FocusSeconds < 0) FocusSeconds = 0;

        WallClock = WallClock.AddSeconds(3600);
        ElapsedSeconds -= 3600; // buffer 左移了 3600，已流逝的偏移等比例缩短
        return true;
    }

    /// <summary>
    /// 归档区在 buffer 里的起始索引。如果任务尚未跑满 2 小时则返回 -1。
    /// 归档条件：ElapsedSeconds ≥ 7200（绘制区已被写满一轮）。
    /// </summary>
    private int ArchiveStart
    {
        get
        {
            // 用已流逝时间判断，不用 buffer 内容——因为初始化填了 Gray，
            // 单靠 buffer[3600] != Init 没法区分「初始灰色」和「真实数据写过」。
            if (ElapsedSeconds < DrawSeconds) return -1;
            return 0;
        }
    }

    /// <summary>统计 buffer 中状态 2+3 的秒数（当前未归档的专注时间）。</summary>
    public int CountFocused()
    {
        var n = 0;
        for (var i = PaddingSeconds; i < TotalSize; i++)
            if (_buf[i] is Focused or AwOffline)
                n++;
        return n;
    }

    /// <summary>专注是否已达成：原始目标 ≤ during(已归档) + 当前 buffer 中的专注秒数。</summary>
    public bool IsFocusComplete => (int)DuringSeconds + CountFocused() >= FocusTargetSeconds;

    /// <summary>任务开始时刻 = WallClock + PaddingSeconds。</summary>
    public DateTimeOffset TaskStart => WallClock.AddSeconds(PaddingSeconds);

    /// <summary>
    /// 把 buffer 投影成 MinuteCell 列表（60 秒/格），只吐完整的格子。
    /// 和旧 <see cref="Replay.ToMinuteCells"/> 同签名，渲染层零改动。
    /// </summary>
    public List<MinuteCell> ToMinuteCells()
    {
        var cells = new List<MinuteCell>();
        for (var i = 0; i < DrawSeconds / 60; i++)
        {
            var cellStart = TaskStart.AddMinutes(i);
            var bufStart = PaddingSeconds + i * 60;
            if (bufStart + 60 > PaddingSeconds + ElapsedSeconds) break; // 未满一分钟

            int counted = 0, off = 0, absent = 0, gap = 0;
            for (var s = 0; s < 60; s++)
            {
                switch (_buf[bufStart + s])
                {
                    case Focused:
                    case AwOffline:
                        counted++; break;
                    case OffTask: off++; break;
                    case Afk: absent++; break;
                    default: gap++; break; // Init 或 Gray
                }
            }
            cells.Add(new MinuteCell(i, cellStart, counted, off, absent, gap));
        }
        return cells;
    }

    /// <summary>
    /// 专注达成的时刻（整分钟边界）。null = 尚未达成。
    /// </summary>
    public DateTimeOffset? FocusCompletedAt()
    {
        var cells = ToMinuteCells();
        double banked = 0;
        foreach (var c in cells)
        {
            banked += c.CountedSeconds;
            if ((int)DuringSeconds + (int)banked >= FocusTargetSeconds)
                return c.Start.AddMinutes(1); // 整分钟边界：该分钟结束时达成
        }
        return null;
    }

    // 方便测试：直接访问内部数组的只读视图
    public ReadOnlySpan<byte> Raw => _buf;
    public Span<byte> DrawSpan => _buf.AsSpan(PaddingSeconds, DrawSeconds);
}
