# 38 · P4 氛围扩展图批生产报告（40 件成品）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 任务 | T-151 P4 氛围批素材生产（31 号 v2.0 §二.4：UI 时代皮肤 6 / 加载图 6 / 军师人格立绘 4 / 成就图标 24） |
| 状态 | **完成**——40/40 成品、40/40 自审通过、0 件 needs-review、0 次质量重拍 |
| 依据 | 31 号方案 v2.0（业主已批，§二.4/§1.2 六时代身份卡/§四生产管线）；32 号 P1 批先例（prompt 风格锚写法/manifest 结构/复现脚本布局）；28 号 T-094 冻结清单（皮肤=叠加层不替换冻结件）；B 层视频 manifest（B-12~15 人格色系锚） |
| 管线 | AI 件：vibeapi 中转 `https://www.vibeapi.cn/v1/images/generations`，模型 gpt-image-2；合成件：`tools/art/p4_achievement_icons.py`（纯标准库、确定性）。密钥 `config/secrets.local.json`（不入库不入日志） |
| 归档 | `E:\game-assets\expand-v1\P4\{skins,loading,personas,achievements}\`（U 盘，含 `_att` 尝试帧与 `tools` 复现脚本）；manifest 双份：E 盘 `P4\manifest.json` + 入库副本 `game/assets/expand-v1/P4-manifest.json`；D 盘副本=`game/assets/expand-v1/P4/achievements/`（24 枚图标，过 asset_validator） |
| 入游戏 | **否**——工程接线（HoloSkin 时代变体叠加/加载图双轨/立绘呼吸 shader/成就图标位）属 T-151 后续与 T-152 |

> **给业主的三句话摘要**：①40 件全部产完：AI 图 16 张（皮肤 6+加载 6+立绘 4）逐张过视觉模型自审全 PASS、0 次质量重拍，成就图标 24 枚改为**程序合成**（确定性脚本绘制，二次生成 sha256 逐字节全等，过 asset_validator 门禁）——是人工像素重绘的底稿，红线不放松；②总生成 21 次调用（16 首过+1 网络超时重试+4 次误配置作废已诚信入账），估算成本 ≈¥5.67，远低于 P4 批预算下限（31 号 §5.2 低配 ≈¥45）；③皮肤是叠加层不动 T-094 冻结件，立绘四人格与 B 层人格视频同色系（红橙/蓝灰/黄绿/粉紫）。

---

## 一、成品总览

| 类目 | 数量 | 规格 | 产线 | 通过 | needs-review |
|---|---|---|---|---|---|
| UI 时代皮肤底纹 skins | 6 | 1024×1024 | AI（gpt-image-2，A 档直用） | 6 | 0 |
| 加载插画 loading | 6 | 1536×1024 | AI（gpt-image-2，A 档直用） | 6 | 0 |
| 军师人格立绘 personas | 4 | 1024×1536 竖幅 | AI（gpt-image-2，A 档直用） | 4 | 0 |
| 成就图标 achievements | 24 | 64×64 | **程序合成**（B 档底稿，人工重绘承接） | 24 | 0 |
| **合计** | **40** | — | — | **40** | **0** |

**成本台账**（业主实报实销口径）：

| 项 | 次数 | 单价（估） | 小计 |
|---|---|---|---|
| 1024² 生成（skins 6 + skin-e5 传输超时重试 1 + 误配置作废 2） | 9 | ¥0.21 | ¥1.89 |
| 1536×1024 生成（loading 6） | 6 | ¥0.315 | ¥1.89 |
| 1024×1536 生成（personas 4 + 误配置作废 2） | 6 | ¥0.315 | ¥1.89 |
| 成就图标 24 枚程序合成 | 0 | ¥0（本地脚本） | ¥0 |
| **合计** | **21 次调用** | — | **≈¥5.67** |

- 单价依据 R5 §4.1【多源】gpt-image-2 ≈¥0.21/张（1024²）；1.5:1 面积档按 1.5× 估 ¥0.315；实报以 vibeapi 账单为准。
- 唯一一次质量线内重试（skin-e5）为**中转网络读超时**（传输层失败，未出图），非质量否决——同 prompt 重发即成功；质量重拍上限（≤2）未触发。
- **误配置作废 4 次（诚信入账）**：首批探针运行前输出目录未建，`gen.py` 先调 API 后写盘——4 张图已生成计费但落盘失败丢弃（skin-e1×2、persona-venom-elder×2）；`results.jsonl` 21 条原始记录全量留痕（16 ok+5 fail，失败也入账）。

## 二、生产纪律执行情况

1. **风格锚（R5 §9 五层落地，同 P1）**：每张 AI 图 prompt 首句写死时代色板 hex（§1.2 身份卡直接进 prompt）+ 时代母题（木望楼/矛尖→石堡/山墙→桁架/齿轮→雷达/播放键→尖塔/六边→星门/裂缝）+ P1 同款共享风格尾缀 `16-bit pixel art adjacent abstract, layered color strata style` + `no text` 反乱码指令。
2. **皮肤=叠加层纪律**：6 张皮肤底纹仅供 HoloSkin 时代变体**叠加**（时代色板渐变层积+母题水印阵列+全息扫描线+边角纹样、中心留白保读性）；T-094 冻结的 `panel-era1/era2.png` 与 SystemPanel 骨架零触碰（§10.2 恒定纪律）。
3. **加载图留白区**：6 张均在画面下 1/4 压成深色近空层积（prompt 显式要求 calm dark near-empty lower quarter），给加载文字与进度条留位。
4. **人格立绘色系对齐**：毒舌老者=红橙（#D9482B/#F2A03D/#6B251A）、参谋学者=蓝灰（#5C7A99/#9AA5B1/#2C3E50）、沙雕工匠=黄绿（#AFC94B/#E3D25E/#5C6E2F）、傲娇少年=粉紫（#E88BB0/#9B6FD0/#5A4A8A）——与 B 层人格解锁视频 B-12~15 的四色抽象研究同族；全部半身像、抽象层积构成、几何暗示五官非写实、no text。
5. **成就图标改道程序合成（B 档）**：31 号原案「AI 概念图→人工 32px 重绘」；本批按业主指令以**程序合成承担概念底稿**——`tools/art/p4_achievement_icons.py`（era2_placeholders.py 先例延续：固定坐标+固定色板+无随机数，仅标准库）。**sprite 人工重绘红线不放松**：24 枚即重绘底稿，升级轮以脚本为基准逐枚像素重绘。
6. **自审（反造假）**：16 张 AI 图经 `mcp__4_5v_mcp__analyze_image` 视觉模型**逐张真跑**（16/16，非抽样），四项判据=主题符合 brief/色板接近 hex/无可读文字乱码/无明显 AI 痕迹，全部 PASS（其中 6 张首跑回文被截断，以紧凑格式重跑补获显式 VERDICT 行——两次都是真实审查，非补记）。24 枚图标以 4× 拼板过同一视觉模型审查（PASS：全部母题可辨、环+盘结构一致、无出界碰撞）+ asset_validator（24/24 ok，各 5 色 ≤64 上限）+ **二次生成 sha256 逐枚全等**（确定性门禁，`--self-test` 可复验）。
7. **manifest（21 号 D21-4 纪律）**：40 条逐条登记（AI 件含 prompt 全文/模型/重试/自审结论；合成件含 sha256/锚点/生产方式）；入库副本 `game/assets/expand-v1/P4-manifest.json`；ai_generated 标注（F-147）AI 件=true、合成件=false。

## 三、成就图标目录与色环映射（24 枚）

仓库尚无正式成就清单文件（成就系统未落地，01 号 F-097/F-093 仅定口径），本批按 **Progression 域实锚**派生 24 枚目录；成就系统落地时按正式清单对位改名（manifest id 即 kebab 文件名）：

| 组 | 枚 | 实锚 | 色环 |
|---|---|---|---|
| 时代首通 6 | era1~era6-clear | F-003 通关判定×六时代 | 各时代主色环 |
| 防线技艺 8 | no-station-lost/defense-intact/no-tower-sold/ammo-steady/efficiency-master/key-untouched/first-research/logistics-clean | `game/content/campaign/objectives.json` obj-s1~s8（SP-8a/SP-8b） | E1×2（采集/箭矢）+E2×3（城墙/研究/物流）+E3×3（经济/效率/仓储）——按机制首发时代落位 |
| 系统与军师 4 | system-bound/system-offline/advisor-max/persona-all | §12.1 系统绑定/F-097 关闭系统可全成就/B-11 军师满级/B-12~15 四人格 | E4×2（电子域）+E5×2（纳米辉光域） |
| 收集叙事 4 | scroll-first/memory-all/zero-truth/wonder-first | C-20 首挖残卷/D 层 8 段记忆/E-16 零的真相/F-060 首座奇观 | E5×1+E6×3（零源于深空真相，收集链收束于星门） |
| 挑战 2 | boss-first/endless-milestone | C-01/C-05 首杀 Boss 踏群者/F-123 无尽三榜里程碑 | E1×1（踏群者）+E4×1（数据榜） |

每枚=时代主色环（r24~28）+右上辅助色强调弧+暗底圆盘+中央亮色母题符号（守望塔形/盾/菱晶/禁售币/满弦箭/仪表/堡楼/卷轴/物流网/目纹/断电环/五星/四面具/八碎片/零之环/阶梯金字塔/巨兽首/无穷环）。视觉审查备忘：E4 雷达塔符号在 64px 下偏抽象、奇观金字塔构图略低——重绘轮可优先精修，不阻塞本批。

## 四、缩略览表

### 4.1 UI 时代皮肤底纹（skins/，1024×1024）

| 文件 | 内容 | 自审 |
|---|---|---|
| skin-e1.png | E1 赭金色域层积+矛尖水印阵列+扫描线+木石纹边角 | ✓ |
| skin-e2.png | E2 青铜苍层积+山墙/编钟水印+青铜浮雕边角 | ✓ |
| skin-e3.png | E3 铆铁赭红层积+桁架/齿轮水印+铆焊纹边角 | ✓ |
| skin-e4.png | E4 电网蓝层积+播放键/雷达扇扫水印+混凝土/电路边角 | ✓ |
| skin-e5.png | E5 聚变青层积+力场弧/六边晶格水印+纳米流光边角（传输重试 1 次·网络） | ✓ |
| skin-e6.png | E6 星门靛蓝层积+星门环/裂缝水印+星云透镜边角 | ✓ |

### 4.2 加载插画（loading/，1536×1024）

| 文件 | 内容 | 自审 |
|---|---|---|
| loading-e1.png | 木望楼特写·晨雾层积·下 1/4 深赭留白 | ✓ |
| loading-e2.png | 石堡箭楼特写·柱廊雾层·下 1/4 深苍留白 | ✓ |
| loading-e3.png | 铆接桁架铁塔特写·烟层龙门吊·下 1/4 深赭红留白 | ✓ |
| loading-e4.png | 混凝土雷达塔特写·城市灯网层·下 1/4 深蓝留白 | ✓ |
| loading-e5.png | 能量尖塔+悬浮环特写·青紫光层·下 1/4 深青留白 | ✓ |
| loading-e6.png | 星门环塔特写·星云透镜层·下 1/4 深靛留白 | ✓ |

### 4.3 军师人格立绘（personas/，1024×1536 竖幅半身）

| 文件 | 内容（主色系） | 自审 |
|---|---|---|
| persona-venom-elder.png | 毒舌老者：兜帽削瘦剪影/撕裂绸带须/ Ember 眼/讥指手势（红橙） | ✓ |
| persona-strategist-scholar.png | 参谋学者：持半透明推演图/层流长袍/银环额饰（蓝灰） | ✓ |
| persona-craftsman-goofy.png | 沙雕工匠：歪嘴笑/歪工具带超大扳手/竖拇指补丁围裙（黄绿） | ✓ |
| persona-tsundere-youth.png | 傲娇少年：抱臂别脸/粉紫渐变发/侧浮面具/腮红光（粉紫） | ✓ |

### 4.4 成就图标（achievements/，64×64 程序合成）

24 枚 `achv-*.png`（清单见 §三；sha256 逐枚登记于 manifest `items[].sha256`；4× 拼板 `E:\game-assets\expand-v1\P4\_contact-sheet-icons.png` 留档）。

## 五、口径说明与遗留

1. **成就图标档位说明**：31 号 §二.4 原案「AI 概念图→32px 人工重绘」，本批按业主 2026-10-13 指令改为「程序合成 B 档（人工重绘红线）」——概念底稿由确定性脚本承担（零 AI 调用零成本、逐字节可复现），人工像素重绘承接不变；最终入库尺寸待成就系统定稿（本底稿 64×64 在白名单内）。
2. **AI 大图不入 game/assets**：1024²+ 尺寸会被 asset_validator SIZE-ALLOWLIST 拒收（CI Step 1.5 扫 `game/assets` 全树），故 D 盘副本=24 枚图标+manifest（同 P1「素材仅归档 E 盘+manifest 入库」口径）；发布期接线走 D21-5 双轨（U 盘→res://）。
3. **色板量化（R5 §9 第 4 层）未做**：同 P1 遗留——待 P4 六时代子色板定稿后随 T-151 入库收口统一过 palette.png。
4. **皮肤切片未做**：1024×1024 底纹→九宫格/边框切片属 T-152 HoloSkin 工程接线（需与 SystemPanel 骨架对位），本批只交底图。
5. **成就目录对位**：§三派生目录的正式化（成就系统 F-xxx 落地）后如 id/分组有出入，按 manifest 重命名登记（图标本体不变，环色/母题按正式目录校对）。
6. **临时图床说明**：自审中转用 Read 工具自动 CDN 直链（会话期有效）+ litterbox（1h 过期，仅 loading-e6 与图标拼板两处）；图源始终为 E 盘本地成品，无第三方留存。

## 六、复现与审计

- AI 件复现：`E:\game-assets\expand-v1\P4\tools\{gen.py, build_jobs.py, build_manifest.py}` + `jobs-all.json`（16 条 prompt 全文）；生成台账 `tools/results.jsonl`（21 条原始记录：16 ok+5 fail——4 条本地误配置+1 条中转超时，失败也入账）。
- 合成件复现：`tools/art/p4_achievement_icons.py`（仓库源=`D:\game\tools\art\`，E 盘 `P4\tools\` 有同步副本）；`--self-test` 一键复验 asset_validator+sha256 全等；输出 `--out`（D 盘入库副本）`--extra`（E 盘归档）。
- manifest：`E:\game-assets\expand-v1\P4\manifest.json`（全量）；`game/assets/expand-v1/P4-manifest.json`（入库副本，随本报告 commit）。
- 门禁：`python tools/ci/asset_validator.py game/assets` → files=395（+24）rejected=0（run_all Step 1.5 口径）。

## 变更记录

- v1.0（2026-10-13）：首版——P4 氛围批 40 件交付：AI 16 张（gpt-image-2 经 vibeapi，21 次调用估算 ≈¥5.67，含 4 次误配置作废诚信入账）逐张 analyze_image 自审 16/16 PASS；成就图标 24 枚程序合成（确定性，sha256 二次全等，asset_validator 全过，0 成本）；归档 E 盘 expand-v1/P4+manifest 双份+D 盘图标副本；工程接线归 T-151/T-152。
