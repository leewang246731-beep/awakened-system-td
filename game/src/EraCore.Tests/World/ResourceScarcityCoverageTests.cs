using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Legacy;
using EraCore.World;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.World;

// [C-19][F-025] 资源地理稀缺分布审计（T-115）：设计 v3.2 §5.1「资源地理稀缺
// （富煤/油田/稀土只在特定关卡）逼扩张」——本套件验证其机制底座：
//   - 矿格（Rock 地形=矿位代理）注入的唯一通道=逐关 map 比率参数：
//     rock_ratio=0 的关在任意 seed 下零矿格（不稀普遍——稀缺绝非生成器随机赠送）；
//     rock_ratio=r 的关覆盖率=r±二项带（稀关有——数值可控）；
//   - 传承刷新丰度 ×0.5 的语料物化：E2 全部已发行关 forest_ratio 恰=
//     E1 默认丰度 × era2 legacy_refresh 乘数（wood 0.5）；
//   - 跨代大陆（E2→E3 seed 派生路径）资源节点数=内容声明且避开让位区；
//   - era3 煤位守卫（T-116 落位 levels/e3.json map 块后已翻转）：非零煤位
//     （rock 位）仅出现于稀缺标记关 L3-2 矿轨关；排期侧 era3/levels.json
//     保持无 map（载体分立）。
// 扫描数字（min/mean/max 覆盖率）随测试输出落 trx，汇总入
// docs/engineering/40-t115-crossera-audit.md §4。
public class ResourceScarcityCoverageTests
{
    private readonly ITestOutputHelper _output;

    public ResourceScarcityCoverageTests(ITestOutputHelper output) => _output = output;

    // ---- 装载辅助（仓库相对寻址 RL9） --------------------------------------

