# 29 - R4 视觉叙事与彩蛋视觉表达研究：热门塔防/策略游戏参照

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 研究代理 | R4（视觉素材叙事 + 彩蛋视觉表达专项） |
| 目的 | 为《我在游戏觉醒系统》（像素塔防、六时代远古→古典→工业→现代→未来→深空、觉醒 AI"系统"）的视觉扩充方案提供业界依据：参照游戏的美术量级、视觉叙事手法、演出风格、彩蛋视觉回报先例、抽象/艺术向可行性、长线更新美术供给节奏 |
| 关联文档 | 14-art-license-audit.md；15-t045-art-integration.md；17-ark-video-api-research.md；18-r1-asset-tech.md（§C11 过场三件套先例）；19-r2-easter-eggs.md（彩蛋机制先例）；28-t094-art-freeze.md；主方案 §4 时代体系 / §12 系统流叙事 / §13 美术方案 |
| 可信度标注 | 【官方】= 官方站点/新闻稿/press kit；【多源】= 官方+第三方或两处以上交叉；【单源】= 仅第三方，落地前需复核；【推断】= 由玩法录像/常识估算，非实测 |

---

## 0. 速查结论表

| # | 问题 | 结论 | 可信度 |
|---|---|---|---|
| C1 | 头部塔防用什么讲故事的？ | **没有一款用"重视频过场"**。BTD6/Thronefall 零叙事文本；王国保卫战=关卡间 10~30 s 地图动画+文字（延续 18 号 C11 结论）；明日方舟=VN 立绘+文本（无配音、可跳过、可自动），社区仍抱怨"读起来费劲"。=> 塔防节奏下，叙事主形态是**静态立绘+短动画节点**，不是播片 | 【多源】 |
| C2 | 美术量级基准 | 单机塔防：Thronefall 2 人团队=10 座岛（关）+ 极简素材集即支撑 8~12 h 通关/100 万份销量；长线运营塔防：BTD6 七年 25 塔/17 英雄/80+ 图（年均约 7 次大更新）；明日方舟六年 433 名干员/186+ 套时装（6 周一活动）。=> 本作 6 时代的"量级锚点"：**单机像素塔防每时代 8~15 张专属图 + 全局母题素材复用即可成立** | 【多源】 |
| C3 | 过场怎么插不打断节奏 | 共识三原则：**永远可跳过、重看可回看、玩法检查点前后不强制播片**（18 号 C11 + 本次检索一致：RPG Maker 论坛/Game Developer 均无"长过场"支持证据）；KR 把叙事放在**关卡外**（地图行进+关前文字），PvZ 放在**点击推进的低成本对话**。=> 本作：关卡内只用 ≤3 s 的"叮！"式闪切演出，视频/长演出只放在**时代切换与终章** | 【多源】 |
| C4 | 彩蛋要不要专属视觉资产 | 要，但**梯度化**：最高 ROI 是"已有素材的规则性异变"（FNAF 8-bit 小游戏、DDLC 程序化故障、Doom 秘密墙后的复古整关），近零新增美术而记忆点极高；**专属新素材只投 1~2 个标志性彩蛋**（Undertale/Deltarune 特殊路线级别的独占演出） | 【多源】 |
| C5 | meta 彩蛋的视觉手法 | **打破游戏自身的视觉语法**：DDLC 用乱码/扭曲立绘/假死机蓝屏；OneShot 改桌面壁纸/生成文件（玩家"游戏外"被视觉入侵）；Undertale 把记忆写在存档外让 NPC"觉得见过你"。=> 本作"系统面板"是现成的视觉语法载体：彩蛋=面板 UI 的受控"故障"（字体漂移、边框撕裂、文案乱码后复原），与"觉醒 AI 知道自己是游戏"的设定天然同构 | 【多源】 |
| C6 | "眼前一亮"的构成公式 | 参照 Outer Wilds/呀哈哈：**惊讶 = 可学习的模式库 × 模式被有意打破的一瞬**。彩蛋视觉不需华丽，需"这一帧不该出现在这个游戏里"的错位感（FNAF 像素小游戏之于恐怖监控、Deltarune 雪葬路线的图像变更即此） | 【多源】 |
| C7 | 抽象/艺术向在塔防可行吗 | 可行且有成功先例：Thronefall（极简剪影+轮廓=可读性即美学，100 万份）、Bad North（极简洋馆式低多边形岛屿，Mobile Games Awards GOTY 决赛提名）；PixelJunk Monsters 证明"手绘卡通风"在 TD 长青。=> **TD 品类天然吃"高可读性抽象"**——塔防要一眼分清单位，抽象化反而增益 | 【多源】 |
| C8 | "时代演进"的抽象表达语言 | 三件套：**色彩层积**（Gris 每阶段一主色映射悲伤五阶段；Journey 用色彩引导情绪）+ **形态演化**（Spore 五/六阶段每阶段独立视觉语法，母题形体随阶段演化）+ **母题重复**（Cocoon 用"球中世界"一个符号贯穿全程）。=> 六时代=六张"主色+母题"卡，同一防御塔/遗迹轮廓跨时代变形重复，玩家从视觉层自己读出演进 | 【多源】 |
| C9 | 演出风格：抽象向 vs 具象向 | 两者都有成功案例，但**像素风天然偏抽象向且更抗"美术量不足"**：Defender's Quest（像素+文字过场）因画风被讥"MS Paint"但 DX 版提分辨率后口碑翻盘——**像素 TD 的风险不在抽象，在"分辨率/整洁度"**；具象写实向（Anomaly 系列）靠电影化 3D 撑，非本作资源形态 | 【多源】 |
| C10 | 长线更新美术供给节奏 | BTD6≈7 次/年（每 6~8 周一更：1 新单位+若干图/皮肤）；明日方舟 6 周/活动（4~6 新立绘+1~3 套时装+偶发 1~3 min 动画 PV）；PVZ 融合版（个人开发者）年更 3~4 个大版本+周年庆宣传片。=> 本作"免费自玩"定位适配 **Thronefall 式脉冲**：首发集中量 + 每 1~2 月一个免费内容脉冲（新图/新彩蛋/时代皮肤） | 【多源】 |

