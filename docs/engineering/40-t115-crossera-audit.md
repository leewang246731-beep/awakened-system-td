# 40 · T-115 跨代资源 E2→E3 + 稀缺分布审计

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 任务 | T-115 跨代资源 E2→E3+稀缺分布审计（Wave 6 / A1；deps T-074✓；与 T-114 并行） |
| 状态 | **完成**——验收四项逐条落定：丰度 ×0.5（声明+语料物化+引擎派生）／弹链映射（裁定=旧弹链自动退役，三源证据）／3:1 折算（E2→E3 守恒测试）／稀缺仅指定关（机制扫描数字表+era3 前向守卫） |
| 验收锚点 | 08-task-plan.md T-115 行「丰度×0.5+弹链映射+3:1 折算+稀缺仅指定关」 |
| 设计依据 | v3.2 §4 跨代资源行（`docs/我在游戏觉醒系统-游戏设计方案.md` L66：上代 2 种基础资源刷新 丰度×0.5；弹链本代化；无用途旧资源 3:1 折"文明基金"）+§5.1 L77（资源地理稀缺——富煤/油田/稀土只在特定关） |
| 实现依据 | `game/content/era3/era.json`（T-112 冻结：铜/铁 legacy_refresh from_era:2 ×0.5）+`game/src/EraCore/Legacy/EraTransition.cs`（T-074 通用折算通道）+`game/src/EraCore/World/GridWorld.cs`（地形生成） |
| 交付物 | 本报告 + `EraCore/Legacy/CrossEraResourcePolicy.cs`（缺口 G1 补全）+ 三套测试 30 项（§6） |
| 纪律 | era3/era.json 只读（缺项报协调方，见 §3 G2/G4）；era_transition.json 不动（T-117 声明范围） |

---

## 一、E2→E3 逐资源审计矩阵

内容侧=era3/era.json 声明（T-112 冻结）；实现侧=T-074 `EraTransitionService` 实际处理路径。
资源全集=E2 时代池可能持有的全部 ResourceKey（六基础资源+中间锭+弹药）。

| 资源 | id（K-2） | 刷新 | 丰度乘数 | 30% 带走 | 3:1 折算 | 实现侧核对 | 证据 |
|---|---|---|---|---|---|---|---|
| 铜 | `era2:res-copper` | ✓ 传承刷新（from_era:2） | ×0.5 | ✓（有用） | ✗ | ✓ 有用分支——入新池 | era3/era.json legacy_refresh 行 + `era3:recipe-bullet` 输入（钢1+铜1） |
| 铁 | `era2:res-iron` | ✓ 传承刷新（from_era:2） | ×0.5 | ✓（有用） | ✗ | ✓ 有用分支——入新池 | 同上 + `era3:recipe-steel` 输入（煤2+铁2） |
| 木材 | `era1:res-wood` | ✗ 不刷新（上代=紧邻 E2；E1 二资源不再续刷） | — | ✗（无用） | ✓ | ✓ 折算分支 | era3/era.json `_resources_note`（"上代"=紧邻前代裁定）+E3 配方无 wood 输入 |
| 石料 | `era1:res-stone` | ✗ 不刷新 | — | ✗ | ✓ | ✓ 折算分支 | 同上 |
| 粮 | `era2:res-grain` | ✗ 不刷新（Staple 位不入刷新对） | — | ✗ | ✓ | ✓ 折算分支 | era3/era.json `_resources_note`（粮=Staple、马=物流位） |
| 马匹 | `era2:res-horse` | ✗ 不刷新（物流位不入刷新对） | — | ✗ | ✓ | ✓ 折算分支 | 同上 |
| 铜锭/铁锭 | `era2:res-*-ingot` | ✗（中间品） | — | ✗ | ✓ | ✓ 折算分支（泛型 ResourceKey） | EraTransition.ComputeLevy 无例外分支 |
| 旧弹药 | `era2:ammo-*`（arrow/stone-bolt/iron-bolt/siege-bolt/fire-oil） | ✗ 弹链本代化 | — | ✗ | ✓ | ✓ 折算分支 | §二 裁定 |

**折算口径**（era_transition.json levy，T-074 冻结）：带走=`floor(carried_share×存量)`（0.3）、留存侧恰 70%；带走份额中无用途行折算=`floor(带走/3)`（useless_fold_divisor=3）入文明基金（用途=仅传承商店，不碰核心数值）。E2→E3 守恒测试见 §6 T2（逐行 `旧存量=带走+留存`，基金 Σ=243 由十行逐值断言）。

