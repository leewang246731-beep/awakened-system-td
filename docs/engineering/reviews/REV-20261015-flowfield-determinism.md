# REV-20261015-flowfield-determinism — 流场墙钟预算→确定性格配额（T-067 行尾 DEBT 清账）

| 项 | 内容 |
|---|---|
| 任务 | T-067 行尾 DEBT 清账（A2 泳道；T-086 发现、T-096 v0.1 发包前硬门槛）——FlowFieldManager 墙钟预算的确定性改造 |
| 依据 | AGENTS.md 纪律 9（同 seed+命令流→哈希全等是底线）；T-086 发现实证（OffSystemRegressionTests 高负载 2 次哈希发散）；设计 v3.2 §6（流场 8ms/帧切片预算）；K-1（30 Hz 固定 tick 时域）；F-043（流场分帧续算） |
| 交付物 | `game/src/EraCore/Pathfinding/FlowField.cs`+`FlowFieldManager.cs`（确定性入口+软护栏）；`game/src/EraCore/Sim/Simulation.cs` 调用点切换（最小改，2 处注明）；`game/src/EraCore.Tests/Pathfinding/` 两文件（切片等价新口径+负载无关哈希+吞吐门禁）；`EraCore.Tests/Spikes/Spike1PerformanceTests.cs` twin 调用点切换；08 的 T-067 行标 `[RESOLVED-20261015]` |
| CI 接入 | 零改动——新测试随 EraCore.Tests 自动入 `run_all.sh` G1 段 |

## 一、问题（T-086 发现的机制）

`FlowFieldManager.Update(budgetMs)` 用 `Stopwatch` 墙钟（默认 8 ms/tick）节流流场重建：每 tick 实际完成的泛洪工作量取决于**该 tick 分到的真实 CPU 时间**。CPU 争用下（CI 并行/邻居泳道/OS 调度）预算被时钟偷走 → 每 tick 完成格数缩水 → 重建完成的 tick 漂移 → 单位在旧场上多走/少走若干 tick → `ComputeStateHash` 发散。实证：OffSystemRegressionTests 高负载下 2 次同种子哈希发散（T-086 发现记录）。注：单场哈希本身是切片无关的（切片边界不进结果）——发散点在**完成时刻**进 sim 状态，这是墙钟决策位进入仿真时域的唯一通道。

## 二、修法（确定性预算）

- **`FlowField.AdvanceDeterministic(int cellBudget)`**：按**处理格数配额**推进——记账单位=1 格（快照扫描 1 格或前沿沉降 1 pop；快照逐格计费支持行中断点，任何正配额在任意宽图上都有进展，不整行起步也就不会停滞）。Dijkstra pop 序确定 ⇒ 总工作量=图快照纯函数 ⇒ 每 tick 花费=min(配额, 剩余工作量) 确定 ⇒ **完成 tick 与墙钟负载无关**。前沿清空时的收尾簿记（不可达扫描/重泛洪重置）不记账但界定为每次 settle 恒定 O(cells)，不破坏确定性（per-tick 最坏=配额+O(cells)）。
- **`FlowFieldManager.UpdateDeterministic(int? maxCellsPerTick = null)`**：仿真入口。脏桶启动与遗留入口共享（`StartDirtyRebuilds` 提取）；配额在本 tick 所有挂起桶间**共享**（构建序先到先得，与墙钟版共享 deadline 的语义一致）。零配额=合法零进展边界（仍报告挂起态）。`LastTickCellsProcessed` 观察位输出本 tick 记账。
- **Stopwatch 降级软护栏**：`UpdateDeterministic` 内 `Stopwatch` 只做观察——超 `SoftGuardThresholdMs`（默认 `SoftGuardWarnMs`=16 ms≈半个 30 Hz tick）记 `SlowTickCount++` 并触发可选 `SlowTickObserver` sink（EraCore 无自建 logger，宿主决定告警形态）。**观察不决策**：配额是唯一行为输入。
- **`FlowField.ProcessedCells`**：累计记账计数（两入口同计），管理器据此分账配额，测试据此断言总工作量与切片方式无关。
- **遗留 `Update(double?)` 保留**：工具与既有调用方（Logistics/GridWorld 集成测试等）不喂仿真 tick，语义不变（类文档已注明"仿真禁用"警告）。

