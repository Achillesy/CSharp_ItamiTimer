using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItamiTimer.Core;

/// <summary>从 AW 取回的一条事件，已经归一化成本项目用的形状。</summary>
/// <param name="App">窗口事件才有；afk 事件为 null。</param>
/// <param name="Status">afk 事件才有：not-afk / afk。</param>
public readonly record struct AwEvent(
    DateTimeOffset Start,
    double DurationSeconds,
    string? App,
    string? Title,
    string? Status)
{
    /// <summary>
    /// **不要写成 <c>Start.AddSeconds(DurationSeconds)</c>。** .NET 的 `AddSeconds(double)`
    /// 会把参数**四舍五入到最近的毫秒**，而 AW 的时间戳是微秒精度。
    ///
    /// 后果很隐蔽（2026-07-27 在真实数据上撞到）：每条事件的终点偏差最多 0.5 毫秒，
    /// 相邻事件不再严格首尾相接，于是重放切出一堆亚毫秒的碎片区间，连续的偷懒段
    /// 合并不起来——同一段 22 分钟的历史，"偷懒次数"从正确的十几次膨胀成 54 次。
    /// 总时长看着没问题，只有"几次"这个数字是错的，所以特别容易蒙混过去。
    /// </summary>
    public DateTimeOffset End => Start.AddTicks((long)Math.Round(DurationSeconds * TimeSpan.TicksPerSecond));
}

public sealed class AwUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// AW 本地 REST API 的访问层（DESIGN.md §8 模块 1）。这是 Core 里**唯一**碰网络的地方。
/// </summary>
public sealed class AwClient : IDisposable
{
    public const string WindowBucketType = "currentwindow";
    public const string AfkBucketType = "afkstatus";

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _bucketIds = [];

    public AwClient(string baseUrl = "http://127.0.0.1:5600")
    {
        // §6.1.2：必须绕过系统代理。系统级 SOCKS/HTTP 代理会把 localhost 流量
        // 一起吞掉，表现成莫名其妙的连不上。AWJ 已经踩过这个坑。
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10),
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

    /// <summary>探活。提交任务前用——连不上就拒绝提交，不允许开始一个从一开始就没法核算的任务（§6.2）。</summary>
    public async Task<string> ProbeAsync()
    {
        var info = await GetAsync("/api/0/info");
        return info.TryGetProperty("hostname", out var h) ? h.GetString() ?? "?" : "?";
    }

    /// <summary>按 bucket 的 type 字段找 id（形如 aw-watcher-window_Codex-Win11），找到后缓存。</summary>
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

        // §6.1.1：afk bucket 是必需的，不是可选的。缺了它，"停在目标应用上起身走开"
        // 就完全隐形——那是最省力的作弊路径。绝不降级为"永远视为在座"。
        throw new AwUnavailableException(
            $"No bucket of type {bucketType} found. Both watchers (window and afk) must be running.");
    }

    /// <summary>
    /// 区间查询。
    ///
    /// **注意 T1**（AWJ 2026-07-26 实测）：AW 只按事件**自己的开始时间**过滤，不按区间
    /// 重叠。一条开始于 since 之前、延伸进区间的事件会**静默消失**。所以这里内部把
    /// 查询窗口往前放宽 6 小时，返回后由调用方自己裁（§14.2）。
    /// </summary>
    public async Task<List<AwEvent>> FetchEventsAsync(string bucketId, DateTimeOffset since, DateTimeOffset until)
    {
        var widened = since.AddHours(-6);
        var q = $"?start={Uri.EscapeDataString(widened.UtcDateTime.ToString("o"))}" +
                $"&end={Uri.EscapeDataString(until.UtcDateTime.ToString("o"))}";
        var arr = await GetAsync($"/api/0/buckets/{Uri.EscapeDataString(bucketId)}/events{q}");

        var events = new List<AwEvent>();
        foreach (var e in arr.EnumerateArray())
        {
            var data = e.GetProperty("data");
            events.Add(new AwEvent(
                // ⚠️ 必须 ToLocalTime()。AW 的 timestamp 是 UTC（末尾 Z），Parse 出来
                // 偏移量是 +00:00；这个偏移量会一路传到 FocusCompletedAt，于是日志里
                // 打出「专注达成于 16:37:35」而实际是本地 00:37:35 —— 2026-07-28 实跑
                // 抓到，跟 ClockDisplayTests 记的那次（06:40:45 / 14:40:45）同一个根因：
                // 同一份输出里混了两个时区。StartedAt 来自 DateTimeOffset.Now（本地），
                // 所以在**边界上**就把 AW 的时刻归一到本地，别让两种偏移量流进核心。
                //
                // 只影响【显示】：DateTimeOffset 的比较和减法本来就是按瞬时点算的，
                // 所有时长核算在修之前就是对的。
                Start: DateTimeOffset.Parse(e.GetProperty("timestamp").GetString()!).ToLocalTime(),
                DurationSeconds: e.GetProperty("duration").GetDouble(),
                App: data.TryGetProperty("app", out var a) ? a.GetString() : null,
                Title: data.TryGetProperty("title", out var t) ? t.GetString() : null,
                Status: data.TryGetProperty("status", out var s) ? s.GetString() : null));
        }
        events.Sort((x, y) => x.Start.CompareTo(y.Start));
        return events;
    }

    public void Dispose() => _http.Dispose();
}
