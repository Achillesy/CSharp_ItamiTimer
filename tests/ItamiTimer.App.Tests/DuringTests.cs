using ItamiTimer.App;

namespace ItamiTimer.App.Tests;

/// <summary>
/// during.json 从 1.0.x 的「账本」迁到 1.1.0 的「checkpoint」（§11.2 / DECISIONS I1）。
///
/// ⚠️ **这里只准调纯函数**：<see cref="During.FromLegacy"/> 和 <see cref="During.Apply"/>
/// 都不读时钟、不落盘；带 I/O 的那半边（<c>Migrate</c> / <see cref="During.Advance"/> /
/// <see cref="During.Save"/>）写的是**用户真实的** `%LOCALAPPDATA%\ItamiTimer\during.json`，
/// 单元测试里调一次就会把用户的累计时长覆盖成测试数据，而且悄无声息——2026-08-06 已经
/// 栽过一次（DECISIONS I5）。
/// </summary>
public class DuringTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 21, 30, 0, TimeSpan.FromHours(8));

    /// <summary>
    /// **迁移必须给老账本盖上「算到此刻」的戳，绝不能留 null。**
    ///
    /// 留 null 的语义是「从没统计过」，下一次任务启动会从 AW 历史的开头重数一遍再**加到**
    /// 这个已有的值上——同一段时间算两遍，数字直接翻倍。老账本的语义本来就是「到此为止
    /// 全部已入账」，所以起点就是迁移这一刻。
    /// </summary>
    [Fact]
    public void MigratingTheOldLedgerStampsRecordedThroughNow_LeavingItNullWouldDoubleCount()
    {
        var migrated = During.FromLegacy("""{ "accumulatedSeconds": { "Economics": 38258 } }""", Now);

        Assert.NotNull(migrated);
        Assert.Equal(38258, migrated["Economics"]);
        Assert.Equal(Now, migrated.RecordedThrough("Economics"));
    }

    /// <summary>
    /// 2026-08-02 之前秒数字段还是 <c>double</c>，更老的文件里可能留着小数——四舍五入救回来，
    /// 不归零。**这是唯一一个丢了就补不回来的数据**（AW 的保留期之外就真没了）。
    /// </summary>
    [Fact]
    public void FractionalSecondsFromTheOldestSchemaAreRoundedRatherThanReset()
    {
        var migrated = During.FromLegacy("""{ "accumulatedSeconds": { "Economics": 38253.4 } }""", Now);

        Assert.NotNull(migrated);
        Assert.Equal(38253, migrated["Economics"]);
    }

    /// <summary>1.1.0 自己的文件不该被当成老文件再迁一次。</summary>
    [Fact]
    public void ACurrentFormatFileIsNotTreatedAsLegacy()
    {
        Assert.Null(During.FromLegacy(
            """{ "goals": { "Economics": { "seconds": 1, "recordedThrough": null } } }""", Now));
    }

    [Fact]
    public void GarbageAndEmptyInputAreNotLegacyFiles()
    {
        Assert.Null(During.FromLegacy("", Now));
        Assert.Null(During.FromLegacy("   ", Now));
        Assert.Null(During.FromLegacy("not json at all", Now));
        Assert.Null(During.FromLegacy("""{ "accumulatedSeconds": {} }""", Now));
    }

    // ---------------------------------------------------------------- checkpoint 语义

    /// <summary>没播种过的小目标读出来是 <c>(0, null)</c>——和播过种的全 0 条目行为完全一致。</summary>
    [Fact]
    public void AnUnseededGoalReadsAsZeroAndNeverRecorded()
    {
        var d = new During();
        Assert.Equal(0, d["Economics"]);
        Assert.Null(d.RecordedThrough("Economics"));
    }

    /// <summary>
    /// <c>recordedThrough</c> **只进不退**：同一分钟内连开两次任务，checkpoint 不会被推回去，
    /// 那一段也就不会被数第二遍。
    /// </summary>
    [Fact]
    public void AdvancingNeverMovesTheCheckpointBackwards()
    {
        var d = new During();
        d.Goals["Economics"] = new GoalTime { Seconds = 100, RecordedThrough = Now };

        d.Apply("Economics", 50, Now.AddMinutes(-10));   // 试图往回推

        Assert.Equal(150, d["Economics"]);                 // 秒数照加
        Assert.Equal(Now, d.RecordedThrough("Economics")); // 但 checkpoint 原地不动
    }

    [Fact]
    public void AdvancingAccumulatesSecondsAndMovesTheCheckpointForward()
    {
        var d = new During();
        d.Goals["Economics"] = new GoalTime { Seconds = 100, RecordedThrough = Now };

        d.Apply("Economics", 3600, Now.AddHours(2));

        Assert.Equal(3700, d["Economics"]);
        Assert.Equal(Now.AddHours(2), d.RecordedThrough("Economics"));
    }

    /// <summary>
    /// 回填一秒都没数到时，<b>checkpoint 照样要推进</b>——「这段时间确实什么都没干」和
    /// 「这段时间还没统计过」是两回事，混为一谈会让空窗期被反复重扫。
    /// </summary>
    [Fact]
    public void AZeroSecondBackfillStillAdvancesTheCheckpoint()
    {
        var d = new During();
        d.Apply("Economics", 0, Now);

        Assert.Equal(0, d["Economics"]);
        Assert.Equal(Now, d.RecordedThrough("Economics"));
    }
}