**调用点切换（逐处注明）**：
1. `Sim/Simulation.cs` `Step()` 第 (5) 段：`_flows.Update(_config.FlowFrameBudgetMs)` → `_flows.UpdateDeterministic(FlowFieldManager.DefaultCellsPerTick)`（注释含 T-067/T-086 出处与原行）。`SimulationConfig.FlowFrameBudgetMs` 保留（ctor 校验+喂管理器遗留墙钟默认值，注释注明不再参与 tick 切片）——最小改不扩大 Sim 面。
2. `Spikes/Spike1PerformanceTests.cs` twin：`twin.Update(DefaultFrameBudgetMs)` → `twin.UpdateDeterministic()`（归因对照与 sim 同一切片机制）；门禁④注释随口径更新。

## 三、配额校准（DefaultCellsPerTick = 2048）

本机（A5 级开发机，**实测时邻居泳道在跑**——负载条件下的数字）300×300 稀疏 d=0.08 seed=42 全图泛洪（WallTear 模式，总工作量 255,794 记账格）：

| 项 | 实测 |
|---|---|
| 单位成本 | ≈0.32 µs/记账格（82 ms / 255,794 格） |
| 2048 格/tick 墙钟成本 | ≈0.7 ms/tick（对 8 ms 设计切片裕量 ~11×；×3 CI 抖动后 ~2.1 ms 仍 <8 ms；对 16 ms 软护栏 ~22×；对 33.3 ms tick ~45×） |
| 300×300 重建完成 | 125 tick（~4.2 s @ 30 Hz） |
| E1 128×128 地图 | ≈46k 格 ⇒ ~23 tick 收敛（spike1 实测初建+重建在预算内，见 §四） |

校准判据（任务口径）：配额吞吐 ≥ 墙钟版 90%——实测 92.8% 达标，故**不调大**；若未来"场地就绪时延"比 per-tick 余量更重要，可上调（注释中已留上调约束：不低于 ~3× 单位成本最坏裕量）。

## 四、吞吐对比（同轮同机自比，机器无关比率门禁）

`FlowFieldManagerTests.DeterministicQuotaThroughputMatchesWallclockOnFullFlood` 留档（同一次运行内对照，首败按 REV-20261014-wave3-round2 #4 三批取中位重判）：

```
[T-067 DEBT 清账] 300x300 d=0.08 seed=42: workUnits=255794
  | wallclock(8ms): frames=10 total=76ms   throughput=3336.6 cells/ms
  | quota(2048/tick): ticks=125 avgCellsPerTick=2046.4 total=82ms throughput=3096.0 cells/ms
  | ratio=0.928  mode=WallTear
```

三重哈希全等钉死（burst=墙钟切片=配额切片）+ 两切片路径总记账数与 burst 全等（255,794）+ 管理器逐 tick 记账累计=字段累计。spike1 门禁（确定性口径重跑）：逻辑帧均=2.329 ms（回归线 3.0）、sim p99=2.881 ms、流场均切片=0.0010 ms、**流场单切片最大=1.60 ms**（门禁④ 10 ms 线内——配额一 tick 的墙钟成本实测值，即每 tick 花的是 ~0.7-1.6 ms 而非等满 8 ms）。

## 五、回归证据

