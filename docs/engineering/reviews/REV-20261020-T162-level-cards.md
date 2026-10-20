# REV-20261020-T162 — B 批·选关卡片化（W10 界面美术大修第二批）

| 项 | 内容 |
|---|---|
| 任务 | T-162 B 批·选关卡片化（08 号 v1.31）：战役=章节通栏题图+关卡卡片网格（banner 缩略图/关名/★★★/锁/威胁角标）；无尽/程序关模式卡片化；【必修】T-161 Spec 审查揪出的出厂包降级面（表现层美术资产根三级链）；导出包实测复验门禁；两件顺手修（ShotHook 魔法数/EndlessMenuUi.SessionActive 口径） |
| 特性 | F-005（战役选关承载面）；F-006/F-007（无尽/程序关入口卡片化）；F-125（呈现层基线——主菜单地基上的第二层）；31 号 §三（banner 素材位的 UI 消费面）；18 号 §2.3（视频释放律沿用）；v1.1.1 热修同族（数据/资产根三级链纪律） |
| 依据 | 08 号 v1.31 T-162 行；73e1219 素材入库面（31 张 banner：29 关全覆盖+2 模式卡，768×512）；映射权威源=game/content/levels/e1~e6.json items[]（映射按「时代+序号」确定性推导，slug=文件名装饰不参与）；威胁角标=关卡 threat 既有字段（e1~e6.json 数据行 0.3/0.45/0.6/0.75/0.9）；星级=战役进度既有三重目标口径（VictorySettlementStore ★=VerdictRows 通过数）；锁=既有解锁逻辑（EraIndex+LowerSlotsCleared+奇观位）；T-161 MainMenuUi/--shot 钩子地基（8a5e689） |
| 交付物 | ①纯决策半边：`EraCore/Presentation/LevelCardDecisions.cs`（banner 主干推导[时代+序号，slug 不参与]/目录清单唯一挑选/威胁三档分界/星槽归一/模式卡主干）+`EraCore.Tests/Presentation/LevelCardDecisionsTests.cs` 30 例（含真文件 29/29 映射验证——e1~e6.json 数据行↔banner 目录逐行命中）+②资产根三级链：`ExpandAssetPaths.RepoRoot()` 重造为与 SimHost/DevFileStore 同族（env ERA_DATA_ROOT[自身+父目录]>editor 特征[仓库 walk-up]>exe 旁目录>程序集 walk-up 兜底）+banner 装载面（BannerFileNames/BannerPath）+③CampaignMenuUi 卡片化：面板=CanvasLayer 90 全画布（盖夹具/信息层，让位主菜单 100）+六章各=终关 banner 通栏题图（整幅 KeepAspectCovered+底部压暗带章名+全锁章「未解锁」徽记）+5 列卡片网格（banner 缩略图 118×56+关名+★★★ 星槽+威胁角标三色+锁遮罩+◆终关徽记+时代色描边）；卡片点击进既有 LevelHost 流（BeginLevel 零改动）；CampaignLevelRow 扩 slot/threat/isFinal 三数据位（既有字段零新造）+④EndlessMenuUi/ProceduralMenuUi 模式卡片化：banner-mode-*.png 主视觉横裁带+两档节奏卡（选中态时代色描边）+面板 584 宽卡片场+⑤ShotHook：LogoTitleCropRegion const（Rect2I(120,0,400,235) 提名）+`--shot-plan level-cards` 五段编排（真按钮信号点入/真滚轮滚动/真返回信号——全程无第二套开面板逻辑）+⑥EndlessMenuUi.SessionActive 对齐 ProceduralMenuUi（Running 才算在局，Settled 残留不锁菜单）+⑦`tools/release/build_v120_dev.sh`（build_v111 家族 Windows 精简形态：banner 31/聚合图 97/ogv 52 预检+导出+装配+裸包冒烟）+⑧截图：docs/screenshots/w10-b/ 七张+docs/screenshots/w10-b-export/ 六张（裸包） |
| 环境 | 本机开发机（D 盘纪律）；窗口车道真机跑（Godot 4.7.2 mono console 1280×720）；裸包=仓库树外目录（D:\tmp-t162-bare——walk-up 不可能碰仓库，模拟玩家机） |

## 一、29/29 banner 映射（确定性推导，slug 不参与）

映射规则（`LevelCardDecisions.LevelBannerStem`）：非终关=`banner-e{时代}-l{slot}-`、终关（id 尾 `-final`）=`banner-e{era}-final-`——e1 json 第 1 行↔l1、final 行↔final；文件名尾部 slug 只是装饰，装载=主干前缀在目录清单里挑唯一文件（`PickBannerFile`，字典序最小确定性消歧）。章节题图=该时代终关 banner 整幅（任务卡「终关 banner 横裁或整幅」的整幅侧）。

