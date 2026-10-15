using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Mods;
using EraCore.Tests.World;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-165][F-156] 示例模组集成（T-103 验收：外置 JSON 增改生效）：
// game/assets/mods/samples/ 两个样板模组（more-arrows 新增 / rebalance-patch 覆盖）
// 与本体 era1/era2 内容过同一装载管线后——注册表返回模组版、模组塔可进战斗侧
// TowerSpec/BuildingRegistry（游戏内生效）、数值锚定 R7 口径不越现有曲线、
// 卸载（restart 后干净）。真实仓库文件直读，非内联夹具。

public class SampleModsTests
{
    private static string FindRepoMarker(string marker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "game", marker);
            if (Directory.Exists(probe) || File.Exists(probe))
            {
                return Path.Combine(dir.FullName, "game");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"未找到 game/{marker}（测试须在仓库内运行）");
    }

    private static string GameDir() => FindRepoMarker("assets");

    private static string Era1File(string name) => Path.Combine(GameDir(), "content", "era1", name);

    private static string Era2File(string name) => Path.Combine(GameDir(), "content", "era2", name);

    private static string SamplesDir() => Path.Combine(GameDir(), "assets", "mods", "samples");

    private static readonly string[] FirstParty =
    {
        Era1File("era.json"),
        Era1File("towers.json"),
        Era1File("enemies.json"),
        Era2File("era.json"),
        Era2File("towers.json"),
        Era2File("enemies.json"),
        Era2File("recipes.json"),
        Era2File("levels.json"),
        Era2File("heroes.json"),
        Path.Combine(GameDir(), "content", "scrolls", "era2.json"),
    };

    private static (ModLoadResult Result, ContentRegistry Registry) BootWithSamples()
    {
        var loader = new ModLoader();
        ModLoadResult result = loader.Compose(FirstParty, SamplesDir());
        var registry = new ContentRegistry();
        loader.Apply(registry, result);
        return (result, registry);
    }

    private static double Number(JsonContentItem item, string field) =>
        JsonDocument.Parse(item.RawJson).RootElement.GetProperty(field).GetDouble();

    // ---- 装载 ------------------------------------------------------------

    [Fact] // [F-165] 两示例模组全量装载：零错误、装载序=按 id 排序确定化
    public void Samples_LoadClean_InIdOrder()
    {
        (ModLoadResult result, _) = BootWithSamples();

        Assert.True(result.Ok);
        Assert.Equal(
            new[] { "more-arrows", "rebalance-patch" },
            result.Packages.Select(p => p.ModId).ToArray());
    }

    // ---- 增（外置 JSON 新增生效）------------------------------------------

    [Fact] // [F-165] 新增塔 more-arrows:tower-barbed 入注册表；中文别名可解析（F-157 aliases）
    public void Sample1_NewTower_RegisteredWithAliases()
    {
        (_, ContentRegistry registry) = BootWithSamples();

        JsonContentItem tower = (JsonContentItem)registry.Get(ContentType.Building, "more-arrows:tower-barbed");
        Assert.Equal("more-arrows", tower.SourceModId);
        Assert.False(tower.Patched);
        Assert.Equal("more-arrows:tower-barbed", registry.GetByName(ContentType.Building, "倒刺塔").Id);
        Assert.Equal("more-arrows:tower-barbed", registry.GetByName(ContentType.Building, "barbed tower").Id);
    }

    [Fact] // [F-165] 新增弹种 more-arrows:proj-barbed：DSL 三族之效果原语（on_hit slow_percent）随装载通过封闭校验
    public void Sample1_NewProjectile_CarriesValidatedEffectPrimitive()
    {
        (_, ContentRegistry registry) = BootWithSamples();

        JsonContentItem projectile = (JsonContentItem)registry.Get(ContentType.Projectile, "more-arrows:proj-barbed");
        JsonElement onHit = JsonDocument.Parse(projectile.RawJson).RootElement.GetProperty("on_hit");
        JsonElement op = Assert.Single(onHit.EnumerateArray());
        Assert.Equal("slow_percent", op.GetProperty("op").GetString());
        Assert.Equal(10, op.GetProperty("percent").GetInt32());
        Assert.Equal(30, op.GetProperty("ticks").GetInt32());
    }

    // ---- 改（外置 JSON 覆盖生效）-------------------------------------------

    [Fact] // [F-165] patch 模式覆盖内置 era1:tower-wood-arrow：注册表返回模组版（dps 12/cost 35）+ overlay 报告
    public void Sample2_PatchesBuiltInWoodArrow_RegistryReturnsModVersion()
    {
        (ModLoadResult result, ContentRegistry registry) = BootWithSamples();

        JsonContentItem patched = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood-arrow");
        Assert.True(patched.Patched);
        Assert.Equal("rebalance-patch", patched.SourceModId);
        Assert.Equal(12.0, Number(patched, "dps"));
        Assert.Equal(35.0, Number(patched, "cost_total"));

        ModOverlayReport report = Assert.Single(result.Reports);
        Assert.Equal("era1:tower-wood-arrow", report.TargetId);
        Assert.Equal("rebalance-patch", report.PatchModId);
        Assert.Equal("rebalance-patch/content/buildings.json", report.File);
        Assert.True(report.Line > 0);
    }

    [Fact] // [F-165] 未被 patch 的本体内容不受影响（era1 其余三塔保持本体来源）
    public void UnpatchedFirstPartyContent_Untouched()
    {
        (_, ContentRegistry registry) = BootWithSamples();

        foreach (string id in new[] { "era1:tower-rockfall", "era1:tower-spike-trap", "era1:tower-campfire" })
        {
            JsonContentItem item = (JsonContentItem)registry.Get(ContentType.Building, id);
            Assert.Null(item.SourceModId);
            Assert.False(item.Patched);
        }

        // era1 Building = 本体 4 + 模组新增 1（覆盖不复制条目）
        Assert.Equal(5, registry.List(ContentType.Building).Count(i => i.Id.StartsWith("era1:", StringComparison.Ordinal) || i.Id.StartsWith("more-arrows:", StringComparison.Ordinal)));
    }

    // ---- 游戏内生效（战斗侧消费）--------------------------------------------

    [Fact] // [F-165] 模组塔与补丁塔流入战斗侧：注册表 raw JSON → TowerSpec → BuildingRegistry.Place 实际落子
    public void ModTowers_FlowIntoCombatSide_TowerSpecAndPlacement()
    {
        (_, ContentRegistry registry) = BootWithSamples();

        JsonContentItem newTower = (JsonContentItem)registry.Get(ContentType.Building, "more-arrows:tower-barbed");
        JsonContentItem patchedTower = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood-arrow");

        TowerSpec newSpec = Assert.Single(TowerSpec.LoadCollection(JsonContentItemJson.AsCollectionDocument(newTower)));
        TowerSpec patchedSpec = Assert.Single(TowerSpec.LoadCollection(JsonContentItemJson.AsCollectionDocument(patchedTower)));

        Assert.Equal("more-arrows:tower-barbed", newSpec.Id);
        Assert.Equal(45, newSpec.CostTotal);
        Assert.Equal(12.0, patchedSpec.Dps); // 补丁生效到战斗数值（本体 10 → 模组 12）

        var world = new GridWorld(6, 6, new LcgRng(20261013), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        var buildings = new BuildingRegistry(world);
        var placed = buildings.Place(new EntityId(9001), newSpec, new CellCoord(2, 2));
        var placedPatched = buildings.Place(new EntityId(9002), patchedSpec, new CellCoord(3, 3));

        Assert.Equal("more-arrows:tower-barbed", placed.ContentId);
        Assert.Equal(newSpec.Hp, placed.Hp);
        Assert.Equal("era1:tower-wood-arrow", placedPatched.ContentId);
        Assert.Equal(2, buildings.Count);
    }

    // ---- R7 锚点数值红线（不越现有曲线）--------------------------------------

    [Fact] // [F-156] 模组数值锚定：新增塔 T1 cost/dps 落在 era1 T1 带内；patch 后数值仍在带内（R7 锚点口径）
    public void SampleNumbers_StayWithinEra1Tier1AnchorBand()
    {
        // 基线：本体 era1 内容（不挂模组）取 T1 武器塔数值带（临时空目录，用后即删 CS-3.8）
        string emptyMods = Path.Combine(Path.GetTempPath(), "eratd-modtests-band-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyMods);
        double costMin = 0, costMax = 0, dpsMin = 0, dpsMax = 0;
        try
        {
            ModLoadResult baseline = new ModLoader().Compose(
                new[] { Era1File("towers.json") }, emptyMods);
            Assert.True(baseline.Ok);
            // 武器塔带（dps>0）：控制/支援塔（尖刺/篝火 造价低、dps 0）另成控制带
            var baseTowers = baseline.Items
                .Select(i => JsonDocument.Parse(i.RawJson).RootElement)
                .Where(e => e.TryGetProperty("tier", out _) && e.GetProperty("tier").GetInt32() == 1
                    && e.GetProperty("dps").GetDouble() > 0)
                .ToList();
            costMin = baseTowers.Min(e => e.GetProperty("cost_total").GetDouble());
            costMax = baseTowers.Max(e => e.GetProperty("cost_total").GetDouble());
            dpsMin = baseTowers.Min(e => e.GetProperty("dps").GetDouble());
            dpsMax = baseTowers.Max(e => e.GetProperty("dps").GetDouble());
        }
        finally
        {
            Directory.Delete(emptyMods, recursive: true);
        }

        Assert.Equal(30.0, costMin); // 锚点自检：era1 T1 武器带确为 [30,60]×[10,20]（era1/towers.json）
        Assert.Equal(60.0, costMax);
        Assert.Equal(10.0, dpsMin);
        Assert.Equal(20.0, dpsMax);

        (_, ContentRegistry registry) = BootWithSamples();

        JsonContentItem newTower = (JsonContentItem)registry.Get(ContentType.Building, "more-arrows:tower-barbed");
        Assert.InRange(Number(newTower, "cost_total"), costMin, costMax);
        Assert.InRange(Number(newTower, "dps"), dpsMin, dpsMax);

        JsonContentItem patched = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood-arrow");
        Assert.InRange(Number(patched, "cost_total"), costMin, costMax);
        Assert.InRange(Number(patched, "dps"), dpsMin, dpsMax);
    }

    // ---- 卸载（restart 后干净）-----------------------------------------------

    [Fact] // [F-165] 卸载重启：无模组目录重启后——本体木箭塔回到本体数值，模组条目全数消失
    public void RestartWithoutMods_CleanState()
    {
        // 先证明带模组时确实是模组版
        (_, ContentRegistry withMods) = BootWithSamples();
        Assert.Equal(12.0, Number((JsonContentItem)withMods.Get(ContentType.Building, "era1:tower-wood-arrow"), "dps"));

        // restart：同一本体内容 + 无 mods 根目录（全新 ContentRegistry 实例）
        var loader = new ModLoader();
        ModLoadResult result = loader.Compose(FirstParty, modsRoot: null);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.True(result.Ok);
        Assert.Empty(result.Packages);
        Assert.Empty(result.Reports);
        JsonContentItem wood = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood-arrow");
        Assert.Null(wood.SourceModId);
        Assert.False(wood.Patched);
        Assert.Equal(10.0, Number(wood, "dps")); // 回到本体 10
        Assert.Null(registry.FindByName(ContentType.Building, "倒刺塔"));
        Assert.Null(registry.FindByName(ContentType.Projectile, "more-arrows:proj-barbed"));
    }
}