---

## 1. 热门参照盘点（研究点 1）

### 1.1 参照总表（16 款/组塔防+策略，热度优先 2024~2026）

| 游戏（年份） | 热度/量级 | 美术量级（可考数字） | 视觉叙事手法 | 演出风格定位 |
|---|---|---|---|---|
| Bloons TD 6（2018~） | 七年长青，v50~v57 持续更新至 2025+，2025-12 出大型 DLC "Frontier Legends" | 25 塔（17 首发+8 后加）/约 17 英雄/80+ 官方图/每塔 16 升级位+6 Paragon | **零剧情文本**：所有"叙事"都在单位升级视觉（Paragon 化=塔的终极形态演出）与更新宣传片里 | 具象卡通 3D，美术全投玩法可读性 |
| 明日方舟（2019~） | 长线运营标杆，2025 年末 CN 服 433 名干员 | 433 干员（每名含精英立绘）/186+ 套时装/主线第 5 章起每章配动画 PV（Yostar Pictures 制作，1~3 min/支） | **VN 演出**：立绘+背景+文本（基本无配音），可跳过/自动/倍速；活动宣发靠动画 PV 放在游戏外 | 具象高精度插画；"游戏内读文本枯燥"是被反复抱怨的痛点（r/arknights 专帖） |
| 王国保卫战系列 + Alliance（2011~2024）+ KR6 Genesis（2025-10-09） | 系列第 5 作 Alliance（2024-09）Steam 好评，KR6 于 2025-10 发售 | 初代即"60+ 种敌人"（官方 press kit）；手绘卡通逐帧 | **关卡间短地图动画+文字引入**（18 号 C11 已核）；Alliance 粉丝考据过场里"矮人全程没出现"——过场是玩家真的会逐帧看的资产 | 具象手绘卡通风（"realistic cartoon"） |
| PVZ 融合版（蓝飘飘 fly，二创，约 2022 末上线） | 2024~2025 中文圈现象级二创，2025-07 办"2.7 周年庆"、更新至 v4.0.x | 全部素材=原版素材改色/拼接+新增融合植物精灵图；个人开发者量级 | **玩法即叙事**：融合配方本身就是"发现时刻"；彩蛋植物=制作组成员形象植物+联动植物，通关特定关卡解锁（BWIKI）；周年庆发布宣传片（游戏外） | 复用素材的拼贴抽象；证明"素材异变"回报极高 |
| Thronefall（2023 EA，2024-10 1.0） | 首月 31.5 万份/$150 万流水，首年 100 万+份（2025-02 官宣） | **2 人团队**、10 座岛（关）、每关预设建筑槽+少量武器/单位 | **零文本零过场**：白天建造/夜晚防御的节拍本身承担节奏叙事；建筑落成动画（网格缩放+粒子+音效）设计成"一键传达力量感" | 抽象极简：限色+剪影+描边，可读性即美学（Game Developer 专访） |
| Bad North（2018） | Mobile Games Awards 年度游戏决赛提名；IGF 2020 参展 | 极简低多边形洋馆式微缩岛屿+程序生成海岸线 | **几乎无叙事 UI**：极简 UI+极简音轨+微缩景观自成一致身份 | 抽象极简（与 Townscaper 同作者 Oskar Stålberg 一脉） |
| 向僵尸开炮（2024，微信小游戏） | 1 亿+ 人玩过（微信塔防榜） | 立项时预研 5~6 种美术风格，用买量数据选定 **Q 版现代风**（"数据最好、爽感反馈最强"，制作人访谈） | **叙事≈0，美术为爽感服务**；买量视频承担全部"世界观表达"（实拍/名场面复刻广告） | 具象 Q 版+合金质感；IAA 商业化驱动 |
| 躺平发育 / 保卫向日葵 / 永远的蔚蓝星球（微信小游戏塔防群，2024~2025） | 躺平发育 1 亿+、蔚蓝星球 1000 万+（微信官方塔防榜） | 小游戏级轻量素材 | 无叙事，靠玩法钩子+社交裂变 | 抽象/功能向 |
| 全境守卫 / 无尽防线（2025-06 微信畅销榜新入榜） | Thronefall-Like 塔防在小游戏平台跑通 | 对 Thronefall 的素材降配复刻 | 继承"白天建造/夜晚防御"零文本节拍 | 抽象极简 TD 已被小游戏市场验证 |
| Loop Hero（2021） | Devolver 发售、像素+meta 叙事代表作 | 复古像素+卡片拼贴（有限素材集） | **世界被巫妖抹除→玩家摆卡"回忆/重建"世界**：机制与叙事同构，全程少量对话文本 | 抽象像素；与本作"像素+系统流叙事"最同形的先例 |
| PixelJunk Monsters（2008~2018 PGM2） | TD 长青系列 | 每图手绘 2D；PGM2 转粘土质感 3D | 无叙事，关卡海报式的手绘村落实景 | 具象手绘卡通（"beautiful art direction"是其口碑支柱） |
| Defender's Quest（2012/DX 2016） | 像素叙事 TD 的先行者 | 像素战场+**2D 立绘+文本的过场**（VN 式） | 过场立绘+文字推进剧情（TD 里少见的"有故事的像素"） | 抽象像素；画风两极（"clean lines" vs "MS Paint"），DX 版提分辨率后翻盘——**像素 TD 叙事可行的直接先例，教训是分辨率与整洁度** |
| Anomaly 系列（2011~2013） | "逆向塔防"电影化代表 | 3D 现代军事+城市废墟，色调节制（红/蓝主导） | 关卡前简报式过场 | 具象写实向——靠电影化 3D 撑，不适合本作资源形态 |
| 音游塔防类（Rune Rhythm Realm / itch.io 节奏 TD tag，2024~2026） | **无爆款**：Steam 仅零星小品，社区原型讨论普遍反馈"不 click" | 小品量级 | 节奏对齐压过策略决策，两类心流冲突 | —（反面参照：玩法融合需单轴优先） |
| Mindustry（2017~，本项目逆向基底） | 开源长青 | 单人开发的像素素材集 | 战役叙事极简（少量对话+章节地图） | 抽象像素工厂（项目内部事实，见仓库逆向摘要） |
| Kingdom Rush "AllStars" | **查无此官方作**：Ironhide 无 All Stars 命名产品；疑为 Alliance（2024）或 Legends of Kingdom Rush 的讹传，建议视觉方案按 Alliance 对齐 | — | — | — |

