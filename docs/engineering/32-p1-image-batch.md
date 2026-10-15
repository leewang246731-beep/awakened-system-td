# 32 · P1 叙事图批生产报告（27 张成品）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-15 |
| 任务 | T-148 前置素材批（P1 叙事图批生产——本次为业主指令的 27 张子集，非 31 号方案全量 35 张） |
| 状态 | **完成**——27/27 成品、27/27 自审通过、0 张 needs-review |
| 依据 | 31 号方案 v2.0（业主已批，d87d5e8）；29 号 R4（抽象轴/母题链）；30 号 R5（风格一致性五层/单价）；story-e1.json / story-e2.json / hooks.json（叙事文案实锚） |
| 管线 | vibeapi 中转 `https://www.vibeapi.cn/v1/images/generations`，模型 gpt-image-2；密钥 `config/secrets.local.json`（不入库不入日志） |
| 归档 | `E:\game-assets\expand-v1\P1\{banners,story,hooks}\`（U 盘，含 `_att` 尝试帧与 `tools` 复现脚本）；manifest 双份：E 盘 `P1\manifest.json` + 入库副本 `game/assets/expand-v1/P1-manifest.json` |
| 入游戏 | **否**——本批只生产+归档+清单；工程接线（image_card/DialogueLine 配图位/记忆闪回）属 T-152 后续 |

> **给业主的三句话摘要**：①27 张图全部产完并通过逐张视觉自审（真跑审查模型，非抽查）；②总生成 31 次（27 张首过 + 3 次网络超时重试 + 1 次连通性测试），估算成本 ≈¥7.46，远低于 P1 批预算下限；③全部图共用"时代色板 hex + 守望塔母题 + 层积抽象风格尾缀"三件套，与六时代视觉身份卡（31 号 §1.2）对齐。

---

## 一、成品总览

| 类目 | 数量 | 规格 | 通过 | needs-review |
|---|---|---|---|---|
| 章节题图 banners | 7 | 1536×1024 | 7 | 0 |
| 叙事插画 story | 15 | 1024×1024 | 15 | 0 |
| 跨时代钩子 hooks | 5 | 1024×1024 | 5 | 0 |
| **合计** | **27** | — | **27** | **0** |

**成本台账**（业主实报实销口径）：

| 项 | 次数 | 单价（估） | 小计 |
|---|---|---|---|
| 1024² 生成（story 15 + hooks 5 + 首过重试 3 + 连通性测试 1） | 24 | ¥0.21 | ¥5.04 |
| 1536×1024 生成（banners 7，按 1.5× 面积估） | 7 | ¥0.315 | ¥2.21 |
| **合计** | **31 次调用** | — | **≈¥7.46** |

- 单价依据 R5 §4.1【多源】gpt-image-2 ≈¥0.21/张（1024²）；实报以 vibeapi 账单为准。
- 重试 3 次全部是**中转网络读超时**（传输层失败，未出图不计费口径待账单核对），非质量否决——同 prompt 重发即成功。

## 二、生产纪律执行情况

1. **风格锚（R5 §9 五层落地）**：每张 prompt 首句写死时代色板 hex（E1 `#C9915C/#6B7A5A/#E07B39`；E2 `#4E8A6E/#B3593A/#E8E2D0`；钩子批另引 E3~E6 色板）+ 时代母题（E1 木望楼/矛尖三角；E2 石堡箭楼/山墙+编钟；E3 桁架+齿轮；E4 雷达塔/播放键；E5 力场弧/六边晶格；E6 星门环+裂缝）+ 全批共享风格尾缀 `16-bit pixel art adjacent abstract, layered color strata style` + `no text` 反乱码指令。
2. **题图贯穿要素**：7 张题图均含"该关地标母题 + 塔防防线剪影 + 空中全息系统面板（示意框/扫描线/几何徽记，无字符）"。
3. **钩子"下一时代母题幽灵"**：5 张钩子各埋下一时代母题残影（E1 壁画→E2 山墙幽灵；E2 残页→E3 桁架幽灵；E3 图纸→E4 雷达幽灵；E4 示波器→E5 力场幽灵；E5 波形→E6 星门幽灵），内容实锚 hooks.json 五条目。
4. **自审（反造假）**：每张生成后经 `mcp__4_5v_mcp__analyze_image` 视觉模型**逐张真跑**（27/27，非抽样）：四项判据=主题符合 brief / 色板接近 hex / 无可读文字乱码 / 无明显 AI 痕迹。链路=成品上传临时图床（litterbox 1h 过期，仅作审查中转）→ 远程视觉审查 → 记录 VERDICT。**全部 PASS，重试上限（≤2）未触发质量重生成。**
5. **manifest（21 号 D21-4 纪律）**：每条含 id/文件/prompt 全文/模型/重试次数/自审结论/生成次数；入库副本 `game/assets/expand-v1/P1-manifest.json`。

