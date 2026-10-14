# REV-20261014-wave3-round1 — Wave 3 批 1（1868ecd..be5dc98 六提交）双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 3 批 1 六提交：184e858（T-072 LlmGateway）/ dbaa80d（T-066 单位全量）/ d2fef8a（T-072 在线测试加固）/ 2754bf4（T-065 塔全量）/ c748114（T-069 文法 12 意图全量）/ be5dc98（T-064 物流全量） |
| 审查日期 | 2026-10-14（双轴后置审查 + 本处置 commit 同日落盘） |
| 双轴 | Standards（CODE_STANDARDS CS-2/CS-6/CS-7 逐条）× Spec（08 §Wave3 任务卡验收 + 11-spec-tables 冻结表口径 + v3.2 设计方案锚点实存性） |
| 处置执行 | W3 批 1 修复代理（单一 commit "W3 batch1 review fixes: baseline report sync, R7 registrations, plan markers, misc"）；#1/#2 为 MAJOR 级裁定，#3 为 Spec 勘误，#4/#5 为补账/收尾，#6 为跨泳道协调增补（T-067 落库后） |
| 验证 | run_all.sh 全绿（G2/G2.5/G1/G3/G4 真实输出摘录见 §四）；R7 登记行与锚点集快照见 §二 |

## 一、发现总览

| # | 轴 | 级别 | 发现 |
|---|---|---|---|
| 1 | S（CS-6 语料版金样纪律：钉死值变更须同步报告并过审） | **MAJOR** | T-069 把语料基线 296/324（91.36%）重钉为 324/324（100%）并重钉 CorpusV1BaselineTests，但 `docs/engineering/16-t061-corpus-baseline.md` 未同步——T-069 提交侧自声明"报告 §3/§6.1 数字同步归 F 泳道"，报告与测试钉死值分叉（测试=100%、报告=91.36%），纪律要求的"同步更新语料与本报告并过审"未闭合 |
| 2 | S（CS-7.1/D-18：豁免必须在审查记录中引用） | **MAJOR** | 批内新增 10 处 `// ci-allow: R7` 行级豁免（T-064 档位表 4 / T-072 两处 / T-066 盾框架 4）未在审查记录中登记——缺登记则豁免不可审查（与 W2 批 1 REV-20261014-wave2-round1 #1 同一裁定路线） |
| 3 | P（v3.2 锚点实存性） | Spec 勘误 | T-065 出售返还注释引"v3.2 §11.4 拆除返还 30%"为**悬空引用**：v3.2 §11.4 实为"权限/钩子/信任度"，全文无"拆除返还"锚点（修正表 #28"残骸修复成本×30%×0.8"为另一语义，且是 Checkpoint 残骸修复不是出售返还）——产品码 3 处+测试 2 处注释同病 |
| 4 | P/S（计划标记账实不符） | MINOR（三件） | ①T-029/T-030 实际已在 W0 修复轮交付（tools/ci/smoke_godot.sh+09-env-locked.md 成文）但 08 表两行未标 RESOLVED；②T-070 分发臂/构造注入 Shotgun Surgery（19 动作接线时 ImplementedKinds/构造器/HasLiveHandler/switch 四处同改）经审查确认但未入 T-070 行尾 DEBT；③ActionRegistry"the other 13 verbs"与 ActionTestSupport.NotImplementedVerbs"15 verbs"两处注释计数口径分裂且未注明差异原因 |
| 5 | P（11-spec-tables SP-2 vs 实现） | MINOR（W2 遗留收尾） | SP-2 配套口径行停留在 T-052 五意图子集时代——T-069 后 12 意图全集已实现（句式 293 条、基线 100%），状态行滞后 |
| 6 | S（RL9 扫描 vs 二开研究交付物，跨泳道协调项） | 增补处置 | 二开研究报告/决议基座含 E:\game-assets、E:\game-videos 等盘符路径——性质=甲方 U 盘素材库指令的合法引用（对齐 09-env-locked 环境事实先例），非代码侧硬编码缺陷；但 18-r1-asset-tech.md（12 处）与 21-secondary-dev-base.md（7 处）未登记豁免致 G2 R9 红，且 18/19/20 三份研究报告未入库（未跟踪） |

