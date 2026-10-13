using EraCore.World;
using Xunit;

namespace EraCore.Tests.World;

// [C-05] zone/lane id single-source validation (F-117: 分区命名与 SetRally
// lane 枚举同一数据源——rule lives only in ZoneId, GridWorld forwards to it)
public class ZoneIdTests
{
    // Documented lane-id enumeration, read out of the frozen docs and
    // hardcoded here ON PURPOSE (the docs are the source; this test proves the
    // shared validator accepts every id the docs actually use):
    //   (id, source) —
    //   east-gate   11-spec-tables SP-1 #1 意图槽 target=分区/城门名
    //               （"守住东门/Hold the east gate" → SetRally{lane}）
    //   south-gate  11-spec-tables SP-1 #7（"木材先送南门/south gate"）
    //   barbican    11-spec-tables SC-002（"敌进入瓮城分区"→瓮城=barbican）
    //   north-gate  T-032 交付的分区命名样例（GridWorldTests zone 用例族）
    //   east-road   同上（T-032 分区命名样例族）
    // The G.2 constraint table itself (v3.2 附录 G.2 = 10-recovered §三
    // T-004) defines the lane id FAMILY (SetRally/SpawnScout `lane` =
    // 语言无关稳定 id) — concrete values ship with level data; every value
    // the docs name must clear this one rule.
    private static readonly (string Id, string Source)[] DocumentedLaneIds =
    {
        ("east-gate", "11-spec-tables SP-1 #1 守住东门"),
        ("south-gate", "11-spec-tables SP-1 #7 南门"),
        ("barbican", "11-spec-tables SC-002 瓮城分区"),
        ("north-gate", "T-032 GridWorldTests 分区样例"),
        ("east-road", "T-032 GridWorldTests 分区样例"),
    };

    // ------------------------------------------------------------ legal

    [Fact] // legal — every documented lane id passes the single rule, and the GridWorld wrapper agrees with ZoneId on all of them (same-source proof)
    public void Documented_lane_enumeration_passes_the_shared_rule()
    {
        foreach (var (id, source) in DocumentedLaneIds)
        {
            Assert.True(ZoneId.IsValid(id), $"documented lane id '{id}' ({source}) must pass ZoneId.IsValid");
            Assert.True(GridWorld.IsValidZoneId(id), $"GridWorld.IsValidZoneId must agree with ZoneId on '{id}' ({source})");
        }
    }

    [Theory] // legal — charset corners of ^[a-z0-9-]+$
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("lane-0-a")]
    [InlineData("l4n3-1d")]
    public void Accepts_charset_corner_ids(string zoneId)
    {
        Assert.True(ZoneId.IsValid(zoneId));
    }

    // ------------------------------------------------------------ illegal

    [Theory] // illegal — counter-examples from the review: underscore / uppercase / space-style ids must be rejected by the shared rule
    [InlineData("mid_lane")] // underscore — outside ^[a-z0-9-]+$
    [InlineData("NorthGate")] // uppercase
    [InlineData("north gate")] // space
    [InlineData("北门")] // non-ASCII — display names live in content data, never in ids
    [InlineData("east.gate")] // dot
    public void Rejects_non_lane_style_ids(string zoneId)
    {
        Assert.False(ZoneId.IsValid(zoneId));
        Assert.False(GridWorld.IsValidZoneId(zoneId));
    }

    [Fact] // illegal — empty string and null are not ids (both validators agree)
    public void Rejects_empty_and_null()
    {
        Assert.False(ZoneId.IsValid(string.Empty));
        Assert.False(ZoneId.IsValid(null));
        Assert.False(GridWorld.IsValidZoneId(string.Empty));
        Assert.False(GridWorld.IsValidZoneId(null));
    }
}
