using System.Globalization;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-111] T-055 HUD 三指标（波次/资源产出率/防线完整度）聚合与
// 等宽数字格式化：合法聚合、边界（空防线/满血/零输出/钳位）、非法（负值
// 与非法塔血量行拒绝）。
public class HudMetricsTests
{
    // ---- legal — aggregation -----------------------------------------------

    [Fact] // legal — 三塔两满一残：完整度=8/12，产出率与波次原样入快照
    public void Compute_aggregates_wave_rate_and_tower_integrity()
    {
        var towers = new (int Hp, int MaxHp)[] { (4, 4), (4, 4), (0, 4) };

        HudSnapshot snapshot = HudMetrics.Compute(currentWave: 3, productionPerSecond: 1.25d, towers);

        Assert.Equal(3, snapshot.Wave);
        Assert.Equal(1.25d, snapshot.ProductionPerSecond, 5);
        Assert.Equal(8d / 12d, snapshot.DefenseIntegrity, 5);
        Assert.Equal(3, snapshot.TowerCount);
    }

    [Fact] // legal — 全满血防线=100%
    public void Full_health_towers_report_full_integrity()
    {
        var towers = new (int Hp, int MaxHp)[] { (260, 260), (260, 260) };

        HudSnapshot snapshot = HudMetrics.Compute(0, 0d, towers);

        Assert.Equal(1d, snapshot.DefenseIntegrity, 5);
        Assert.Equal(0, snapshot.Wave);
        Assert.Equal(0d, snapshot.ProductionPerSecond, 5);
    }

    // ---- boundary — empty defense line and clamped formatting ---------------

    [Fact] // boundary — 空防线（塔数 0）=完整度 1.0，塔数并列展示以免误导
    public void Empty_defense_line_is_full_integrity_with_zero_towers()
    {
        HudSnapshot snapshot = HudMetrics.Compute(0, 2d, Array.Empty<(int, int)>());

        Assert.Equal(1d, snapshot.DefenseIntegrity, 5);
        Assert.Equal(0, snapshot.TowerCount);
    }

    [Theory] // boundary — 波次两位补零，≥100 全长输出（等宽列对齐口径）
    [InlineData(0, "W00")]
    [InlineData(7, "W07")]
    [InlineData(42, "W42")]
    [InlineData(100, "W100")]
    public void FormatWave_pads_to_two_digits(int wave, string expected)
    {
        Assert.Equal(expected, HudMetrics.FormatWave(wave));
    }

    [Theory] // boundary — 产出率一位小数（invariant，不随区域设置漂移）
    [InlineData(0d, "0.0/s")]
    [InlineData(1.25d, "1.3/s")] // AwayFromZero 进位
    [InlineData(2.5d, "2.5/s")]
    [InlineData(10d, "10.0/s")]
    public void FormatProduction_uses_one_invariant_decimal(double rate, string expected)
    {
        Assert.Equal(expected, HudMetrics.FormatProduction(rate));
    }

    [Theory] // boundary — 完整度百分比取整并钳位到 [0,100]
    [InlineData(0d, "0%")]
    [InlineData(0.005d, "1%")]
    [InlineData(0.5d, "50%")]
    [InlineData(1d, "100%")]
    [InlineData(1.5d, "100%")] // 上界外钳位
    public void FormatIntegrity_rounds_and_clamps(double integrity, string expected)
    {
        Assert.Equal(expected, HudMetrics.FormatIntegrity(integrity));
    }

    [Fact] // boundary — 格式化与聚合在同一文化环境（土耳其 i 等）下不变
    public void Formatting_is_culture_invariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // 小数逗号文化

            Assert.Equal("1.5/s", HudMetrics.FormatProduction(1.5d));
            Assert.Equal("W03", HudMetrics.FormatWave(3));
            Assert.Equal("50%", HudMetrics.FormatIntegrity(0.5d));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---- illegal — rejected inputs -------------------------------------------

    [Theory] // illegal — 负波次/负产出率被拒（具体异常，不止不抛）
    [InlineData(-1)]
    [InlineData(-100)]
    public void Compute_rejects_negative_wave(int wave)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HudMetrics.Compute(wave, 0d, Array.Empty<(int, int)>()));
    }

    [Fact] // illegal — 负产出率被拒
    public void Compute_rejects_negative_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HudMetrics.Compute(0, -0.1d, Array.Empty<(int, int)>()));
    }

    [Theory] // illegal — 塔血量行非法（max≤0、hp 越界）整单拒绝
    [InlineData(0, 0)]   // max=0
    [InlineData(4, -1)]  // max<0
    [InlineData(-1, 4)]  // hp<0
    [InlineData(5, 4)]   // hp>max
    public void Compute_rejects_malformed_tower_rows(int hp, int maxHp)
    {
        Assert.Throws<ArgumentException>(
            () => HudMetrics.Compute(0, 0d, new (int, int)[] { (hp, maxHp) }));
    }

    [Fact] // illegal — null 塔列表拒绝
    public void Compute_rejects_null_tower_list()
    {
        Assert.Throws<ArgumentNullException>(() => HudMetrics.Compute(0, 0d, null!));
    }
}
