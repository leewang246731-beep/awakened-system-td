# 15 T-045 E1 占位美术+CC0 地形接入报告

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-10-13（v1.0 交付）/ 2026-10-15（v1.1 补状态行，W3 收官双轴审查 F5——与 24 号同款系列性补齐） |
| 状态 | **已交付**（commit f6cf568；G2.5 资产校验器门禁随 T-045 接入 run_all 并持续全绿）；E1 素材冻结收口归 T-094（Q8 范围） |
| 任务卡 | T-045（E1 占位美术+CC0 地形接入；Wave 1，E 泳道，组件 C-28；覆盖 F-013/F-126；依赖 T-031） |
| 验收标准 | Kenney 地形入库过校验器（08-task-plan T-045 行） |
| 依据 | 14-art-license-audit.md §1.1 A1/§3/台账纪律；07-decided-plan Q8；CODE_STANDARDS CS-4.2（注释残留标记）、CS-5.1（res:// 寻址） |
| 交付物 | game/assets/kenney-tiny-dungeon/（入库资产库）；presentation/EraTD/textures/tiles/（8 tile）+ shaders/terrain-atlas.gdshader + scripts/TerrainAtlas.cs；MultiMeshRenderer/Main 贴图接入；run_all.sh G2.5 门禁；本报告 |

---

## 1. 来源与许可证复验（14 号台账 A1 行入库批次回填）

| 项 | 内容 |
|---|---|
| 素材包 | Kenney **Tiny Dungeon** v1.0（2022-05-07 发布，132 tile，tile=16×16） |
| 来源 URL | https://kenney.nl/assets/tiny-dungeon |
| zip 直链（实取） | `https://kenney.nl/media/pages/assets/tiny-dungeon/f8422efb44-1674742415/kenney_tiny-dungeon.zip`（页内"Consider a donation"弹窗的"Continue without donating..."锚点；98,530 字节） |
| 许可证复验 | 2026-10-13 MCP webReader 实访资产页：License 字段=**Creative Commons CC0**（链至 creativecommons.org/publicdomain/zero/1.0），免费下载；包内 `License.txt` 同条款（"free to use in personal, educational and commercial projects"，署名非强制） |
| 许可证快照 | `docs/assets/licenses/kenney-tiny-dungeon.txt`（14 §1.2 台账纪律第②条） |
| 原包留档 | `game/assets/kenney-tiny-dungeon/kenney-tiny-dungeon.zip`（byte-exact 原始 zip，98,530 B；CC0 允许再分发） |
| 2× 放大口径 | 源 tile 保持 16×16 入库，32px 世界格由渲染端整数 2× 放大（shader `filter_nearest`，无重采样、无 mipmap），符合 14 §1.1 A1"整数 2× 放大（Nearest，不改变许可证义务）"警示 |

## 2. 入库清单与校验结果（asset_validator，T-046 交付物）

### 2.1 game/assets/kenney-tiny-dungeon/（资产库根）

| 内容 | 说明 |
|---|---|
| `Tiles/tile-0000.png … tile-0131.png` | **132 个 16×16 tile 全部通过**：SIZE-ALLOWLIST（16∈{16,32,48,64}）✓；PALETTE-COUNT 实测 1~12 色（上限 64）✓；FILENAME-KEBAB ✓。**无 --allow-oversized-palette 特批** |
| `kenney-tiny-dungeon.zip` | 原始包留档（校验器只扫 .png，不判 zip） |
| `License.txt` / `Tilesheet.txt` / `Visit Kenney.url` / `Visit Patreon.url` | 包内随附文档（非 PNG，校验器忽略） |
| 文件名规范化 | 原包 `tile_0000.png`（下划线）→ `tile-0000.png`（kebab）：**133 个文件机械重命名，像素内容零改动**（zip CRC 校验解压 + 校验器逐文件 PNG CRC/IDAT 解码复核） |

REJECT 处置记录（任务步骤 2 要求逐个记录）：

| 文件 | 判定 | 处置 |
|---|---|---|
| `Tilemap/tilemap.png`（203×186）、`Tilemap/tilemap_packed.png`（192×176） | SIZE-ALLOWLIST REJECT（非清单尺寸；203/186 亦非 16 倍数） | **不入库树**——仅存于原始 zip；sheet 规格接入待后续任务（T-078 及 FRAME-STRIP 门禁演进） |
| `Preview.png` / `Sample.png`（918×515） | SIZE-ALLOWLIST REJECT | 同上（官网同图在线可见，见 §4 MCP 核验） |
| `Tiled/sampleMap.tmx` / `sampleSheet.tsx` | 非 PNG（校验器忽略）；引用已处置的 tilemap | 不入库树，随 zip 留档 |

