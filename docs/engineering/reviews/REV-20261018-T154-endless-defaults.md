# REV-20261018-T154 — 无尽缺省定版（业主裁定落地）

| 项 | 内容 |
|---|---|
| 任务卡 | T-154（无尽缺省定版（业主裁定落地）；Wave 9，C 泳道，优先级 9，覆盖 F-006；前置 T-143✓） |
| 验收（08 行原文） | 滚词缀默认开+经典固定表可选（开局明示可切）；混编 3:1 主场主导+波渐升（41 波 9:1→81 波 3:1 终值待实测微调）；设计案 v3.3 补冻结锚点；档身份哈希分账纪律复核（老存档零漂移） |
| 裁定输入 | 业主 2026-10-18 批复（08 v1.24 变更记录①②）+ REV-20261017-T143 §6.1/§6.2 两项待裁定 |
| 状态 | **全量交付**（两项裁定引擎面+数值真相源+设计案 v3.3；run_all 六步全绿——G1 6788/6788 含本任务 32 新增（Endless 命 232），见 §5） |
| 提交 | 单 commit（`game/src/EraCore/Endless/**` 五改两新 + `game/src/EraCore.Tests/Endless/**` 两改一新 + `game/content/endless/endless-policies.json` + 设计案 + 本 REV；并行 T-155 文件零触碰——见 §6 并行纪律） |

---

## 1. 裁定一落地：滚词缀默认开 + 经典固定表两档显式

1. **`EndlessAffixSchedule` 枚举**（`EndlessEntry.cs` 新增）：两档显式选项——`Rolling`（滚词缀表）=生产缺省；`ClassicFixed`（经典 SP-7b 冻结激活表）=开局回切档。`EndlessEntryRequest.AffixSchedule` 缺省=`Rolling`（**缺省翻转在引擎入口面成立**：不传任何词缀参数的新开局即滚词缀档，节奏=`EndlessAffixRollPolicy.Default` T-143 生产档）。
2. **矛盾守卫**：`ClassicFixed` 档位与显式 `AffixRolls` 滚动策略同时递交=参数矛盾，入口拒绝（ArgumentException）——两档显式无歧义；未定义档位值拒绝。
3. **界面明示面**：`EndlessSession.AffixSchedule`（新属性）=开局冻结的档身份只读查询（Rolling/ClassicFixed）——C-21 表现层接线位（业主裁定「界面明示本局节奏」的引擎查询面；无尽入口 UI 本体仍挂 C-21 域，本任务交付引擎查询位+两档 API）。
4. **档身份哈希分账纪律复核（T-143 已含配置冻结的复核确认）**：
   - 词缀节奏入 `EndlessRun.ComputeStateHash`（T-143 `WriteRollPolicy`：null=false 前缀/非空=true+5 字段）——两档=不同身份哈希，测试实证（同 seed 同请求仅档位差→波 41/60 哈希 NotEqual）。
   - **零漂移矩阵**：`ClassicFixed` 与「翻转前缺省面」（直配 `EndlessRunConfig.AffixRolls=null`，含同一起始基准）60 波逐点名册/词缀通道/身份哈希全等（测试 `The_two_affix_schedules_settle_into_distinct_identities`）——**生产缺省翻转对老口径零漂移**：老语义可完整复原（显式 ClassicFixed），既有直配调用（AffixRolls=null）语义不变。
   - **老榜零漂移实测**：已有榜行的 port 上两档新局各自落账——老行波数/分数原样保留、同榜追加不混排（测试 `Existing_board_rows_stay_untouched_by_new_schedule_runs`）。无尽无持久会话态（不进 LevelSession/检查点域，T-118 裁定）——「老存档」面=无无尽持久数据可漂移；榜行=`EndlessScoreRecord`（Board/波数/击杀/词缀分/总分）不含哈希，落账只写新局。
