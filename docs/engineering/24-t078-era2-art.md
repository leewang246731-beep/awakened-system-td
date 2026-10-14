# 24 T-078 E2 美术包报告（CC0+程序合成占位，校验器门禁）

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-10-13（v1.0 交付）/ 2026-10-15（v1.1 补状态行，W3 收官双轴审查 F5） |
| 状态 | **已交付**（commit 2ffd036+1553e40；run_all G1~G4 全绿含 G2.5 资产门禁 files=404 rejected=0；R9 盘符路径文件级豁免已登记 allowlist——同 18~21/23 号 U 盘素材库先例）；E2 素材冻结收口归 T-094（Q8 范围） |
| 任务卡 | T-078（E2 美术包；Wave 3，E 泳道，组件 C-28；覆盖 F-014/F-042/F-132；依赖 T-045） |
| 验收标准 | E2 塔/载具/UI 素材过校验器（08-task-plan T-078 行） |
| 依据 | 14-art-license-audit §1/§3（台账纪律+像素规格）；21-secondary-dev-base §2.1（AI PNG 不进 presentation 红线）；15-t045（E1 先例）；CODE_STANDARDS CS-3/CS-4.1/CS-5.1 |
| 交付物 | game/assets/era2/（两 CC0 包入库树 239 PNG+元文件）；presentation/EraTD/textures/era2/（25 PNG）；tools/art/era2_placeholders.py（确定性合成器）；scripts/Era2TerrainAtlas.cs + Main.cs ART2 冒烟标记；许可证快照×2；本报告 |

---

## 1. 来源与许可证复验（14 号台账入库批次回填）

### 1.1 新增两包（Kenney 渠道，逐包 WebFetch 实访复验，2026-10-13）

| 包 | 页面复验 | zip 实取（downloads/ 留档，gitignore 排除） | 许可证快照 |
|---|---|---|---|
| **Kenney Tiny Town**（16×16 tile，包内实测 132） | License=**Creative Commons CC0**，免费下载 | `kenney_tiny-town.zip` 182,243 B，sha256 `9768692dccff…764be977` | `docs/assets/licenses/kenney-tiny-town.txt`（复验记录+包内 License.txt 逐字节快照） |
| **Kenney Medieval RTS**（默认尺寸精灵 64×64） | License=**Creative Commons CC0**，免费下载 | `kenney_medieval-rts.zip` 1,831,847 B，sha256 `f8217b5e8688…34764e8` | `docs/assets/licenses/kenney-medieval-rts.txt`（同上格式） |

14 号台账建议回填：§1.1 增补 Tiny Town / Medieval RTS 两行（Kenney CC0 渠道，核验方法与 A1 同源）；本报告即入库批次记录。

### 1.2 Foozle Spire Tower Pack（台账 A3，CC0）——本轮未入库原因

itch.io 免费包下载走浏览器交互（页面无静态 zip 直链，匿名 HTTP 不可达），脚本化素材管线拿不到上传件；**非许可证问题**（A3 行 CC0 结论维持）。E2 五塔占位按任务卡预批准的回退路线走程序合成（§3）。

### 1.3 OpenGameArt（台账 B1 禁用待核）——维持禁用（本轮未完成逐包核验，不采）。

## 2. 入库清单与校验结果（asset_validator，G2.5）

### 2.1 game/assets/era2/（资产库）

| 目录 | 内容 | 校验 |
|---|---|---|
| `kenney-tiny-town/Tiles/` | 132 tile（`tile_0000.png`→`tile-0000.png` 机械 kebab 重命名，像素零改动；先例=T-045） | 132/132 过（16×16，≤9 色） |
| `kenney-tiny-town/*.txt/*.url` | License.txt / Tilesheet.txt / url×3（非 PNG） | 忽略 |
| `kenney-medieval-rts/{environment,structure,unit,tile}/` | 107 精灵（`medievalEnvironment_01.png`→`medieval-environment-01.png` 等机械重命名；64×64 默认尺寸） | 107/107 过（64×64，≤64 色） |
| `kenney-medieval-rts/License.txt` | 包内许可证 | 忽略 |
| `downloads/` | 原始 zip×2（sha256 见 §1.1；.gitignore 排除不入库） | 非 PNG |

汇总：`files=239 rejected=0 warned=0`，exit 0。

**REJECT 处置记录**（不合规格内容不进树，仅存 downloads/ 原 zip）：

| 文件 | 判定 | 处置 |
|---|---|---|
| medieval-rts `PNG/@2 size/`（128×128 全部 126 张） | SIZE-ALLOWLIST REJECT（128∉{16,32,48,64}） | 不入库树 |
| medieval-rts `Spritesheet/`、`Tilesheet/`、Vector/、Thumbs.db、Preview/Sample | 大画布/非清单尺寸/非 PNG | 不入库树（先例=T-045 tilemap 处置） |
| **medieval-rts 19 个超色板精灵**：environment-{02,03,04,11,12,18,19}（渐变树 78~87 色）、structure-{13,14,15,21}、tile-{41..48}（水域渐变 78+ 色） | **PALETTE-COUNT REJECT（>64 色 master 上限）** | 不入库树（无特批；不用 --allow-oversized-palette） |
| tiny-town `Tilemap/`、Preview/Sample | SIZE-ALLOWLIST REJECT | 不入库树 |

