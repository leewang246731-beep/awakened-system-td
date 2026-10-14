# REV-20261014-T067-golden-first-creation — T-067 首版金样登记（CS-6.2 首版条款）

| 项 | 内容 |
|---|---|
| 登记对象 | commit `8488523`（"T-067: C-12 full orchestration (entity groups, era2 subsystems, golden+replay still deterministic)"）——首版金样 `game/content/golden/sim-e2-golden.json` 与产生它的 C-12 全量编排引擎同 commit 合入 |
| 登记日期 | 2026-10-14 |
| 条款依据 | CODE_STANDARDS CS-6.2 v1.1 首版条款（先例 d6f5862/T-040、3a92a16/T-057）；CS-6.3 依据号已入 commit message（G.1 条 2 / F-155 / v3.2 §6 / spike① 报告 §3） |
| 引擎触面 | `game/src/EraCore/Sim/`（Simulation/SimulationConfig 扩展 + EntityGroups/ 3 新文件 + SimProjectile + SimTeleporterLane），全部为本 commit 新增或本任务内扩展——引擎先于金样存在的首版因果成立（分组+全子系统编排引擎即金样产生器） |
| 豁免登记 | `tools/ci/allowlist.txt` 增 `T8: 8488523` 条目（本 REV 即登记处；更新金样无豁免通道，必须独立 commit） |

## 一、金样内容与验证

- 场景：E2 全子系统夹具（SimE2Fixture，era2 内容 JSON 驱动）——10 动词 28 命令（7 座劳工营能量骨干、3 条采集线、3 条 era-2 弹药链（箭矢/石矢/火油）、箭楼→弩塔升级+火油溅射燃烧、六档物流全家（民夫/马车/矿轨/传送带/无人机/传送阵能量钩 60 点/s）、执戈者英雄令×2、确认出售残骸），seed 20261015 ×1800 tick；采样步长 300，7 个采样（tick 0/300/…/1800）由真实引擎产出（CS-4.4）。
- 7 检查点 StateHash（留档）：8569934103109878095 / 2995839191221114670 / -9197006259156875547 / -2435409693289533429 / -5832134813774395462 / -5153833583858112698 / 8004785478414080499。
- 生成入口：`ERA_SIM_E2_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Sim`（已留档于金样文件头 regen_command；行内容禁手改 CS-6.1）。
- 生成即验证：WriteGolden 落盘后回读并由引擎逐采样复现（SimE2GoldenTests.WriteGolden 自检）。
- E1 金样 sim-e1-golden.json 逐位不变（新组件缺席即不入哈希——金样稳定域见 Simulation.ComputeStateHash 注释；SimGoldenTests 每测复证）。

## 二、完成门槛验证（真实执行，摘录）

- `dotnet test --filter EraCore.Tests.Sim`：**94/94 通过**（分组单元/传送阵钩单元/弹影单元/E2 编排集成/确定性三断言/双金样回归/性能护栏）。
- 确定性三断言（E2 全子系统配置）：同 seed 逐 tick 全等（1801 采样）+判别力自查（>1700 个不同哈希值）；异 seed 首分叉（tick 0）后逐 tick 不等；命令时点 600/900 分叉点=600 且此后不回聚；回放验证器 Verified=true。
- E2 编排活性：护盾破+再生观测、英雄技能 Fired 后 1200t 冷却且 CD 内不重置、传送阵 EnergySpent=DeliveredItems×5>0（账本满足度 1.0）、升级同格同 id 换行+换弹系、出售残骸 27/27（30% 口径）+占格阻断重建、六档逐档交付≥1、燃烧 DOT 活跃、弹影在飞、确认位缺省拒绝。
- `dotnet build -c Release -p:TreatWarningsAsErrors=true`：0 警告 0 错误。
- `python tools/ci/redline_scan.py`：本任务触面 0 违规（T8 于本 REV 登记；T2 声明 ISpatialIndex 纯接口）。当日共享树上另有并行泳道未跟踪文档的 R9 违规（docs/engineering/18、21），非本 commit 触面。
- 全仓测试 `dotnet test EraTD.sln`：3235/3236 通过，唯一失败为 LlmOnlineTests.MiMo_live_connection_probe（在线网络探测，重跑即绿——T-072 已内置两次去抖；与本任务无关）。
- 性能护栏（验收④）：worktree 隔离的重构前 HEAD 与分组引擎背靠背配对 A/B——重构前 sim Step CPU 均 2.179 ms（配对校准 829.45 ms）vs 重构后 1.840 ms（配对 750.19 ms），归一比值 ×0.94 ≤ ×1.2（留档于 SimGroupingPerfGuardrailTests 类注释）；套内断言=best-of-4 进程 CPU 均 ≤ 0.959×1.2×2.25（共享机并发容差，PerfSerialCollection/REV-20261014-wave2-round1 先例口径）。

## 三、遗留与挂账

- 四叉树：接口预留未实现（03 分解决议——spike① 线性扫描 1.336 ms 未证明空间查询需求；首个空间查询消费方任务接入），T2 已声明。
- 对象池（G.1 条 2 第四项）：实体分配剖析未见 30 Hz 热路径 GC 压力（spike① 留档），随实体回收剖析任务落位（EntityGroup 类注释登记）。
- 冲锋脉冲（Boss 撞塔，波 20+）与踏群 AoE 的建筑侧结算：三速度通道折算已就位（ResolveSpeedEffects 每拍折算 slow+charge+aura），Boss 战编排任务接入。
- 共享机墙钟噪声：本任务实测同代码 best-of-3 跨分钟漂移 ×1.3（多泳道邻居），紧公差墙钟门禁不可复现——性能护栏取 CPU 时间+配对 A/B 留档口径（类注释）；如后续独占 CI 出现可收紧套内线。
