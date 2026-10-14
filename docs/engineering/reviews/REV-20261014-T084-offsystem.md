# REV-20261014-T084-offsystem — 关闭系统 E1 自动通关回归（R2 硬标准首证记录）

| 项 | 内容 |
|---|---|
| 任务 | T-084 关闭系统 E1 自动通关回归（Wave 3 收口，F 泳道，组件 C-30；验收=headless 通关 E1 全关+全成就可得断言） |
| 依据 | 设计文档 v3.2 §2 支柱 4 / §10.7 / §19-R2（**系统全程可选：关闭系统可全通关、可全成就、警报功能不缺失——硬验收**；关闭=警报降级中性横幅+音效、功能无损、无对话成就）；F-097（系统可选性）；T-075 OffModeDowngrade 降级矩阵；T-085 e1.json 四关命令流；T-081 AutopilotPenalties 首通成就钩子；T-073 CampaignProgression 结算链 |
| 交付物 | `game/src/EraCore.Tests/Regression/OffSystemRegression/`（OffSystemCampaignDriver.cs 共享驱动 + OffSystemRegressionFixture.cs 三局装置 + OffSystemRegressionTests.cs 八断言面，共 8 用例） |
| 产品代码改动 | **零**——CampaignLoop/Progression/Agent/Pipeline 无一行扩展（F 泳道纪律：只写测试） |
| CI 接入 | 零改动——随 EraCore.Tests 自动入 `run_all.sh` G1 段 |

## 一、驱动口径（三局同种子同命令流，唯一变量=开关位）

- **关闭局（回归主体）**：`PanelStateMachine(false)` 开关位落位（PanelState.Hidden、军令输入面/徽记全隐、HUD 中性、警报通道=NeutralBanner）→ 不构造 AgentPipeline（军令通道关闭）→ 逐关 `LevelSession`（T-085 e1.json 四关、map.seed、T-085 命令流纯玩家建造：build-labor-camp/harvester/converter/tower/assign-carrier 五动词，零军令/零卷轴）→ RunFor 600 tick 分片跑到 Victory 谓词 → `CampaignProgression.SettleVictory`（ObserveFacts+判分+开箱）→ T-081 `FilterFirstClearAchievements` 首通授予 → CompleteLevel+ClearExitSnapshot → `CampaignGrid.Occupy` → 四关全占（主城+4=5 格，占领序=关序）。
- **关闭局复跑（确定性对照之一）**：同配置从零再跑一整局。
- **开启对照组（可选性证明）**：同种子同命令流，唯一差异=`AgentPipeline.OverSimulation` 把军师挂上同一活体模拟（era=1，E1 建造动词锁定）+ 每波界 `Ledger.BeginWave` 回充；玩家整局不下一条令。
- **审计三轨**（每 600 tick=20 s 采样 + 每波界）：① K-8 计数汇流（CountingJournalWriter 按 Input/Validation/回充噪声分账）；② 系统点账本余额观察（E1 era=1 点经济关闭，全程恒 0）；③ 警报通道（塔线真实完整度经 `HudMetrics.Compute` → `OffModeDowngrade.DefenseAlarmAt` 双开关位各评估一次）。
- **主机审计计数口径**（HostRunCounters 实况背书，非声明值）：坏事件 0 有"终局建筑全员存活+HP 不可变"背书（当前 E1 引擎无敌方→建筑伤害路径）；DefenseIntegrityMin/WaveEndEfficiencyMin=采样最小值；AmmoStarvedSeconds=塔效率缺口累积 Σ(1−efficiency)×Δt（上界口径，非事件级精算——**如实登记的近似**）；FirstWaveLeaks=波 1 入场窗（grace..grace+interval）内 ArrivedAtTarget 粘滞位计数；SystemBuildPlanCount=K-8 动作事件计数（0）。

## 二、四关通关断言结果（首证实测）

