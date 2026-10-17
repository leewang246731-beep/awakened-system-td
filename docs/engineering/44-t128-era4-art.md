# 44 · T-128 E4 美术包（现代色板全量程序合成）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **已交付**（E4 全量程序合成就位；本表=28 号冻结清单的 E4 续表——28 号 §1 冻结面 v0.1=E1+E2 不变，E3 续表=41 号，E4 随本表登记。28 号 §6 待做 #8（E3/E4 美术包）随本表全数闭账，仅余 W8 T-142） |
| 任务卡 | T-128（E4 美术包；Wave 7，E 泳道，组件 C-28；覆盖 F-133；依赖 T-119✓） |
| 验收标准 | E4 素材全过校验器（08-task-plan T-128 行） |
| 依据 | 28-t094-art-freeze（冻结清单先例+程序合成回退路线+Boss 画布裁定）；41-t119-era3-art（E3 续表先例+传送带冻结几何契约）；31-visual-enrichment §1.2 E4 视觉身份卡（电网蓝 #3D7EA6/混凝土灰 #9A9A94/警示黄 #E3C14F+混凝土雷达塔+播放键三角×雷达扇扫母题）；era4 内容四文件（T-123 冻结，只读：towers/enemies/era/levels）；01-feature-inventory F-133/F-042/F-127；33-video-A-tier A-04（E3→E4 跃迁成片=帧参考）；CODE_STANDARDS CS-4.1/CS-5.1 |
| 交付物 | tools/art/era4_placeholders.py（新，E4 合成器）；presentation/EraTD/textures/era4/** 30 PNG（§2/§5）；测试 Era4ArtMappingTests（11 项，game/src/EraCore.Tests/Presentation/）；本映射清单 |

---

## 1. 范围与口径

- **范围**=E4（现代）全部战斗内像素资产：四塔×升级位（导弹/激光/雷达/地雷）、九敌+夜枭 Boss、卡车载具（era.json logistics.truck）、传送带 E4 皮肤（logistics 第 3 档=15 件/s：belt 纹理色板换装，复用 T-119 双层动画全套几何）、E4 地形子集（混凝土/公路/玻璃幕墙 9 槽）、UI 色板。实体清单唯一真相源=`game/content/era4/{towers,enemies,era}.json`（T-123 冻结，只读）。
- **来源**=**100% 自研程序合成**（tools/art/era4_placeholders.py，代码生成像素=仓库自身 MIT 代码产物，来源缩写 **P4**）。E4 无 CC0 素材源入库（Kenney 三包均前代主题；T-078 预批准回退路线+T-119 全量程序合成先例）。红线对齐：程序绘制=确定性（固定坐标+固定 18 色现代子板、无随机数、无 AI 生图），实测二次生成 **sha256 逐字节全等 30/30**（`--self-test` 自动复验）。
- **验收闭环**=G2.5 资产门禁（run_all Step 1.5 全目录扫描）+ 传送带 E4 皮肤 8 帧循环的逐像素断言（§3）+ 特殊形态锚点断言（§4）+ 本清单 sha256 登记（§5）。
- **时代路由**：E4 地面层/atlas 装载接线随 E4 关卡视觉任务落位（41 号 §6 同款「装载即证明」口径的另一半——本包以素材+映射测试交付，presentation 侧 Era4TerrainAtlas/ArtPack 接线不在本任务卡范围，挂 E4 视觉接线任务）。

## 2. E4 实体→素材映射全表（30 件）

"来源"缩写：**P4**=程序合成 era4_placeholders.py。画布单位 px；色数=validator 实测 distinct RGBA（含透明）。

### 2.1 塔（era4/towers.json：tier/max_tier 驱动升级位文件名）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era4:tower-missile L1/L2（tier1/max2） | 导弹塔 | towers/tower-missile-l1.png / -l2.png | 32×32 | 14/14 | P4 |
| era4:tower-laser L1/L2（tier2/max2） | 激光塔 | towers/tower-laser-l1.png / -l2.png | 48×48 | 14/15 | P4 |
| era4:tower-radar L1（max1 单档） | 雷达 | towers/tower-radar-l1.png | 32×32 | 13 | P4 |
| era4:tower-mine L1（max1 单档，specialization） | 地雷 | towers/tower-mine-l1.png | 32×32 | 12 | P4 |

- 升级位标识：L2=警示黄标记（侧挂发射舱+炮塔警示带/双侧警示电容组+塔腰警示带）——E1 骨白、E2 金、E3 黄铜之后的第四代升级位色律（每代一识）。
- 画布档：激光塔 48（高能发射塔，E3 臼炮同档先例）；其余 32。
- **max1 单档**（雷达=支援设施、地雷=利基行）不发明 L2 档（Era4ArtMappingTests 哨兵断言 l2 不得存在）。

### 2.2 敌+Boss（era4/enemies.json 全行）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era4:enemy-militia | 武装民兵 | enemies/enemy-militia.png | 32×32 | 8 | P4 |
| era4:enemy-assault-trooper | 突击兵 | enemies/enemy-assault-trooper.png | 32×32 | 11 | P4 |
| era4:enemy-siege-mech | 攻城机甲 | enemies/enemy-siege-mech.png | 32×32 | 12 | P4 |
| era4:enemy-raid-quad | 掠袭四驱 | enemies/enemy-raid-quad.png | 32×32 | 12 | P4 |
| era4:enemy-missile-trooper | 导弹兵 | enemies/enemy-missile-trooper.png | 32×32 | 10 | P4 |
| era4:enemy-riot-armor | 防爆装甲兵 | enemies/enemy-riot-armor.png | 32×32 | 10 | P4 |
| era4:enemy-kamikaze-drone | 自爆无人机 | enemies/enemy-kamikaze-drone.png | 32×32 | 11 | P4 |
| era4:enemy-ew-officer | 电子战军官 | enemies/enemy-ew-officer.png | 32×32 | 11 | P4 |
| era4:enemy-boss（era.json boss 夜枭） | 夜枭 | enemies/boss-nightowl.png | 64×64 | 9 | P4 |

**Boss 画布裁定**（28 号 §5/41 号 §2.2 先例重演）：内容表 `canvas_size_px=96`，96 不在校验器 SIZE-ALLOWLIST {16,32,48,64}——本轮取 **64×64**（14 §3「常规 Boss 64~96」区间下限，合规，不扩清单）；升 96（及终章 Boss 96×112）仍须随 SIZE-ALLOWLIST 扩展走 REV（28 号 §6 待做 #2 挂账维持）。

**夜枭形态**（31 号 §二 C-04 帧参考「夜枭掠过城市灯火」的实体侧对应）：飞翼隐形轰炸机（无尾飞翼+菱形折面缝蒙皮）+**枭目双传感器**（glass 盘+警示黄瞳=锁定光，夜枭得名形）+腹内空袭弹序（traits.airstrike 弹舱）+翼尖电网蓝航行灯（本代主色锚）+蒙皮护盾微光（shield 1200 行形）——Era4ArtMappingTests 逐像素锚点断言。

### 2.3 载具/UI/地形/传送带皮肤

| 实体/用途 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|
| E4 载具 truck（era.json logistics.truck；15/s 档并列机动运输） | units/truck-right.png | 32×32 | 14 | P4 |
| E4 面板色板（C-22 换肤底稿） | ui/panel-era4.png | 32×32 | 16 | P4 |
| E4 地形槽 0~8（Era3TerrainAtlas 语义序对位：Plain/代位/Road×2/Wall×2/Rock×2/Water） | tiles/floor-concrete、floor-concrete-grated、road-asphalt、road-asphalt-alt、wall-glass-curtain、wall-glass-curtain-alt、floor-rubble-concrete、floor-rubble-concrete-alt、water-modern | 16×16 | 3~7 | P4 |
| 传送带直段·动画层（shader 采样，E4 皮肤） | belt/belt-body.png | 16×16 | 5 | P4 |
| 传送带直段·静态层（带边护栏） | belt/belt-edge.png | 16×16 | 4 | P4 |
| 传送带弯口·静态层（弯口边框） | belt/belt-turn-edge.png | 16×16 | 4 | P4 |
| 传送带转弯 8 帧（帧门禁对象，§3） | anim/belt-turn-anim.png | 64×32 | 5 | P4 |

- 卡车双件口径：era3/units/truck-right.png（T-119 预制 E4 前置件，铆铁色板）与 era4/units/truck-right.png（本件，现代色板正式档）并存——前置件冻结不动（28 号 §6 纪律），E4 时代取本件。
- 传送带 E4 皮肤与 era3 belt 全套（T-119）**几何逐字节同构**（§3），仅色板换装。

**盘点结论**：E4 内容实体映射 **19 项**（塔 4 种×升级位=6 张、敌 9 张、载具 1）+地形 9+UI 1+传送带皮肤 4 全部有素材过校验器——E4"无实体无素材"清零（弹药件 era4:ammo-{missile,capacitor,mine} 无图=历代口径：E1~E3 弹药件均无独立 sprite，弹药以塔体弹链/弹匣位表达）。

## 3. 传送带 E4 皮肤（15/s 档，T-119 冻结几何复用）

- **档位口径**：era.json logistics tier=3（ladder 第 4 档位值 15.0 件/s——v3.2 §5 L78「0.3→0.6→10→15→20→∞」），carrier=conveyor_belt+truck 并列。**带速不烙进纹理**：动画速度=决策层 scroll 相位量（BeltRenderDecisions.ShaderOffsetPx/FrameIndexFor）驱动，纹理仅图案相位源——同一皮肤可服务任意带速档，E4 15/s 与 E3 10/s 共用同一帧律。
- **色板换装**：PAT16（周期 16）=沥青胎面两档（asphalt_dark/asphalt_mid）+警示黄防滑条两档（idx2 warn_main 亮/idx10 warn_dark 暗）——E3 煤黑×黄铜同构异色；护栏=混凝土灰立缘+警示黄柱钉（E3 钢轨×黄铜铆钉对位）。
- **几何契约三方镜像不变**（41 号 §4 全表继续有效）：带体 tile 16px/图案周期 16px/直段步长 16px/转弯步长 21px/8 帧×2px——BodyTilePx↔BODY_TILE_PX、PatternPeriodPx↔PAT16 长度、StraightPathPx↔STRAIGHT_PATH_PX、TurnPathPx↔TURN_PATH_PX、TurnFrameCount/TurnPxPerFrame↔TURN_FRAMES/PX_PER_FRAME；u 律=era3_placeholders.turn_path_u 逐式复制（**冻结契约：几何不随皮肤变**）。直线段仍由 shaders/belt-scroll.gdshader UV 滚动消费（材质双纹理换绑 era4 belt 目录即得 E4 皮肤，渲染接线随 E4 视觉任务）。
- **帧边界对齐断言**（合成器 `--self-test`+C# Era4ArtMappingTests 双侧）：①每帧逐像素符合 u 律（E4 PAT16 色）；②双臂逐像素平移（竖臂帧 k+1(x,y)=帧 k(x,y+2)、横臂帧 k+1(x,y)=帧 k(x−2,y)）；③循环闭合（8 帧×2px=16px=周期）；④八帧两两不同；⑤静态护栏与带体不透明集零重叠。anim/belt-turn-anim 的 validator frames=2 为横条启发式 INFO 注记（64×32 非 128×16，同 41 号 §3.2 口径）。

## 4. 特殊形态裁定（任务卡口径落位）

| 形态 | 裁定 | 落位 |
|---|---|---|
| 雷达=旋转 | **决策层静态基准帧+旋转属渲染层**：本包供 canonical 单帧（抛物面开口朝上+馈源头/杆+静态扇扫注记弧+播放键母题徽）；运行时旋转=渲染层 SpriteRotationQuarters（T-119 传送带四向旋转/FlipX 同律）。不预烘旋转动画帧（升帧=28 号 §6 待做 #3 REV 挂账） | towers/tower-radar-l1.png；Era4ArtMappingTests.Radar_sprite_is_the_static_canonical_dish_form 逐像素锚点 |
| 地雷=埋设半隐 | **地表线 y=21 分界**：以上出露感应穹顶+触发须+LED+警示接触环；以下没入地面带（埋设床全覆盖+埋设体暗影隐现）；穹顶以上留大段透明缘（非通高实心方块——「埋没的下半不可见」即半隐语义） | towers/tower-mine-l1.png；Era4ArtMappingTests.Mine_sprite_depicts_half_buried_placement 逐像素锚点 |

播放键母题（E4 专属，31 号 §1.2）落位：雷达/导弹/激光塔基座徽+卡车箱徽（grid_main 三角，h=3~7px 五档）；雷达扇扫=静态注记弧（circuit_light 虚点，§4 上行）。

## 5. E4 清单 sha256（30 PNG，路径根=presentation/EraTD/textures/era4/）

| # | 文件 | 实体/用途 | 画布 | 色数 | 来源 | sha256 |
|---|---|---|---|---|---|---|
| 1 | towers/tower-missile-l1.png | era4:tower-missile L1 | 32×32 | 14 | P4 | 3c83a1d664ba3e983aeb3220906441156eb20dbbc8c8ca56f37f25d33a034bcf |
| 2 | towers/tower-missile-l2.png | era4:tower-missile L2 | 32×32 | 14 | P4 | 3655355afddbe4574a99b1c8513a1b1a3ccc23d56b062c65d0dd5c490acb5221 |
| 3 | towers/tower-laser-l1.png | era4:tower-laser L1 | 48×48 | 14 | P4 | 6280cbe56831b72296b3a505eac3c731d804dea58aebafae581fcd6ed2cb861c |
| 4 | towers/tower-laser-l2.png | era4:tower-laser L2 | 48×48 | 15 | P4 | 359572fc5313adcb8231602654e829fb3b5d8d300b24d317ad08c0358eae3f7f |
| 5 | towers/tower-radar-l1.png | era4:tower-radar L1 | 32×32 | 13 | P4 | a59384c03e227ddcd60800f02de79447ed3764fd61ba9e5988a925592c2c87c9 |
| 6 | towers/tower-mine-l1.png | era4:tower-mine L1 | 32×32 | 12 | P4 | 638e5d46b3cc3d10a64df6902ee4da30d1285a19e070338e1ba9577896a793f0 |
| 7 | enemies/enemy-militia.png | era4:enemy-militia | 32×32 | 8 | P4 | 0790e52bc27d45c95abb37159e73882d937cffc50c3c5cac11350ddff0a048a5 |
| 8 | enemies/enemy-assault-trooper.png | era4:enemy-assault-trooper | 32×32 | 11 | P4 | cd562f7ecc05aaa01b2fc295e4d53dceb73465e0d77f2036a567556c262dbdd9 |
| 9 | enemies/enemy-siege-mech.png | era4:enemy-siege-mech | 32×32 | 12 | P4 | de3309a8eb4b3c11e445da4d6cb4f9eb53743055cf447586776cf06307985f5d |
| 10 | enemies/enemy-raid-quad.png | era4:enemy-raid-quad | 32×32 | 12 | P4 | 13258b1a083081ab4c06484bb40a024b85ba3139b9f12b59f553f3c264692ce4 |
| 11 | enemies/enemy-missile-trooper.png | era4:enemy-missile-trooper | 32×32 | 10 | P4 | 938541932bbd6c72a2ba036ec413bc3a13d24070720481cd80bf75ff05f305ba |
| 12 | enemies/enemy-riot-armor.png | era4:enemy-riot-armor | 32×32 | 10 | P4 | 1573f32f04454a4b01778fdc6a4b0f891473f8659637da9ef4e9ac84bd07303b |
| 13 | enemies/enemy-kamikaze-drone.png | era4:enemy-kamikaze-drone | 32×32 | 11 | P4 | 8cf6cae0c2968b27a9f7bd919f07feaa7c402ed3cb65f96da9ba89d0e62e5c86 |
| 14 | enemies/enemy-ew-officer.png | era4:enemy-ew-officer | 32×32 | 11 | P4 | 2d00b61b0419386bf19b6a821c5ae07c6ee2fb84df02f1f9518b719dd291d504 |
| 15 | enemies/boss-nightowl.png | era4:enemy-boss 夜枭 | 64×64 | 9 | P4 | 4ea272773c4ebaaae769217199f7139e183fe9ca7d562202adad5a7c25930113 |
| 16 | units/truck-right.png | E4 载具 卡车 | 32×32 | 14 | P4 | a20bbc2bd10d0096b01b7668f906335e878a5ff423cea0834dfaf853a106bf1f |
| 17 | ui/panel-era4.png | E4 面板色板 | 32×32 | 16 | P4 | ad2b29c1653e773141ba75346e28bd18a7ed20c9096b01657bc85eaa7f9785ee |
| 18 | tiles/floor-concrete.png | E4 地形槽 0 | 16×16 | 3 | P4 | 91479728b2f2641cc8ecd035ea1a56d65b9d06dc6c206b1d3b8971cce666caee |
| 19 | tiles/floor-concrete-grated.png | E4 地形槽 1 | 16×16 | 5 | P4 | a1d2e7399912e860275251dae8a28fc58b073521b48aff6d74dd677127b10fbf |
| 20 | tiles/road-asphalt.png | E4 地形槽 2 | 16×16 | 6 | P4 | 52f5e296acb754e62bb1dde16ae84cef5c0a3a525f456c14ae84bf74b82234df |
| 21 | tiles/road-asphalt-alt.png | E4 地形槽 3 | 16×16 | 7 | P4 | ad8fdb9a203bcadbcfb28a6fdb7ccb708ef3fba2f5ed0c284dfd031a5013dfee |
| 22 | tiles/wall-glass-curtain.png | E4 地形槽 4 | 16×16 | 6 | P4 | 203ca49f46308087a67631f49f55f707cbdc85b68a49fd06bfc9084c95bced8f |
| 23 | tiles/wall-glass-curtain-alt.png | E4 地形槽 5 | 16×16 | 7 | P4 | d52cefe13aa6ecbaf508842e401e25ff5c52a19092419223bf38a009a5d162b5 |
| 24 | tiles/floor-rubble-concrete.png | E4 地形槽 6 | 16×16 | 6 | P4 | 57569168ce504ba40cb792d593c1bd74424b6a3925e186efc491a01645e2bace |
| 25 | tiles/floor-rubble-concrete-alt.png | E4 地形槽 7 | 16×16 | 6 | P4 | b7ff43c7a87a466a9f87de18aa3d7528fe8ad4d5b8908c7140866805abf5b859 |
| 26 | tiles/water-modern.png | E4 地形槽 8（水） | 16×16 | 5 | P4 | b6265797c302d37435b4efa99a876b97e3ddc11377a61f5ab05c48847d7b4627 |
| 27 | belt/belt-body.png | 传送带直段·动画层 | 16×16 | 5 | P4 | abf898dccaa1f2aaeb7de776818f99dcc76adc6e2aa766f9ec36f7eaec73a6cb |
| 28 | belt/belt-edge.png | 传送带直段·静态层 | 16×16 | 4 | P4 | e6428464decbe37ad90321d450f20d7f75048dbe9d8163d1a21821e904cb63f0 |
| 29 | belt/belt-turn-edge.png | 传送带弯口·静态层 | 16×16 | 4 | P4 | a882b961ae1fb44980f350d980014988c4a9d87c5d4d6d00aef9997614870945 |
| 30 | anim/belt-turn-anim.png | 传送带转弯 8 帧 | 64×32 | 5 | P4 | d51709905bf674c4f0682e3a8f1798130bcdf49ee3d1ab0d637d2f1e055aff5c |

合成器 `tools/art/era4_placeholders.py` 为本清单 30 件的确定性复现源（二次生成 sha256 全等）；脚本后续演进不追溯已登记 PNG（28 号 §6 冻结声明同款纪律，变更须 REV）。

## 6. 调色板一致性与验证记录

- **E4 现代子板 18 色**（era4_placeholders.py `PALETTE`）：电网蓝 3（31 号主色 #3D7EA6 居中）/混凝土灰 3（辅 #9A9A94）/警示黄 3（辅 #E3C14F）/沥青黑 3（公路·车胎·机械暗部——E3 煤黑对位）/玻璃 3（幕墙反光·座舱·雷达罩）/电路绿 3（走线·指示灯——纹理语言第三件）；全部单件色数 3~16 ≤ master 64（28 号 §5 全库口径延续）。四代子板累计 23+22+18+18=81 色——**逐文件口径**（每件 ≤64 色）与 28 号 §5 全库统计口径一致，无超限件。
- 合成器自测：`python tools/art/era4_placeholders.py --self-test` → **static_files=30 rejected=0 warned=0**；belt: frames=8 grid=4x2x16 u-law=ok arms-shift=ok distinct=8 loop=seamless(2px×8=16=PAT16 period) overlay=disjoint；**determinism=30/30 sha256-equal**（二次生成临时目录逐文件比对）。
- G1 新增测试 11 项全绿：Era4ArtMappingTests（映射完整性 3+夜枭形态锚点+载具/UI/地形/belt 在场+雷达静态形态+地雷半隐+直段双层 u 律+转弯 8 帧五联断言+boundary 帧序锁相+illegal 单档哨兵×2）。
- 时代路由：E4 地面层切换与 belt 皮肤材质换绑随 E4 关卡视觉接线任务落位（41 号 §6「装载即证明」先例的延续；本包以素材+双侧逐像素断言交付）。

### 6.1 门禁记录（bash tools/ci/run_all.sh，2026-10-13）

| 门禁 | 结果 | 明细 |
|---|---|---|
| G2 红线 | ✓ | 9 checks / 0 FAIL / 0 WARN |
| G2.6 模组 | ✓ | 0 FAIL（3 mods / 8 content documents） |
| G2.5 资产 | ✓ | files=586 rejected=0 warned=0（含本包 +30；anim/belt-turn-anim 的 frames=2 为横条启发式 INFO 注记，8 帧契约见 §3 断言） |
| G1 测试 | ✓ | **6149 passed / 0 failed**（684s；含本任务新增 11 项 Era4ArtMappingTests） |
| G3 构建 | ✓ | Release 0 警告 0 错误（TreatWarningsAsErrors=true，49s） |
| G4 冒烟 | ✓ | SMOKE_OK（29s；E3 ART3/BELT 标记维持，E4 接线未挂→无 ART4 标记=口径如实，见 §1 时代路由行） |

## 变更记录

- v1.0（2026-10-13）：T-128 首次成文。E4 实体→素材映射 19 项全表+地形/UI/传送带皮肤（"无实体无素材"清零）；30 件 100% 程序合成（18 色现代子板，播放键三角×雷达扇扫母题）；传送带 E4 皮肤=15/s 档色板换装（T-119 冻结几何逐式复用，u 律/帧边界对齐双侧断言）；特殊形态落位（雷达=静态基准帧+旋转渲染层注记、地雷=埋设半隐，逐像素锚点）；sha256 清单 30 件；Boss 64 画布裁定（28 §5 先例，96=REV 挂账维持）；28 号 §6 待做 #8（E3/E4 美术包）闭账（E5/终章=T-142 W8 维持）。
