# REV-20261019-T160c — 战役败北面与演出钩子接线（T-160 双轴审查两真断点收口）

| 项 | 内容 |
|---|---|
| 任务 | T-160c（T-160/T-160b 双轴审查判明的两个真断点：①战役败北面全缺——`CampaignLevelHost.PumpWindowFrame` 只判 IsVictory，DeclareDefeat/失败分档/检查点消费/失败面板/遥测 Defeat 行整面未接；②演出钩子两缺——e3/e4/e5 终局关胜利拍收束演出未发射+剧情钩子路由绑死夹具 e1+e2 整文件混装） |
| 特性 | F-001（单局循环宿主面·败北半边）/F-004（失败分档·检查点消费）/F-006（时长遥测 Defeat 终态）/F-107（时代收束×5·发射位）/F-113（E1 断弹钩子）/F-114（E2 觉醒钩子）/F-108（终章播片二选一·复核不破）/F-002（胜利结算演出·顺序共演） |
| 依据 | 设计方案 v3.2 §3 失败分档（普通关=检查点恢复+残骸修复账 30%×首败 0.8；检查点=⌊胜波×0.75⌋ 且 ≥10）+§12 交付规格（5 段时代收束 20~30s）+§12.1 谱系；REV-20261018-T160（战役宿主面；§七遗留②章末奇观/⑤重玩挂账沿用）；REV-20261019-T160b（会话外部泳道先例）；`LevelSession.DeclareDefeat` 调用约定（引擎源=宿主检测→声明；`AcknowledgeDefeat` 收尾）；43 号 §5 收束段表（段 3/4/5=e3/e4/e5-finale 显式发射；段 1/2 归 T-089 结算演出域）；eX-finale.json `_annotation_convention`（"由战役宿主在本时代终局关胜利拍显式发射…与 transition 独立两场不同步连播"）；FinalePlayback 冻结常量（e6-final 胜利拍=开关二选一，T-136/T-160 既有接线）；CinematicDirector.LoadContent 装载集注释（"逐关装载随 C-29 落地"——本任务即该收口） |
| 交付物 | ①引擎最小缝×2：`EraCore/CampaignLoop/LevelSession.cs` 增 `TryResumeCheckpoint`（检查点续局工厂——败北恢复缝）+`EraCore/Cinematics/CinematicHookRouter.cs` `ParseBeat` 公开（封闭 beat 表单源）；②表现层：`CampaignLevelHost.cs`（败北判定 IsBaseFallen/声明入口 DeclareDefeat/HandleDefeat 分档结算/三行动 ContinueFromCheckpoint·RestartLevel·DismissDefeatToMenu/钩子路由 BuildHookRouter 随当前关构造/波始拍路由/EraFinaleLaunchIds 收束→跃迁发射序+**transition id 缺陷收口**）+`CampaignMenuUi.cs`（失败面板 UI+每帧排空 PendingLaunches+冒烟三新段）+`CinematicDirector.cs`（装载集增 e3/e4/e5-finale 三件）；③链路测试：`EraCore.Tests/Levels/CampaignHostDefeatChainTests.cs`（2）+`EraCore.Tests/Cinematics/CampaignLaneHookRoutingTests.cs`（5） |
| 环境 | 本机开发机（D 盘纪律）；零网络零生图；G4 沙盒根 `.tmp-tests/campaign-smoke` 即用即清 |

## 一、断点一（败北面全缺）——引擎调用约定与判定裁定

**引擎败北语义（读源结论）**：引擎**无自动败北谓词**——`LevelSession.DeclareDefeat(bool firstDefeat)` 是宿主声明式 API（InLevel 门→机器 `Lose()`→遥测 `End(Defeat)` 自动落行→`FailureTiers.FailNormalLevel` 普通关分档执行→返回 `NormalLevelFailureOutcome`）；宿主应用分档后 `AcknowledgeDefeat()` 回 Menu。**基地陷落/HP 归零由宿主检测声明**——本任务判定=`IsBaseFallen`（仓库=本关唯一基地建筑，HP≤0），帧泵每 tick 步进后检测。**诚实登记**：生产模拟当前无建筑伤害路径（践踏脉冲=WaveSpawner 数据位先行，生产 Simulation 不驱动——引擎既有状态），该谓词在现有内容下不会自触发；冒烟经宿主公共声明入口 `DeclareDefeat(reason)` 构造败北（与判定命中走同一 `HandleDefeat` 链；先例=EndlessMenuUi.OnDiePressed 声明式终局）。伤害面接线（践踏脉冲驱动）挂账后续引擎任务。

