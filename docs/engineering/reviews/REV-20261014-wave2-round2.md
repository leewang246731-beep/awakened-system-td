# REV-20261014-wave2-round2 — Wave 2 批 3 修复（根因+批 2 遗留+审查增补 F1~F8）发现与处置

| 项 | 内容 |
|---|---|
| 处置对象 | Wave 2 批 3 修复任务 F1~F4（run_all 两红根因+批 2 遗留×2）+ 批 3 Standards 轴审查增补 F5~F8（审查代理产出、经协调方转达收录） |
| 日期 | 2026-10-14（修复执行 + 本登记同批落盘） |
| 双轴 | Standards（CODE_STANDARDS 逐条）× Spec（08 任务卡验收 + 设计 v3.2 + 11-spec-tables 冻结表口径）——**批 3 双轴完整审查报告尚待审查代理落盘，本文件不代拟审查结论**（F5~F8 为已转达的 Standards 轴增补发现，逐条如下；其余轴的完整发现/级别计数以审查代理后续 REV/报告为准，届时补录引用） |
| 处置执行 | Wave 2 批 3 修复代理（单一 commit "W2 batch3 fixes: perf-test serial collection (root cause), sim lambda ordering, ACL risk doc, misc"） |
| 验证 | `bash tools/ci/run_all.sh` 连续两次全绿（真实输出摘录见 §四；两次均 G1~G4 全 PASS） |

## 一、发现总览（F1~F8）

| # | 轴 | 级别 | 发现 | 处置 |
|---|---|---|---|---|
| F1 | Q（G1 稳定性，根因） | **MAJOR** | 今日 run_all 两红根因：Spike1/Spike2 与 FlowFieldManagerTests（TierA 性能用例）无 [Collection] 标注，xUnit v2 按类各建默认集合、集合间并行——计时敏感测试互相偷 CPU 墙钟（T-043 回归线 1.336×2.25 ms 的并发劣化容差按单邻居负载设计；REV-20261014-wave2-round1 §四已留档过一次并发超限） | ✅ 新建共享独占串行集合 perf-serial（§三-F1） |
| F2 | S（CS 一致性，批 2 遗留-1） | MAJOR | Simulation.ApplyBuildTower lambda 注册顺序：`_towers.Add` 先于 RegisterTower/_pacersById——注册段中途抛白名单异常时退费但留半注册（`_towers` 有塔、`_pacersById` 无 pacer），后续 Step 抛 KeyNotFoundException、registry 占用格泄漏 | ✅ 顺序重排+registry 回滚（§三-F2） |
| F3① | S（C-17/T-054 残余风险登记，批 2 遗留-2） | 登记 | T-054 文件回落后端 ACL 收紧为尽力而为：拒绝 ACL 编辑的文件系统上仅 Warn、明文保留 | §二专项登记 |
| F3② | Q（测试强度，批 2 遗留-3） | MINOR | Compose_truncates_overlong_summary 仅断言长度 200——截错位置/丢字/补字放不过 | ✅ 补断言截断内容=原文前 200 字符前缀（§三-F3） |
| F5 | S（K-8 契约，批 3 审查增补） | MAJOR | BuildInteractionUi 给 PlayerBuildLane 传裸 InMemoryJournalWriter——违反泳道 journal 契约（ctor 参数注释：sequence-ordered per run，由 SequencingJournalWriter 重编号 Seq），玩家事件 Seq=0 恒不递增 | ✅ 双侧换 SequencingJournalWriter 包装+递增断言（§三-F5） |
| F6 | S/P（章节引用纠偏，批 3 审查增补） | MINOR（3 处） | CampaignGrid 三处"v3.2 §7 六边形"引用失锚（v3.2 无六边形几何定义，实际出处=11-spec-tables SP-9 L271）；PlayerBuildLane 头部"系统点限军师"为引号简称、裁定依据未展开 | ✅ 引用改指 SP-9 L271；裁定结构化并正式登记于 §三-F6 |
| F7 | S（措辞与事实一致性，批 3 审查增补） | MINOR | DingSynth 注释"same bytes on every machine"过强：Math.Sin/Exp 跨架构无 IEEE 位级保证 | ✅ 降级为"同机确定；跨机以 WAV 落盘快照为准"（§三-F7） |
| F8 | P（Spec 轴，批 3 审查增补） | MINOR（窄化未挂账） | HUD 第三指标窄化：设计 §10.2/F-111 要求"波次构成分析"，T-055 薄版落为波次号+在场塔数，窄化当时未挂账 | 登记并挂 T-075（§三-F8 + 08 v1.5） |