5. **分数口径复核（Score 走实际档）**：`EndlessRun.Defeat`/`EndlessRules.Score` 按 `Config.AffixRolls` null 与非 null 分流（T-143 已修）——本任务复核实证：两档各跑 100 波死亡结算，Rolling 局词缀分=`AffixBonusPoints(waves, seed, weights, policy)` 块级口径、ClassicFixed 局=固定表逐波口径，各自对账 SP-7c 总分且两档词缀分确实不同（测试 `Both_schedules_score_by_their_own_affix_bonus`）。

## 2. 裁定二落地：混编 3:1 主场主导+波渐升（配置化）

1. **`EndlessMixPolicy` 波渐升扩展**（既有权重钮扩参，非新硬编码）：+`RampEndWave`/`EndStartEraWeight`/`EndOtherEraWeight` 三参数（全或无守卫；`RampEndWave ≥ MixFromWave`；终值权重严格正）；新查询 `WeightsAt(wave)`——固定档=常量；渐升档=`t=(w−mix)/(end−mix)` 线性插值，`w ≥ end` 恒终值（退化 `end==mix`=混编起即终值，除零守卫）。`EndlessEnemyPool.Pick(rng, role, wave, startEra, mix)` 的两处权重读取改经 `WeightsAt`（每抽取仍恰一次 `NextDouble`——rng 流形状不变）。
2. **渐升插值锚（业主口径逐点核对）**：41 波=9:1（t=0）、61 波=6:1（t=0.5）、81 波=3:1（t=1 含锚）、121 波起恒 3:1；51 波=7.5:1/71 波=4.5:1 中间线性（测试 `Ramp_weights_interpolate_linearly_between_the_mix_and_end_waves` 精确断言）。
3. **数值真相源=JSON（RL7，bgm-programs 同构先例）**：新 `game/content/endless/endless-policies.json`（schema `urn:eratd:content:endless:policies:1`）——mix 块=41/9:1/81/3:1 渐升生产档（**终值 3:1 注记"待 T-158 业主一小时亲测后实测微调——改表即改产"**）；affix_rolls 块=滚词缀生产节奏（与 `EndlessAffixRollPolicy.Default` 同值冻结，测试同构校验锚定双源零漂移）。
4. **`EndlessPolicyTable` 严格装载器**（新源文件，`System.Text.Json`）：closed `$schema`/`content_type`、mix/affix_rolls 双块必备、渐升键全或无、数值校验委托策略构造（单一真相）后以 `EndlessPolicySchemaException` 带源标签回抛（AudioSchemaException/NarrativeSchemaException 先例：每内容域一异常型）；`_` 前缀注解键装载器忽略（终值待校注记走注解键——bgm-programs `_annotation_convention` 同构）。
5. **入口缺省解析链**（`EndlessEntry.Begin`）：混编=`显式 MixPolicy > PolicyTable.MixPolicy > EndlessMixPolicy.Default（无表兜底）`；词缀节奏=`显式 AffixRolls >（Rolling 档）PolicyTable.AffixRollPolicy > EndlessAffixRollPolicy.Default >（ClassicFixed 档）null=SP-7b 冻结表`。**生产缺省=宿主装载 endless-policies.json 递交 `PolicyTable`**（引擎无 IO，CS-1.4——audio/bgm-programs.json 同款：宿主读表传入，代码内 `Default` 为无表兜底，其注释已改注 T-154 地位）。
6. **渐升入身份哈希**：`WriteMixPolicy` 扩写渐升三参数（有渐升=+bool+3 字段；固定档仅 +1 bool 位）——渐升档 vs 固定档、不同终值=不同运行身份（测试 `Ramp_parameters_join_the_run_identity`）。哈希格式扩展不触任何持久面（榜行不存哈希；金样 game/content/golden/ 五件均不含 Endless 域——核验）。
7. **确定性/T-118 语义回归锚不破**：渐升=纯 (policy, wave) 函数——同 seed 同渐升档两局 90 波逐波哈希全等（测试）；等权全池回归锚（MixFromWave=1+1:1 与无策略名册逐 spawn 全等）既有测试零改动全绿；入库表档与无表兜底档在混编段名册确实分野+表档自身确定性（EndlessPolicyTableTests）。

