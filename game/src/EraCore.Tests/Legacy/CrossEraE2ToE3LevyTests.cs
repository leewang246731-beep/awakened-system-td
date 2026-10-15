using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Legacy;
using EraCore.Sim;
using EraCore.Tests.Persistence;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Legacy.LegacyFixture;

namespace EraCore.Tests.Legacy;

// [C-20][F-011] E2→E3 跨代资源审计（T-115）：用真 T-074 事务 +
// CrossEraResourcePolicy 派生的新时代有用集跑 2→3 跳代——逐资源守恒
// （旧存量=带走+留存）、无用途旧资源 3:1 折文明基金（木材/石料/粮/
// 马匹/中间锭/旧弹药）、弹链本代化裁定（E3 塔弹全=本代配方输出，
// era2:ammo-* 无 E3 消费者→自动退役入折算）。
//
// 内容缺口已闭合（T-117）：era_transition.json 现携带 final_levels["2"]
// 行——T-115 的 negative 守卫（未补丁真表 2→3 硬拒）已翻转为内容回归
// （真表直接放行，见 illegal 节翻转行）；其余字段原值复用。
public class CrossEraE2ToE3LevyTests : SqliteSavePortTestBase
{
    private static ResourceKey Res(string id) => new(id);

    /// <summary>An E2-era campaign pool: all six audit rows + ingots + old ammo.</summary>
    private static ResourceStock E2Pool(int wood, int stone, int grain, int horse, int copper, int iron)
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 2);
        _ = pool.Deposit(Res("era1:res-wood"), wood);
        _ = pool.Deposit(Res("era1:res-stone"), stone);
        _ = pool.Deposit(Res("era2:res-grain"), grain);
        _ = pool.Deposit(Res("era2:res-horse"), horse);
        _ = pool.Deposit(Res("era2:res-copper"), copper);
        _ = pool.Deposit(Res("era2:res-iron"), iron);
        _ = pool.Deposit(Res("era2:res-copper-ingot"), 210);
        _ = pool.Deposit(Res("era2:res-iron-ingot"), 90);
        _ = pool.Deposit(Res("era2:ammo-arrow"), 120);
        _ = pool.Deposit(Res("era2:ammo-iron-bolt"), 60);
        return pool;
    }

    private static EraTransitionResult RunTransition(ResourceStock pool, EraTransitionSpec spec, CountingSavePort port)
    {
        // 真内容表（T-117 起含 "2" 终局关行）：树侧以声明 id 注册占领。
        string finalId = spec.FinalLevels.TryGetValue(2, out string? declared) ? declared : "test:level-e2-final-undeclared";
        var tech = Tree(2, finalId);
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 2).Id); // 大图书馆
        var city = CityFacts(11, 30, 9, Mapping().Orientations);

        var policy = CrossEraResourcePolicy.Parse(
            File.ReadAllText(Path.Combine(EraDir("era3"), "era.json")));
        var request = new EraTransitionRequest(
            Seed,
            FromEraIndex: 2,
            pool,
            tech,
            ledger,
            city,
            Anchor,
            policy.NewEraResources,
            new SimClock(),
            new LcgRng(Seed + 3),
            port,
            EmptyMeta(Seed, 2),
            Mapping(),
            spec,
            Wonders());
        return new EraTransitionService().Execute(request);
    }

    // ---- legal --------------------------------------------------------------

    [Fact] // [F-011] legal — 折算守恒：旧存量=带走+留存；带走=⌊30%×存量⌋；
            // 无用途行折算=⌊带走/3⌋；铜/铁带走入新池、其余八种全部不入新池
    public void E2_to_E3_levy_conserves_every_resource_line()
    {
        var pool = E2Pool(wood: 600, stone: 300, grain: 900, horse: 150, copper: 500, iron: 800);
        double copperBefore = pool.AmountOf(Res("era2:res-copper"));
        double ironBefore = pool.AmountOf(Res("era2:res-iron"));
        var counting = new CountingSavePort(CreatePort());

        EraTransitionResult result = RunTransition(pool, Transition(), counting);

        // 逐资源守恒：旧存量 = 带走 + 留存（十行全覆盖）
        Assert.Equal(10, result.Levy.Count);
        Assert.All(result.Levy, line => Assert.Equal(line.OldAmount, line.Carried + line.Retained, 9));

        // 带走侧 = ⌊30% × 存量⌋（carried_share=0.3 来自真 era_transition.json）
        Assert.All(result.Levy, line =>
            Assert.Equal(Math.Floor(line.OldAmount * 0.3), (double)line.Carried, 9));

        // 有用集（era3/era.json 派生）恰=铜/铁：携带不折算；其余八行折算=⌊带走/3⌋。
        // 逐行期望（存量→带走→折算）：wood 600→180→60、stone 300→90→30、grain 900→270→90、
        // horse 150→45→15、copper-ingot 210→63→21、iron-ingot 90→27→9、
        // ammo-arrow 120→36→12、ammo-iron-bolt 60→18→6；Σ折算=243。
        var expectedFold = new Dictionary<string, long>
        {
            ["era1:res-wood"] = 60,
            ["era1:res-stone"] = 30,
            ["era2:res-grain"] = 90,
            ["era2:res-horse"] = 15,
            ["era2:res-copper-ingot"] = 21,
            ["era2:res-iron-ingot"] = 9,
            ["era2:ammo-arrow"] = 12,
            ["era2:ammo-iron-bolt"] = 6,
        };
        Assert.Equal(243, result.FundGained);
        foreach ((string id, long folded) in expectedFold)
        {
            EraLevyLine line = result.Levy.Single(l => l.Resource.Value == id);
            Assert.Equal(folded, line.FundFolded);
        }

        foreach (string useful in new[] { "era2:res-copper", "era2:res-iron" })
        {
            EraLevyLine line = result.Levy.Single(l => l.Resource.Value == useful);
            Assert.Equal(0, line.FundFolded);
            Assert.True(line.Carried > 0);
        }

        // 新池=带走侧：铜/铁按 ⌊30%×存量⌋ 到位；八种旧资源/中间锭/旧弹药全不在新池
        Assert.Equal(Math.Floor(copperBefore * 0.3), result.NewPool.AmountOf(Res("era2:res-copper")), 9);
        Assert.Equal(Math.Floor(ironBefore * 0.3), result.NewPool.AmountOf(Res("era2:res-iron")), 9);
        Assert.Equal(2, result.NewPool.Resources.Count);
        Assert.DoesNotContain(result.NewPool.Resources, r => r.Value.StartsWith("era1:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.NewPool.Resources, r => r.Value.StartsWith("era2:ammo-", StringComparison.Ordinal));
        Assert.DoesNotContain(result.NewPool.Resources, r => r.Value.EndsWith("-ingot", StringComparison.Ordinal));

        // 仓库等级 2+1=3（era3/era.json warehouse_level=3；跃迁表 level_gain=1）
        Assert.Equal(3, result.NewPool.WarehouseLevel);
        Assert.Equal(1, counting.CampaignWrites); // campaign 域恰一次写
        Assert.Equal(2, result.FromEraIndex);
        Assert.Equal(3, result.ToEraIndex);
    }

    [Fact] // [F-011] legal — 弹链本代化（E3 弹=钢+铜/钢+煤）：本代塔弹闭包于本代配方输出，
            // 旧弹药（era2:ammo-*）零 E3 消费者→事务内自动退役（3:1 折算，上一测试断言其 FundFolded>0）
    public void E3_ammo_chain_is_fully_native_and_old_ammo_retires_into_the_fold()
    {
        string era3Dir = EraDir("era3");
        using JsonDocument recipes = JsonDocument.Parse(File.ReadAllText(Path.Combine(era3Dir, "recipes.json")));
        using JsonDocument towers = JsonDocument.Parse(File.ReadAllText(Path.Combine(era3Dir, "towers.json")));

        var produced = recipes.RootElement.GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("output").GetProperty("resource").GetString()!)
            .ToHashSet();
        Assert.Equal(new HashSet<string> { "era3:res-steel", "era3:ammo-bullet", "era3:ammo-shell" }, produced);

        var towerAmmo = towers.RootElement.GetProperty("items").EnumerateArray()
            .Select(t => t.TryGetProperty("ammo_item", out var ammo) && ammo.ValueKind == JsonValueKind.String
                ? ammo.GetString()!
                : null)
            .ToList();

        // 每个非空塔弹都在本代配方输出域内（铁甲墙纯阻挡 ammo_item=null 豁免）
        Assert.Equal(3, towerAmmo.Count);
        Assert.All(towerAmmo.Where(a => a is not null), ammo => Assert.Contains(ammo!, produced));
        Assert.Contains(towerAmmo, a => a is null);

        // E3 弹链输入含传承刷新资源：bullet=钢+铜（era2:res-copper）、steel=煤+铁（era2:res-iron）
        var bulletInputs = recipes.RootElement.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("output").GetProperty("resource").GetString() == "era3:ammo-bullet")
            .GetProperty("inputs").EnumerateArray()
            .Select(i => i.GetProperty("resource").GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "era3:res-steel", "era2:res-copper" }, bulletInputs);

        // 旧弹药零 E3 引用：era3 全部内容文件的字符串值域无 "era2:ammo-" 前缀
        foreach (string file in new[] { "era.json", "towers.json", "recipes.json", "enemies.json", "levels.json" })
        {
            string text = File.ReadAllText(Path.Combine(era3Dir, file));
            Assert.DoesNotContain("era2:ammo-", text);
        }
    }

    // ---- boundary ------------------------------------------------------------

    [Fact] // [F-011] boundary — 小额池：六资源各存量 2 → 带走 ⌊0.6⌋=0、留存全额、基金 0
    public void Small_E2_pool_floors_to_zero_carried_and_zero_fund()
    {
        var pool = new ResourceStock(new SimClock(), warehouseLevel: 2);
        foreach (string id in new[]
                 {
                     "era1:res-wood", "era1:res-stone", "era2:res-grain",
                     "era2:res-horse", "era2:res-copper", "era2:res-iron",
                 })
        {
            _ = pool.Deposit(Res(id), 2);
        }

        var counting = new CountingSavePort(CreatePort());

        EraTransitionResult result = RunTransition(pool, Transition(), counting);

        Assert.Equal(6, result.Levy.Count);
        Assert.All(result.Levy, line =>
        {
            Assert.Equal(0, line.Carried);
            Assert.Equal(0, line.FundFolded);
            Assert.Equal(line.OldAmount, line.Retained, 9);
        });
        Assert.Equal(0, result.FundGained);
        Assert.Empty(result.NewPool.Resources);
        Assert.Equal(1, counting.CampaignWrites); // 零带走仍完整成事务
    }

    // ---- G3 守卫翻转 + illegal ------------------------------------------------

    [Fact] // [F-011] legal（G3 守卫翻转，T-117）——真内容表现在携带 "2" 终局关行：
            // 未打补丁的 era_transition.json 直接放行 2→3 跳代（T-115 期此行硬拒
            // "no final level for era 2"——行 id 与 E1 行同款约定 campaign:level-e2-final）
    public void Real_transition_table_now_carries_the_E2_final_level_row()
    {
        var pool = E2Pool(wood: 60, stone: 30, grain: 90, horse: 15, copper: 50, iron: 80);
        var counting = new CountingSavePort(CreatePort());

        EraTransitionResult result = RunTransition(pool, Transition(), counting);

        Assert.Equal(2, result.FromEraIndex);
        Assert.Equal(3, result.ToEraIndex);
        Assert.Equal(1, counting.CampaignWrites);
        Assert.Equal("campaign:level-e2-final", Transition().FinalLevels[2]);
        // E2→E3 排期=e3-01..03（target_era=3 行，非 E1→E2 的 e2 行）
        Assert.Equal(
            new[] { "campaign:level-e3-01", "campaign:level-e3-02", "campaign:level-e3-03" },
            result.Schedule.Select(slot => slot.LevelId).ToArray());
    }

    [Fact] // [F-011] illegal — 终局关未占领仍硬拒（E2→E3 行落地后的持续守卫：
            // 有行但树未占领 → Precheck 拒绝且零写入）
    public void E2_to_E3_jump_still_refuses_when_the_final_level_is_not_captured()
    {
        var pool = E2Pool(wood: 60, stone: 30, grain: 90, horse: 15, copper: 50, iron: 80);
        var tech = Tree(2, "test:some-other-level"); // 未占领声明终局关
        var ledger = new LegacyLedger(Archaeology(), Shop());
        ledger.MarkWonderBuilt(Wonders().Single(w => w.EraIndex == 2).Id);
        var request = new EraTransitionRequest(
            Seed, FromEraIndex: 2, pool, tech, ledger,
            CityFacts(11, 30, 9, Mapping().Orientations), Anchor,
            CrossEraResourcePolicy.Parse(File.ReadAllText(Path.Combine(EraDir("era3"), "era.json"))).NewEraResources,
            new SimClock(), new LcgRng(Seed + 3), new CountingSavePort(CreatePort()),
            EmptyMeta(Seed, 2), Mapping(), Transition(), Wonders());

        EraTransitionRejectedException ex = Assert.Throws<EraTransitionRejectedException>(
            () => new EraTransitionService().Execute(request));
        Assert.Contains("campaign:level-e2-final", ex.Message, StringComparison.Ordinal);
    }
}
