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

    public PipelineHarness(double initialWood = 200.0, int eraIndex = AgentEra)
    {
        _rootDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "eratd-t053-" + System.Guid.NewGuid().ToString("N"));
        SimulationConfig config = Config(initialWood);
        Simulation = new Simulation(config, new SimRng(Seed), Seed);
        JournalPath = System.IO.Path.Combine(_rootDir, "war-report.jsonl");
        Journal = new JsonlJournalWriter(JournalPath);
        LogSink = new CapturingLogSink();
        Pipeline = AgentPipeline.OverSimulation(
            Simulation,
            config,
            L1IntentParserTests.CreateParser(),
            eraIndex,
            Journal,
            LogSink,
            new Dictionary<string, CellCoord>
            {
                ["east_gate"] = EastGateAnchor,
                ["south_gate"] = SouthGateAnchor,
            });
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
