namespace ItamiTimer.Core;

/// <summary>
/// The second-level judgment code. This is exactly what the buffer stores,
/// one byte each.
///
/// ⚠️ <b>The value's magnitude is "covering priority", not "how favourable it is to the
/// user"</b>: the smaller the value, the later it's painted, and the more authoritative it
/// is.
///
/// Ranked by favourability, <see cref="Afk"/> (blank, not your fault) should sit above
/// <see cref="OffTask"/> (red, entirely your fault); but afk must be <b>painted last</b> to
/// override the window judgment -- when nobody's there, whatever the window shows doesn't
/// matter -- so its value must be smaller. Whoever swaps these two based on
/// "favourability" makes afk's priority <b>silently stop working, with no error</b>
/// (DECISIONS H1).
///
/// When deciding "does this second count as focus", always write it as <c>&gt;= Focused</c>,
/// never enumerate specific code values: <b>counts as focus ⟺ code ≥ Focused(4)</b>. That
/// way adding a new code later can't accidentally miss a spot.
/// </summary>
public enum JudgmentCode : byte
{
    /// <summary>Never painted. Not counted, not drawn.</summary>
    Init = 0,

    /// <summary>Time still expected to be spent (the commitment arc). Recomputed every tick, see <see cref="JudgmentBuffer.RefreshGray"/>.</summary>
    Gray = 1,

    /// <summary>ActivityWatch's afk says nobody's there. Not counted, but not your fault either (§0.4.1).</summary>
    Afk = 2,

    /// <summary>A window event exists, doesn't match the selected goal. Fail-closed.</summary>
    OffTask = 3,

    /// <summary>A window event exists, matches the selected goal.</summary>
    Focused = 4,

    /// <summary>
    /// This second has no record at all from ActivityWatch -- either the whole tick
    /// couldn't connect, or it connected fine but this second had no event.
    /// <b>Counts as focus</b>: not being able to produce data is ActivityWatch's own fault,
    /// and the user shouldn't be penalized for it (§3.1, a knowing fail-open).
    /// </summary>
    AwOffline = 5,
}