### 1.2 手法分层：叙事资产在塔防里的四种插入位置

1. **关卡外**（王国保卫战：地图行进+关前 10~30 s 动画/文字；BTD6 更新宣传片）——最不打断；本作"时代切换"对应此层。
2. **关卡内的微演出**（Thronefall 建筑落成动画、Paragon 升级演出，≤3 s，不可跳也无须跳）——塔防的正反馈语言。
3. **结算/检查点**（PvZ 疯狂戴夫点击推进对话、Defender's Quest 章间 VN 过场）——玩家已松手，可容纳 30 s~2 min 的立绘+文本。
4. **大节点播片**（明日方舟动画 PV 放游戏外宣发而非游戏内；KR5 章末过场）——游戏内长播片在塔防里几乎没有正面先例，唯一安全位是**终章真相揭示**（本作 E6 已规划播片对话，正好落在唯一安全位上）。

静态插画 vs 动态视频比例：全部成功样本中，**"静态/微动"资产占绝对多数（>90%）**——明日方舟 433 干员立绘 vs 主线 8 章中约 4~5 支 PV（PV 还主要在游戏外）；Thronefall/BTD6 视频过场为零。【多源+推断】

---

## 2. 彩蛋的视觉回报（研究点 2）

### 2.1 八组先例与视觉手法

