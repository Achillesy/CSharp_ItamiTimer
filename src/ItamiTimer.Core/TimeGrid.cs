namespace ItamiTimer.Core;

/// <summary>
/// Whole-minute alignment. A pure function, no clock -- every moment is passed in as a parameter.
/// </summary>
public static class TimeGrid
{
    /// <summary>
    /// Wipes out seconds and anything finer, landing on the whole minute it's currently
    /// inside of.
    ///
    /// Used in two places:
    ///
    /// **A task's startedAt** (settled by the user on 2026-07-27, DECISIONS A6). Starting
    /// at 23:13:10 -> counted from 23:13:00, not 23:14:00. The cost, stated plainly: this
    /// counts up to 59 seconds **before** the click too.
    ///
    /// The original design rounded **up**, precisely to avoid those 59 seconds; but then
    /// the user would have to sit and wait up to 59 seconds after clicking before it even
    /// started -- the user chose not to wait. (<c>CeilToMinute</c> was therefore removed on
    /// 2026-08-02: by the end it was only used by its own test, and the reasoning behind
    /// that decision is enough to keep here.)
    ///
    /// Rounding up would also have had a side effect that **still holds here too**: every
    /// cell is always a full 60 seconds, because the start still lands on a whole minute.
    ///
    /// **The end of the accounting interval** (§14.2): the minute currently in progress
    /// isn't drawn -- it waits until that minute has finished, otherwise that cell would
    /// keep flickering as the seconds tick by.
    /// </summary>
    public static DateTimeOffset FloorToMinute(DateTimeOffset t)
        => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}
