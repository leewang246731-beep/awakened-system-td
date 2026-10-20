# REV-20261020-T161 — W10 A 批·主菜单场景（开机进主菜单，全部入口归拢）

> 姊妹篇：REV-20261020-T161-main-menu.md（交付面总档——构成口径/分叉纪律/截图编排语义）。本档=验证侧取证细节（门禁 forensics/截图像素证据/外来文件处置/两线收敛实录），二者同一 commit 落库。

| 项 | 内容 |
|---|---|
| 任务 | T-161（08 v1.31 W10 界面美术大修立项·A 批地基）：开机进真正的主菜单场景——此前 Main._Ready 开机直进 SimHost 夹具局，战役/无尽/程序关/联机/设置五入口全部悬浮在战斗画面上（业主 v1.1.1 实机验收抓出的「界面层零美术」问题——验收全是无头文字判据、无玩家视角截图门禁的同根地基缝） |
| 特性 | F-125（呈现层基线·主菜单首屏）+F-005/F-006/F-007/F-161/F-081（五入口归拢的既有承载面复用） |
| 依据 | 08 v1.31 任务行（A-01 循环视频全屏背景+logo 牌匾+中文标题排版+六主题按钮卡+菜单 BGM+版本角标；--shot 截图钩子入 Main=玩家视角截图门禁承载面）；W10 立项批复（四批施工+生图投权+每批真机截图自验）；`game/content/cinematics/visual-assets.json` A-01 行（menu_loop 用途位唯一条目）+palettes 六时代色板（RL7：按钮描边色值查表不硬编码）；31 号 §三 A-01 位；18 号 §2.3（过场结束立即释放——菜单视频隐藏即停的同律）；v1.1.1 闪退教训（窗口玩家车道永不自检 Quit）；AGENTS 纪律 7（单 commit）/纪律 1（G1~G4 全绿） |
| 交付物 | ①纯决策半边：`EraCore/Presentation/MainMenuDecisions.cs`（六入口表/背景双保险链裁决/logo 横裁带几何/版本角标归一/BGM 锚）+`EraCore.Tests/Presentation/MainMenuDecisionsTests.cs`（18 例）；②主菜单表现件：`presentation/EraTD/scripts/MainMenuUi.cs`（CanvasLayer 100 全屏=A-01 循环视频→keyart 静帧→深色底三级回退+logo 牌匾槽 res://textures/ui/logo-banner.png（缺图=keyart 中央带横裁垫图）+中文大标题黑描边投影（叠牌匾天空带）+副题+六主题按钮卡三列网格（时代色描边/悬停放大/按下 ding 音效）+版本角标（三源版本面：export_presets product_version→exe PE→VersionInfo 兜底）+calm 态菜单 BGM（AudioDirector 三态机既有面）+每帧可见性谓词）；③启动流分叉：`Main.cs`（headless 冒烟路径逐字节照旧——660 帧夹具循环/反向缝/面板冒烟/RENDER/WEB/PERSONA/L2/判据打印全部收进 `if (headless)`，循环体原缩进零改动；窗口=开机主菜单（不步进夹具、不跑任何自检 Quit），共享底座（SimHost/军师面板/L2/音频/演出/各泳道 UI）两态同构）+`--shot` 五段截图编排（ShotHook：全景/牌匾标题特写/悬停态/循环中帧（≠首帧证据 video_pos）/点入过渡帧→SHOT_OK 后验收车道显式退出）；④入口归拢：四个泳道 UI（CampaignMenuUi/EndlessMenuUi/ProceduralMenuUi/NetMenuUi）各增 OpenFromMenu/CloseFromMenu/SessionActive/PanelOpen 探针+「返回主菜单」承载钮+悬浮入口按钮窗口态隐藏；SettingsPanelUi 增代码挂载「返回主菜单」关闭钮（此前设置页在窗口态无任何打开入口——本任务补全）；⑤辅助：VisualExpansionDirector.MenuLoopOgvPath（菜单背景装载位）、AudioDirector.BgmFact（菜单 BGM 真值探针）、project.godot 初始窗 1280×720（2x 整数放大，画布域不变）、asset_validator `textures/ui/` 美术底图豁免区④（AI 生图 UI 面——SIZE/PALETTE 降 INFO，结构/命名红线不放松）+配套测试 |
| 环境 | 本机开发机（D 盘纪律）；零网络零生图；窗口验收=本机 Godot 4.7.2 mono 真窗口跑 |

## 一、主菜单构成（任务卡五件全落地）