## 二、T-054 ACL 残余风险登记（F3① 专项）

**事实**（`game/src/EraCore/Secrets/FileSecretStore.cs` 类注释与实现，T-054/C-17/F-084）：
- 文件回落后端在**每次写入后**尽力收紧权限——Windows：关继承+仅当前用户 ACL；Unix：chmod 0600；
- 收紧失败**仅记 Warn 并保留文件**（明文 secrets.bin 留在盘上）——设计选择"丢密钥"重于"权限降级"（0600 尽力语义，方案 §11.6 回落）；
- 后端选择（SecretStoreFactory）：DPAPI 探测通过即用 DPAPI，文件回落仅在 DPAPI 不可用时发生（回落原因经 ILogSink Warn 具名）。

**残余风险**：DPAPI 不可用（非 Windows/受限会话）**且**文件系统拒绝 ACL 编辑（如某些网络盘/FAT 系/挂载选项）叠加时，provider→secret 明文映射以默认权限（可能组/全局可读）存续。风险双门叠加才成立，单门不触发；但两门皆非用户可控可感知（回落 Warn 在日志、ACL 失败 Warn 亦在日志）。

**缓解建议**（按成本递增，供后续任务卡引用）：
1. **首选路径**（零代码）：DPAPI 可用的部署不用文件回落——SecretStoreFactory 已按此序选择；运维基线将"secret-store factory=select backend=file-fallback" Warn 列为告警项。
2. 启动期探测：回落到文件后端时立即做一次探测写入+读回 ACL 校验，ACL 收紧失败升级为显著告警（而非逐次 Warn），提示该机密存储不满足 §11.6 分级预期。
3. 结构性收口（远期）：BYOK 密钥不走文件回落（§11.6 分级内 Key 属"OS 凭据管理器优先"档），或回落实现改用用户级加密容器。

不另开任务卡：与 C-26 BYOK 设置界面（T-077，方案 §11.6 的宿主侧）自然交汇，届时按上面 1→2→3 顺序落。

## 三、逐项处置表

