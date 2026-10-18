# REV-20261018-T140 — 程序关卡生成器（Wave 8 批 5，F-007）

| 项 | 内容 |
|---|---|
| 任务卡 | T-140（程序关卡生成器；Wave 8，A 泳道，组件 C-10/C-19，测试类型 seed 回归，量 L；覆盖 F-007；前置 T-013✓（SP-7 无尽外推规则已定案，程序关生成规则本任务新设计——P-9/D-23 记录在案） |
| 验收（08 行原文） | 同 seed 同图+≥10 局中位 40~90 分钟 |
| 状态 | **全量交付**（生成器核心+可玩性保障+硬验收实测+入口面；48 条新测试；`bash tools/ci/run_all.sh` 六步全绿——见 §5） |
| 提交 | 三 commit（PR1 生成器核心 / PR2 管线与可玩性测试 / PR3 入口面+Endless 开关+时长链路+本 REV） |

---

## 1. 交付物清单（产品侧）

1. **`EraCore/Procedural/ProceduralSeed.cs`**（新）：FNV-1a 64 子 seed 派生（域标签 UTF-16 → seed/attempt 字节混入——CampaignHash/GridWorld 基元族）。**重摇次数入哈希的基元面**：attempt 是派生输入，两条不同重摇路径不可能撞同一地形流。零 `Random`/零 `string.GetHashCode`（平台稳定）。
2. **`EraCore/Procedural/ProceduralRequest.cs`**（新）：`ProceduralTier`（难度档——威胁**非字段**而是 `Threat.ClampThreat(AdjacentEnemyCamps)` 活推导，营数带 [0,2]→威胁 0.3/0.6/0.9 常规带+终局带，与手工关 E1 标定同公式同带；波表/建造计划/资源乘数全数据化）+ `ProceduralEraContent`（时代内容包：敌册名册/塔表/币种/仓库等级——宿主递交，RL7）+ `ProceduralConstraints`（约束集：尺寸/比例/入口数/宽限带+重摇预算）+ `ProceduralRequest`（整请求装载即校验：塔线查表可解/Boss 不撞名册等）。
3. **`EraCore/Procedural/ProceduralLevel.cs`**（新）：生成产物记录（与手工关行同构的 map/stock/waves/command_script/objectives/unlocks 面）+ `ToLevelJson()`（schema `urn:eratd:content:levels:1` 单条目文档——**既有装载管线可直接装载**）+ `IdentityHash()`（全字段 FNV，重摇计数在内）+ `TerrainHash`（GridWorld 逐格地形哈希）。`_rerolls`/`_terrain_hash` 以注解键（`_` 前缀装载器忽略）随 JSON 留痕。
4. **`EraCore/Procedural/ProceduralLevelGenerator.cs`**（新）：核心生成器（纯逻辑零 Godot 零 IO，随机全经注入 `Func<long, IRng>`——CS-1.4）。每次尝试：布局抽取（尺寸/比例/入口数/边——layout 子 seed 流）→ **地形=生产 GridWorld 同一抽取**（terrain 子 seed；生成器不画第二套地形，管线重放逐格全等）→ 基地锚定（入口对边中心向心扫描）→ 出生点（边缘线均布+抖动）→ 可玩性闸 → 稀疏建筑选址（流场距离升序=基地沿线击杀环，切比雪夫 ≥2 间距——车道可路由性结构值）→ 自动建造计划（经济链+塔梯队+搬运线路，实体序号=仿真约定）→ 初始资源=塔线总价×档位乘数。
5. **`EraCore/Procedural/ProceduralCampaignEntry.cs`**（新）：战役外程序关入口数据位记录（入口行/时长带/约束带/档位梯）+ `OpenEras`（时代域动态枚举——装载集驱动，Endless AvailableStartEras 同纪律）。
6. **`game/content/campaign/procedural.json`**（新）：入口注册表（RL7——档位梯/波表/建造计划/约束带/时长带全部数值落内容表，产品码零硬编码）：三档 regular-1（t=0.3，e1-01 同形）/regular-2（t=0.6，e1-04 同形）/endgame（t=0.9+Boss，e1-final 同形）；约束带=装载兼容口径（water/rock=0，见 §3.1）。
7. **`EraCore/Endless/EndlessEntry.cs` / `EndlessRules.cs` / `EndlessRun.cs`**（改）：`EndlessEntryRequest.ProceduralMap`（可选「程序图」开关，缺省 false）→ `EndlessRunConfig.ProceduralMap`（配置冻结）→ `ComputeStateHash`（开关入身份哈希）。**只读消费**：Endless 波循环零接触（名册/波形与开关正交，测试实证）；程序图安放=宿主侧（run seed→`ProceduralSeed.Derive(seed,"endless-map",n)`，T-143 奇观带入「引擎不复制效果面」先例）。存量调用零影响（缺省关）。

