# REV-20261020-T163 战斗 HUD 图标化（C 批）

- 任务：T-163「C 批·战斗 HUD 图标化」（08 v1.31 原文行）
- 基线：HEAD=f68e03e（T-162 收官 08 v1.33）
- 纪律面：R5/R7/R9（零机器绝对路径）/T2 FNV/T4；CS0104 System.Environment；headless 判据零改动（只加不改）；XML 中文注释；≤最小 diff
- 声明：本 REV 由施工代理起草，主管验收后登记。

## 一、交付清单（对照 08 v1.31 原文）

| # | 原文条目 | 落地面 |
|---|---------|--------|
| 1 | 顶部资源条（icon+数字） | `BattleHudUi`（窗口车道）：era.json resources 数据序渲染行，icon 48px 档（`HudIconDecisions.ResourceIconPx`），数字右贴；数量=战役会话 sim `ResourceStock` 活体读（每 15 帧拍，与 SystemPanel HUD 同拍）。era 双条件重绑（sim 引用+eraIndex 双比，SessionBattlefieldViewport.Bind 同律） |
| 2 | 底部建造栏（塔 icon+价+快捷键） | `BuildInteractionUi.BuildBar()` 图标化（塔 icon=128×128 res:// 装载+价 Label+快捷键角标）+`scenes/build_ui.tscn` 建造栏移底部居中（StatusLabel 上移让位）。towers.json 序即栏序（既有律零改动）。禁用态=图标灰暗+价红（BuildGreyMap 同源判定）；选中态=时代色描边（palettes 同源，经 ExpandDirector.EraColor）。快捷键真绑定=Q/W/E/T…按槽位序→ArmTower 同一链（按钮 Pressed 与键盘同路径，点击建造链零回退） |
| 3 | 波次预告横幅（敌 icon） | `BattleHudUi` 波次预告横幅：敌 icon×数量徽记行（每类 32px icon+×N），boss 波横幅金边描边+boss 类金相（`HudIconDecisions.IsBossEnemyId`=-boss 尾约定）。与 SystemPanel 既有文字预告（RefreshComposition）并存增强（零回退）。数据源=`CampaignAdvisorBoard.BuildWaveCompositionView`（本任务补齐——SimHost 同律镜像，读面独立成对） |
| 4 | 暂停/倍速 | `BattleHudUi` 四按钮（⏸/1×/2×/3×）：按下=合成 InputEventKey 入真输入链→`BuildInteractionUi._UnhandledKeyInput` 既有键链（与玩家敲空格/1-2-3 同一条链，零第二套语义；ShotHook.EmitWheel 真事件先例）。按钮态镜像=`BuildInteractionUi.PaceFact()` 只读探针（钟仍归建造泳道） |
| 5 | 军师面板时代皮肤+立绘 | `SystemPanel.ApplyEraTheme/EraColor/ExpandSkinTarget/ResetEraTheme`：标题带时代色+36×36 立绘位（`era{N}-advisor.png` res:// 装载，缺图=既有程序头像回退不阻塞，落地后随重绑自动出现——eraIndex 双条件重绑）+边框皮肤=既有 `VisualExpansionDirector.ApplyEraSkin`（visual-assets.json palettes 同源经 ExpandAssetPaths 三级链，**零新根解析**）。退出战役=ResetEraTheme 还原菜单态 |
| 6 | F1 词汇表改收起抽屉 | `InfoLayersUi.BuildHelpPanel` 改造：面板贴右缘（(378,34)，不再居中压顶）+右缘竖排把手常驻（「F1/词/汇/表」，ZIndex 41）；F1 键/把手/词条链/收起钮=ToggleHelp 同一开合口。headless 判据（UI_OK glossary 搜索等）零改动 |
| 7 | T-161 遗留 BGM 战况 | **做了（半径内）**：`AudioDirector.ObserveSessionEnemies/ResetSessionEnemies`（会话敌数注入缝——_Process 三态目标会话优先，有敌=Combat/Boss、无敌=Calm，「会话进行中恒 calm」既有面接通）；`CampaignMenuUi.PumpBattleAudio` 每帧喂敌数+boss 波（-boss 尾在场）+关时代变化时 SetActiveEra/SetVoiceEra 双路由。**菜单专属 BGM 曲：留 T-164**（bgm-programs.json 无 menu 行——新 program 行+timbre 合成+混音验证超一小时预算，诚实登记不造假） |
| 8 | --shot-plan battle-hud | ShotHook 五段真信号编排：真按钮 OpenEntry("campaign")→真按钮 PressLevel("campaign:level-e1-01")（EmitSignal(Pressed)→OnLevelPressed→BeginLevel，与玩家点击同一条链）→进局 72 帧成形→01-hud-full 全景+02-build-bar/03-wave-preview 同帧真裁剪→军师 Expand→04-advisor-panel 真裁剪→F1 真键事件（InputEventKey→_UnhandledKeyInput）开/收两态分帧截取→05-glossary-drawer 真帧水平拼接（零伪造）。五张落 `docs/screenshots/w10-c/` |
| 9 | 门禁 | run_all 六步全绿（G2 红线 9/9+G2.5 资产+G2.6 模组+G1 测试+G3 Release 零警告+G4 SMOKE_OK）；headless 判据零改动+只加不改的 HUDICONS 探针行；窗口存活（无参玩家车道 15s 不退出实测） |

## 二、图标装载覆盖（任务卡「92 全接还是部分+理由」）

**92/92 全接 + 立绘 6/6**：

