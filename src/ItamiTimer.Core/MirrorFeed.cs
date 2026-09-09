namespace ItamiTimer.Core;

/// <summary>
/// **把 ActivityWatch 的数据喂进 <see cref="AwMirror"/> 的那一段**（DESIGN §7.5）——
/// 初始化、每秒推进、按节拍取数、掉线兜底，全在这里。
///
/// ## 为什么它必须住在 Core
///
/// `AwMirror` 是纯转换，两个前端本来就共享；但**驱动**曾经只存在于 App 的
/// `TaskSession` 里，于是 `itami` 拿得到镜像这个类、却没有把它喂饱的代码，只能退回
/// "自己查 AW → 直接喂事件"的老路。**两个前端因此走在两条数据路上**，而 §15.7 那次
/// 事故的结论正是"验证工具和被验证对象必须是同一个引擎、同一个节拍"。
///
/// 这跟 DECISIONS L25 是同一个形状：`Command.BuildShell` 当年也是因为"不要出现用户
/// 命令行测试可行、到了 UI 跑不动"才收成一份定义。区别是那次用 `<Compile Include>`
/// 把 App 的源文件 link 进 CLI（因为它带平台味道），而镜像驱动是**纯 AW 管道**，
/// 没有一点平台味道，所以正经放进 Core，两个前端自然共享，不需要那条反向边。
///
/// ## Core 不打日志
///
/// 状态变化用回调抛给调用方（跟 <see cref="Backfill"/> 的 `progress` 同一路数）：
/// App 那层接到日志里，CLI 那层想打屏就打屏。
/// </summary>
public sealed class MirrorFeed
{
    /// <summary>
    /// 跟 AW 通信的间隔：**5 秒，而且对齐到 :00 :05 :10 … :55**（判据是
    /// <c>now.Second % PollSeconds == 0</c>，读的是墙上时钟的秒数，不是"距上次多久"）。
    ///
    /// **为什么是 5 秒**（2026-08-29 实测定的，不是拍的）：AW 的窗口桶是**批量提交**的
    /// ——`last_updated` 的前进间隔实测 40 个样本全部落在 9.3~11.1 秒，规律得像个定时器，
    /// 而且**不受活动驱动**（整整 100 秒没有窗口切换时，它照样每 10 秒跳一次）。更要命
    /// 的一条：捕捉到的那次真实窗口切换，**新事件第一次出现时 `duration` 已经等于它的
    /// 全部年龄**（10.2 秒 / dur=10.2）——它不是"先以 0 秒诞生再长大"，而是在提交那一刻
    /// **一次性写下、直接覆盖整整 10 秒**。
    ///
    /// 也就是说**信息本身 10 秒才产生一次**。每秒去问，约 90% 的请求换回"没变"。
    /// 取提交节拍的一半：保证每次提交最多 5 秒内被看到，不会因为相位不巧而整整错过
    /// 一个周期；而总延迟的地板（0~10.5 秒攒在 AW 那边）我们本来就控制不了。
    ///
    /// ⚠️ 别改成 10 秒：轮询周期和提交周期同量级会撞相位，最坏能逼近 21 秒。
    /// ⚠️ 也别改回 1 秒：那 5 倍请求买到的只有 ~2 秒平均延迟，而地板是 ~5 秒。
    /// </summary>
    public const int PollSeconds = 5;

    /// <summary>**窗口桶**每次取最近几条。窗口事件互不重叠，十几秒内正常最多几条，20 是很大的余量。</summary>
    private const int FetchLimit = 20;

    /// <summary>
    /// **afk 桶单独一个、大得多的上限**（3.9.3，DECISIONS O10）。
    ///
    /// ⚠️ **别跟 <see cref="FetchLimit"/> 合成一个数**。afk 桶跟窗口桶的形状完全不同：
    /// `aw-watcher-afk` 每次心跳**新写一行**，而不是延长已有那条——一次离开会在桶里留下
    /// 几十条 <b>start 完全相同、duration 各不相同</b>的独立事件（实测 30 条，30 个不同
    /// id）。更要命的是 <b>id 顺序跟时长不相关</b>（实测 id=131044 是 1310.8s，更晚的
    /// id=131060 只有 1296.1s），所以"最新的 N 条"里**可能恰好没有最长的那条**。
    ///
    /// 后果是 afk 只覆盖到某个中途的时刻，再往后的秒仍由窗口事件说了算——锁屏离开会被
    /// 判成跑偏，正是 O9 说的"冤枉人"。用户 2026-09-09 报的就是这个：离开 19 分钟
    /// （`loginwindow "Login"`），afk 桶全程正确，表盘却大片红格。实测同一次查询：
    /// <c>limit=20</c> 只覆盖到 09:07:53，<c>limit=60</c> 才够到真正的终点 09:08:17。
    ///
    /// 取 500：实测副本约 **1.4 条/分钟**，500 条够连续离开约 6 小时。响应只在 afk 桶的
    /// `last_updated` 真的前进时才拉，而它写得很稀疏（实测卡住 38 秒还不写）。
    ///
    /// ⚠️ **别改成按区间查**（2026-09-09 试过并否掉）：AW 的 <c>end=</c> 会把事件**裁到
    /// 那个时刻**——实测头查询无论 limit 5/20/100，覆盖都停在区间起点 09:00:56 一秒不多；
    /// 而区间查询本身只按事件自己的 start 过滤（Note T1），那条 08:46:26 开始的离开事件
    /// 压根不在返回里。结果 afk 会停在 <c>now-244s</c>，最后 4 分钟全归窗口管，**比现在
    /// 更糟**。不带 <c>end</c> 的"最近 N 条"能拿到**正在进行中那条**，这正是它存在的理由。
    /// </summary>
    private const int AfkFetchLimit = 500;