## 2. 验收面证据（对 08 行原文）

| 验收项 | 证据（测试锚） |
|---|---|
| **同 seed 同图** | `ProceduralLevelGeneratorTests.Same_seed_generates_identical_levels_byte_for_byte`：双实例 **JSON 串逐字节相等**+**TerrainHash 逐格相等**+身份哈希/重摇计数相等；`Same_seed_terrain_matches_the_pipeline_gridworld_draw`：产物 map 行走生产 GridWorld 构造重放，地形哈希与生成器侧全等（生成器不画第二套地形）；`ProceduralDurationChainTests.Same_seed_double_run_of_generated_levels_hashes_identically`：三档各一同 seed 生成关 headless 跑到通关，通关刻+终局状态哈希+塔数全等（引擎面）。对偶面 `Different_seeds_generate_distinct_maps`：100 seed 地形哈希两两互异 |
| **≥10 局中位 40~90 分钟** | `ProceduralDurationChainTests.Ten_generated_levels_median_victory_time_lands_in_the_40_90_band`：10 个不同 seed（20260101..20260110）×三档梯轮转（4/3/3）生成关+自动通关实测，**中位=54.80 分钟**（全 10 局逐局落带 45.44~60.08）；零拒收（24/24、16/16、10/10）；终局档 Boss 必死。**全表见 §4** |
| 威胁度标定（§7 0.9 终局/常规带） | `ProceduralRequestTests.Tier_threat_follows_the_frozen_section7_camp_formula`：档位威胁=clamp(0.3+0.3×营数)（0.3/0.6/0.9=手工关 E1 标定带同公式）；`Generated_rows_carry_the_formula_threat_and_win_waves`：产物威胁/胜波=§7 公式双面（10+⌊t×10⌋），宽限落非教学带 ×1.5~2 |
| 可达性/资源配比/重摇 | `Every_entry_reaches_the_base_on_the_drawn_terrain`（真流场 C-11 复用）；`Generated_layouts_stay_inside_the_constraint_bands`+`Drawn_forest_share_stays_near_the_requested_ratio`（逐格实绘占比 ±0.05）；`Water_bands_trigger_deterministic_rerolls`+`Rerolled_levels_still_pass_the_reachability_gate`+`Exhausted_reroll_budget_throws_honestly`（确定性重摇链：attempt 入哈希；预算耗尽诚实抛 `ProceduralGenerationException`，绝不静默降级） |
| 既有装载管线可装载 | `ProceduralPipelineTests.Generated_json_loads_through_the_existing_handcrafted_pipeline`：三档生成 JSON 全部通过 `HandcraftedLevelData.Parse`（T-085 schema 全校验**未改动一行**）→ `BuildConfig`/`BuildCommands` 组装 → LevelSession 起局 |
| 入口面 | `ProceduralCampaignEntryTests`：注册表装载即校验+时代域动态枚举（era1 装载⇒era1 开放；未装载⇒入口隐而不坏）；`EndlessProceduralMapTests`：开关冻结+入哈希+与波循环正交+缺省零漂移 |

## 3. 设计裁定（本任务自裁，登记待查）

1. **威胁带 [0.3,0.9]（营数 0~2）为程序关档位域**：§7 公式营数 3+（威胁 1.2）的波间隔 72s 使单局节奏结构性离开 40~90 分钟档（胜波 22×72s≈26min+清场<40min）——档位守卫拒绝营数 >2（无尽域外推归 T-013/SP-7，不是程序关卡域）。时长闭环模型（宽限+(胜波−1)×波间隔+清场）在 procedural.json `_duration_calibration` 逐档推演留档，10 局实测对齐（§4）。
2. **产物水比/岩比=0（装载兼容口径）**：既有手工关装载面（T-085 `HandcraftedLevelData`）对 E1 行硬性要求 `water_ratio=0`/`rock_ratio=0`（固定布局可建性口径）。任务卡「生成器产物可直接走既有装载管线」⇒ 注册表约束带钉 water/rock=0——地形多样性=森林布局/尺寸/比例带/入口边/布置（100 seed 全异已证）。生成器**核心**接受任意水/岩带（约束集输入面），水域带在核心层实证重摇链；如未来开「水域程序关档」，需先扩装载面带（独立任务，勿静默）。
3. **稀疏选址（切比雪夫 ≥2）为车道可路由性结构值**：首版流场距离紧排选址把建筑挤成实心团——`Simulation.TryRoute` 在端点首自由邻格间寻路，实心团内建筑无自由邻格→搬运线 `no-route` 拒收→塔断弹（T-140 调试实测：471 敌滞留全场不清）。稀疏化后零拒收+全档通关（调参过程诚实留档于此，不伪造首版即绿）。间距 2 带 ci-allow 登记（结构值非平衡数值；e1-final 手工环 ~2 格节距先例）。
4. **budget_seconds=2400（带内钳制）**：手工装载面预算带 [900,2400]s=手工关 15~40 分钟档；程序关 40~90 分钟档（§3 设计案 L49）结构性超出——产物预算=档位行值 2400（带内上沿），语义=三重目标 C1 阈值基值钳制口径（手工关实际时长超预算的 RED-FLAG 先例同源，T-095 实测校准挂账继续）。
5. **入口=独立数据位（campaign/procedural.json）**：读 campaign/ 既有结构（objectives/techtree/chests/signin 四注册表）——程序关入口非关卡行（不进 levels/{era}.json 六件套）而是并列注册表（content_type=ProceduralEntry，注解键声明 K-2 封闭枚举无此类、镜像 objectives.json 先例）。最小面：一张表=入口行+约束带+档位梯；campaign 结构零改动。
6. **Endless 开关=只读冻结面**：Endless 域无地图（波循环非关卡会话）——「程序图」开关只入配置+身份哈希，宿主按 run seed 派生地图 seed 安放（D-34「不留坑」方针：接口位预留、效果面不复制、写面零接触）。

