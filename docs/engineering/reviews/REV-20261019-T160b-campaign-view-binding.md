# REV-20261019-T160b — 战役战场渲染绑定（T-160 收口半边——REV-20261018-T160 §七遗留③）

| 项 | 内容 |
|---|---|
| 任务 | T-160b（主管查实立项：战役会话逻辑全真，但 Main 战场渲染层仍绑死 SimHost 夹具——玩家从菜单进战役关，画面还是 E1 夹具图） |
| 特性 | C-21（战场渲染三件：桥+双 MultiMesh 层+相机）/F-005（关卡选择与进度——进关画面面）/F-110~F-116（军师面板在战役关内可用） |
| 依据 | REV-20261018-T160 §七遗留③「窗口车道画面：战场渲染（MultiMesh 桥/镜头/HUD）尚未绑定到战役会话」；先例=REV-20261018-T139 NetClientViewport（镜像快照→SimSnapshotBridge 复用单机渲染路径）；31 号 §1 时代路由（era1~era6 图集既有——ART2~ART6 标记先证装载） |
| 交付物 | ①引擎最小扩：`Simulation.CommandEnqueued` 观察缝+`LevelSession` 命令流记账/续局种子化+`EraCore.Tests/CampaignLoop/LevelSessionExternalLaneTests.cs`（3 条）；②表现层：`presentation/EraTD/scripts/SessionBattlefieldUi.cs`（SessionBattlefieldViewport 新件）+`MultiMeshRenderer.SetupGround` 时代图集参数+`SystemPanel.IAdvisorBoardHost` 宿主缝（SimHost 显式实现/CampaignAdvisorBoard 战役实现）+`CampaignMenuUi`（视口泵+面板重绑+HUD）+`ProceduralMenuUi`（同律接线）+`Main` 只读宿主面；③G4 新段 `CAMPAIGN_VIEW`（并入 CAMPAIGN_OK 执法面）+`PROC_OK` 增 `view=` 位 |
| 环境 | 本机开发机（D 盘纪律）；零网络零生图；G4 沙盒根 `.tmp-tests/campaign-smoke` 即用即清 |

## 一、边界声明（SimHost 夹具路径不动）

REV-20261018-T160 §一的裁定延续：SimHost+Main 的夹具渲染路径（`Main._Ready` 内 660 帧同步环+`_groundLayer/_unitLayer/_camera` 绑 `_host`）**零改动**——G4 十三段既有判据（BRIDGE/PANEL/RENDER/…）不破。会话泳道（战役/程序关）**自持渲染件**（NetClientViewport 先例）：`SessionBattlefieldViewport` 自建相机+双 MultiMesh 层，经 `Main.BattleHost/AdvisorPanel` 两个只读面在退出时还原面板——夹具与会话两条渲染路径分账并存（E1 夹具退役仍挂 T-160 REV §七遗留①的任务卡）。

**退出到菜单口径（律③）**：会话局内隐藏 Main 夹具双层（同层 z=0/10 不叠画）+会话相机接管；退出=视口 Release（层隐藏+镜头交还）+夹具双层恢复+军师面板还原夹具板——即「恢复菜单态」（夹具画面+菜单覆盖，任务卡允许的两口径之一，此处取前者）。

## 二、动态重绑（交付①——四条律的实现）

`SessionBattlefieldViewport`（`scripts/SessionBattlefieldUi.cs`）：

1. **①图随关变**：`Bind(LevelSession, eraIndex)` 检测 sim 引用变化即整面重建——新 `SimSnapshotBridge`（`SessionSnapshotSource`→`Sim.Snapshots.Latest` K-6 面+`SessionFramebuffer`→`Sim.World` 地形色码，SimHost/NetClientViewport 同款映射）+`SetupGround/SetupUnits` 按新图宽高+**时代路由图集**：era1=`TerrainAtlas`（色码→槽）；era2~6=`Era2~6TerrainAtlas.Build()`+`TileIndexForTerrain(TerrainKindOfColor(rgb))`（帧_buffer 色码反查地形种类——`SetupGround` 新增 `tilesPerRow` 参数，着色器 `tiles_per_row` 不再烙死 era1 的 8）。
2. **②换关重进再重绑**：`Bind` 每帧幂等（引用相等零动作）——战役 `BeginLevel` 每次新造 LevelSession（sim 新引用）即触发重建；跃迁后重进同律。
3. **③退出恢复菜单态**：见 §一。
4. **④相机重居中**：`Bind` 时 `Position=(宽×32/2, 高×32/2)`（Main._Ready 先例同律）+`Enabled=true` 接管，`Release` 交还。

