using System.Text.Json;
using EraCore.Endless;
using EraCore.Legacy;
using EraCore.Persistence;
using EraCore.Tests.Levels;
using EraCore.Tests.Legacy;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143] 链路层长局回放（四特性合流）：era1~era4 真内容装载，E4 开局
// （任选时代）→ 基准段敌池全 E4（敌池基准）→ 41 起跨时代混编（混编敌人）→
// 每 10 波滚词缀（滚词缀）+81 起冻结叠层垫底 → 奇观带入（真账本只读联动）→
// 中途换档（三榜分记口径不变）→ 120 波死亡终局结算（滚词缀加权分对账+三榜
// 落账）→ 同 seed 整局重放哈希全等（确定性）。
public class EndlessDeepeningChainTests
{
    private const long Seed = 20261434;
    private const int Waves = 120;

    /// <summary>各代 L1 波形（game/content/levels/eN.json 首关 waves 行）.</summary>
    private static (int Base, int Begin, int Spacing, double Scaling) Shape(int era) => era switch
    {
        1 => (14, 3, 3, 1.6),
        2 => (16, 3, 4, 1.6),
        3 => (16, 3, 4, 1.6),
        _ => (16, 3, 4, 1.6), // e4.json 首关同形
    };

    /// <summary>各代真敌表（game/content/era{N}/enemies.json 经生产解析器）.</summary>
    private static IReadOnlyList<EnemySpec> Rows(int era) =>
        HandcraftedLevelContent.Enemies($"era{era}").Values.OrderBy(spec => spec.Id, StringComparer.Ordinal).ToList();