| # | 裁决与处置 | 落点 |
|---|---|---|
| F1 | **性能测试独占串行集合（根因修复）**：新建 `PerfSerialCollection`（`[CollectionDefinition(DisableParallelization=true)]`），Spike1PerformanceTests/Spike2CoexistenceTests/FlowFieldManagerTests 整类加入。xUnit v2 官方语义：DisableParallelization 集合"guaranteed not to run in parallel against any other test"——比普通集合（仅类内串行、集合间仍并行）更强，性能中位数不再与任何邻居共抢 CPU。**Checkpoint 冲突读码裁定**：一个类只能属一个集合，但 CheckpointHeavyCollection 类与 perf 类互不相交，无归属冲突；perf-serial 的独占性已覆盖"Checkpoint 重负载与性能用例互扰"（独占=不与任何并行集合重叠，含 CheckpointHeavy），故 **Checkpoint 四类保持原状不并入**（避免巨型串行集合拉长墙钟）。FlowFieldManagerTests 整类进（取舍：非计时逻辑用例皆小图秒级，串行代价可忽略；xUnit 集合粒度=类）。Stopwatch 排查（grep Tests 目录）：另命中 FlowFieldTests——仅注释提及，切片等价用例断言预算机制（frames≥2）与逐位哈希，非墙钟数值，并行负载只会增加帧数不会翻转断言，**不纳入**（依据随行注明） | 新增 `game/src/EraCore.Tests/PerfSerialCollection.cs`；三类加 `[Collection(PerfSerialCollection.Name)]`（取舍与裁定见该文件注释） |
| F2 | **建造命令 lambda 半注册（先注册后入账+占用回滚，两者并用=最小且正确）**：纯重排仍留 registry 占用泄漏（Place 先于抛点），纯 try-catch 不重排则回滚面覆盖全部七处突变——故取"全部会抛白名单异常的子系统注册（WeaponFire 构造/RegisterTower/SuppliedWeaponFire 构造/弹匣构造）先行，提交段（List.Add/新鲜 id 的 Dictionary.Add/定宽 3×3 脏区）不抛，catch 仅回滚 `_registry.Remove(id)`+`_magazineById.Remove(id)` 后重抛"。RegisterTower 全部校验先于自身变更 ⇒ 拒绝时监控器无变更，无需撤销；退费仍由 BuildCostCompensation 补偿窗负责（拒绝码 `place-failed-cost-refunded:ArgumentException`）。**异常路径测试**：毒化目录项（ReloadTicks 合法过预检、AmmoPerShot=-1 触发 RegisterTower 白名单拒）→ 断言退费至初始 100、拒绝码具名、零残留（Towers 空/Buildings 仅初始条目/后续 Run(300) 不抛 KeyNotFoundException——修复前必抛）、占用格不泄漏（同格改用合法塔重下命令 Ok 落子） | `game/src/EraCore/Sim/Simulation.cs` ApplyBuildTower；`game/src/EraCore.Tests/Sim/SimulationTests.cs` 新用例 Refunded_tower_rejection_leaves_no_half_registration |
| F3① | ACL 残余风险登记（本 REV §二）：T-054 文件回落"ACL 收紧失败仅 Warn、明文保留"的残余风险+三级缓解建议（首选=DPAPI 可用即不走文件回落） | 本文档 §二 |
| F3② | 截断测试加固：Compose_truncates_overlong_summary 补 `Assert.Equal(overlongReport[..200], report.Summary)`（内容=原文前 200 字符原样前缀，非仅长度）——与 AgentReporter 实现（`summary = summary[..SummaryMaxLength]` 纯前缀切片）逐字符对齐 | `game/src/EraCore.Tests/Agent/Pipeline/AgentReporterTests.cs` |
| F5 | **玩家事件 Seq 严格递增（K-8 单流有序）**：生产侧 BuildInteractionUi.SetupCore 改传 `new SequencingJournalWriter(_journal)`（内存 sink 作 inner，冒烟计数不变；BUILD_OK 行新增 player_journal_last_seq 观测位）；测试侧 PlayerBuildLaneTests.MakeLane 同形包装；新用例 Player_events_carry_strictly_increasing_seq 断言玩家事件 Seq=[1,2,3] 严格递增（按 Actor=player 载荷形状过滤——军师/账本载荷缺 Actor 字段，STJ 可空绑定不可作为判据）。**注意**：AgentPipeline 内部本就自包装 sequencer（PipelineJournal），本修复不触碰军师侧 | `game/presentation/EraTD/scripts/BuildInteractionUi.cs`；`game/src/EraCore.Tests/Interaction/PlayerBuildLaneTests.cs` |
| F6 | **章节引用纠偏（3 处）+裁定结构化**：CampaignGridOptions/CampaignGrid 类摘要/NeighbourOffsets 三处"v3.2 §7 六边形"改指 **11-spec-tables SP-9 L271**（"节点置于六边形网格；邻接=六边形 6 邻"——六邻接口径实际出处；v3.2 §7 无六边形几何定义）；PlayerBuildLane §6.1 引用精化为 **v3.2 §6（塔防战斗系统末条 L82）**（同款句实际所在）。**"系统点限军师"引号简称改结构化裁定并正式登记如下**——玩家手动建造不消耗系统点、不出方案卡（拖放松手=玩家预授权 allow 直过），依据三段：① v3.2 §11.1 不变式③「预算限量：系统点数」是游戏内智能体（"系统"）三条不变式之一，点数预算主体=军师；② v3.2 §11.4 计价表（建造 2/战斗 1/物流 1/巡逻 1/侦察 0）是军师动作计价，玩家手势非计价对象；③ 结构事实：AgentPipeline.ExecuteSteps 将 TrySpend（点数扣减）与 ApprovalGate（方案卡审批）包在 PipelineExecutor.InvokeStep **之外**的包装段，玩家道直调 InvokeStep——点数与审批位于军师管线包装层、不在共享咽喉内。校验/执行/战报三段仍与军师同源（同一 InvokeStep/ActionRegistry/SimCommand） | `game/src/EraCore/CampaignLoop/CampaignGrid.cs`；`game/src/EraCore/Agent/Actions/PlayerBuildLane.cs`（文件头+PlayerBuildJournalPayload.PointsSpent 注释+类摘要）；`PlayerBuildLaneTests.cs` 注释同步 |
| F7 | DingSynth 类摘要措辞降级：删"same bytes on every machine"，改为 same-machine deterministic——同层 ⇒ 同机同字节；Math.Sin/Exp 跨架构无 IEEE 位级保证，不作跨机逐字节承诺；跨机一致性以落盘 WAV 快照为准（DingSynthTests 同跑逐字节回归+可听测锚点） | `game/src/EraCore/Audio/DingSynth.cs` 类摘要 |
| F8 | **HUD 第三指标窄化登记（Spec MINOR，补挂账）**：设计 §10.2/F-111 口径="常驻 HUD 显示产出率/防线完整度/波次构成分析"；T-055 薄版第三指标落为波次号+在场塔数（SystemPanel `波次 W00`/`防线 100%(4塔)`）——窄化合理（薄版无逐波构成数据源）但当时未挂账。现补挂 **T-075**（C-22 全量面板，Wave 3 D 组）恢复全口径 | `docs/engineering/08-task-plan.md` T-075 行尾 DEBT+变更记录 v1.5 |

