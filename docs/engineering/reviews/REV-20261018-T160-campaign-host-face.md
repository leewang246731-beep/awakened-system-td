# REV-20261018-T160 — 战役宿主脸（表现层战役装载与推进——补平「玩家从 UI 玩全战役」的最后一层）

| 项 | 内容 |
|---|---|
| 任务 | T-160（08 v1.26 行：战役宿主脸——表现层战役装载与推进；C-29 债=T-059 行「真实关卡装载器替换夹具装配路径」的收口） |
| 特性 | F-005（关卡选择与进度）/F-001（单局循环宿主面）/F-003（时代跃迁接线）/F-108（终章播片二选一消费）/F-002（胜利结算演出消费） |
| 依据 | 08 v1.26 T-160 行+T-059 行 DEBT/C-29；SimHost.cs 头注释「The real level loader (C-29/T-059) replaces the config assembly later」即本任务；REV-20261018-T157 §③「战役关现状如实登记：表现层现无战役关构造位——战役宿主面随后续任务立项时消费同一双注入缝」遗留①清账；REV-20261017-v10-fullclear（19 关+三跃迁无头链先例）；REV-20261018-T155（遥测双注入缝）；REV-20261018-T136（终章播片 FinalePlayback 二选一） |
| 交付物 | PR1 引擎最小扩（`EraCore/CampaignLoop/HandcraftedLevelContent.cs` 装载器生产化+`EraCore/CampaignLoop/EraLevelAssembly.cs` 生产组装律+`LevelSession.TryResume` 遥测缺口修复+测试侧薄委托）+`EraCore.Tests/Levels/CampaignHostAssemblyMatrixTests.cs`（29 关装载矩阵）；PR2 `presentation/EraTD/scripts/CampaignMenuUi.cs`+`scripts/CampaignLevelHost.cs`（战役菜单+真装载+通关推进+跃迁+终章+存档续玩+遥测；Main.cs 5q 接线）；PR3 `EraCore.Tests/Levels/CampaignHostProgressionChainTests.cs`（E1→E2 连续推进链） |
| 环境 | 本机开发机（D 盘纪律）；零网络（跃迁负向探针零提交）；零生图；G4 沙盒根 `.tmp-tests/campaign-smoke` 即用即清 |

## 一、边界声明（与 SimHost 夹具的关系——PR4 裁定）

**SimHost 保留为 headless 冒烟夹具，本任务不退役**。理由：SimHost（E1 测试夹具局）仍是 G4 冒烟既有十三段（BRIDGE/PANEL/CINE/NARR/AUDIO/UI/EXPAND/RENDER/WEB/REPLAY/PERSONA/L2/NET）的驱动宿主——CinematicDirector/NarrativeHud/AudioDirector/InfoLayersUi/ReplayUi 均以 `SimHost` 为构造参数，整体退役=十三段全部改接 LevelSession 的大迁移，超出本任务「表现层接线」的边界且会动 G4 判据（任务纪律明令「保留 SimHeadless 冒烟路径不动（G4 判据别破）」）。**真装载路径与夹具路径现已并存且分账**：

- 夹具路径（保留）：`Main._Ready → new SimHost()`（E1 锚点+里程碑命令流）——只服务冒烟各段；
- 真装载路径（本任务落）：`Main._Ready → CampaignMenuUi.Instantiate → CampaignLevelHost → HandcraftedLevelCatalog.Parse + EraLevelAssembly.BuildConfig/BuildCommands → LevelSession`（e1~e6.json 全 29 行，TryResume 续玩两路）。

E1 夹具退役（SimHost 缩减为纯冒烟驱动或迁移十三段到真装载路径）**挂账后续任务**（迁移面=G4 十三段断言逐段等价改造，量级另立任务卡）。

## 二、引擎最小扩依据（「勿改引擎」红线的最小例外，逐条）

