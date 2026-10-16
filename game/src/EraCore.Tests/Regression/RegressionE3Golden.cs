using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Tests.Regression;

// T-122 (F-172) E3 长程回归金样信封（game/content/golden/regression-e3-v1.json，
// 首版=新文件非改 T-083 regression-v1.json——两金样并行互不触碰，现有 e1/e2/
// sim/checkpoint/regression-v1 零漂移保持）：头留档 seed/波数/采样节奏/40 波
// 循环语义/关卡轮换数/路径注/再生成命令（CS-6.1/CS-6.3——sim-e1/e2 金样先例），
// levels=E3 四关内容冻结参数快照（levels/e3.json 波表+塔线+初始授予——与活内容
// 互锁，内容表漂移即红），windows=每 50 波统计摘要+窗口哈希，summary=首跑
// 基线数字+摘要哈希。守卫告警（WARN 级）不入金样——越带不阻断（T-083 验收
// 08 第 3 条同口径），留档走测试输出与 CI 日志。序列化走 source-gen 上下文
//（AOT 红线同产品侧惯例；RegressionGuards/RegressionWindowRow/RegressionSummaryRow
// 复用 T-083 信封的公共记录）。

/// <summary>Envelope of the E3 regression golden file (T-122 first creation).</summary>
public sealed record RegressionE3GoldenFile(
    string Schema,
    string Task,
    long Seed,
    int Waves,
    int SampleEvery,
    int WaveCycle,
    int LevelRotation,
    string PathNote,
    string RegenCommand,
    string CheckCommand,
    string Notes,
    IReadOnlyList<RegressionE3LevelRow> Levels,
    RegressionGuards Guards,
    IReadOnlyList<RegressionWindowRow> Windows,
    RegressionSummaryRow Summary);

/// <summary>
/// One E3 level's frozen simulator input snapshot（levels/e3.json 行：威胁/胜波/
/// 波表旋钮/roster/精英线/初始授予钢点/塔线里程碑——互锁面，非期望值）。
/// </summary>
public sealed record RegressionE3LevelRow(
    string LevelId,
    double Threat,
    int WinWaves,
    int BaseCount,
    int BeginWave,
    int Spacing,
    double Scaling,
    int BatchesPerWave,
    double ValleySeconds,
    IReadOnlyList<string> RegularRoster,
    bool EliteLineEnabled,
    string? EliteEnemyId,
    double InitialStockPoints,
    IReadOnlyList<RegressionE3TowerMilestone> TowerLine);

/// <summary>塔线里程碑（command_script build-tower 行：波锚+塔 id+价格）。</summary>
public sealed record RegressionE3TowerMilestone(int FromWave, string TowerId, double Price);

/// <summary>Source-gen JSON context of the E3 regression golden (AOT-safe, snake_case）.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RegressionE3GoldenFile))]
public sealed partial class RegressionE3GoldenJsonContext : JsonSerializerContext;

/// <summary>regression-e3-v1.json 的路径/序列化/再生成开关助手。</summary>
internal static class RegressionE3Golden
{
    public const string Schema = "urn:eratd:golden:regression-e3:1";
    public const string RegenEnvironmentVariable = "ERA_REGRESSION_E3_GOLDEN_UPDATE";
    public const string FileName = "regression-e3-v1.json";

    public static string GoldenPath() => Path.Combine(RegressionContent.GoldenDir(), FileName);

    public static string Serialize(RegressionE3GoldenFile golden) =>
        JsonSerializer.Serialize(golden, RegressionE3GoldenJsonContext.Default.RegressionE3GoldenFile);

    public static RegressionE3GoldenFile Deserialize(string json) =>
        JsonSerializer.Deserialize(json, RegressionE3GoldenJsonContext.Default.RegressionE3GoldenFile)
        ?? throw new JsonException("E3 regression golden deserialized to null.");
}
