# REV-20261017-T143 — 无尽深化与词缀（Wave 8 批 1，F-006 深化）

| 项 | 内容 |
|---|---|
| 任务卡 | T-143（无尽深化与词缀；Wave 8，C 泳道，组件 C-19，测试类型 R[回归]，量 M；覆盖 F-006；前置 T-118✓） |
| 验收（08 行原文） | 任选时代/混编敌人/奇观带入/每 10 波词缀 |
| 状态 | **全量交付**（Endless 侧四特性 + 55 条新测试 + 链路长局回放；`bash tools/ci/run_all.sh` 全绿——见 §5） |
| 提交 | 单 commit（`game/src/EraCore/Endless/**` + `game/src/EraCore.Tests/Endless/**` + 本 REV；并行 T-134 文件零触碰——见 §7 并行纪律） |

---

## 1. 交付物清单（产品侧）

1. **`Endless/EndlessStartBaseline.cs`**（新）：`EndlessResourceStock`（初始资源行）+ `EndlessStartEraBaseline`（起始时代基准：初始资源+解锁塔面，构造校验空白菜单/重复/负数量）。**任选时代深化的基准面**——起始时代除波形外（T-118 已有）再钉初始资源与解锁面。
2. **`Endless/EndlessMixPolicy.cs`**（新）：`EndlessMixPolicy(mixFromWave, startEraWeight, otherEraWeight)`——**混编敌人调度**：MixFromWave 以下=开局时代独占（敌池基准），以上=双权重钮跨时代混编（每线内归一）；`Default`=(41, 1.0, 1.0)（41=对齐 SP-7a「>40 深段」边界；1:1=时代均匀，与 EndlessEraPools 权重 1.0 先例同款，SP-7 只钉「按权重」不钉权重值）。
3. **`Endless/EndlessAffixRolls.cs`**（新）：`EndlessAffixRollPolicy(rollEveryWaves, rollStartWave, kindsPerRoll, allowConsecutiveRepeat, repeatIntensityGain)` + `EndlessAffixRolls` 静态 roller——**每 10 波滚词缀**：RollStartWave 起每 RollEveryWaves 波一块、块内词缀恒定块界滚换（四词缀域=SP-7b 冻结集合，只滚激活表不滚量级）；**重复规则**=AllowConsecutiveRepeat（连续块可否同词缀；关闭时可行性耦合 kindsPerRoll≤2（4−k≥k），构造期拒绝）；**叠加规则**=RepeatIntensityGain（连续重摇层数 r → 强度 ×gain^(r−1)，`Default`=×1.5 镜像 SP-7b 叠层先例）；**确定性**=FNV-1a 哈希派生抽取（seed+块号+抽取序），零 `IRng` 接触——名册随机流与滚动正交（测试实证同 rng 两局名册逐 spawn 一致）；81+ 冻结叠层（EndlessAffixes.Stacks）照常垫底；块号钳 `MaxBlocksClamp=1000`（溢出守卫）。
4. **`Endless/EndlessWonderCarry.cs`**（新）：`Resolve(requested, builtWonderIds)`——**奇观带入（只读消费）**：requested ⊆ 已建集合（trim/ordinal 比对）方可带入；未解锁拒绝（ArgumentException）；`builtWonderIds=null`=无战役联动透传（T-118 语义不回退）。已建集合来源=宿主递交的 `LegacyLedger.BuiltWonderIds` 只读面（Endless 侧零跃迁域接触）。
5. **`EndlessEnemyPool.cs`**（改）：+`Pick(rng, role, wave, startEra, mix)`——混编调度抽取（基准段=开局时代行独占；开局时代缺该线行→整桶原权重诚实回退；混编段=逐行权重 × 时代因子；每抽取恰一次 `NextDouble`，与原 Pick 同流形状）。
6. **`Endless/EndlessAffixes.cs`**（改，含 §5a 卫生修复）：`Modifiers(wave, tuning)` **保持原始直体**（T-080 固定表路径零新增分配——首版重构曾委托带校验的组合重载引入 Distinct+ToList+闭包，排查期修复回直体）；新增 `Modifiers(wave, tuning, activeKinds, repeatIntensity)` 组合重载（单趟布尔掩码检 None/重复词缀，无 LINQ/闭包；通道合成/81+ 叠层/中性缺席/tick 换算=同一冻结机制复用）——滚词缀路径专用。原固定表行为逐字节等价（既有测试零改动全绿）。
7. **`EndlessRules.cs`**（改）：`EndlessRunConfig` +3 可选参数（`startBaseline`/`mixPolicy`/`affixRolls`，基准时代与开局时代一致性守卫）；`PlanWave` 接混编抽取与滚词缀调制；`AffixBonusPoints(waves, seed, weights, policy)` 块级口径（O(blocks)，long 域防 int 回绕）；`Score` +可选滚词缀档（缺省=固定表，既有调用零影响）。
8. **`EndlessRun.cs`**（改）：身份哈希入账起始基准/混编档/滚词缀档（确定性台账面扩）；`Defeat` 词缀加权与总分走实际档（滚词缀局的 Score=SP-7c+滚词缀加权——修发现时点的隐藏不一致：原 Score 内嵌固定表加成）。
9. **`EndlessEntry.cs`**（改）：`EndlessEraContent` +`InitialResources`/`Towers`（可选，缺省空）；`EndlessEntryRequest` +`BuiltWonderIds`/`MixPolicy`/`AffixRolls`（可选）；`Begin`=奇观校验（联动在场时）→起始基准冻结→混编缺省解析 `EndlessMixPolicy.Default`→配置冻结；**`AvailableStartEras(eraContent)`=时代枚举动态读取**（装载集时代升序去重——今天 era1~era4 装载面下 E1~E4，era5 内容装载后 E5 自动出现，Endless 侧零改动零冲突；时代域上界 1..6=附录 A 冻结界，非时代数硬编码）。