`PumpFrame`=单机同一脏块路径（SyncFrame→TakeDirty→Rebuild，不发明第二条渲染法）；渲染计数/地形签名/尺寸/era 只读探针供 G4 与观测。

## 三、军师面板绑战役会话（交付⑤——引擎最小扩）

**问题**：`SystemPanel` 的军令路径走 `AgentPipeline`，其执行器（`PipelineExecutor.EnqueueAndApply/PlaceBuildableCells`）**直投 `Simulation.Enqueue` 并自行 Step 一拍**——不经 `LevelSession.Enqueue`，则确认的 BuildPlan 建造不进 `_issued` 命令流 → 波界/退出快照的重放键漏掉军令建造 → 重进局与活体局发散（无损续玩不变量破坏）。LevelSession 的命令入口=`Enqueue(SimCommand)`（InLevel 门+`_issued` 记账——任务卡「读命令入口确认」即此）。

**最小扩（两缝，纯观测/记账面，零 sim 状态/零哈希影响）**：

1. `Simulation.CommandEnqueued` 事件（`Enqueue/EnqueueRange` 入队后通知）；
2. `LevelSession` 私有构造挂接观察记账（`_issued` 唯一真源=sim 入队观察——会话侧与外部泳道同一条记账路径，`Enqueue` 不再手工双记）；`TryResume` 以重放命令流种子化 `_issued`（重进会话此前 `_issued` 为空——退出→重进→再退出的第二次快照会丢整段历史，本任务顺带收口的既有缺口）。

**执法**：`LevelSessionExternalLaneTests`（3 条全绿）——外部泳道命令入账恰一条；外部建造参与退出快照（退出→重进哈希全等+塔数保留+命令在流）；双重退出重进命令流种子化无损。

**表现面**：`SystemPanel.Bind(SimHost)`→`Bind(IAdvisorBoardHost)`（`Pipeline/Store/BuildHudSnapshot` 三面）；`SimHost` 显式实现（internal 表面零变动）；`CampaignAdvisorBoard`（CampaignLevelHost.cs，纯 C#）=真五段管线 over 该关 LevelSession sim（时代治理=关卡时代序——E1 关系统点不启用是设计律 §11.4；手工关数据无命名分区→分区锚点空集，分区军令诚实澄清/拒绝，不伪造锚点；战报=宿主根 reports/ GUID 文件，K-8）+HUD 三指标会话活体计算。窗口车道：开局 `AdvisorPanel.Bind(board)`+每 15 帧 `RefreshHud(board.BuildHudSnapshot())`；退出还原夹具板。战役关卡内军师面板可用=BuildPlan 等确认命令经会话通道落子（§五冒烟硬证）。

## 四、无尽/程序关排查（交付③边界）

- **程序关（ProceduralMenuUi→LevelSession）**：同缺实证——进局后无任何渲染绑定。**同律修复**：`PumpWindowSession` 内 `Bind(session, EraIndexOf(level.Era))+PumpFrame`（每帧幂等，重开局=新图重绑）+退出 `ReleaseBattlefield`（视口释放+夹具层恢复）；G4 `PROC_OK` 增 `view=bound:{W}x{H}:ground{N}` 位（600 tick 真跑绑定+脏块>0+尺寸=该关图）。
- **无尽（EndlessMenuUi→EndlessSession/EndlessRun）**：**无此缺**——EndlessRun 是波/记分抽象局（`BeginWave/CompleteWave/Die`，无 C-12 Simulation、无战场世界），设计上没有战场画面可绑；EndlessMenuUi 的 UI 面即全部体验面。不修（非缺）。

## 五、冒烟判据（G4 新段 `CAMPAIGN_VIEW`——并入 CAMPAIGN_OK 执法）

