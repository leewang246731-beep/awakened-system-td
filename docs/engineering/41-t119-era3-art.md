# 41 · T-119 E3 美术包（含传送带双层动画）

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-10-13 |
| 状态 | **已交付**（E3 全量程序合成+传送带双层动画素材就位；本表=28 号冻结清单的 E3 续表——28 号 §1 冻结面 v0.1=E1+E2 不变，E3 随本表登记） |
| 任务卡 | T-119（E3 美术包（含传送带双层动画）；Wave 6，E 泳道，组件 C-28；覆盖 F-042/F-127/F-133；依赖 T-094✓） |
| 验收标准 | UV 滚动 shader+转弯 8 帧双层正确渲染（08-task-plan T-119 行） |
| 依据 | 28-t094-art-freeze（冻结清单先例+程序合成回退路线+Boss 画布裁定）；31-visual-enrichment §1.2 E3 视觉身份卡（铆铁赭红/煤黑/蒸汽灰白+桁架铁塔+桁架三角×齿轮母题）；14-art-license-audit §3（帧数惯例行：传送带=直线 UV 滚动 shader+转弯 8 帧双层 tile 动画）；01-feature-inventory F-127/F-133/F-042；33-video-A-tier A-03（E2→E3 跃迁成片=帧参考）；CODE_STANDARDS CS-4.1/CS-5.1 |
| 交付物 | tools/art/era3_placeholders.py（新，E3 合成器）；presentation/EraTD/textures/era3/** 31 PNG（§2）；shaders/belt-scroll.gdshader（新，F-127 直线段双层滚动）；game/src/EraCore/Presentation/BeltRenderDecisions.cs（新，headless 渲染决策层）+presentation 侧 Era3TerrainAtlas.cs/Era3ArtPack.cs 接线+Main.cs ART3/BELT 冒烟标记；测试 BeltRenderDecisionsTests/Era3ArtMappingTests（47 项）；本映射清单 |

---

## 1. 范围与口径

- **范围**=E3（工业）全部战斗内像素资产：三塔×升级位、九敌+堡垒号 Boss、矿轨载具（E3 carrier）、E4 前置载具卡车与传送带双层动画（era3/era.json `_ladder_note`：15/s 档传送带/卡车=E4 物流，T-119 预制为 E4 档位前置资产）、E3 地形子集（工业地砖/铁轨/烟囱装饰）、UI 色板。实体清单唯一真相源=`game/content/era3/{towers,enemies,era}.json`（T-112/T-116 冻结，只读）。
- **来源**=**100% 自研程序合成**（tools/art/era3_placeholders.py，代码生成像素=仓库自身 MIT 代码产物）。E3 无 CC0 素材源入库（Kenney 三包均为前代主题；28 号 §6 待做 #8 预告的 E3 包走 T-078 预批准回退路线）。红线对齐：程序绘制=确定性（固定坐标+固定 18 色工业色板、无随机数、无 AI 生图），实测二次生成 **sha256 逐字节全等 31/31**（`--self-test` 自动复验）。
- **验收闭环**=G2.5 资产门禁（run_all Step 1.5 全目录扫描）+ 传送带 8 帧循环的逐像素断言（§4）+ headless 渲染决策层测试（§3）+ 本清单 sha256 登记（§5）。

## 2. E3 实体→素材映射全表（31 件）

"来源"缩写：**P3**=程序合成 era3_placeholders.py。画布单位 px；色数=validator 实测 distinct RGBA（含透明）。

### 2.1 塔（era3/towers.json：tier/max_tier 驱动升级位文件名）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era3:tower-gatling L1/L2（tier1/max2） | 加特林 | towers/tower-gatling-l1.png / -l2.png | 32×32 | 15/16 | P3 |
| era3:tower-mortar L1/L2（tier2/max2） | 臼炮 | towers/tower-mortar-l1.png / -l2.png | 48×48 | 15/15 | P3 |
| era3:tower-ironclad-wall L1（max1 单档） | 铁甲墙 | towers/tower-ironclad-wall-l1.png | 32×32 | 8 | P3 |

### 2.2 敌+Boss（era3/enemies.json 全行）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era3:enemy-conscript | 征召兵 | enemies/enemy-conscript.png | 32×32 | 8 | P3 |
| era3:enemy-line-infantry | 线列兵 | enemies/enemy-line-infantry.png | 32×32 | 9 | P3 |
| era3:enemy-ram | 蒸汽冲车 | enemies/enemy-ram.png | 32×32 | 13 | P3 |
| era3:enemy-scout-rider | 铁骑斥候 | enemies/enemy-scout-rider.png | 32×32 | 11 | P3 |
| era3:enemy-gunner | 排枪手 | enemies/enemy-gunner.png | 32×32 | 8 | P3 |
| era3:enemy-ironclad-guard | 铁甲卫兵 | enemies/enemy-ironclad-guard.png | 32×32 | 11 | P3 |
| era3:enemy-sapper | 爆破工兵 | enemies/enemy-sapper.png | 32×32 | 12 | P3 |
| era3:enemy-overseer | 工头军士 | enemies/enemy-overseer.png | 32×32 | 9 | P3 |
| era3:enemy-boss（era.json boss 堡垒号） | 堡垒号 | enemies/boss-bastion.png | 64×64 | 15 | P3 |

**Boss 画布裁定**（28 号 §5 先例重演）：内容表 `canvas_size_px=96`，96 不在校验器 SIZE-ALLOWLIST {16,32,48,64}——本轮取 **64×64**（14 §3「常规 Boss 64~96」区间下限，合规，不扩清单）；升 96（及终章 Boss 96×112）仍须随 SIZE-ALLOWLIST 扩展走 REV（28 号 §6 待做 #2 维持挂账）。

### 2.3 载具/UI/地形/装饰/传送带

| 实体/用途 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|
| E3 载具 carrier=mine_rail（era.json logistics） | units/mine-rail-right.png | 32×32 | 9 | P3 |
| E4 前置载具卡车（F-042 第 3 档；T-123 数据位） | units/truck-right.png | 32×32 | 11 | P3 |
| E3 面板色板（C-22 换肤底稿） | ui/panel-era3.png | 32×32 | 16 | P3 |
| Era3TerrainAtlas 槽位 0~8（Era3TerrainAtlas.cs TileFiles） | tiles/floor-steel、floor-steel-grated、rail-iron、rail-iron-alt、wall-riveted、wall-riveted-alt、floor-slag、floor-slag-alt、water-industrial | 16×16 | 4~6 | P3 |
| 烟囱装饰（E3 地形子集·装饰件） | structures/chimney.png | 32×64 | 13 | P3 |
| 传送带直段·动画层（shader 采样） | belt/belt-body.png | 16×16 | 5 | P3 |
| 传送带直段·静态层（带边钢轨） | belt/belt-edge.png | 16×16 | 4 | P3 |
| 传送带弯口·静态层（弯口边框） | belt/belt-turn-edge.png | 16×16 | 4 | P3 |
| 传送带转弯 8 帧（帧门禁对象，§4） | anim/belt-turn-anim.png | 64×32 | 5 | P3 |

**盘点结论**：E3 内容实体映射 **18 项**（塔 3 种×升级位=5 张、敌 9 张、Boss 1、载具 1）+地形 9+UI 1+装饰 1 全部有素材过校验器——E3"无实体无素材"清零；另交付 E4 前置资产 3 件（卡车+传送带双层全套归 §4 口径）。

## 3. 传送带双层动画（F-127 验收核心）

规格锚点=14 号 §3 帧数惯例行「传送带=直线 UV 滚动（shader）+转弯 8 帧双层 tile 动画」+01 号 F-127「直线段用 shader 滚动不耗帧缓存；转弯 8 帧两层正确叠加渲染」。**双层语义**（两段共用）：动画层=带体表面（煤黑胎面×2 档+黄铜防滑条，PAT16 周期 16px）；静态层=铆接钢轨边框（不随动画爬行）。

### 3.1 直线段=UV 滚动 shader（shaders/belt-scroll.gdshader）

- 双层单 draw：fragment 以 over-composite 合成**动画带体**（`body` 纹理 V 向 `−scroll_px/16` 采样偏移，`repeat_enable` 无缝包裹 16px 周期——滚动零帧缓存）与**静态带边**（`edge` 纹理原 UV 采样）。
- 朝向：每实例 `INSTANCE_CUSTOM.y` 携带顺时针四分之一转数（terrain-atlas 先例），canonical=南向行进（+V），旋转作用于整叠双层。
- 参数由决策层唯一供给：`scroll_px` ← `BeltRenderDecisions.ShaderOffsetPx`。

### 3.2 转弯段=8 帧 tile 动画（anim/belt-turn-anim.png）

- **存储**=4×2 网格 64×32（8 帧×16px；单行 8 帧横条=128px 宽**超 SIZE-ALLOWLIST** {16,32,48,64}——冻结校验器不扩清单（28 号白名单零滥用纪律），取双行网格；validator 自动帧探测报 frames=2 属横条启发式 INFO 注记，帧数契约由合成器 `--self-test` 与 C# Era3ArtMappingTests 双侧断言 8）。
- **几何/u 律**（与合成器、C# 测试三方镜像）：规范右弯=南入北行东出；带体=竖臂(x∈[5,10],y∈[11,15])∪弯心(x,y∈[5,10])∪横臂(x∈[11,15],y∈[5,10])，路径坐标 u=15−y / 5+(x−5)+(10−y) / 15+(x−10)，全程 u∈[0,20]→**转弯步长 21px**；第 k 帧带体色=PAT16[(u−2k) mod 16]（每帧前进 2px）。
- **帧边界对齐断言**（验收「8 帧循环无缝」的机器证）：①每帧逐像素符合 u 律；②双臂逐像素平移——竖臂帧 k+1(x,y)=帧 k(x,y+2)、横臂帧 k+1(x,y)=帧 k(x−2,y)（帧边界的直读形式）；③循环闭合——8 帧×2px=16px=PAT16 周期，帧 8 逐像素回到帧 0；④八帧两两不同（循环非退化）；⑤静态边框与带体不透明集**零重叠**（两层正确叠加，互不遮挡）。
- 左弯=规范右弯水平镜像（决策层 FlipX），四向=旋转（决策层 SpriteRotationQuarters）——8 帧覆盖直/左弯/右弯×方向。

### 3.3 headless 渲染决策层（game/src/EraCore/Presentation/BeltRenderDecisions.cs）

T-102 决策表先例（OffModeDowngrade/RendererDecisionMap：**决策全在纯逻辑层，presentation 只镜像不重推导**，headless 可全测）+传送带类型输入（ConveyorBelt.Cells 链→逐格计划）：

- `PlanBelt(cells)`→逐格 `BeltCellPlan`：Kind（Straight/TurnLeft/TurnRight/Reversal，叉积定向；180° 折返显式上报不静默伪装）、Entry/Exit 行进向、**OriginPx**（图案锁相原点：路径长累积 mod 16——直段 16≡0、弯 21≡5，跨直/弯边界图案连续）、旋转四分之一转、FlipX。
- **同一相位量驱动两层**：φ=(scroll−origin) mod 16——直线 shader 偏移=φ（直用）；转弯帧号=floor(φ/2)。shader 表面与 8 帧 tile 共用一时钟一原点，跨段锁相（BeltRenderDecisionsTests.Frame_and_shader_share_one_phase_quantity 断言）。
- 接线：presentation Era3ArtPack.BuildBeltScrollMaterial（材质+双纹理+参数）/TurnFrameRect（网格帧定位）；Main.cs 冒烟标记 ART3/BELT 证明装载。

## 4. 传送带几何冻结契约（三方镜像）

| 常量 | 值 | 镜像处 |
|---|---|---|
| 带体 tile | 16px（32px 格 2× nearest 整数放大） | BeltRenderDecisions.BodyTilePx ↔ 合成器 BODY_TILE_PX ↔ Era3ArtPack.BeltBodyPx |
| 图案周期 | 16px | PatternPeriodPx ↔ PAT16 长度 |
| 直段步长 | 16px/格（=周期，直链 origin 恒 0） | StraightPathPx ↔ 合成器 STRAIGHT_PATH_PX |
| 转弯步长 | 21px（=周期+5，弯后下游相位 +5） | TurnPathPx ↔ 合成器 TURN_PATH_PX/u∈[0,20] |
| 帧数×步进 | 8 帧×2px（=16px=周期→循环闭合） | TurnFrameCount/TurnPxPerFrame ↔ TURN_FRAMES/PX_PER_FRAME |

变更任一值须同步三处+REV（帧动画几何=冻结产物契约，同 28 号 §6 冻结声明纪律）。

## 5. E3 清单 sha256（31 PNG，路径根=presentation/EraTD/textures/era3/）

| # | 文件 | 画布 | 色数 | sha256 |
|---|---|---|---|---|
| 1 | towers/tower-gatling-l1.png | 32×32 | 15 | dd727f33164ed12c6f2f0c50b81765bf7eef538ddce80cb8514d19f7ecb71e92 |
| 2 | towers/tower-gatling-l2.png | 32×32 | 16 | 132ce79bc074744ade8ff9404d5ce395d91bde2239abffd0ffe665952f369fe5 |
| 3 | towers/tower-mortar-l1.png | 48×48 | 15 | 1223e64f33680ce82030d55fa56b7c84a20dfc3f9ed924f2a36d9eb6e40354f5 |
| 4 | towers/tower-mortar-l2.png | 48×48 | 15 | 36e031e95d990cc8c0f3bb22eea7587f6ae880eed9562ccb611c8e29bacd517a |
| 5 | towers/tower-ironclad-wall-l1.png | 32×32 | 8 | 237e8abe02fc4fccc155039b35072e83f9ee351ac3201f6234095ab8631f7a85 |
| 6 | enemies/enemy-conscript.png | 32×32 | 8 | e26c4c259dd894a2013333d5870d9846e6fdbe1f375cca56c5c31fd579beadc2 |
| 7 | enemies/enemy-line-infantry.png | 32×32 | 9 | 06456a64d445a8d0e2bd2f83f9fe88181361291b626c0920397c9b62ffee9027 |
| 8 | enemies/enemy-ram.png | 32×32 | 13 | 83e78deb8446eb9c9af9b4dd0a2a4b287a27f44aa67a5c1595d39d9fa4d1e9df |
| 9 | enemies/enemy-scout-rider.png | 32×32 | 11 | a6b3e7544487776b58a5f1e18c455af03c8e87b93c6f400b7cd2c9ec9d41d4ba |
| 10 | enemies/enemy-gunner.png | 32×32 | 8 | 4b32e93e2eb869a38b5680f8319a2a40a0df73d539befdc8dce401ce05e5d6ab |
| 11 | enemies/enemy-ironclad-guard.png | 32×32 | 11 | 55f2900932e9dec679f7df984be15aa909723cf61898101b30e8773efda28a72 |
| 12 | enemies/enemy-sapper.png | 32×32 | 12 | ee614d7c54337016e18ba884d84695fe27a03962093dedd9cd5df795a6c24361 |
| 13 | enemies/enemy-overseer.png | 32×32 | 9 | c7478fbaad386d36b1a193c2f4bc253cd633abba7bf742b7f9fa28f945f65022 |
| 14 | enemies/boss-bastion.png | 64×64 | 15 | 875eda75f24dde62b317ba144e7bbd20d4979a4167f2e0987d2950a95f6018a3 |
| 15 | units/mine-rail-right.png | 32×32 | 9 | 5c110b265f6bf0cb9a413109292d708781883083eb6a35ef1f88827e0541f29a |
| 16 | units/truck-right.png | 32×32 | 11 | 1b2598c49587ded49c64fd23bbf017b5cfd1e94f711c718eedacb7881896d8e1 |
| 17 | ui/panel-era3.png | 32×32 | 16 | f1d80c55e336926ae28a1c6d8c674d54be6fa1cb5263a8a2643f0ee0ac53d5fe |
| 18 | tiles/floor-steel.png | 16×16 | 4 | b176ff653c16160cbb066a95eee4a38777d9a8d53d605015800321a2b5462a50 |
| 19 | tiles/floor-steel-grated.png | 16×16 | 4 | 0ff90aa3bca3707f410b66d77ee97f7572fc2377091cac3622999ba5f1132ebf |
| 20 | tiles/rail-iron.png | 16×16 | 4 | 2d9697492e18c402a89787fcaadbc8e7e6f277010ad6d381cbcf271b88db0f94 |
| 21 | tiles/rail-iron-alt.png | 16×16 | 4 | 1194879142c43e92918a7972e16f2eabf6769744531f944ef7502ea3fb00d0c6 |
| 22 | tiles/wall-riveted.png | 16×16 | 4 | d76bddff3085dc16af2fd0b6d59528b03680fa23e4f82330f9d0a0128aca7df6 |
| 23 | tiles/wall-riveted-alt.png | 16×16 | 5 | 2257e65a8955ce13d71aad655b0203ed6c9340bec2e1f622f57196eadbb4a4e5 |
| 24 | tiles/floor-slag.png | 16×16 | 6 | 7cd523e8d975e66d06c666032e11375d3ba1048ead9752a65389b7bb56c5091c |
| 25 | tiles/floor-slag-alt.png | 16×16 | 5 | 070820fc1eaf0aac83059378c9091eb3edfa1618f1fe4def0a2f8da48b6326fc |
| 26 | tiles/water-industrial.png | 16×16 | 4 | b4eff8320744720a8872b8c203dea266813346674e236992ae61596fa8844934 |
| 27 | structures/chimney.png | 32×64 | 13 | 47e97cd0b66ab4dd03e756fd7bb85292e8aa7fbd0d2ffdb4239b742189ef18ef |
| 28 | belt/belt-body.png | 16×16 | 5 | 11f386469a59b3ff2e5ed97573d0fb854aebeb94b3a39835e6f7f243df391a16 |
| 29 | belt/belt-edge.png | 16×16 | 4 | f9569a7953d32efd80a77500ff1139198231171d925ee29e8773c92990dbe972 |
| 30 | belt/belt-turn-edge.png | 16×16 | 4 | 02d2c51ccb0b8b21bcaabeb32b274ccafb613e28df92d73004d1b8231e972a87 |
| 31 | anim/belt-turn-anim.png | 64×32 | 5 | 17a0ff8e0dd21f0b3f659e35819151a748a4322ee48a56178757b26f59728bab |

合成器 `tools/art/era3_placeholders.py` 为本清单 31 件的确定性复现源（二次生成 sha256 全等）；脚本后续演进不追溯已登记 PNG（28 号 §6 冻结声明同款纪律，变更须 REV）。

## 6. 调色板一致性与验证记录

- **E3 工业子板 18 色**（era3_placeholders.py `PALETTE`）：铆铁赭红 3（31 号主色 #A8503C 居中）/煤黑 3（#2E2A28）/蒸汽灰白 3（#C9C4BC）/钢 3/黄铜 3（齿轮）/炉火 3；全部单件色数 4~16 ≤ master 64（28 号 §5 全库口径延续）。
- 合成器自测：`python tools/art/era3_placeholders.py --self-test` → **static_files=31 rejected=0 warned=0**；belt: frames=8 grid=4x2x16 u-law=ok arms-shift=ok distinct=8 loop=seamless(2px×8=16=PAT16 period) overlay=disjoint；**determinism=31/31 sha256-equal**（二次生成临时目录逐文件比对）。
- G1 新增测试 47 项全绿：BeltRenderDecisionsTests（决策表/锁相/帧律/非法输入 40 项）+Era3ArtMappingTests（映射完整性/PNG 解码逐像素 u 律/帧边界平移/循环闭合/双层零重叠 7 项）。
- **headless 局限注明**（验收口径如实披露）：G4 冒烟证明 belt-scroll.gdshader 资源装载+材质参数绑定+转弯条带几何（ART3/BELT 标记，Godot 无头进程内实测，SHADER ERROR=0）；「正确渲染」的像素级证明=纯逻辑决策层 47 项测试+合成器/C# 双侧逐像素断言（§3/§4），**GPU 着色输出属窗口化运行事实，headless 无 GPU 不做像素冒充**（CS-4.8 同纪律）。
- 时代路由：E3 地面层切换（TerrainFramebuffer era 路由）随 E3 关卡视觉任务落位——本包 Era3TerrainAtlas 以 ART2 同款「装载即证明」口径接入（28 号 §8 先例）。

### 6.1 门禁记录（bash tools/ci/run_all.sh，2026-10-13）

| 门禁 | 结果 | 明细 |
|---|---|---|
| G2 红线 | ✓ | 9 checks / 0 FAIL / 0 WARN |
| G2.5 资产 | ✓ | files=552 rejected=0 warned=0（含本包 +31；anim/belt-turn-anim 的 frames=2 为横条启发式 INFO 注记，8 帧契约见 §3.2/§4 断言） |
| G1 测试 | ✓ | **5857 passed / 0 failed**（含本任务新增 47 项：BeltRenderDecisionsTests 40+Era3ArtMappingTests 7） |
| G3 构建 | ✓ | Release 0 警告 0 错误（TreatWarningsAsErrors=true） |
| G4 冒烟 | ✓ | SMOKE_OK + `ART3 era=industrial atlas_tiles=9 tile_px=16 atlas=144x16 pack_files=22/22` + `BELT shader=belt-scroll layers=2 body_px=16 turn_frames=8 strip=64x32 grid=4x2`，SHADER ERROR=0 |

- **中间跑干扰披露（28 号 §8 共享树先例同款）**：本任务两次中间 run_all 在 G1 单点红于 `LlmGatewayTimeoutTests.Idle_gap_above_the_budget_classifies_as_idle_timeout`（400ms 空闲预算 vs 1500ms 间隔的墙钟竞态测试，期望 timeout-idle 实得 null——CancelAfter 定时回调在满套件负载下被延迟越过间隔所致）；隔离复跑该类 8/8 通过（480ms）；两次失败时段恰与并行 T-118 泳道在共享树上跑门禁重叠（其提交 3cd0285 于中途落盘）。T-118 落盘后同树复跑 **全绿（上表）**——判定=并行负载态时基漂移，非本任务改动引入（本任务零触碰 Llm/流/计时域）。

## 变更记录

- v1.0（2026-10-13）：T-119 首次成文。E3 实体→素材映射 18 项全表+地形/UI/装饰（"无实体无素材"清零）；31 件 100% 程序合成（18 色工业子板，桁架×齿轮母题）；传送带双层动画全套（belt-scroll.gdshader+3 张 16px 双层纹理+64×32 转弯 8 帧网格+帧边界对齐断言）；headless 渲染决策层 BeltRenderDecisions（T-102 决策表先例+相位锁相）；sha256 清单 31 件；Boss 64 画布裁定（28 号 §5 先例，96=REV 挂账维持）。
- v1.1（2026-10-13）：T-125 遗迹映射正式版跨域注记（本清单冻结面零变更）。E3→E4 跃迁的新时代地图遗迹标记（立碑遗迹/考古挖掘位/纪念馆修复位/核心保留区勾勒，RelicMarkerBoard 消费）属 **era 无关域**，冻结登记在 28 号 v1.1 §2.3/§4.4（textures/relic/，relic_placeholders.py 合成）——不属本清单 era3/ 树，E4 时代专属素材仍归 T-128。
