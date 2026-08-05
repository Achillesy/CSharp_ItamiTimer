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
    /// Range query.
    ///
    /// **Note T1** (measured by the sibling AWJ project on 2026-07-26): ActivityWatch only
    /// filters by an event's **own start time**, not by interval overlap. An event that
    /// started before `since` and extends into the range will **silently disappear**. So
    /// this widens the query window 6 hours into the past internally, and the caller clips
    /// it themselves afterward (§14.2).
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

    public void Dispose() => _http.Dispose();
}
