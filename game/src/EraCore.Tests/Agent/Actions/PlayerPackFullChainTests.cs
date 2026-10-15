using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Governance;
using EraCore.Agent.Grammar;
using EraCore.Agent.Pipeline;
using EraCore.Combat;
using EraCore.Contracts.Content;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;
using EraCore.Mods;
using EraCore.Sim;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.Agent.Actions.ModLinkTestPaths;

namespace EraCore.Tests.Agent.Actions;

// [F-095][F-156][F-157][F-165] 真实玩家级模组全链联测（T-104 验收）：
// samples/player-pack（新塔 2 座不同类+新敌 1 种+新卷轴 1 张+弹种 on_hit+波组
// world_ops）装载后——L1 军令「建两座 XX」（模组塔中文别名）→BuildPlan 草案
// （cells 按时代上限）→审批→落子→模组塔实际开火（伤害走模组弹种，on_hit
// 燃灼原语现行生效）→模组敌入波（WaveGroup roster 面驱动）→模组卷轴装载
// 且 L1 可触发。headless 全链断言+同 seed 双跑确定性+MODLINK_OK 冒烟标记。
// 真实仓库文件直读（样板模组库根），非内联夹具（CS-3.4）。

public class PlayerPackFullChainTests
{
    private const int AgentEra = 2; // BuildPlan/TriggerScroll 解锁档（E2+）
    private const long Seed = SimE1Fixture.Seed;
    private static readonly ResourceKey Wood = SimE1Fixture.Wood;

    /// <summary>One full chain run over the real sample mods: fresh composition, five-stage pipeline, deterministic sim run.</summary>
    private sealed class ChainRun : IDisposable
    {
        public const string ThornTowerId = "player-pack:tower-thorn-ballista";
        public const string CinderTowerId = "player-pack:tower-cinder-mortar";
        public const string ShadeEnemyId = "player-pack:enemy-shade";
        public const string QuickstepScrollId = "player-pack:scroll-quickstep";

        private readonly string _rootDir;
        public ContentRegistry Registry { get; }
        public ModParamSpace Space { get; }
        public Simulation Simulation { get; }
        public AgentPipeline Pipeline { get; }
        public CapturingLogSink LogSink { get; }

        public ChainRun()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "eratd-t104-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootDir);
            (ModLoadResult result, Registry) = Boot(SamplesDir());
            Assert.True(result.Ok, "三样板模组应零错误装载: " + string.Join("; ", result.Errors));

            // F-095 参数空间：塔清单=本体（patch 叠加生效）+模组塔；Schema 门接并集
            Space = ModParamSpace.Compose(SimEra1Content.LoadTowerSpecs(), Registry, "era1:");
            Assert.True(Space.Ok, "参数空间扩展应零错误: " + string.Join("; ", Space.Errors));

            // L1 解析器：塔/卷轴别名词典=扩展并集（模组源打通，T-090 管线）
            var parser = new L1IntentParser(
                GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile("patterns-zh.json"), "zh"),
                GrammarData.FromPatterns(GrammarTestPaths.ReadGrammarFile("patterns-en.json"), "en"),
                GrammarData.FromLexicon(GrammarTestPaths.ReadGrammarFile("lexicon.json")),
                Space.TowerAliases,
                AliasDictionary.FromTowerJson(GrammarTestPaths.Era1EnemiesJson()),
                Space.ScrollAliases);

            // 关卡：E1 夹具口径 + 模组塔清单/模组敌清单 + 波程 roster 并入夜巡组敌
            SimulationConfig baseline = SimE1Fixture.Config(initialStock: new List<KeyValuePair<ResourceKey, double>>
            {
                new(Wood, 500.0), // 4 座模组塔造价 2×50+2×55=210，留足余量
            });
            var waveRoster = baseline.Waves.RegularRoster
                .Concat(Space.WaveRosters.SelectMany(static r => r.Enemies))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            SimulationConfig config = baseline with
            {
                TowerCatalog = Space.Towers,
                Enemies = Space.Enemies,
                Waves = baseline.Waves with { RegularRoster = waveRoster },
            };