**败北结算=检查点消费**：`HandleDefeat` ①`DeclareDefeat(firstDefeat)`（首败键=宿主 `_defeatSeen` 按关 id 记账，本坐口径——修复折扣 30%×0.8 的"首败"输入）②`ClearExitSnapshot`（关终局清 #exit——引擎契约"a level ENDS"；防重开误续死局）③分档事实提取入 `PendingDefeat`（失败面板数据）④`AcknowledgeDefeat` 回 Menu。

**检查点续局（引擎缝①依据）**：「从检查点继续」此前无会话级恢复路径（TryResume 只认 #exit）——宿主复刻私有构造模式=越权，借道 #exit 槽=语义滥用。`LevelSession.TryResumeCheckpoint` = TryResume 的 #ckpt 镜像（`WaveCheckpointStore.TryRestoreCheckpoint`→`RestoredWaveSnapshot.Replay` 重放→机器 Menu→InLevel→Paused→调用方 `ResumePlaying`）：纯公共缝组合，零新存档格式（重放即恢复，T-040 不变量同源），零既有路径行为变更。失败面板「继续」档可用位=`RestoredFromWave>0`（检查点波恒 ≥10=Campaign.Checkpoint 下限；0=未跨检查点波=仅重开——任务卡判据的引擎事实形态）。

**UI（失败面板）**：败绩分档呈现（档位=普通关失败+检查点波+恢复点/未跨）+残骸修复账（条数/总价/首败 ×0.8 折）+三行动：「从检查点继续」（检查点不足隐藏）/「重开本关」（清本关存档域——`Checkpoints.Clear()` 终局关清档同律——后从头开局）/「退回菜单」（检查点域保留）。`_Process` 两处随本任务收口：①每帧排空 `PendingLaunches`（此前只在胜利帧排——beat 驱动的剧情钩子（e1-aha 第 2 波）会积压到通关才播的时序缺陷）；②**串行放演**——原实现同一帧逐个 `Launch`，`BeginPlayback` 互相覆盖时间轴（[结算,收束,跃迁] 只剩最后一场）——改为本地待演队+导演空闲（非 Playing）逐场开演，发射序在窗口车道真实落地。

**遥测**：Defeat 行由引擎 `DeclareDefeat` 自动入账（T-155 终态三态的败北半边收口）；检查点续局=新行 `Resumed=true`（可拼接还原整关）；续局通关=Victory 行——G4 断言 `telemetry=victory+abandoned+resumed+defeat` 四面。

**败北全链冒烟（G4 判据）**：实测输出——

```
CAMPAIGN_OK … defeat=declared-after-checkpoint tier=normal restored=wave10
  checkpoint_continue=resumed@tick86400->victory early_defeat=restart-only
  restart=fresh@tick0 telemetry=victory+abandoned+resumed+defeat …
```

即：e1-02 真打到检查点波（10=max(10,⌊13×0.75⌋)）越界（tick 86400）→声明败北→面板事实（tier=normal/恢复波 10/残骸账/首败）→从检查点继续（续局 tick==86400/波==11/机器 InLevel）→**再打到 Victory**；e1-03 未跨波败北=恢复波 0+无继续档→重开=零点新局（tick 0）；campaign 域合并写读回（e1-02 已通关态持久）。

## 二、断点二（演出钩子）——发射序与路由裁定

**①收束→跃迁发射序（裁定依据）**：`ExecuteTransition` 入队序=**收束演出→跃迁过场**。依据：eX-finale 数据注释明言"由战役宿主在本时代终局关胜利拍显式发射…与 transition-eX-eY 独立两场、不同步连播"+§12 叙事序（本关结算（victory.json 已先入队）→时代落幕收束→新时代跃迁开场）+43 号 §5 段表（段 3/4/5=e3/e4/e5-finale；段 1/2 无段文件=数据现实，归 T-089 结算演出域）。`EraFinaleLaunchIds(era, contentRoot)`=文件存在性驱动（`cinematics/eX-finale.json` 在即收束先行）——后续补 e1/e2 收束段零代码改动自动入序。**顺带收口的既有缺陷**：T-160 原实现跃迁 id 误拼 `transition-e{X}-{Y}`（缺第二端 e 前缀）——入队 id（`cinematic:transition-e1-2`）与装载脚本 id（`cinematic:transition-e1-e2`）不符，窗口车道 Launch 即抛（此前从未被冒烟执法到——G4 未跑过终局关胜利）；本任务修正+发射序探针×5 执法。