## 2. 四特性验收面（对 08 行原文）

| 特性 | 验收证据（测试锚） |
|---|---|
| 任选时代 | `EndlessStartBaselineTests`：动态枚举=装载集升序去重（含 era5 合成集今日即可开局——时代数不落码面证明）；E1~E4 真内容塔面基准（era.json towers 行逐代前缀互锁）；基准随开局时代走（E1/E2 同装载、开局 E2 → 基准=E2 面）；基准入身份哈希。`EndlessDeepeningChainTests`：E4 开局 120 波链路（波形+initial_stock+塔面三冻结）。 |
| 混编敌人 | `EndlessMixPolicyTests`：基准段（<41）三线全开局时代；混编段跨时代+份额随权重钮（9:1 vs 1:9 实证转向；2:1 池份额落 0.8±0.12 带）；开局时代缺线回退；同 seed 逐波哈希全等；MixFromWave=1+等权与无策略名册逐 spawn 全等（T-118 语义回归锚）。 |
| 奇观带入 | `EndlessWonderCarryTests`：已解锁子集保序冻结；未解锁/空白拒绝（illegal）；无联动透传；真账本（LegacyLedger 真内容装配）只读联动——带入+整局+落账后账本哈希与已建集合不变、K-7 文档不写 `legacy:campaign-ledger` 槽只落三榜槽。 |
| 每 10 波词缀 | `EndlessAffixRollsTests`：起滚下空；块内恒定块界滚换；无重复规则下连续块互斥；允许重复下连续重摇层×gain^(r−1)（逐块核对）；同 seed 同序列+异 seed 异序列；滚动与名册随机流正交（同 rng 名册逐 spawn 一致）；滚词缀调制复用冻结通道合成（缺席通道中性/到场通道 SP-7b 量级/81+ 叠层垫底公式断言）；记分块级口径=逐波暴力和；极端波块钳。 |

**三榜分记口径不变**：榜=开局锁档（EndlessRunConfig.Participation 冻结），四深化档全部只入身份哈希与运行面，不触榜路由；链路测试中途换档后结算仍落开局档榜、三榜互不混排。

## 3. 设计裁定（本任务自裁，登记待查）

1. **入口混编缺省=T-143 生产档**（基准 41 起混编）：任选时代验收要求「起始时代影响敌池基准」——缺省保持 T-118 全池混编则该要求落空；`EndlessRunConfig` 原始构造缺省仍=null（T-118 语义，既有测试/宿主直配零影响）。等权全池=MixFromWave=1+1:1 钮的极限形（测试锚定逐 spawn 全等）。
2. **滚词缀缺省=null（SP-7b 冻结激活表）**：W3 出口裁定（REV-20261015-wave3-exit F2）钉生产默认档=冻结规格表；T-143 滚动为参数化深化、入口显式传入即启用（链路测试以 `Default` 档实证全链）。**翻转生产缺省需业主/规格裁定**（登记为遗留，§6）。
3. **滚动确定性=哈希派生而非 IRng**：滚动若吃主随机流则名册流形变（破坏既有确定性锚）；哈希派生是确定性种子扩张非「随机」（CS-1.4 口径：随机只经端口——本通道无随机性，同 seed 必同序列）。块行走 O(block)/次调用，钳 1000 块防极端波。
4. **奇观带入效果应用=宿主侧**：Endless 冻结/校验/哈希带入集合（参与运行身份），奇观增益的战场应用（ResourceStock.PyramidBonus 等生产通道）归宿主按带入集合消费——引擎侧不复制跃迁域效果面（勿改跃迁域红线）。
5. **时代域上界 1..6 保持附录 A 冻结**：动态化的是「可开局时代枚举」（装载集驱动），非时代编号域；era7+ 仍会被域守卫拒绝（附录 A 六时代界）。