验证三面：
- **G1 真文件测试**：`RepoBanners_CoverAllTwentyNineCampaignRowsAndBothModeCards`——walk-up 仓库（UiContentFilesTests 同律，RL9 零绝对路径）读 e1~e6.json 全 29 行+banner 目录实清单，逐行断言命中；29/29+2 模式卡全绿（30 例整套通过）。
- **窗口车道运行时**：`CAMPAIGN_CARDS chapters=6 cards=29 banners=29/29 root=<根>`（RefreshMenu 即打印，缺数如实上报）。
- **裸包运行时**：同行 `banners=29/29 root=D:\tmp-t162-bare`（见 §三）。

## 二、资产根三级链（T-161 Spec 审查降级面修复）

`ExpandAssetPaths.RepoRoot()` 由单一 walk-up 重造为 SimHost/DevFileStore 同族三级链：①env `ERA_DATA_ROOT`（start-game.bat 注入的包内 data 子根——自身与其**父目录**[=包根]都探测）→②editor 特征=仓库 walk-up（dev 编辑器轨不回退）→③exe 旁目录（发布裸包直点 exe——game/assets、game/content 随包在 exe 旁直接可达）→④程序集 walk-up 兜底（dev 直跑 mono 临时 exe 形态不回退）。MainMenuUi 的 MenuLoopOgvPath/keyart 与本批 banner 装载全部经此链。dev 仓库形态行为不回退（dev 会话 root=D:\game 实测）。

**裸包实测**（包=release/v1.2.0-dev/T-162-bare-win64 拷至仓库树外 D:\tmp-t162-bare，walk-up 无仓库可碰）：
- launcher 形态（ERA_DATA_ROOT=包内 data）：`root=D:\tmp-t162-bare` ✓
- 直点 exe（无 env）：`root=D:\tmp-t162-bare` ✓（三级链两条腿都实测）

## 三、导出包实测复验（本批验收门禁）

产物：`tools/release/build_v120_dev.sh`（build_v111.sh 家族 Windows 精简形态；版本面=export_presets v1.1.1 参数复用——升版面归主管发版链，任务卡允许项；预检=banner 31+expand-v1 聚合图 97+ogv 52 [73e1219 后实际资产面]；裸包冒烟 SMOKE_OK+CAMPAIGN_OK matrix=29/29 全过）→ 裸包目录+`--shot-plan level-cards`（在裸包目录里跑，仓库树外）。

**三证**（docs/screenshots/w10-b-export/）：

| 证 | 证据 |
|---|---|
| 六时代色按钮卡有色差 | 00-menu-full.png（六卡描边=晨曦赭金/古典绿/工业棕橙/现代蓝紫/未来青蓝/终章靛蓝六色互异，截图实测） |
| A-01 视频背景活着 | `MENU background=MenuLoopVideo`+SHOT 行 video_pos=2.57s（launcher 形态）/2.5s→13.15s 推进（直点形式四段编排，w10-t161 同款≠首帧证据） |
| 选关卡片网格有 banner 缩略图 | 01-chapter-overview.png+`CAMPAIGN_CARDS banners=29/29`（六章节题图+卡片缩略图全为真实插画，非纯色块） |

## 四、五张自验截图（docs/screenshots/w10-b/，--shot-plan level-cards）

| 张 | 内容 | 验证点 |
|---|---|---|
| 01-chapter-overview | 章节通栏+网格全景（E1/E2 章可见） | 章题图（远古/古典）+5 列卡片+章名带压暗底；全锁章徽记见 04 |
| 02-level-cards | 首章特写（同帧真裁剪 1260×352） | 觉醒林地★★/枯河隘口★（通关星）/石语河谷可进入/双丘烽线+◆踏群者王庭锁遮罩/威胁·低绿·中黄·高红角标 |
| 03-mode-cards | 无尽模式卡 | banner-mode-endless 主视觉+两档节奏卡（滚词缀选中态描边）+行动排 |
| 04-locked-state | 滚轮滚至 E5/E6 章 | 未解锁章节（全锁）+全卡未解锁遮罩+威胁角标（真滚轮 64 档，campaign_scroll=719 证据） |
| 05-export-bare | 裸包关键帧（=w10-b-export/01 同源复用不同名） | 裸包卡片网格+banner 缩略图 |

编排全程真信号链：入口点入=真按钮 Pressed（OpenEntry）/返回=真按钮 Pressed（PressMenuReturn）/滚动=真 InputEventMouseButton 滚轮（窗口坐标域——画布域发滚轮不动 ScrollContainer 的实测教训）/特写=同帧真裁剪（非合成）。