| 先例 | 彩蛋视觉表达 | 新增美术成本 | 回报特征 |
|---|---|---|---|
| Undertale 屠杀线 / Deltarune 雪葬(Weird)路线 | 路线开启瞬间的**独占音效提示**、全路线图像变暗、后续章节持续蔓延；Toby Fox 在补丁里**持续修改该路线图像**，社区逐帧追踪每次变更（当 live service 追更） | 低（复用素材异变+少量独占帧） | "这一帧不该在这个游戏里"的错位感=最强记忆点；社区级解谜事件（Reddit megathread） |
| FNAF 系列 | 隐藏 8-bit 像素小游戏（双击 Cam 8 海报等入口）；找齐全部小游戏才解锁好结局；Sister Location 通关 Custom 夜后解锁 6 段连播过场 | **极低**（刻意用比本体更廉价的复古像素风） | 廉价风=年代闪回的"语法破格"；撑起整个系列的社区考据生态 |
| 塞尔达·旷野之息 呀哈哈（900 个） | 约 10 余种固定互动模式的小石头/石圈/苹果阵；终奖"Hestu 的礼物"刻意寒酸（详见 19 号 §1.1） | 单模式素材复用 900 次 | 模式库×密度；收集动机与终奖脱钩 |
| Doom（1993/2016/Eternal） | 秘密墙/隐藏拉杆→**整段可玩的复古 DOOM 关卡**藏在现代游戏里；开启秘密时有可听见的开门声线索 | 中（复用经典关卡资产） | "整关级彩蛋"的标杆；听觉线索让秘密可被"发现"而非"碰见" |
| Outer Wilds（2019） | 无彩蛋专属资产；**知识即进度**——"豁然开朗"由信息拼图触发，演出只是把镜头给到玩家已经理解的那个天体 | 零 | 证明"啊哈"可以纯由认知达成；彩蛋视觉只需**给发现瞬间一个郑重的定格**（快照/音停/慢镜） |
| DDLC | 程序化故障：文本乱码、立绘扭曲（Act 2 进社团屋有 1/3 概率出现扭曲立绘）、假系统崩溃屏 | **近零**（程序化视觉） | meta 恐怖的全部视觉=打破本作视觉语法；学界专文研究（Univ. of Glasgow Press Start） |
| OneShot | 解谜线索写在**游戏外**：改桌面壁纸、生成文件、写注册表，事后还原 | 零美术（系统层操作） | "游戏知道我"的最直白视觉证据；玩家截图传播率极高 |
| 星际拓荒式总结 | —（同 Outer Wilds） | — | 与 19 号 §1.3 的"重构自主性"结论一致：彩蛋演出永远不要替玩家说出真相 |

### 2.2 ROI 结论（彩蛋视觉三档梯度）

- **T0 语法异变（0 新素材，程序化）**：UI 故障/乱码/字体漂移/假崩溃/存档外记忆（Undertale/DDLC/OneShot 手法）——本作系统面板天然适配，**默认档**。
- **T1 素材复用异变（改色/拼接/年代降级）**：FNAF 8-bit 小游戏、PVZ 融合版融合植物、Doom 复古关——像素游戏改色成本≈0，**主力档**。
- **T2 专属新资产（仅 1~2 处）**：Deltarune 级别的独占演出帧/短动画——只投给贯穿式大彩蛋（对应 19 号候选 E-01"互指残卷"之类），**点睛档**。

**meta 彩蛋视觉公式**：破坏玩家已习得的视觉语法（面板、字体、图标、颜色规则），且破坏必须"受控可复原"——DDLC 不可复原带来了传播，但对免费自玩定位的口碑风险高；本作遵守主方案 §12.1"不搞背叛骗局"，取"惊而不吓、异而后复"。【多源】

---

## 3. 抽象/艺术向整体概念（研究点 3）

### 3.1 七组先例

| 作品 | 抽象语言 | 对本作的直接启示 |
|---|---|---|
| Limbo（2010） | 单色、光/负空间/阴影三件套，零文本叙事 | "少"本身可以是主视觉；光照层级=像素配色表的排布逻辑 |
| Inside（2016） | "eschewing language in favour of evocative imagery"——用几何/灯光/表面三要素做极简 | 时代氛围差可用**色调+几何母题**拉开，不靠物件数量 |
| Gris（2018） | 每关卡=悲伤五阶段之一=一种主色；灰度起步、色彩随进程**层积**回归（学术研究证实按 Kübler-Ross 模型设计） | **六时代=六主色的最有力依据**：色彩层积天然表达"进程/演进"且几乎零边际成本 |
| Journey（2012） | "We tried to make the colors guide the players' emotions"（CGW 开发者原话）；沙丘渐变色=情绪曲线 | 主色之外用**渐变**承载同一时代内的进程（时代内 10 关的色相微移） |
| Cocoon（2023） | 无台词无文本；单一符号"球中世界"贯穿；LIMbo/INSIDE 主玩法设计师操刀；r/gamedev 共识"dial back on everything and still be awesome" | **母题重复**的威力：一个可变形符号（塔/遗迹/核心）串起全部时代 |
| Spore（2008） | 五/六阶段（细胞→生物→部落→文明→太空），每阶段独立美术语法与镜头语言 | "时代切换"的结构先例：换时代=换视觉语法（不只是换贴皮）；AWN 2008 专文 |
| Loop Hero（2021）+ Thronefall（2023）+ Bad North（2018） | 像素/低多边形抽象在策略-塔防品类的成功案例（详见 §1） | **塔防品类做抽象向不仅可行，且"高可读性抽象"是品类刚需**；Loop Hero 证明像素+meta 叙事同体成立 |

### 3.2 "时代演进"的抽象视觉语法（提炼）

