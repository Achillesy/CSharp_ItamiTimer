namespace ItamiTimer.Core;

/// <summary>
/// 从 ActivityWatch 自己的历史里，重新推导某个小目标在任意一段过去时间里真正拿到了多少秒。
///
/// <b>这是账本的唯一入账口</b>（§11.2）。任务运行期间那套逐分钟的判定只喂表盘和完成
/// 判定，一秒都不落盘；真正进 <c>during.json</c> 的数字，永远是下一次任务启动时由这里
/// 重新数出来的。
///
/// <b>⚠️ 和运行期判定的唯一区别，也是全部重点：这里 fail-closed。</b>
///
/// <see cref="JudgmentBuffer.Cover"/> 会把「查了但 AW 没数据」那段填成
/// <see cref="JudgmentCode.AwOffline"/>，而 <c>AwOffline(5) ≥ Focused(4)</c>——它计入专注
/// （H2 的知情 fail-open）。那条规则**只在程序正在跑、拍子正在走的时候站得住**：机器摆明
/// 开着、人摆明在，AW 哑了是 AW 的锅，不该由用户背。
///
/// 回填的处境正相反：<b>程序当时没在跑。</b>AW 没数据最大的可能是机器关着、或者人压根
/// 不在电脑前。照 fail-open 记账，一次跨周末的回填能凭空记 48 小时。所以这里的初值是
/// <see cref="JudgmentCode.Init"/>（=0，不计入）：<b>有证据才算</b>。
///
/// 好在 <see cref="Judgment.Paint"/> 自己什么都不填——它只画事件，fail-open 是
/// <see cref="JudgmentBuffer"/> 用水位线加上去的。所以这里直接复用那个纯函数，一行都不用改：
/// 一块清零的 span 交给它画一遍，然后数 <c>≥ Focused</c> 的格子。
///
/// afk 覆盖层在这里比运行期更不能少：AW 的窗口 watcher 会在人离开时持续拉长当前窗口的
/// <c>duration</c>（姊妹项目 AWJ 的 Note T2），没有 afk 盖顶，一个长驻的匹配窗口能给你
/// 记一整夜。这一层同样由 <see cref="Judgment.Paint"/> 自带。
/// </summary>
public static class Backfill
{
    /// <summary>
    /// 一次画多长。一天 = 86400 字节的 span，复用同一块数组，内存 O(1)。
    ///
    /// 切片是为了**内存和单次请求的体积**，不是为了速度：首次回填可能要走完整个 AW 历史，
    /// 一次性拉两年的窗口事件会是几十 MB 的 JSON，span 也会涨到 60 MB 以上。
    ///
    /// ⚠️ **原本是一周，2026-08-06 真机干跑时被打回来的**：150 天的历史走到第 17 周，
    /// 那一周的窗口事件多到单次请求超过了 10 秒，直接抛
    /// <see cref="AwUnavailableException"/>——而回填的失败语义是「不推进 checkpoint、
    /// 下次重试」，于是那段历史会永远卡在同一个地方，每次启动重试、每次同样超时。
    /// 一天的片子小一个数量级，配合 <see cref="ClientTimeoutSeconds"/> 才有足够余量。
    ///
    /// 代价有两条，都可以接受：
    /// <list type="bullet">
    ///   <item>请求数 ×7（150 天 = 300 次），但这是一次性的；</item>
    ///   <item><see cref="AwClient.FetchEventsAsync"/> 每次往前放宽 6 小时（Note T1），
    ///         按天切就是每 24 小时多拉 6 小时 = 1.25 倍冗余（按周只有 1.04 倍）。
    ///         纯粹是网络开销，不影响正确性——重复拉到的事件会被 span 的边界裁掉。</item>
    /// </list>
    ///
    /// 切片本身的代价是每个边界最多错 1 秒（<see cref="Judgment.Paint"/> 的边界秒归属由
    /// 覆盖优先级决定，偏严格一侧）。两年 730 个边界 = 最多 730 秒 ≈ 12 分钟，
    /// 相对于两年的总量可以忽略。
    /// </summary>
    public const int ChunkSeconds = 24 * 3600;

    /// <summary>
    /// 回填专用的 HTTP 超时，比 <see cref="AwClient.DefaultTimeoutSeconds"/> 宽得多。
    ///
    /// 两个场景的取舍正好相反：运行期宁可短超时 fail-open 过去，也不能把整分钟的节拍拖住；
    /// 回填是一次性的后台活，**慢一点无所谓，失败才是问题**——失败意味着 checkpoint
    /// 不推进，同一段历史下次启动还得从头再来。
    /// </summary>
    public const int ClientTimeoutSeconds = 120;

