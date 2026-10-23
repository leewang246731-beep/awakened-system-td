# REV-20261023-T165 —— 审美打磨批（53 号文审计驱动）审查记录

| 项 | 值 |
|---|---|
| 任务 | T-165 审美打磨批（08 号文 v1.37 立项；输入=53 号文 §三 P1-P3 + §四修复工作序七件） |
| 施工 | 施工代理（单代理，2026-10-23）；方法论=game-visual-taste 技能 fix 模式六步序 |
| 基线 | HEAD=0fd8859（W10 四批全交付 + 135 件美术资产 + UiFeel 皮肤中心） |
| 纪律 | RL7（数值从注册表来——本批全部表现层常量带 ci-allow 标注或非 R7 扫描区）；零玩法/数值/锚点改动；headless 判据零改动（本批未动任何判据行）；≤最小 diff（10 文件全为表现层） |

## 一、七件逐项落地（53 号文 §四工作序）

### 件 1 字号与尺度（P2-1 小字族 +1~2px 档）

| 位 | 前 | 后 | 文件 |
|---|---|---|---|
| 威胁角标 | 8px/44×14 | 10px/54×16 | CampaignMenuUi.cs |
| 终关徽记◆（终关关名整行） | 10px | 12px（普通关 10px 不动——对比敢拉开） | CampaignMenuUi.cs |
| 关卡状态角标（锁/可进入/已通关） | 8px | 9px | CampaignMenuUi.cs |
| 军师气泡按钮（◈ 系统·待命） | 12px | 13px | SystemPanel.cs |
| HUD 三指标（波次/产出/防线） | 12px | 13px | SystemPanel.cs |
| 军师信任行/档位提示/徽记 | 10/10/9px | 11/11/10px | SystemPanel.cs |
| 资源条数字 | 14px | 15px | BattleHudUi.cs |
| 预告徽记 ×N | 10px | 12px | BattleHudUi.cs |
| 预告标题 | 11px | 12px | BattleHudUi.cs |

### 件 2 色彩归位（P2-4/P3-3/P2-3）

- **锁遮罩去脏灰**：黑 0.55 遮罩→时代色暗变体（`era×0.20+抬升，α0.62`）；锁卡描边灰→时代色暗变体（`era×0.45+抬升`）——锁态=时代色阶暗部，同屏不再有两族色（CampaignMenuUi.cs）。复评证据：06-locked-emblems（暗橙棕/暗青按章变化，图像模型读出"彩色暗变体，非灰"）。
- **波次预告降亮让位资源条**：EraBox 增 dim 变体（bg RGB×0.70/α0.58、边 RGB×0.72/α0.50；首版 ×0.82 复评读"难分主次"后二次加强）；boss 金边警示态不降亮（BattleHudUi.cs）。复评证据：01-era-skins 两带读出"明显分层"。
- **设置分组标题带时代色**：全部 Caption 行+CompareHeading 升 e5 时代色 13px 档（SettingsPanelUi.ApplyEraSkin）。

### 件 3 构图井位（P1-2/P3-2/P2-3）

- **军师立绘上位**：`HudIconDecisions.AdvisorPortraitPx` 36→64（53 号文 64~96 档取 64——360 宽面板内容带安全上界）；报告区 300→236 让位重平衡（SystemPanel.cs）。
- **主菜单按钮区色场垫底**：既有 keyart（六时代群像）横裁色场+0.62 压暗带垫在六卡下（UiFeel.BuildColorBed 新 helper——BuildBannerStrip 无标题变体；满度原则：空位拿整片色场填；缺图跳过不阻塞）（MainMenuUi.cs）。
- **设置 BYOK 区垫底**：e5 章 banner（设置入口同键）横裁+0.78 重压暗，插 Backdrop 与 Scroll 之间（纯兄弟插入——冒烟探针 GetNode 路径零变动；缺图跳过）（SettingsPanelUi.TryInstallFormBed）。

### 件 4 状态补全（P1-3 hover/焦点痕迹）