1. **色彩层积**：每时代一张主色卡（含 UI 面板边框色/文字强调色/地形基调），时代内 10 关做同色相微渐变（Journey 手法）；终章深空回归 E1 远古主色的**补色/余烬变体**，形成首尾闭环（Gris 式回归）。
2. **形态演化**：选 1~2 个**演化母题**（建议：主基地"核心塔"与"遗迹拱门"），同一轮廓在六时代按"木石→铜铁浮雕→铆接钢→混凝土/玻璃→纳米流态→暗物质星环"变形重复——玩家第 3 个时代起会主动预测下一形态，预测即参与叙事（Spore 手法，成本=6 套变形而非 6 套无关设计）。
3. **母题重复**： Cocoon 手法——所有时代图标/装饰纹样从同一几何母版派生（例：三角=远古矛尖→古典山墙→工业桁架→现代播放键→未来力场→深空裂缝）。
4. **语法破格预留给彩蛋与"系统"**：meta 演出（§2）就是唯一允许打破上述三语法的时刻——正因为日常语法严格，破格才有威力（DDLC 的故障之所以吓人，是因为日常界面太"正常"）。

---

## 4. 数据汇总（研究点 4）

| 维度 | 数据点 | 来源与可信度 |
|---|---|---|
| BTD6 更新节奏 | 2018 发布至 2025 年 v50+（7 年 50+ 次主要版本，年均约 7 次）；每更典型=1 新塔/英雄+若干地图+皮肤+平衡；2025-12 大型 DLC Frontier Legends（新 Paragon+地图） | 【多源】Bloons Wiki update history、r/btd6 v57 notes、ixbt |
| BTD6 存量 | 25 塔/约 17 英雄/80+ 官方图/每塔 16 升级位/6 Paragon | 【多源】Bloons Wiki/Fandom |
| 明日方舟存量 | CN 服 2025 年末 433 名干员（Global 411）；6.5 周年时 186+ 套时装在售/复刻；Amiya 单角色 6 套时装（2025-12） | 【多源】Arknights Terra Wiki（Operator/Outfit 词条） |
| 明日方舟叙事产能 | 主线 8 章中第 5 章起每章配动画 PV（Yostar Pictures，1~3 min/支）【单源：note.com 考据】+ 大型活动常配 PV【多源案例】；活动周期约 6 周/个（社区共识） | 【单源+多源】混合，标注使用 |
| Kingdom Rush 量级 | 初代 60+ 种敌人（官方 press kit）；系列 5+1 作（KR6 Genesis 2025-10-09 发售）；Alliance（2024-09）为最新主线作，"Kingdom Rush AllStars"查无官方产品 | 【官方+多源】 |
| Thronefall 量级 | 2 人团队；10 座岛/8~12 h 通关；首月 31.5 万份、毛收入约 $150 万、退货率 6.9%；首年 100 万+份（2025-02 官宣+最终更新追加 12 个小游戏模式）；1.0 于 2024-10-11 | 【多源】GameDiscoverCo 深度报道、Steam 公告 |
| PVZ 融合版节奏 | 个人开发者；2025 年内 v3.1→v3.5→v4.0.x 多个大版本；3.1 新增"深渊"S2 共 30 关；2025-07-04 发布 2.7 周年庆版宣传片（据此推算首发约 2022 年末【推断】） | 【多源】萌娘百科、BWIKI、B 站动态 |
| 微信小游戏塔防 | 官方塔防榜：向僵尸开炮 1 亿+、躺平发育 1 亿+、永远的蔚蓝星球 1000 万+ 人玩过；2025-06 Thronefall-Like《全境守卫》新入畅销榜；行业媒体定调"塔防是小游戏破局关键品类"（2025-01） | 【多源】微信小游戏官方榜、bianews、36kr |
| 向僵尸开炮美术决策 | 立项预研 5~6 种美术风格，以买量数据选定 Q 版现代风（"数据最好、爽感反馈最强"） | 【多源】cgames 制作人对谈、nadianshi/知乎复盘 |
| 过场时长基准 | **无正式统计调查**；设计共识=可跳过优先、重复观看必可跳、长度上限取决于沉浸维持而非固定秒数（RPG Maker 论坛共识/Game Developer 经典文章"很短的过场反而强化节奏"）；塔防内正演出的经验值=≤3 s 微演出 | 【多源】论坛+行业文章（无量化调查，如实标注） |
| 静态 vs 动态比例 | 成功样本中叙事资产 >90% 为静态立绘/插画/微动画（明日方舟 433 立绘 vs 约 4~5 支主线 PV；KR/PvZ/Thronefall 游戏内视频过场≈0） | 【多源+推断】 |

---

## 5. 对本作的建议要点（供视觉扩充方案整合，不构成冻结决策）

