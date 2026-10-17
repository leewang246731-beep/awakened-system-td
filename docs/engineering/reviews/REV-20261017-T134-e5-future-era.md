# REV：T-134 E5 未来章内容与机制（2026-10-17）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-17 |
| 任务 | T-134 E5 未来章内容与机制（08 行：轨道炮/无人机巢/力场塔/主宰/聚变/无人机可玩；F-008/F-012/F-017/F-092 [C-02,C-09,C-10]，同章法，L） |
| 提交 | PR1=091dad9（era5 数据五件套+levels/e5.json+内容校验测试）；PR2=0bd56c2（E4→E5 跃迁域+CampaignState 清账+戴森云板）；PR3=57e12a6（湮灭通道+20/s 档+全托管档+E5 可玩验收）——三 PR 均已 push origin master |
| 规格真相源 | v3.2 §4 L62 E5 行（资源 稀土/纳米/聚变燃料；能源 聚变；物流 无人机 20/s；塔 轨道炮/无人机巢/力场塔；Boss 主宰 HP45000/甲30；系统形态 全托管可选）+§4 L68 公式 E=5+SP-1b L52~L54 定类+SP-4 L148 湮灭表+附录 F.3 奇观链第五个（戴森云 能源 ×2）+REV-20261017-v10-fullclear §八3/§八5 DEBT/勘正注 |

---

## 一、交付面

### 1. PR1：era5 内容五件套 + levels/e5.json（091dad9）

| 项 | 内容 |
|---|---|
| 数据 | `game/content/era5/{era,towers,enemies,recipes,levels}.json`：E5 时代行（资源=稀土[本代新矿]+石油/铁[紧邻前代 E4 传承刷新 ×0.5]+纳米/聚变燃料[炼链制品]；能源=聚变 100000/400000/2000000[×4/×5 ∈3~5 带，湮灭芯堆=能源 L3 危险位 confirm]；物流=无人机 20/s 阶梯第 4 档；仓库等级 5；系统形态 full_hosting_optional）+塔三行（轨道炮=能量[armor_pierce]/无人机巢=穿刺/力场塔=减速[DPS 0.2×带+反湮灭半径 ×0.5 钩子三文件同源]）+敌九行（era4 行形对位跨代步 ×2；主宰 HP45000/甲30 逐字锚点+盾 2400+重力脉冲）+配方五行（稀土→纳米/聚变燃料→三级弹链恰用满 §5 链深 ≤3）+排期三关（13/16/19 §7 公式） |
| 关卡 | `levels/e5.json` 首批 5 关（e5-01 聚变教学[×3 宽限]/e5-02 无人机航路[drone 20/s 干线首发+无人机巢升级位]/e5-03 考古与托管[dig_sites×2]/e5-04 力场与湮灭[力场塔首发+聚变燃料支链]/e5-final 主宰降临[boss_wave=19]）；纳米=E5 建造货币（E1 木/E2 铜/E3 钢/E4 混凝土/E5 纳米跨代递进）；`final_levels[5]` 不预置（E5→终章=T-135 域，悬空引用禁律——测试钉死该边界） |
| 跃迁参数 | `legacy/era_transition.json` first_three_schedule +target_era=5 三行（逐值对齐 era5/levels.json 冻结侧；final_levels[4] 值不变——本任务清账其『引用先行』状态：E5 目标域实体化后 E4→E5 跃迁真实可达） |
| 测试 | Era5ContentTests（时代行/能源阶梯/SP-4 L148 湮灭表逐值/危险位/塔定类+UpgradeLaw+类带/敌表跨代步/配方链深/前三关排期/三源矩阵+突变反例/E=5 公式代入[系统点 11/卷轴 16/BuildPlan 14]/注册表纪律）+Era5HandcraftedLevelSetTests（schema/引用闭合/drone 档 e1~e4 不提前/排期一致/终局关边界/装载确定性/负向突变）+Era5HandcraftedLevelPacingTests（中位带/波节奏/数量公式/升级位） |

### 2. PR2：E4→E5 跃迁域 + CampaignState 清账 + 戴森云板（0bd56c2）