**星显示的存档种子（诚实登记）**：02 的 ★ 需已通关进度——本机玩家根无战役进度，截图会话经 `ERA_DATA_ROOT` 指到临时根 `D:\tmp-t162-seed\root`，其 victory-settlements.json 由**生产序列化器**（VictorySettlementStore.Save 经仓库外一次性 console 工具，EraCore 纯库引用）写入 e1-01 两星/e1-02 一星两行真实结构档；卡片态由真实 CampaignLevelHost 进度逻辑呈现（e1-03 解锁/其余锁），非 UI 伪造。工具与临时根均在仓库外，不入库。

## 五、两件顺手修（T-161 Standards 审查判断项）

1. **ShotHook 魔法数**：`Rect2I(120, 0, 400, 235)` → `LogoTitleCropRegion` static readonly const 块（含口径注释）。
2. **EndlessMenuUi.SessionActive**：`_session is not null` → `_session is { State: EndlessSessionState.Running }`（Settled 终局残留不算在局——与 ProceduralMenuUi 的 InLevel 判据同口径；主菜单谓词不再被终局残锁）。

## 六、门禁与实测

| 门禁 | 结果 |
|---|---|
| G2 红线 | PASS：9 checks 0 FAIL 0 WARN（首跑 8 FAIL=本批新注释「占位」标记词——措辞改「垫底/垫图」后清零；既有红线零新增） |
| G2.5 资产 | PASS：760 files 0 rejected（31 banner+92 icons 经 P1 聚合区/ui 豁免区承载——73e1219 入库面首次过门禁实测） |
| G2.6 模组 | PASS：0 FAIL |
| G1 测试 | PASS：**6946 passed, 0 failed**（1375s；T-161 基线 6916+本批 LevelCardDecisionsTests 30 例） |
| G3 Release | PASS：零警告（TreatWarningsAsErrors=true——裸包构建同链过） |
| G4 冒烟 | PASS：SMOKE_OK 捕获（十三段判据零改动；裸包形态亦过——CAMPAIGN_OK matrix=29/29） |
| run_all | **六步全绿（总体 PASS，exit=0）** |
| 窗口存活 | 无参 26s 不自退（timeout rc=124=进程活到被杀；日志零 ERROR/Quit 行；MENU 行 background=MenuLoopVideo bgm=calm:audible version=v1.1.1） |
| headless 红线 | 判据段零改动（CampaignMenuUi 冒烟/Endless 冒烟/Procedural 冒烟全部照旧——BuildUi 仅建结构不载 banner，冒烟路径不碰新面） |

## 七、施工口径备忘

- **卡片点击链**：OnLevelPressed→`_host.BeginLevel(levelId)` 逐字节未动；Disabled 态=既有（Playable 才可点——重玩挂账 REV §七遗留⑥ 原样）；存档续玩/推进/跃迁语义零回退（宿主逻辑零 diff）。
- **威胁角标口径**：数据=threat 既有字段（别不新造）；三档分界 <0.45 低 / <0.75 中 / ≥0.75 高（既有定值集 0.3/0.45/0.6/0.75/0.9 的呈现切分——低=起步/中=推进/高=压轴）；角标底色=通用红黄绿交通灯语义（非 RL7 时代色板域，与 MainMenuUi 中性色同律）。
- **面板让位序**：选关面板 CanvasLayer 90——盖夹具战场+信息层（分区命名/提示气泡在菜单态是噪音，T-161 首版截图实测泄漏——本批收口），让位主菜单（100）与败北面板（同层尾序）。
- **banner 不走 res://**：game/assets 在 Godot 项目根外（exe 旁松散文件轨——T-096-v02 先例），无 .import 依赖；build_v120_dev 的 --import 步照跑（logo-banner 等项目内件）。

## 八、诚实遗留

1. **章节锁徽记的可见性**：04-locked-state 滚到底（E5/E6 章）时章名带在图上但首章锁徽记（「· 未解锁」后缀）在全锁章上才出现——本机种子进度下 E2+ 全锁均有；纯文案态由 `chapter.Rows.All(Locked)` 数据驱动，无截图逐一取证（视觉判据留人工查收）。
2. **02 星数视觉粒度**：★★/★ 以 10px 字号渲染，截图肉眼可辨但偏小（T-164 皮肤批若做卡面精化可提字号/换星形图标）。
3. **build_v120_dev.sh 无 zip/USB/Linux 通道**：本批门禁只需一个本地裸包；正式发版仍走 build_v111 家族全量脚本（版本面升版归主管）。
4. **无尽/程序关模式卡的「卡片化选项」边界**：节奏两档=真卡片选择；时代/档位/种子保留下拉与文本框（六时代下拉卡片化收益低且加高面板——留 T-164 皮肤批一并裁定）。
5. **直点 exe 的存档根**：三级链修的是**美术资产**根；SimHost 数据根在直点 exe 形态仍落 exe 旁 data\（v1.1.1 热修既有行为，快速开始.txt 已引导玩家走 start-game.bat——本批不动）。