**②战役泳道剧情钩子（选最小面=宿主侧路由）**：`CinematicHookRouter` 留在引擎（纯逻辑），**钩子行装载随战役会话走**——`CampaignLevelHost.BuildHookRouter(level)`：行源=`HandcraftedLevel.CinematicHooks`（**逐关数据位**，非 CinematicDirector 夹具的 e1+e2 整文件混装——混装会把 e1-01 的断弹行错绑到 e1-03，逐关构造从根上不串关）；脚本=内容侧带 hook 键的全集三件（e1-aha/e2-awaken/finale-zero，CinematicHookRouterTests+FinaleCinematicsTests 钉死），过滤本关声明集内者。发射拍=**level_start（新开局）+wave_start（波前进，锚波精确匹配）**；演出经 `PendingLaunches`→UI `DrainPendingLaunches`→`CinematicDirector.Launch` **显式通道**（4b9e120 先例——先定后演/回看同通道，零 Director 新缝）；进度门=CinematicProgressStore 已看过不重播（LaunchByHook 同律，门在宿主入队侧）。**不路由 victory 拍**（裁定）：e1-01 交底行无脚本认领（T-089 域零发射）；e6-final 真相行=终局关胜利拍=宿主显式开关二选一（FinalePlayback.ScriptIdFor）既有接线——路由抢拍会造成 finale-zero/off 双播，显式位为权威（`EnqueueLaunch` 的单发幂等兜底同向）。**引擎缝②依据**：`ParseBeat` 公开——宿主把行数据的 beat 字符串映射为枚举须复用同一张封闭表（私有复刻=双源漂移，未知 beat 须绑定即抛）。

**③e6-final→finale-zero/off 复核不破**：既有二选一接线（HandleVict e6 分支）零改动；冒烟既有断言 `finale=switch-picked` 保留绿；链路测试 `E6_final_victory_row_is_claimed_by_the_finale_script_and_picked_by_switch` 以 FinalePlayback 冻结常量互证行↔脚本↔二选一三面。

**演出触发矩阵（交付时点全量）**：

| 触发位 | 演出 | 发射通道 | 执法面 |
|---|---|---|---|
| e3-final 胜利拍 | victory-settlement→**e3-finale**→transition-e3-e4 | 显式（宿主入队序） | G4 发射序探针+链路测试⑤ |
| e4-final 胜利拍 | victory-settlement→**e4-finale**→transition-e4-e5 | 同上 | 同上 |
| e5-final 胜利拍 | victory-settlement→**e5-finale**→transition-e5-e6 | 同上 | 同上 |
| e1/e2-final 胜利拍 | victory-settlement→transition-e1-e2 / -e2-e3（无收束段文件=数据现实） | 显式 | G4 探针（e1 期望串） |
| e1-01 第 2 波 | **e1-aha**（断弹→闪红→民夫送箭→恢复） | 钩子路由（wave_start 锚 2） | G4 `aha-routed`+链路① |
| e2-01 开局 | **e2-awaken**（觉醒三选一→令簿） | 钩子路由（level_start） | G4 `awaken-routed`+链路③ |
| e1-01 胜利 | （交底行无脚本认领——T-089 域） | — | 链路① OnVictory 空 |
| e6-final 胜利拍 | **finale-zero / finale-zero-off**（系统开关二选一） | 显式（T-160 既有） | G4 `finale=switch-picked`+链路④ |
| 任意关胜波达成 | victory-settlement（先定后演） | 显式（T-160 既有） | G4 既有段 |

## 三、链路测试（两断点各配）