| 关 | 威胁/胜波 | Victory tick | 采样哈希样本数 | 终局塔数 | 判分（判定器自抽） | 完成 | 档位 | 开箱 | 入库（木） |
|---|---|---|---|---|---|---|---|---|---|
| e1-01 觉醒林地 | 0.3/13 | 115541 | 192 | 3 | s5 效率≥70% ✓｜c8 首波零漏 ✗（本局节奏事实） | 2 | gold | 4 件 | 120 |
| e1-02 枯河隘口 | 0.3/13 | 108462 | 180 | 4 | s6 主城仓库零损 ✓｜c8 ✗ | 2 | gold | 4 件 | 1177 |
| e1-03 石语河谷 | 0.45/14 | 101266 | 168 | 4 | s1 采集站零损 ✓｜c7 全程不暂停 ✓ | 3 | gold_extra | 5 件 | 120 |
| e1-04 双丘烽线 | 0.6/16 | 97733 | 162 | 6 | s1 ✓｜c7 ✓ | 3 | gold_extra | 5 件 | 90 |

命令流 45 条全签发全应用（零拒收）；战役池木 2000（恰抵 E1 仓库上限——入池只计上限内实证）；装饰 18 件全入账。**四关全 Victory ⇒ "关闭系统可全通关"（E1 半边）成立**（E1+E2 全章硬验收归 T-130）。

## 三、全成就可得结论

- **成就清单口径**（对齐现有 Progression 钩子，无一新增产品面）：首通成就（T-081 钩子，逐关 `first-clear:{id}`）｜三重目标判分（TripleObjectiveJudge 判定器按关卡 id 种子自抽 strategy+challenge——**如实登记：e1.json 关卡行声明的 objectives 实例对当前结算链未读取，属既有生产行为，判分面以判定器自抽为准**）｜宝箱档位+装饰件（ChestRewards 种子化先定后演）｜占领里程碑（四关全占=CapturedLevelIds 关序）。
- **枚举扫描断言（R2"无对话成就"裁定）**：ObjectiveMetric 词表 16 条全部是玩家行为事实（建塔/击杀/占领/无损/节奏），词表无 Dialog/Chat/Order/Persona/Trust 类成员；引用系统用量的两条（C4 不使用系统建造/C5 不使用卷轴）均为节制型 at_most——关闭局平凡满足、开启局不用即满足，**两态同可得，不锁任何一边**。
- **局内逐条授予见证**：四关首通成就全授（零托管参与 ⇒ T-081 钩子全放行）；四关判分全执行（survival ✓×4，实际完成数 2/2/3/3）；四箱全开（件数=档位表 4/4/5/5）；四节点全占。**⇒"关闭系统可全成就"（成就触发器无一依赖系统开启）成立**。
- 首证时点结果冻结为回归锚（Off_system_run_pins_the_first_proof_outcomes）：内容/平衡/种子漂移即红。c8 在本脚本局两关未达（首波有漏敌）——挑战目标是玩家技巧面（建塔时序/位形），与系统开关无关，扫描面不依赖系统即满足 R2 口径；实测调优归 T-090/T-095。

## 四、警报功能不缺失（照发+中性载荷）

- **事件照发计数>0**：关闭局全程真实读数评估流 706 次（四关 × 波界+20s 采样），通道未被开关静默；每一读数双开关位 `Fires` 判定全等（parity 违规=0——**任何完整度下开关位都不改变是否照发**）。
- **天然 fired=0 的如实记录**：当前 E1 引擎无敌方→建筑伤害路径，塔线完整度全程恒 1.0（integrityMin=1.0 钉住），天然局无危机可触发。危机路径的照发+载荷证据由**降级演练**补齐：完整度 100%→0% 按 5% 扫过同一决策核——恰 40% 不发（边界）、35% 及以下双态同刻照发、关闭态载荷恒为中性事实句 `防线完整度 N%`（正则钉形、无"建议"/无叹号/非人格台词），开启态=人格台词原文（对照锚）。
- 中性横幅+音效的 UI/音频面归 T-075（OffModeDowngrade 单测）与 T-092；本回归钉的是逻辑层（事件照发+中性文案）。

