using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Tests.Regression;

// T-083 (F-172) 回归金样信封（game/content/golden/regression-v1.json）：头留档
// seed/波数/采样节奏/回退路径注/再生成命令（CS-6.1/CS-6.3——同 sim-e1/e2
// 金样先例），params 与 wave-e2-golden.json 冻结头互锁（单一事实源），windows
// =每 50 波统计摘要（敌量/击杀/资源曲线采样点）+窗口哈希，summary=首版基线
// 数字+摘要哈希。守卫告警（WARN 级）不入金样——越带不阻断（08 任务卡 T-083
// 验收口径），留档走测试输出与 CI 日志。序列化走 source-gen 上下文（AOT
// 红线同产品侧惯例）。

/// <summary>Envelope of the regression golden file (T-083 first creation).</summary>
public sealed record RegressionGoldenFile(
    string Schema,
    string Task,
    long Seed,
    int Waves,
    int SampleEvery,
    int WaveCycle,
    string PathNote,
    string RegenCommand,
    string CheckCommand,
    string Notes,
    RegressionWaveParams Params,
    RegressionGuards Guards,
    IReadOnlyList<RegressionWindowRow> Windows,
    RegressionSummaryRow Summary);

/// <summary>波次参数块（须与 wave-e2-golden.json 冻结头逐字段相等——测试互锁）。</summary>
public sealed record RegressionWaveParams(
    double Threat,
    int Base,
    int Begin,
    int Spacing,
    double Scaling,
    int BossWave,
    string EliteEnemyId,
    string BossEnemyId,
    int BatchesPerWave,
    int TickRateHz,
    double InitialStockPoints,
    int TowerCap);

/// <summary>守卫带配置（首版启发式，[NEW] 阈值——验收 08 第 3 条原文带界）。</summary>
public sealed record RegressionGuards(
    double KillMinS,
    double KillMaxS,
    double IncomeSpendMin,
    double IncomeSpendMax,
    string Level);

/// <summary>一个 50 波窗口的统计摘要（敌量/击杀/泄漏/资源曲线采样点+窗口哈希）。</summary>
public sealed record RegressionWindowRow(
    int Wave,
    int Enemies,
    int Kills,
    int Leaks,
    double MedianKillS,
    double Income,
    double Spend,
    double Stock,
    int Towers,
    long WindowHash);

/// <summary>全程首版基线数字（击杀时间中位/通胀率/摘要哈希）。</summary>
public sealed record RegressionSummaryRow(double MedianKillS, double IncomeSpendRatio, long SummaryHash);

/// <summary>Source-gen JSON context of the regression golden (AOT-safe, snake_case）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RegressionGoldenFile))]
public sealed partial class RegressionGoldenJsonContext : JsonSerializerContext;

/// <summary>regression-v1.json 的路径/序列化/反序列化助手。</summary>
internal static class RegressionGolden
{
    public const string Schema = "urn:eratd:golden:regression:1";
    public const string RegenEnvironmentVariable = "ERA_REGRESSION_GOLDEN_UPDATE";
    public const string FileName = "regression-v1.json";

    public static string GoldenPath() => Path.Combine(RegressionContent.GoldenDir(), FileName);

    public static string Serialize(RegressionGoldenFile golden) =>
        JsonSerializer.Serialize(golden, RegressionGoldenJsonContext.Default.RegressionGoldenFile);

    public static RegressionGoldenFile Deserialize(string json) =>
        JsonSerializer.Deserialize(json, RegressionGoldenJsonContext.Default.RegressionGoldenFile)
        ?? throw new JsonException("Regression golden deserialized to null.");
}
