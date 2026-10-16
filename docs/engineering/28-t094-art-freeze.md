# 28 T-094 E1+E2 美术包冻结清单（v0.1）

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-10-13 |
| 状态 | **已冻结生效**（Q8 口径 v0.1=E1+E2；此后 §4 清单内素材不再变更，变更须走 REV，见 §6。v1.1=T-125 增补跨代遗迹标记域 §2.3/§4.4——新增冻结面，既有 63 PNG 零触碰） |
| 任务卡 | T-094（E1+E2 美术包冻结；Wave 4，E 泳道，组件 C-28；覆盖 F-132；依赖 T-078） |
| 验收标准 | 全实体/UI 素材过校验并冻结清单（08-task-plan T-094 行，Q8 范围） |
| 依据 | 14-art-license-audit §3/§4（像素规格+台账纪律）；15-t045-art-integration（E1 先例）；24-t078-era2-art（E2 先例+程序合成回退路线）；07-decided-plan Q8（首发=E1+E2）；CODE_STANDARDS CS-4.1/CS-5.1 |
| 交付物 | 本冻结清单；tools/art/era1_placeholders.py（新）+era2_placeholders.py（增补敌/英雄）；presentation/EraTD/textures/era1/** 20 PNG+era2/** 增补 10 PNG（缺漏清零，§3）；R9 豁免登记（tools/ci/allowlist.txt）；U 盘同步 zip（§7） |

---

## 1. 冻结范围与口径

- **范围**=Q8 裁定首发两章 E1（远古）+E2（古典）的全部战斗内像素资产：塔×级、敌种、Boss、英雄、地形 tile、UI 色板、载具、E2 建筑件。实体清单唯一真相源=`game/content/era1/{towers,enemies}.json`、`game/content/era2/{towers,enemies,heroes}.json`、`game/content/era{1,2}/era.json`（载具/Boss/资源）与 `game/content/era2/heroes.json`（E1 英雄行同表）。
- **两处资产线**（24 号 §6 口径延续）：①`presentation/EraTD/textures/`（战斗内像素资产，asset_validator 门禁内）=本清单冻结面；②E 盘 `E:\game-assets\`（演出素材/底稿，T-146 manifest 管线）不属本冻结，两线对账结论：**零交集**（E 盘 era2-banner 等 4 件为 banner 演出域，未在 §4 清单内）。
- **验收闭环**=G2.5 资产门禁（run_all Step 1.5，扫描 `game/assets`+`presentation/EraTD/textures` 全目录）+ 调色板一致性抽查（§5）+ 本清单 sha256 冻结（§4）。
- **许可结论**：冻结面素材只来自两类来源——Kenney CC0 三包（14 号台账 A1+T-078 两包）与**自研程序合成**（tools/art 两脚本，代码生成像素=仓库自身 MIT 代码产物，无第三方版权）。无新增第三方包入库，无需新许可证快照；既有四快照 sha256 见 §4.3。AI 生图红线延续：AI PNG 全部只在 `docs/assets/`（概念图六张）与 E 盘参考域，**零张进入 presentation**。

## 2. 实体→素材映射全表（冻结盘点结果）

盘点方法：逐文件枚举内容 JSON 全部实体条目 → 在 presentation 纹理树中找对应素材 → 记录来源/许可。"来源"缩写：**TD**=Kenney Tiny Dungeon（CC0）、**TT**=Kenney Tiny Town（CC0）、**MRTS**=Kenney Medieval RTS（CC0）、**P1**=程序合成 era1_placeholders.py、**P2**=程序合成 era2_placeholders.py。画布单位 px；色数=validator 实测 distinct RGBA。

### 2.1 E1（远古，`textures/era1/`+`textures/tiles/`）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era1:tower-wood-arrow L1/L2 | 木箭塔 | towers/tower-wood-arrow-l1.png / -l2.png | 32×32 | 4/7 | P1 |
| era1:tower-rockfall L1/L2 | 落石台 | towers/tower-rockfall-l1.png / -l2.png | 32×32 | 8/10 | P1 |
| era1:tower-spike-trap L1/L2 | 尖刺陷阱 | towers/tower-spike-trap-l1.png / -l2.png | 32×32 | 6/7 | P1 |
| era1:tower-campfire L1/L2 | 篝火 | towers/tower-campfire-l1.png / -l2.png | 32×32 | 8/10 | P1 |
| era1:enemy-beastling | 兽崽 | enemies/enemy-beastling.png | 32×32 | 6 | P1 |
| era1:enemy-hunter | 猎狼 | enemies/enemy-hunter.png | 32×32 | 7 | P1 |
| era1:enemy-boar | 铠猪 | enemies/enemy-boar.png | 32×32 | 8 | P1 |
| era1:enemy-tribesman | 蛮徒 | enemies/enemy-tribesman.png | 32×32 | 9 | P1 |
| era1:enemy-warband | 蛮兵 | enemies/enemy-warband.png | 32×32 | 8 | P1 |
| era1:enemy-champion | 蛮勇士 | enemies/enemy-champion.png | 32×32 | 12 | P1 |
| era1:enemy-ram | 撞木 | enemies/enemy-ram.png | 32×32 | 10 | P1 |
| era1:enemy-boomshrew | 自爆鼩 | enemies/enemy-boomshrew.png | 32×32 | 12 | P1 |
| era1:enemy-boss（era.json boss） | 踏群者 | enemies/boss-stomper.png | 64×64 | 10 | P1 |
| era1:hero-firstfire（era2/heroes.json E1 行） | 初火守望者 | heroes/hero-firstfire.png | 32×32 | 14 | P1 |
| era1 载具 carrier=porter（era.json logistics） | 民夫 | units/porter-right.png | 32×32 | 11 | P1 |
| E1 面板色板（C-22 换肤底稿） | — | ui/panel-era1.png | 32×32 | 16 | P1 |
| TerrainAtlas 槽位 0~7（TerrainAtlas.cs TileFiles） | E1 地形 | tiles/floor-dirt.png、floor-dirt-pebbled、floor-dirt-mottled、floor-rubble、wall-brick、wall-brick-alt、wall-top、water-pool | 16×16 | 1~8 | TD |

### 2.2 E2（古典，`textures/era2/`）

| 实体（内容 ID） | 名称 | 素材文件 | 画布 | 色数 | 来源 |
|---|---|---|---|---|---|
| era2:tower-arrow L1/L2 | 箭楼 | towers/tower-arrow-l1.png / -l2.png | 32×32 | 8/11 | P2 |
| era2:tower-crossbow L1/L2 | 弩塔 | towers/tower-crossbow-l1.png / -l2.png | 32×32 | 10/12 | P2 |
| era2:tower-repeating-crossbow L1/L2 | 连弩塔 | towers/tower-repeating-crossbow-l1.png / -l2.png | 48×48 | 9/12 | P2 |
| era2:tower-onager L1/L2 | 弩炮（专精线） | towers/tower-onager-l1.png / -l2.png | 48×64 | 10/12 | P2 |
| era2:tower-fire-oil L1/L2 | 火油塔 | towers/tower-fire-oil-l1.png / -l2.png | 32×48 | 11/12 | P2 |
| era2:enemy-skirmisher | 散兵 | enemies/enemy-skirmisher.png | 32×32 | 8 | P2 |
| era2:enemy-warrior | 勇士 | enemies/enemy-warrior.png | 32×32 | 11 | P2 |
| era2:enemy-ram | 攻城锤 | enemies/enemy-ram.png | 32×32 | 10 | P2 |
| era2:enemy-raider | 轻骑 | enemies/enemy-raider.png | 32×32 | 8 | P2 |
| era2:enemy-crossbowman | 弩手 | enemies/enemy-crossbowman.png | 32×32 | 9 | P2 |
| era2:enemy-guard | 重装卫士 | enemies/enemy-guard.png | 32×32 | 11 | P2 |
| era2:enemy-firearrow | 火矢手 | enemies/enemy-firearrow.png | 32×32 | 10 | P2 |
| era2:enemy-centurion | 蛮族百夫长 | enemies/enemy-centurion.png | 32×32 | 14 | P2 |
| era2:enemy-boss（era.json boss） | 铁蹄 | enemies/boss-ironhoof.png | 64×64 | 13 | P2 |
| era2:hero-spearwarden | 执戈者 | heroes/hero-spearwarden.png | 32×32 | 11 | P2 |
| era2 载具 carrier=carriage（era.json logistics） | 马车 | units/carriage-right.png | 32×32 | 8 | P2 |
| E2 面板色板 | — | ui/panel-era2.png | 32×32 | 16 | P2 |
| Era2TerrainAtlas 槽位 0~8（Era2TerrainAtlas.cs TileFiles） | E2 地形 | tiles/grass、grass-spotted、road-stone、road-stone-alt、wall-stone-brick、wall-stone-brick-alt、farm-plowed、farm-plowed-alt、water | 16×16 | 1~5/水 3 | TT×8+P2×1 |
| 水域动画帧（帧门禁对象） | — | anim/water-anim.png | 32×16 | 3 | P2 |
| E2 走廊建筑件（城墙/城门渲染素材） | — | structures/watchtower、gatehouse、wall-crenellated | 64×64 | 44/17/15 | MRTS |

**盘点结论**：E1+E2 内容实体共 **63 项映射**（塔 9 种×两级=18 张、敌 16 种=16 张、Boss 2、英雄 2、载具 2、地形 17、UI 色板 2、E2 建筑 3、水域动画 1），全部有素材过校验器——**"无实体无素材"清零**。盘点时缺口共 **30 件**（§3），已全部程序合成补齐。

### 2.3 跨代遗迹标记（T-125 增补 v1.1，`textures/relic/`）

v1.1 新冻结面（T-125 遗迹映射正式版，F-057/F-168）：遗迹映射生产通道（RelicMapper/RelicDigSites/RelicMarkerBoard）的新时代地图 UI 标记面。该域 **era 无关**（遗迹本体=上一代建筑、标记=新一代地图 UI），不属任何单代美术包（E4 美术包=T-128 未产，按任务卡「美术未产占位合成」指示先行占位）；全部由 tools/art/relic_placeholders.py 程序合成（**P3** 来源，确定性：固定坐标+固定 12 色跨代遗迹色板=风化石灰岩 3+青铜铭刻 2+苔藓 2+勘测旗红 2+基座阴影 2+勾勒金 1，无随机数无 AI 生图）。stem 与 EraCore.Presentation/RelicMarkerBoard.cs 冻结常量逐一对应（跨层契约，RelicArtMappingTests 实锚）：

| 标记类（RelicMarkerKind） | 名称 | 素材文件 | 画布 | 来源 |
|---|---|---|---|---|
| StandingRelic（立碑遗迹：回放原位/移位） | 断柱纪念碑 | relic/relic-standing.png | 32×32 | P3 |
| DigSite（考古挖掘位：降级废墟地图实体） | 碎石堆+勘测旗 | relic/relic-dig-site.png | 32×32 | P3 |
| MemorialSlot（纪念馆修复位：未消费修复槽） | 纪念碑修复位 | relic/relic-memorial.png | 32×32 | P3 |
| CoreReserveOutline（A5 核心保留区勾勒） | 勾勒 tile（alpha 192 边框+255 四角） | relic/relic-reserve.png | 16×16 | P3 |

sha256 见 §4.4；升级口=28 号 §6 同律（占位→人工重绘须 REV+本清单递增版本，脚本为底稿）。

## 3. 缺漏补齐（本轮 30 件，程序合成）

T-045 只交付了 E1 地形 8 tile；T-078 交付了 E2 塔/马车/UI/水/建筑。冻结盘点发现以下实体零素材，按 T-078 预批准回退路线（08 卡片"CC0 找不到就用代码合成像素图"）以 tools/art 程序合成占位补齐：

| 缺口类别 | 数量 | 合成器 | 说明 |
|---|---|---|---|
| E1 塔×两级（4 塔） | 8 | era1_placeholders.py（新） | 木箭塔/落石台/尖刺陷阱/篝火；L2=骨白+战血标识（与 E2 金色 L2 区分代际） |
| E1 敌 8 种+踏群者 Boss | 9 | 同上 | Boss 画布 64（§5 白名单纪律） |
| E1 英雄头像（初火守望者） | 1 | 同上 | 32×32 半身像：羽冠+火把 |
| E1 载具民夫 | 1 | 同上 | 32×32 背篓步行（era.json carrier=porter） |
| E1 面板色板 | 1 | 同上 | 与 panel-era2 对偶（土色系 4×4 矩阵） |
| E2 敌 8 种+铁蹄 Boss | 9 | era2_placeholders.py（增补） | 复用该脚本 22 色 E2 古典色板（调色板一致性，§5） |
| E2 英雄头像（执戈者） | 1 | 同上 | 32×32 半身像：金冠缨盔+石蓝披风 |

红线对齐（24 号 §3 同款）：程序绘制=确定性（固定坐标+固定色板、无随机数、无 AI 生图），与 21 号 §2.1「AI PNG 不进 presentation」不冲突；实测二次生成 **sha256 全等**（era1 20 件、era2 24 件含既有 14 件均逐字节复现），人工重绘升级时以两脚本为底稿。`--self-test` 即调 asset_validator 校验自身输出：era1 `static_files=20 rejected=0`；era2 `static_files=31 rejected=0 anim_files=1 frames=2/expect-2`。

## 4. 冻结清单（sha256）

### 4.1 presentation 冻结面 63 PNG（逐文件）

路径根=`game/presentation/EraTD/textures/`；sha256=SHA-256 全文。来源/许可缩写见 §2（TD/TT/MRTS=Kenney CC0；P1/P2=自研程序合成）。

| # | 文件 | 实体/用途 | 画布 | 色数 | 来源 | sha256 |
|---|---|---|---|---|---|---|
| 1 | tiles/floor-dirt.png | E1 地形槽 0 | 16×16 | 1 | TD | 63519065228f5f8af6200d979fa54cd7f00650034ceea10a1609203a34b3ff9b |
| 2 | tiles/floor-dirt-pebbled.png | E1 地形槽 1 | 16×16 | 3 | TD | c4257bfa85878e4d293237cd9a50e72b3e883f721e4fe1c48132514bb93668f2 |
| 3 | tiles/floor-dirt-mottled.png | E1 地形槽 2 | 16×16 | 2 | TD | af45eaf5658a481cbf5645ed006ea59072e8bcdbe481b15a55204179d291f5c7 |
| 4 | tiles/floor-rubble.png | E1 地形槽 3 | 16×16 | 5 | TD | 3f4d77da84e948836c16d2c81f2812b7805e76147921f0ea9f047705f709a62a |
| 5 | tiles/wall-brick.png | E1 地形槽 4 | 16×16 | 4 | TD | 2d553978f4663e6fde5d5eb8dce1db9fd9aff9823c04c4ac9b5434625910c6c2 |
| 6 | tiles/wall-brick-alt.png | E1 地形槽 5 | 16×16 | 4 | TD | d010f83dc263ca1b3b4788ef380c7ef5695a21493c7db10e423c9891e2eac730 |
| 7 | tiles/wall-top.png | E1 地形槽 6 | 16×16 | 7 | TD | a18f13529118365c4054f2b6dc7379190c8d99fc70f2f1c374f6e95f532630ce |
| 8 | tiles/water-pool.png | E1 地形槽 7（水） | 16×16 | 8 | TD | 82ee687a8c6b7929ad25f4f9497a2e96b54b00c68cf174c405b77b74b456df33 |
| 9 | era1/towers/tower-wood-arrow-l1.png | era1:tower-wood-arrow L1 | 32×32 | 4 | P1 | 825106ab703b42e57ede567cafa3322e611a4c0a1625946af37f2c30d1d8cdd1 |
| 10 | era1/towers/tower-wood-arrow-l2.png | era1:tower-wood-arrow L2 | 32×32 | 7 | P1 | f9f4cf3c1711a7b8efb3648576f7d261b27a7609c6e3d100b5579162893f45bc |
| 11 | era1/towers/tower-rockfall-l1.png | era1:tower-rockfall L1 | 32×32 | 8 | P1 | 7d7efe19b77fe3f975640c3081884e1716e462f362213ca0db72f76c26f95fae |
| 12 | era1/towers/tower-rockfall-l2.png | era1:tower-rockfall L2 | 32×32 | 10 | P1 | 93ec13bd0b0bd658a5d8bfddc37d4d9736ffcb5f7fdc1f280882524d34f35aad |
| 13 | era1/towers/tower-spike-trap-l1.png | era1:tower-spike-trap L1 | 32×32 | 6 | P1 | 0ed7a88d913e67f268e7cacea62da8138d74dd7724ae45048f771fe21470b9d3 |
| 14 | era1/towers/tower-spike-trap-l2.png | era1:tower-spike-trap L2 | 32×32 | 7 | P1 | b17be1c7f96751d2cfeb8fd86b9f55ce61b35c5f86534355f9677f9afa54e7a8 |
| 15 | era1/towers/tower-campfire-l1.png | era1:tower-campfire L1 | 32×32 | 8 | P1 | 1bf4c849c9d3bb61633e283f00cbf7c9936999bcaef46ab8fe0d999f72f42d40 |
| 16 | era1/towers/tower-campfire-l2.png | era1:tower-campfire L2 | 32×32 | 10 | P1 | 9f68f63c8edb5bedca315584d311f350a6249dddd4098334b5aede0d9a10cb5b |
| 17 | era1/enemies/enemy-beastling.png | era1:enemy-beastling | 32×32 | 6 | P1 | 8f52e32ee866efb9abe7a15275ca7975f93c62065a9aa221fc381326f6a138e1 |
| 18 | era1/enemies/enemy-hunter.png | era1:enemy-hunter | 32×32 | 7 | P1 | 5c21ecded7d7f953773de093b65ac1b60ee65609be9561f4f5b33d926a79003c |
| 19 | era1/enemies/enemy-boar.png | era1:enemy-boar | 32×32 | 8 | P1 | 85a570a40ee167b295509bc9d4f1f2935a83eac89dd4fba30ae9ba6ad817fbe2 |
| 20 | era1/enemies/enemy-tribesman.png | era1:enemy-tribesman | 32×32 | 9 | P1 | 2814e83c3fadce222fb0d9f81eb4efad6c65f01e3cef7a3e9c79df712901fdef |
| 21 | era1/enemies/enemy-warband.png | era1:enemy-warband | 32×32 | 8 | P1 | d1e952a46dd2b40f6a3068b5e5a330f1622bf80c164c6fe02c5ac5c86e1b4dd0 |
| 22 | era1/enemies/enemy-champion.png | era1:enemy-champion | 32×32 | 12 | P1 | a5208c0971fbe71e77b7404ea7586c99af0003f34ec3fee4332f0f842b9a5cae |
| 23 | era1/enemies/enemy-ram.png | era1:enemy-ram | 32×32 | 10 | P1 | 835e763f9e6083f15f214bc1b60e5727f794bec371e80e6e5728ac06141c6d29 |
| 24 | era1/enemies/enemy-boomshrew.png | era1:enemy-boomshrew | 32×32 | 12 | P1 | ad119269dc3d158a8b8a1a585a1dc20f5dd624929334f6b51447d4089f443b75 |
| 25 | era1/enemies/boss-stomper.png | era1:enemy-boss 踏群者 | 64×64 | 10 | P1 | 71f5b574320d168d8c6f124b2b838d7606c1f5e07c34e40f78c1acd3d33f3b4c |
| 26 | era1/heroes/hero-firstfire.png | era1:hero-firstfire | 32×32 | 14 | P1 | bc5a259554aba8b0684839c15acfbb8878b1718d5a32f5ea5b8fd59af10a8880 |
| 27 | era1/units/porter-right.png | E1 载具 民夫 | 32×32 | 11 | P1 | 18356ad85f92dd356808a0158df8d0c383890154ef15a12aa9f289d20d386769 |
| 28 | era1/ui/panel-era1.png | E1 面板色板 | 32×32 | 16 | P1 | 0595746ec958a615e4450b59651b603fa957d65567f9570eb056e13bb2626abc |
| 29 | era2/towers/tower-arrow-l1.png | era2:tower-arrow L1 | 32×32 | 8 | P2 | d0f557e20c3880aa0cd05fdb827c9ed5f1ffa7563382f1bf5993fa7f2d4f9468 |
| 30 | era2/towers/tower-arrow-l2.png | era2:tower-arrow L2 | 32×32 | 11 | P2 | 95d1ef7bedc2743745d301a4ff56ae15b3e7065625de3eaec106df4c6f0b3332 |
| 31 | era2/towers/tower-crossbow-l1.png | era2:tower-crossbow L1 | 32×32 | 10 | P2 | 86e98deb18fe964e9bc8f1c4e6010a954ddef54b06c2a331f02cf540dd0f6894 |
| 32 | era2/towers/tower-crossbow-l2.png | era2:tower-crossbow L2 | 32×32 | 12 | P2 | 844a8b1d1d8240ba6010cec6d1b2ac3482eb6c3e1c4ffdb7a73de500f04674d5 |
| 33 | era2/towers/tower-repeating-crossbow-l1.png | era2:tower-repeating-crossbow L1 | 48×48 | 9 | P2 | fbd8cad31fc1d6afd9ff35ab3737def03471f0db1f10c6c1c371f9c6d102ac53 |
| 34 | era2/towers/tower-repeating-crossbow-l2.png | era2:tower-repeating-crossbow L2 | 48×48 | 12 | P2 | 6d2b69ce7fe8681134c4751a1c411b1b07aeef3f19929b32a04858d2cd954a7c2 |
| 35 | era2/towers/tower-onager-l1.png | era2:tower-onager L1 | 48×64 | 10 | P2 | 0d375edd51532c6ba28e7ebae32f3c3c6a0b1b79734ff8eae7ae5515cedf6a60 |
| 36 | era2/towers/tower-onager-l2.png | era2:tower-onager L2 | 48×64 | 12 | P2 | caea77625392099b65e4e2810ee78019140b50064bb044b2dae9ce8b55c8c46b |
| 37 | era2/towers/tower-fire-oil-l1.png | era2:tower-fire-oil L1 | 32×48 | 11 | P2 | d36726cbf1b1c47c87c6663be34af56741c153cde8cf7ae52a5278e5257922ff |
| 38 | era2/towers/tower-fire-oil-l2.png | era2:tower-fire-oil L2 | 32×48 | 12 | P2 | 214da423d16adf258d0ca54e090a1acdf3c50fed33524535b11ecfe24d53341 |
| 39 | era2/enemies/enemy-skirmisher.png | era2:enemy-skirmisher | 32×32 | 8 | P2 | 3f8e70fc05ee4c2cb0446c49760b072b21cfdedf1e746ffe4d14a1cb1a97653e |
| 40 | era2/enemies/enemy-warrior.png | era2:enemy-warrior | 32×32 | 11 | P2 | 427442d2b5937c1b17ac423bab45cc3adf28fac482f562b0ed594ff153d2b5a4 |
| 41 | era2/enemies/enemy-ram.png | era2:enemy-ram | 32×32 | 10 | P2 | d6c1419b5346a20ba5b955e5b0ea4d519e2733c733b8d53f5575a257282a4488 |
| 42 | era2/enemies/enemy-raider.png | era2:enemy-raider | 32×32 | 8 | P2 | 5707fd6841c5e516112369c72af85f80b15e67eed2ec0e3dd5323c0e001e6583 |
| 43 | era2/enemies/enemy-crossbowman.png | era2:enemy-crossbowman | 32×32 | 9 | P2 | b9e5f8e8ef2ff2eb6479c559769e61fdb821f82cc16343bca2917b7a1dcd01fa |
| 44 | era2/enemies/enemy-guard.png | era2:enemy-guard | 32×32 | 11 | P2 | d9bdef9b96027d5dd431d8c2704a9ded31011ac84d48e4c70714f7f0be2d9fec |
| 45 | era2/enemies/enemy-firearrow.png | era2:enemy-firearrow | 32×32 | 10 | P2 | b1265053ee9d6f1928ab7c1e91becc4c16fc1d7ee4b210fec1434b3d83bcf3a8 |
| 46 | era2/enemies/enemy-centurion.png | era2:enemy-centurion | 32×32 | 14 | P2 | 81cb8b10ed9aae587c64bb8711f008a8e630111b3550793992ba64475302138b |
| 47 | era2/enemies/boss-ironhoof.png | era2:enemy-boss 铁蹄 | 64×64 | 13 | P2 | d5897fed2d8bbd6648a2cb583a5c330ca5ede6e129400126349e0cadb435448c |
| 48 | era2/heroes/hero-spearwarden.png | era2:hero-spearwarden | 32×32 | 11 | P2 | 42393352f20cd388839c41a089306210913cd6934c737261d75115dd36968ef0 |
| 49 | era2/units/carriage-right.png | E2 载具 马车 | 32×32 | 8 | P2 | 286d07e211d9df66548118d38f2d64b0467e427dc9da1ff0753008d6fe73e18d |
| 50 | era2/ui/panel-era2.png | E2 面板色板 | 32×32 | 16 | P2 | 9ac9f3b72302aa8c6405fd138046f79d44eb67d59027b40aa752de9ce6df8ef5 |
| 51 | era2/tiles/grass.png | E2 地形槽 0 | 16×16 | 1 | TT | d4c692ef80ee97b3395b885ae07020d42a106683ff3650df240f8e972bd5a041 |
| 52 | era2/tiles/grass-spotted.png | E2 地形槽 1 | 16×16 | 3 | TT | cf3b3df8669e621af90944fb3155b15f13adf203dda464e6149d7318f3660713 |
| 53 | era2/tiles/road-stone.png | E2 地形槽 2 | 16×16 | 4 | TT | 3db01ff694f3d9f1e492d45913c3f85ff5f704ac262a1404d368c9b272a3f42d |
| 54 | era2/tiles/road-stone-alt.png | E2 地形槽 3 | 16×16 | 5 | TT | c62b7cd4a8397dc1d7be794246ee5a05aefef46ae320db1cf1aa9d2e752899ed |
| 55 | era2/tiles/wall-stone-brick.png | E2 地形槽 4 | 16×16 | 4 | TT | 7362f4df34b78d83fa8960f193bc15a3efc392c9d5ae7b4e308647051353969a |
| 56 | era2/tiles/wall-stone-brick-alt.png | E2 地形槽 5 | 16×16 | 4 | TT | 674c9bb782acc6d640332167bbcee82e0d0984f147b1a6c9afaac983a863491c |
| 57 | era2/tiles/farm-plowed.png | E2 地形槽 6 | 16×16 | 5 | TT | e1b87cceac398679e30d7cd4feecf4f40b8104515c255ed2d8e4fd4f868bb35c |
| 58 | era2/tiles/farm-plowed-alt.png | E2 地形槽 7 | 16×16 | 5 | TT | dff7a37b7fa1be3648dcb1d01be3df26eed3ea5dfb8d8b7ffdcc2af06715c1ad |
| 59 | era2/tiles/water.png | E2 地形槽 8（水，程序合成） | 16×16 | 3 | P2 | a3562c1e8f3470b9093f63301cbfacc5b5898908df69818059942bc47b2edab9 |
| 60 | era2/anim/water-anim.png | 水域两帧动画（帧门禁对象） | 32×16 | 3 | P2 | e8537b1bbd2fd38bb95052877f191361f4b2b026d3574655deebdfd4b4d8f1df |
| 61 | era2/structures/watchtower.png | E2 城墙建筑件 | 64×64 | 44 | MRTS | 69d3e0b700884405c66c24796a5a09d9ec1efedc680a3caf822405d6869a84bc |
| 62 | era2/structures/gatehouse.png | E2 城门建筑件 | 64×64 | 17 | MRTS | a171320299e0361647ff21fbf01f9c0d3e6f89636e336ca8653fda2a405652b5 |
| 63 | era2/structures/wall-crenellated.png | E2 垛墙建筑件 | 64×64 | 15 | MRTS | 0a341c6e7bbfc0f933e357a5ef89ab2a074d09ecbfb43f44d4f600519feff1aa |

### 4.2 game/assets 资产库三包（源库，tree digest 冻结）

逐文件 sha256 以 tree digest 冻结（算法：包内全部 .png 按**排序后相对路径+空格+sha256** 逐行拼接，对整体取 SHA-256——`python tools/ci/asset_validator.py` 的逐文件报告可复算）。REJECT 处置与逐包细节见 15 号 §2/24 号 §2（超色板 19 件、@2 大图 126 件等未入树）。

| 包（路径根=game/assets/） | 文件数 | tree digest（SHA-256） | 许可证 |
|---|---|---|---|
| kenney-tiny-dungeon/Tiles | 132 | 9a06bb195a0afbbcfa2fa0ca55674c24cfeb457973e7a8ce6c905194900f5ba7 | CC0（licenses/kenney-tiny-dungeon.txt） |
| era2/kenney-tiny-town/Tiles | 132 | 1c5811900f2d7b701bdec733ba582dcb7ceb1abd1a751be7f36860a87e2868c7 | CC0（licenses/kenney-tiny-town.txt） |
| era2/kenney-medieval-rts/{environment,structure,unit,tile} | 107 | 07cbf8c554b26acf116e69f8d10495b083d9edcf9e87bb56e48550b5e19fa384 | CC0（licenses/kenney-medieval-rts.txt） |

### 4.3 留档件（原始 zip+许可证快照，downloads/ 均不入库）

| 件 | 字节 | sha256 |
|---|---|---|
| game/assets/kenney-tiny-dungeon/kenney-tiny-dungeon.zip（入库留档） | 98,530 | c109438ab06f65fd80f9b2686a4cf9c7c11dc64444b47333ec71d602f8bb5fc7 |
| game/assets/era2/downloads/kenney_tiny-town.zip（gitignored） | 182,243 | 9768692dccff1d706408a5aedd6ca4f6cd1409506cbc84cb2f862919764be977 |
| game/assets/era2/downloads/kenney_medieval-rts.zip（gitignored） | 1,831,847 | f8217b5e8688dbb248736284678b8d4f31cfe43b399df4a7d0cf5f7c534764e8 |
| docs/assets/licenses/kenney-tiny-dungeon.txt | — | 9f574a2f1f636a3d…（16 位前缀，全文快照 T-045） |
| docs/assets/licenses/kenney-tiny-town.txt | — | 343c478f9ab2201a…（同上口径） |
| docs/assets/licenses/kenney-medieval-rts.txt | — | e8dfc7b7b30760f6…（同上口径） |

（音频线 zip/快照=26 号 T-092 台账，不属本清单。）

### 4.4 跨代遗迹标记（T-125 增补 4 PNG，v1.1）

路径根=presentation/EraTD/textures/relic/（§2.3 冻结面；合成器 tools/art/relic_placeholders.py，二次生成 sha256 全等实测）：

| 素材文件 | sha256 |
|---|---|
| relic/relic-standing.png | 40247b304f8e9e8ae20b6f730fda702716399f78978fe83e40b797a72884d66d |
| relic/relic-dig-site.png | 298b47842811ad08d0b040896ffa2bef02a2cc8f1e29a24184fe28283208362e |
| relic/relic-memorial.png | a73445a11fac8f1d26958c5e67be7d6a1740d57f3607920c0b72563ded7a56e0 |
| relic/relic-reserve.png | f45cde4b43132ea10f314ab21e8cbfcc606e58509b790a64c065422853950e9d |

## 5. 调色板一致性与白名单纪律

- **代际子板**：E1 远古子板 23 色（土 4/木 3/兽皮 3/骨 2/火 3/绿 3/血 2/铁 3，era1_placeholders.py `PALETTE`——与 Tiny Dungeon 土色系 tile 同族）；E2 古典子板 22 色（石/木/铁/金/蓝石/火/水，era2_placeholders.py `PALETTE`，T-094 增补的敌/英雄全部复用该 22 色）。两子板合计 45 色 ≤ master 64 上限（14 §3）。
- **全库逐文件色数**（validator 实测，434 文件）：最大 61（MRTS watchtower）≤64；>32 色仅 66 文件且全部为 Kenney CC0 原件（未改像素）；程序合成件最大 16 色。**零 REJECT、零 WARN**。
- **白名单零滥用**：入库全程未用 `--allow-oversized-palette`（run_all G2.5 调用参数无该旗标，24 号 19 件超色板 MRTS 精灵当时即拒收未入树）；SIZE-ALLOWLIST 维持 {16,32,48,64} 原清单未扩。
- **Boss 画布裁定**：内容表 canvas_size_px=96（两 Boss），但 96 不在校验器允许清单——本轮取 **64×64**（14 §3「常规 Boss 64~96」区间下限，合规），不扩清单；升 96（及终章 Boss 96×112）须随 SIZE-ALLOWLIST 扩展走 REV（§6 待做 #2）。

## 6. 冻结声明与待做清单

**声明**：v0.1（Q8=E1+E2）范围内 §4.1 全部 63 PNG+§4.2 三包库自本清单生效起冻结——**不再变更**（含重命名/改尺寸/改色）；任何变更（含占位→人工重绘升级、Boss 96 画布升级、动画帧引入）须开 REV 登记（docs/engineering/reviews/）+code-review 双轴后递增本清单版本。合成器脚本 tools/art/era{1,2}_placeholders.py 为冻结产物的确定性复现源（二次生成 sha256 全等），脚本后续演进不追溯已冻结 PNG。**v1.1 增补**：§2.3/§4.4 跨代遗迹标记 4 PNG（textures/relic/，T-125）并入同一冻结纪律——合成器 tools/art/relic_placeholders.py 为其确定性复现源，升级口同上（era 无关域，不随 T-128 E4 美术包自动重绘；重绘升级时以 REV 登记）。

**待做清单（不阻塞 v0.1，逐项挂 REV 升级口）**：

| # | 项 | 归属 | 说明 |
|---|---|---|---|
| 1 | E5/终章美术音频包 | **W8**（T-142，不排期） | 远期窗口，v0.1 范围外 |
| 2 | Boss 96 画布+终章 Boss 96×112 | 随 T-119/T-142 | 需扩 SIZE-ALLOWLIST=REV |
| 3 | 敌人动画帧（移动 6×2 向+死亡 6，14 §3 帧数惯例） | 升级轮 | v0.1 冻结为静态单帧+water-anim 两帧样例 |
| 4 | E2 塔 L3/L4 视觉档 | 升级轮 | 当前五塔×两级=T-078 验收口径；tier 3/4 差异化视觉档未画 |
| 5 | 系统人格四头像 | C-13/C-22 线（14 §4 P2 自绘项） | 系统面板域非战斗实体域，随人格切换 UI（F-122）交付 |
| 6 | E2 水渠（aqueduct）sprite | 随 T-064 物流全量 | 机制数据位先行（era2:era.json logistics.aqueduct），视觉未画 |
| 7 | 占位→人工重绘升级 | 全量 | 以 tools/art 两脚本为底稿（P1/P2 44 件全量可重生成） |
| 8 | E3/E4 美术包 | T-119/T-128（W6/W7） | 首发后按波次 |

## 7. U 盘同步（业主素材统一 U 盘管理）

冻结清单+全部入库素材已打包同步：`E:\game-assets\freeze-v0.1\eraTD-art-freeze-v0.1.zip`（445 条目=本清单 md+presentation 冻结面 63 PNG+game/assets 375 件+许可证快照 4+合成器脚本 2；sha256=`57759284a3a14cebed07fcebb47686e559cdc20e10cb233d568643c57ea98ac6`，zipfile 完整性校验 OK）。E 盘既有 characters/era-banners/scenes 目录未改动（§1 两线口径）。

## 8. 验证记录

执行环境：`NUGET_PACKAGES='D:\nuget'`、`DOTNET_ROOT=/d/dotnet`（AGENTS 环境速查）。

- 逐条目校验（G2.5 口径全目录）：`python tools/ci/asset_validator.py game/assets game/presentation/EraTD/textures` → **files=434 rejected=0 warned=0**（T-094 前 404 → 补 30）。
- 合成器自测：era1 `self-test ok: static_files=20 rejected=0`；era2 `self-test ok: static_files=31 rejected=0 anim_files=1 water-anim frames=2/expect-2`。
- 确定性：两脚本二次生成（临时目录）与工作树逐文件 sha256 比对 **44/44 全等**（era1 20+era2 24）；era2 既有 14 件（T-078 已提交版）重生成零漂移（git 工作树无 diff）。
- 视觉抽查：两代 contact sheet（20+10 件）放大 3× 目检——可辨识性全过；boss-ironhoof 首版甲片铆钉浮空（画布上方）已修正（y 20→29 贴甲面）后重生成。
- **共享工作树干扰披露（T-045/T-078 先例同款）**：run_all 首跑在共享树上被并行 T-093 泳道未提交 WIP（game/src/EraCore/Presentation/ 8 文件触发 T2 无测试映射 FAIL+game/content/ui/）阻断——均非本任务文件。按 24 号 §5 先例采取**隔离执行**：git worktree 检出 HEAD（691bf92）+ T-094 全部文件拷入，隔离树跑完整门禁（T-093 文件零触碰、零迁移；Godot 钉死编辑器经目录联接挂入，--import 预热导入缓存后冒烟）。
- 隔离树门禁结果（T-094 全部文件在场、T-093 WIP 不在场）：

| 门禁 | 结果 | 明细 |
|---|---|---|
| G2 红线 | ✓ | 9/9 PASS（R9 含本清单+E 盘路径，豁免已登记 allowlist.txt） |
| G2.5 资产 | ✓ | files=434 rejected=0 warned=0（404→434=+30 补漏） |
| G1 测试 | **基线既有 13 失败** | 5028 passed/13 failed；失败集与**干净 HEAD 对照树完全一致**（diff 全等取证）：11×Llm 在线/SSE 协议（live 网络端点环境依赖，"服务端返回了无法解析的响应"，24 号 §5 同款先例）+1×HoldoutV1BlindTests 哈希钉（holdout 语料哈希与钉值不符，T-090 域）+1×NarrationBookTests schema 拒绝（T-092 域）；本任务零代码/测试改动，非本任务引入 |
| G3 构建 | ✓ | Release 0 警告 0 错误（TreatWarningsAsErrors=true） |
| G4 冒烟 | ✓ | SMOKE_OK + `ART atlas_tiles=8` + `ART2 atlas_tiles=9 pack_files=15/15` + AUDIO_OK，SHADER ERROR=0 |

- G1 基线失败处置口径：与本任务无关的既有失败如实披露（AGENTS 纪律 10）；干净树对照=第二 worktree 检出 HEAD 不拷 T-094 文件，同过滤器复跑 61 测试 → Failed: 13（与隔离树失败清单逐条 diff 全等）。

## 变更记录

- v1.0（2026-10-15）：T-094 首次成文。实体→素材映射 63 项全表（E1+E2 "无实体无素材"清零）；30 件程序合成补漏（era1_placeholders.py 新建+era2_placeholders.py 增补）；冻结清单 sha256（presentation 63 逐文件+三包 tree digest+留档件）；调色板一致性（E1 23 色/E2 22 色，全库 max 61≤64，白名单零滥用）；冻结声明+W8/升级待做 8 项；U 盘同步 freeze-v0.1。
- v1.1（2026-10-13）：T-125 遗迹映射正式版增补。§2.3/§4.4 跨代遗迹标记 4 PNG（textures/relic/：立碑遗迹/考古挖掘位/纪念馆修复位/核心保留区勾勒）——era 无关域新冻结面，relic_placeholders.py 程序合成（P3，12 色跨代遗迹色板，二次生成 sha256 全等实测），stem 与 RelicMarkerBoard 冻结常量逐一对应（RelicArtMappingTests 实锚）；既有 E1+E2 63 PNG 零触碰（v0.1 冻结面不动）。E4 时代专属素材仍归 T-128（待做 #8 不变）。