## 3. 设计案 v3.3（规格真相源升级）

| 位置 | 行号 | 内容 |
|---|---|---|
| 头部版本表格 | L5-6 | v3.3（无尽缺省定版：两项裁定摘要）；日期 2026-10-18 |
| §10 无尽模式条·冻结锚点① | **L114** | 滚词缀默认开+经典固定表开局可选（两档可切、界面明示本局节奏） |
| §10 无尽模式条·冻结锚点② | **L115** | 混编 3:1 主场主导+波渐升（41 波 9:1→81 波 3:1 线性渐升；终值待 T-158 实测微调，数值冻结于 endless-policies.json） |
| 变更记录 v3.3 | L443 | 两裁定全措辞+实现面+与 W3 出口 F2 的缺省归属关系（固定表降为可选回切档，表体零改动）+本 REV 引用 |

## 4. 测试增量（32 条：改 2 文件+新 1 文件；Endless 命 232 全绿）

| 文件 | 用例 | 覆盖 |
|---|---|---|
| `Endless/EndlessMixPolicyTests.cs`（+6） | 渐升守卫(illegal 6 行 Theory)/插值曲线（41/51/61/71/81/121 逐点）/固定档恒权/退化渐升（end==mix）/渐升份额真实转向（早段 0.90~0.99 vs 晚段 0.78~0.92 带）/渐升确定性（90 波双局全等）/渐升参数入身份（vs 固定/不同终值三局互异） |
| `Endless/EndlessPolicyTableTests.cs`（新，8） | 解析守卫(illegal：坏 JSON/根/错 schema/错 marker/缺块/mix 六行/roll 三行)/入库表生产形状（41/9:1/81/3:1+三插值锚+affix_rolls≡代码 Default 同构校验）/表档运行（vs 兜底档名册分野+自身确定性） |
| `Endless/EndlessEntryTests.cs`（+6，改 1） | 缺省翻转（缺省=Rolling+Default 节奏+KindsAt 序列一致）/ClassicFixed=SP-7b 冻结表（41 Swift ×1.25/51 +Harden ×1.5，自旧缺省测试迁移）/档位守卫(illegal：互斥+未定义)/缺省链（表>兜底/显式>表×2）/两档分账+ClassicFixed≡直配 null 零漂移（60 波名册+词缀通道+哈希）/两档各自分数口径（100 波死亡对账）/老榜零漂移（port 榜行保留+同榜追加不混排） |

五级覆盖自查：单元（枚举守卫/插值公式/Parse 校验/策略构造）、逻辑（渐升曲线/份额带/块级与固定表两种记分口径）、集成（入口→配置→运行→结算→落账，含表递交链）、回归（既有 Endless 命 200 零漂移全绿：等权全池/T-118 语义锚零改动；金样零触碰）、链路（两档 60/100 波长局对账+确定性重放）。

## 5. 门禁摘要（2026-10-18 晚，与 T-155 同树并行的安静窗终跑）

| 门禁 | 结果 | 说明 |
|---|---|---|
| G2 红线（redline_scan 9 检查） | **PASS** | 0 FAIL 0 WARN；R7 无新增豁免（渐升/词缀数值全部活在 JSON——本任务唯一 R7 面变化=EndlessMixPolicy.Default 既有豁免行理由改注"无表兜底档"）；T2：EndlessPolicyTable.cs↔EndlessPolicyTableTests.cs 新映射 |
| G2.5 资产校验 | **PASS** | 641 文件 0 rejected（本任务零资产面） |
| G2.6 模组校验 | **PASS** | 0 FAIL，3 mods |
| G1 测试（全量） | **PASS 6788/6788**（15m17s） | 含本任务 32 新增（Endless 命 232/232）+T-155 遥测套件；独立复跑 Endless 命多次全绿 |
| G3 Release 构建 | **PASS** 零警告 | xUnit2031（Assert.Single+Where）开发中消除 |
| G4 Godot 冒烟 | **PASS** | SMOKE_OK（运行时/程序集/EraCore 链路三合一） |

