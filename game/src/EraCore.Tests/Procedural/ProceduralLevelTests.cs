using EraCore.Contracts.Foundation;
using EraCore.Procedural;
using Xunit;

namespace EraCore.Tests.Procedural;

// [C-19][F-007] T-140 生成产物数据面：身份哈希（重摇次数入哈希）与
// JSON 序列化的字节稳定性（同关卡→字节同串——逐字节全等断言的材料）。
public class ProceduralLevelTests
{
    private static ProceduralLevel Sample(int rerolls = 0, int mapSeed = 20261018) => new(
        Id: "procedural:level-era1-1A2B3C4D",
        Era: "era1:era-ancient",
        Slot: 1,
        NameZh: "程序林地·1A2B3C4D",
        NameEn: "Procedural Grove #1A2B3C4D",
        Threat: 0.3,
        WinWaves: 13,
        GraceMultiplier: 1.75,
        BudgetSeconds: 2400,
        AdjacentEnemyCamps: 0,
        Map: new ProceduralMap(
            mapSeed, 16, 24, 0.1, 0.0, 0.0,
            new CellCoord(8, 23),
            new[] { new CellCoord(8, 0), new CellCoord(6, 0) },
            new CellCoord(2, 14)),
        InitialStock: new[] { new ProceduralStockLine("era1:res-wood", 225.0) },
        Waves: new ProceduralWaveTable(
            14, 3, 3, 1.6, 4, 15.0,
            new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
            false, null, null, null),
        CommandScript: new[]
        {
            new ProceduralScriptStep(1, "build-labor-camp", new CellCoord(4, 20), null, null, null, null, null, null),
            new ProceduralScriptStep(1, "assign-carrier", null, null, null, null, 3, 4, "porter"),
        },
        Rerolls: rerolls,
        TerrainHash: 1234567890L)
    {
        StrategyObjectiveIds = new[] { "campaign:obj-s4" },
        ChallengeObjectiveIds = new[] { "campaign:obj-c8" },
    };

    [Fact]
    public void Identity_hash_is_stable_and_field_sensitive()
    {
        // legal — 身份哈希：同产物双算全等；任一字段漂移（含重摇次数）即变。
        ProceduralLevel level = Sample();
        Assert.Equal(level.IdentityHash(), Sample().IdentityHash());
        Assert.NotEqual(level.IdentityHash(), Sample(rerolls: 1).IdentityHash());
        Assert.NotEqual(level.IdentityHash(), Sample(mapSeed: 20261019).IdentityHash());
    }

    [Fact]
    public void Reroll_count_is_inside_the_identity_hash()
    {
        // legal — 重摇次数入哈希（T-140 硬要求）：两张仅重摇计数不同的图
        // 必不同哈希（即使地图面巧合相同）。
        ProceduralLevel first = Sample(rerolls: 0);
        ProceduralLevel second = Sample(rerolls: 4);
        Assert.Equal(first.TerrainHash, second.TerrainHash);
        Assert.NotEqual(first.IdentityHash(), second.IdentityHash());
    }

    [Fact]
    public void Json_serialization_is_byte_stable_for_equal_levels()
    {
        // legal — JSON 面：同关卡字节同串（逐字节全等断言材料）；
        // 不同重摇计数的关卡串不同。
        Assert.Equal(Sample().ToLevelJson(), Sample().ToLevelJson());
        Assert.NotEqual(Sample().ToLevelJson(), Sample(rerolls: 1).ToLevelJson());
    }

    [Fact]
    public void Json_carries_the_handcrafted_schema_blocks()
    {
        // legal — 同构面：序列化产物携带手工关 schema 全部结构块
        //（map/initial_stock/waves/command_script/objectives/unlocks）。
        using var doc = System.Text.Json.JsonDocument.Parse(Sample().ToLevelJson());
        var root = doc.RootElement;
        Assert.Equal("urn:eratd:content:levels:1", root.GetProperty("$schema").GetString());
        Assert.Equal("era1:era-ancient", root.GetProperty("era").GetString());
        var item = root.GetProperty("items")[0];
        Assert.Equal("procedural:level-era1-1A2B3C4D", item.GetProperty("id").GetString());
        Assert.Equal(13, item.GetProperty("win_waves").GetInt32());
        Assert.Equal(2, item.GetProperty("map").GetProperty("entry_cells").GetArrayLength());
        Assert.Equal(1, item.GetProperty("initial_stock").GetArrayLength());
        Assert.True(item.GetProperty("map").TryGetProperty("base_cell", out _));
        Assert.True(item.GetProperty("map").TryGetProperty("warehouse_cell", out _));
        Assert.True(item.GetProperty("waves").TryGetProperty("regular_roster", out _));
        Assert.Equal(2, item.GetProperty("command_script").GetArrayLength());
        Assert.Equal(1, item.GetProperty("objectives").GetProperty("strategy").GetArrayLength());
        Assert.True(item.GetProperty("unlocks").TryGetProperty("actions", out _));
    }
}
