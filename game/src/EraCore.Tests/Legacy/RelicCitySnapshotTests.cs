using Xunit;
using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using EraCore.Persistence;

namespace EraCore.Tests.Legacy;

// [C-20][F-056][F-061] 跃迁快照编解码与 A5 核心区几何（T-074）：
// 编解码往返/损坏语义/核心区一次性计算口径。K-7 载荷由 C-20 拥有
// （ILevelLayoutSnapshot 注释明示 payload 归 C-04/C-20）。
public class RelicCitySnapshotTests
{
    private static RelicCitySnapshot SampleSnapshot()
    {
        var anchor = new CellCoord(48, 48);
        var zone = new[] { new CellCoord(40, 40), new CellCoord(41, 40), new CellCoord(42, 41) };
        var buildings = new[]
        {
            new RelicBuildingSnapshot(100_001, "era1:tower-wood-arrow", 1, 90, -3, -3),
            new RelicBuildingSnapshot(100_002, "era1:tower-xbow-bastion", 3, 270, 2, -1),
            new RelicBuildingSnapshot(100_003, "era1:tower-fire-oil-tower", 4, 0, 0, 4),
        };
        return new RelicCitySnapshot(anchor, zone, buildings);
    }

    // ---- codec ------------------------------------------------------ legal

    [Fact] // legal — 往返逐字段无损
    public void codec_roundtrips_field_for_field()
    {
        RelicCitySnapshot snapshot = SampleSnapshot();
        RelicCitySnapshot decoded = RelicCityCodec.Decode(RelicCityCodec.Encode(snapshot));
        Assert.True(snapshot.ValueEquals(decoded));
        Assert.Equal(snapshot.Anchor, decoded.Anchor);
        Assert.Equal(snapshot.Buildings[1].TypeId, decoded.Buildings[1].TypeId);
        Assert.Equal(snapshot.Buildings[2].Orientation, decoded.Buildings[2].Orientation);
    }

    [Fact] // legal — 同一快照编码字节全等（跃迁确定性断言的载体）
    public void encoding_is_byte_deterministic()
    {
        byte[] first = RelicCityCodec.Encode(SampleSnapshot());
        byte[] second = RelicCityCodec.Encode(SampleSnapshot());
        Assert.Equal(first, second);
    }

    // ---- codec --------------------------------------------------- boundary

    [Fact] // boundary — 空建筑/空核心区是合法载荷（空城跃迁）
    public void empty_city_roundtrips()
    {
        var empty = new RelicCitySnapshot(new CellCoord(1, 1), Array.Empty<CellCoord>(), Array.Empty<RelicBuildingSnapshot>());
        RelicCitySnapshot decoded = RelicCityCodec.Decode(RelicCityCodec.Encode(empty));
        Assert.True(empty.ValueEquals(decoded));
    }

    // ---- codec ---------------------------------------------------- illegal

    [Theory] // illegal — 结构违例在编码侧拒绝（tier/朝向/空类型）
    [InlineData(0, 90, "era1:t")] // tier 0
    [InlineData(5, 90, "era1:t")] // tier 5
    [InlineData(2, 400, "era1:t")] // facing 400
    [InlineData(2, -1, "era1:t")] // facing -1
    [InlineData(2, 90, "")] // 空 type id
    public void encode_rejects_structural_violations(int tier, int facing, string typeId)
    {
        var bad = new RelicBuildingSnapshot(1, typeId, tier, facing, 0, 0);
        Assert.Throws<ArgumentException>(() => RelicCityCodec.Encode(
            new RelicCitySnapshot(new CellCoord(1, 1), Array.Empty<CellCoord>(), new[] { bad })));
    }

    [Fact] // illegal — 双 id 拒绝
    public void encode_rejects_duplicate_building_ids()
    {
        RelicBuildingSnapshot a = SampleSnapshot().Buildings[0];
        RelicBuildingSnapshot b = a with { RelX = 5 };
        Assert.Throws<ArgumentException>(() => RelicCityCodec.Encode(
            new RelicCitySnapshot(new CellCoord(1, 1), Array.Empty<CellCoord>(), new[] { a, b })));
    }

    [Theory] // illegal — 损坏载荷抛 SaveCorruptException（K-7 语义）
    [InlineData("bad-magic")]
    [InlineData("truncate-tail")]
    [InlineData("bad-version")]
    public void decode_reports_corruption(string mode)
    {
        byte[] payload = RelicCityCodec.Encode(SampleSnapshot());
        byte[] damaged = mode switch
        {
            "bad-magic" => new[] { (byte)'X', (byte)'Y', (byte)'Z', (byte)'W' }.Concat(payload[4..]).ToArray(),
            "truncate-tail" => payload[..^2],
            "bad-version" => payload[..3].Concat(new byte[] { 9 }).Concat(payload[4..]).ToArray(),
            _ => throw new InvalidOperationException(mode),
        };
        Assert.Throws<SaveCorruptException>(() => RelicCityCodec.Decode(damaged));
    }

    // ---- A5 core zone ---------------------------------------------- legal

    [Fact] // legal — 半径 10 ∩ 已建区外扩 2 的集合口径（07 #13）
    public void core_zone_intersects_radius_with_built_margin()
    {
        var anchor = new CellCoord(50, 50);
        var built = new[] { new CellCoord(50, 50), new CellCoord(58, 50) }; // Cheb 8：外扩 2 恰达半径边界
        IReadOnlyList<CellCoord> zone = CoreZoneGeometry.ComputeCoreZone(built, anchor, coreRadiusCells: 10, buildMarginCells: 2);
        Assert.Contains(new CellCoord(50, 50), zone); // 已建格本身在集合内
        Assert.Contains(new CellCoord(58, 50), zone);
        Assert.Contains(new CellCoord(60, 50), zone); // 58+2：外扩 2 恰达半径 10 边界
        Assert.DoesNotContain(new CellCoord(61, 50), zone); // 外扩越出半径截断
        Assert.DoesNotContain(new CellCoord(53, 55), zone); // 距两建筑 >2（外扩不可达）
    }

    [Fact] // legal — 行主序排序（字节确定性）
    public void core_zone_sorts_row_major()
    {
        var anchor = new CellCoord(50, 50);
        var built = new[] { new CellCoord(52, 52), new CellCoord(48, 48) };
        IReadOnlyList<CellCoord> zone = CoreZoneGeometry.ComputeCoreZone(built, anchor, 10, 2);
        var sorted = zone.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        Assert.Equal(sorted, zone);
    }

    [Fact] // boundary — 建筑在半径之外：其外扩格仍被半径闸门截断
    public void core_zone_excludes_far_buildings_entirely()
    {
        var anchor = new CellCoord(50, 50);
        var built = new[] { new CellCoord(70, 70) }; // Cheb 20 > 10
        IReadOnlyList<CellCoord> zone = CoreZoneGeometry.ComputeCoreZone(built, anchor, 10, 2);
        Assert.Empty(zone); // 已建区∩半径=∅（建筑本体也不进集合）
    }

    [Fact] // illegal — 负半径/负边距拒绝
    public void core_zone_rejects_negative_geometry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreZoneGeometry.ComputeCoreZone(Array.Empty<CellCoord>(), new CellCoord(1, 1), -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CoreZoneGeometry.ComputeCoreZone(Array.Empty<CellCoord>(), new CellCoord(1, 1), 10, -1));
    }
}