            Simulation = new Simulation(config, new SimRng(Seed), Seed);
            var view = new SimulationActionView(Simulation, config);
            var journal = new JsonlJournalWriter(Path.Combine(_rootDir, "war-report.jsonl"));
            LogSink = new CapturingLogSink();
            var sequencer = new SequencingJournalWriter(journal);
            var clock = new SimTickClock(() => view.Tick);
            var registry = new ActionRegistry(
                LogSink,
                view,
                validator: new JsonSchemaValidator(() => AgentEra, Space.BuildableTowerIds), // F-095 动态并集进 Schema 门
                eraOrdinalProvider: () => AgentEra,
                towerEstate: Simulation.Estate,
                armedScrolls: new HashSet<string> { QuickstepScrollId }); // 模组卷轴装备入列

            Pipeline = new AgentPipeline(
                parser,
                new IntentRouter(),
                new PipelinePlanner(
                    view,
                    new Dictionary<string, CellCoord>
                    {
                        ["east_gate"] = new(12, 12),
                        ["south_gate"] = new(4, 8),
                    },
                    config.BaseCell,
                    "era1:tower-wood-arrow",
                    () => AgentEra),
                new ApprovalGate(AgentEra),
                new SystemPointLedger(AgentEra, sequencer, clock),
                new PipelineExecutor(registry, Simulation, view),
                registry,
                new AgentReporter(),
                sequencer,
                view,
                () => view.Tick);
            Pipeline.Ledger.BeginWave(1, isBossWave: false);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_rootDir, recursive: true);
            }
            catch (IOException)
            {
                // temp cleanup best effort (CS-3.8) — never fails the test run
            }
        }
    }

    /// <summary>Runs the whole chain once and reports every observable fact the assertions and the smoke marker need.</summary>
    private sealed record ChainFacts(
        long FinalHash,
        int TowersPlaced,
        IReadOnlyList<string> PlacedTowerIds,
        int FiredTowers,
        bool ShadeSpawned,
        bool BurnActive,
        bool BuildOk,
        bool ScrollOk,
        bool AliasHit,
        int TowerDelta,
        int EnemyDelta,
        int ScrollDelta,
        string Marker,
        bool MarkerLogged,
        IReadOnlyList<long> SampledHashes);

    private static ChainFacts RunChain()
    {
        using var run = new ChainRun();

        // ---- ① L1 军令（模组塔中文别名）→ BuildPlan 草案 → 审批 → 落子
        OrderOutcome thornAwait = run.Pipeline.Handle("在东门建两座荆棘弩塔");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, thornAwait.Kind);
        OrderOutcome thornBuilt = run.Pipeline.Confirm(thornAwait.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, thornBuilt.Kind);
        var thornStep = thornBuilt.Trace.Steps.First(step => step.Verb == "BuildPlan");
        Assert.Equal("Ok", thornStep.Status);

        run.Pipeline.Ledger.BeginWave(2, isBossWave: false); // 波 1 预算已耗，换波补给
        OrderOutcome cinderAwait = run.Pipeline.Handle("在南门建两座焚石炮");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, cinderAwait.Kind);
        OrderOutcome cinderBuilt = run.Pipeline.Confirm(cinderAwait.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, cinderBuilt.Kind);
        var cinderStep = cinderBuilt.Trace.Steps.First(step => step.Verb == "BuildPlan");
        Assert.Equal("Ok", cinderStep.Status);

        List<SimTower> towers = run.Simulation.Towers.ToList();
        bool buildOk = towers.Count == 4
            && towers.Count(t => t.Spec.Id == ChainRun.ThornTowerId) == 2
            && towers.Count(t => t.Spec.Id == ChainRun.CinderTowerId) == 2;

        // BuildPlan 草案 cells 按时代上限（E2=4+2×2=8）内的 2 格请求——从执行步载荷断言
        using (JsonDocument plan = JsonDocument.Parse(thornStep.ActionJson))
        {
            Assert.Equal(2, plan.RootElement.GetProperty("cells").GetArrayLength());
            Assert.Equal(ChainRun.ThornTowerId, plan.RootElement.GetProperty("tower_id").GetString());
        }

        Assert.All(towers, tower => Assert.True(tower.Weapon.FiredCount == 0, "落子当拍未开火"));

        // ---- ② 模组卷轴：装载（装备集内）且 L1 军令可触发
        run.Pipeline.Ledger.BeginWave(3, isBossWave: false);
        OrderOutcome scrollAwait = run.Pipeline.Handle("开疾风卷轴");
        OrderOutcome scrollFired = run.Pipeline.Confirm(scrollAwait.PendingId);
        var scrollStep = scrollFired.Trace.Steps.FirstOrDefault(step => step.Verb == "TriggerScroll");
        bool scrollOk = scrollStep is not null && scrollStep.Status == "Ok"
            && run.Pipeline.State.ScrollActivations.Count == 1;

        // ---- ③ 跑波：模组敌入波（夜巡组 roster 驱动）→ 模组塔开火 → 燃灼 on_hit 生效
        bool shadeSeen = false;
        bool burnSeen = false;
        var sampledHashes = new List<long>();
        long tick = 0;
        while (tick < 4200)
        {
            run.Simulation.Step();
            tick = run.Simulation.CurrentTick;
            if (!shadeSeen && run.Simulation.Units.Any(u => u.Alive && u.ContentId == ChainRun.ShadeEnemyId))
            {
                shadeSeen = true;
            }

            if (!burnSeen && run.Simulation.Burn.ActiveCount > 0)
            {
                burnSeen = true;
            }

            if (tick % 600 == 0)
            {
                sampledHashes.Add(run.Simulation.ComputeStateHash());
            }
        }

        int firedTowers = run.Simulation.Towers.Count(t => t.Weapon.FiredCount > 0);
        JsonContentItem cinderProjectile = (JsonContentItem)run.Registry.Get(
            ContentType.Projectile,
            JsonDocument.Parse(((JsonContentItem)run.Registry.Get(ContentType.Building, ChainRun.CinderTowerId)).RawJson)
                .RootElement.GetProperty("ammo_item").GetString()!);
        double projectileDamage = JsonDocument.Parse(cinderProjectile.RawJson).RootElement.GetProperty("damage_per_shot").GetDouble();
        SimTower placedCinder = run.Simulation.Towers.First(t => t.Spec.Id == ChainRun.CinderTowerId);
        Assert.Equal(projectileDamage, placedCinder.Spec.DamagePerShot); // 伤害走模组弹种（装载门+落子实况双验）
        bool fireWalksProjectile = firedTowers == 4 && burnSeen && placedCinder.Spec.DamagePerShot == projectileDamage;

        // ---- ④ MODLINK_OK 冒烟标记（全部由真实事实拼出，非字面常量）
        int towerDelta = run.Registry.List(ContentType.Building).Count(i => ((JsonContentItem)i).SourceModId == "player-pack");
        int enemyDelta = run.Registry.List(ContentType.Enemy).Count(i => ((JsonContentItem)i).SourceModId == "player-pack");
        int scrollDelta = run.Registry.List(ContentType.Scroll).Count(i => ((JsonContentItem)i).SourceModId == "player-pack");
        bool aliasHit = run.Space.TowerAliases.TryResolve(TextNormalizer.Normalize("荆棘弩塔"), out string thornAlias)
            && thornAlias == ChainRun.ThornTowerId;
        string marker = $"MODLINK_OK towers=+{towerDelta} enemies=+{enemyDelta} scrolls=+{scrollDelta}"
            + $" alias_hit={(aliasHit ? "True" : "False")} build={(buildOk ? "ok" : "failed")} fire={(fireWalksProjectile ? "on_hit" : "off")}";
        run.LogSink.Write(LogLevel.Info, run.Simulation.CurrentTick, marker);
        bool markerLogged = run.LogSink.Entries.Any(entry => entry.Message == marker);

        return new ChainFacts(
            run.Simulation.ComputeStateHash(),
            towers.Count,
            towers.Select(t => t.Spec.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            firedTowers,
            shadeSeen,
            burnSeen,
            buildOk,
            scrollOk,
            aliasHit,
            towerDelta,
            enemyDelta,
            scrollDelta,
            marker,
            markerLogged,
            sampledHashes);
    }

    [Fact] // [F-095] 全链：模组装载→L1 别名军令→BuildPlan→审批→落子→开火（on_hit）→入波→开卷，一次跑通
    public void PlayerPack_RunsTheWholeChain_Headless()
    {
        ChainFacts facts = RunChain();

        Assert.Equal(4, facts.TowersPlaced);
        Assert.True(facts.BuildOk, "两座荆棘弩塔+两座焚石炮应全部落子");
        Assert.True(facts.AliasHit, "模组塔中文别名应可指挥");
        Assert.Equal(4, facts.FiredTowers);
        Assert.True(facts.ShadeSpawned, "夜巡组的影掠兽应已入波");
        Assert.True(facts.BurnActive, "焚石炮 on_hit 燃灼原语应现行生效（sim.Burn 活跃）");
        Assert.True(facts.ScrollOk, "模组卷轴应装载且 L1 可触发");
        Assert.Equal("MODLINK_OK towers=+2 enemies=+1 scrolls=+1 alias_hit=True build=ok fire=on_hit", facts.Marker);
    }

    [Fact] // [F-095] 冒烟标记：MODLINK_OK 行由真实事实拼装并写入日志汇（可观测）
    public void PlayerPack_Marker_IsComposedFromFacts_AndLogged()
    {
        ChainFacts facts = RunChain();

        Assert.Equal(2, facts.TowerDelta);
        Assert.Equal(1, facts.EnemyDelta);
        Assert.Equal(1, facts.ScrollDelta);
        Assert.True(facts.MarkerLogged, "标记行应已写入日志汇");
    }

    [Fact] // [F-095] 确定性：同 seed 双跑——终态哈希、采样哈希序列、落子清单、开火塔数全等
    public void PlayerPack_ChainIsDeterministic_SameSeedTwice()
    {
        ChainFacts first = RunChain();
        ChainFacts second = RunChain();

        Assert.Equal(first.FinalHash, second.FinalHash);
        Assert.Equal(first.SampledHashes, second.SampledHashes);
        Assert.Equal(first.PlacedTowerIds, second.PlacedTowerIds);
        Assert.Equal(first.FiredTowers, second.FiredTowers);
        Assert.Equal(first.ShadeSpawned, second.ShadeSpawned);
        Assert.Equal(first.BurnActive, second.BurnActive);
        Assert.Equal(first.Marker, second.Marker);
    }

    [Fact] // [F-095] 卸载模组后 BuildPlan 回封闭集：无模组引导的视图中模组塔 id 无造价（handler 拒）
    public void AfterUninstall_BuildPlanFallsBackToTheClosedBuiltinSet()
    {
        using var withMods = new ChainRun();
        Assert.True(withMods.Space.ContainsTower(ChainRun.ThornTowerId)); // 带模组时确在并集内

        (_, ContentRegistry cleanRegistry) = Boot(modsRoot: null);
        ModParamSpace clean = ModParamSpace.Compose(SimEra1Content.LoadTowerSpecs(), cleanRegistry, "era1:");
        Assert.True(clean.Ok);
        Assert.False(clean.ContainsTower(ChainRun.ThornTowerId)); // 卸载即回封闭集
        Assert.False(clean.ContainsTower("more-arrows:tower-barbed"));

        // 同一关卡（本体封闭清单）下，模组塔 id 在 handler 层也无造价（ErrTargetInvalid 通道）
        SimulationConfig config = SimE1Fixture.Config();
        var sim = new Simulation(config, new SimRng(Seed), Seed);
        var view = new SimulationActionView(sim, config);
        Assert.False(view.TryGetTowerCost(ChainRun.ThornTowerId, out _));
        Assert.True(view.TryGetTowerCost("era1:tower-wood-arrow", out _));
    }
}
