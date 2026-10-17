# REV-20261018-T135 — 终章内容与机制（T-135）

| 项 | 内容 |
|---|---|
| 任务 | T-135 终章内容与机制（Wave 8 批 2；L 级拆 3 PR） |
| 覆盖特性 | F-008（六时代体系数据表·终章行）、F-018（终章塔组）、F-060（时代奇观系统·纪元之门）＋F-003/F-005/F-009/F-010/F-012/F-033/F-042/F-055/F-092 同构面 |
| 组件 | C-02（内容注册表）＋C-19/C-20/C-08/C-07 接线面 |
| 规格真相源 | v3.2 §4 L63 终章行（资源 反物质/暗物质；能源 零点能；物流 传送阵；塔防特色 时空塔/奇点炮；Boss 堕落守望者 HP120000/甲40；系统形态 真相揭示（播片对话））+§4 L68 公式代入 E=6+§4 能源阶梯行（终章未列风险项）+§5 L78~L79（物流阶梯末位 ∞；仓库 6）+§13.1 L204（终章 Boss 画布 96×112）+SP-1b L56~L57（终章塔预归类）+SP-4 L142（适用代=E3 起，无终章行）+附录 F.3（奇观链第六环 纪元之门=通关之门）+v3.2 §12 L188~L191（叙事锚） |
| 分支 | task/C02-F018-F060-finale-era（自 master=0e0c389 拉出） |
| PR 提交 | PR1=0f6c6e6（era6 五件套+e6.json+final_levels[5]+校验）；PR2=62074cd（跃迁域+纪元之门板+宿主面 6）；PR3=af073fc（可玩验收+零点能装配面+autopilot E6 行） |
| 状态 | 交付（3 PR；本 REV=验收记录+局限如实） |

## 一、交付物（3 PR）

### PR1 — era6 数据五件套+levels/e6.json+final_levels[5]（内容与校验）

- `game/content/era6/{era,towers,enemies,recipes,levels}.json`：终章数据五件套
  （v3.2 §4 L63 逐字锚点：资源 反物质/暗物质；能源 零点能 10000000/40000000/200000000
  ［×4/×5 ∈3~5 带，跨代步 ×5］；物流 传送阵=阶梯末位 ∞（throughput null+TransportTiers.
  Teleporter 同值）；塔 时空塔=减速/奇点炮=能量［SP-1b L56~L57 预归类 T-135 追认］；
  Boss 堕落守望者 HP120000/甲40+画布 96×112［§13.1 逐字锚点，canvas_size_px=96+
  canvas_height_px=112 双字段］；系统形态 真相揭示（播片对话——本体=T-136，本行=数据位）；
  仓库等级 6；risk_events=false 无 risk/annihilation/meltdown 块——**零点能=无风险能源，
  依据：v3.2 §4 能源阶梯行风险列（E1 粮仓/E3 过热/E4 熔毁/E5 湮灭，E2 无，终章未列）
  +SP-4 L142 适用代=E3 起负载率类风险表无终章行，两处真相源一致**）
- `game/content/levels/e6.json`：终章手工关 5 关（前三关排期+e6-04 重甲对策关+
  e6-final 堕落守望者 Boss 关=通关之门，requires_wonder=campaign:wonder-era-gate 数据位）
- `game/content/legacy/era_transition.json`：final_levels["5"]=campaign:level-e5-final
  （第五跳引用落位——实关行已随 T-134 交付，两侧齐备不悬空）+first_three_schedule
  target_era=6 三行（逐值对齐 era6/levels.json 冻结侧）
- 测试基建：HandcraftedLevelData（era6 converter_recipe 闭表守卫+requires_wonder
  可选字段解析+注释更新）、HandcraftedLevelContent（Era6ScheduleRows+EraDir era6）