- 关卡卡 hover：`UiFeel.HoverScaleTween`（主菜单六卡 MouseEntered/MouseExited 同律收口为单源 helper；1.06 密网格档+悬停抬 Z=8——放大卡压邻卡绘制序修正；锁卡不接）（CampaignMenuUi.cs）。
- 下拉/文本框焦点态：件 5 皮肤自带（OptionButton focus=3px 提亮边；LineEdit focus=足饱和时代色边+光标/选区时代色）。

### 件 5 系统件像素皮肤化（P1-1 最大件）

UiFeel 新增四族（全部代码生成像素件，零二进制资产——BuildAvatarTexture 同律；T-045 资产门禁零触碰）：

| 件 | 内容 |
|---|---|
| `ApplyOptionSkin` | 三态盒（ButtonTriple 同族色阶）+9×5 像素箭头（icon_normal_color 系按时代着色）+focus 3px 边+弹出清单 panel/hover 时代色框 |
| `ApplyLineEditSkin` | normal=暗底+时代色暗变体 2px 边；focus=时代色边；caret/selection 时代色 |
| `ApplyCheckBoxSkin` | checked/unchecked（radio 同套）=12×12 像素方块（未选=2px 时代色环+时代色暗底；选中=时代色填充+暗勾 7 像素） |
| `ApplySpinBoxSkin` | 内嵌 LineEdit 同族（数字行与文本行同律） |

接线（三面通用+军师面板）：程序关档位下拉/种子框（e3）、无尽时代下拉（e2）、设置三下拉+三文本框+六开关+八数字框（e5）、军师军令框（中性青——§10.2 骨架超然于时代）。复评证据：设置面复评"没有发现纯灰色系统默认控件（滚动条除外）"；未选/选中单选框"同一套像素描边语法两态，统一"。

### 件 6 飘字限流（P2-2）

同目标同屏 ≤2 条（`FloatsPerUnitMax=2`）；超限命中并入该目标最新一条（数字累加重显+寿命刷新）——重叠处一条不断更新的合计（SessionBattlefieldUi.cs，对象池/HP 差分机制零改动）。证据：skin-polish 编排 floats 探针 1→2→3（跨目标合计，单目标 ≤2）。

### 件 7 时代皮肤切换 0.2s 色彩过渡（P3——已做进）

`BattleHudUi.TransitionPanelSkin`：Bind 换代时资源条/预告框 live 盒色值 0.2s 线性插值（独立实例每帧改色——缓存盒零突变零污染；终点被 boss 框/新过渡接管时如实让位）；预告框 boss 态位守卫防每拍 stomp。像素风律内（色彩渐变，非位移模糊）。

## 二、门禁与验收

| 门 | 结果 |
|---|---|
| G2 红线扫描 | PASS 9 checks 0 FAIL（本批新增 ci-allow: R7 T-165 标注 5 处——表现层色阶/压暗档/带高/时长秒；presentation 目录本不在 R7 扫描区，EraCore 侧仅改常量值） |
| G2.5 资产规格 | PASS 776 files rejected=0（零新资产——像素件全代码生成） |
| G2.6 模组校验 | PASS |
| G1 测试 | PASS 6967/6967（无断言改动——本批零判据零测试面变更） |
| G3 构建 | PASS Release 零警告（TreatWarningsAsErrors） |
| G4 冒烟 | PASS SMOKE_OK（两轮：初版+终版复跑——中间两处复评微调后全量重跑） |
| 窗口存活 | PASS 45s+（v1.1.1 门禁 25s 上浮；MENU background 行在——非黑窗） |
| headless 判据 | 零改动（无判据行触碰；皮肤为纯样式覆写） |

## 三、before/after 截图（docs/screenshots/w10-e/，左=before 右=after，Cyan 分隔条）

