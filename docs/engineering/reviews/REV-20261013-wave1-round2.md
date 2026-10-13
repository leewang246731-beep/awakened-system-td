# REV-20261013-wave1-round2 — Wave 1 批次 2（62495ff..4055c53 四提交）code-review 双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 1 批次 2 提交 `62495ff..4055c53`：1bf3d5c（T-039 C-10 units E1）、9608b67（T-038 C-09 towers/weapons E1）、8c8f368（T-033 C-08 E1 energy ledger）、4055c53（T-047 C-07 transporter A*/民夫档）（62495ff=批次 1 修复基线，不含在内） |
| 审查日期 | 2026-10-13 |
| 双轴 | Standards（CODE_STANDARDS/DOCUMENT 追溯纪律/06 质量门禁）× Spec（08-task-plan 任务卡验收 + 03 §6 契约清单原文 + 设计文档 v3.2 逐字锚点） |
| 处置执行 | 修复执行代理（单一 commit "Wave1 batch2 review fixes: damage unification + test hardening + spec provenance"）；6 项发现全部本轮修复，无挂账、无驳回 |
| 并行纪律 | Economy/ 与 presentation/、EraCore/Presentation/ 由其他代理并行撰写——本批未触碰（工作区未跟踪的 Presentation/ 文件不属于本 commit） |
| 验证 | §三全过（run_all.sh G1~G4 全 PASS；明细见下） |

## 一、发现总览

编号 #1~#6 对应协调方发现 F1~F6。分布：Standards 轴 4 项（#1/#2/#3/#5）、Spec 轴 1 项（#4）、流程留痕 1 项（#6）；#1（伤害公式双源）为最重要项。

## 二、逐项处置表（轴：S=Standards，P=Spec）