口径说明（诚实记录，安静窗纪律 2026-10-14）：终跑前有两次 G1 红——首跑红=V10FullClear「全四章链超 20 分钟」墙钟护栏（时窗与 T-155 的 run_all 并发）；二跑红=Spike1/SimGrouping/FlowSolve 三性能护栏（背景 CPU 31% 负载期）。同一工作树同代码的对照证据：①15:45 独立全量跑 6786/6786 全绿（含上述全部护栏）；②隔离 worktree（HEAD+仅本任务文件）单跑 Spike1 由红转绿（同状态两次结果翻转=负载抖动带）；③安静窗（CPU 6%/零邻居进程）终跑六步全绿。定性=共享机负载噪声（T-143 §5a 同款先例：CPU 时间口径对多源 SMT/缓存/热争用不免疫），非代码回归——Endless 域零 sim/流场消费方（grep 实证沿 T-143 §5a）。

## 6. 并行纪律（T-155 同树并行）

本任务提交面=`game/src/EraCore/Endless/**`（五改：EndlessMixPolicy/EndlessEnemyPool/EndlessEntry/EndlessRun+新 EndlessPolicyTable）、`game/src/EraCore.Tests/Endless/**`（两改一新）、`game/content/endless/endless-policies.json`、设计案 v3.3、本 REV。T-155（遥测）文件（`Telemetry/**`、`CampaignLoop/LevelSession.cs`、`Tests/Telemetry/**`、`Tests/Persistence/**`）**零触碰**；`git status` 实证改动集互斥。

## 7. 局限与遗留（如实）

1. **入口 UI 本体未接**：两档界面（开局切换控件+节奏明示文案）归 C-21 表现层任务挂接 `EndlessSession.AffixSchedule`/`EndlessEntryRequest.AffixSchedule` seam（T-118 入口裁定同款状态——引擎 seam 已就绪，Godot 按钮绑定待表现层）。
2. **宿主表装载接线未做**：生产渐升档生效需宿主装载 `endless-policies.json` 递交 `PolicyTable`（C-21 接线时三行：读文件→Parse→传入请求）；引擎侧无 IO 是 CS-1.4 纪律而非缺交付。
3. **终值 3:1 待 T-158 实测微调**：业主一小时亲测后改 `endless-policies.json` 的 `end_start_era_weight` 即改产（JSON 注记+设计案 L115 已标注）。
4. **`EndlessMixPolicy.Default` 地位变化**：从"T-143 生产档"降为"无表兜底档"（数值不变 41/1:1，既有测试零改动；生产档=endless-policies.json 渐升档）——若宿主漏传表，缺省=1:1 固定档而非渐升（测试锚定该行为，宿主接线清单挂 §7.2）。

## 8. 红线自查

R9 绝对路径（无——测试 repo 相对寻址同 AudioContent 先例）/T1 残留标记（无——grep 核验 TODO/FIXME 零）/R5 密钥（无）/R7 数值锚点（新代码零锚点数值：渐升/词缀数值全在 JSON；枚举 0/1 为白名单平凡值；EndlessMixPolicy.Default 既有豁免行改注理由属注释更新非新增豁免）/T2 src↔test 映射（EndlessPolicyTable.cs↔EndlessPolicyTableTests.cs 新增成对）/T4 恒真断言（无——全部具体值/序列/份额带/哈希断言）/T3 F-ID 关联（EndlessPolicyTableTests 类级 [F-006][T-154]，改注文件类级头同步加 T-154）。
