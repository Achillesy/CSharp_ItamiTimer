namespace ItamiTimer.Core;

/// <summary>
/// **表盘上「人不在」那一格的唯一来源**（3.10.0，DESIGN §7.5）：由本机键鼠空闲时长推出
/// 「离开」区间，不再从 ActivityWatch 的 afk 桶取数。
///
/// ## 为什么不再用 AW 的 afk 桶
///
/// 用户 2026-09-13 报：锁屏离开 1 小时 51 分钟（AW 全程判 `afk`、窗口是 `loginwindow`），
/// 表盘却大片红格。查下来根因**不在取数条数上**（那是 3.9.3 和 3.9.8 两次修错的地方），
/// 而在这里：
///
/// <list type="bullet">
///   <item>两个桶的写入节奏差很远——实测 <b>window 每 10 秒必跳一次（哪怕什么都没变）</b>，
///         而 afk 桶跳着来，能连续 35 秒不动；</item>
///   <item>而取数是**各自判断各自的 `last_updated`**：窗口桶跳了就取、afk 桶没跳就不取；</item>
///   <item><see cref="AwMirror.Apply"/> 里窗口事件会**重刷整段**——锁屏时 `loginwindow`
///         是一条横跨近两小时的事件，每取一次就把整段刷成跑偏；</item>
///   <item>那一拍要是没取到 afk，**就没有任何东西把它盖回来** → 红格。</item>
/// </list>
///
/// 所以红格跟「取 20 条还是 1000 条」「怎么去重」毫无关系——**afk 只要还来自「取数」，
/// 就必然存在「这一拍没取到」**。换成本机信号之后，这个失败模式结构上消失。
///
/// 顺带解决的还有：afk 事件的 duration 滞后真实时间 10~35 秒（实测）、一次离开在桶里
/// 堆出几十上百条 start 相同 duration 各异的副本、以及依赖 `aw-watcher-afk` 活着。
///
/// ## ⚠️ 这条路**只管表盘，不碰账本**
///
/// <c>during.json</c> 走的是另一条完全独立的路：点 Start 时 <see cref="Backfill"/> 重放
/// **AW 的历史**、`During.Advance` 落盘。`AwMirror` / `JudgmentBuffer` 的结果**从不落盘**
/// （CLAUDE.md「不落盘任务状态」）。所以本机信号「程序关着就没有记录」这个性质，
/// 对表盘无所谓——镜像本来就只在程序运行时存在。
///
/// ⚠️ **别把这个信号引进 <see cref="Backfill"/>**：那边要能回查程序没运行时的历史，
/// 只有 AW 做得到。注释见 <c>InputIdle</c> 的类注释——它当年拒绝的正是这件事，而它
/// 拒绝的是「判定的输入」这个**全局**命题，保护的是账本，不是表盘。
///
/// ## 跟 AW 对齐的两条语义
///
/// <list type="number">
///   <item><b>门槛 180 秒</b>（<see cref="ThresholdSeconds"/>），跟 AW 默认值一致。
///         179 秒不动**不算离开**——那是在看内容。</item>
///   <item><b>起点回填到输入停止那一刻</b>（<c>now − idle</c>），不是门槛跨过那一刻。
///         实测 2026-09-13 那次：本地算出 14:55:19，AW 那条 afk 事件的起点也是 14:55:19，
///         两边独立得出同一个数。</item>
/// </list>
///
/// ⚠️ 代价（知情）：跨过 180 秒那一刻会**回溯改写**前 180 秒的格子——刚才还是绿的会变成
/// 空白。这是 AW 的语义，不是 bug；换成「从门槛那一刻才算」反而会跟下次 Start 时
/// <see cref="Backfill"/> 从 AW 算出来的结果对不上。
///
/// ## 为什么要记住区间，而不是只看此刻
///
/// 你离开 5 分钟又回来，<c>idle</c> 归零，可那 5 分钟**还在镜像的窗口里**，而窗口事件
/// 每次取数仍会把它重刷成跑偏。所以必须把区间留着，直到它整个滑出镜像窗口。
/// 状态是**有界的**（最多覆盖 <see cref="AwMirror.Capacity"/> 秒），不是缓存。
///
/// 纯逻辑，<c>now</c> 和 <c>idle</c> 都是参数，所以能测。
/// </summary>
public sealed class IdleAfk
{
    /// <summary>多久没有键鼠输入算「离开」。**跟 AW 的默认 afk 超时一致**，别单独调。</summary>
    public const int ThresholdSeconds = 180;

    /// <summary>
    /// 认「还是同一段离开」的起点容差。<c>idle</c> 是个 double 秒数、有抖动，所以
    /// <c>now − idle</c> 每拍会差零点几秒。2 秒足够宽，又不可能把两次离开并成一段
    /// ——两次离开之间必然隔着至少 <see cref="ThresholdSeconds"/> 秒的在座时间。
    /// </summary>
    private const double SameSpanToleranceSeconds = 2;

    private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _spans = [];

    /// <summary>当前留着的「离开」区间，按起点升序。只用于测试和诊断。</summary>
    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Spans => _spans;

    /// <summary>
    /// 每拍调一次：<paramref name="idle"/> 是此刻「距上次键鼠输入多久」。
    /// 不到门槛什么都不做；到了就开一段新的、或把当前这段延长到 <paramref name="now"/>。
    /// 最后把整个滑出镜像窗口的区间丢掉。
    /// </summary>
    public void Track(DateTimeOffset now, TimeSpan idle)
    {
        if (idle.TotalSeconds >= ThresholdSeconds)
        {
            var start = now - idle;
            if (_spans.Count > 0 &&
                Math.Abs((_spans[^1].Start - start).TotalSeconds) <= SameSpanToleranceSeconds)
                _spans[^1] = (_spans[^1].Start, now);   // 同一段，延长
            else
                _spans.Add((start, now));               // 新的一段
        }

        // 整段都落在镜像窗口之外的，留着也画不上，丢掉——这是状态有界的保证
        var floor = now.AddSeconds(-AwMirror.Capacity);
        _spans.RemoveAll(s => s.End < floor);
    }

    /// <summary>
    /// 还原成 <see cref="AwMirror.Apply"/> 认得的 afk 事件。**账本一个字不用改**：它拿到的
    /// 仍然是「窗口事件 + afk 事件」两个列表，只是 afk 那半换了来源。
    ///
    /// ⚠️ **时长要 +1 秒**：这里的区间是**整秒闭区间**（「<c>End</c> 那一秒人也不在」），
    /// 而事件的覆盖口径是半开的 <c>floor(start) … ceil(end)-1</c>。不加这一秒，最新那一格
    /// ——也就是表盘前沿、眼睛最先看到的那一格——会漏掉。走 <see cref="AwMirror.Apply"/>
    /// 时它碰巧会被 <c>Predict</c> 的沿用填上，而走 <see cref="AwMirror.MarkUnavailable"/>
    /// （AW 掉线那条路）没有预测，漏的就是真漏。多出来的那一秒画不到未来去：
    /// <c>PaintAfk</c> 自己会把 <c>&gt; Newest</c> 的秒裁掉。
    /// </summary>
    public List<AwEvent> Events()
    {
        var events = new List<AwEvent>(_spans.Count);
        foreach (var (start, end) in _spans)
            events.Add(new AwEvent(start, (end - start).TotalSeconds + 1, null, null, "afk"));
        return events;
    }
}