## 4. 10 局时长表（硬验收②实测——2026-10-18，headless 自动通关，offsystem 命令流先例）

| # | seed | 档位 | 威胁 t | 胜波 | 宽限 | 地图 | 出生点 | **通关实测** | tick | 塔 | 命令(发/收) | Boss | 终局哈希 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 20260101 | regular-1 | 0.30 | 13 | ×1.96 | 19×25 | 3 | **60.08 min** | 108148 | 3 | 10/10 | — | 8966857901040188805 |
| 2 | 20260102 | regular-2 | 0.60 | 16 | ×1.92 | 20×23 | 3 | **55.22 min** | 99395 | 5 | 16/16 | — | 6110136751845312120 |
| 3 | 20260103 | endgame | 0.90 | 19 | ×1.59 | 20×24 | 2 | **45.44 min** | 81793 | 9 | 24/24 | 死 | -8303631735861094145 |
| 4 | 20260104 | regular-1 | 0.30 | 13 | ×1.85 | 18×25 | 4 | **59.38 min** | 106884 | 3 | 10/10 | — | 4996274191257536119 |
| 5 | 20260105 | regular-2 | 0.60 | 16 | ×1.51 | 19×26 | 4 | **54.02 min** | 97240 | 5 | 16/16 | — | -8244462214873471955 |
| 6 | 20260106 | endgame | 0.90 | 19 | ×1.92 | 18×25 | 2 | **47.19 min** | 84934 | 9 | 24/24 | 死 | 8463347076944604724 |
| 7 | 20260107 | regular-1 | 0.30 | 13 | ×1.83 | 19×24 | 3 | **59.52 min** | 107141 | 3 | 10/10 | — | -9078711475634120094 |
| 8 | 20260108 | regular-2 | 0.60 | 16 | ×1.63 | 20×25 | 4 | **54.39 min** | 97894 | 5 | 16/16 | — | -944296516196077531 |
| 9 | 20260109 | regular-1 | 0.30 | 13 | ×1.83 | 18×26 | 4 | **59.28 min** | 106696 | 3 | 10/10 | — | 76287081606728425 |
| 10 | 20260110 | endgame | 0.90 | 19 | ×1.98 | 17×26 | 4 | **46.24 min** | 83225 | 9 | 24/24 | 死 | 8253300385054509016 |

**中位 =（54.39+55.22）/2 = 54.80 分钟 ∈ [40,90]** ✓（10/10 逐局落带：min 45.44 / max 60.08；带内余量下沿 +5.4min、上沿 -29.9min）
口径说明：实测=LevelSession headless 通关时刻（弹链节流包含在内——手工关同口径的诚实读数，T-084 实锚带 2800~3900s 同源）；自动通关命令流=生成器产物的教学脚本（零拒收全应用）；同 seed 双跑（#1/#2/#3 代表三档）通关刻+终局哈希全等。

## 5. 测试增量（53 条，六新文件 + Endless 开关五条）