表现层无法引用 EraCore.Tests（测试工程不可被游戏工程引用）——而手工关装载器（`HandcraftedLevelData`）与组装律（`HandcraftedLevelAssemblyTests.BuildConfig/BuildCommands`+各章 Playthrough 的能源表挂载+`FinalBossLevelTests` 践踏调参）此前全部活在测试工程内。「程序关已在用」的先例（ProceduralEntryHost）是对 procedural.json 的**小表重解析**（~200 行平铺）；手工关 schema 是 ~870 行全量校验+六时代组装面，表现层复制一份=双源漂移。故取**提升而非复制**：

1. **`HandcraftedLevelCatalog`（HandcraftedLevelContent.cs）**：T-085 装载器逐字段原样提升为 EraCore 生产面（Xunit `Assert` → 异常，其余零改动）。测试侧 `HandcraftedLevelData` 改为**薄委托+全局类型别名**（11 个 global using）——约 30 处测试引用面零改动，两侧共用同一份解析代码：生产装载器的任何字段级变化立即被既有全部关卡测试（schema/装配/链路）执法。
2. **`EraLevelAssembly`**：组装律提升（BuildConfig/BuildCommands/能源三表/仓库等级/践踏调参族 `TrampleFor`{1:60,2:100,3:100,4:0,周期 45；E5/E6 null}——FinalBossLevelTests.Finals 与 V10 TramplePerEra 同锚，ci-allow 登记）。测试侧 `HandcraftedLevelAssemblyTests.BuildConfig/BuildCommands` 与 `HandcraftedLevelContent` 的能源四读数全部改为委托——**既有全部关卡测试（含 V10 链/E1~E6 Playthrough/可达性）随之变成生产组装路径的回归网**（本任务实证：FinalLevelTransitionReachability 6/6 复跑全绿=行为逐字节等价）。
3. **`LevelSession.TryResume` 遥测缺口修复（一行）**：无 `#exit` 快照的新开局分支 `new LevelSession(config, rng, port, campaignPool)` 丢弃了 `durationTelemetry` 参数——经 TryResume 起局的战役/程序关真人局遥测整面漏记（T-155「宿主注入即记」契约违例；G4 冒烟首跑即抓到：rows=1 且 Resumed=true）。补传后纯观测面追加，零 sim/哈希影响（Telemetry/Procedural 套件 70/70 复跑佐证）。

以上三项均为**零行为变更的装载/组装/观测面提升**（不改任何既有引擎逻辑/数值/哈希路径）；T2 纯契约豁免已登记 `tools/ci/allowlist.txt`（行为由既有全套关卡测试执法，不另立同名测试文件）。

## 三、PR1 — 战役菜单与真装载器（29 关装载矩阵）

**入口与菜单**（`CampaignMenuUi`，EndlessMenuUi/ProceduralMenuUi 同构）：「战役」按钮（668,8）→ 章节分组关卡列表（六章 ScrollContainer：`第 N 章 · {era.json name_zh}`+每章按 slot 排序的关卡行）+退出存档按钮+局内状态行。进度态三源：①时代位=`CampaignState.EraIndex`（K-7 campaign 文档）；②占领/★=`VictorySettlementStore` 档案（`Decided.VerdictRows` 通过数=★ 1~3——三重目标即三星，无杜撰星级）；③奇观位=`LegacyLedger.IsWonderBuilt`（e6-final `requires_wonder=campaign:wonder-era-gate` 通关之门数据行）。

**解锁律（章内顺序推进）**：时代位之内——章首恒可玩；slot n 需同章 slot<n 全部已通关；e6-final 额外需纪元之门奇观位（=e6 非终局四关全清后 `MarkWonderBuilt` 落位——V10 章末奇观位同律的终章形态；真「建造纪元之门」玩法面归后续任务，REV §七遗留③）。

**装载矩阵（验收①装载面）**：`CampaignHostAssemblyMatrixTests` 93 条全绿——六章文件量级锚（5/4/5/5/5/5=29≥24 任务口径）+全 29 行生产组装构造即证（Menu 态/胜波=数据行 win_waves/命令流非空/种子=map.seed）+各时代组装面（E1~E4 践踏族/E3 过热/E4 过热+熔毁+辐射/E5 过热+湮灭+力场塔/E1/E2/E6 无通道+e6-final 通关之门数据位）+全 29 行同 seed 双装载双构造哈希全等（T-085 确定性③六时代全量）。