| # | 轴 | 发现 | 处置 | 落点 |
|---|---|---|---|---|
| 1 | S（伤害公式双源，最重要） | T-039 的 Unit.ApplyDamage 自带本地整数结算式 `max(d−甲, (d+9)/10)`，与 T-038 Combat.Damage 的浮点直伤分支构成同一公式的两份实现——后续任一侧演进（护盾/衰减入整数路径）都会静默分叉，且 Units 侧实现无法被 Combat 侧测试锁住 | 按 Standards 轴方案统一：①`EraCore/Combat/Damage.cs` 新增整数孪生入口 `DirectInt(int d, int armor)`（=max(d−armor, ceil(0.1×d))，ceil 以纯整数 `(d+9)/10` 保留；甲 clamp≥0；XML 注明"唯一权威整数入口，供确定性回放"）；②Unit.ApplyDamage 的本地公式实现删除，改为委托 `Damage.DirectInt`（保持 Units→Combat 单向，03-decomposition §3.1——已核 Combat 侧零 Units 引用）；③**分歧回归网格**：`DirectInt_matches_legacy_ceil_semantics_on_full_grid` 表驱动断言 d=1..30 × 甲=0..15 全 480 格与原 ceil 语义逐格相等——期望值用 `decimal` ceil 独立重算（与实现的整数路径互为漂移护栏），并重导出遗留式 `(d+9)/10`，任一侧漂移即红；另补 DirectInt 边界（甲>伤下限、负甲 clamp、负伤拒绝） | `game/src/EraCore/Combat/Damage.cs`（+DirectInt）；`game/src/EraCore/Units/Unit.cs`（删本地式改委托）；`game/src/EraCore.Tests/Combat/DamageTests.cs`（+480 格网格与 4 例边界/非法） |
| 2 | S（BossBattle 三处弱断言） | T-039 集成验收存在：①零塔用例 `Assert.Equal(0, outcome.ShotsFired)`/`Equal(0, TowersDestroyed)` 恒真（空列表 Sum 恒 0，红不了）；②单塔用例"~20 发"上界无推导（实测被践踏摧毁的塔只打 ~7 发，上界既松又无据）；③击杀用例缺伤害守恒断言（总有效伤害与 Boss HP 的对应无人证） | BattleOutcome 扩展三个可观测：`TotalDamage`（Σ ApplyDamage 结算返回）、`BossHpLost`（MaxHp−Hp，Boss 实收视角）、`TramplePulses`（践踏节拍格点计数——PulseTrample 对空目标列短路，零塔对照的节拍只能测试侧计，判据与引擎相位式同式）；①零塔用例改有意义断言：Boss 存活至到达（Killed=false）+基地被突破（ArrivedAtBase）+累计伤害=0（BossHpLost 口径，可红）+践踏次数>0（~1387 ticks÷45≈30 格点）；②射弹上界给全推导：塔(5,17)→px(176,560)、Boss 沿 x=208 恒速直行⇒横距 32；入射界（严格<）dy²<160²−32²=24576⇒|y−560|<156.77；y=16+0.75t⇒窗口 [517,934] 共 418 ticks；周期 30⇒至多 floor(418/30)+1=14 发（实测塔 HP120 被 78 伤/45t 践踏约 720t 摧毁⇒~7 发，14 为纯射界上界）；③伤害守恒上界精确断言：每发结算恒 4（d/甲固定）⇒总数=4×结算发数；937×4=3748<3750≤938×4=3752⇒击杀恰在第 938 发，区间断言 [3750, 3750+4]（初始 HP+单发溢杀上限口径）+等值断言 3752（总数∈4ℤ 使区间内唯一可达值）+BossHpLost=3750（0 夹不掩盖守恒） | `game/src/EraCore.Tests/Units/BossBattleIntegrationTests.cs`（BattleOutcome 扩展+三用例断言重写） |
| 3 | S（EnergyNodeId 两测纯 record 复述） | T-033 的 `Value_round_trips`/`Works_as_dictionary_key` 为语言保证复述（record 值相等/字典键），无失败模式——弱测 | 选"替换为有意义的边界"路线（合规性核实：ci-allow: T3 豁免路线只能压 T3 告警、无法把方法移出关联计数分母——类级 [C-08] 标签已使全类计入，扫描器无"计数排除"机制，故弃）：①`Value_round_trips`→`Negative_and_extreme_values_construct_legally_and_stay_distinct`（非法负值构造行为=原语层不拒绝、原样保留、与 0/极值互异——opaque 契约，域警察在签发方）；②`Works_as_dictionary_key`→`Mixed_sign_ids_key_one_dictionary_independently`（负值/0/正值/long.MinValue 混合单命名空间独立成键，对应账本生产者/消费者/虚拟节点同册） | `game/src/EraCore.Tests/Energy/EnergyNodeIdTests.cs`（两测替换，测试数不变） |
| 4 | P（粮仓满溢 spec 溯源断裂） | 设计文档能源阶梯行的 E1 风险事件全称（v2.1 基线 §4.2 原文含损耗机理注记）在 v3.0 重写时被压丢——E1 行只剩"人力"，风险事件从 E3 起列，导致 SP-4 与 era1.json 注解读作"E1 无风险事件"，与 T-033 已交付的 E1 粮仓满溢事件（GranaryOverflowEvent）出处断裂 | 溯源恢复：①设计文档 §4 能源阶梯行恢复完整措辞"**粮仓满溢（鼠患/霉变损耗食物）**"并注明承接 v2.1 基线 §4.2 E1 行；文头版本升 v3.2.2、变更记录加溯源行（出处=Spec 轴审查）；②GranaryOverflowEvent.cs 头注释出处同步（v3.2 §4.2 E1 行），并澄清 era1.json `energy.risk_events=false` 仅指 E3+ 负载率类阶梯风险（SP-4 L142）、E1 食物损耗事件照常生效；③一致性收尾：SP-4 适用代句与 era1.json `_source` 注解同步区分两类风险（消除"E1 无风险事件"误读） | `docs/我在游戏觉醒系统-游戏设计方案.md`（§4 行/版本行/变更记录 v3.2.2）；`game/src/EraCore/Energy/GranaryOverflowEvent.cs`（头注释）；`docs/engineering/11-spec-tables.md`（SP-4 适用代句）；`game/content/era1/era.json`（`_source` 注解） |
| 5 | S（吞吐锚点距离口径未冻结） | T-047 Carrier 的 0.3 件/s 锚点由 160px 参考距往返周期推出（吞吐=容量/往返周期，距离敏感——行程随距离伸缩、服务窗不随），但代码注释与 T-064 验收行均未声明该口径——六档验收若各档异距测，档间比较无意义 | 口径冻结：①Carrier.cs 文档注释明确"0.3 件/s 锚点仅在参考距离 160px 单程成立；吞吐=容量/往返周期、距离敏感；T-064 六档验收须以 160px 参考距统一口径（同距比较）"；②08-task-plan T-064 行尾加"[口径：吞吐锚点参考距离=160px 冻结（batch2 审查裁定），六档同距比较]"，变更记录升 v1.2 | `game/src/EraCore/Logistics/Carrier.cs`（Tier anchor 段）；`docs/engineering/08-task-plan.md`（T-064 行+变更记录 v1.2） |
| 6 | S（审查留痕） | 批次 2 双轴审查发现与处置须按 G5 流程留档（06 质量门禁；round 1 先例 REV-20261013-wave1-round1） | 本文档：两轴发现总览+逐项处置表+完成门槛真实输出；无代码变更 | `docs/engineering/reviews/REV-20261013-wave1-round2.md`（本文） |

## 三、完成门槛验证（全部真实执行，输出原文摘录）

① **全量测试**（`export NUGET_PACKAGES='D:\nuget' DOTNET_ROOT=/d/dotnet`；`dotnet test game/EraTD.sln`）：

```
已通过! - 失败:     0，通过:  1362，已跳过:     0，总计:  1362，持续时间: 3 s - EraCore.Tests.dll (net8.0)
```

（含并行代理工作区未跟踪的 Presentation/ 测试与本批新增：伤害网格 480 格+DirectInt 边界 4 例；EnergyNodeId/BossBattle 为替换/收紧不净增。定向复核：`--filter FullyQualifiedName~DirectInt|~BossBattle|~EnergyNodeId` 491/491 绿；网格单测 480/480 绿。红线 T3 关联 519/519 方法 100%，含本轮全部新增/替换测试。）

② **本地流水线**（`bash tools/ci/run_all.sh`，含 Godot headless 冒烟；真实输出摘录）：

```
==================== 门禁汇总（G1~G4 ↔ docs/engineering/13-quality-gates.md）====================
  G1   测试全绿   : PASS  dotnet test: 1362 passed, 0 failed (9s)
  G2   红线零违规 : PASS  7 checks | 0 FAIL | 0 WARN | exit_code=0
  G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，3s）
  G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，9s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

## 四、遗留与挂账

- 无新增挂账。既有挂账不变：Q11（08 §6，v2.1"57s/107s"口径追回）、DEBT（T-038 已于批次 2 清账）、T-045 帧数自动门禁。
- 并行纪律：本批未触碰 Economy/、presentation/、EraCore/Presentation/（其他代理撰写中）；上述路径若在本 commit 出现即违纪。