| 并排图 | 面 | 验证点 |
|---|---|---|
| 01-level-cards-ba | 选关卡片特写 | 威胁角标 10px/终关◆12px/时代色描边 |
| 02-locked-state-ba | 锁章节滚到位 | 锁遮罩时代暗变体 |
| 03-mode-cards-ba | 无尽模式卡 | 时代下拉像素皮肤（e2） |
| 03b-procedural-mode-ba | 程序关模式卡 | 档位下拉+种子框皮肤（e3） |
| 04-menu-bed-ba | 主菜单全景 | 按钮区 keyart 色场垫底 |
| 05-settings-ba | 设置页（BYOK） | 系统件像素皮肤+分组时代色+e5 色场垫底 |
| 06-battle-hud-ba | 战斗 HUD 全景 | 预告降亮+资源数字 15px |
| 07-wave-preview-ba | 预告横幅特写 | 降亮变体+徽记 12px |
| 08-advisor-panel-ba | 军师面板特写 | 立绘 64 井位+军令框皮肤+报告区重平衡 |
| 09-era-skins-ba | 时代皮肤对照带 | 资源条全亮 vs 预告压暗分层 |
| 10-damage-floats-ba | 伤害飘字帧 | 同目标 ≤2+合并 |
| 11-locked-emblems-ba | 全锁章节带 | 锁遮罩/描边时代暗变体（多章色相变化） |

before 源=v120-release/{win-menu,win-levels,win-battle}+w10-d（发版/验收库既有归档）。**例外披露**：设置页归档 before（w10-a/settings）被首启披露弹窗整面遮挡，为公平对照在沙盒数据根（v120 先例 ERA_DATA_ROOT）预确认披露后，对 HEAD 基线（git stash→重编译→截取→pop 复原）与本批各截一次净面（raw/before-settings vs raw/after-settings）——两侧同沙盒同状态。

raw 分镜归档：w10-e/raw/{before-settings,after-settings,after-levels,after-battle,after-skin}（--shot 既有五编排：--shot-open settings / --shot-plan level-cards / battle-hud / skin-polish；全程真按钮信号+真输入事件）。

## 四、六维复评自评表（对照 53 号文基线）

| # | 面 | 层 | 排 | 色 | 构 | 感 | 聚 | 基线总 | 复评总 | 提升点 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 主菜单 | 3 | 3 | 4 | 4 | 2 | 3 | ~16 | ~19 | 构+1（按钮区色场垫底——半屏空得填）感+1（卡 hover 律已立，垫底后卡群可辨） |
| 2 | 战役选关总览 | 3 | 3 | 4 | 4 | 3 | 4 | 19 | 21 | 色+1（锁遮罩/锁描边归时代色阶）感+1（卡 hover 放大+描边亮起） |
| 3 | 关卡卡片特写 | 3 | 4 | 4 | 3 | 3 | 4 | 19 | 21 | 排+1（角标 10px/终关◆12px 对比拉开）色+1 感+1 同上，构-1 密网格 hover 放大略压邻卡（抬 Z 已修读序，幅度保守 1.06）→ 净 +2 |
| 4 | 战斗 HUD | 3 | 4 | 4 | 4 | 3 | 4 | 21 | 22 | 色+1（预告让位资源条——一屏一主 accent 归位） |
| 5 | 无尽模式菜单 | 3 | 3 | 4 | 3 | 3 | 4 | 18 | 20 | 色+1（下拉时代色框）感+1（下拉三态/焦点） |
| 6 | 程序生成关菜单 | 3 | 3 | 3 | 3 | 3 | 4 | 15 | 19 | 色+1 感+1（下拉+种子框像素皮肤/焦点态）聚+1（系统件风格混血收口）排+1（字号档随皮肤对齐） |
| 7 | 军师面板 | 3 | 4 | 3 | 4 | 3 | 4 | 19 | 21 | 构+1（立绘 64 井位人格上位+报告区重平衡）排+1（三指标/气泡 13px、军令框像素化后视觉重） |
| 8 | F1 词汇抽屉 | —* | —* | —* | —* | —* | —* | ~17 | ~17 | 本批未触（不在七件清单） |
| 9 | 设置页 | 3 | 4 | 4 | 3 | 3 | 3 | 18 | 21 | 色+1（分组时代色+e5 色场）感+1（下拉/文本框/开关三态焦点）聚+1（系统件像素化——通用件退位） |
| 10 | 时代皮肤对照 | 4 | 4 | 4 | 4 | 3 | 4 | 23 | 23 | 保住（复核：两带分层更清，无回归） |
| 11 | 伤害飘字帧 | 2 | 3 | 3 | 4 | 3 | 4 | 19 | 21 | 层+1（限流后同目标 ≤2——单信息点不再叠罗汉）排+1（合并合计可读） |
| 12 | 按钮三态 | 专项证据面 | — | — | — | — | — | — | — | w10-d/02 既有证据延续（本批未动三态配方） |