> 注：medieval-rts 自带水域 tile（41~48）全部超 64 色被拒——E2 水域因此走程序合成（§3），
> 非偏好选择而是门禁结果。

### 2.2 presentation/EraTD/textures/era2/（E2 子集，25 PNG，全部过校验）

**tiles/（9）**——古典色板地形（F-132 地形面）：

| 入库名 | 来源 | 内容 |
|---|---|---|
| `grass.png` | Tiny Town tile-0000 | 开阔草地（Plain） |
| `grass-spotted.png` | tile-0001 | 草地花斑变体（Forest 暂代） |
| `road-stone.png` | tile-0041 | 鹅卵石路+路缘（Road） |
| `road-stone-alt.png` | tile-0096 | 切石广场（Rock 暂代） |
| `wall-stone-brick.png` | tile-0048 | 垛口石墙（Wall） |
| `wall-stone-brick-alt.png` | tile-0049 | 石墙变体 |
| `farm-plowed.png` | tile-0012 | 犁沟农田 |
| `farm-plowed-alt.png` | tile-0013 | 犁沟变体 |
| `water.png` | **程序合成** | 三蓝斜波纹水域 |

**anim/（1）**：`water-anim.png`（32×16 两帧横条；`--expect-frames 2` 门禁自测对象，帧数门禁为目录级故独立成目录）。

**towers/（10）**——五塔×两级占位（F-014；与 `game/content/era2/towers.json` 的 `era2:tower-*` 一一对应）：

| 入库名 | 画布 | 对应塔（tier） | 构成 |
|---|---|---|---|
| `tower-arrow-l1/-l2.png` | 32×32 | 箭楼（T1） | 石基+木塔身+射击平台+垛口+侧挂弓；L2 金旗/金箍 |
| `tower-crossbow-l1/-l2.png` | 32×32 | 弩塔（T2） | 宽石台+顶置弩机（铁身/木弓片）；L2 金准星 |
| `tower-repeating-crossbow-l1/-l2.png` | 48×48 | 连弩塔（T3） | 双层石塔+双联弩+木弹匣；L2 金边弹匣 |
| `tower-onager-l1/-l2.png` | 48×64 | 弩炮（T3 专精线） | 低重石台+绞盘+粗发射臂+环形索 |
| `tower-fire-oil-l1/-l2.png` | 32×48 | 火油塔（T4） | 石柱+铁支架+火油釜+火焰 |

**units/（1）**：`carriage-right.png`（32×32）——E2 运输马车（F-042：挽马+双轮栏板车+货物）。

**ui/（1）**：`panel-era2.png`（32×32）——E2 面板色板（古典金描边+石蓝底+金/蓝/石/木 4×4 色块矩阵），供 C-22 E2 换肤取色（色板底稿，非成品 UI）。

**structures/（3）**：`watchtower.png`/`gatehouse.png`/`wall-crenellated.png`（64×64，Medieval RTS CC0：medieval-structure-02/03/04 机械重命名）——走廊城墙/城门等 E2 建筑渲染的现成 CC0 件。

汇总：`files=25 rejected=0 warned=0`（尺寸 16/32/48/64 全在白名单；kebab 全合规）。

## 3. 程序合成占位（tools/art/era2_placeholders.py）

- **定位**：任务卡预批准回退路线（「CC0 找不到就用代码合成像素图」）。触发：①itch Foozle Spire 塔（A3）脚本管线不可达（§1.2）；②Medieval RTS 仅 1 座瞭望塔配不齐五塔×两级；③其水域 tile 超色板被拒（§2.1）。
- **红线对齐**：程序绘制=确定性（固定坐标+22 色固定色板，无随机数、无 AI 生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」不冲突——产物由代码逐字节复现（实测 14 文件二次生成 sha256 全等），人工重绘升级时以本脚本为底稿。
- **实现**：stdlib-only（struct/zlib 自写 PNG 编码器，与 asset_validator 同依赖纪律，不引 Pillow）；`--self-test` 生成后即调 asset_validator：towers/units/ui/tiles 常规门禁 + anim/ 以 `--expect-frames 2` 过帧门禁。实测：`self-test ok: static_files=21 rejected=0 anim_files=1 water-anim frames=2/expect-2`。
- **色板**（E2 古典子板 22 色 ≤64）：石 4/木 3/铁 3/金 3/蓝石 3/火 3/水 3；金+石蓝=面板主色（F-132「古典金/石蓝」口径）。

## 4. 渲染接入（presentation，最小改动；E1 路径零触碰）

