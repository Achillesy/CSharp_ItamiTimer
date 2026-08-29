using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.Core;

/// <summary>One event fetched from ActivityWatch, already normalized into this project's shape.</summary>
/// <param name="App">Only present for window events; null for afk events.</param>
/// <param name="Status">Only present for afk events: not-afk / afk.</param>
public readonly record struct AwEvent(
    DateTimeOffset Start,
    double DurationSeconds,
    string? App,
    string? Title,
    string? Status)
{
    /// <summary>
    /// **Don't write this as <c>Start.AddSeconds(DurationSeconds)</c>.** .NET's
    /// `AddSeconds(double)` **rounds its argument to the nearest millisecond**, while
    /// ActivityWatch's timestamps are microsecond precision.
    ///
    /// The consequence is very subtle (hit on real data on 2026-07-27): each event's end
    /// point is off by up to 0.5ms, so adjacent events no longer meet exactly end-to-end,
    /// and replay ends up slicing a pile of sub-millisecond fragment intervals, unable to
    /// merge what should be one continuous off-task stretch -- the same 22 minutes of
    /// history had its "number of off-task episodes" balloon from a correct dozen or so up
    /// to 54. The total duration looked fine; only the "how many times" number was wrong,
    /// which made it especially easy to miss.
    /// </summary>
    public DateTimeOffset End => Start.AddTicks((long)Math.Round(DurationSeconds * TimeSpan.TicksPerSecond));
}

public sealed class AwUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The access layer for ActivityWatch's local REST API. This is
/// the **only** place in Core that touches the network.
/// </summary>
public sealed class AwClient : IDisposable
{
    public const string WindowBucketType = "currentwindow";
    public const string AfkBucketType = "afkstatus";

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _bucketIds = [];

    /// <summary>
    /// 默认超时 10 秒——这是按**运行期那种 4 分钟窗口的小查询**定的：AW 卡住时宁可让这一拍
    /// fail-open 过去，也不能把整分钟的节拍拖住。
    ///
    /// 回填是完全相反的场景，得用 <see cref="Backfill.ClientTimeoutSeconds"/>，见那边的注释。
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    public AwClient(string baseUrl = "http://127.0.0.1:5600", int timeoutSeconds = DefaultTimeoutSeconds)
    {
        // §6.1.2: must bypass the system proxy. A system-level SOCKS/HTTP proxy swallows
        // localhost traffic along with everything else, showing up as a mysterious
        // connection failure. Already hit this trap in the sibling AWJ project.
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        try
        {
            using var resp = await _http.GetAsync(path);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new AwUnavailableException(
                $"Cannot reach ActivityWatch at {_http.BaseAddress}. Make sure aw-server is running.", e);
        }
    }

    /// <summary>Finds a bucket's id by its type field (something like aw-watcher-window_Codex-Win11), caching it once found.</summary>
    public async Task<string> FindBucketIdAsync(string bucketType)
    {
        if (_bucketIds.TryGetValue(bucketType, out var cached)) return cached;

        var buckets = await GetAsync("/api/0/buckets/");
        foreach (var b in buckets.EnumerateObject())
        {
            if (b.Value.TryGetProperty("type", out var t) && t.GetString() == bucketType)
            {
                var id = b.Value.GetProperty("id").GetString()!;
                _bucketIds[bucketType] = id;
                return id;
            }
        }

        // §6.1.1: the afk bucket is required, not optional. Without it, "walk away while
        // staying focused on the goal app" becomes completely invisible -- the cheapest
        // exploit available. Never degrade to "always assume present".
        throw new AwUnavailableException(
            $"No bucket of type {bucketType} found. Both watchers (window and afk) must be running.");
    }