**本代三资源**（era3/era.json 本代行）：煤 `era3:res-coal`（新矿）、钢 `era3:res-steel`（`era3:recipe-steel` 输出件，非原料——SupplyChain one key one meaning）、铁=沿用 E2 命名空间跨代保留（既在刷新对又是 §4 E3 行点名资源）。仓库等级 E2=2→E3=3（跃迁自动 +1，测试断言）。

## 二、弹链映射裁定：旧弹链**自动退役**（非可续用）

**裁定**：E2→E3 跳代时，旧弹链（`era2:ammo-*` 池内存量）走 3:1 折算文明基金通道退役；E3 无任何续用入口。三源证据：

1. **塔侧消费域**（`game/content/era3/towers.json`）：三塔 `ammo_item` 值域={`era3:ammo-bullet`（加特林）, `era3:ammo-shell`（臼炮）, null（铁甲墙纯阻挡，ammo_per_shot=0）}——无 `era2:ammo-*` 消费者。
2. **配方侧生产域**（`game/content/era3/recipes.json`）：输出域={steel, bullet, shell}——本代弹链全量本代命名空间；E3 弹=钢+铜（bullet：钢1+铜1）与钢+煤（shell：钢2+煤1），**铜以传承刷新资源身份作原料**（"弹链本代化"中旧资源的角色=不作弹、作料）。
3. **事务侧出口**（`EraTransition.cs` ComputeLevy L340-353）：`useful = NewEraResources.Contains(resource)`——`era2:ammo-*` ∉ era3 有用集（§一矩阵）→ 带走份额 3:1 折算。

设计对应：v3.2 §4 跨代资源行"弹链本代化"+“无用途旧资源 3:1 折算文明基金"——本代化=配方/塔弹全量本代命名空间；退役出口=折算。一致性测试 §6 T2 `E3_ammo_chain_is_fully_native_and_old_ammo_retires_into_the_fold`（闭包断言+era3 五文件全文无 `era2:ammo-` 引用）。

## 三、实现侧核对：T-074 通用框架 vs era3 实配置的缺口清单

| # | 缺口 | 位置 | 处置 | 归属 |
|---|---|---|---|---|
| G1 | `NewEraResources`（折算有用集）无生产侧派生——T-074 测试手写集合，era3 实配置无入口 | `EraTransitionRequest` 参数 | **本任务补全**：`CrossEraResourcePolicy.Parse(era.json)` 唯一派生源（有用集+刷新行+AbundanceOf） | T-115 ✓ |
| G2 | `abundance_multiplier`（×0.5）无引擎消费者：E3 关卡地图未落位，丰度折减目前仅以关卡 map 比率逐关声明物化（E2 先例：三关 forest=0.05=e1 基准 0.10×0.5，§四表 3） | era3/levels.json 无 map 块（T-112 范围=排期侧先行冻结） | era3.json 只读未动；引擎侧 `AbundanceOf` 已可供电图/守卫（§6 T3 已用于 E2 语料断言） | **T-116**（战役与关卡制作落 map 块；缺项报协调方） |
| G3 | era_transition.json 未扩展 2→3：`final_levels` 无 "2" 行、`first_three_schedule` 仍指 E2 关 | content/legacy/era_transition.json | 不动（era3.json `_resources_note` 明示"E2→E3 扩展=T-117"）；negative 测试钉死缺口（§6 T4），T-117 落位后转绿为内容回归 | **T-117** |
| G4 | 跨代大陆资源节点无资源类型：`NewContinentFactory` 12 节点为通用占位（occupancy 注册），不区分煤/铁/铜，亦不应用传承 ×0.5 丰度 | `RelicMapper.cs` + relic_mapping.json `resource_node_count` | 结构性缺口：需内容 schema 扩展（节点→资源 id 映射）+设计裁定（大陆侧矿脉是否分类型/分丰度），超出"最小补全"边界 | **协调方裁定→T-117+** |
| G5 | E2 语料"石料刷新"未物化：era2.json 声明 stone ×0.5 刷新，但 e2 三关 rock_ratio=0（零矿格，§四表 1）——声明与语料不一致 | levels/e2.json | 审计观察：按"稀缺仅指定关"可释为逐关取舍，但已发行全量零矿使刷新行形同虚设；E2 手工关实测随 T-095 DEFERRED | **协调方裁定**（改语料或改声明） |
| G6 | 生产侧无 E2→E3 跃迁组装点（`EraTransitionService` 仅测试调用；CampaignLoop 组装=E1→E2 集成链） | CampaignLoop | 与 G3 同因（内容表未扩展则组装无从读 2→3 参数） | T-117 |