1. **Pathfinding 全部既有测试口径更新后全绿**：87/87（FlowFieldTests 41+FlowFieldManagerTests 31+IFlowGridTests/GridWorldFlowFieldIntegrationTests 等）。切片等价断言双口径钉（墙钟 1 ms 切片既有钉保留 + 配额切片新钉：字段级 `QuotaSlicedBuildEqualsOneShotAcrossAllPhases` 三阶段拆墙图、管理器级 `QuotaSlicedManagerBuildEqualsOneShotOnLargeMaze` 128×128）；预算边界（零配额零进展/负配额拒绝/配额恰花满 `QuotaSpendingIsExactEvenBelowOneRow`）；脏区/LRU/旧场续服/桶帽语义零改动（遗留入口共享 `StartDirtyRebuilds`，原测试原样通过）。
2. **负载无关哈希新单测**：`DeterministicQuotaCompletionIsIndependentOfCpuNoise`——两同图管理器，其一在每次泛洪间插入 1e6 次迭代的计算噪声循环（消耗真实墙钟、时长随负载漂移），断言完成 tick 数/场哈希/重建计数/总记账数与安静对照**全等**（墙钟入口下该断言会被噪声打破——正是 T-086 发散机制的直接钉）。配套 `SoftGuardObservesWithoutChangingBehavior`（阈值测试缝降 0 强制触发，断言观察不改行为：完成 tick/哈希/重建数与默认阈值对照全等）。
3. **确定性压测（OffSystemRegression 同 seed）**：`--filter OffSystemRegressionTests` 连跑 3 次全绿（3×8/8），每次装置内含关闭局双跑（OffRun vs OffRunAgain）的 `Off_system_double_run_hashes_are_identical`（逐关采样哈希账本+终局哈希全等）——3 次独立双跑哈希全等证据。
4. **性能门禁**：spike1/spike2/spike3 全绿（10/10；spike1 数字见 §四，首跑即过未动用重跑容差）。
5. **run_all**：见提交时汇报（G1~G4）。

## 六、语义审视与挂账

- **既有墙钟切片测试语义**：`SlicedBuildEqualsOneShotBuildOnLargeMaze`/`ManagerSlicesRebuildAcrossFramesWithinBudget`/`UpdateWithZeroBudgetReportsPendingWithoutProgress` 等保留原墙钟口径（遗留入口仍在册、非仿真路径合法）；等价性断言的**仿真口径**由配额版新钉接管。`FullFlood300x300SparseStaysWithinBudgetEnvelope`（TierA 墙钟包络）原样保留+新增 §四 吞吐对比门禁。
- **配额<行宽语义**：初版整行计费会在配额<图宽时零进展死锁（自测捕获：128 图配额 96 不终止）；终版逐格+行中断点，任何正配额可推进（`QuotaSpendingIsExactEvenBelowOneRow` 钉）。
- **`FlowFrameBudgetMs` 配置位**：仍经 `SimulationConfig` 校验并传入 ctor，但仿真 tick 不再消费——如后续要求配置面收敛，可在内容注册表化时并入配额参数（挂 C-19/RL7 后续，非本债范围）。
- **收尾簿记不记账**：每次 flood settle 的 O(cells) 扫描/重置不进配额（每次 build 恒定次数，确定性无虞）；per-tick 最坏=配额+O(cells)，实测最坏切片 1.6 ms 远在包络内。

## 七、完成门槛验证

- `dotnet test --filter Tests.Pathfinding`：**87/87 通过**。
- `dotnet test --filter "Tests.Sim|Tests.Logistics|GridWorldFlowField"`：**258/258 通过**（Simulation 切换无行为回退）。
- `dotnet test --filter OffSystemRegressionTests` ×3：**3×8/8 通过**（含双跑哈希全等）。
- `dotnet test --filter Spikes`：**10/10 通过**（spike1/spike2/spike3 门禁全绿）。
- `bash tools/ci/run_all.sh`：**总体 PASS（G1~G4 全 PASS）**——实跑记录（2026-10-15，与 T-088 泳道并行窗口）：G2 红线零违规（9 checks/0 FAIL）｜G2.5 资产规格 files=404 rejected=0｜G1 `dotnet test` **5041 passed, 0 failed**（187 s）｜G3 Release `TreatWarningsAsErrors=true` 零警告（18 s）｜G4 SMOKE_OK 已捕获（16 s）。
- 单 commit：`determinism: flow-field budget wallclock→cell-quota (T-067 DEBT cleared, load-independent hashing)`，仅含 Pathfinding 两产品文件+对应测试两文件+Spike1 调用点+Simulation 最小改两处+08 标记+本 REV。