| 文件 | 变更 |
|---|---|
| `scripts/Era2TerrainAtlas.cs`（新） | 镜像 TerrainAtlas 单行 atlas 方案：加载 `res://textures/era2/tiles/` 9 tile 合成 144×16 ImageTexture；`TerrainKind`→槽位映射（Plain→grass、Road→road-stone、Wall→wall-stone-brick、Water→water…）；`ProbePackFiles()` 探测 towers/units/ui/structures 15 文件存在性 |
| `scripts/Main.cs` | 仅追加 ART2 冒烟标记（构建 era2 atlas，缺图即抛）：`ART2 era=classical atlas_tiles=9 tile_px=16 atlas=144x16 pack_files=15/15 source=kenney-tiny-town+medieval-rts-cc0+procedural-standin`；E1 ground 层/state_hash/BRIDGE 计数零改动 |
| `.gitignore` | +2 行：`!/tools/art/`（合成器入库白名单）与 `game/assets/*/downloads/`（原始 zip 不入库） |
| 接入边界 | 塔占位/马车/UI 色板仅入库+探测，不接渲染节点——F-014 塔渲染归 T-065、F-042 载具渲染归 T-064（美术包先行，各域消费）；era 地面层路由随 E2 关卡工作落地 |

## 5. 验证记录

**门禁执行方式披露**：共享工作树上有并行泳道（T-085）未提交 WIP（`game/src/EraCore.Tests/Levels/`、`game/content/levels/`）处于编译中间态，全量门禁无法在共享树直接跑。按 T-045 先例（15 号 §6）采取隔离执行：**git worktree（检出 c352965=T-079 提交后 HEAD）+ T-078 全部文件拷入**，在隔离树跑完整门禁；期间对 T-085 文件做 sha256 快照迁移+复原（byte-exact，RESTORE-OK），未改动其任何内容。

隔离树门禁结果（T-078 文件全量在场）：

- `asset_validator game/assets/era2`：**files=239 rejected=0 warned=0**。
- `asset_validator presentation/EraTD/textures/era2`：**files=25 rejected=0 warned=0**；`--expect-frames 2`（anim/）：files=1 rejected=0。
- **run_all（G2/G2.5/G3/G4）**：
  - G2 红线：9 checks | 0 FAIL（25 WARN 照放行）✓
  - **G2.5：files=404 rejected=0 warned=0**（E1 132+8；era2 239+25）✓
  - G3 构建：Release **0 警告 0 错误**（TreatWarningsAsErrors=true，含 Era2TerrainAtlas.cs/Main.cs 编译）✓
  - G4 冒烟：SMOKE_OK 已捕获 + **ART2 行**（era2 atlas 9 tile 装载 + pack_files=15/15）✓
- **G1 测试：4433/4440 passed，7 失败均为基线既有**（与 T-078 无关，已用干净基线复现取证）：
  - 6×Llm 在线/SSE 协议测试（MiMo live probe×2、SSE frames、LlmConnectionProbe×2、Openai SSE chunk boundary）——干净 c352965 上同样失败（live 网络端点环境依赖；测试日志「服务端返回了无法解析的响应」）；
  - 1×Spike1PerformanceTests.MazeComposite300Enemies1000ProjectilesLogicBudget——干净基线同样失败（性能预算测试，沙箱时钟敏感）。
  - 取证方法：隔离树移除 T-078 全部文件后按同名过滤器复跑 → 同样 7 失败；加回 T-078 文件 → 失败集完全一致（无新增失败）。
- 视觉核验：Tiny Town 132 tile 拼贴表+精选 13 tile 特写、Medieval RTS structure/tile 拼贴表逐格读图选定（草地/犁沟/鹅卵石路/垛墙/切石/瞭望塔/城门）；程序合成 8 件放大预览目检（基座+武备色块可辨识）。
- 校验器零改动（T-046 交付物原样，仅消费其 CLI/库接口）。

## 6. 与 E 盘素材库的关系（21 号 §2）

E:\game-assets 既有 4 张 E2 向资产（era2-banner-citadel 等）属**演出素材/底稿**
（banner=AI 直用允许域），走 T-146/T-147 manifest 管线，不属本包（战斗内像素
资产）范围；本包全部为 validator 门禁内 presentation 资产。两线在 F-132 冻结
（T-094）时合并对账。

## 变更记录

- v1.0（2026-10-13）：T-078 首次成文。Kenney Tiny Town+Medieval RTS CC0 复验
入库（239 文件过校验器+机械 kebab 重命名+19 超色板件与 @2/sheet REJECT 处置
留档）；presentation era2 子集 25 PNG（CC0 11+程序合成 14）；五塔×两级/马车/
UI 色板/水域程序合成器入库 tools/art；Era2TerrainAtlas+ART2 冒烟标记接入；
隔离树门禁 G2/G2.5/G3/G4 全过、G1 基线既有 7 失败取证归档。