1. **时代识别卡（六张）**：每时代定"主色+演化母题形态+UI 面板皮肤+系统语气字体"四件套（§3.2 语法）；主色卡建议低饱和起步、逐时代加饱和与色域，终章用 E1 补色闭环（Gris/Journey 依据，C8）。
2. **演出分级**：R0 战斗内"叮！"≤3 s 闪切（BTD6/Thronefall 语言）；R1 时代切换 15~30 s 定格蒙太奇（母题形变演出，静态帧+Ken Burns 式运镜即可，KR 关卡间动画定位）；R2 终章唯一长播片 ≤90 s（E6 已规划播片对话，落在塔防唯一安全位，18 号 C11+本次 C3）；全部可跳过、可在图鉴回看。
3. **插画量级锚点**：每时代 1 张大主题插画+2~3 张关键节点立绘（系统人格化立绘优先），全局约 20~30 张静态资产+6 次 R1 演出即可支撑六时代叙事（对照 Thronefall 2 人量级与 C2 锚点）；用 17 号 Seedance 管线出 R1/R2 草稿，18 号 ogv 管线落地。
4. **彩蛋视觉走三档梯度（§2.2）**：T0 系统面板语法异变为默认（对应 19 号"系统记得你"天然彩蛋资产）；T1 改色/年代降级素材异变做密度（如某关敌人全员变成 E1 像素风闪回=FNAF 手法）；T2 仅 1~2 处独占演出投给贯穿式彩蛋。所有破格"受控可复原"（对齐 §12.1 不背叛）。
5. **抽象方向**：本作像素风+TD 品类双因素都指向"高可读性抽象"（C7/C9）：优先把预算花在**轮廓/剪影可读性、色彩层积、母题一致性**三处，而非具象写实细节；Defender's Quest 的教训=像素必须锁分辨率整洁度（本项目已有全局 Nearest，15 号）。
6. **更新节奏**：免费自玩定位参照 Thronefall 脉冲式：首发六时代+随后每 1~2 月一个免费内容脉冲（新图/新彩蛋/时代皮肤），不走明日方舟式 6 周重料周期（C10）。
7. **不采**：音游塔防融合（无成功先例，C 表外负面参照）；游戏内高频长播片；不可复原的惩罚性 meta 演出。

---

## 6. 检索记录（2026-10-13，WebSearch/web_search_prime 为主，两次命中 429 限流后改词重试）

| # | 检索问题 | 关键命中 |
|---|---|---|
| Q1 | BTD6 更新节奏/内容量 | Bloons Wiki update history、r/btd6 v57、ixbt（Frontier Legends） |
| Q2 | 明日方舟 VN/立绘/PV | r/arknights cutscene 批评帖、Terra Wiki、note.com 主线 PV 考据 |
| Q3 | Kingdom Rush Alliance 2024 | GamesPress、Steam 讨论、Dave Chats Games、r/kingdomrush 过场考据帖 |
| Q4 | Thronefall 极简设计 | Game Developer 专访（剪影/描边/落成动画） |
| Q5 | Thronefall 销量/关卡数 | GameDiscoverCo 深度报道（31.5 万/月）、1.0 十关、1M 官宣 |
| Q6 | Bad North 风格/奖项 | Subpixel Films 访谈、Mobile Games Awards 决赛提名、IGF 2020 |
| Q7 | PVZ 融合版（中文） | 萌娘百科、BWIKI（彩蛋植物）、B 站动态（深渊 S2/30 关）、游民星空 |
| Q8 | 微信小游戏塔防（中文） | 微信官方塔防榜、bianews、36kr（全境守卫）、stcn（无尽冬日长青） |
| Q9 | 向僵尸开炮美术决策（中文） | cgames 制作人对谈（5~6 风格测出 Q 版现代）、知乎复盘、GameLook |
| Q10 | 音游塔防 | Steam Rune Rhythm Realm、itch.io tag、r/gamedesign 原型讨论（负反馈） |
| Q11 | Undertale/Deltarune 特殊路线 | Deltarune Wiki（Weird Route）、r/Deltarune 图像变更追踪帖、megathread |
| Q12 | FNAF 隐藏小游戏 | FNaF Wiki（Cam 8 双击海报/好结局）、Sister Location 六连播过场、r/fivenightsatfreddys "为什么用 8-bit" |
| Q13 | Doom 秘密墙 | Steam 指南（隐藏拉杆→复古关）、Bethesda "5 Secrets of DOOM"、Doom Wiki |
| Q14 | Outer Wilds 知识驱动 | Ludo Node "Metroidbrainia"（Designing for the 'Aha!' Moment）、r/patientgamers |
| Q15 | 塞尔达呀哈哈 | 沿用 19 号 §1.1（GDC 2017 三角法则/900 种子/Hestu 礼物） |
| Q16 | DDLC 故障 meta | Glasgow Press Start 学位论文、DDLC Wiki Easter Eggs（1/3 扭曲立绘）、Reddit 四次破壁清单 |
| Q17 | OneShot 游戏外视觉 | Siliconera、Wikipedia、Steam 讨论（壁纸改回/注册表） |
| Q18 | Limbo/Inside 极简 | girlsongames 艺术分析（光/负空间/阴影）、r/gamedesign "Inside and Minimalism" |
| Q19 | Gris 色彩叙事 | Lamerichs、Atlantis Press 论文（Kübler-Ross 模型证实）、Substack "emotional architecture" |
| Q20 | Journey 色彩引导 | CGW 2012 开发者原话、Gemsbok 生态批评、Zucconi 沙丘着色技术文 |
| Q21 | Cocoon 符号化 | r/gamedev（dial back and still be awesome）、Thinky Games、Game Designer 深访 |
| Q22 | Spore 阶段演进 | SporeWiki Game stages、AWN 2008 "The Evolution of Spore" |
| Q23 | Loop Hero 像素 meta | Game Developer postmortem、Medium 设计拆解、Devolver 官方页 |
| Q24 | TD 艺术向先例 | GamingBolt/Gaming Nexus（PixelJunk）、Sprites and Dice+Steam（Defender's Quest 画风争议）、11 bit（Anomaly） |
| Q25 | Kingdom Rush AllStars 查证 | Ironhide 官网 games 页（无此作；有 KR6 Genesis 2025-10-09） |
| Q26 | 过场时长/跳过 | RPG Maker 论坛、Game Developer 2002、r/gamedesign（无量化调查，如实标注） |
| Q27 | KR 系列量级 | 官方 press kit（60+ 敌人）、Wikipedia |

