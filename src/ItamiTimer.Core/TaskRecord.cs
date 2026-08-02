using System.Text.Json.Serialization;

namespace ItamiTimer.Core;

/// <summary>A task's final state. There's no in-between "state machine state" -- the phase is always derived (§3).</summary>
public enum RecordStatus
{
    /// <summary>Submitted, not yet ended.</summary>
    Committed,

    /// <summary>Focus achieved and rest completed. Ends here, never auto-starting the next round (Principle 1).</summary>
    Completed,

    /// <summary>Abandoned by the user partway through.</summary>
    Abandoned,
}

/// <summary>
/// The persisted model for a submitted task.
///
/// One goal, chosen via radio button; locked once Start is pressed.
/// </summary>
public sealed record TaskRecord
{
    /// <summary>
    /// The task's start time, **truncated to a whole minute**
    /// (<see cref="TimeGrid.FloorToMinute"/>, §14.1). Locked once submitted, never changes
    /// (Principle 1).
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// The committed focus length (minutes). Locked once submitted.
    /// </summary>
    public required int FocusMinutes { get; init; }

    /// <summary>
    /// The currently selected goal's name (matches a group name in rules.json).
    /// A single radio-button choice, locked once Start is pressed.
    /// </summary>
    public required string? Group { get; init; }

    public RecordStatus Status { get; init; } = RecordStatus.Committed;

    /// <summary>Only has a value when <see cref="Status"/> is Abandoned.</summary>
    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>
    /// Rest length (minutes) = <b>⌈focus ÷ 5⌉</b> (finalized by the user, 2026-08-02).
    ///
    /// <b>Only ever reads the <see cref="FocusMinutes"/> locked in at submission,
    /// regardless of how long this round actually took</b>: even across two archive
    /// rolls (§4.4), a 50-minute task still gets a 10-minute break. What archiving
    /// decrements is the "remaining target", a different quantity. Using the remaining
    /// target to compute rest would mean the longer you drag it out, the shorter your
    /// break gets -- a backwards incentive (DECISIONS H6).
    ///
    /// The original formula was <c>⌊focus/5⌋+1</c>, and that +1 had two jobs, <b>neither of
    /// which exists anymore</b>:
    ///
    /// <b>(1) Compensating for "detection lag"</b> -- the old rest started from a
    /// completion moment <b>derived from the ledger</b>, up to 60 seconds later than the
    /// actual moment of detection. In the second version, the completion moment
    /// <b>is</b> the tick of detection itself (§4.5), so there's no lag left to compensate for.
    ///
    /// <b>(2) Guaranteeing rest for any nonzero length</b> -- <c>⌊focus/5⌋</c> computed 0
    /// when focus &lt; 5, meaning the rest phase wouldn't exist at all and the rest wedge
    /// would never be visible. <c>⌈focus/5⌉</c> is always &gt;= 1 for focus &gt;= 1, so this
    /// patch isn't needed either.
    ///
    /// So it's now a clean "exact one fifth": 10 -> 2, 25 -> 5, 50 -> 10.
    ///
    /// ⚠️ The old +1 was also hiding a premise that was never written down anywhere:
    /// <b>the slider only produces multiples of 5</b>. Once the slider's step changed to 1
    /// on 2026-07-31, <c>⌊f/5⌋+1</c> broke on 6 of 8 values, while the test guarding it,
    /// with its <c>InlineData</c>, still stopped at 10/25/50 -- all multiples of 5, always
    /// holding. <b>A guardrail test's values must cover what the slider can actually
    /// produce.</b>
    ///
    /// Core sets no bound on the length (range constraints belong to the UI layer), so this
    /// formula must hold for <b>any positive integer</b>.
    ///
    /// <b>Never persisted</b>: writing a derived value into JSON would make people think it
    /// can be hand-edited, and then be confused when editing it does nothing.
    /// </summary>
    [JsonIgnore]
    public int RestMinutes => (FocusMinutes + 4) / 5;
}