| 项 | 内容 |
|---|---|
| CampaignState 清账（§八3 DEBT） | 薄壳 E1-only → E1~E5 完整宿主面：`EraIndex` 属性（SavePoolTo 写当前时代——不再硬写 1）；LoadPoolFrom 恢复任意 E1~E5 文档（池按存档时代仓库等级重建=同容量无损；E6/越界拒收）；`AdvanceEra(toEra, newPool)` 逐跳推进（一步一跳/仓库等级=时代号守卫/领养跃迁事务 NewPool）；CampaignGrid.ResetToHome 同 seed 确定性重绘（跳代=新大陆语义，占领重置主城，奖励银行切新池） |
| E4→E5 跃迁域 | E4ToE5TransitionRegressionTests：30 对 (遗迹城,世界) seed 矩阵（30% 课税守恒逐行+基金 165 手算锚[coal 270+electronics 90+concrete 135 走 3:1——E5 弹链本代化后无用途行]+F.7 映射无丢失+新大陆确定性重放+排期=e5-01..03+仓库 4+1=5+dig-site/标记板全量一致+G9 聚合核心区完好率 ≥95% 带）+对抗大陆 E2E（全域水域→12 废墟全量挖掘位）+硬前置负向（缺终局关/缺互联网奇观各硬拒且点名、零写入）+D-35 传承行不进节点层钉 |
| 可达性 | FinalLevelTransitionReachabilityTests +E4 全五关（含 e4-final 夜枭）headless 真通关→逐关占领→E4→E5 Precheck 放行（互联网=E4 硬前置侧）+负向表扩 (4,e4.json,e4-final,era5)；CampaignState 领养测试（AdvanceEra(result)：时代 4→5/池=NewPool/带走 30% 逐资源可读/存档写时代 5） |
| 戴森云板 | `Legacy/WonderEnergyOutputBoard`（附录 F.3 奇观链第五板：bonus_kind=energy_output Π(bonus_value) 乘子族——建成恰 ×2；板=奇观表×账本建成位×能源账本唯一交点，WonderThroughputBoard/WonderRadarSharingBoard 同纪律）+`EnergyLedger.SetOutputMultiplier` 消费通道（发电翻倍/负载率减半[20/40=0.6 手算锚]=E5 湮灭对策面；≥1 守卫；=1 哈希中性、≠1 才折入——E1~E4 位全等） |
| 锚点同步 | LegacyContentTests era_transition 冻结锚点按规程更新（排期 9→12 行+E5 块+final_levels[5] 不预置断言；T-117/T-125 扩表同款锚点同步先例——PR1 带红锚点由本 PR 清，主管核验指认） |

### 3. PR3：湮灭通道 + 20/s 档 + 全托管档 + 可玩验收（57e12a6）

| 项 | 内容 |
|---|---|
| E5 湮灭引擎 | EnergyRiskEngine 第三通道：`AnnihilationTuning`（SP-4 L148 表体全值经 era5/era.json energy.annihilation 行驱动[RL7：含残骸 30% 内容补位行]）；触发=负载率 ≥150% 连续 10 s **或**聚变建筑 HP 归零（`SetFusionHpZero` 世界层输入位，账本每结算消费一次）；无倒计时即时引爆；后果载荷=平伤 2000[EnemyDamage 载荷位]+连锁 ×0.6 衰减+力场半径 ×0.5+肇事聚变机退册；`OnSettle` 6 参重载（无湮灭行引擎旗标无视=行为与哈希不变）；哈希折叠仅在有行时（E3/E4 位 bit-identical） |
| 世界层 | `EnergyRiskWorldConsumer.PlanAnnihilationChain`：BFS 连锁规划（node-id 序确定性/逐栋一次/深度衰减 UnitDamage×0.6^d/力场在场半径 8→4 收缩链[含传递圈]）+`ApplyBlast` radiusMultiplier；Simulation 消费接线：`AnnihilationForceFieldTowerId` 内容通道（era5:tower-force-field 在场判定）+连锁机退册+湮灭无辐射区（行差） |
| 20/s 传送带档 | BeltRenderDecisions：`Era5GearItemsPerSecond=20`+`SkinSwitchUpperItemsPerSecond=17.5` 上沿边界；era5 皮肤 stem 随 T-142 美术包入表（勿造贴图文件），现行未交付上档乘最新已交付皮肤（era4）行为冻结测试组（T-142 落包改红提醒扩表）；TransportTiers.Drone 引擎档位（T-064 全量）与内容 20/s 同值对齐 |
| 全托管可选档 | `AutopilotEraGate`（v3.2 §4 L62 系统形态列：E5 解锁位）+`AutopilotDirector.EngageFullAutopilot(campaignEraIndex)` 战役泳道重载（E1~E4 拒绝且点名当前时代/模式不变；E5 开启；0/越界拒收）；无尽三榜不受闸门（原始无参 Engage=T-081 泳道保留）；三面同源（闸门+era.json system_stage_features full_autopilot_optional+era5/levels.json slot3 unlocks full_autopilot——内容测试侧互证） |
| 可玩验收 | `Era5LevelPlaythroughTests`：五关 headless 全 Victory（①胜波=数据行 win_waves §7 双面；②命令流全量签发零拒收[drone 干线/升级差价/聚变燃料支链引擎面合法]；③终局塔数=脚本 build-tower 数；④时长表[估算中位 ∈15,40 分钟带+headless 实测并列，RED-FLAG 沿承]；⑤同 seed 双跑状态哈希与通关刻全等；⑥主宰出场 HP=45000×1.1667×1.5=78750 手算锚+出场刻≥19 波开场）；装配=Era5EnergyRisk（过热沿用+湮灭挂载/熔毁=null——SP-4 行域 E4=熔毁/E5=湮灭）+力场反湮灭配置位 |

