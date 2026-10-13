# REV-20261014-wave2-round1 — Wave 2 批 1（babe01e..95ad05c 六提交）双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 2 批 1 六提交：3a92a16（T-057 检查点）/ 75c2baa（T-057 金样登记）/ 5fd3a9d（T-050 治理 E1）/ 45925c8（T-052 L1 文法子集）/ 3118c4a（T-057 测试收容）/ 95ad05c（T-051 动作注册表） |
| 审查日期 | 2026-10-14（双轴后置审查 + 本处置 commit 同日落盘） |
| 双轴 | Standards（CODE_STANDARDS CS-2/CS-6/CS-7 逐条）× Spec（08 §Wave2 任务卡验收 + 11-spec-tables 冻结表口径） |
| 处置执行 | Wave 2 批 1 修复代理（单一 commit "Wave2 batch1 review fixes: REV for R7 exemptions, SP-2 erratum, lockstep guard"）；#1/#3 为 MAJOR 级裁定，#2/#4 为勘误/加固 |
| 验证 | run_all.sh 全绿（G2/G2.5/G1/G3/G4 真实输出摘录见 §四）；lockstep 守卫 19/19 动作五字段一致 |

## 一、发现总览

| # | 轴 | 级别 | 发现 |
|---|---|---|---|
| 1 | S（CS-7.1/D-18） | **MAJOR** | 批内新增 14 处 `// ci-allow: R7` 行级豁免（TrustModel 5 / ActionCatalog 4 / ActionSchemaTable 5）未在审查记录中引用登记——CS-7.1 明文"豁免必须在审查记录（docs/engineering/reviews/）中引用"，缺登记则豁免不可审查 |
| 2 | P（11-spec-tables SP-2 vs 任务卡） | 勘误 | T-052"E1 高频子集"口径分裂：任务卡侧=守住X/建造/敌情查询/储备查询，SP-2 配套口径行（原 v1.1 校准）=敌情查询/储备查询/设闹/守住X；实现（L1IntentParser.SupportedIntents）已按并集 5 意图落地，文档行滞后 |
| 3 | S（CS-2.x 单源精神） | **MAJOR**（结构性，挂账缓收） | Governance.SystemActionTariffTable（T-050/C-14）与 Actions.ActionCatalog（T-051/C-03）双源并存：19 动作的计价/权限档/解锁时代/Boss 加倍/危险位元数据两处各写一份；另 PermissionTier 与 ActionPermissionTier 双枚举同构。单侧改动会静默分叉 |
| 4 | Q（测试强度） | MINOR | ApprovalSessionStateTests 仅合法路径（Save/往返/边界），缺非法输入（null/损坏 JSON）反序列化行为断言——fail 语义未被测试钉住 |

## 二、R7 豁免登记（处置 #1 主项；行号=95ad05c 时点）

豁免性质（与 W1 收口 REV-20261013-wave1-round3 #3 同一裁定路线）：**全部为文档化域常量**（信任度量纲/时间量纲/时代序号量纲/点数量纲/条数上限量纲），冻结出处随行标注任务 ID 与依据，非无据魔数；0 处改注册表（均非内容平衡数值，不属 game/content 注册表管辖）。锚点集现状（16 值，era1 内容 ≥10 整数）：15/20/40/45/50/60/70/80/90/100/120/150/220/2500/280/800。

### 2.1 TrustModel.cs（5 处——v3.2 §11.4 信任域常量，细则见附录 F.4；修正表 #15 三档阈值）

| 行 | 常量 | 值 | 冻结依据 |
|---|---|---|---|
| L46 | MaxScore | 100 | v3.2 §11.4/F.4"信任度取值 0~100 分"上界（信任分量纲，非内容平衡数值） |
| L64 | RegressionTarget | 50 | v3.2 §11.4/F.4"每波向 50 回归 ±1；跨时代重置 50"回归锚=重置值（信任分量纲） |
| L70 | AlarmJitterGraceSeconds | 15 | v3.2 §11.4/F.4"闹钟 ±15s 抖动豁免"窗口（时间量纲，秒） |
| L73 | SkepticalBelow | 40 | v3.2 §11.4/F.4 三档徽记阈值下沿"<40 将信将疑"（07 修正表 #15；信任分量纲） |
| L76 | DevotedAtLeast | 70 | v3.2 §11.4/F.4 三档徽记阈值上沿"≥70 言听计从"（07 修正表 #15；信任分量纲） |