## 四、PR2 — 通关推进与跃迁（E1→E2 链实证）

**胜利结算**（`CampaignLevelHost.HandleVictory`）：`CampaignProgression.SettleVictory`（判分/开箱/占领——V10 SettlementOf 同律，HostRunCounters 活体输入按 600 tick 采样）→ `VictorySettlement.Decide` 先定后演（转盘段数 RL7 读自 victory.json 的 wheel_spin 步骤）→ `VictorySettlementStore.Upsert+Save`（★面+重进不重播）→ `PendingLaunches` 入队（窗口车道 `DrainPendingLaunches`→`CinematicDirector.LaunchVictorySettlement(decided)`/`Launch(scriptId)` 显式通道）→ `CompleteLevel+ClearExitSnapshot` → 下一关解锁（菜单进度态即时刷新）。

**E 关 final 胜利 → 跃迁**：章末奇观位 `MarkWonderBuilt` → `EraTransitionService.Execute`（**CityFacts=终局关真实存续建筑**——Id/ContentId/Tier/Cell 全取自胜利时刻的 `Sim.Buildings`，非 V10 链的合成校准事实；朝向恒 0=建筑无朝向面，锚=仓库格）→ `CampaignState.AdvanceEra(result)`（时代位+新池仓库+1+新大陆网格）→ 跃迁过场播片钩子 `cinematic:transition-eX-eY` 入队（CinematicDirector 既有装载）→ 下一时代章节解锁。跃迁 rng=宿主 SplitMix64 族（WorldSeed+era 派生——确定性，与测试链 LcgRng 族不同流属宿主口径，非同流性要求）。

**e6-final 胜利 → 终章**：`FinalePlayback.ScriptIdFor(PanelSettingsStore.Load(...).SystemEnabled)` 二选一（人格台词版/字幕旁白版——F-097 持久化开关位）入队 + 通关总结（`CompletionSummary`：六时代全清+已通关计数+制作组致谢）。

**E1→E2 连续推进链（验收②实证）**：`CampaignHostProgressionChainTests.E1_full_clear_then_real_transition_unlocks_and_plays_E2_01` 全绿（47 s 真跑）——e1-01→…→e1-final 五关经生产组装律连续真通关（占领集逐关断言）→ 终局关未建奇观硬拒（负向）→ `MarkWonderBuilt` 后真城市跃迁事务（campaign 域恰一次写/ToEra=2/仓库 2）→ `AdvanceEra` 时代位=2 → 排期=e2 前三关 → **e2-01 经同一条生产路径开局并真通关**。E2→E6=同构代码路径+装载矩阵全量面（每章 PlaythroughTests 已逐关证 Victory、跃迁服务 E2→…→E5→E6 事务族已由 Legacy/可达性套件证）。

## 五、PR3 — 存档续玩与遥测

**退出到菜单**：`LevelSession.Pause→SaveAndExitToMenu`（level 域 `#exit` 快照=重放键；遥测随终态自动落 Abandoned 行）+ **campaign 域合并写**（`SaveCampaignDocument`：保留既有文档载体族——legacy ledger 载荷/遗迹城快照/遗迹登记/已解锁科技，只刷新时代位/池/ledger 编码。`CampaignState.SavePoolTo` 的薄面会清空载体族，不适用于跃迁后的战役——宿主侧合并口径即本 REV 登记的边界，未改引擎）。

**重进续玩**：`CampaignLevelHost.Create` 读档链——`LoadPoolFrom`（时代/池恢复）→ campaign 文档 ledger 载荷 `DecodeInto`（奇观位恢复）→ `VictorySettlementStore`（占领/★恢复）→ `TechTree.RegisterCapture`（跃迁前置树面恢复）；选关后 `TryResume`（`#exit` 快照在位即无损续——Paused 冻结位 `ResumePlaying` 回 InLevel，命令流随快照重放，不重入队）。

**遥测双注入（T-155 战役半边收口）**：`LevelSessionConfig.Telemetry`（`Kind=Campaign`/levelId/时代序/map.seed/source）+`LevelDurationTelemetry` 服务双递交。窗口局 source=`player`（D-03 真根+WallClock）；headless 冒烟局 source=`script`（沙盒根+SteppedClock 分账不污染玩家账本）。

