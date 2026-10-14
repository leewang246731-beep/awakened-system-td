using Xunit;

namespace EraCore.Tests.Regression.OffSystemRegression;

// T-084 (F-097, R2 硬标准首证) 关闭系统 E1 自动通关回归——共享装置：
// 一次装置构造跑三局完整 E1 战役（关闭局 / 关闭局复跑 / 开启对照组），
// 每局四关全通关（约 40 万 tick/局）。三局由全部用例共享（xUnit
// ICollectionFixture）；集合不设 DisableParallelization（纯确定性逻辑无墙钟
// 敏感，与 CampaignLoopE2ETests 同判），装置构造约 70 s。

/// <summary>Shared three-run fixture of the off-system regression suite.</summary>
public sealed class OffSystemRegressionFixture : IDisposable
{
    /// <summary>The regression run: system off for the whole campaign.</summary>
    public OffSystemRunReport OffRun { get; }

    /// <summary>The determinism twin: system off, run again from scratch.</summary>
    public OffSystemRunReport OffRunAgain { get; }

    /// <summary>The control run: system on, advisor present, zero orders issued.</summary>
    public OffSystemRunReport ControlRun { get; }

    public OffSystemRegressionFixture()
    {
        OffRun = OffSystemCampaignDriver.Run(systemEnabled: false);
        OffRunAgain = OffSystemCampaignDriver.Run(systemEnabled: false);
        ControlRun = OffSystemCampaignDriver.Run(systemEnabled: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 三局均为纯内存终态报告，无待释放资源（驱动内已自清沙盘，CS-3.8）。
    }
}

/// <summary>
/// Collection of the off-system regression: one fixture instance shared by
/// every test of the suite (the three campaigns run once, ~70 s total).
/// </summary>
[CollectionDefinition(Name)]
public sealed class OffSystemRegressionCollection : ICollectionFixture<OffSystemRegressionFixture>
{
    public const string Name = "EraCore.Tests.Regression.OffSystem";
}