## 附录：来源链接清单（正文按节引用）

**§1 塔防参照**
- Bloons Wiki update history: https://www.bloonswiki.com/Update_history:Bloons_TD_6
- r/btd6 v57 更新说明: https://www.reddit.com/r/btd6/comments/1wzgvfn/bloons_td_6_v570_update_notes/
- ixbt（v52+Frontier Legends DLC）: https://ixbt.games/en/news/2025/12/07/bloons-td-6-polucila-masstabnoe-obnovlenie-520-i-dlc-frontier-legends-c-novym-paragonom-kartami-i-sarami.html
- Bloons Fandom（内容量）: https://bloons.fandom.com/wiki/Bloons_TD_6
- Arknights Terra Wiki Operator: https://arknights.wiki.gg/wiki/Operator ；Outfit: https://arknights.wiki.gg/wiki/Outfit
- r/arknights 过场批评: https://www.reddit.com/r/arknights/comments/13g9kwe/a_criticism_of_arknights_story_cutscenes ；阅读体验: https://www.reddit.com/r/arknights/comments/ybd0gg/i_love_arknights_story_but_actually_reading_it
- GamesPress KR5 Alliance: https://www.gamespress.com/5-Reasons-to-Watch-for-Kingdom-Rush-5-Alliance ；Dave Chats Games: https://davechatsgames.wordpress.com/2024/09/14/kingdom-rush-5-alliance-review ；r/kingdomrush 过场考据: https://www.reddit.com/r/kingdomrush/comments/1g0t104/ironhide_where_are_the_dwarves_in_the_ending_in
- KR 官方 press kit（60+ 敌人）: https://www.kingdomrush.com/kingdom-rush-presskit ；Ironhide 作品页（KR6 Genesis）: https://www.ironhidegames.com/games
- Game Developer·Thronefall 极简专访: https://www.gamedeveloper.com/design/mastering-minimalism-and-layering-complexity-with-strategy-game-thronefall
- GameDiscoverCo·Thronefall 销量: https://newsletter.gamediscover.co/p/deep-dive-how-thronefall-went-minimal ；1.0 更新（12 小游戏模式）: https://steamcommunity.com/app/2239150/eventcomments/4329727208140091872
- Bad North 访谈: https://medium.com/subpixelfilms-com/a-minimal-brand-of-madness-oskar-st%C3%A5lberg-and-richard-meredith-on-the-development-of-bad-north-514d5cf1a7a1 ；MGA 决赛: https://www.facebook.com/rawfury/posts/2823750004404277 ；IGF: https://igf.com/entry/2020
- PVZ 融合版·萌娘百科: https://mzh.moegirl.org.cn/植物大战僵尸融合版 ；BWIKI 彩蛋植物: https://wiki.biligame.com/pvzrh/肥料 ；B 站动态（3.1 深渊）: https://www.bilibili.com/opus/1134038036652228610
- 微信小游戏塔防榜: https://xiaoyouxi.qq.com/rankdetail/114.html ；bianews 塔防破局: https://www.bianews.com/news/details?id=202900 ；36kr 全境守卫: https://m.36kr.com/p/3340689796364801 ；stcn 长青密码: https://stcn.com/article/detail/3415727.html
- cgames 向僵尸开炮美术对谈: https://www.cgames.com/contents/2/6076.html ；知乎复盘: https://zhuanlan.zhihu.com/p/17978710715
- PixelJunk Monsters: https://gamingbolt.com/pixeljunk-monsters-ultimate-hd-pc-review ；PGM2: http://www.nintendoworldreport.com/review/47647/pixeljunk-monsters-2-switch-review
- Defender's Quest: https://spritesanddice.com/reviews/defenders-quest-review-mashup-old-and-new ；DX: https://www.gamingnexus.com/Article/5704/Defendere28099s-Quest-Valley-of-the-Forgotten-DX
- Anomaly: https://11bitstudios.com/games/anomaly-warzone-earth
- 音游塔防: https://store.steampowered.com/app/3912670/Rune_Rhythm_Realm/ ；https://itch.io/games/tag-rhythm/tag-tower-defense