- 冻结锚点按规程更新（T-117/T-125/T-134 同款先例）：LegacyContentTests
  （FinalLevels[5]+键域 1..5 封闭+排期 12→15 行）、Era5HandcraftedLevelSetTests
  （final_levels[5]『不预置』断言翻转为落位断言）
- 新增测试：Era6ContentTests（~40 用例：时代行/能源阶梯/无风险双真相源/传送阵 ∞ 档
  引擎对齐/塔定类与 F.5 锚带/跨代成本 ×2 校准留档/敌表跨代步/Boss 96×112+重力脉冲
  /配方链深/前三关排期/三源矩阵/techtree 前向守卫/E=6 公式族（生产 API 实证：系统点 13/
  卷轴 18/BuildPlan 16）/注册表纪律/前引守卫）+Era6HandcraftedLevelSetTests（schema/
  引用闭合/排期一致性/通关之门两侧同源/装载确定性/负向突变×4）+Era6Handcrafted
  LevelPacingTests（15~40 分钟中位带/波节奏不触顶/数量公式复算/重甲线增线+零升级步）

### PR2 — E5→终章跃迁域+纪元之门板（通关之门）+CampaignState 宿主面小扩

- `game/src/EraCore/Legacy/WonderFinaleGateBoard.cs`：奇观链第六板（finale_gate 行×
  LegacyLedger 唯一交点；FinaleLevelEnterable/BlockingWonderId——跃迁硬条件语义的
  终点形态：前五环各闸一次跃迁（Precheck），第六环闸全游戏终局关）
- `game/src/EraCore/CampaignLoop/CampaignState.cs`：宿主面小扩 MaxHostedEraIndex 5→6
  （E5→终章 AdvanceEra 领养+终章文档恢复；六时代终点无第七跳——AdvanceEra 在 6 封顶）
- 测试：E5ToFinaleTransitionRegressionTests（30 对 seed 矩阵：30% 课税守恒+基金 270
  手算锚［nano 90+fusion-fuel 60+oil 120 走 3:1］+F.7 无丢失+新大陆确定性+排期=e6-01..03+
  仓库 5+1=6+G9 ≥95% 带+对抗大陆 E2E+硬前置负向（缺终局关/缺戴森云各拒且零写入）+
  D-35 传承行钉）+WonderFinaleGateBoardTests（冻结行/门锁点名/开门/e6-final
  requires_wonder 两侧同源闭环/构造守卫）+CampaignStateTests（出面集 {0,7}+E6 文档恢复
  往返经宿主面步行自动覆盖）+FinalLevelTransitionReachabilityTests（E5 全五关真通关→
  第五跳 Precheck 放行+负向扩 (5,e5.json,e5-final,era6)）

### PR3 — 零点能/传送阵档/终章可玩验收

- `Era6LevelPlaythroughTests`：五关 headless 全 Victory（命令流全量签发零拒收/
  Boss 出场 210000 手算锚/同 seed 双跑哈希全等/零点能装配面 EnergyRisk=null 与
  E5 挂表对照）
- `AutopilotEraGateTests`：Engage 泳道补 (6,true) 行——全托管可选档『E5 开、终章同开』
  延续（依据：系统形态阶梯=授权递进列，真相揭示=叙事形态升级不收回授权；闸门 >=E5
  谓词自然覆盖 E6）
- 传送阵 ∞ 档：Era6ContentTests 传送阵档位组（era.json throughput null ↔ TransportTiers.
  Teleporter ∞ 同值+Teleport 形态+世博会乘子下 ∞ 恒 ∞）。**BeltRenderDecisions.
  Era6GearItemsPerSecond 常量由并行 T-142 落入**（见 §五并行缝）
- 原型校准迭代（DiagE6Probe 探针实证链，探针不入库）：①奇点弹转换器暗物质输入
  缺载（初版命令流漏 dark→shell 载具指派→全五关塔零开火 ammo=0）——逐关补齐
  dark/iron→shell 载具线；②e6-04 移出长枪阵列（初版重甲+远程狙击混合线实测塔线
  被狙损不通关——e5-final 轻三线保塔线同款裁定先例；长枪阵列保留在 e6-02 实测可胜位）

