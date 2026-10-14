using EraCore.Agent.Pipeline;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Sim;

namespace EraCore.Tests.Agent.Pipeline;

// T-053 (C-13) 测试基建：真实 E1 关卡夹具（复用 Sim 模块的 SimE1Fixture 与
// Actions 的捕获 Fake，CS-3.4 禁 mock 被测系统）+ era2 智能体层接线 + K-8
// JSONL 临时落盘（CS-3.8：系统临时目录，Dispose 清理）。纯辅助类，不含测试
// 方法（T3 不适用）。

/// <summary>
/// One disposable pipeline-over-simulation harness: fresh seeded simulation,
/// L1 parser from repo content, era-2 agent stack (points enabled, ceiling
/// 5), JSONL journal under a unique temp directory. The ledger's wave 1 is
/// begun in the constructor (refill to ceiling — the host-side wave hook).
/// </summary>
internal sealed class PipelineHarness : IDisposable
{
    public const int AgentEra = 2;
    public const long Seed = SimE1Fixture.Seed;
    public static readonly ResourceKey Wood = SimE1Fixture.Wood;

    /// <summary>east_gate anchor — the build ring scan starts here (cells verified clear at authoring time).</summary>
    public static readonly CellCoord EastGateAnchor = new(12, 12);

    /// <summary>south_gate anchor.</summary>
    public static readonly CellCoord SouthGateAnchor = new(4, 8);

    private readonly string _rootDir;

    public PipelineHarness(
        double initialWood = 200.0,
        int eraIndex = AgentEra,
        SimulationConfig? config = null,
        IReadOnlySet<string>? armedScrolls = null,
        EraCore.Agent.Grammar.L1IntentParser? parser = null)
    {
        _rootDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "eratd-t053-" + System.Guid.NewGuid().ToString("N"));
        SimulationConfig configUsed = config ?? Config(initialWood);
        Simulation = new Simulation(configUsed, new SimRng(Seed), Seed);
        JournalPath = System.IO.Path.Combine(_rootDir, "war-report.jsonl");
        Journal = new JsonlJournalWriter(JournalPath);
        LogSink = new CapturingLogSink();
        Pipeline = AgentPipeline.OverSimulation(
            Simulation,
            configUsed,
            parser ?? L1IntentParserTests.CreateParser(),
            eraIndex,
            Journal,
            LogSink,
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = EastGateAnchor,
                ["south_gate"] = SouthGateAnchor,
            },
            armedScrolls: armedScrolls);
        Pipeline.Ledger.BeginWave(1, isBossWave: false);
    }

    public Simulation Simulation { get; }

    public AgentPipeline Pipeline { get; }

    public JsonlJournalWriter Journal { get; }

    public string JournalPath { get; }

    public CapturingLogSink LogSink { get; }

    /// <summary>The E1 fixture config with an explicit initial wood grant.</summary>
    public static SimulationConfig Config(double initialWood = 200.0) =>
        SimE1Fixture.Config(initialStock: new List<KeyValuePair<ResourceKey, double>> { new(Wood, initialWood) });

    /// <summary>
    /// T-068 era-2 harness config（升级/出售/英雄走 E2 塔链）——E2 内容塔 +
    /// 英雄 + 3000 木材（复用 SimE2Fixture 口径，初始授予改由参数决定）。
    /// </summary>
    public static SimulationConfig Era2Config(double initialTimber = 3000.0) =>
        SimE2Fixture.Config() with
        {
            InitialStock = new List<KeyValuePair<ResourceKey, double>>
            {
                new(new ResourceKey("era2:res-timber"), initialTimber),
            },
        };

    /// <summary>Era-2 grammar parser（era2 塔/敌别名——箭楼/弩塔/攻城锤等）。</summary>
    public static EraCore.Agent.Grammar.L1IntentParser Era2Parser() =>
        EraCore.Agent.Grammar.L1IntentParser.FromJson(
            EraCore.Tests.Agent.Grammar.GrammarTestPaths.ReadGrammarFile("patterns-zh.json"),
            EraCore.Tests.Agent.Grammar.GrammarTestPaths.ReadGrammarFile("patterns-en.json"),
            EraCore.Tests.Agent.Grammar.GrammarTestPaths.ReadGrammarFile("lexicon.json"),
            EraCore.Tests.Agent.Grammar.GrammarTestPaths.Era2TowersJson(),
            EraCore.Tests.Agent.Grammar.GrammarTestPaths.Era2EnemiesJson());

    public void Dispose()
    {
        Journal.Dispose();
        try
        {
            System.IO.Directory.Delete(_rootDir, recursive: true);
        }
        catch (System.IO.IOException)
        {
            // temp cleanup best effort (CS-3.8) — never fails the test run
        }
    }
}