| 文件 | 条数 | 覆盖 |
|---|---|---|
| `Procedural/ProceduralSeedTests.cs` | 6 | 派生确定性/域分离/attempt 分离/异 seed 相异/map.seed 投影界/入参守卫(illegal) |
| `Procedural/ProceduralRequestTests.cs` | 9 | §7 营数公式(t 0.3/0.6/0.9)/注册表档位带/营数越带(illegal)/波表计划负值(illegal)/Boss 成对(illegal)/目标池(illegal)/约束带形状/退化带(illegal)/塔价查表/请求守卫(illegal) |
| `Procedural/ProceduralLevelTests.cs` | 4 | 身份哈希稳定+字段敏感/重摇计数入哈希/JSON 字节稳定/结构块齐全 |
| `Procedural/ProceduralLevelGeneratorTests.cs` | 12 | 同 seed 逐字节逐格全等/管线重放地形全等/100 seed 相异/公式威胁胜波/约束带落带/实绘占比带/全出生点真流场可达/资源配平/水域带重摇确定性/重摇产物可玩/预算耗尽诚实抛(illegal)/入参守卫(illegal) |
| `Procedural/ProceduralPipelineTests.cs` | 4 | 三档 JSON 过既有装载器/封闭动词表/组装起局/同 seed 双跑哈希全等 |
| `Procedural/ProceduralCampaignEntryTests.cs` | 3 | 注册表装载校验/时代域动态枚举/注册表守卫(illegal) |
| `Procedural/ProceduralDurationChainTests.cs` | 2 | **10 局时长链路（硬验收②）**/三档同 seed 双跑全等（硬验收①引擎面） |
| `Endless/EndlessProceduralMapTests.cs` | 5 | 开关冻结+缺省关(boundary)/入身份哈希/与波循环正交/宿主 seed 派生确定性 |

五级覆盖自查：单元（Seed 派生/记录守卫/JSON 形状）、逻辑（威胁公式对齐/比例带/资源配平/重摇链）、集成（装载管线→组装→LevelSession 起局/Endless 入口→配置→哈希）、回归（全套件 6600+ 全绿：手工关六件套/金样/Endless 既有族零漂移）、链路（10 局生成→装载→自动通关实测中位落带+双跑全等）。

## 6. run_all 门禁摘要（2026-10-18，共享树与 T-131 联机预留评审并行——本任务文件零交集）

| 门禁 | 结果 | 说明 |
|---|---|---|
| G2 红线（redline_scan 9 检查） | **PASS** | 0 FAIL 0 WARN（R7 16 锚点；新增 ci-allow 三行：档位营数上界 2/最小图面 3/稀疏间距 2——均为结构值非平衡数值，§3.3 登记） |
| G2.5 资产校验 | **PASS** | 641 文件 0 rejected（本任务零新资产） |
| G2.6 模组校验 | **PASS** | 0 FAIL（procedural.json content_type=ProceduralEntry 不在 K-2 封闭枚举——注解键声明先例=objectives.json，预载索引跳过零冲突） |
| G1 测试 | **PASS** | 6684 passed / 0 failed（927s；含本任务 53 条新测试） |
| G3 Release 构建 | **PASS** | 零警告（TreatWarningsAsErrors=true，32s） |
| G4 Godot 冒烟 | **PASS** | SMOKE_OK（20s；运行时/程序集/EraCore 链路三合一） |
| **总体** | **PASS** | 六步全绿（首跑 G4 曾红一次：`t055-panel-war-report.jsonl` 被并行会话进程锁占——环境文件争用非代码缺陷，锁释放后独立复跑 SMOKE_OK+全量重跑六步全绿，两次记录如实登记） |

## 7. 遗留与红线自查

1. **表现层入口未接线（挂账）**：战役选图/无尽菜单的「程序关/程序图」按钮绑定归 C-21 表现层任务（Endless seam 先例同源——入口数据位+引擎面已就绪，宿主接线另立）。EraCore 零 Godot 引用维持（GodotSharpGuardTests 全绿）。
2. **仅 era1 档位标定（挂账）**：三档梯的波表/建造计划锚 e1 关族；era2+ 程序档需各代内容包+经济链动词面（converter_recipe 等）标定，随需求立项（生成器核心时代无关——内容包即插即用，era1 打样已证）。
3. **水域/岩比程序档（设计面登记）**：核心层已支持（重摇链实证）；装载面 water/rock=0 钳制未扩——开档前须先扩 T-085 装载带（§3.2）。
4. **budget 钳制口径**：程序关预算=2400 钳制值（§3.4）；C1 限时目标在程序关的实测校准随 T-095 业主实测同源清账。
5. 红线自查：无绝对路径/无残留标记/无数值锚点裸奔（ci-allow 三行登记）/金样零触碰/Endless 写面零接触（只读冻结）/08 与 AGENTS 未动/时长数字全部实测留档（§4 表）。

### §3.1a 威胁带措辞勘正（批 5 Spec 审查 #5 补记）

正文"0.3/0.6/0.9 对齐手工 E1 标定带"为略称：手工 e1 关实际含 0.45（四关威胁集
{0.3,0.3,0.45,0.6,0.9}），0.45 非营数公式（0.3+0.3n）可表达值——程序档取公式可表的
三档；§7/F.2 合法上界 1.2 被档位守卫排除（§3.1 时长论证），属有档的裁窄非规格失配。