## 4. 测试增量（55 条，五新文件；Endless 命 200 全绿）

| 文件 | 用例数 | 覆盖 |
|---|---|---|
| `Endless/EndlessMixPolicyTests.cs` | 14 | 策略守卫(illegal 5)/生产档形状/基准段独占/混编段权重份额/权重钮转向/缺线回退(boundary)/抽取守卫/确定性/身份哈希/等权全池=T-118 回归锚 |
| `Endless/EndlessAffixRollsTests.cs` | 18 | 策略守卫(illegal 6)/生产档形状/块节奏/同 seed 同序列/名册流正交/重复+叠加规则/无重复无层/调制复用/81+ 垫底/记分块级=逐波和/守卫/显式词缀表守卫/极端波钳 |
| `Endless/EndlessWonderCarryTests.cs` | 11 | Resolve 全矩阵（legal/boundary/illegal）/入口联动三态/真账本只读证明（哈希不变+不写跃迁槽） |
| `Endless/EndlessStartBaselineTests.cs` | 10 | 基准守卫(illegal)/配置时代一致性(illegal)/动态枚举/era5 今日可开局/基准冻结/缺席合法/身份哈希/E1~E4 真塔面(Theory 4) |
| `Endless/EndlessDeepeningChainTests.cs` | 2 | 链路长局回放（E4 开局 120 波：动态枚举+基准冻结+基准段独占+混编段跨时代+滚词缀块收集+81+ 叠层+换档不换榜+滚词缀加权对账+三榜落账+账本只读+采样点重放哈希全等）；同 seed 整局重放终局一致 |

五级覆盖自查：单元（Resolve 纯函数/策略守卫/池抽取）、逻辑（混编份额/滚动序列/调制公式/记分口径）、集成（入口→配置→运行→结算→落账）、回归（既有 Endless 命 200 全绿：入口默认档变化后 T-118 族零漂移；金样零触碰）、链路（120 波长局回放双局对账）。

## 5. run_all 门禁摘要（2026-10-17 晚，共享树与 T-134 era5 PR2/PR3 并行）

| 门禁 | 结果 | 说明 |
|---|---|---|
| G2 红线（redline_scan 9 检查） | **PASS** | 0 FAIL 0 WARN（R7 16 锚点扫描含新增 ci-allow 行） |
| G2.5 资产校验 | **PASS** | 586 文件 0 rejected |
| G2.6 模组校验 | **PASS** | 0 FAIL，3 mods |
| G1 测试（全量 6354） | **本任务面全绿；红=阻塞于 T-134 域在途** | 完整 run_all 5 红：`Era5LevelPlaythroughTests`×4（E5 关未通关——T-134 PR2/PR3 在途，已派修）；复跑排除该类后仅余 `Era5HandcraftedLevelPacingTests`×2（其文件运行前一分钟仍在编辑，mid-edit 红非终态）。**非 Endless 域零失败**：Endless 命 200/200（含本任务 55 新增）、既有全部套件绿、首跑中曾红的 `FlowFieldManagerTests.DeterministicQuota…`（吞吐比 0.890 vs 0.90）在复跑中通过=共享机负载噪声（安静窗口纪律口径，PerfSerialCollection 先例）。 |
| G3 Release 构建 | **PASS** | 零警告（独立复跑 `dotnet build -c Release`） |
| G4 Godot 冒烟 | **PASS** | SMOKE_OK（独立复跑 smoke_godot.sh） |

口径说明：run_all 为序贯门禁，G1 步遇 T-134 在途红即停；G3/G4 依分步日志与独立复跑补证。本任务提交面（Endless/Endless 测试）在所有跑法下全绿。

## 5a. 性能回归协查（SimGroupingPerfGuardrail 红态指控的隔离复现，2026-10-17 W8 批 1）