## 五、对照组等价 + 确定性

- **对照组（军师在场不用）**：input=0（零军令）、exec=0（零系统动作）、K-8 仅 60 条波界回充噪声（4 关波数 14+14+15+17）、点账本恒 0。与关闭局逐字段等价：**关卡进度**（四关同刻 Victory：115541/108462/101266/97733；EndState/WinWaves 全等）｜**资源入库**（逐关 Loot 行全等+战役池逐资源全等）｜**成就授予集合**（判分同抽同判、档位/开箱同物、首通同授、占领序同）｜终局状态哈希全等+采样哈希账本全等。**⇒"可选性不改变可达性"成立**。
- **确定性**：关闭局双跑——每关采样哈希账本（≥100 样本/关，防空账本假绿）全等、终局哈希全等、胜利刻/入库行/战役池/占领序全等。

## 六、发现与挂账

1. **e1.json 声明目标实例未被结算链读取**：TripleObjectiveJudge 按 Fnv64(关卡id) 自抽目标对，关卡行的 `objectives.strategy/challenge` 声明（s4/c8、s1/c3、s2/c1、s6/c7）只是内容声明面——判分面与声明面当前脱钩。本回归按生产行为（判定器自抽）断言并在用例内校验声明实例落池（Assert.Subset）。建议 C-19 后续接线时让 SettleVictory 读关卡行实例（或明确文档化自抽为唯一定义），届时本套件判分锚同步更新。
2. **c1 限时目标结构性不可达（内容面）**：L3 声明 c1（通关≤预算×80%=1200s）但满波排期本身 3375s（间隔 212s×13 波+宽限 371s）——预算/排期失配是 T-085 内容校准域（理论节奏估算器与实际 spawner 排期的口径差），实测口径归 T-095。本局判分面判定器未抽中 c1，不影响本回归结论，但内容侧应复核 budget_seconds 与波表的相容性。
3. **警报天然 fired=0（引擎面）**：敌方→建筑伤害（攻城锤 BuildingDamageMultiplier 等规格已备）未接入 Simulation——危机警报在 E1 实局无自然触发源。挂账给后续敌 AI/攻城任务：接入后本回归的 NaturalAlarmFired 断言（==0）应改为"照发计数>0"的天然观测。
4. **弹药断供审计为采样上界口径**：S4 判分输入是效率缺口累积（20s 采样），非断供事件精算；引擎当前无断供事件流暴露（HostRunCounters 文档已注明审计缝归 C-13/C-07 后续）。首证局实测 starved=0（补给链全程满效率），口径差不影响本局判分。
5. **性能**：套件三局共约 75s（每局约 23.8s、约 41.5 万 tick），纯确定性逻辑无墙钟断言；装置级 ICollectionFixture 三局共享，八用例复用同一装置实例。

## 七、完成门槛验证

- `dotnet test --filter FullyQualifiedName~OffSystemRegressionTests`：**8/8 通过**（装置 75s + 用例 <0.2s）。
- `bash tools/ci/run_all.sh`：**总体 PASS（G1~G4 全 PASS）**——实跑记录（2026-10-14）：G2 红线零违规（9 checks/0 FAIL）｜G2.5 资产规格 files=404 rejected=0｜G1 `dotnet test` **4524 passed, 0 failed**（196 s，含本套件 8 项）｜G3 Release `TreatWarningsAsErrors=true` 零警告（19 s）｜G4 SMOKE_OK 已捕获（17 s）。
- 单 commit：`T-084: off-system E1 full-clear regression (R2 hard standard first proof)`，仅含 `game/src/EraCore.Tests/Regression/OffSystemRegression/` 三文件 + 本 REV。

## 变更记录

- v1.0（2026-10-14）：T-084 首版交付（本文件）——R2 硬标准首证：关闭系统通关 E1 四关全成就可得、警报照发中性、对照组终态等价、双跑确定性全绿。
