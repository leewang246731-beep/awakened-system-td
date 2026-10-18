using EraCore.Endless;
using EraCore.Tests.World;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-154] 无尽生产策略表（endless-policies.json——业主两项裁定的数值
// 真相源，RL7）：严格解析守卫（closed schema/marker/渐升键全或无/数值非法经
// 策略构造拒绝）+ 入库表形状断言（mix 渐升生产档 41 波 9:1→81 波 3:1 终值
// 待 T-158 实测微调；affix_rolls≡EndlessAffixRollPolicy.Default 同构校验——
// 双源零漂移锚）+ 从表构造的运行行为（渐升档经 WeightsAt 真实生效）。
// IO 由测试自持（仓库相对寻址 game/content，RL9——AudioContent 同模式）。
public class EndlessPolicyTableTests
{
    /// <summary>仓库相对寻址 game/content/endless/endless-policies.json.</summary>
    private static string ShippedTable()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName, "game", "content", "endless");
            if (Directory.Exists(probe))
            {
                return File.ReadAllText(Path.Combine(probe, "endless-policies.json"));
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/endless（测试须在仓库内运行）");
    }

    private const long Seed = 20261540;

    // ------------------------------------------------ 解析守卫 -----

    [Fact] // illegal：坏 JSON/非对象根/错 schema/错 marker/缺块拒绝
    public void Malformed_tables_are_rejected()
    {
        Assert.Throws<EndlessPolicySchemaException>(() => EndlessPolicyTable.Parse("{ 不合法", "t"));
        Assert.Throws<EndlessPolicySchemaException>(() => EndlessPolicyTable.Parse("[]", "t"));

        string full = ShippedTable();
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(full.Replace(SchemaUrnText, "urn:eratd:wrong"), "t"));
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(full.Replace("\"EndlessPolicyTable\"", "\"OtherTable\""), "t"));
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(full.Replace("\"mix\"", "\"mixing\""), "t"));       // 缺 mix 块
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(full.Replace("\"affix_rolls\"", "\"rolls\""), "t")); // 缺 affix_rolls 块
    }

    private const string SchemaUrnText = "urn:eratd:content:endless:policies:1";

    [Theory] // illegal：mix 块数值/渐升键全或无守卫（经策略构造拒绝后带源标签回抛）
    [InlineData("{\"mix_from_wave\":0,\"start_era_weight\":9,\"other_era_weight\":1}")]
    [InlineData("{\"mix_from_wave\":41,\"start_era_weight\":0,\"other_era_weight\":1}")]
    [InlineData("{\"mix_from_wave\":41,\"start_era_weight\":9,\"other_era_weight\":1,\"ramp_end_wave\":81}")]
    [InlineData("{\"mix_from_wave\":41,\"start_era_weight\":9,\"other_era_weight\":1,\"ramp_end_wave\":40,\"end_start_era_weight\":3,\"end_other_era_weight\":1}")]
    [InlineData("{\"mix_from_wave\":41}")]
    public void Invalid_mix_blocks_are_rejected(string mixBlock)
    {
        string json = "{\"$schema\":\"" + SchemaUrnText + "\",\"content_type\":\"EndlessPolicyTable\"," +
                      "\"mix\":" + mixBlock +
                      ",\"affix_rolls\":{\"roll_every_waves\":10,\"roll_start_wave\":41,\"kinds_per_roll\":1," +
                      "\"allow_consecutive_repeat\":false,\"repeat_intensity_gain\":1.5}}";
        // label：违规场景描述（Theory 行内注释）
        Assert.Throws<EndlessPolicySchemaException>(() => EndlessPolicyTable.Parse(json, "t"));
    }

    [Fact] // illegal：affix_rolls 块守卫（缺键/缺布尔/数值非法经策略构造拒绝）
    public void Invalid_roll_blocks_are_rejected()
    {
        string MixBlock() => "\"mix\":{\"mix_from_wave\":41,\"start_era_weight\":9,\"other_era_weight\":1," +
                             "\"ramp_end_wave\":81,\"end_start_era_weight\":3,\"end_other_era_weight\":1}";
        string Head() => "{\"$schema\":\"" + SchemaUrnText + "\",\"content_type\":\"EndlessPolicyTable\"," + MixBlock() + ",\"affix_rolls\":";

        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(Head() + "{\"roll_every_waves\":10,\"roll_start_wave\":41,\"kinds_per_roll\":1}}", "t")); // 缺布尔+增益
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(Head() + "{\"roll_every_waves\":0,\"roll_start_wave\":41,\"kinds_per_roll\":1," +
                "\"allow_consecutive_repeat\":false,\"repeat_intensity_gain\":1.5}", "t")); // 节奏非法
        Assert.Throws<EndlessPolicySchemaException>(() =>
            EndlessPolicyTable.Parse(Head() + "{\"roll_every_waves\":10,\"roll_start_wave\":41,\"kinds_per_roll\":9," +
                "\"allow_consecutive_repeat\":true,\"repeat_intensity_gain\":1.5}", "t")); // kinds 越界
    }

    // ------------------------------------------------ 入库表（生产数值真相源）-----

    [Fact] // legal：入库表=生产档形状——mix 渐升 41 波 9:1→81 波 3:1（终值待 T-158 实测微调）；affix_rolls≡代码生产档
    public void Shipped_table_is_the_t154_production_shape()
    {
        EndlessPolicyTable table = EndlessPolicyTable.Parse(ShippedTable(), "endless/endless-policies.json");

        EndlessMixPolicy mix = table.MixPolicy;
        Assert.Equal(41, mix.MixFromWave);
        Assert.Equal(9.0, mix.StartEraWeight);
        Assert.Equal(1.0, mix.OtherEraWeight);
        Assert.Equal(81, mix.RampEndWave);
        Assert.Equal(3.0, mix.EndStartEraWeight);
        Assert.Equal(1.0, mix.EndOtherEraWeight);
        Assert.Equal((9.0, 1.0), mix.WeightsAt(41)); // 起始锚
        Assert.Equal((6.0, 1.0), mix.WeightsAt(61)); // 插值锚
        Assert.Equal((3.0, 1.0), mix.WeightsAt(81)); // 终值锚（待 T-158 实测微调——改表即改产）

        // 双源同构校验：表节奏 ≡ EndlessAffixRollPolicy.Default（T-143 生产档数值冻结面）
        EndlessAffixRollPolicy rolls = table.AffixRollPolicy;
        Assert.Equal(EndlessAffixRollPolicy.Default.RollEveryWaves, rolls.RollEveryWaves);
        Assert.Equal(EndlessAffixRollPolicy.Default.RollStartWave, rolls.RollStartWave);
        Assert.Equal(EndlessAffixRollPolicy.Default.KindsPerRoll, rolls.KindsPerRoll);
        Assert.Equal(EndlessAffixRollPolicy.Default.AllowConsecutiveRepeat, rolls.AllowConsecutiveRepeat);
        Assert.Equal(EndlessAffixRollPolicy.Default.RepeatIntensityGain, rolls.RepeatIntensityGain);
    }

    [Fact] // legal：从入库表构造的运行真实渐升——同 seed 与无渐升兜底档名册不同、自身两局逐波全等
    public void Table_built_runs_ramp_and_stay_deterministic()
    {
        EndlessPolicyTable table = EndlessPolicyTable.Parse(ShippedTable(), "endless/endless-policies.json");
        long mixFrom = table.MixPolicy.MixFromWave;
        long rampEnd = table.MixPolicy.RampEndWave!.Value;

        var ramped = new EndlessRun(
            new EndlessRunConfig(Seed, 1, 0.9, 6, 1, 3, 1.0, 10, TwoEraPool(),
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                mixPolicy: table.MixPolicy),
            new EraCore.Tests.World.LcgRng(Seed));
        var replay = new EndlessRun(
            new EndlessRunConfig(Seed, 1, 0.9, 6, 1, 3, 1.0, 10, TwoEraPool(),
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                mixPolicy: table.MixPolicy),
            new EraCore.Tests.World.LcgRng(Seed));
        var fallback = new EndlessRun(
            new EndlessRunConfig(Seed, 1, 0.9, 6, 1, 3, 1.0, 10, TwoEraPool(),
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                mixPolicy: EndlessMixPolicy.Default),
            new EraCore.Tests.World.LcgRng(Seed));

        bool rosterDiverged = false;
        for (int wave = 1; wave <= (int)rampEnd + 10; wave++)
        {
            EndlessWavePlan rampedPlan = ramped.BeginWave();
            EndlessWavePlan replayPlan = replay.BeginWave();
            EndlessWavePlan fallbackPlan = fallback.BeginWave();
            Assert.Equal(rampedPlan.Roster, replayPlan.Roster); // 表档确定性（同 seed 全等）
            if (wave >= mixFrom && !rampedPlan.Roster.SequenceEqual(fallbackPlan.Roster))
            {
                rosterDiverged = true; // 渐升生产档 ≠ 无表兜底 1:1 档（混编段份额不同）
            }

            ramped.CompleteWave(rampedPlan.EliteCount, rampedPlan.BossCount);
            replay.CompleteWave(replayPlan.EliteCount, replayPlan.BossCount);
            fallback.CompleteWave(fallbackPlan.EliteCount, fallbackPlan.BossCount);
        }

        Assert.True(rosterDiverged, "生产渐升档必须与兜底 1:1 档在混编段产生不同名册");
    }

    /// <summary>双时代小池（E1 主场 2 行 + E2 跨代 1 行——渐升份额可辨）.</summary>
    private static EndlessEnemyPool TwoEraPool() => new(new[]
    {
        new EndlessEnemyPoolEntry("era1:enemy-a", 1, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-b", 1, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-c", 2, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-elite", 1, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-elite", 2, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-boss", 1, EndlessEnemyRole.Boss, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-boss", 2, EndlessEnemyRole.Boss, 1.0),
    });
}