## 四、完成门槛验证（真实执行，连续两次全绿）

`bash tools/ci/run_all.sh`（NUGET_PACKAGES='D:\nuget'、DOTNET_ROOT=/d/dotnet，09-env-locked 口径）——修复即稳定性修复，连续两次全量全绿为出口条件：

**第 1 次**（连续序列第 1 跑）：

```
  G2   红线零违规 : PASS  9 checks | 0 FAIL | 0 WARN | exit_code=0
  G2.5 资产规格校验 : PASS  files=140 rejected=0 warned=0 allowed_sizes=[16, 32, 48, 64] max_palette=64 allow_oversized_palette=False expect_frames=None
  G1   测试全绿   : PASS  dotnet test: 2479 passed, 0 failed (56s)
  G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，3s）
  G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，8s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

**第 2 次**（紧随其后的连续跑，树零改动）：

```
  G2   红线零违规 : PASS  9 checks | 0 FAIL | 0 WARN | exit_code=0
  G2.5 资产规格校验 : PASS  files=140 rejected=0 warned=0 allowed_sizes=[16, 32, 48, 64] max_palette=64 allow_oversized_palette=False expect_frames=None
  G1   测试全绿   : PASS  dotnet test: 2479 passed, 0 failed (55s)
  G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，1s）
  G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，7s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

专项佐证（真实输出行）：
- G1 计 2479 用例含本批新增 2 例（Refunded_tower_rejection_leaves_no_half_registration / Player_events_carry_strictly_increasing_seq）与加固 1 例（Compose_truncates_overlong_summary——原用例补断言，不增计数）；
- G4 冒烟 BUILD_OK 行（修复后同码补跑捕获）：`BUILD_OK ... player_journal_events=4 player_journal_last_seq=4 ...`——玩家流 4 事件、末位 Seq=4（F5 生效：Seq 1..4 严格递增，修复前恒 Seq=0）；
- F1 生效形态：G1 两次均 55~56s 全绿（此前两红为 spike1/spike2 墙钟超限；perf-serial 独占串行后计时不再受并行邻居干扰——xUnit v2 独占集合语义见 PerfSerialCollection.cs 注释引用）。

## 五、遗留与挂账

- **批 3 双轴完整审查报告待补录**：本 REV 收录的 F5~F8 为审查代理 Standards 轴增补发现（经协调方转达），完整双轴报告（含 Spec 轴全量对照、级别计数、审查对象 commit 区间）落盘后在此补引用——**本文件不代拟该结论**。
- 新增挂账：F8 → T-075 行尾 DEBT（HUD 第三指标恢复"波次构成分析"全口径，08 v1.5）。
- F3① ACL 残余风险为登记非挂账：缓解路径与 C-26/T-077 交汇（§二）。
- 既有挂账不变：T-070 四件套（08 v1.4）、T-053/spike 系（08 v1.3）、Q11（08 §6）、G7/G8 待启用（13-quality-gates §1）。