**核对结论**：T-074 通用框架对 §一矩阵六行**零代码缺口**（有用谓词/折算除子/基金入账/新池组装全为参数驱动）；缺口集中在**内容配置（G2/G3）与派生入口（G1，本任务已补）**。

## 四、稀缺分布审计（程序化扫描数字表）

扫描路径=真引擎（`GridWorld` 逐格单抽 + `NewContinentFactory` E2→E3 派生 seed）；输出落 trx（§6 T3），数字如下。

**表 1 · 已发行语料（e1/e2 全部 7 关，真 seed 真生成器）**：rock_ratio=0 → 矿格数=0

| 关卡 | seed | 图幅（格） | rock_ratio | rock 格数 |
|---|---|---|---|---|
| campaign:level-e1-01 | 20261101 | 16×24 | 0.0 | 0 |
| campaign:level-e1-02 | 20261102 | 16×24 | 0.0 | 0 |
| campaign:level-e1-03 | 20261103 | 16×24 | 0.0 | 0 |
| campaign:level-e1-04 | 20261104 | 16×24 | 0.0 | 0 |
| campaign:level-e2-01 | 20261201 | 20×26 | 0.0 | 0 |
| campaign:level-e2-02 | 20261202 | 20×26 | 0.0 | 0 |
| campaign:level-e2-03 | 20261203 | 20×26 | 0.0 | 0 |

**表 2 · 机制可控性**（rock_ratio=0.10，32×24=768 格，50 seeds）：

| 统计 | 值（矿格占比，无量纲） |
|---|---|
| min | 0.0755 |
| mean | 0.1003（贴声明值 0.10） |
| max | 0.1185 |
| 4σ 二项带 | [0.0567, 0.1433]（σ=√(p(1−p)/格数)=0.0108） |

**表 2b · 零矿稳健性**：rock_ratio=0，40 个异 seed → 全零（矿格注入的唯一通道=逐关比率参数——生成器无随机赠矿路径）。

**表 3 · 传承刷新 ×0.5 语料物化**：e2 三关 forest_ratio=0.05 = e1-01（教学关基准）0.10 × 0.5（乘数读自 era2.json legacy_refresh）；e1 四关 forest∈[0.08,0.15] 全严格高于刷新档。

**表 4 · E2→E3 跨代大陆**（`DeriveSeed(worldSeed, 3)` 派生路径，12 world seeds）：

| 统计 | 值 |
|---|---|
| 资源节点数 | 12/12（=relic_mapping.json `resource_node_count` 声明值；全部 Chebyshev 距 >让位半径 10 格） |
| 矿格占比（让位后） | min 0.0910 / mean 0.0949 / max 0.0980（期望 0.0952=0.10×(96²−21²)/96²，核心让位区岩格清平原后） |

**"稀缺仅指定关"验证结论**：
- **不稀普遍 ✓（机制层）**：矿格（Rock=矿位代理）注入唯一通道=关卡 map.ratio 显式声明；未声明（=0）的关在任意 seed 下零矿格（表 1+2b，7 真关+40 异 seed）。
- **稀关有 ✓（机制层）**：声明 r 的关覆盖率=r±二项带（表 2，mean 0.1003 vs 0.10）——数值可控，支持"富煤矿只在特定关"的逐关点配。
- **内容层未落地（诚实披露）**：era3 levels.json 三关无 map 块（T-112 范围=排期侧）——**煤的"只在特定关"尚无内容载体**；前向守卫测试（§6 T3）已钉：T-116 落 map 块后守卫翻转（届时应改为：煤位字段仅出现于稀缺标记关）。且 GridWorld 地形四值（Plain/Forest/Rock/Water）无资源细分——"煤≠岩"的类型化同样待 G4 裁定。

## 五、缺口补全清单（本任务交付的实现侧）