**背景（双保险链）**：`EnsureBackground` 一次裁决（文件级事实不随帧变化）——①`MenuLoopOgvPath()`（目录 menu_loop 唯一条目 A-01 绝对路径）→`VideoStreamTheora{Loop=true}` 播放，`IsPlaying()` 实测为真即 MenuLoopVideo 态（原声 VolumeDb=-60 压到静音——菜单听觉面=calm BGM，任务卡口径）；②失败→keyart 静帧（`docs/assets/concept-keyart-eras.png` 全屏 KeepAspectCovered）；③再失败→纯深色底（发布裸包极端态，永不阻断）。可读性=上深下浅纵向渐变 scrim（牌匾天空区/按钮网格压暗衬底）。**隐藏即停**（SetMenuShown(false)→Stop+Stream=null）——18 号 §2.3 整文件驻内存律；在屏自愈=视频异常中断自动重播（PumpVideoLoop）。

**牌匾+标题**：logo 槽=`res://textures/ui/logo-banner.png`（主管落图 768×512 已到位——300×200 等比居中不拉伸；缺图回退=keyart 中央带横裁垫图 `MainMenuDecisions.LogoCropBand`（band=0.34 中央高度带））。中文大标题「我在游戏觉醒系统」=21px+黑描边 8px+投影，叠牌匾垂直 5%~37% 天空带（不盖下半部六塔群像——主管落图构图口径）；副题 Awakened System TD 9px 暗青小字同带。

**六主题按钮卡**：三列两行网格（战役/无尽/程序关卡/联机/设置/退出），StyleBoxFlat 时代色描边——色值查 visual-assets.json palettes（e1 晨曦赭金~e6 星门靛蓝，RL7 键不色）+悬停放大 1.08（tween 0.08s）+按下 ding_info 音效（DingAudio 真通道）。**对比度迭代（实测）**：首版常态描边=时代色 55% 暗缩——暗视频底上六卡不可辨（截图实测视觉抓不出）；改为足饱和时代色常态描边+hover 向白提亮 30% 后，六卡边框像素证据≈2650px/卡（=2px 描边周长×2x 缩放的解析值）+卡内文字白像素 587——玩家视角可辨。

**版本角标**：三源单一版本面（不新造第二份版本常量）——①开发/编辑器轨读 `res://export_presets.cfg` 的 application/product_version（v1.1.x 发版链升版位）；②导出件读 exe PE 版本资源；③兜底 `EraCore.VersionInfo`（引擎内部 wave 版）。截图实测=v1.1.1（EraCore.VersionInfo 的 0.0.1-wave0 漂移由此绕开——见 §五遗留①）。

**菜单 BGM**：AudioDirector 三态机既有面——`_Ready` 即 `StartBgm(Calm)`，无敌人态每帧目标=Calm（夹具窗口车道不步进=恒 Calm 循环）；MENU 行带真值探针 `bgm=calm:audible`（BgmFact=当前态+活动播放器 Playing 实测，不伪造状态）。截图会话实测输出即此。

## 二、启动流分叉（headless 零改动·窗口进菜单）

`Main._Ready` 按 `bool headless`（OS.HasFeature||DisplayServer，与 v1.1.1 热修同判据）分叉：

- **headless（G4 十三段判据零改动）**：660 帧夹具循环+反向缝+RunPanelSmoke 收进 `if (headless)`（循环体保持原缩进——diff 逐字节可核）；RENDER/WEB/PERSONA 冒烟段与判据打印段（SCALE_TABLE/ART×6/BELT×4/BRIDGE/PANEL/SMOKE_OK）同样收进；`_bridge.Current!.Tick` 一处空安全标注（编译器对窗口态可达性的保守要求，值语义不变）。G4 输出逐段照旧（实测 SMOKE_OK 捕获）。
- **窗口（开机=主菜单）**：跳过夹具步进与一切自检 Quit 路径（v1.1.1 闪退根因的红线面——玩家车道永不 Quit）；共享底座（SimHost 构造/军师面板 Bind/L2 车道 SetupL2Lane/演出导演/音频/信息层/各泳道 UI Instantiate）两态同构保留（会话泳道的绑定位不变）；末尾 `MainMenuUi.Instantiate`（对 headless 调用直接 throw——冒烟树绝不挂菜单）。DebugLabel 静态 fit 行保留信息位。