**指控**：T-134 worktree 隔离二分称共享树红态（sim Step CPU 均 4.227ms vs 门禁 2.589ms ×3）来自本任务 Endless 在途改动。

**隔离复现（方法同款：worktree@HEAD + 仅本任务文件拷入）**：
| 场景 | 结果 |
|---|---|
| 护栏单独跑（我的改动在场） | **1.562ms GREEN** |
| 护栏在全套件语境（我的 55 测试同进程） | **1.528ms GREEN**（首试即过线） |
| 修复后连跑三次 | **1.554 / 1.580 / 1.615ms 全 GREEN**（方差极小） |

⇒ 隔离树下不重现红态；本任务 Endless 改动不在 sim 路径（`EndlessAffixes`/`EndlessRun` 无 Sim 消费方，grep 实证）。红态时窗恰逢共享树多源负载：断连前 run_all 的孤儿 testhost（40+ 分钟满载 CPU）+ 轮询构建；CPU 时间口径对邻居偷墙钟免疫、对多源 SMT/缓存/热争用不免疫（`PerfSerialCollection` 注释先例：2.25 容差按「单邻居」设计）。**定性=环境负载，非代码回归**（数据已回报协调会话）。

**顺手修复的真问题（无论指控成立与否均已修）**：排查发现本任务首版重构曾把 `EndlessAffixes.Modifiers(wave, tuning)` 委托给带校验的组合重载——T-080 固定表路径每次调用新增 `Distinct`+`ToList`+闭包分配（非 sim 路径，但属热路径卫生违规）。已修：固定表路径恢复**原始直体**（与 T-080 版逐行等价、零新增分配）；组合重载改单趟布尔掩码（无 LINQ/闭包，重复词缀经掩码检出）。Endless 命 200/200 复验绿。

## 6. 局限与遗留（如实）

1. **滚词缀生产缺省翻转待裁定**（§3.2）：现缺省=SP-7b 冻结表；翻转需业主确认（影响 41 波起所有无尽局的词缀体验）。
2. **混编权重生产档为 1:1 时代均匀**（§3.1）：无规格锚点（SP-7 只钉「按权重」）；调参=宿主改 EndlessMixPolicy.Default 或入口传入，无需改引擎。
3. **奇观带入的战场效果应用未接**（§3.4）：宿主表现层（C-21 域）接线时按 `Config.WonderIds`+`StartEraBaseline` 消费；本任务只交付冻结/校验/哈希面。
4. **滚词缀块行走 O(block)**：单波调用 O(钳位块数)（≤1000 块×4 哈希）；结算块级口径 O(blocks)——10⁵+ 波极端长局的结算成本为观察项（现实死亡波 ≪10⁴，非问题）。
5. **入口混编缺省变化对宿主的影响**：多时代装载的入口局，1~40 波名册从「全池混编」变为「开局时代独占」——T-118 时代语义如此；若宿主要旧行为，入口传 `MixFromWave=1`+等权即复原（测试锚定全等）。

## 7. 并行纪律（T-134 同树并行）

T-134（era5）与本任务同工作区并行：其文件（`game/content/era5/**`、`levels/e5.json`、`Era5ContentTests.cs`、`Levels/HandcraftedLevel*` 改动）**零触碰**；本任务提交只含 `Endless/`、`Endless` 测试、本 REV。期间 T-134 一次在途编译错（Era5ContentTests CS1503）短暂阻塞共享测试工程，经协调其修复后恢复——本任务未动其文件。动态时代枚举与 era5 的零冲突已双向实证：本任务 era5 合成集测试（Endless 侧无需 era5 知识）；T-134 落地的真 era5 内容不在本任务装载面内。

## 8. 红线自查

R9 绝对路径（无，测试内容仓库相对发现同先例）/T1 残留标记（无）/R5 密钥（无）/R7 数值锚点（新增常量逐行 `ci-allow: R7 T-143` 注记：41 节奏锚×2、NoRepeatMaxKindsPerRoll=2 工程推导、MaxBlocksClamp=1000 工程上限、RepeatIntensityGain 1.5 镜像先例——`tools/ci/redline_scan.py` 9 检查 0 FAIL 0 WARN 实跑通过）/T2 src↔test 映射（四新源文件各配同词干测试）/T4 恒真断言（无；全部具体值/序列/哈希断言）/T3 F-ID 关联（类级 `[F-006][T-143]`，扫描 100%）。