    /// <summary>
    /// 数出 <c>[since, until)</c> 里属于 <paramref name="goal"/> 的专注秒数。
    ///
    /// 两端都用调用方给的绝对时刻，<b>不做整分钟对齐</b>——对齐是提交任务那一侧的事
    /// （<c>TaskRecord.StartedAt</c> 已经是整分钟，§14.1），这里对齐反而会在 checkpoint
    /// 上引入一个来回漂移的零头。
    ///
    /// 连不上 AW 会照常抛 <see cref="AwUnavailableException"/>——<b>这里绝不能 fail-open</b>。
    /// 调用方接住之后要做的是「**不推进 checkpoint**」，下次启动自然重试同一个窗口：
    /// 推进 checkpoint 这个动作本身就是成功的唯一证明，不需要任何重试逻辑。
    /// </summary>
    /// <summary>
    /// 承重的那条规则，单独一个纯函数：<b>一块清零的 span 交给 <see cref="Judgment.Paint"/>
    /// 画一遍，数 <c>≥ Focused</c> 的格子。</b>
    ///
    /// 清零 = 全 <see cref="JudgmentCode.Init"/> = 不计入，<b>这就是 fail-closed 的全部
    /// 实现</b>。运行期那条 fail-open 是 <see cref="JudgmentBuffer.Cover"/> 用水位线额外
    /// 填 <see cref="JudgmentCode.AwOffline"/> 加上去的，<see cref="Judgment.Paint"/> 自己
    /// 从不填任何底色——所以这里复用它，一行都不用改。
    ///
    /// 抽成独立函数不是为了复用，是为了<b>能测</b>：这个函数一旦被人「顺手统一」成和运行期
    /// 一样的底色，账本就会开始把关机时间记成专注，而且不报错。
    /// </summary>
    public static long CountSpan(
        DateTimeOffset from,
        int seconds,
        IReadOnlyList<AwEvent> windowEvents,
        IReadOnlyList<AwEvent> afkEvents,
        GroupRules rules,
        string goal,
        JudgmentCode[]? scratch = null)
    {
        if (seconds <= 0) return 0;

        var span = scratch is not null && scratch.Length >= seconds
            ? scratch.AsSpan(0, seconds)
            : new JudgmentCode[seconds].AsSpan();
        span.Clear();

        Judgment.Paint(span, from, windowEvents, afkEvents, rules, goal);

        long n = 0;
        foreach (var c in span)
            if (c >= JudgmentCode.Focused) n++;
        return n;
    }

    /// <param name="progress">每画完一片调一次：(这片的右端, 到此为止的累计秒数)。给日志用。</param>
    public static async Task<long> CountAsync(
        AwClient aw,
        DateTimeOffset since,
        DateTimeOffset until,
        GroupRules rules,
        string goal,
        Action<DateTimeOffset, long>? progress = null)
    {
        if (until <= since) return 0;

        var winBucket = await aw.FindBucketIdAsync(AwClient.WindowBucketType).ConfigureAwait(false);
        var afkBucket = await aw.FindBucketIdAsync(AwClient.AfkBucketType).ConfigureAwait(false);

        // ConfigureAwait(false) 贯穿全程：画格子是 CPU 活，首次回填可能要画上几千万秒，
        // 绝不能落在 UI 线程上。调用方那一层的 await 会自己捕获 UI 上下文，续体照样回得去。
        var span = new JudgmentCode[ChunkSeconds];
        long total = 0;

        for (var from = since; from < until; from = from.AddSeconds(ChunkSeconds))
        {
            var to = from.AddSeconds(ChunkSeconds);
            if (to > until) to = until;

            var n = (int)Math.Round((to - from).TotalSeconds);
            if (n <= 0) continue;

            // FetchEventsAsync 内部已经往前放宽 6 小时（Note T1：AW 只按事件自己的开始
            // 时刻过滤，跨进查询窗口的事件会凭空消失），Paint 会把越界的部分裁掉。
            var win = await aw.FetchEventsAsync(winBucket, from, to).ConfigureAwait(false);
            var afk = await aw.FetchEventsAsync(afkBucket, from, to).ConfigureAwait(false);

            total += CountSpan(from, n, win, afk, rules, goal, span);
            progress?.Invoke(to, total);
        }

        return total;
    }
}