伴生防御标注（不计豁免位）：L55 `FalseAlarmPerWaveCap = 5`——"误报 −5 每波封顶"（F.4）单次罚分数；值 5<10 在 R7 锚点集 ≥10 过滤下永不触发，标记属冻结出处的随行标注，一并在此登记备查。

### 2.2 ActionCatalog.cs（4 处——附录 G.2 解锁时代列量纲 + v3.2 §11.4 计价）

| 行 | 常量 | 值 | 冻结依据 |
|---|---|---|---|
| L25 | EraOrdinals.E2 | 2 | 附录 G.2"解锁时代"列时代序号（#10/N9/A2 增列；序号量纲，非内容平衡数值） |
| L28 | EraOrdinals.E3 | 3 | 同上（E3 行解锁序号） |
| L37 | EraOrdinals.Finale | 6 | 附录 G.2 终章序号（#17 终章 E=6；K-3 cells 上限 4+2×era 的量纲端点） |
| L75 | ConstructionPoints | 2 | v3.2 §11.4 计价"建造 2/战斗 1/物流 1/巡逻 1/侦察 0"建造档（点数量纲） |

### 2.3 ActionSchemaTable.cs（5 处——v2.1 §11.3 K-3 字段表冻结域常量，经 10-recovered §三回收）

| 行 | 字段规则 | 值 | 冻结依据 |
|---|---|---|---|
| L136 | QueryWorld.head_limit MaxInt | 20 | v2.1 §11.3 head_limit≤20 行数上限 |
| L147 | BuildPlan.cells CountCapBase/CountCapPerEra | 4 / 2 | v2.1 §11.3 cells≤4+2×时代序号（基值 4、每时代 +2 的 2） |
| L201 | SetTowerMode.target_id[] MaxCount | 6 | v2.1 §11.3 target_id[]≤6 数组上限 |
| L225 | AskPlayer.options MinCount/MaxCount/ItemMaxLength | 2 / 4 / 8 | v2.1 §11.3 options 2~4 项、label≤8 字 |
| L233 | ReportToPlayer.findings MaxCount | 8 | v2.1 §11.3 findings≤8 条 |

### 2.4 承重核验（实测）

对当前锚点集逐值核验：**承重行**（去标记即红）=TrustModel L46/L64/L70/L73/L76（100/50/15/40/70 均命中锚点集）+ ActionSchemaTable L136（20 命中锚点集），共 6 行；其余标记行（ActionCatalog 4 行之 2/3/6/2、ActionSchemaTable L147/L201/L225/L233 之 4/2/6/2/4/8/8、TrustModel L55 之 5）数值 <10，在 R7 锚点集 ≥10 过滤下**永不触发**，属"冻结出处随行标注"性质——保留不删（D-18 可审查性由标记文本自身承担，且防未来检查口径变化）。累计 `ci-allow: R7` 标记行=15（审查裁定计数 14 处豁免位 + 1 伴生防御标注），与 grep 实测一致。

## 三、逐项处置表

