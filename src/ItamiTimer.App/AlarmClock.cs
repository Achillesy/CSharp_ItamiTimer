namespace ItamiTimer.App;

/// <summary>
/// The alarm model. **Pure logic, no UI, no clock** -- now is always a
/// parameter, so the whole class can be unit tested directly
/// (tests/ItamiTimer.App.Tests/AlarmClockTests.cs).
///
/// There's exactly one piece of state: <see cref="FireAt"/>, the ring time computed from
/// the last time the hand was moved. **The yellow hand's position is derived from it**
/// (the time point mod 12 hours, set by the user on 2026-07-30) -- its position isn't
/// stored separately, and "did it change, did it fire" isn't recorded either, the same
/// approach as this project's "state is derived, not accumulated" (Principle 4).
///
/// Firing is a monotonic comparison, <see cref="ShouldFire"/>, with no angular tolerance
/// (0.5°/minute, and a 1.5° "tolerance" would mean firing 3 minutes early, DECISIONS E1).
/// </summary>
public sealed class AlarmClock
{
    /// <summary>One notch = 1 minute; the dial has 720 minutes per revolution (12-hour clock), so 720 stops total.</summary>
    public const double SlotMinutes = 1;
    public const double FaceMinutes = 720;

    /// <summary>
    /// The ring time computed from the last time the hand was moved. **Not cleared once it
    /// fires** -- it's still the source of the yellow hand's position; "already fired" is
    /// tracked separately by <see cref="_fired"/>. Null = the hand has never been moved.
    /// </summary>
    public DateTime? FireAt { get; private set; }

    /// <summary>Whether this round's time point has already fired (or was already expired on restore = invalid).</summary>
    private bool _fired;

    /// <summary>
    /// The yellow hand's position on the dial (0-719 minutes): **the time point mod 12
    /// hours**. Rests at 12 o'clock (0) if the hand has never been moved.
    /// </summary>
    public double Position => FireAt is { } at
        ? (at.Hour % 12) * 60 + at.Minute + at.Second / 60.0
        : 0;

    /// <summary>
    /// Moves the yellow hand by <paramref name="minutes"/> minutes -- positive is
    /// clockwise, negative is counter-clockwise (scroll wheel: forward = counter-clockwise,
    /// back = clockwise). Immediately recomputes and fixes "when will it next ring" with a
    /// strict algorithm; the tooltip reads <see cref="FireAt"/> directly -- what's shown and
    /// what will actually fire are the same value.
    ///
    /// <see cref="NextRing"/> preserves the dial position (today's T / T+12 / tomorrow's T
    /// are all the same mod 12 hours), so the derived <see cref="Position"/> lands exactly
    /// on the new position after the move.
    /// </summary>
    public void Bump(double minutes, DateTime now)
    {
        // C#'s % gives a negative result for negative numbers; wrapping counter-clockwise past 12 needs true modular arithmetic
        var pos = ((Position + minutes) % FaceMinutes + FaceMinutes) % FaceMinutes;
        FireAt = NextRing(now, pos);
        _fired = false;
    }

    /// <summary>Is it due yet. A monotonic comparison, doesn't re-derive the yellow hand's position.</summary>
    public bool ShouldFire(DateTime now) => !_fired && FireAt is { } at && now >= at;

    /// <summary>Fires once and is done -- the alarm is one-shot, not a daily repeat (DECISIONS E5). The time point stays, as the source of the yellow hand's position.</summary>
    public void MarkFired() => _fired = true;

    /// <summary>
    /// Restored from a previous session: loading the time point is only for display, it
    /// **doesn't activate the alarm**. An alarm missed while the program was closed doesn't
    /// fire late.
    /// </summary>
    public void Restore(DateTime? fireAt, DateTime now)
    {
        FireAt = fireAt;
        _fired = true; // Never active on startup; only activated once the user moves the wheel or turns on Execute
    }

    /// <summary>
    /// Activates the alarm (called when the user scrolls the wheel / turns on Execute).
    /// Only activates if the target time is still in the future -- an expired one doesn't fire late.
    /// </summary>
    public void Activate(DateTime now)
    {
        if (FireAt is { } at && at > now)
            _fired = false;
    }

    /// <summary>
    /// Yellow-hand slot (12-hour dial time T) -> the next actual moment it will ring.
    /// All three tiers use **strictly less than** (set by the user on 2026-07-30,
    /// DECISIONS E2):
    ///
    /// <code>
    /// now &lt; today's T       -> today's T (the morning half)
    /// now &lt; today's T + 12h -> T + 12 (the afternoon half)
    /// otherwise               -> tomorrow's T
    /// </code>
    ///
    /// **Deliberately not "less than or equal"** -- if now lands exactly on the yellow
    /// hand's slot (the moment it was set happens to coincide with the hour hand), that
    /// means "12 hours from now", not "right now", otherwise it would suddenly ring in the
    /// middle of being adjusted.
    /// </summary>
    public static DateTime NextRing(DateTime now, double faceMinutes)
    {
        var t = now.Date.AddMinutes(faceMinutes);
        var tPlus12 = t.AddHours(12);
        if (now < t) return t;
        if (now < tPlus12) return tPlus12;
        return t.AddDays(1);
    }
}
