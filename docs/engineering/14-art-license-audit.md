# 14 美术管线与许可证审计（T-031）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **生效**（T-031 交付：管线规格成文+许可证台账建立+六概念图登记；素材**入库时**的逐包复验由 T-045/T-046 校验器把关，本表为台账权威源） |
| 任务卡 | T-031（美术管线规格+CC0 审计启动+概念图核验；E 泳道，组件 C-28；覆盖 F-128/F-129/F-130/F-134） |
| 依据 | 方案 §13（13.1~13.5）/附录 D/附录 F.6；《02-consistency-audit》N10 裁定；《07-decided-plan》Q8（首发=E1+E2）；《13-quality-gates.md》G2 |
| 核验方法 | 下表"核验"列全部来自 **2026-10-13 真实网页访问**（Kenney 官网资产页、itch.io 逐包页面、CraftPix 官网许可证页）；无法在线确认的标 **禁用待核**，不作有利于使用的推定 |

---

## 1. 首发 E1+E2 素材来源表（许可证台账）

定位约束（方案 §13.2/§16）：全免费自玩 → 只用 **CC0/Public Domain 或明确允许商用的免费条款**；
**禁止转售素材本身**；任何 ShareAlike/传染性条款（CC-BY-SA/GPL）**慎用至禁用**。

### 1.1 已核验（可入口径）

| # | 素材包 | 作者/渠道 | 链接（2026-10-13 实访） | 许可证核验结论 | 用途（E1+E2） | 状态 |
|---|---|---|---|---|---|---|
| A1 | **Tiny Dungeon** | Kenney（kenney.nl） | https://kenney.nl/assets/tiny-dungeon | 页面明示 **Creative Commons CC0**；免费下载 | E1/E2 地形基底。**规格警示：原包 tile=16×16**，进 32px 网格须整数 2× 放大（Nearest，无重采样变形、不改变许可证义务） | 可用 |
| A2 | **UI Pack: Sci-Fi** | Kenney（kenney.nl） | https://kenney.nl/assets/ui-pack-sci-fi | 页面明示 **Creative Commons CC0**；免费（130 文件） | 系统面板骨架重描底稿（成品须按 §13.4 配方自绘，不直接成品入 UI） | 可用 |
| A3 | **Spire - Tower Pack 1**（2 塔×3 级，含 .ase 源） | Foozle（foozlecc.itch.io） | https://foozlecc.itch.io/spire-tower-pack-1 | 页面 License 栏明示 **Creative Commons Zero (CC0)**："free to use and modify for all projects, including commercial projects"，署名不要求 | E2 塔组参考/混搭候选；入库仍过 §3 校验器 | 可用 |
| A4 | **Spire - Enemy Pack 1**（4 飞行敌，含 .ase 源） | Foozle（foozlecc.itch.io） | https://foozlecc.itch.io/spire-enemy-pack-1 | 页面 License 栏明示 **Creative Commons Zero (CC0)**（同上引文） | E2 敌种候选 | 可用 |
| A5 | **Spire 系列其余各包**（Tower Pack 2/3/4、Enemy Pack 2、Tileset 1、Builder Pack） | Foozle | https://foozlecc.itch.io （系列入口） | 系列页声明 "public domain license"，商用可、署名不要求；**系列页抽样 2 包（A3/A4）逐页核验一致**，其余各包入库时逐包复核页面 License 栏 | E2 补充 | 有条件可用（入库逐包复验） |
| A6 | **CraftPix 免费素材**（Free Game Assets @ itch；TD 相关免费包如 Free Monster Enemy Sprites for Tower Defense、Free Pixel Citizens for Top-Down TD、Free Enemy Pixel Pack for Top-Down Game） | CraftPix（free-game-assets.itch.io） | https://free-game-assets.itch.io ；示例包 https://free-game-assets.itch.io/free-monster-enemy-sprites-for-tower-defense ；许可证正文 https://craftpix.net/file-licenses/ | itch 包页**无许可证字段**（页面仅宣传链接）；许可证正文在 craftpix.net：**Freebie License——任意数量个人/商业项目可用、可随游戏分发、修改可、署名不要求；禁止转售/再分发源文件（PNG/AI/EPS 等）或轻微改版；禁止用于训练/微调/测试 AI** | E2 补充候选。**入库时必须归档许可证快照**（条款在官网不在包内），且注意其禁止 AI 训练条款对本库 AI 生图治理无冲突（本库不拿素材训练模型） | 有条件可用（入库归档许可证快照+逐包复验） |

### 1.2 禁用待核（在核验完成前不得入库）

| # | 素材 | 原因 |
|---|---|---|
| B1 | OpenGameArt 塔防包（方案 §13.2 提及） | **逐包未核验**。OGA 托管包许可证谱系宽（CC0/CC-BY/CC-BY-SA/GPL 并存），BY-SA/GPL 有传染性与"全免费发布"定位冲突——逐包打开许可证字段核验前一律禁用待核 |
| B2 | itch 其他作者免费 TD 包（tag-tower-defense/free 共 151+ 项：Skyel、Ilustra Mundo、Artyom Zagorskiy、CreativeKind 等） | 未核验；itch 免费包默认无统一许可证，逐包页面 License 栏未确认前禁用待核 |
| B3 | CraftPix **付费** TD 包（$1.40 档） | 违背全免费成本定位（Q8 裁定范围外），不采 |
| B4 | Kenney Audio / Freesound（音频线，T-092） | 属音频台账（F-139），本表不裁定；届时按同法逐包核验 |