汇总：`summary: files=132 rejected=0 warned=0 allowed_sizes=[16,32,48,64] max_palette=64`，exit_code=0。

### 2.2 presentation/EraTD/textures/tiles/（E1 地形子集，8 tile）

从 132 tile 中按 MCP 视觉核验挑选（挑选过程见 §4；Kenney 原编号→入库名）：

| 入库名 | Kenney 编号 | 内容 | 颜色数 |
|---|---|---|---|
| `floor-dirt.png` | tile-0048 | 平坦泥地 | 1 |
| `floor-dirt-pebbled.png` | tile-0049 | 泥地+碎石斑点 | 3 |
| `floor-dirt-mottled.png` | tile-0050 | 泥地斑驳变体 | 2 |
| `floor-rubble.png` | tile-0024 | 碎石/残骸地面 | 5 |
| `wall-brick.png` | tile-0057 | 砖墙立面 | 4 |
| `wall-brick-alt.png` | tile-0058 | 砖墙变体 | 4 |
| `wall-top.png` | tile-0026 | 墙顶与地面衔接 | 7 |
| `water-pool.png` | tile-0032 | 发光绿水池（水系占位映射） | 8 |

8/8 通过校验（16×16、≤8 色、kebab-case）。包内无草地 tile（地牢题材），草→苔藓/泥地系变体替代，命名如实（不虚构 grass）。

## 3. 渲染接入（presentation，最小修改）

| 文件 | 变更 |
|---|---|
| `scripts/TerrainAtlas.cs`（新） | 加载 `res://textures/tiles/` 8 tile（ResourceLoader→GetImage→RGBA8 归一），BlitRect 合成单行 128×16 ImageTexture；提供 mock 帧缓冲色（0xRRGGBB）→ atlas 槽位映射（5 绿→rubble/dirt×3/pool，未知色回退 floor-dirt）。墙槽位 4~6 已入 atlas 供 T-056 建造渲染取用 |
| `shaders/terrain-atlas.gdshader`（新） | canvas_item shader：vertex 段读 `INSTANCE_CUSTOM.x`（该 builtin 仅 vertex 可用——fragment 引用会 SHADER ERROR，已实测修正）算 atlas UV，fragment 以 `filter_nearest` 采样；16px tile 铺 32px 世界格=精确 2× 整数放大 |
| `scripts/MultiMeshRenderer.cs` | `SetupGround` 增可选参 `terrainAtlas`/`tileIndexForColor`：有 atlas→`UseCustomData=true`+ShaderMaterial（atlas/tiles_per_row 参数），脏 chunk 重建时 `SetInstanceCustomData`（槽位）+白色实例色；无 atlas→保持 T-042 纯色 quad 路径。**chunk 划分/实例槽位/脏标记逻辑零改动**（smoke BRIDGE 行计数不变：ground_chunk_rebuilds=4） |
| `scripts/Main.cs` | SetupGround 两行传参 + 冒烟新标记行 `ART atlas_tiles=8 tile_px=16 atlas=128x16 scale=2x-nearest source=kenney-tiny-dungeon-cc0`（SMOKE_OK 判据行原样） |
| 导入设置 | `*.import` 按 .gitignore 不入库；过滤由 shader `filter_nearest` 硬保证（优先级高于项目默认），叠加 project.godot 既有 `default_texture_filter=0`（全局 Nearest，T-042）；2D 默认导入 mipmap=off，atlas 经 `ImageTexture.CreateFromImage` 重建亦无 mipmap |

确定性：state_hash 接入前后一致（-8836687117718294168），贴图不影响模拟链路。

## 4. MCP 工具使用记录（甲方要求：本任务必须使用 MCP）