## 三、缩略览表（27 张：文件名 / 内容 / 自审）

### 3.1 章节题图（banners/，1536×1024）

| 文件 | 内容 | 自审 |
|---|---|---|
| banner-e1-l1-awakening-grove.png | E1-L1 觉醒林地：森林边缘黎明+将熄篝火+木栅防线与木望楼+断矛 | ✓ |
| banner-e1-l2-dry-river-pass.png | E1-L2 枯河隘口：干裂河床穿峡谷+岭上防线+地平线兽道扬尘 | ✓ |
| banner-e1-l3-stonewhisper-vale.png | E1-L3 石语河谷：崖壁几何岩刻壁画（西壁锚点）+河谷防线 | ✓ |
| banner-e1-l4-twin-hillock-line.png | E1-L4 双丘烽线：双丘烽火平行烟层+丘间拉开的防线 | ✓ |
| banner-e2-l1-first-waters.png | E2-L1 水轮初鸣：大河木水轮+渡槽拱初起+石堡箭楼防线 | ✓ |
| banner-e2-l2-carriage-roads.png | E2-L2 车道与水渠：马车道+旱桥水渠+梯田层积+陶顶驿亭 | ✓ |
| banner-e2-l3-scrolls-and-spear.png | E2-L3 残卷与执戈者：大理石遗迹发掘场+执戈守望者+出土残卷 | ✓ |

### 3.2 叙事插画（story/，1024×1024）

| 文件 | 内容（幕文案锚） | 自审 |
|---|---|---|
| story-e1-act1-humiliation-1.png | E1 一幕 受辱：兽群暗色层涌过破碎东篱（"兽群踏碎了营地东篱"） | ✓ |
| story-e1-act1-humiliation-2.png | E1 一幕 受辱余波：断弓+坠矛+逃散猎户（"头一次丢下猎弓逃散"） | ✓ |
| story-e1-act2-awakening-1.png | E1 二幕 觉醒：宿主跪地全息面板绽放（"绑定完成，叫我零"） | ✓（重试 1 次·网络） |
| story-e1-act2-awakening-2.png | E1 二幕 证明：断粮箭塔被系统高亮+民夫送补（"民夫已经在路上了"） | ✓ |
| story-e1-act3-face-slap-1.png | E1 三幕 打脸：兽潮撞满编防线首兽倒地（"箭塔是满的，民夫在线上"） | ✓（重试 1 次·网络） |
| story-e1-act3-face-slap-2.png | E1 三幕 胜利：朝阳木望楼+箭羽斜列+兽影退散（"反推开始"） | ✓ |
| story-e1-opening.png | E1 开场白：夜林余烬+攥断矛醒来+故障闪的面板残片（"信号微弱"） | ✓ |
| story-e1-tease.png | E1 卡点预告：夜岭蒙皮巨兽拖撞木+云中山墙幽灵（E2 撞角预警） | ✓ |
| story-e2-act1-humiliation-1.png | E2 一幕 受辱：高使节嘲弄矮墙+火矢痕天层（"边陲小城也配叫城邦"） | ✓ |
| story-e2-act1-humiliation-2.png | E2 一幕 受辱：火矢雨落木郊+半途水渠脚手架暴露 | ✓ |
| story-e2-act2-innovation-1.png | E2 二幕 觉醒：大水轮湍流转动+轴传工坊（"用水力代替人力"） | ✓（重试 1 次·网络） |
| story-e2-act2-innovation-2.png | E2 二幕 革新：水驱磨站箭头阵列（"箭簇第一次不需要人力打磨"） | ✓ |
| story-e2-act3-face-slap-1.png | E2 三幕 名场面：火矢如雨 vs 越战越亮的大理石白墙+史官记录 | ✓ |
| story-e2-act3-face-slap-2.png | E2 三幕 收束：使节车驾调头+天空编钟涟漪（"账记在城墙上"） | ✓ |
| story-e2-opening.png | E2 开场白：新时代水汽+界碑+已转的水轮+天边火矢烟（"水与铜"） | ✓ |