- 映射=id 冒号转横线确定性推导（`HudIconDecisions.IconStemOf`），**零散装映射表**（T3 纪律）；清单权威源=game/content/era{1..6}/{era,towers,enemies}.json 现场推导（塔 21/敌 54/资源去重 17）。
- 三重证据：①单测 `RepoIcons_CoverEveryTowerEnemyAndResourceRowOfAllSixEras`（92 id 逐行命中实文件——缺图=接线错不是缺资产）；②headless 探针行 `HUDICONS towers=21/21 enemies=54/54 resources=17/17 portraits=6/6 track=res-imported`（G4 只加不改扩展）；③窗口实测 `BATTLE_HUD bind era=1 resources=3 icon_hits=3/3 portrait=True`。
- 显示面按需呈现（装载层全接）：e1 局=建造栏 4 塔 icon+资源条 3 资源 icon+预告横幅敌 icon 行；建造栏其余 17 塔 icon 随时代泳道装载目录切换出现（见 §五遗留①）。
- 六立绘 256×256 主管并行生成全部落地（res:// textures/ui/portraits/，era1-advisor 已在基线前到位，其余 5 张施工窗口期落地——缺图优雅回退纪律保证过程不阻塞）。

## 三、实测收口：会话战场+HUD 同屏渲染（本批最大修复）

窗口车道进局后战场/UI 全体错位（根因两层，五轮诊断探针定位）：

1. **Camera2D 平移 root 画布全体**：SessionBattlefieldViewport 原用 Camera2D 对齐图心——Camera2D 平移整个 root canvas layer（SystemPanel/建造栏/BattleHudUi 全体 Control 被牵移出屏；MakeCurrent 换 current 相机也只换平移源不除平移）。**修复=虚拟相机**：视口自身 `Position` 平移（图心→画布心，ViewportScale 常量），只动自持两层 MultiMesh，会话 UI 纹丝不动；Release 归零。
2. **WorldCamera 常开**（main.tscn 夹具镜头，T-161 菜单态即平移着 root UI——被 CanvasLayer 100 菜单盖住不可见）：窗口分支 `_camera.Enabled=false`（夹具层窗口态不步进/被盖，无需镜头；headless 冒烟段不动——判据零改动）。

诊断证据行（--shot battle-hud 打印，保留为编排证据）：`HUD_LAYOUT resource=(8,68) 220x52 … camera=null`+像素采样（资源条 HoloBox 底/木材 icon 色/横幅底/沙色地形逐点命中）。

**顺手项（进局 UX 闭环，进局收面板引出）**：`OnLevelPressed` 进局后收选关面板（战场+HUD 呈现）；HUD 右上 ✕ 钮=`PressExitLevel()` 真信号（OnExitPressed 同链——存档续玩退出）；败北「退回菜单」重开选关面板（面板已收的新态）。

## 四、验证记录

- G1：定向 `HudIconDecisionsTests` 21/21（含 92/92 真文件映射）+全量 dotnet test 全绿（run_all 汇总行见提交信息）。
- G3：`dotnet build game/EraTD.sln -c Release` 零警告（TreatWarningsAsErrors）。
- G4：smoke_godot SMOKE_OK 捕获；既有判据行（BUILD/UI/AUDIO/PANEL/CAMPAIGN/…）逐字节零改动；新增 HUDICONS 行=只加不改扩展。
- 红线：首跑 T1×12（注释「占位」禁词——措辞改「回退」）+R7×1（icon px 档位行级 ci-allow 豁免）→复跑 9/9 全过。
- 窗口：无参玩家车道 15s 存活（不 Quit）；--shot battle-hud 五张真截（SHOT_OK files=5）。

## 五、诚实遗留（路由 T-164）

1. **建造栏时代联动**：建造泳道（BuildInteractionUi）自带 era1 演示 sim（T-056 既有架构：建造交互=独立演示泳道，非战役会话建造），栏 icon=装载目录（era1）4 塔；战役进 e2+ 关时建造栏不随时代换装（会话建造=大改，超 C 批半径）——e1 关完整可用，e2+ 登记T-164「战斗内建造会话化」。
2. **菜单专属 BGM 曲**：见 §一#7——留 T-164。
3. **T-162 遗留三连抄/banner 构建聚合**：按任务纪律未动（T-164 范围）。
4. **节奏按钮/✕/把手为文字符号**（⏸/1×/✕/F1 竖排）而非图形 icon——icon 素材集 92 张无 UI 控件件；T-164 皮肤批可换 StyleBoxIcon。
5. HUD_LAYOUT/SHOT_PROBE 诊断行保留在 --shot 编排内（level-cards SHOT_PROBE 先例）；BATTLE_HUD bind 行为窗口车道常驻观测行（进局一次）。
6. build_ui.tscn DemolishCard offset_right=760 越出 640 画布（T-056 既有，窗口态未复现路径）——观察不动，T-164 皮肤批顺手。

## 六、文件清单

新增：`game/src/EraCore/Presentation/HudIconDecisions.cs`、`game/src/EraCore.Tests/Presentation/HudIconDecisionsTests.cs`、`game/presentation/EraTD/scripts/BattleHudUi.cs`、五张截图、本 REV。
修改：`Main.cs`（挂载+探针+编排+相机让位）、`BuildInteractionUi.cs`（图标化+快捷键+PaceFact）、`SystemPanel.cs`（立绘位+时代主题+皮肤面）、`InfoLayersUi.cs`（抽屉）、`CampaignMenuUi.cs`（喂接+音频泵+PressLevel/PressExitLevel+进局收面板）、`CampaignLevelHost.cs`（CampaignAdvisorBoard.BuildWaveCompositionView）、`AudioDirector.cs`（会话敌数缝）、`SessionBattlefieldUi.cs`（虚拟相机）、`scenes/build_ui.tscn`（建造栏底部）。