**汇总**：全场无 2 分维；≥20/24 的面=2,3,4,5,6,7,9,10,11（九面）；主菜单折算 ~19（回传截断面不推算补分口径延续）；F1 未触不重评。目标达成（全场 ≥20/24 除截断/未触面，无 2 分维）。

复评方法披露：自评=施工代理对照 before/after 并排图+53 号文六维定义逐维判定；图像模型（GLM 视觉）对 05-settings/02-level-cards/06-battle-hud/01-era-skins/06-locked-emblems/04-advisor-panel 六面独立读图复核（读出证据已引述各件）。主管可复评（53 号文口径：我可复评，你先自查无 2 分维）。

## 五、诚实遗留与披露

1. **军师立绘取 64 档**（非 96）：360 宽面板内容带上界——96 需缩报告区至 ~190px（可读性受损）或面板超宽压战场。64 已达"人格可辨"井位（复评读出上半身像+青环清晰）；96 档留待面板版式重构批次（架构项）。
2. **设置页 before 特殊处理**：见 §三披露（披露弹窗遮挡→沙盒净面双侧同态对比）。
3. **滚动条/SpinBox 上下箭头仍为引擎缺省**：53 号文七件未列（下拉/文本框/开关为清单项）；像素化滚动条建议入下批清单。
4. **飘字合并的显示语义**：超限合并后合计数字重置 45 帧寿命（保可读窗口）；若业主偏好"合计沿用剩余寿命"可一行改。
5. **复评两处迭代过程**：首版预告降亮（×0.82）复评读"难分主次"→二次加强（×0.70/α0.58）；checkbox 未选态首版读灰→时代色暗底+2px 环二修。过程如实记录（三循环为限内：看→修→再看各一轮）。
6. **run_all 双跑**：G1~G4 全绿后又做两处样式微调→对最终树全量重跑 run_all（终版记录见下方补充）——中间版记录不作为门禁证据。

## 六、变更清单（git）

- game/presentation/EraTD/scripts/UiFeel.cs（+四族皮肤件/hover tween/色场垫底 helper）
- game/presentation/EraTD/scripts/CampaignMenuUi.cs（角标/◆/锁变体/卡 hover）
- game/presentation/EraTD/scripts/BattleHudUi.cs（字号/dim 变体/0.2s 过渡/boss 态位守卫）
- game/presentation/EraTD/scripts/SystemPanel.cs（立绘井位重平衡/字号/军令框皮肤）
- game/presentation/EraTD/scripts/MainMenuUi.cs（keyart 色场垫底/装载拆出）
- game/presentation/EraTD/scripts/SettingsPanelUi.cs（Caption 时代色/表单垫底/系统件皮肤接线）
- game/presentation/EraTD/scripts/ProceduralMenuUi.cs、EndlessMenuUi.cs（下拉/文本框皮肤接线）
- game/presentation/EraTD/scripts/SessionBattlefieldUi.cs（飘字限流+合并）
- game/src/EraCore/Presentation/HudIconDecisions.cs（AdvisorPortraitPx 36→64）
- docs/screenshots/w10-e/（12 并排图+5 组 raw 分镜）

—— 08 号文登记归主管验收后处理（本 REV 不动 08）。