1. **`CampaignHostDefeatChainTests`（断点一，2 例全绿）**：①e1-02 生产组装律真跑到检查点波越界→`DeclareDefeat(true)`（tier=NormalLevel/恢复波=10=公式值/残骸账每条 `原造价×0.30×0.80`（v3.2 §3 设计字面）/遥测 Defeat 行）→`TryResumeCheckpoint`（机器 Paused/tick=恢复点/波=检查点波+1/命令流种子化计数相等/**续局哈希==原局账本同位采样**——重放即恢复的逐字节证明）→续局真通关（Victory/胜波=13）+Resumed 行；②未跨检查点波（波 1 命令段已建塔的活局）：恢复波 0/恢复态=tick 0 新局（**残骸账空=引擎语义**——账随恢复态走，非缺漏）/`TryResumeCheckpoint` 返回假+全新 Menu 会话/Defeat 行入账。
2. **`CampaignLaneHookRoutingTests`（断点二，5 例全绿）**：①e1-01 泳道恰在第 2 波发射 e1-aha（第 1/3 波与重复拍零发射；victory 拍零发射）；②无钩行关零发射+**不串关**（e1-03/e2-02 在全套脚本在场时不发射 e1-01/e2-01 的行——混装收口的直证）；③e2-01 泳道开局恰发 e2-awaken（单发）；④e6-final 胜利行=FinalePlayback 冻结常量互证（行 id=FinaleHookId/拍=Victory/on 版认领+off 版无钩子键+开关二选一）；⑤收束三件+跃迁五件全解析（hookless 显式位+id 命名一一对应）。

## 四、门禁

| 门禁 | 结果 |
|---|---|
| G2 红线扫描 | PASS：9 checks \| 0 FAIL \| 0 WARN（无新豁免；R7 只扫 EraCore 源——两处引擎缝零数值字面量；T3 F-ID 关联 100% 含新测试） |
| G1 测试 | PASS：6898 passed, 0 failed（6891 基线+7 新面：败北链 2+钩子路由 5；既有 CampaignLoop/Checkpoint/回放/金样族全量复跑不红——引擎缝零行为变更） |
| G3 Release 构建 | PASS：零警告零错误（TreatWarningsAsErrors=true，49s） |
| G4 冒烟 | PASS：SMOKE_OK 捕获（52s）+CAMPAIGN_OK（含 defeat/checkpoint/cinematic/finale-order 新位）+CAMPAIGN_VIEW+CINE_OK+PROC_OK 全段绿 |
| run_all | 六步全绿（G2/G2.5/G2.6/G1/G3/G4 总体 PASS，1153s 测试步） |

附带核查：①首跑 G1 唯一红=`LlmGatewayTimeoutTests.Idle_gap_above_the_budget...`（Llm 域计时测试，与本任务改动面零交集）——单套件复跑 8/8 绿+run_all 内 G1 复跑 6898/6898 全绿，判定=全量负载下计时抖动（诚实记录）；②G4 尾部「20 ObjectDB instances leaked」=T-160b REV 已登记的既有等量告警，非本任务引入。

## 五、遗留与挂账

1. **基地陷落伤害面**：生产 Simulation 无建筑伤害路径（践踏脉冲数据位先行未驱动）——`IsBaseFallen` 判定面已就位但现有内容不会自触发；败北全链经声明入口实证。伤害面接线（践踏/漏敌→仓库 HP）挂账后续引擎任务，届时判定面零改动直接生效。
2. **FailFinalLevel 终局关分档未接**：引擎 `FailureTiers.FailFinalLevel`（清本关存档+免费演习）存在但 `LevelSession.DeclareDefeat` 恒走普通关档——**与关卡数据一致**（e6-final 行 `_source` 明言"败绩分档=§3 终局关失败→检查点（⌊19×0.75⌋=14 且 ≥10）"——终章终局关也走检查点档）。终局关专属分档若启用需 progression 侧裁定，登记在案。
3. **首败折扣键=本坐口径**：`_defeatSeen` 宿主内存态（跨进程重启后同关再败按首败折）——跨坐持久化（campaign 域列）若需要另立小任务；影响面仅修复账 0.8 折扣位。
4. **收束/跃迁的窗口车道全真播放**：发射序与装载面已收口（Director 三件装载+Launch 通道），但 G4 未跑 E3~E5 终局关全链（量级=三章通关）；链路面由发射序探针+内容侧解析测试执法，全链播放演算归首次真人三章通关。
5. **SimHost E1 夹具退役**：继承 T-160 REV §七遗留①（G4 十三段迁移面另立任务卡）。