    /// <summary>
    /// 这个 bucket 自己的建立时刻——它可能持有数据的最早边界。
    ///
    /// <b>只用来给首次回填的分块循环定个下界，查完就扔，绝不落盘</b>（DECISIONS I3）。
    /// 把它写进 <c>during.json</c> 是错的：重装 AW、迁移 datastore 都会让这个值变，
    /// 而且播种发生在程序启动路径上，那一刻 AW 可能根本连不上——于是「已播种但起点未知」
    /// 这个状态无论如何都省不掉，materialize 它只是在旁边多加一条会变质的脆弱路径。
    ///
    /// 回填窗口的起点<b>不需要准确，只需要足够早</b>；真正的裁剪是 AW 自己干的。
    ///
    /// 返回 null = 这个 AW 版本没给 <c>created</c> 字段，调用方自己兜底。
    /// </summary>
    public async Task<DateTimeOffset?> FindBucketCreatedAsync(string bucketType)
    {
        var buckets = await GetAsync("/api/0/buckets/");
        foreach (var b in buckets.EnumerateObject())
        {
            if (b.Value.TryGetProperty("type", out var t) && t.GetString() == bucketType
                && b.Value.TryGetProperty("created", out var c)
                && DateTimeOffset.TryParse(c.GetString(), out var created))
                return created.ToLocalTime();   // 和 FetchEventsAsync 一样，在边界上归一到本地时区
        }
        return null;
    }

    /// <summary>
    /// 覆盖 <c>[since, until)</c> 的全部事件：**「跨进区间的那几条」+「区间本身」**，
    /// 两次请求，结果完备。
    ///
    /// **Note T1**（姊妹项目 AWJ 2026-07-26 实测）：ActivityWatch **只按事件自己的开始
    /// 时刻过滤**，不按区间相交。一条在 `since` 之前开始、延伸进区间的事件会**凭空消失**。
    ///
    /// ⚠️ **这里原来的对策是"统一往前放宽 6 小时"，2026-08-29 换掉了**，因为那个 6 小时
    /// **既是猜的、又不可证充分**：
    /// - 不够时**不报错**——一个窗口连续保持同一标题超过 6 小时（挂机、长视频、盯着一个
    ///   PDF 过夜）照样整条丢掉。而 <see cref="Backfill"/> 是 fail-closed 的，丢掉的秒
    ///   **不计入**，也就是这个洞在**少算用户的时间**；
    /// - 够用时也很贵——实测一次拉回 337 条 / 65.7 KB，只为裁出其中几分钟，而
    ///   `Backfill` 按 24 小时分块，每块都白拉一遍那 6 小时的重叠。
    ///
    /// 现在改成先问一句"<b>谁跨进了这个区间</b>"（`?limit=N&amp;end=since`，实测 367 字节
    /// 就能拿到覆盖起点的那条，哪怕它是三天前开始的），再精确查区间本身。**没有任何
    /// 需要猜的常数**，而且更便宜。
    /// </summary>
    public async Task<List<AwEvent>> FetchEventsAsync(string bucketId, DateTimeOffset since, DateTimeOffset until)
    {
        // ① 跨进区间的那几条。
        // ⚠️ **不能只取 1 条**：afk 桶里有 start 相同、duration 不同的**重叠事件**（实测
        // 308.1 / 313.1 / 293.0 / 298.0 秒），只取最新开始的那条可能漏掉更长的那条，
        // 于是"人不在"的一段就被算成在座——正是最不能犯的那类错。
        var events = await FetchLatestAsync(bucketId, CrossingHeadLimit, before: since);

        // ② 区间本身，精确，不放宽
        var q = $"?start={Uri.EscapeDataString(since.UtcDateTime.ToString("o"))}" +
                $"&end={Uri.EscapeDataString(until.UtcDateTime.ToString("o"))}";
        events.AddRange(Parse(await GetAsync($"/api/0/buckets/{Uri.EscapeDataString(bucketId)}/events{q}")));

        events.Sort((x, y) => x.Start.CompareTo(y.Start));
        return events;
    }

    /// <summary>
    /// 问"谁跨进了这个区间"时取几条。窗口事件不重叠、1 条就够；afk 桶有重叠事件，
    /// 多取几条兜住。取多了没有代价——越界的部分 <see cref="Judgment.Paint"/> 会裁掉。
    /// </summary>
    private const int CrossingHeadLimit = 5;