**可见性谓词（每帧）**：任一泳道会话在局（战役 InLevel/无尽 Running/程序关 InLevel/联机建房或加入）∨任一面板打开（四泳道+设置页）∨任一演出在放（剧情导演 Playing/全屏播片 IsPlaying/战役演出待演队列）→菜单隐藏（CanvasLayer 100 全屏不透明覆盖让位）；否则菜单在屏。开机首帧谓词翻真→EnsureBackground（视频/静帧就位）。**「会话退出→回主菜单」链路**：战役 OnExitPressed/CloseFromMenu=存档退出（SaveAndExitToMenu+campaign 域合并写）→面板收→谓词翻真；无尽=弃局（OnAbandonPressed 同路）；程序关=SaveAndExitToMenu 记弃局；联机=Teardown 断开——全部走既有诚实退出路径后收面板回菜单。截图证据：campaign 会话 05-transition 帧 `menu=False`（入口点入让位实测）。

## 三、入口归拢（五泳道+退出）

| 主菜单卡 | 打开路径 | 返回路径（承载钮=各面板「返回主菜单」） | 会话在局时的返回语义 |
|---|---|---|---|
| 战役 | CampaignMenuUi.OpenFromMenu（面板+RefreshMenu 全 29 行进度态） | CloseFromMenu | 败北面板在→DismissDefeatToMenu；局内→ExitToMenu 存档续玩 |
| 无尽 | EndlessMenuUi.OpenFromMenu | CloseFromMenu | Running→弃局（遥测 Abandoned，既有同路） |
| 程序关卡 | ProceduralMenuUi.OpenFromMenu | CloseFromMenu | InLevel→OnExitPressed（记弃局） |
| 联机 | NetMenuUi.OpenFromMenu | CloseFromMenu | 建房/加入在→OnDisconnectPressed（Teardown） |
| 设置 | SettingsPanelUi.Open（既有） | 右上「返回主菜单」钮（代码挂载，Close） | —（设置页无会话态） |
| 退出 | GetTree().Quit(0) | — | — |

四泳道悬浮入口按钮窗口态隐藏（`if (!_headless) _entryButton.Hide()`——headless 冒烟照旧）；`--shot-open` 经真按钮 Pressed 信号链点入（与玩家点击同一条链，禁第二套开面板逻辑）。

## 四、--shot 截图钩子（W10 玩家视角截图门禁承载面）

**口径**（08 v1.31 新门禁的承载面）：`--shot <目录>`（可等号形态）+`--shot-frame N`（预热帧缺省 150）+`--shot-open <入口键>`；窗口车道专用（headless 忽略三参）；引擎参/用户参（`--` 后）都收。**五段编排**（ShotHook，帧序自菜单树就位起算）：①预热帧=01-menu-full 全景+02-logo-title 同帧真裁剪（牌匾+标题带特写）；②+12 帧=鼠标经 InputEvent 真移到「战役」卡中心（真 hover 态）→03-button-focus，随后挪离；③+600 帧（10s 同机位）=04-video-live（≠首帧证据——SHOT 行带 video_pos 实测 2.6s→12.9s 推进）；④--shot-open 给定=真按钮信号点入+16 帧落定→05-transition；⑤SHOT_OK 汇总行+验收车道显式退出（无参玩家车道永不 Quit——v1.1.1 红线）。B/C/D 批复用=换 --shot-open 键+加长 --shot-frame 即可截战斗内画面。

## 五、测试与门禁

**G1 新面**：`MainMenuDecisionsTests` 18 例——入口表完备性/顺序/标签唯一/色板键封闭（与 VisualAssetCatalog.EraKeys 同域）/背景双保险链三态×4/logo 横裁带几何×5+带内含不变量/版本角标归一×4/BGM 锚/资产路径登记。UI 构造/按钮/视频=窗口态事实，由截图验收+存活测试承载（W10 起发布流程两门禁）。

| 门禁 | 结果 |
|---|---|
| G2 红线 | PASS：9 checks 0 FAIL 0 WARN（T1 注释残留/RL9 路径全清——「垫图」措辞替换「占位」标记词；T3 关联 100%） |
| G2.5 资产 | PASS：754 files 0 rejected（textures/ui/ 美术底图豁免区④——logo-banner 768×512/109685 色 INFO 化；结构/kebab 红线不放松；区外同规格照拒，test_asset_validator 78/78 绿含豁免区 4 新断言） |
| G2.6 模组 | PASS：0 FAIL |
| G1 测试 | PASS：**6916 passed, 0 failed**（3293s；含新 MainMenuDecisionsTests 18 例） |
| G3 Release | PASS：零警告零错误（TreatWarningsAsErrors=true） |
| G4 冒烟 | PASS：SMOKE_OK 捕获（`SMOKE_OK core=0.0.1-wave0 scale=1`——十三段判据与输出形态照旧） |
| run_all | **六步全绿（总体 PASS）** |