## 二、数值锚点核对表（逐字锚点测试钉死）

| 锚点 | 值 | 出处 | 测试 |
|---|---|---|---|
| Boss HP/甲 | 120000/40 | v3.2 §4 L63 | Era6ContentTests+SetTests+Playthrough（出场 210000 手算锚） |
| Boss 画布 | 96×112 | v3.2 §13.1 L204 | Era6ContentTests（canvas_size_px+canvas_height_px 双字段） |
| E=6 公式族 | 系统点 13/卷轴 18/BuildPlan 16 | v3.2 §4 L68 | Era6ContentTests（生产 API：SystemPointLedger.Ceiling/ScrollEngine.ResidentCap/JsonSchemaValidator 16 过 17 拒） |
| 仓库等级 | 6（池上限 2000×6=12000） | v3.2 §5/附录 F.3 | Content+Transition+Pacing |
| 物流阶梯终章档 | ∞ | v3.2 §5 L78 末位 | Era6ContentTests（TransportTiers.Teleporter 同值） |
| 能源阶梯 | 1e7/4e7/2e8（×4/×5） | 类推锚带 [NEW] | Era6ContentTests（跨代步 ×5 断言） |
| 跃迁参数 | 30%+3:1+仓库+1 | 全代冻结 | E5ToFinale 矩阵（基金 270 手算锚） |
| 奇观链第六环 | 纪元之门 era_index 6/finale_gate | 附录 F.3 | WonderFinaleGateBoardTests+SetTests 两侧同源 |

## 三、run_all 记录（终跑=三 PR 落库后，共享工作树含 T-142 在途态）

| 步 | 门禁 | 结果 |
|---|---|---|
| 1 | G2 红线扫描 | PASS（9 checks 0 FAIL 0 WARN；R7 锚点 16 处——含 CampaignState.MaxHostedEraIndex=6 的 ci-allow 登记） |
| 1.5 | G2.5 资产规格 | PASS（641 files rejected=0） |
| 1.6 | G2.6 模组离线 | PASS（3 mods 8 文档） |
| 2 | G1 测试全绿 | **PASS 6483 passed / 0 failed（959s）**——含本任务增量与 T-142 并行增量（共享工作树同跑；批 1 基线 6353/6354） |
| 3 | G3 Release 构建 | PASS（零警告，TreatWarningsAsErrors=true） |
| 4 | G4 Godot 冒烟 | PASS（SMOKE_OK；ART6 era=finale belt=e6@inf/s——T-142 终章美术包已在树） |

本任务域套件：Era6ContentTests+Era6HandcraftedLevelSetTests+Era6Handcrafted
LevelPacingTests+Era6LevelPlaythroughTests=72 全绿；E5ToFinaleTransition
RegressionTests（30 对 seed 矩阵）+WonderFinaleGateBoardTests+FinalLevel
TransitionReachability（E5 真通关链）=11 全绿；CampaignStateTests/Autopilot
EraGateTests/LegacyContentTests/Era5HandcraftedLevelSetTests 更新行全绿。
批 2 派工时 T-153 配额再校准已先行（0e0c389，纪律遵守）。

## 四、局限与诚实披露

1. **跨代成本基点步 ×2（非 ×3 链值）**：奇点炮 4860=2430×2（[NEW-T-135 原型校准]，
   非 7290）——池上限公式 2000×等级 线性 vs ×3 跨代链指数在终章相交破产
   （7290×2=14580=池上限 121.5%，双炮压轴线不可建造）；×2 步=敌表 HP×2/塔 DPS×2
   同幅（单位 EHP 造价持平），4860×2=9720=池上限 81%（E5 终局 97% 同形）。F.5 严格
   ×3/级律=线内档步冻结律（终章无线内步——两塔 max_tier 1），跨代基点=E5『30→90→
   270→810→2430』类推惯例 [NEW] 可校准。推导链测试留档（Cross_era_cost_basis_is_
   calibrated_at_two_not_the_bankrupt_three）。