| # | 补全 | 文件 | 说明 |
|---|---|---|---|
| F1 | 折算表派生唯一源 | `game/src/EraCore/Legacy/CrossEraResourcePolicy.cs`（新增） | era.json resources 行（含 legacy_refresh）→ `NewEraResources` 有用集+刷新行+`AbundanceOf(resource)`；严格装载（结构违约点名抛）；结构性校验带（from_era 必为前代、乘数 (0,1)——刷新不得增强）不含设计数值（RL7） |
| F2 | 测试夹具 era 目录寻址 | `game/src/EraCore.Tests/Legacy/LegacyTestSupport.cs`（+`EraDir`） | 仓库相对（RL9） |

补全边界：不改 era3/era.json（只读）、不改 era_transition.json（T-117）、不改 relic_mapping.json（G4 待裁定）、不动 World 生成器（机制层无缺口——表 2 证明比率即控制量）。

## 六、测试与证据（30 项全绿）

| 套件 | 文件 | 覆盖 |
|---|---|---|
| T1 策略派生 | `EraCore.Tests/Legacy/CrossEraResourcePolicyTests.cs`（8 项） | legal：era3 有用集恰四资源+铜/铁刷新 ×0.5；era2 集∩E1={wood,stone}（与 T-074 手写集同源）。boundary：最小单资源文档。illegal：重复 id/from_era 越界与自指/乘数 0 与 1/缺 id/缺 items/era_index 越界——逐项点名断言 |
| T2 折算守恒+弹链 | `EraCore.Tests/Legacy/CrossEraE2ToE3LevyTests.cs`（4 项） | legal：真 T-074 事务跑 2→3（真 era_transition.json 仅补 final_levels["2"] 的 spec 副本）：十行守恒 `旧存量=带走+留存`、带走=⌊30%×存量⌋、八无用途行折算逐值（Σ基金=243）、铜/铁 ⌊30%⌋ 入新池且旧资源/锭/弹药全不入、仓库 2+1=3、campaign 域恰一次写；弹链闭包（塔弹∈配方输出+era3 五文件无 `era2:ammo-`）。boundary：小额池全 floor 到 0。illegal：未补丁真内容表 2→3 硬拒（T-117 缺口钉死） |
| T3 稀缺覆盖扫描 | `EraCore.Tests/World/ResourceScarcityCoverageTests.cs`（13 项+7 数据行理论） | §四全部数字的断言化：7 真关零矿格、40 异 seed 零矿、0.10 二项带、扫描器负向对照（满岩图≈1 防恒零假绿）、E2 丰度物化、E2→E3 大陆节点数/避让/岩格带、era3 前向守卫（无 map 块）、e1/e2 分布字段显式声明 |
| T4（并入 T2 illegal 行） | — | G3 缺口的 regression 形式 |

CI：`bash tools/ci/run_all.sh` 全绿（G2 红线 9 检查 0 违规／G2.5 资产 521 文件 0 拒收／G2.6 模组 0 FAIL／G1 测试 5665 通过 0 失败／G3 Release 零警告／G4 冒烟 SMOKE_OK）。**执行环境说明**（诚实披露）：验证跑在与本 commit 内容逐一相同的检出（HEAD+本任务六文件）上——主工作树当时含并行泳道 T-114 的在途 Units 改动（其 FortressBattleTests 中途态红）与共享数据根 `D:\game\game\data` 的双测试主机争用（全量并行下 testhost 崩溃与性能门禁偶发超线，隔离重跑皆绿；静机全量即全绿）——与本任务变更无关，不纳入本 commit 判据。

## 七、结论

- 验收四项：**丰度 ×0.5**=内容声明（era3.json 铜/铁 ×0.5）+E2 语料物化（0.05=0.10×0.5）+引擎派生（`AbundanceOf`）三线闭合，E3 地图载体待 T-116；**弹链映射**=自动退役裁定（三源证据+闭包测试）；**3:1 折算**=T-074 框架零缺口+E2→E3 守恒测试本任务落定；**稀缺仅指定关**=机制层验证成立（唯一注入通道+带内可控），内容层待 T-116（前向守卫已钉）。
- 报协调方两项裁定：G4（跨代大陆资源节点类型化+×0.5 应用）、G5（E2 石料刷新声明 vs 语料零矿）。

## 变更记录

- v1.0（2026-10-13，T-115）：首版。审计矩阵+弹链裁定+缺口清单 G1~G6+扫描数字表+补全 F1/F2+测试 30 项。单 commit「T-115: cross-era audit E2->E3 (abundance, scarcity map, fold-in gaps)」。