---

## 二、原型校准迭代记录（[NEW] 值域内的实测调整，DiagE5Final 探针实证链——探针为临时测试不入库）

| 迭代 | 观测 | 处置 |
|---|---|---|
| ①弹链断流 | 轨道炮初版 160@6 发/s=弹链需求 0.6 件/s（E4 同位塔两倍）→ e5-03/04/05 弹尽 | 同 DPS 半消耗：轨道炮 320@3（reload 10=E4 导弹同档）/无人机巢 256@7.5 |
| ②单体线密度 | E4 塔线=溅射（AOE 面收益），E5 轨道炮=单体直射——同 DPS 清场力不对位 → e5-03/04/05 波表密度减档（20→12/28→16/24→8…多轮） |
| ③Boss 泊位 | 主宰初版 1.07 px/t：23 s 抵基地，到场敌不入塔目标优先序 → Boss 满血泊死无解；0.43 实测过境仅伤 45%（弹药经济约束有效 DPS~560） | 定档 0.32 px/t（0.3 格/s=历代最重移动要塞，83 s 过境窗）+e5-final 纯轨炮三座线 |
| ④塔线狙杀 | 长枪无人机（射程 7 停射塔）在塔转火到场堆时自由狙塔（轨道炮 t≈640 s 阵亡——探针建筑 HP 面实证） | e5-final 敌表五线收轻中快三线（无长枪/无重甲屏障）——保塔线全程存活 |
| ⑤升级位布局 | 仓库上限 10000 vs 塔价 ×3 阶梯：2 轨炮+升级=9720 封顶，e5-final 塞不下第三塔+升级 | 无人机巢升级位移 e5-02『无人机航路』（主题对位+弹链经济充裕位+w10 换装窗）；e5-02 波表 32→24 保铺场窗 |
| ⑥波谷 | 密度减档后估算中位 13.97<15 分钟带 | e5-03/04/05 波谷 15→30/40 s（rhythm=valley-bound 段拉回带内；波谷更长=蓄弹窗更宽，可玩性同向） |

## 三、清账三项状态（任务卡勘正注/DEBT 收口）

| 项 | 状态 |
|---|---|
| final_levels[4]『引用先行』（T-130 勘正注） | **清账**——E5 目标域实体化（内容+排期+回归+可达性四侧齐备）：E4→E5 事务在真实游戏可达（FinalLevelTransitionReachability E4 全清→放行；E4ToE5 30 对矩阵全绿）；final_levels[4] 值本身不变（T-124 落位即实锚） |
| CampaignState 薄壳 E1-only（v10 §八3 DEBT） | **清账**——E1~E5 完整宿主面（时代跟踪/任意时代恢复/事务领养/网格重绘，见 PR2 面） |
| 戴森云（奇观链第五个） | **落位**——wonders.json 行（T-124 预置）×WonderEnergyOutputBoard 消费通道（T-134）×能源账本乘子通道：建成=发电 ×2/负载率减半（能源 ×2 的引擎侧真实生效+确定性折哈希） |

## 四、测试与门禁