`CampaignMenuUi.RunViewSmoke`（沙盒根，problems 合流 CAMPAIGN_FAIL/Quit(1)），实测输出：

```
CAMPAIGN_VIEW e1=16x24 ground_rebuilds=1 units_seen=1 unit_rebuilds=3088
  terrain=level-world-equal rock_cells=0 e2=20x26 era2_atlas=True
  rebind_terrain_changed=True advisor=build-plan-on-session-channel
  towers=0->1 issued_advisor_commands=1 advisor_resume_towers_kept=True
  panel_hud=True exit_restore=True
```

即任务卡判据逐条：①进 e1-01→渲染层脏块重建>0+**地形签名=该关真图**（视口帧缓冲逐色计数≡会话世界直扫）+若干帧后单位层有实体（grace+2 波窗泵程，单位脏块 3088）；非夹具佐证=e1 行 rock_ratio=0→岩石格 0（夹具图 T-040 锚 rock 0.05≈19 格）+尺寸=数据行 16×24；②退出到菜单→释放+夹具层恢复（exit_restore）；③切换 e2-01 重绑生效——图尺寸 16×24→20×26+era2 图集路由+地形签名随图变；④军师面板绑战役会话——BuildPlan 确认命令走 LevelSession 通道（命令流入账+`towers=0->1`+**退出重进军令建造无损保留**=重放键覆盖军令来源的硬证明）+面板 HUD 随会话波次（panel_hud）。

注：units_seen=1 为 e1-01 前波真数据（threat 0.3 薄波），>0 即判据达成；ground_rebuilds=1=静态地形一次性全量重建（`FramebufferVersion` 常量——SimHost/NetClientViewport 同律），>0 即判据达成。

## 六、门禁

| 门禁 | 结果 |
|---|---|
| G2 红线扫描 | PASS：9 checks \| 0 FAIL \| 0 WARN（无新豁免；新增常量均非锚点数值） |
| G2.5 资产规格 | PASS：files=643 rejected=0 |
| G2.6 模组离线 | PASS：0 FAIL（3 mods/8 docs） |
| G1 测试 | PASS：6891 passed, 0 failed（6888 基线+3 新面 LevelSessionExternalLaneTests；既有 CampaignLoop/Checkpoint/回放族全量复跑不红——观察缝零行为变更） |
| G3 Release 构建 | PASS：零警告零错误（TreatWarningsAsErrors=true） |
| G4 冒烟 | PASS：SMOKE_OK 捕获+CAMPAIGN_OK/CAMPAIGN_VIEW/PROC_OK（含 view= 位）全段绿 |
| run_all | 六步全绿（总体 PASS） |

附带核查：G4 尾部「20 ObjectDB instances leaked」告警经 master 基线对照为**既有等量**（stash 前后同为 20——非本任务引入）。

## 七、遗留与挂账

1. **E1 夹具退役**：继承 T-160 REV §七遗留①（G4 十三段迁移面另立任务卡）。
2. **战役关编成/预览行**：窗口车道 Main 本就不逐帧刷第三指标构成明细（`_Process` 无该泵）——本任务绑定 HUD 三指标+波次；构成/下一波预览的会话车道逐帧刷新随夹具退役任务一并收口。
3. **军令步进的哈希账本采样洞**：军师确认命令的执行器自 Step 一拍不经 `RunFor`——该拍不入 `HashLedger`（观测账本纯观测面，无恢复语义影响；金样/回放不受累）。随军师局 SystemBuildPlanCount 记账（T-160 REV 同挂账）一并补。
4. **退出快照与未来到期命令**：`CommandsUpTo` 只记 `IssuedAtTick<=退出刻` 的命令——宽限期内退出会丢未到期脚本命令（既有引擎语义，非本任务引入；CAMPAIGN_OK ③续玩判据不受影响）。等级：低（重进后缺后续建造，状态仍与退出刻一致）。
5. **程序关军师面板**：战役侧面板重绑已落；程序关未接 `IAdvisorBoardHost`（其会话无 HandcraftedLevel 配置面——ProceduralEntryHost 未暴露 LevelSessionConfig）。挂账后续（同律半边，渲染面已收口）。