**冒烟判据（G4，`CAMPAIGN_OK`）**：`matrix=29/29 chapters=6 e1-01=victory stars=2 unlock=e1-02 resume=lossless@tick300 save=campaign-merged-reload telemetry=victory+abandoned+resumed source=script transition=refused-without-final-capture finale=switch-picked` + 全 29 行逐行状态。即：①UI 构造的真 LevelSession 跑 E1-01 到 Victory（~115k tick 真跑）+★2 行落账+解锁推进；②29 关装载矩阵（构造即证）；③E1-02 中途退出 tick300→重进无损续（level 域）+campaign 域合并写→新宿主实例读回（时代/进度态恢复）；④遥测三面行（Victory+Abandoned+Resumed）；⑤跃迁负向探针（E1 final 未占领→`EraTransitionRejectedException`——UI 路径接真事务前置）+终章二选一（开关位驱动）。

## 六、门禁

| 门禁 | 结果 |
|---|---|
| G2 红线扫描 | 0 FAIL 0 WARN（R7 豁免四处 ci-allow 登记：WorldSeed 锚/网格尺寸/Boss 波缺省占位/跃迁锚；T2 豁免两条 allowlist 登记） |
| G1 测试 | 全量绿（6888+1 新增面：矩阵 93 条+链路 1 条；关键复跑：FinalLevelTransitionReachability 6/6、Telemetry+Procedural 70/70。首跑 6887/6888 唯一红=`Era6LevelPlaythroughTests.Zero_point_energy…` 的「BuildConfig 基线不挂」旧断言——该断言的语义（测试侧基线无时代面）随装载器生产化退役，已改为断言生产组装律直挂 E5 双表+生产面=测试叠装面同值，复跑绿） |
| G3 Release 构建 | 零警告零错误 |
| G4 冒烟 | SMOKE_OK 捕获+CAMPAIGN_OK 全段（既有 PROC/ENDLESS/CINE/…段不红——SMOKE_OK 行在） |
| run_all | 六步全绿（见回传摘要） |

## 七、遗留与挂账

1. **SimHost E1 夹具退役**（§一）：迁移面=G4 十三段断言逐段改造，另立任务卡。
2. **章末奇观=清章落位**：E1~E5 奇观在 final 通关后自动 `MarkWonderBuilt`、e6 纪元之门在非终局四关全清后落位——真「建造奇观」玩法面（生产链/周期/拆除）归后续内容任务；本面语义=跃迁硬前置的事实登记（V10 同律）。
3. **窗口车道画面**：战役关卡在独立 LevelSession 上推进（帧泵 1 tick/帧+状态行），战场渲染（MultiMesh 桥/镜头/HUD）尚未绑定到战役会话——「从 UI 玩」当前=菜单+推进+演出+存档全真，战场观感面归 C-21 表现层后续（SimHost 泳道的渲染件复用路径已在 §一声明）。冒烟/测试判据不受影响（headless 断言会话/结算/存档事实）。
4. **跃迁 rng 族**：宿主用 SplitMix64（WorldSeed+era），测试链用 LcgRng——campaign 文档内容（大陆/遗迹登记）随族不同，确定性各自成立；若未来要求与测试链同字节，需统一注入族（登记在案）。
5. **LevelDurationTelemetryChainTests 对 TryResume 缺口的既有覆盖**：该套件经直构造路径覆盖新开局，未覆盖 TryResume 新开局分支的遥测——本缺口由 G4 CAMPAIGN_OK 的 telemetry 三面断言执法（生产消费面）；若后续补 xUnit 直断言更佳（小改进，不阻塞）。
6. **重玩挂账**：`CampaignProgression.RegisterCapture` 为单占领契约（重复 settle 抛异常）——已通关关的「重玩」在菜单面禁用（按钮=「已通关」灰置）；真重玩（重 settle 判分/宝箱重滚/星数刷新）需 progression 侧重复占领语义裁定后另立任务。