| # | 工具 | 对象 | 结果 |
|---|---|---|---|
| 1 | `mcp__web_reader__webReader` | https://kenney.nl/assets/tiny-dungeon | 页面结构化读取：License=CC0、tile 16×16、页内媒体基址 `media/pages/assets/tiny-dungeon/` |
| 2 | `mcp__web_reader__webReader` | `…/8c4f705db0-1674742415/` 与 `…/f962f622b4-1761142514/`（探测） | 网关 5xx（目录页不可读）→ 改由 curl 抓资产页 HTML 定位弹窗内 zip 直链（f8422efb44-1674742415） |
| 3 | `mcp__4_5v_mcp__analyze_image` | kenney.nl preview.png（918×515） | " dungeon-themed pixel art… floors, walls, doors, water/lava… grid-aligned" |
| 4 | `mcp__4_5v_mcp__analyze_image` | kenney.nl sample.png | "maze-like dungeon… tan dirt floors… brick walls… grid-aligned 16x16 pixel art" |
| 5 | `mcp__4_5v_mcp__analyze_image` | 本地 132 tile 拼贴表（CDN 化） | 12×11 网格读数：地板/墙/水/苔藓候选索引带置信警示 |
| 6 | `mcp__4_5v_mcp__analyze_image` | 4× 放大带编号拼贴表 | 精确索引：地板 048-053、墙 001/002/014/015/026/027/036-040/057-059、绿水池 032、苔藓地 012/013/024/025 |
| 7 | `mcp__4_5v_mcp__analyze_image` | 候选 16 tile 8× 放大特写 | 逐 tile 描述（48=纯泥地…32=发光绿水池），最终 8 选定 |
| - | `mcp__plugin_image-search__search_image` | — | 未使用（来源唯一=Kenney 官方，无需素材检索） |

本地文件完整性不由远程图保证，而由校验器逐文件 PNG CRC+IDAT 全解码复核（132/132 通过即像素级验证）。

## 5. run_all 门禁接入（"入库过校验器"闭环）

- `tools/ci/run_all.sh` 新增 **Step 1.5 = G2.5 资产规格校验**（本步失败=run_all 红，非 INFO 级——理由：验收语义是"过校验器"门禁而非观察项；G 编号外挂为 G2.5，13-quality-gates 表以 G1~G4 为正式门禁，G2.5 在汇总表单列一行并注明依据=14 §3）：
  `python tools/ci/asset_validator.py --json <log>/assets.json game/assets game/presentation/EraTD/textures`
- 扫描范围含 `game/assets` 资产库根——后续任何包（T-078 E2 等）入库即被门禁覆盖。
- `tools/ci/asset_validator.py` 仅微调文件头"run_all.sh 接入点"注释（原文"本版未接入——素材尚未入库"已过时；规则代码零改动）。

## 6. 验证记录

- smoke（Godot 4.7.2 headless）：`SMOKE_OK core=0.0.1-wave0 scale=1` + `ART atlas_tiles=8 …` + `BRIDGE … state_hash=-8836687117718294168`；SHADER ERROR=0（首版 fragment 引用 INSTANCE_CUSTOM 报错，已改 vertex 段后归零）。
- `bash tools/ci/run_all.sh` **全绿**：
  - G2 红线：7 checks | 0 FAIL | 0 WARN
  - **G2.5 资产规格（新接入）：files=140 rejected=0 warned=0**（132 库 tile + 8 presentation tile）
  - G1 测试：1362 passed, 0 failed
  - G3 构建：Release 零警告（TreatWarningsAsErrors=true）
  - G4 冒烟：SMOKE_OK 已捕获
- **共享工作树干扰披露**：run_all 执行时，A 泳道未提交 WIP（`game/src/EraCore/{Sim,Supply}` 与 `EraCore.Tests/{Sim,Supply,Spikes}`，26 文件）正占用同一工作树且处于中间态（4 个 CS9007 编译错 + 其 `SimulationConfig.cs:61` 注释含 'placeholder' 触发 T1——均非本任务文件）。为取得本任务门禁证据，将上述五个**未跟踪**目录暂移出仓库（sha256 快照 26 条）→ run_all 全绿 → 移回并 diff 快照**逐字节一致**（RESTORE-OK）。本任务未改动、未提交任何 Sim/Supply/Spikes 文件；该隔离操作可逆且已完整复原。
- 校验器自测 `tools/ci/test_asset_validator.py`：50 passed, 0 failed（本任务仅改 asset_validator.py 文件头注释，规则代码零改动，自测复核通过）。

## 变更记录

- v1.0（2026-10-13）：T-045 首次成文。Kenney Tiny Dungeon CC0 复验+入库（132 tile 过校验器+133 文件 kebab 规范化+sheet/preview REJECT 处置留档）；E1 8 tile 子集进 presentation；MultiMesh 地面层 atlas+shader 接入（2× nearest 整数放大，脏 chunk 逻辑不变）；run_all 增 G2.5 门禁。