    private readonly AwClient _aw;
    private readonly AwMirror _mirror;

    private string? _winBucket, _afkBucket;
    private bool _ready;
    private bool _down;

    /// <summary>上一次探到的两个桶的 <c>last_updated</c>：既是"有没有新东西"的游标，也是陈旧诊断的依据。</summary>
    private DateTimeOffset _winSeen, _afkSeen;

    /// <summary>镜像本体。判定、反色、滴答、跑偏归因都从它读。</summary>
    public AwMirror Mirror => _mirror;

    /// <summary>窗口桶最后一次写入的时刻，<c>default</c> = 还没探到过。**陈旧诊断只能用它**，见 <see cref="RefreshAsync"/>。</summary>
    public DateTimeOffset WindowLastUpdated => _winSeen;

    /// <summary>初始化完成：(吸收的窗口事件数, afk 事件数)。</summary>
    public Action<int, int>? OnInitialized;

    /// <summary>AW 从"能用"变成"不能用"，参数是原因。**只在状态变化时调**，不是每次失败都调。</summary>
    public Action<string>? OnUnavailable;

    /// <summary>AW 从"不能用"恢复。同样只在状态变化时调。</summary>
    public Action? OnRestored;

    public MirrorFeed(AwClient aw, DateTimeOffset start, GroupRules rules, string? selectedGroup)
    {
        _aw = aw;
        _mirror = new AwMirror(start, rules, selectedGroup);
    }

    /// <summary>
    /// **每秒调一次**（由调用方那唯一的钟驱动，DECISIONS L8）。里面自己决定这一秒
    /// 要不要真的跟 AW 说话：
    ///
    /// <list type="bullet">
    ///   <item><b>每一秒</b>：把镜像推进到 <paramref name="now"/> 并跑预测。**纯内存、
    ///         零成本**——反色要的 1 秒粒度全靠它，跟通信频率无关。</item>
    ///   <item><b>只在 <see cref="PollSeconds"/> 的整数倍那一秒</b>：探一次两个桶的
    ///         `last_updated`（实测 739 字节，一个请求同时给出"活着没"和"变了没"），
    ///         **前进了才**去拉事件。</item>
    /// </list>
    ///
    /// 所以对外只有**一个节拍**（每秒），不多出时间点；而稳态下真正的事件查询约每
    /// 10 秒才发生一次。
    ///
    /// 失败一律收敛成"那些秒没有记录"（<see cref="AwMirror.MarkUnavailable"/>），
    /// 不抛出去——§3.1 的知情 fail-open：没有记录算专注。
    /// </summary>
    public async Task RefreshAsync(DateTimeOffset now)
    {
        try
        {
            if (_winBucket is null || _afkBucket is null)
                (_winBucket, _afkBucket) = await _aw.FindWatcherBucketsAsync();

            if (!_ready)
            {
                await InitializeAsync(now);
                _ready = true;
            }
            else
            {
                List<AwEvent> win = [], afk = [];
                if (now.Second % PollSeconds == 0)
                {
                    var seen = await _aw.FetchLastUpdatedAsync();
                    win = await PullIfChangedAsync(_winBucket, seen, _winSeen, FetchLimit);
                    afk = await PullIfChangedAsync(_afkBucket, seen, _afkSeen, AfkFetchLimit);
                    if (seen.TryGetValue(_winBucket, out var w)) _winSeen = w;
                    if (seen.TryGetValue(_afkBucket, out var a)) _afkSeen = a;
                }
                // 空列表也要 Apply：镜像仍然要推进到 now，让预测把空隙和末尾填上
                _mirror.Apply(win, afk, now);
            }

            if (_down)
            {
                _down = false;
                OnRestored?.Invoke();
            }
        }
        catch (AwUnavailableException ex)
        {
            _mirror.MarkUnavailable(now);
            if (!_down)
            {
                _down = true;
                OnUnavailable?.Invoke(ex.Message);
            }
        }
    }

    /// <summary>某个桶的 <c>last_updated</c> 前进了才去拉事件，否则连请求都不发。</summary>
    private async Task<List<AwEvent>> PullIfChangedAsync(
        string bucket, IReadOnlyDictionary<string, DateTimeOffset> seen, DateTimeOffset last, int limit)
        => seen.TryGetValue(bucket, out var lu) && lu > last
            ? await _aw.FetchLatestAsync(bucket, limit)
            : [];

    /// <summary>
    /// 灌满镜像：把 <c>[镜像起点, now]</c> 查全，一次喂进去。
    ///
    /// "跨进区间的那几条"由 <see cref="AwClient.FetchEventsAsync"/> 自己负责（§7.6 的
    /// "头 + 精确区间"），所以这里不用再自己拼一遍——你可能六个小时前就开着那个窗口
    /// 没动过，而 AW 只按事件自己的 start 过滤（T1）。
    /// </summary>
    private async Task InitializeAsync(DateTimeOffset now)
    {
        var from = _mirror.Oldest;

        var win = await _aw.FetchEventsAsync(_winBucket!, from, now);
        var afk = await _aw.FetchEventsAsync(_afkBucket!, from, now);

        _mirror.Apply(win, afk, now);
        OnInitialized?.Invoke(win.Count, afk.Count);
    }
}