**§2 彩蛋**
- Deltarune Weird Route: https://deltarune.fandom.com/wiki/Weird_Route ；图像变更: https://www.reddit.com/r/Deltarune/comments/1l6z7z5/weird_route_imagery_has_been_changed_again ；megathread: https://www.reddit.com/r/Undertale/comments/preuec/alternate_snowgrave_route_chapter_2_megathread
- FNaF3 Minigames: https://fivenightsatfreddys.fandom.com/wiki/Minigames_(FNaF_3) ；SL 过场: https://freddy-fazbears-pizza.fandom.com/wiki/End-of-Night_Minigames_(FNaF:_SL) ；8-bit 讨论: https://www.reddit.com/r/fivenightsatfreddys/comments/kwaaeq/why_does_scott_use_8bit_mini_games_and_cutscene
- Doom 秘密: https://steamcommunity.com/sharedfiles/filedetails/?id=683752326 ；https://bethesda.net/en-EU/news/5-secrets-of-doom-1 ；https://doom.fandom.com/wiki/Easter_egg
- Metroidbrainia/Aha 设计: https://ludonodestudios.medium.com/the-rise-of-metroidbrainia-designing-knowledge-gated-puzzle-games-02300a10df2e ；r/patientgamers: https://www.reddit.com/r/patientgamers/comments/1gpjf3h/outer_wilds_what_does_knowledge_is_the_only
- DDLC 学位论文: https://press-start.gla.ac.uk/index.php/press-start/article/view/221 ；Wiki 彩蛋: https://doki-doki-literature-club.fandom.com/wiki/Easter_Eggs ；破壁清单: https://www.reddit.com/r/DDLC/comments/83pbr4/an_extended_list_of_all_the_fourthwallbreaking
- OneShot: https://www.siliconera.com/solve-oneshots-puzzles-must-leave-game ；https://en.wikipedia.org/wiki/OneShot ；https://medium.com/@austin.bijumon/oneshot-a-story-that-speaks-directly-to-the-player-d1f762adb851

**§3 抽象向**
- Limbo 艺术分析: https://www.girlsongames.ca/2017/04/27/art-examination-playdead-limbo ；Inside 极简: https://www.reddit.com/r/gamedesign/comments/4w3me0/game_array_playdeads_inside_and_minimalism
- Gris: https://nicollelamerichs.com/2021/02/22/game-theory-gris-and-grief-in-games ；https://www.atlantis-press.com/article/125967805.pdf ；https://www.researchgate.net/publication/357706939_The_Embodiment_of_Kubler-Ross_Model_through_Game_Elements_in_GRIS_and_Its_Impact_on_Player_Experience
- Journey: https://www.cgw.com/Publications/CGW/2012/Volume-35-Issue-4-June-July-2012/Mysterious-Journey.aspx ；https://www.alanzucconi.com/2019/10/08/journey-sand-shader-1 ；https://epiloguegaming.com/visualizing-the-narrative-journey
- Cocoon: https://www.reddit.com/r/gamedev/comments/1esyhol/the_game_cocoon_is_an_inspiring_showcase_of ；https://thinkygames.com/news/cocoon-the-world-bending-puzzle-game-from-geometric-interactive-is-out-now ；https://www.gamedeveloper.com/design/the-challenges-of-laying-worlds-upon-worlds-in-puzzle-game-cocoon
- Spore: https://spore.fandom.com/wiki/Game_stages ；https://www.awn.com/animationworld/evolution-spore
- Loop Hero: https://www.gamedeveloper.com/design/postmortem-loop-hero ；https://medium.com/@sacitsivri/game-design-breakdown-loop-hero-4a86d55142b8

**§4 过场时长**
- RPG Maker 论坛: https://forums.rpgmakerweb.com/threads/how-long-is-too-long-for-cutscenes.155837/page-2
- Game Developer 2002: https://www.gamedeveloper.com/design/better-game-design-through-cutscenes
- r/gamedesign 不可跳过: https://www.reddit.com/r/gamedesign/comments/1azr8m6/unskippable_cutscenes_are_bad_game_design

> 检索执行注记：WebSearch 服务在 Q11 后备查询与 Q17 首查中出现两次 429 限流，均换词重试成功；中文条目（Q7/Q8/Q9）经 web_search_prime 中文查询命中。所有"【推断】"标注项未做二次复核，落地前如需精确数字（如明日方舟主线 PV 确切支数、PVZ 融合版首发日期）建议按附录链接复核。