    /// <summary>各代真塔面（era.json towers 行——解锁面基准）.</summary>
    private static IReadOnlyList<string> Towers(int era)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", $"era{era}");
            if (Directory.Exists(probe))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(probe, "era.json")));
                return doc.RootElement.GetProperty("items").EnumerateArray().Single()
                    .GetProperty("towers").EnumerateArray()
                    .Select(row => row.GetString()!).ToList();
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/era{era}（测试须在仓库内运行）");
    }

    /// <summary>四时代真内容集（era1~era4，T-143 今天的动态时代面）.</summary>
    private static IReadOnlyList<EndlessEraContent> FourEras() => new[]
    {
        new EndlessEraContent(1, Rows(1), Shape(1).Base, Shape(1).Begin, Shape(1).Spacing, Shape(1).Scaling,
            Towers: Towers(1)),
        new EndlessEraContent(2, Rows(2), Shape(2).Base, Shape(2).Begin, Shape(2).Spacing, Shape(2).Scaling,
            Towers: Towers(2)),
        new EndlessEraContent(3, Rows(3), Shape(3).Base, Shape(3).Begin, Shape(3).Spacing, Shape(3).Scaling,
            Towers: Towers(3)),
        new EndlessEraContent(4, Rows(4), Shape(4).Base, Shape(4).Begin, Shape(4).Spacing, Shape(4).Scaling,
            InitialResources: new[] { new EndlessResourceStock("era4:res-concrete", 2000.0) }, // e4.json 首关 initial_stock
            Towers: Towers(4)),
    };

    private static EndlessEntryRequest Request() => new(
        Seed,
        StartEraIndex: 4, // 任选时代：E4 开局
        EndlessParticipationMode.Assisted,
        ThreatLevel: 0.9,
        EraContent: FourEras(),
        WonderIds: new[] { "campaign:wonder-pyramid", "campaign:wonder-internet" },
        AffixRolls: EndlessAffixRollPolicy.Default); // 每 10 波滚词缀（生产档）；混编走入口默认=T-143 生产档

    private static LegacyLedger BuiltLedger()
    {
        var ledger = new LegacyLedger(LegacyFixture.Archaeology(), LegacyFixture.Shop());
        ledger.MarkWonderBuilt("campaign:wonder-pyramid");
        ledger.MarkWonderBuilt("campaign:wonder-internet");
        return ledger;
    }

    [Fact] // legal：链路全流——四特性合流 120 波长局回放+结算+确定性重放
    public void Era4_deep_run_replays_and_settles_the_full_t143_surface()
    {
        var ledger = BuiltLedger();
        long ledgerHashBefore = ledger.ComputeStateHash();
        var port = new InMemorySavePort();
        var request = Request();
        request = request with { BuiltWonderIds = ledger.BuiltWonderIds }; // 奇观带入只读联动

        EndlessSession session = EndlessEntry.Begin(request, new LcgRng(Seed), port, scoreSink: null);

        // ---- 任选时代：E4 开局基准冻结（波形+初始资源+解锁面）
        Assert.Equal(new[] { 1, 2, 3, 4 }, EndlessEntry.AvailableStartEras(request.EraContent)); // 动态枚举
        Assert.Equal(4, session.Run.Config.StartEraIndex);
        Assert.Equal(Shape(4).Base, session.Run.Config.WaveBaseCount);
        Assert.Equal(new[] { "campaign:wonder-pyramid", "campaign:wonder-internet" }, session.Run.Config.WonderIds);
        EndlessStartEraBaseline baseline = session.Run.Config.StartEraBaseline!;
        Assert.Equal(4, baseline.EraIndex);
        Assert.Equal(new[] { new EndlessResourceStock("era4:res-concrete", 2000.0) }, baseline.InitialResources);
        Assert.Equal(Towers(4), baseline.UnlockedTowers);

        // ---- 长局回放：三段断言（基准段/混编段/滚词缀段）
        var replay = EndlessEntry.Begin(request, new LcgRng(Seed)); // 同 seed 重放局
        var blockKinds = new HashSet<string>();
        bool sawMixing = false, sawStacks = false;
        for (int wave = 1; wave <= Waves; wave++)
        {
            EndlessWavePlan plan = session.Run.BeginWave();
            EndlessWavePlan replayPlan = replay.Run.BeginWave();
            Assert.Equal(plan.Roster, replayPlan.Roster); // 名册逐 spawn 一致

            if (wave < 41)
            {
                Assert.All(plan.Roster, spawn => Assert.Equal(4, spawn.EraIndex)); // 基准段=开局时代独占
            }
            else if (plan.Roster.Any(spawn => spawn.EraIndex != 4))
            {
                sawMixing = true; // 混编段带入其他时代
            }

            if (wave >= 41)
            {
                Assert.NotEmpty(plan.Modifiers.ActiveKinds);                 // 滚词缀段每波有词缀
                blockKinds.Add(string.Join(",", plan.Modifiers.ActiveKinds)); // 块界滚换收集
                if (plan.Modifiers.Stacks >= 1)
                {
                    sawStacks = true; // 81+ 冻结叠层垫底
                }
            }
            else
            {
                Assert.Empty(plan.Modifiers.ActiveKinds); // 起滚前无词缀
            }

            // 三榜分记口径不变：换档只改当前档，榜别恒为开局档（重放局镜像同一动作流）
            if (wave == 50)
            {
                Assert.True(session.Run.SwitchParticipation(EndlessParticipationMode.Manual, "takeover:test"));
                Assert.True(replay.Run.SwitchParticipation(EndlessParticipationMode.Manual, "takeover:test"));
            }

            bool byAutopilot = wave % 3 == 0;
            session.Run.RecordAction(byAutopilot);
            replay.Run.RecordAction(byAutopilot);
            session.Run.CompleteWave(plan.EliteCount, plan.BossCount);
            replay.Run.CompleteWave(replayPlan.EliteCount, replayPlan.BossCount);

            if (wave is 10 or 41 or 81 or 120)
            {
                Assert.Equal(session.Run.ComputeStateHash(), replay.Run.ComputeStateHash()); // 采样点哈希全等
            }
        }

        Assert.True(sawMixing, "41 起必须混编跨时代");
        Assert.True(sawStacks, "81 起冻结叠层必须垫底");
        Assert.True(blockKinds.Count >= 3, $"120 波 8 块滚词缀至少滚出 3 种词缀组合（实际 {blockKinds.Count}）");

        // ---- 死亡终局结算：滚词缀加权对账+三榜分记+落账
        EndlessSettlement settlement = session.Die();
        long expectedAffixBonus = EndlessRules.AffixBonusPoints(
            Waves, Seed, EndlessScoreWeights.Default, EndlessAffixRollPolicy.Default);
        Assert.Equal(expectedAffixBonus, settlement.AffixBonusPoints);
        long expectedScore = (Waves * 100L) + (settlement.EliteKills * 20L) + (settlement.BossKills * 200L)
            + expectedAffixBonus; // SP-7c + 滚词缀加权（EliteCount 逐波全灭→击杀=计划数）
        Assert.Equal(expectedScore, settlement.Score);
        Assert.Equal(EndlessBoardKind.Assisted, settlement.Board); // 开局档锁榜（中途换档不换榜）
        Assert.Equal(EndlessParticipationMode.Manual, settlement.CurrentParticipation);
        Assert.Equal(1, settlement.SwitchCount);
        Assert.True(settlement.Persisted);
        EndlessBoards boards = EndlessBoardStore.Load(port);
        Assert.Single(boards.Of(EndlessBoardKind.Assisted)); // 三榜互不混排
        Assert.Empty(boards.Of(EndlessBoardKind.Manual));
        Assert.Empty(boards.Of(EndlessBoardKind.Autopilot));

        // ---- 只读消费：整局+落账后跃迁账本不动
        Assert.Equal(ledgerHashBefore, ledger.ComputeStateHash());
        Assert.Equal(2, ledger.BuiltWonderIds.Count); // 恰带入的两奇观已建（未被改写）
        Assert.Equal(new[] { "campaign:wonder-pyramid", "campaign:wonder-internet" }, ledger.BuiltWonderIds);
    }

    [Fact] // legal：同 seed 整局重放终局一致（第二终局：结算值与哈希对账）
    public void Same_seed_full_replay_settles_identically()
    {
        var request = Request();
        EndlessSession first = EndlessEntry.Begin(request, new LcgRng(Seed));
        EndlessSession second = EndlessEntry.Begin(request, new LcgRng(Seed));

        long firstHash = 0, secondHash = 0;
        for (int wave = 1; wave <= 90; wave++)
        {
            EndlessWavePlan a = first.Run.BeginWave();
            EndlessWavePlan b = second.Run.BeginWave();
            first.Run.CompleteWave(a.EliteCount, a.BossCount);
            second.Run.CompleteWave(b.EliteCount, b.BossCount);
            firstHash = first.Run.ComputeStateHash();
            secondHash = second.Run.ComputeStateHash();
        }

        Assert.Equal(firstHash, secondHash);
        EndlessSettlement firstSettle = first.Die();
        EndlessSettlement secondSettle = second.Die();
        Assert.Equal(firstSettle.Score, secondSettle.Score);
        Assert.Equal(firstSettle.AffixBonusPoints, secondSettle.AffixBonusPoints);
        Assert.Equal(firstSettle.Board, secondSettle.Board);
    }
}