    private static string ContentDir(string relativeUnderContent)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", relativeUnderContent);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{relativeUnderContent}");
    }

    private static JsonElement.ArrayEnumerator LevelRows(string relativeUnderContent, string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContentDir(relativeUnderContent), fileName)))
            .RootElement.GetProperty("items").EnumerateArray();

    private static double RockFraction(GridWorld world)
    {
        long rock = 0;
        for (int y = 0; y < world.Height; y++)
        {
            for (int x = 0; x < world.Width; x++)
            {
                if (world.TerrainAt(new CellCoord(x, y)) == TerrainKind.Rock)
                {
                    rock++;
                }
            }
        }

        return rock / (double)(world.Width * world.Height);
    }

    // ---- legal：不稀普遍 —— rock_ratio=0 的关零矿格（作者未点名的稀缺永不出现） ----

    public static IEnumerable<object[]> AuthoredZeroRockLevels()
    {
        foreach (string eraFile in new[] { "e1.json", "e2.json" })
        {
            foreach (JsonElement row in JsonDocument.Parse(
                         File.ReadAllText(Path.Combine(ContentDir("levels"), eraFile)))
                     .RootElement.GetProperty("items").EnumerateArray())
            {
                JsonElement map = row.GetProperty("map");
                if (map.GetProperty("rock_ratio").GetDouble() == 0.0)
                {
                    yield return new object[]
                    {
                        row.GetProperty("id").GetString()!,
                        map.GetProperty("seed").GetInt64(),
                        map.GetProperty("width").GetInt32(),
                        map.GetProperty("height").GetInt32(),
                        map.GetProperty("forest_ratio").GetDouble(),
                        map.GetProperty("water_ratio").GetDouble(),
                    };
                }
            }
        }
    }

    [Theory] // [F-025] 已发行 e1/e2 全部 7 关 rock_ratio=0：真 seed 真生成器跑出零矿格
    [MemberData(nameof(AuthoredZeroRockLevels))]
    public void Authored_zero_rock_levels_generate_zero_rock_cells(
        string id, long seed, int width, int height, double forest, double water)
    {
        var world = new GridWorld(width, height, new LcgRng(seed), forest, rockRatio: 0.0, waterRatio: water);

        Assert.Equal(0.0, RockFraction(world), 12);
        _output.WriteLine($"{id}: seed={seed} {width}x{height} rock_cells=0 (rock_ratio=0)");
    }

    [Fact] // [F-025] 零矿格对 seed 稳健：40 个异 seed 全零（生成器无随机赠矿通道）
    public void Zero_rock_ratio_yields_zero_rock_cells_across_forty_seeds()
    {
        for (int i = 0; i < 40; i++)
        {
            var world = new GridWorld(32, 24, new LcgRng(20261150 + i), forestRatio: 0.05, rockRatio: 0.0, waterRatio: 0.0);
            Assert.Equal(0.0, RockFraction(world), 12);
        }
    }

    // ---- legal：稀关有 —— 指定比率关的矿格覆盖率=r±二项带（机制可控） ------

    [Fact] // [F-025] rock_ratio=0.10（跨代大陆/新关声明值）：50 seed 覆盖率全在 4σ 带内
    public void Declared_rock_ratio_maps_to_binomial_coverage_band()
    {
        const double ratio = 0.10;
        const int width = 32, height = 24, seeds = 50;
        double sigma = Math.Sqrt(ratio * (1 - ratio) / (width * height)); // 二项 σ=√(p(1-p)/格数)
        double min = 1.0, max = 0.0, sum = 0.0;

        for (int i = 0; i < seeds; i++)
        {
            var world = new GridWorld(width, height, new LcgRng(20261200 + i), forestRatio: 0.15, rockRatio: ratio, waterRatio: 0.08);
            double fraction = RockFraction(world);
            Assert.InRange(fraction, ratio - 4 * sigma, ratio + 4 * sigma);
            min = Math.Min(min, fraction);
            max = Math.Max(max, fraction);
            sum += fraction;
        }

        _output.WriteLine(
            $"rock_ratio=0.10 {width}x{height} seeds={seeds}: min={min:F4} mean={sum / seeds:F4} max={max:F4} band=[{ratio - 4 * sigma:F4},{ratio + 4 * sigma:F4}] sigma={sigma:F4}");
        Assert.InRange(sum / seeds, ratio - sigma, ratio + sigma); // 均值贴声明值
    }

    // ---- illegal：扫描器负向对照 —— 计数器必须真的在数（防恒零假绿） -------

    [Fact] // [F-025] 非法对照：满岩关（rock_ratio=1.0 合法极值）覆盖率必须≈1
    public void Rock_counter_is_not_a_constant_zero()
    {
        var world = new GridWorld(16, 12, new LcgRng(7), forestRatio: 0.0, rockRatio: 1.0, waterRatio: 0.0);

        Assert.True(RockFraction(world) > 0.99, $"full-rock map measured {RockFraction(world):F4}");
    }

    // ---- legal：传承刷新丰度 ×0.5 的语料物化（E2 已发行关全量） -------------

    [Fact] // [F-011][F-025] E2 三关 forest_ratio 恰=E1 教学关基准(0.10)×era2 legacy_refresh(wood)=0.5
    public void E2_level_forest_ratios_materialize_the_legacy_half_abundance()
    {
        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(ContentDir("era2"), "era.json")));
        double refresh = policy.AbundanceOf(new ResourceKey("era1:res-wood"));
        Assert.Equal(0.5, refresh, 9); // era2/era.json legacy_refresh 行（T-079 冻结）

        // E1 语料基准：教学关（slot 1）forest=0.10，四关带 [0.08, 0.15]
        double e1Teaching = LevelRows("levels", "e1.json")
            .Single(row => row.GetProperty("slot").GetInt32() == 1)
            .GetProperty("map").GetProperty("forest_ratio").GetDouble();
        Assert.Equal(0.10, e1Teaching, 9);

        // E2 三关全等 0.05=0.10×0.5（传承刷新档的语料物化）
        double expected = e1Teaching * refresh;
        foreach (JsonElement row in LevelRows("levels", "e2.json"))
        {
            double ratio = row.GetProperty("map").GetProperty("forest_ratio").GetDouble();
            Assert.Equal(expected, ratio, 9);
        }

        // 上代（E1）本家丰度严格高于刷新档：四关 forest 全 > 0.05
        Assert.All(LevelRows("levels", "e1.json"), row =>
            Assert.True(row.GetProperty("map").GetProperty("forest_ratio").GetDouble() > expected,
                "E1 home-era forest abundance must exceed the refreshed half"));
    }

    // ---- legal：跨代大陆（E2→E3 派生 seed）资源节点分布 ----------------------

    [Fact] // [F-011][F-025] E2→E3 大陆：节点数=声明值、全部避开让位区、岩格覆盖率在折减带内
    public void E2_to_E3_continent_places_the_declared_nodes_outside_the_yield_zone()
    {
        var mapping = LegacyDataLoader.ParseRelicMapping(
            File.ReadAllText(Path.Combine(ContentDir("legacy"), "relic_mapping.json")));
        const int worldSeeds = 12;
        double min = 1.0, max = 0.0, sum = 0.0;

        for (int i = 0; i < worldSeeds; i++)
        {
            // NewContinentFactory.Create 内部以 DeriveSeed(worldSeed, 3) 重播——E2→E3 大陆路径
            NewContinent continent = NewContinentFactory.Create(mapping, 20261300 + i, targetEraIndex: 3, new LcgRng(1));

            Assert.Equal(mapping.ResourceNodeCount, continent.ResourceNodes.Count);
            Assert.All(continent.ResourceNodes, cell =>
                Assert.True(
                    CoreZoneGeometry.Chebyshev(cell, continent.Anchor) > mapping.TerrainYieldRadiusCells,
                    $"node ({cell.X},{cell.Y}) inside the yield zone"));

            double fraction = RockFraction(continent.World);
            min = Math.Min(min, fraction);
            max = Math.Max(max, fraction);
            sum += fraction;
        }

        // 让位通道把核心 21×21 区内的岩格清为平原：期望≈0.10×(9216−441)/9216≈0.0952
        double expected = 0.10 * (96.0 * 96.0 - 441) / (96.0 * 96.0);
        _output.WriteLine(
            $"E2->E3 continent seeds={worldSeeds}: rock min={min:F4} mean={sum / worldSeeds:F4} max={max:F4} expected={expected:F4}");
        Assert.InRange(sum / worldSeeds, expected - 0.01, expected + 0.01);
        Assert.InRange(min, expected - 0.02, expected + 0.02);
        Assert.InRange(max, expected - 0.02, expected + 0.02);
    }

    // ---- boundary：era3 煤位守卫（T-116 落位 map 块后的翻转形态） -----------
    // 前向守卫（v1：era3 levels 无 map 块）已由 T-116 战役制作翻转——
    // levels/e3.json 落位 map 块后，煤（rock 位=矿位代理）分布字段的负向
    // 守卫=非零煤位仅出现于稀缺标记关（L3-2 矿轨关）；排期侧 era3/levels.json
    // 保持无 map（T-112 范围=排期侧先行冻结，map 载体归 levels/e3.json）。

    [Fact] // [F-025] boundary — 排期侧 era3/levels.json 保持排期纯度（map 块载体=levels/e3.json）
    public void Era3_schedule_rows_stay_map_free()
    {
        int rows = 0;
        foreach (JsonElement row in LevelRows("era3", "levels.json"))
        {
            rows++;
            Assert.False(row.TryGetProperty("map", out _), $"{row.GetProperty("id").GetString()}: 排期侧不落 map 块（载体=levels/e3.json）");
            Assert.False(row.TryGetProperty("initial_stock", out _), $"{row.GetProperty("id").GetString()}: 排期侧不落 initial_stock");
        }

        Assert.Equal(3, rows); // T-112 冻结的前三关排期行
    }

    [Fact] // [F-025] legal+negative — 煤位字段仅出现于稀缺标记关（T-116 翻转守卫）：
            // 非零 rock_ratio 只在 L3-2 矿脉铁轨（富煤矿），其余 E3 关显式声明 0（煤稀）
    public void Coal_deposit_rock_ratio_lands_only_on_the_flagged_e3_level()
    {
        const string flaggedId = "campaign:level-e3-02"; // 矿轨关=富煤矿（e3.json _source 锚）
        int rows = 0;
        foreach (JsonElement row in LevelRows("levels", "e3.json"))
        {
            rows++;
            string id = row.GetProperty("id").GetString()!;
            JsonElement map = row.GetProperty("map"); // 落位后每行必有 map 块
            Assert.True(map.TryGetProperty("rock_ratio", out JsonElement rock), $"{id}: rock_ratio 必须显式");

            if (id == flaggedId)
            {
                Assert.Equal(0.10, rock.GetDouble(), 9); // 富煤矿 map 参数（§四表 2 同值）
            }
            else
            {
                Assert.Equal(0.0, rock.GetDouble(), 9); // 煤稀：零声明=零矿格（表 2b 通道）
            }
        }

        Assert.Equal(5, rows); // T-116 首批 4 关+T-124 终局关 e3-final（rock=0 同守——终局关非富煤矿位）
    }

    [Fact] // [F-025] boundary — 已发行 e1/e2/e3 关全量显式声明分布字段（稀缺裁决的显式前提）
    public void Authored_levels_always_declare_their_distribution_explicitly()
    {
        foreach (string eraFile in new[] { "e1.json", "e2.json", "e3.json" })
        {
            foreach (JsonElement row in LevelRows("levels", eraFile))
            {
                JsonElement map = row.GetProperty("map");
                Assert.True(map.TryGetProperty("rock_ratio", out _), "rock_ratio 必须显式");
                Assert.True(map.TryGetProperty("forest_ratio", out _), "forest_ratio 必须显式");
                Assert.True(map.TryGetProperty("water_ratio", out _), "water_ratio 必须显式");
            }
        }
    }
}