## 二、R7 豁免登记（处置 #2 主项；行号=be5dc98 时点产品码）

豁免性质（延续 REV-20261014-wave2-round1 §二的裁定路线）：**全部为文档化域常量**（吞吐档位量纲/超时量纲/日志条数量纲/护盾公式结构常量/百分比量纲上界），冻结出处随行标注任务 ID 与依据，非无据魔数；0 处改注册表（均非 game/content 注册表管辖的内容平衡数值——物流档位参数属 T-064 任务卡口径+v3.2 §5 吞吐锚点的工程分解，护盾常量属 v3.2 §6 公式结构）。锚点集现状（16 值，era1 内容 ≥10 整数扣白名单 {10,30}）：15/20/40/45/50/60/70/80/90/100/120/150/220/2500/280/800。

### 2.1 TransportTier.cs（4 处——T-064 六档吞吐档位表，v3.2 §5 阶梯锚点的工程参数化）

| 行 | 档位 | 常量值 | 冻结依据 |
|---|---|---|---|
| L135 | 马车档（tier 2） | 4, 3.5, 54, 54 | v3.2 §5 阶梯第 2 档：容量 4 件、速度 3.5 px/tick 为任务卡口径；54+54t 对称服务窗=0.6 件/s 锚点@160px 的整数窗原型校准（精确 0.6 不可达，0.6017 落 ±5% 验收带内，行内注释含推导） |
| L147 | 矿轨档（tier 3） | 20, 16.0, 20, 20 | v3.2 §5 阶梯第 3 档 10 件/s 锚点@160px：容量 20 件、速度 16 px/tick、20+20t 服务窗，RT=2×160/16+40=60t 精确锚点（行内注释含断轨降速推导） |
| L158 | 传送带档（tier 4） | 15.0 | v3.2 §5 阶梯第 4 档 15 件/s 锚点（0.5 件/tick×30Hz 长度无关稳态流）；浮点常量表面化锚点数值，供档位表断言直接对照（自文档化标注） |
| L167 | 无人机档（tier 5） | 20, 20.0, 7, 7 | v3.2 §5 阶梯第 5 档 20 件/s 锚点@160px：容量 20 件、速度 20 px/tick、7+7t 服务窗，RT=2×160/20+14=30t 精确锚点 |

### 2.2 Llm 网关（2 处——T-072，提交 184e858）

| 行 | 常量 | 值 | 冻结依据 |
|---|---|---|---|
| LlmGateway.cs L51 | DefaultFirstByteTimeout | 15 s | v3.2 §11.7 双超时"首字节 10~15s"取上限（调用工程域常量，时间量纲，非平衡数值） |
| LlmConfig.cs L150 | LogRetention | 50 | v3.2 §11.6 llm.config.json 模板缺省值 log_retention=50（BYOK schema 域常量，条数量纲，非平衡数值） |

### 2.3 盾框架（4 处——T-066，提交 dbaa80d）

| 行 | 常量 | 值 | 冻结依据 |
|---|---|---|---|
| ShieldState.cs L30 | RegenPointsPerSecond | 20 | v3.2 §6 冻结域常量：破盾 5 s 后 20/s 再生（公式结构常量，非内容表数值——盾量初值由内容/波公式注入，RL7/CS-2.3 引擎不自造） |
| ShieldState.cs L33 | BreakDelayTicks | 150 | v3.2 §6 冻结域常量：破盾 5 s 后再生启动（5 s×30 Hz K-1 tick 域换算，时间量纲） |
| ShieldState.cs L36 | PierceInterruptTicks | 90 | T-066 任务卡 [NEW-原型校准]：穿甲弹命中打断再生 3 s（3 s×30 Hz；无 v3.2 锚点，任务卡口径随行标注） |
| Unit.cs L437 | MaxSlowPercent | 100 | 百分比量纲上界（slow=(0,100%] 区间的数学上界，量纲常量非平衡数值） |

