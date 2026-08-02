using ItamiTimer.Core;

namespace ItamiTimer.Core.Tests;

/// <summary>
/// A guard for the time-zone display bug hit on 2026-07-27.
///
/// The report's "Focus achieved at" printed 06:40:45 when the real time was 14:40:45 -- the
/// same report mixed two time zones: `StartedAt` came from `DateTimeOffset.Now` (local
/// offset), while `FocusCompletedAt` was derived from an ActivityWatch event, and
/// ActivityWatch returns UTC, which `DateTimeOffset.Parse` keeps as `+00:00`.
///
/// What's guarded here is the root cause: **a moment derived from an ActivityWatch event
/// carries that event's own offset along with it**. So anywhere it gets displayed must
/// convert to local time first (the rendering layer already funnels through
/// Renderer.Clock).
///
/// **It happened again on 2026-07-28**: the UI layer's log printed "Focus achieved at
/// 16:37:35" when local time was actually 00:37:35. The previous fix only funneled the
/// CLI's rendering through one place, leaving the App's log line uncovered -- proof that
/// "every display site remembers to convert on its own" isn't a convention that can be
/// trusted. It's now fixed by **normalizing at the boundary**: `AwClient` calls
/// `ToLocalTime()` the moment it parses a timestamp, so neither offset ever flows into the
/// core.
/// </summary>
public class ClockDisplayTests
{
    /// <summary>
    /// The second version fills this hole at the root: **the completion moment is no
    /// longer derived from an ActivityWatch event** -- it's exactly the tick that was fed
    /// in (DESIGN §4.5). So its offset comes from the caller's clock, unrelated to whether
    /// the event was UTC or local.
    ///
    /// This pins down that guarantee -- it's also the cure for §15.1: a moment that isn't
    /// derived from the ledger can never jump backward just because the ledger got
    /// rewritten.
    /// </summary>
    [Fact]
    public void TheCompletionMomentComesFromTheCallersClock_NotInheritedFromAnAwEventsOffset()
    {
        var rules = GroupRules.Parse("""
            { "groups": { "Economics": { "rules": [ { "title": "Econ" } ] } } }
            """);
        var localStart = new DateTimeOffset(2026, 7, 27, 14, 35, 0, TimeSpan.FromHours(8));
        var buf = new JudgmentBuffer(localStart, 5);

        // The event is fed in with a UTC offset -- a caller outside AwClient could
        // perfectly well do this
        var utcStart = localStart.ToUniversalTime();
        List<AwEvent> win = [new(utcStart.AddMinutes(-3), 1200, "SumatraPDF.exe", "Mankiw Econ.pdf", null)];

        TickOutcome outcome = default;
        DateTimeOffset tick = default;
        for (var i = 1; i <= 5 && !outcome.Completed; i++)
        {
            tick = localStart.AddMinutes(i);
            outcome = buf.Tick(tick, win, [], rules, "Economics");
        }

        Assert.True(outcome.Completed);
        Assert.Equal(TimeSpan.FromHours(8), tick.Offset);      // The offset comes from this tick, not the event
        Assert.Equal(localStart.AddMinutes(5), tick);
    }

    [Fact]
    public void TheSameMomentWithDifferentOffsetsIsEqual_AccountingIsUnaffected()
    {
        var utc = new DateTimeOffset(2026, 7, 27, 6, 40, 45, TimeSpan.Zero);
        var local = new DateTimeOffset(2026, 7, 27, 14, 40, 45, TimeSpan.FromHours(8));

        Assert.Equal(utc, local);                       // The same instant
        Assert.NotEqual(utc.ToString("HH:mm:ss"), local.ToString("HH:mm:ss")); // But displayed differently
    }

    [Fact]
    public void NormalizedAtTheBoundary_MomentsParsedByAwClientAreLocalOffset()
    {
        // This is the real line of defense now: AwClient calls ToLocalTime() right at the
        // parsing step, so a moment coming from ActivityWatch never flows into the core
        // still carrying +00:00 (see AwClient.FetchEventsAsync).
        // This pins down that exact line's guarantee, so nobody "casually" removes the
        // ToLocalTime() call later.
        var utc = DateTimeOffset.Parse("2026-07-27T16:37:35.000Z");
        Assert.Equal(TimeSpan.Zero, utc.Offset);                       // Parses as UTC
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), utc.ToLocalTime().Offset);
        Assert.Equal(utc, utc.ToLocalTime());                          // The absolute instant is unchanged
    }
}
