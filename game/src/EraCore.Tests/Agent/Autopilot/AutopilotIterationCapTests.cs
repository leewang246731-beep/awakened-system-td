using EraCore.Agent.Autopilot;
using Xunit;

namespace EraCore.Tests.Agent.Autopilot;

// [F-093][C-13] T-081 迭代上限（§11.8"10 轮/波×点数取小"）：表驱动钉死
// min(10, 波点数) 的每个折点——0、两侧夹逼、常数边。
public class AutopilotIterationCapTests
{
    public static TheoryData<int, int> CapRows => new()
    {
        // legal/boundary：波点数 → 迭代上限 = min(10, max(0, 点数))
        { 0, 0 },    // 无点数预算：本轮 0 轮（无预算即无行动）
        { 1, 1 },    // 点数钳制
        { 2, 2 },
        { 5, 5 },    // E2 波初余额（ceiling 1+2×2=5）的典型值
        { 9, 9 },    // 恰在常数边内侧
        { 10, 10 },  // 常数边：点数=10 时两边相等
        { 11, 10 },  // 点数越过常数边 → 钳在 10
        { 25, 10 },
        { 100, 10 }, // 远越界仍钳 10（10 轮/波硬顶）
    };

    [Theory] // legal：min(10, 点数) 全折点表驱动（§11.8 迭代上限公式）
    [MemberData(nameof(CapRows))]
    public void Iteration_cap_is_min_of_ten_and_wave_points(int wavePoints, int expectedCap)
    {
        Assert.Equal(expectedCap, AutopilotDirector.IterationCap(wavePoints));
    }

    [Fact] // boundary：负点数防御性钳 0（伪造波不可能给出负余额，公式不炸）
    public void Negative_points_clamp_to_zero()
    {
        Assert.Equal(0, AutopilotDirector.IterationCap(-7));
    }

    [Fact] // legal：常数边钉死——§11.8"10 轮/波"冻结口径 + E2 波初余额推导
    public void Cap_constants_are_pinned()
    {
        Assert.Equal(10, AutopilotDirector.MaxIterationsPerWave);
        Assert.Equal(10, AutopilotDirector.IterationCap(int.MaxValue)); // 上界侧钳制
    }
}