### 3.3 跨时代钩子（hooks/，1024×1024）

| 文件 | 内容（hooks.json 实锚 / 埋的下一时代母题幽灵） | 自审 |
|---|---|---|
| hook-e1-mural-airship.png | E1 谷壁剥落壁画：腹中绘星辰的浮空船（埋 E2 山墙+编钟幽灵） | ✓ |
| hook-e2-prophecy-page.png | E2 渠底淤层捞出的半页异纸：无字母的符号在发光（埋 E3 桁架+齿轮幽灵） | ✓ |
| hook-e3-impossible-blueprint.png | E3 工坊铆铁台上的旧图纸：角落出现雷达碟+播放键水印（埋 E4 幽灵） | ✓ |
| hook-e4-satellite-signal.png | E4 雷达站示波器：电网蓝屏上脉动波形（埋 E5 力场弧+六边晶格幽灵） | ✓ |
| hook-e5-echo-whisper.png | E5 低语化为光之波形：面板残片带异样批注（埋 E6 星门环+裂缝幽灵） | ✓ |

## 四、口径说明与遗留

1. **15 张叙事插画的分配**：业主清单枚举（三幕×2 两章=12 + 开场白 2 + 胜利交底 2 + 卡点预告 1）合计 17 张，与总数 27（7+15+5）冲突。按 **27 总数硬约束**执行为：12 幕图 + 开场白 2（E1/E2）+ 卡点预告 1（E1→E2 撞角兽，story-e1.json tease 实锚）。**裁掉"胜利交底 2"**：其意象（E1 焦页"更大的东西醒了"/E2 残卷归档）与钩子批 #1（壁画记忆）/#2（预言残页）高度重叠；如业主要全量 29 张，可按 manifest 内 prompt 追加 2 张（约 +¥0.5）。
2. **E2 卡点预告未做**：story-e2.json 的 tease 为纯文本（era3 未交付，elite_id=null），无实体锚——与 31 号"E3+ 未交付章只登记不落"口径一致。
3. **色板量化（R5 §9 第 4 层）未做**：本批为"直用档"生图+自审；统一 palette.png 量化属 T-148 全量 35 张入库前的收口步骤（需 P4 六时代子色板定稿），本批图未量化入库不入 res://。
4. **F-147 标注**：全部 27 张为 AI 生成插画；manifest 已带 AI-generated 口径字段，正式入库（T-152 接线）时随资产携带。
5. **临时图床说明**：自审用 litterbox（1 小时过期）仅作审查中转，图源始终为 E 盘本地成品，无第三方留存。

## 五、复现与审计

- 复现脚本：`E:\game-assets\expand-v1\P1\tools\{gen.py, build_jobs.py, upload.py, build_manifest.py}` + `jobs-all.json`（27 条 prompt 全文）。
- 生成台账：`E:\game-assets\expand-v1\P1\tools\results.jsonl`（31 条原始记录，含 3 条超时失败记录——失败也入账）。
- manifest：`E:\game-assets\expand-v1\P1\manifest.json`（全量）；`game/assets/expand-v1/P1-manifest.json`（入库副本，随本报告 commit）。

## 变更记录

- v1.0（2026-10-15）：首版——P1 叙事图批 27 张（题图 7/插画 15/钩子 5）经 vibeapi gpt-image-2 产出，31 次生成调用（估算 ≈¥7.46），逐张 analyze_image 自审 27/27 PASS、0 needs-review；归档 E 盘 expand-v1/P1 + manifest 入库副本；工程接线归 T-152。