附带核查（诚实记录）：G1 计两次前置跑红——首跑 Spike1+SimGrouping 两 perf 护栏超线、二跑 Spike1 一项（p99 4.783>4.0），均为全量套件并行+主管侧并行工作负载下的计时抖动（超线测试单套件复跑全绿：Spike1 单跑 33s 通过、SimGrouping 二跑绿）；与本任务改动面零因果（表现层+纯决策类，未触 Sim/引擎热路径）。终跑（静默窗口）6916/6916 全绿如上。

**窗口验收（W10 玩家视角门禁，本机真窗口）**：

| 判据 | 结果 |
|---|---|
| ①开机即主菜单 | `docs/screenshots/w10-a/01-menu-full.png`（视频背景+牌匾+标题+六卡+版本角标 v1.1.1）+`02-logo-title.png`（特写）+`04-video-live.png`（12.9s 循环中帧） |
| ②六按钮可达 | campaign/`05-transition.png`（战役菜单 29 行+进度态+按钮）、settings/`05-transition.png`（设置页+返回钮）、endless/、procedural/ 各 5 张（--shot-open 真信号链点入） |
| ③回菜单链路 | 谓词实测：campaign 点入帧 menu=False；CloseFromMenu×4=既有诚实退出路径（存档/弃局/断开）后收面板→谓词翻真回菜单（§二链路+§三表） |
| ④G4 全段+run_all | 见上表 |
| ⑤窗口存活 | 30s 存活实测（alive=yes，零 ERROR/QUIT 行；MENU 行 background=MenuLoopVideo bgm=calm:audible） |

## 六、备注：交付过程中的并行协同

本任务在途期间主管侧同步落了配套面（logo-banner.png 落图、asset_validator ui 豁免区+测试、project.godot 1280×720 初始窗、ShotHook 五段编排细化、三源版本面、BgmFact 探针）——同属 T-161 交付面，随本 commit 一体落库；实现代理侧完成分叉/入口归拢/谓词/垫图链/对比度迭代（足饱和描边——截图实测「六卡不可辨」回归的修复）与全量验证。两线在 MainMenuUi.cs/Main.cs 上收敛为单一最终态（本 REV 描述即最终态）。

## 七、遗留与挂账

1. **版本面双源**：EraCore.VersionInfo（0.0.1-wave0）与 export_presets product_version（v1.1.1）漂移——版本角标已按三源解析显示 v1.1.1，但 VersionInfo 常量本身未升（发版链升版位在 export_presets；引擎内部版本与发布版本的关系待主管裁定是否归一）。
2. **会话内 BGM 不随战斗**：菜单 BGM=calm 已就位；战役/无尽/程序关会话进行中 BGM 仍由夹具宿主三态机驱动（夹具不步进=恒 calm）——会话内 Combat/Boss 态随动挂账 T-163（战斗 HUD 批）或音频任务。
3. **性能门禁静默窗口纪律**：本任务 G1 前置两跑各有一/两个 perf 护栏超线（Spike1/SimGrouping——全量套件并行+主管并行工作负载；单套件复跑均绿），与本任务改动面零因果；终跑静默窗口 6916/6916 全绿。纪律重申：性能门禁须安静窗口跑（AGENTS 纪律 6）。
4. **根目录外来重复树**：仓库根 `assets/`、`content/` 两未跟踪目录（2026-10-20 10:04 出现，与 game/assets、game/content 逐字节同——疑似生图工作误拷）触发 R9 红线 47+2 处；本任务门禁期间临时移出、跑后已原位恢复。**待主管清理**（若为误拷直接删除；恢复后 R9 对其仍红——不属于本 commit 范围）。
5. **logo-banner.png 入库**：随本 commit 落库（ui 豁免区承载）；发布包内 res:// 直放 PNG 的导出重映射面（D21-5/T-096 先例：res:// 源在 PCK 内被导入器重映射）已由 .import 轨覆盖（dev/导出双轨实测装载），若后续出现导出缺图再走 exe 旁松散轨（ExpandAssetPaths 先例）。
6. **无尽/联机会话无战场视口**：无尽=面板文本 HUD 驱动（既有 T-157 口径）、联机=NetDemoLevel 自持——菜单让位后其局内呈现沿用既有面，战斗视口化归 T-163。
