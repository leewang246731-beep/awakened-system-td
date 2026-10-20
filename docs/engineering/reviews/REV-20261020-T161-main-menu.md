# REV-20261020-T161 — A 批·主菜单场景（W10 界面美术大修首发批）

| 项 | 内容 |
|---|---|
| 任务 | T-161 A 批·主菜单场景（08 号 v1.31）：开机进主菜单——A-01 循环视频全屏背景+logo 牌匾+中文标题排版+六主题按钮卡+菜单 BGM+版本角标；全部既有入口归拢于此；--shot 截图钩子入 Main（玩家视角截图门禁的承载面） |
| 特性 | F-125（主菜单/玩家视角口径）；F-005/F-006/F-007/F-161（战役/无尽/程序关/联机入口归拢）；F-081（设置页 BYOK 入口）；31 号 §三 A-01 位（menu_loop 用途位）；18 号 §2.3（视频即放即释律） |
| 依据 | 08 号 v1.31 T-161 行+W10 立项批复（生图投权/截图验收门禁）；31 号 §1.2 六时代色板锚（visual-assets.json palettes，RL7 键不色）；v1.1.1 热修教训（51 号：玩家窗口路径永不自检 Quit——headless 分叉红线）；T-152 VisualExpansionDirector 既有 g 位（menu_loop 播放器与素材目录）；T-160b 会话泳道先例（SessionActive/PanelOpen 谓词面）；export_presets.cfg product_version（v1.1.x 发版版本面，build_v111.sh 预检项） |
| 交付物 | ①纯逻辑半边：`EraCore/Presentation/MainMenuDecisions.cs`（入口表/背景双保险链裁决/logo 横裁带几何/版本角标归一/BGM 态锚——`EraCore.Tests/Presentation/MainMenuDecisionsTests.cs` 7 测试）+②表现层：`scripts/MainMenuUi.cs`（CanvasLayer 100 全屏：A-01 循环视频[VolumeDb=-60 原声压静音]→keyart 静帧→深色底双保险链；768×512 logo 牌匾 300×200 等比居中+中文标题叠天空区[垂直 5%~50% 带内不盖六塔群像]+副题；六主题按钮卡三列两行=战役/无尽/程序关卡/联机/设置/退出，时代色描边[visual-assets.json palettes 查表]+悬停放大+ding 音效；版本角标=单一版本源解析[export_presets.cfg→exe PE 资源→EraCore.VersionInfo 兜底]；每帧可见性谓词=任一会话在局/面板开/演出中→隐藏[隐藏即停视频释放]）+③启动流分叉：`Main.cs`（headless=夹具冒烟路径逐字节照旧[G4 十三段判据零改动]；窗口=开机进主菜单+泳道持有位 CampaignUi/EndlessUi/ProceduralUi/NetUi/CineDirector/ExpandDirector）+④入口归拢：CampaignMenuUi/EndlessMenuUi/ProceduralMenuUi/NetMenuUi 各加 OpenFromMenu/CloseFromMenu/SessionActive/PanelOpen（窗口态悬浮入口按钮隐藏，headless 冒烟照旧；各泳道面板加「返回主菜单」钮——会话退出→回主菜单链路）+SettingsPanelUi「返回主菜单」钮+⑤`--shot` 截图钩子：Main.ShotHook 五段编排（全景/牌匾标题裁剪特写/真鼠标悬停态/晚 10s 循环帧[≠首帧证据 video_pos 行]/真按钮信号点入过渡帧；走完 SHOT_OK+显式退出；无参玩家车道永不 Quit）+⑥AudioDirector.BgmFact 事实探针（三态机当前态+该时代层真在响——MENU 行只报真值）+⑦asset_validator 显式豁免区④（presentation textures/ui/ UI 美术底图——W10 AI 生图投权面不进 sprite 管线；结构/命名红线不放松；selftest 78/78）+⑧project.godot 初始窗口 1280×720（画布 640×360 域不变）+⑨logo-banner.png（768×512，vibeapi 候选 AI 自选落位；.import 由 --headless --import 生成，gitignore 属正常）+⑩五张真机截图 docs/screenshots/w10-t161/ |
| 环境 | 本机开发机（D 盘纪律）；零网络零生图（牌匾底图=立项批候选既有件）；窗口车道真机跑（Godot 4.7.2 mono console + 1280×720 窗口） |

## 一、入口归拢口径（诚实记录）

任务卡第 4 卡「军师(BYOK)」**无独立既有入口面**——BYOK 表单与军师三档下拉（L1/L2/L3）都在设置页（SettingsPanelUi，C-26/T-137 交付）内。按「找不到的入口如实报告，别假接」纪律：六卡=**全部既有入口**（战役/无尽/程序关卡/联机/设置/退出），军师(BYOK) 归并入设置卡（不造假卡）；联机（NetMenuUi，T-139）是既有入口必须归拢，故占第 4 卡位。MainMenuDecisions.Entries() 键=e1~e6 时代色板（RL7：色值查 visual-assets.json，代码只留键）。

## 二、headless 分叉纪律（v1.1.1 红线执行）