2. **时长估算 RED-FLAG 沿承**：终章 headless 实测通关时刻远超估算器口径（e6-01
   3830s vs 估算 ~940s）——T-084 实锚 ~3× 结构性偏差的延续（含泄漏敌滞留核心位的
   清场尾部）；15~40 分钟带仅估算模型内成立，实测口径归 T-095。
3. **终章英雄行缺席**：era6 无 heroes.json（SP-6 占位、随章节设计定案落位——e4/e5
   同款诚实装载面）；hero_recruit=排期位先行冻结。
4. **Boss 重力脉冲=数据位先行**：traits.gravity_pulse（900t/3.5 格/800 点）引擎消费
   随 Boss 战宿主扩展（E5 主宰同款纪律）；画布 canvas_height_px=112 为 T-142 美术包
   消费的数据位。
5. **传送阵可玩面=数据位+引擎档**：TeleporterLink 逐件耗能钩子=T-064 已实装引擎位；
   关卡命令流干线=drone 20/s（传送阵非 trip 形态不入 assign-carrier 封闭档位表，
   e4 传送带 data-first 同款先例）——传送阵的关卡可玩接线（发电机动词不在 19 动作
   封闭清单）随能源建造面立项。
6. **Endless 只读核查**（勿动纪律）：EndlessEntry 时代枚举=动态读取零硬编码
   （"时代数不落码面"，EndlessStartBaselineTests 注释自证）；EndlessStartBaselineTests
   的 [InlineData("era1",1)..("era4",4)] 为参数化样本行非封闭注册表，era6 合成集入局
   =宿主侧追加样本行（非必须）；三榜/基准无 E1~E5 硬编码块——零遗留，无需 REV 挂账。
7. **techtree 终章节点=前向守卫空转**：campaign/techtree.json 无 era_index=6 节点
   （E5 同款；wonders.json 纪元之门行 tech_node=campaign:tech-e6-wonder-era-gate
   不在 techtree——T-124 §八4 奇观建造生产接线缝的全代既有 DEBT，非本任务新增）。

## 五、并行缝（T-142 同树并行）

- 批 2 派工=T-135+T-142 并行（≤2 纪律）；T-142 在同一工作树推进 era5/终章美术与
  AudioDirector 曲目。本任务期间工作树出现的 audio（bgm-programs.json/AudioDirector/
  Main.cs/audio tests）与 presentation（textures/era5、Era6GearItemsPerSecond 常量）
  改动**均非本任务所写**，git add 只加本任务路径（纪律 7），不代提交。
- BeltRenderDecisions.Era6GearItemsPerSecond（∞ 档常量）与 era5 皮肤档位由 T-142
  落入；本任务侧 Era6ContentTests 的 ∞ 档位断言锚 TransportTiers.Teleporter
  （引擎档），与该常量同值不耦合其提交时序。
- 全量门禁期间观测到 2 个 audio 测试红（BgmProgramTests——失败消息位于 T-142 在途
  编辑的测试体内：突变化串非法 JSON 与时值减半失配），归属 T-142 在途态非本任务
  回归（隔离证据：涉事文件均不在本任务改动集；本任务域套件全绿）。

## 六、遗留

| 项 | 归属 |
|---|---|
| 传送阵关卡可玩接线（能源/传送建造动词不在 19 动作清单） | 后续内容/进度任务 |
| 终章英雄行（SP-6 定案） | 后续章节设计任务 |
| Boss 战宿主的 gravity_pulse 引擎消费 | 后续 Boss 战扩展 |
| 奇观建造生产接线（tech_node 悬空，全代既有） | T-124 §八4 既有 DEBT |
| 终章叙事/播片/台词（真相揭示本体） | T-136（下一批） |