> 台账纪律：上表每行在**素材实际入库时**（T-045/T-078/T-094）须 ①重访链接复验许可证仍在
> 及条款未变；②把许可证文本快照归档 `docs/assets/licenses/<包名>.txt`；③过 T-046
> 校验器（尺寸/帧数/调色板）后才进 `presentation/`。任何一项不满足 → 退回禁用待核。

## 2. AI 概念图治理（重申：仅参考，不入口径）

依据方案 §13.5/附录 D/附录 F.6 与 02-N10 裁定（F.6 为陈述式实存清单）：

| 文件（docs/assets/） | 登记用途 |
|---|---|
| concept-E1-watchtower.png | E1 远古·望楼塔氛围/色板参考 |
| concept-E2-citadel.png | E2 古典·城堡要塞参考 |
| concept-E4-modern.png | E4 现代（远期章）参考 |
| concept-E5-future.png | E5 未来（远期章）参考 |
| concept-system-panel.png | 系统面板（§13.4 配方）构图参考 |
| concept-keyart-eras.png | 六时代一墙贯通 key art 参考 |

三条红线（不因本表存在而松动）：

1. **AI 生成 PNG 一律不进 `presentation/` 资产目录**——帧漂移/网格不对齐/超色板三害（方案 §13.2），仅存 `docs/assets/` 作重绘底稿；
2. **不拿 A 表素材训练/微调任何模型**（CraftPix Freebie 明令禁止；亦违背 F-129 治理定位）；
3. 商店物料若用 AI 图必须"由 AI 生成"标注（F-147，方案 §16.3），概念图转 key art 除外需先过人工重绘。

## 3. 像素规格落地参数（引用方案 §13.1，入库校验依据）

| 参数 | 值 | 校验器落点（T-046） |
|---|---|---|
| Tile | **32×32** | 网格对齐逐像素断言（16×16 源包须整数 2× 后入库） |
| Viewport | **640×360（20×11 格）**，Stretch `canvas_items`+Aspect `keep`+Scale Mode `integer`，全局 Nearest | 骨架恒定项（T-042 集成断言） |
| 画布尺寸 | 小塔 32×40（含出血）；标准塔（2×2）64×72~80；**终章 Boss 96×112**；敌人 32×32（实体 ~24×28）；常规 Boss 64~96 | 尺寸白名单校验 |
| 帧数惯例 | 敌人=移动 6 帧×2 向+死亡 6 帧≈18 帧；塔=塔基静态+塔顶动件（待机 3+攻击 4≈7 帧）；传送带=直线 UV 滚动（shader）+转弯 8 帧双层 tile 动画 | 帧数/sheet 结构校验 |
| 调色板 | master ≤64 色；每时代 8~12 色子板+共享阴影色 | ≤64 色计数校验（金样色板哈希） |

## 4. 自绘优先级清单（方案 §13.2"自绘重点"展开；F-129）

免费素材混搭覆盖面有限，以下四类**必须自绘**（AI 出概念图→人工 32px 网格重绘），按首发（E1+E2）依赖排序：

| 优先级 | 类目 | 理由与规格锚点 |
|---|---|---|
| P1 | **塔顶动件** | 每塔待机 3+攻击 4≈7 帧的动件是"塔有生命感"的核心，且免费包几乎不提供与我方塔基规格（32×40/64×72~80）匹配的可拆分动件；E1 六类塔（T-007 定案）+E2 五塔（F.5 基准）逐塔配 |
| P2 | **系统人格头像** | 四人格（F-109）专属视觉锚点，市面无对应素材；规格走系统面板像素配方（§13.4：青系描边+扫描线一致风格） |
| P3 | **Boss** | 常规 Boss 64~96、终章 Boss 96×112 大画布+死亡演出帧，免费 TD 包普遍缺失；E1/E2 首发各 ≥1 |
| P4 | **六时代主色板** | 每代 8~12 色子板+共享阴影色，master ≤64——由 AI 概念图（concept-keyart-eras/concept-E*/-system-panel）取色后人工定稿；色板先于素材冻结，是 §3 校验器与金样色板哈希的前置 |

## 5. 与门禁/任务卡的挂接

- 素材入库=内容数据类任务 → 五级测试按 06 §1.6"美术资产行"：规格校验器（元测试）+许可证审计+≤64 色校验；G2 红线扫描对入库路径生效；
- 本表台账条目变更（新增/升格/禁用）走 code-review 双轴（G5），许可证结论变化属契约级记录（G10 同纪律）；
- 后续任务：T-045（E1 占位美术+CC0 地形接入）、T-046（资产导入校验器）、T-078/T-094（E2 包与冻结）逐批回填本表"入库批次"列。

## 变更记录

- v1.0（2026-10-13）：T-031 首次成文。素材来源表 6 行已核验（Kenney×2 CC0、Foozle Spire 系列 CC0 抽样 2 包+系列页、CraftPix Freebie License 条款链核验）、4 行禁用待核；AI 概念图六图登记与三红线重申；像素规格/自绘优先级清单落定；入库复验与校验器挂接条款成文。
