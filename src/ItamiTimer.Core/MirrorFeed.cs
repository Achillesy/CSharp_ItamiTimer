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

    private readonly AwClient _aw;
    private readonly AwMirror _mirror;
    private readonly Func<TimeSpan> _idle;
    private readonly IdleAfk _idleAfk = new();

    private string? _winBucket;
    private bool _ready;
    private bool _down;

    /// <summary>上一次探到的窗口桶的 <c>last_updated</c>：既是"有没有新东西"的游标，也是陈旧诊断的依据。</summary>
    private DateTimeOffset _winSeen;

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

    /// <param name="idle">
    /// 「距上次键鼠输入多久」。**注进来而不是直接调**，因为它必然是平台调用
    /// （<c>App/Platform/InputIdle.cs</c>），而 Core 一句平台代码都不许有。
    ///
    /// ⚠️ 两个前端**必须传同一个实现**——`itami start` 的全部意义就是"验的是界面跑的
    /// 那台引擎"（§15.7）。CLI 那边靠 csproj 的 `<Compile Include>` 把同一个源文件 link
    /// 进去，跟 `Command.cs` 一样的路数（DECISIONS L5/L25）。测试传假的。
    /// </param>
    public MirrorFeed(AwClient aw, DateTimeOffset start, GroupRules rules, string? selectedGroup,
                      Func<TimeSpan> idle)
    {
        _aw = aw;
        _idle = idle;
        _mirror = new AwMirror(start, rules, selectedGroup);
    }

    /// <summary>
    /// **每秒调一次**（由调用方那唯一的钟驱动，DECISIONS L8）。里面自己决定这一秒
    /// 要不要真的跟 AW 说话：
    ///
    /// <list type="bullet">
    ///   <item><b>每一秒</b>：读一次本机键鼠空闲、把镜像推进到 <paramref name="now"/> 并跑
    ///         预测。**纯内存、零成本**——反色要的 1 秒粒度全靠它，跟通信频率无关。</item>
    ///   <item><b>只在 <see cref="PollSeconds"/> 的整数倍那一秒</b>：探一次**窗口桶**的
    ///         `last_updated`（实测 739 字节，一个请求同时给出"活着没"和"变了没"），
    ///         **前进了才**去拉事件。</item>
    /// </list>
    ///
    /// **3.10.0 起只有窗口桶要取数**：afk 那一半改由 <see cref="IdleAfk"/> 从本机键鼠空闲
    /// 算出来（DESIGN §7.5.1）。所以这里也不再需要 afk 桶的游标和 bucket id。
    ///
    /// 所以对外只有**一个节拍**（每秒），不多出时间点；而稳态下真正的事件查询约每
    /// 10 秒才发生一次。
    ///
    /// 失败一律收敛成"那些秒没有记录"（<see cref="AwMirror.MarkUnavailable"/>），
    /// 不抛出去——§3.1 的知情 fail-open：没有记录算专注。
    /// </summary>
    public async Task RefreshAsync(DateTimeOffset now)
    {
        // **在 try 外面、在任何网络调用之前**：离开与否是本机自己的事，AW 连不上也照算。
        // 下面两条路（正常 Apply / 掉线 MarkUnavailable）都要用它。
        _idleAfk.Track(now, _idle());
        var afk = _idleAfk.Events();

        try
        {
            _winBucket ??= await _aw.FindBucketIdAsync(AwClient.WindowBucketType);

            if (!_ready)
            {
                await InitializeAsync(now, afk);
                _ready = true;
            }
            else
            {
                List<AwEvent> win = [];
                if (now.Second % PollSeconds == 0)
                {
                    var seen = await _aw.FetchLastUpdatedAsync();
                    win = await PullIfChangedAsync(_winBucket, seen, _winSeen, FetchLimit);
                    if (seen.TryGetValue(_winBucket, out var w)) _winSeen = w;
                }
                // 窗口那半是空列表也要 Apply：镜像仍然要推进到 now，让预测把空隙和末尾填上。
                // ⚠️ afk 那半**每一拍都是完整的当前区间集**，不是"这一拍新取到的"——正因如此，
                //    窗口事件重刷整段（锁屏那条 loginwindow 横跨两小时）也盖不掉它。3.9.x 那个
                //    大 bug 的根因就在这里：从前 afk 靠取数，"这一拍没取到"就等于没人盖回来。
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
            // AW 掉线也照画 afk：那个信号根本不经过 AW（见 MarkUnavailable 的注释）
            _mirror.MarkUnavailable(now, afk);
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
    ///
    /// **afk 那半不查 AW**（3.10.0）：第一拍的 <see cref="IdleAfk"/> 已经能给出"此刻这次
    /// 离开是从什么时候开始的"（起点回填到输入停止那一刻），超出环的部分 <c>PaintAfk</c>
    /// 自己裁掉。程序刚起来时更早的离开确实无从得知——但那几秒也早就滚出这 245 秒的环了。
    /// </summary>
    private async Task InitializeAsync(DateTimeOffset now, IReadOnlyList<AwEvent> afk)
    {
        var win = await _aw.FetchEventsAsync(_winBucket!, _mirror.Oldest, now);

        _mirror.Apply(win, afk, now);
        OnInitialized?.Invoke(win.Count, afk.Count);
    }
}
