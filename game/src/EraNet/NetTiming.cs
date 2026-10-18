using EraCore.Sim;

namespace EraNet;

/// <summary>
/// T-139 (F-161) 网络节奏常量：08 任务卡验收词「快照 200ms」在冻结 30 Hz
/// tick 域（K-1，<see cref="Simulation.TicksPerSecond"/>）下的整数换算——
/// 30 tick/s × 0.2 s = 6 tick（48 号 §3.1 的整除守恒论证）。宿主每过
/// <see cref="SnapshotCadenceTicks"/> tick 推一行 SimHashSample 摘要；客户端
/// 同节奏自采样对账。公式派生而非手填（CS-2.3/CS-4.4：200/1000 = 0.2 s 的
/// 毫秒域常数属于验收词本身，不是内容锚点）。
/// </summary>
public static class NetTiming
{
    /// <summary>快照摘要节拍（tick）：验收词 200 ms ÷ tick 周期。</summary>
    public const int SnapshotCadenceTicks = Simulation.TicksPerSecond * 200 / 1000;
}