Main._Ready 分叉：headless=660 帧夹具循环+面板冒烟+RENDER/WEB/PERSONA 判据段+SMOKE_OK+Quit(0) **逐字节照旧**（G4 十七行判据实测全绿，CINE_V2 playback 行[VisualExpansionDirector:659 同族]继续工作）；窗口=跳过全部自检 Quit 路径（L2 冒烟本就 headless-only）→ MainMenuUi.Instantiate（对 headless 调用直接 throw——绝不挂进冒烟局）→ MENU_BOOT 行。菜单可见性=每帧谓词（CanvasLayer 100 隐藏即停视频释放整文件驻内存——18 号 §2.3 律）。

## 三、菜单 BGM（既有管线复用，零新生成）

AudioDirector 三态 BGM 机（T-092 代码合成+等功率淡换）自 _Ready 即 StartBgm(Calm)；菜单态=夹具无敌人 ⇒ _Process 三态裁决恒落 Calm——**菜单 BGM=calm 态循环是既有管线的自然结果，非新轨**。MainMenuDecisions.MenuBgmState="calm" 锚定（G1 断言）；AudioDirector.BgmFact() 事实探针取真值进 MENU 行（`bgm=calm:audible`——不伪造状态）。A-01 视频原声压到 -60dB（菜单听觉面让位 BGM）。

## 四、玩家视角截图验收（W10 新门禁承载面）

`--shot=<目录>`（窗口专用；headless 忽略）五段编排：①预热 150 帧后 01-menu-full.png+02-logo-title.png（同帧真裁剪——任务卡允许的特写形态）②InputEvent 真鼠标移到「战役」卡中心→03-button-focus.png（真悬停态：放大+高亮）③晚 600 帧（10s）04-video-live.png（同机位不同视频帧；SHOT 行带 video_pos 证据——实测 01@2.55s vs 04@12.81s，像素差异采样 68.9%）④`--shot-open campaign` 真按钮 Pressed 信号点入（与玩家点击同一条链）→16 帧落定 05-transition.png（菜单隐藏+战役面板在屏）⑤SHOT_OK+正常退出（验收车道显式退出；**无参玩家车道永不 Quit**——窗口存活 25s 实测：无参启动 25s 不自退+RENDER_PROBE 第 60 帧在）。B/C/D 批复用：`--shot-open <键>`+`--shot-frame N` 加长预热即截战斗内画面。

## 五、门禁与实测

终跑（静默窗口，2026-10-20）：

| 门禁 | 结果 |
|---|---|
| G2 红线 | PASS：9 checks 0 FAIL 0 WARN |
| G2.5 资产 | PASS：754 files 0 rejected（ui 美术底图豁免区④承载 logo-banner） |
| G2.6 模组 | PASS：0 FAIL |
| G1 测试 | PASS：**6916 passed, 0 failed**（3293s；含 MainMenuDecisionsTests 18 例） |
| G3 Release | PASS：零警告（TreatWarningsAsErrors=true） |
| G4 冒烟 | PASS：SMOKE_OK 捕获（`SMOKE_OK core=0.0.1-wave0 scale=1`——十三段判据输出形态照旧） |
| run_all | **六步全绿（总体 PASS）** |

截图验收实测：w10-a/ 五组会话（无 open 四张+campaign/settings/endless/procedural 各五张，SHOT_OK 汇总行齐）+w10-t161/ 五张（本 REV §四 口径）；窗口存活 30s 实测（无参启动不自退、零 ERROR/QUIT 行、MENU 行 background=MenuLoopVideo bgm=calm:audible version=v1.1.1）。

G1 前置两跑各有一/两个 perf 护栏超线（Spike1/SimGrouping——满载计时抖动，超线测试单套件复跑全绿；与本批改动面零因果），终跑静默窗口全绿——诚实记录见 REV-20261020-T161-verification（验证侧全档）。

## 六、诚实遗留

1. **同仓并发施工碰撞实录**：本批施工期检测到另一存活代理在同仓并行（T-162 banner/T-163 icon 资产生成+同任务截图）——期间一次窗口跑因共享 dev journal 文件锁互斥中断（data/reports/t055-*.jsonl 被对方实例占用）、一次 Godot 缺 DOTNET_ROOT 环境启动失败、G1 首跑 Spike1 性能门禁在对方满载下超线（3.295ms>3.0ms，中位重判仍超；单独重跑即绿——v1.9 登记的负载容差同款机制）。处置：错峰重跑门禁全绿；本提交只圈 T-161 范围文件（对方在途资产不混入）。
2. **中文字体**：仓库无自定义中文字体资源——全部既有中文 UI 同用引擎缺省字体，本场景沿用同一缺省+8px 黑描边+投影保可读（截图实测标题清晰）。引入自定义字体（如思源黑体授权件）挂 T-164 皮肤批一并裁定。
3. **logo 牌匾天空区排布**：标题/副题按 768×512 构图的天空带（5%~50%）锚定排布；若后续换牌匾图（构图变化）需复核 MainMenuUi 版式常量（PlaqueWidth/TitleFontPx 等——本文件顶部版式常量块）。
4. **--shot 无 hover 断言**：03-button-focus 的悬停态靠真 InputEvent 驱动，但门禁未机械断言「卡已放大」（视觉判据留人工查收——截图即判据）。

## 七、版本角标单一版本源

不新造版本号常量：开发/编辑器轨读 res://export_presets.cfg `application/product_version`（v1.1.x 发版链在此升版——实测读出 v1.1.1）；导出件读 exe PE 版本资源（同一 product_version 由导出器打入）；兜底 EraCore.VersionInfo（引擎内部 wave 版本）。三源同一发布面。