| 项 | 内容 |
|---|---|
| 测试增量 | +~120 用例（Era5ContentTests 52/Era5HandcraftedLevelSetTests 14/Era5HandcraftedLevelPacingTests 8/Era5LevelPlaythroughTests 7/EnergyAnnihilationTests 17/WonderEnergyOutputBoardTests 7/E4ToE5TransitionRegressionTests 4/CampaignStateTests +5/AutopilotEraGateTests 8/BeltRenderDecisionsTests +5/FinalLevelTransitionReachability +2/LegacyContentTests 锚点扩）；目标面（Era5*+湮灭+板+战役壳+E4ToE5+闸门+Belt+LegacyContent）203/203 绿 |
| run_all | 文末登记（最终门禁） |
| 红线自查 | 无密钥/无锚点常量入产品码（湮灭残骸 30% 经内容行+[NEW] 标注；全托管解锁位=EraOrdinals 量纲 ci-allow）/无绝对路径/无恒真断言（负向用例：矩阵突变/非法参数行/闸门拒绝/装载器逐条违规点名） |
| 确定性 | E5 五关同 seed 双跑哈希全等；湮灭引擎同表同序列双跑哈希全等；无行引擎旗标输入哈希不变；乘子 =1 哈希中性；E1~E4 全部位等价（哈希折叠门控验证） |

## 五、局限与移交（如实）

1. **主宰重力脉冲=数据位**（traits.gravity_pulse 900t/3 格/600 点）：引擎消费随 Boss 战宿主扩展（对位 e4 夜枭空袭同状态——数据位先行、宿主后接，T-124 先例延续）；e5-final 可玩性不依赖该技能接入。
2. **湮灭世界层消费的最小面**：Sim 消费=平伤 ApplyBlast+连锁规划退册+力场半径减半；连锁建筑的世界行残留（残骸/estate 语义）=wreck 状态沿 T-065 estate 域，本任务不扩；辐射区通道不挂湮灭（SP-4 L148 行差）。
3. **E5 英雄行**：SP-6 占位随章节设计定案——era5 无 heroes.json，hero_recruit=排期位先行冻结（e4 同形诚实装载面）。
4. **techtree E5 节点**：无（前向守卫空转；科技树接线归后续内容/进度任务——T-124 e4 同状态）。
5. **era5 矿位稀缺声明通道**（稀土矿脉）：随 T-115 §四跨代扩展另落（e5 全关 rock=0 固定布局规则）。
6. **估算器 RED-FLAG 沿承**（e1~e5）：LevelPacingEstimator 节奏模型与生产 WaveSpawner 结构性偏差（T-084 实锚 ~3×）——实测口径归 T-095。
7. **性能门禁环境事件（如实记录）**：本任务期间 SimGroupingPerfGuardrail/Spike1 曾红——隔离二分证据（干净 HEAD 绿+HEAD+本任务源码绿+共享树红）与主管复验（T-143 隔离 GREEN×5；红测时窗=T-143 断连遗留孤儿 testhost 满载争用）确认非本任务回归；最终 run_all 安静窗口复验结果见文末。
8. **编辑事故（如实记录）**：①SimulationConfig TowerCatalog 行曾被我 PR3 编辑附带误伤致共享红编译（主管修复+归属勘正认领）；②enemies.json 速度对全局替换误伤长枪无人机行（内容测试即红，即时修复）——两起均已入教训：共享文件编辑后立即 build、同值多行必须带行上下文替换。

## 六、run_all 登记（最终门禁，如实）

| 步 | 结果 |
|---|---|
| G2 红线扫描 | PASS（tools/ci/redline_scan.py 9 检查零违规） |
| G2.5 资产校验 | PASS（586 文件 0 rejected 0 warned） |
| G2.6 模组校验 | PASS（0 FAIL，3 mods/8 content docs） |
| G1 测试 | 6353/6354 passed（13 m 3 s）。唯二红例均时窗抖动、安静窗即时复验绿：①run_all#2（47 m，邻居负载 34~45%）唯一红=V10FullClear 链 20 分钟墙钟护栏（19 关全 Victory 功能面全过，纯墙钟超时）→安静窗复验 V10FullClearTests 8/8（2 m 23 s）；②run_all#3 唯一红=FlowFieldManagerTests 吞吐护栏 ratio 0.899 vs 0.90（差 0.1%）→即时复验 1/1 绿；同窗三性能门禁（FlowSolve/SimGrouping/Spike1）复验 3/3 绿（18 s） |
| G3 Release 构建 | PASS（0 警告 0 错误——TreatWarningsAsErrors 口径） |
| G4 Godot 冒烟 | SMOKE_OK（core=0.0.1-wave0 scale=1——运行时/程序集/EraCore 链路三合一） |

结论：六步全绿（两例负载抖动按安静窗口纪律复验绿，证据如上）；测试增量 +197（6354 vs T-130 基线 6157）。