| # | 裁决与处置 | 落点 |
|---|---|---|
| 1 | **R7 豁免补登记（MAJOR）**：14 处豁免逐文件逐行登记于本 REV §二（CS-7.1"豁免必须在审查记录中引用"自此闭合）；豁免性质=文档化域常量，0 处改注册表；承重/防御实测核验记入 §2.4 | 本文档 §二（登记处）；豁免行本身 babe01e..95ad05c 已带 `// ci-allow: R7 <任务ID 理由>` |
| 2 | **SP-2 勘误（口径分裂）**：code-review 裁定取并集——E1 高频子集=**并集 5 意图**（守住X/建造/敌情查询/储备查询/设闹），与 T-052 落地实现（L1IntentParser.SupportedIntents 五意图）一致；原"[NEW-原型校准] 4 意图"行改写并作废原标注；变更记录 v1.2（头部版本号 v1.0→v1.2 一并补正——v1.1 行存在但头部漏更） | `docs/engineering/11-spec-tables.md`（SP-2 配套口径行 + 变更记录 v1.2） |
| 3 | **双源 lockstep 守卫（MAJOR，结构性挂账+最小立即缓解）**：单源化收口属结构性改动（牵两表消费方与既有测试，且与 T-070 动作组全集同一改写面），按 08 挂账纪律记 DEBT 不即改；过渡期防线=新增 lockstep 守卫测试，逐 19 动作 cross-check 计价/权限档/解锁时代/Boss 加倍语义/危险位五字段（Boss 加倍在目录侧无显式位，按 group=combat 派生核验），任一不一致即红——把"静侧改动→静默分叉"变成"立即红"，为未来单源化（ActionCatalog 升格唯一行源）兜底 | 新增 `game/src/EraCore.Tests/Agent/ActionMetadataLockstepTests.cs`；`docs/engineering/08-task-plan.md` T-070 行尾 DEBT（v1.4）：TariffTable↔Catalog 单源化+双权限枚举合并 PermissionTier+TriggerScroll 3s 冷却落点+SetAlarm 钳制 vs 拒绝语义裁定，四件一并收口 |
| 4 | **弱测加固（MINOR）**：ApprovalSessionStateTests 补非法行 5 例，fail 语义按实测行为断言并注明——null 字符串输入→ArgumentNullException fail-fast（STJ string 重载非空注解，实测行为）；JSON "null" 字面量→null 引用（无会话态，调用方按空白名单处理）；截断/类型错位 payload→JsonException fail-fast（绝不产出半态；与 GovernanceJsonContextTests 既有 malformed-open 用例互补）；空对象 `{}`→构造绑定默认态（EraIndex=0、列表 null，门层装载时重校验，null 白名单不可作为数据混入） | `game/src/EraCore.Tests/Agent/Governance/ApprovalSessionStateTests.cs`（新增 5 个非法/缺省用例） |

## 四、完成门槛验证（真实执行，摘录）

- `bash tools/ci/run_all.sh`（NUGET_PACKAGES/DOTNET_ROOT 按 09-env-locked）：**总体 PASS（G1~G4 全 PASS）**——G2 红线扫描 `9 checks | 0 FAIL | 0 WARN`（R7 注记 16 锚点 | 145 产品文件已扫；T8 注记 3 金样 commit | 2 首版豁免；T3 995/995 方法已关联 100%）；G2.5 资产校验 `files=140 rejected=0`；G1 dotnet test **2310 passed, 0 failed**（含本批新增 lockstep 守卫 2 用例与非法输入 5 用例；测试计数含并行批次在树文件）；G3 Release 构建 TreatWarningsAsErrors=true 零警告；G4 Godot 冒烟 SMOKE_OK。注：本 commit 落盘期间与 T-053 并行批次共机，spike1 性能门（流场切片预算）曾在并发负载下瞬时超限，静默窗口复跑实测通过（并发抖动非回归，其 Sim 侧 diff 无流场相关行）。
- lockstep 守卫专项：`ActionMetadataLockstepTests` 两用例通过——结构断言（两表各 19 行、与 SystemActionKind 枚举逐一对应、无重复）+ 逐 19 动作五字段 cross-check（计价/权限档/解锁时代/Boss 加倍/危险位）全部一致，**19/19 相等确认**。
- 非法输入专项：`ApprovalSessionStateTests` 9 用例全绿（4 既有合法/边界 + 5 新增非法/缺省），fail 语义均为实测行为。

## 五、遗留与挂账

- 本轮新增挂账=处置 #3（T-070 行尾 DEBT：单源化四件套——Catalog 升格唯一行源+派生视图删重复、PermissionTier 双枚举合并、TriggerScroll 3s 冷却落点、SetAlarm 钳制 vs 拒绝语义裁定）；lockstep 守卫为过渡期防线，单源化落地后转为其回归基线。
- R7 十四处豁免登记属登记事实非挂账（豁免理由随行可审；§2.4 承重核验为登记时的锚点集快照，内容 JSON 扩容后以扫描器实时判定为准）。
- 既有挂账不变：T-053 TowerMagazine 双适配器等（08 v1.3/v1.4 累计）、Q11（08 §6）、G7/G8 待启用（13-quality-gates §1）。