### 2.4 承重核验（实测，锚点集快照=本 REV 登记时点）

对锚点集逐值核验：**承重行**（去标记即红）=8 处——TransportTier L147/L167（20 命中锚点集）、LlmGateway L51（15 命中）、LlmConfig L150（50 命中）、ShieldState L30/L33/L36（20/150/90 命中）、Unit L437（100 命中）；**防御性标注**（现行扫描口径下永不触发）=2 处——TransportTier L135（4/54 均不在锚点集、3.5 为浮点不入整型扫描）、L158（15.0 浮点字面量，`R7_INT_LITERAL_RE` 对小数片段前后锚 `[\w.]` 排除，永不匹配）——保留不删（D-18 可审查性由标记文本自身承担，且防锚点集/检查口径变化；内容 JSON 扩容后以扫描器实时判定为准）。累计批 1 新增 `ci-allow: R7` 标记行=10，与 grep 实测一致。

## 三、逐项处置表

| # | 裁决与处置 | 落点 |
|---|---|---|
| 1 | **基线报告同步（MAJOR）**：16-t061 升版 v1.1，新增 §7"基线 v2（2026-10-14，T-069 后）"——重钉数字 324/324=100%（zh 162/en 162）、28 miss 全修复按 v1 §3.4 原分类归零（句式 21/词典 4/容错 3，容错含 §5 FuzzyRepair 两例+camfire 一例）、70 缺口行口径切换说明（gap_intent 注解转解析成功+SP-2 槽位键校验、语料历史注解保留、subset_gap 退役）；§3/§4/§5 归档为 T-061 时点历史基线，原 §7/§8 顺延 §8/§9；升版记录入变更记录 | `docs/engineering/16-t061-corpus-baseline.md`（v1.1） |
| 2 | **R7 豁免补登记（MAJOR）**：10 处豁免逐文件逐行登记于本 REV §二（CS-7.1"豁免必须在审查记录中引用"自此闭合）；豁免性质=文档化域常量，0 处改注册表；承重 8+防御 2 实测核验记入 §2.4 | 本文档 §二（登记处）；豁免行本身 184e858/dbaa80d/be5dc98 已带 `// ci-allow: R7 <任务ID 依据>` |
| 3 | **悬空引用勘误（Spec）**："v3.2 §11.4 拆除返还 30%"改为"[NEW-T-065] 无文档锚点，登记于 REV-wave3-round1"——产品码 3 处（TowerEstateOps.cs 两处+SellTowerAction.cs 一处）+测试 2 处（TowerEstateOpsTests.cs 两处，其一为同病 §11.4 引用）；**只改注释，SellRefundFraction=0.30 数值与断言零改动**（数值本身审查无异议，异议仅在出处标注） | `game/src/EraCore/Combat/TowerEstateOps.cs`、`SellTowerAction.cs`、`game/src/EraCore.Tests/Combat/TowerEstateOpsTests.cs`（均注释行） |
| 4 | **计划标记补账（MINOR 三件）**：①T-029/T-030 行尾补 `[RESOLVED-20261014: 冒烟/环境登记于 W0 修复轮交付（smoke_godot.sh+09-env-locked.md），补记]`；②T-070 行尾 DEBT 扩入分发臂/构造注入单源化（19 动作接线时 ImplementedKinds/构造器/HasLiveHandler/switch 四处同改的 Shotgun Surgery——审查确认），与 v1.4 四件套 DEBT 并列；③ActionRegistry/ActionTestSupport 两处计数注释统一同一口径：13=19−4 常开 handler−2 estate 条件档（T-065 接线后转真），未接线注册表=15（13+2 estate 对）——差异原因随两处注释互见注明，仅注释零行为变更。**落点说明（并行竞态）**：①②的 08-task-plan.md 改动被并行泳道 commit 5af3e2e（secondary-dev base doc）先行裹挟入库（内容逐字一致，该 commit 复核确认），其上另有该泳道自加的 v1.7 次级开发任务钩子；③随本修复 commit 落地 | `docs/engineering/08-task-plan.md`（v1.6，经 5af3e2e 入库）；`game/src/EraCore/Agent/Actions/ActionRegistry.cs`、`game/src/EraCore.Tests/Agent/Actions/ActionTestSupport.cs`（注释，本 commit） |
| 5 | **SP-2 状态收尾（W2 Spec MINOR）**：SP-2 配套口径行补 T-069 实现状态——12 意图全集已实现（L1IntentParser.SupportedIntents=12、句式 293 条=zh 149+en 144、每意图每语言 ≥10，"句式变体 ≥120"超额兑现），语料基线 324/324=100%（16-t061 §7 基线 v2）；"E1 高频子集 5 意图"转历史口径、subset_gap 机制退役；变更记录 v1.3 | `docs/engineering/11-spec-tables.md`（v1.3） |
| 6 | **二开研究文档 R9 处置（跨泳道增补）**：①三份未跟踪研究报告（18-r1-asset-tech/19-r2-easter-eggs/20-r3-essence-economy）git add 入库（R1/R2/R3 代理正式交付物，docs/engineering/ 文档泳道）；②allowlist.txt 四条 R9 文件级豁免（18/19/20/21——U 盘素材库路径为甲方指令的设计事实非硬编码缺陷，19/20 现无盘符路径属预防性登记；代码侧 RL9 不变）；③复跑 redline_scan `9 checks \| 0 FAIL \| 0 WARN` | `tools/ci/allowlist.txt`；`docs/engineering/18/19/20` 三文件入库（随本 commit） |