    /// <summary>
    /// 每个 bucket 的 <c>last_updated</c>（<see cref="AwMirror"/> 每秒的探针，DESIGN §7.5）。
    ///
    /// **一次请求 739 字节**（实测）同时给出两个 watcher 的新鲜度，它既是"在线探测"，
    /// 也是"要不要去拉事件"的游标——`last_updated` 没动就说明这个桶什么都没变。
    ///
    /// ⚠️ **两个桶的节奏差很远**（实测）：window 每 ~10 秒前进一次，afk 卡住 38 秒还在涨
    /// （它只在状态变化或自己的慢心跳时才写）。**别拿同一个阈值判"死没死"**，afk 会被
    /// 误判，而误判的后果是 afk 覆盖失效 → 离开被算成跑偏 → 冤枉人（DECISIONS O9）。
    /// </summary>
    public async Task<Dictionary<string, DateTimeOffset>> FetchLastUpdatedAsync()
    {
        var buckets = await GetAsync("/api/0/buckets/");
        var map = new Dictionary<string, DateTimeOffset>();
        foreach (var b in buckets.EnumerateObject())
        {
            if (b.Value.TryGetProperty("last_updated", out var lu) && lu.GetString() is { } str)
                map[b.Name] = DateTimeOffset.Parse(str).ToLocalTime();
        }
        return map;
    }

    /// <summary>
    /// 最近 <paramref name="limit"/> 条事件（最新的在前），**不带时间区间**。
    ///
    /// 这是绕开 T1 的另一条路，而且比放宽 6 小时又便宜又更对：**最新那条正是正在进行中的
    /// 那条**（实测：start 在 23 分钟前、duration 一直长到此刻），所以"停在同一个窗口很久"
    /// 这种情况天然覆盖，不需要猜要放宽多少。实测 `limit=20` 是 4.7 KB，而放宽 6 小时的
    /// 范围查询是 65.7 KB。
    ///
    /// <paramref name="before"/> 给了的话就是"<b>早于</b>这个时刻的最近 N 条"——初始化镜像
    /// 时用它拿"覆盖镜像起点的那条事件"（实测有效，DESIGN §7.5）。
    /// </summary>
    public async Task<List<AwEvent>> FetchLatestAsync(string bucketId, int limit, DateTimeOffset? before = null)
    {
        var q = $"?limit={limit}";
        if (before is { } b) q += $"&end={Uri.EscapeDataString(b.UtcDateTime.ToString("o"))}";
        return Parse(await GetAsync($"/api/0/buckets/{Uri.EscapeDataString(bucketId)}/events{q}"));
    }

    private static List<AwEvent> Parse(JsonElement arr)
    {
        var events = new List<AwEvent>();
        foreach (var e in arr.EnumerateArray())
        {
            var data = e.GetProperty("data");
            events.Add(new AwEvent(
                // ⚠️ Must call ToLocalTime(). ActivityWatch's timestamp is UTC (trailing
                // Z), and Parse gives it a +00:00 offset; that offset would otherwise flow
                // all the way through to the display layer, and a log line would print
                // "Focus achieved at 16:37:35" when local time was actually 00:37:35 --
                // caught during a real run on 2026-07-28, the same root cause as the one
                // recorded in ClockDisplayTests (06:40:45 / 14:40:45): the same output
                // mixing two time zones. StartedAt comes from DateTimeOffset.Now (local),
                // so ActivityWatch's moment is normalized to local time right here **at
                // the boundary**, instead of letting two different offsets flow into the
                // core.
                //
                // Only affects **display**: DateTimeOffset comparison and subtraction
                // already operate on the instant itself, so every duration calculation was
                // already correct before this fix.
                Start: DateTimeOffset.Parse(e.GetProperty("timestamp").GetString()!).ToLocalTime(),
                DurationSeconds: e.GetProperty("duration").GetDouble(),
                App: data.TryGetProperty("app", out var a) ? a.GetString() : null,
                Title: data.TryGetProperty("title", out var t) ? t.GetString() : null,
                Status: data.TryGetProperty("status", out var s) ? s.GetString() : null));
        }
        events.Sort((x, y) => x.Start.CompareTo(y.Start));
        return events;
    }

    /// <summary>窗口/afk 两个 bucket 的 id，一次问齐（<see cref="AwMirror"/> 那条路每秒都要用）。</summary>
    public async Task<(string Window, string Afk)> FindWatcherBucketsAsync()
        => (await FindBucketIdAsync(WindowBucketType), await FindBucketIdAsync(AfkBucketType));

    public void Dispose() => _http.Dispose();
}
