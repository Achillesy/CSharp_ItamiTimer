namespace ItamiTimer.Core;

/// <summary>
/// One cell on the coloured ring = one minute. **The one and only
/// contract between the judgment layer and the rendering layer.**
///
/// <b>This is a projection of the buffer, not a minute-by-minute accumulating array</b>
/// (Principle 4). The test: close the UI and reopen it, and every cell's colour must be
/// reconstructible exactly as before. So it carries no state of its own -- recomputed from
/// <see cref="JudgmentBuffer"/> on every tick.
///
/// <b>The five counts map one-to-one to <see cref="JudgmentCode"/></b> and always add up to
/// 60: the start is truncated to a whole minute (A6), and `ToMinuteCells` only ever emits
/// whole minutes, so there's no such thing as "the last cell is under 60 seconds".
///
/// <b>Carries no colour</b> -- how to colour it is the rendering layer's job (§8's fourth
/// rule): the CLI renders it as ANSI colour blocks, the dial renders it as a coloured ring.
/// "The colour blocks are purely cosmetic, not the ledger" holds precisely because of this:
/// the judgment layer never knows what green or red even means.
///
/// <b>But it does carry a tier</b> (<see cref="Tier"/>, added 2026-08-02). This class used
/// to say "doesn't carry a tier either", and the result was that §4.6's rules got written
/// out **twice**, once in the CLI and once for the dial -- the three focus thresholds, the
/// argmax, the tie-break toward the larger value, repeated word for word. The two happened
/// to still agree, but changing one and forgetting the other wouldn't raise any error --
/// the same disease as `executeCommand`'s two parallel read paths (§15.4).
///
/// The dividing line is drawn like this: **"what this cell should be read as" is judgment;
/// "what a given reading should be drawn as" is rendering.** A rendering layer that wants
/// to go back to a continuous encoding can still ignore <see cref="Tier"/> and use the raw
/// counts directly.
///
/// Keeping the raw counts instead of only storing the tier carries one more implication:
/// if the dial ever wants to go back to D1's **continuous** barrel-stave encoding, the data
/// is still there, and the judgment layer wouldn't need to change.
/// </summary>
/// <param name="Index">
/// Zero-based; cell i covers <c>[task start + i minutes, +1 minute)</c>.
/// **It's also where the lap number comes from** (<c>Index / 60</c> -> lane 0 or 1, §8.3).
/// ⚠️ After archiving, the task's start moves forward an hour (§4.4), so `Index` restarts
/// from 0 -- and that's **correct**: the whole reason archiving exists is to keep the dial
/// always showing only the most recent hour or two.
/// </param>
/// <param name="Start">
/// This cell's start time, which naturally lands on a whole minute -- which is why
/// "the minute hand IS the write head" (§8.2.2): the angle comes straight from it.
/// </param>
/// <param name="FocusSeconds">Code >= <see cref="JudgmentCode.Focused"/>, i.e. the portion counted as focus.</param>
/// <param name="OffTaskSeconds">A window event exists but doesn't match the goal. Red.</param>
/// <param name="AfkSeconds">Afk says nobody's there. Not counted, but not your fault either -- a hollow dashed box.</param>
/// <param name="GraySeconds">The commitment arc: time not reached yet, still expected to be spent.</param>
/// <param name="InitSeconds">Never painted (a hole left by a missed tick). Draws nothing.</param>
public readonly record struct MinuteCell(
    int Index,
    DateTimeOffset Start,
    int FocusSeconds,
    int OffTaskSeconds,
    int AfkSeconds,
    int GraySeconds,
    int InitSeconds)
{
    /// <summary>
    /// What this cell should be read as. **The rule is written in exactly
    /// this one place.**
    ///
    /// If there's any focus at all, it's tiered by <c>&gt;40 / &gt;20 / &gt;0</c>; when
    /// there isn't a single second of focus, the <b>largest count</b> among the other four
    /// categories wins, ties broken toward the <b>larger code value</b> (OffTask &gt; Afk
    /// &gt; Gray &gt; Init, fail-closed).
    ///
    /// Argmax rather than "majority": when three categories are mixed, none of them might
    /// reach a majority, and a threshold-based rule would default into red -- so "29
    /// seconds away + 28 seconds off-task" would get the whole cell judged entirely red --
    /// <b>painting someone stepping away as red is wronging them</b> (§0.4.1). Argmax has
    /// no threshold, so it has no such cliff.
    /// </summary>
    public CellTier Tier
    {
        get
        {
            if (FocusSeconds > 40) return CellTier.FocusFull;
            if (FocusSeconds > 20) return CellTier.FocusMid;
            if (FocusSeconds > 0) return CellTier.FocusLow;

            var best = InitSeconds;
            var pick = CellTier.NotDrawn;
            if (GraySeconds >= best) { best = GraySeconds; pick = CellTier.Pending; }
            if (AfkSeconds >= best) { best = AfkSeconds; pick = CellTier.Away; }
            if (OffTaskSeconds >= best) pick = CellTier.OffTask;
            return pick;
        }
    }
}

/// <summary>
/// How a cell is read (§4.6). **The order is the "who covers whom" order**, just like
/// <see cref="JudgmentCode"/>: ties are broken toward whichever comes later.
/// </summary>
public enum CellTier : byte
{
    /// <summary>Never painted (a hole left by a missed tick). Draws nothing.</summary>
    NotDrawn,

    /// <summary>The commitment arc: not reached yet.</summary>
    Pending,

    /// <summary>Not present. Not counted, not your fault.</summary>
    Away,

    /// <summary>A window event exists but doesn't match.</summary>
    OffTask,

    /// <summary>1-20 seconds of focus.</summary>
    FocusLow,

    /// <summary>21-40 seconds of focus.</summary>
    FocusMid,

    /// <summary>41-60 seconds of focus.</summary>
    FocusFull,
}