## 四、完成门槛验证（真实执行，摘录）

- `bash tools/ci/run_all.sh`（NUGET_PACKAGES/DOTNET_ROOT 按 09-env-locked）：**总体 PASS（G1~G4 全 PASS）**——G2 红线扫描 `9 checks | 0 FAIL | 0 WARN`（R7 注记 16 锚点 | 193 产品文件已扫；T8 注记 4 金样 commit | 3 首版豁免——T-067 首版金样 8488523 已登记；T3 1468/1468 方法已关联 100%）；G2.5 资产校验 `files=140 rejected=0 warned=0`；G1 dotnet test **3236 passed, 0 failed**（116 s）；G3 Release 构建 TreatWarningsAsErrors=true 零警告（8 s）；G4 Godot 冒烟 SMOKE_OK（13 s，运行时/程序集/EraCore 链路三合一）。
- R7 专项复核：批 1 十处 `ci-allow: R7`（TransportTier 4+Llm 2+ShieldState 3+Unit 1）全部在案且逐行登记于 §二；锚点集快照 16 值与 §2.4 核验一致。

## 五、遗留与挂账

- 本轮新增挂账=处置 #4②（T-070 行尾 DEBT 扩入：分发臂/构造注入单源化——Shotgun Surgery 四处同改面，与 v1.4 单源化四件套一并收口；过渡期防线=ActionMetadataLockstepTests 既有 lockstep 守卫，分发臂单源化后转为其回归基线）。
- R7 十处豁免登记属登记事实非挂账（豁免理由随行可审；§2.4 承重核验为登记时点锚点集快照，内容 JSON 扩容后以扫描器实时判定为准）。
- 既有挂账不变：T-053 TowerMagazine 双适配器、T-070 v1.4 四件套（08 v1.3/v1.4 累计）、Q11（08 §6）、G7/G8 待启用（13-quality-gates §1，其中 G8 矩阵已随 T-069 部分落地）。
