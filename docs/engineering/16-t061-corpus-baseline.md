# 16 T-061 军令语料库 v1 入库+首解率基线报告

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 任务卡 | T-061（语料库 ≥200 条中英入库+基线报告；Wave 2，F 泳道；覆盖 F-170；依赖 T-052） |
| 验收标准 | 08-task-plan T-061 行：双语语料入库+首解率基线数字出（可 <80%）——**本任务是基线测量不是出货门禁**，数字如实 |
| 依据 | v3.2 §11.5（出货门槛=单意图首解率 ≥80%（中英各自，≥200 条语料））；11-spec-tables SP-2（12 意图枚举冻结+配套口径「E1 高频子集=并集 5 意图」「意图可解析≠动作可执行」）；CODE_STANDARDS CS-4.4（命中率由真实 Evaluator 跑出，禁固定值冒充）、CS-6（金样纪律的语料版钉死） |
| 交付物 | game/content/grammar/corpus-v1.jsonl（324 条）；game/src/EraCore.Tests/Agent/Grammar/CorpusV1BaselineTests.cs（分布/行为/基线钉死三层守卫，7 测试）；本报告 |

---

## 1. 语料库构成与分布（corpus-v1.jsonl）

### 1.1 总量与均衡

| 项 | 数值（条） | 说明 |
|---|---|---|
| 总行数 | 324 | ≥200 达标（超出 62%，为 5 意图 ≥25 条/语言的分布要求所致） |
| 中文（zh） | 162 | 双语差 0（要求 \|zh−en\|≤10） |
| 英文（en） | 162 | 同上 |
| 成功期望行 | 220（110/语言） | expected_parse=success，携带意图+规范槽位期望 |
| 失败期望行 | 104（52/语言） | 17 unparsed（15 反例+2 垃圾）+35 subset_gap/语言 |
| 种子并入 | 60/60 | corpus-seed.jsonl 全部并入，归一化后零重复；同文行槽位期望与种子零差异（脚本核验：missing=0, slot diffs=0） |

### 1.2 意图分布（行）

| 意图族 | zh | en | 构成 |
|---|---|---|---|
| HoldRegion（守住X） | 25 | 25 | 22 正例 + 3 反例 |
| Build（建造） | 25 | 25 | 22 正例 + 3 反例 |
| QueryThreat（敌情查询） | 25 | 25 | 22 正例 + 3 反例 |
| QueryStockpile（储备查询） | 25 | 25 | 22 正例 + 3 反例 |
| SetAlarm（设闹） | 25 | 25 | 22 正例 + 3 反例 |
| 7 缺口意图（升级/出售/集火/改集火模式/物流改道/巡逻/卷轴触发+停止） | 5×7=35 | 5×7=35 | 全部 expected_parse:failure + expect_key=agent.clarify.subset_gap + gap_intent 注解 |
| 垃圾行（呃呃呃呃/嗯/blah blah blah/ok） | 2 | 2 | family=none，unparsed |

正例多样性口径（任务卡：口语/省略/错别字[编辑距离≤1]/全半角/大小写）：别名交替（箭塔↔木箭塔、town center↔city center、粮↔粮食↔吃的）、量词/数量变体（两座/2/俩性量词/十二/十五/全角２３５）、全半角与大小写折叠（"２分钟后叫我"、"DEFEND THE SOUTH WALL"、"ANY THREATS…"）、错别字（箭楼→箭塔、火推→火堆 为等长替换，走同音容错修复；camfire 为非等长错字，**如实记 miss**）、口语省略（"wood?"、"建塔"、"east gate, hold it"）、语序变体（"南门给我守住"话题前置）。

行内注解约定（装载器忽略的扩展键）：`expect_key`（失败行期望的澄清话术键）、`family`（反例/垃圾行的意图族归属，"none"=无族）、`gap_intent`（7 缺口意图行指向的冻结意图名）。

## 2. 测量方法

- 评测器：`CorpusChannel.Evaluate`（T-052 交付，本任务未改 EraCore 源码一行——并行纪律：只写语料/报告/新测试三处）。命中判据：成功行=意图 kind 相同且槽位图逐键逐值相同（ordinal）；失败行=返回失败且携带非空澄清键。
- 解析器：`L1IntentParser`（T-052 五意图子集），从 game/content/grammar/ 三文件+era1/towers.json 构造，纯函数无 I/O。
- 数字来源：xunit 实跑输出（`CorpusV1BaselineTests`），**非设计文档期望值**（CS-4.4）。

## 3. 基线数字

### 3.1 总体与分语言（命中率，%）

| 口径 | zh | en | 总体 |
|---|---|---|---|
| 全语料（含反例/缺口/垃圾行） | 147/162 = **90.74** | 149/162 = **91.98** | 296/324 = **91.36** |
| 正例口径（仅成功期望行，**v3.2 §11.5 出货门槛同形**） | 95/110 = **86.36** | 97/110 = **88.18** | 192/220 = **87.27** |

拒绝通道正确率：反例+垃圾 36 条全部落 `agent.clarify.unparsed`（100%）；7 缺口意图 70 条全部落 `agent.clarify.subset_gap`（100%）——「意图可解析≠动作可执行」的澄清卡/快捷轮盘路径（SP-2 配套口径）在语料上零违约。

### 3.2 分意图正例首解率（%，=出货门槛口径；行=正例命中/正例总数）

| 意图 | zh | en |
|---|---|---|
| HoldRegion | 18/22 = 81.82 | 17/22 = **77.27（<80，最弱项）** |
| Build | 19/22 = 86.36 | 20/22 = 90.91 |
| QueryThreat | 18/22 = 81.82 | 19/22 = 86.36 |
| QueryStockpile | 19/22 = 86.36 | 20/22 = 90.91 |
| SetAlarm | 21/22 = 95.45 | 21/22 = 95.45 |

**读数**：总体与全部分意图（除 en HoldRegion）均在 80% 线上；en HoldRegion 77.27% 低于出货线——但**本任务是基线测量非门禁**，此数字如实保留，作为 T-069（12 意图全量+句式扩容）/T-090（盲测调优冲刺，其口径=留出集中英各自 ≥80%）的改进靶点。出货判定按 v3.2 §11.5 在 T-090 的**留出集**上做，本库是训练侧基线。

### 3.3 失败类型分布（失败期望行 104 条的期望键 vs 实际）

| 期望键 | 行数 | 实际命中 | 说明 |
|---|---|---|---|
| agent.clarify.unparsed | 34（17/语言） | 34 | 反例 15+垃圾 2 每语言 |
| agent.clarify.subset_gap | 70（35/语言） | 70 | 7 缺口意图 × 5 × 2 语言 |

### 3.4 miss 原因分类（28 条正例 miss 的归类，条）

| 原因类别 | zh | en | 合计（占比，%） |
|---|---|---|---|
| 句式缺（玩家说法真实、模板未覆盖） | 11 | 10 | 21（75.0） |
| 词典缺词（别名/数字词表缺失） | 2 | 2 | 4（14.3） |
| 容错缺陷（同音修复越权 2 + 非等长错字不修复 1） | 2 | 1 | 3（10.7） |

**失败原因 TOP3：①句式缺（21/28）；②词典缺词（4/28）；③容错缺陷（3/28）。**

## 4. miss 逐条清单（28 条全列；行号=corpus-v1.jsonl 条目序）

| # | 语言/意图族 | 军令 | 原因 | 说明 |
|---|---|---|---|---|
| 19 | zh/HoldRegion | 东门顶住 | 句式缺 | "顶住"动词框架无模板 |
| 20 | zh/HoldRegion | 别放敌人进北门 | 句式缺 | "别放…进"框架（现有"别让敌人过{target}"不够） |
| 21 | zh/HoldRegion | 南门给我守住 | 句式缺 | 话题前置语序 |
| 22 | zh/HoldRegion | 全力防守东门 | 句式缺 | "防守"动词（现有"全力守住/重点防守"两半） |
| 40 | zh/Build | 在南面建两座火堆 | 容错越权 | **FuzzyRepair 缺陷**：把"座火"窗口改写成"篝火"，破坏了紧邻的精确塔面"火堆"（见 §5） |
| 46 | zh/Build | 东边造个火堆 | 句式缺 | "造"无"在"前缀（build-zh-05 需"在{where}造"） |
| 47 | zh/Build | 南门再来两座箭塔 | 句式缺 | "再来"增量框架 |
| 69 | zh/QueryThreat | 本波威胁是什么 | 句式缺 | "{scope}威胁是什么"未泛化（只有常量"当前波威胁是什么"） |
| 70 | zh/QueryThreat | 下一波是什么敌人 | 句式缺 | 语序变体（常量"下一波敌人是什么"） |
| 71 | zh/QueryThreat | 现在的敌情如何 | 句式缺 | "如何"疑问词 + "的" |
| 72 | zh/QueryThreat | 敌情如何 | 句式缺 | 同上 |
| 88 | zh/QueryStockpile | 查一下石头 | 容错越权 | **FuzzyRepair 缺陷**：把"下石"窗口改写成"落石"（见 §5） |
| 96 | zh/QueryStockpile | 木材多吗 | 句式缺 | "多吗"框架 |
| 97 | zh/QueryStockpile | 粮草还剩多少 | 词典缺词 | "粮草"别名不在 lexicon（"粮"在，整词不在） |
| 122 | zh/SetAlarm | 半分钟后叫我 | 词典缺词 | 中文数字词表缺"半"（半分钟=30 s） |
| 169 | en/HoldRegion | hold the north gate at all costs | 句式缺 | hold-en-02="hold {target} at all costs" 缺 the 变体 |
| 180 | en/HoldRegion | hold the west wall at all costs | 句式缺 | 同上 |
| 182 | en/HoldRegion | hold the western gate | 词典缺词 | west_gate 别名只有 west gate/westgate（east 有 eastern gate——**非对称缺口**） |
| 183 | en/HoldRegion | keep them off the north side | 句式缺 | "keep them off"框架 |
| 184 | en/HoldRegion | east gate, hold it | 句式缺 | 话题前置+省略 |
| 204 | en/Build | I want arrow towers at the east gate | 句式缺 | "I want"框架 |
| 205 | en/Build | build two camfire at the south gate | 容错能力缺 | camfire=删字母错字（campfire 8→7 字符）；FuzzyRepair 仅支持**等长替换**，长度变化不修复 |
| 232 | en/QueryThreat | incoming wave threats | 句式缺 | "incoming wave"是 scope 词典面但常量句式"next wave threats"未泛化为 {scope} |
| 233 | en/QueryThreat | what's coming in the next wave | 句式缺 | "in the"插入语 |
| 234 | en/QueryThreat | how's the situation | 句式缺 | 省略式问法 |
| 258 | en/QueryStockpile | wood? | 句式缺 | 裸词省略（单说资源名） |
| 259 | en/QueryStockpile | are we running low on wood | 句式缺 | "running low"框架 |
| 284 | en/SetAlarm | remind me in half a minute | 词典缺词 | 英文数量词 "half a" 不在词表 |

## 5. 缺陷发现：FuzzyRepair 同音容错越权（2 例，T-069 修复项）

实测复现（ScratchDebug 一次性探针，不入库）：

| 输入（归一后） | FuzzyRepair 输出 | 后果 |
|---|---|---|
| 在南面建两座火堆 | 在南面建两**篝火**堆 | 窗口"座火"与精确塔面"火堆"相邻重叠（共用"火"），1 替换判近（座→篝）后改写，破坏了本已正确的"火堆"→全句无模板可配 |
| 查一下石头 | 查一**落石**头 | 窗口"下石"1 替换判近（下→落），把资源词"石头"内部改写成塔面"落石"→全句无模板可配 |

根因：`AliasDictionary.FuzzyRepair` 的守卫 `ContainsExactSurface(window)` 只检查**窗口自身**的子串是否为精确塔面，不检查窗口与**相邻**精确塔面/词典词（资源/分区）的重叠。修复方向（T-069）：修复窗口若与文中既有精确塔面重叠（窗口边界±1 字符内存在精确面），或改写会破坏相邻词典词，则放弃修复；两案例即此根因。**本任务不修 parser**——两行语料保留为 miss，钉死进回归，修复后自动转为命中（钉死值只升不降纪律见证）。

## 6. 基线钉死与改进建议

### 6.1 钉死纪律（CS-6 金样纪律的语料版）

`CorpusV1BaselineTests.Baseline_hit_counts_are_pinned` 把以下数字**如实钉死**（相等断言，非下限）：总数 324、总命中 296、zh 147、en 149、三命中率（0.9136/0.9074/0.9198，4 位小数）、5 意图族 × 2 语言的（行数, 命中数）对、miss 总数 28（zh 15 + en 13）。**政策：未来 parser 改进只允许升不允许降**——钉死值任何变动（升亦然）必须同步更新语料与本报告并过审，防止「改语料凑绿/悄悄退化」两个方向的假成功。`Miss_inventory_is_pinned` 同时把 miss 逐条清单输出到测试日志，供审阅比对。

### 6.2 改进建议清单（T-069 全量文法/T-090 盲测冲刺的输入；按预期收益排序）

1. **FuzzyRepair 守卫加强**（§5 根因，修 2 条 miss + 消除一类潜在静默破坏）：相邻精确面重叠检测。
2. **句式补齐 TOP 缺口**：hold-en-02 加 the 变体（修 2）；"顶住/防守"中文动词框架（修 2）；"{scope}威胁是什么"泛化+"incoming wave"入 {scope} 句式（修 2）；话题前置语序（"南门给我守住"/"east gate, hold it"）；裸词省略（"wood?"→单 {item} 句式）；"再来/I want/running low/keep them off/how's the situation"框架各 1。
3. **词典扩容**：资源别名 粮草→era1:res-food；分区 west/south/north 补 "western/southern/northern gate"（对齐既有 eastern gate）；数字词表补 半分钟=30 s 与英文 "half a minute"。
4. **错字容错升级**：等长替换→编辑距离 1（含删/插字），覆盖 camfire 类自然错字（需防误报，建议仅塔面槽启用+置信阈值）。

## 7. 语料质量抽查样例（10 条，供人工复核"玩家真会说"红线）

| # | 军令 | 期望 | 实测 | 备注 |
|---|---|---|---|---|
| 1 | 东门交给你了，给我守住 | HoldRegion/east_gate | 命中 | 口语+逗号噪声 |
| 2 | 在北门造一座火推 | Build/north_gate/1/campfire | 命中 | 错别字（火推→火堆，等长容错修复正例） |
| 3 | 在南面建两座火堆 | Build/south_side/2/campfire | miss | §5 缺陷受害者——完全自然的军令 |
| 4 | 粮草还剩多少 | QueryStockpile/era1:res-food | miss | 词典缺词（粮草） |
| 5 | 5分钟后开打 | SetAlarm/300/开打 | 命中 | reason 槽口语 |
| 6 | build 2 more campfires at the east gate | Build/east_gate/2/campfire | 命中 | "2 more"增量框架 |
| 7 | hold the north gate at all costs | HoldRegion/north_gate | miss | 句式缺（the 变体） |
| 8 | wood? | QueryStockpile/era1:res-wood | miss | 极简省略（真实玩家输入） |
| 9 | target mode: strongest first | failure/subset_gap | 命中 | 7 缺口意图代表（解析器正确拒绝+定向澄清） |
| 10 | 多造几个箭塔 | failure/unparsed | 命中 | 反例代表（数量含糊，应澄清不应猜） |

质量红线声明：324 条全部为自然军令（口语/省略/带噪声），无一条为「解析器刚好能解析的凑数句」；22 条 miss 正例（含 2 条缺陷受害行）是**如实测量**的结果而非语料错误——期望值按 SP-2 槽位规范（语言无关 id/阿拉伯数字/秒）书写，定义的是 parser 应达到的目标语义。

## 8. 验收对照与 CI

| 验收点（08 T-061 / 任务说明） | 结果 |
|---|---|
| ≥200 条中英入库 | 324 条（162/162）✓ |
| 5 已实现意图 ≥25 条/语言（正例多样化+反例 ≥3） | 25/意图/语言（22 正例+3 反例）✓ |
| 7 未实现意图 ≥5 条/语言（failure+subset_gap） | 5×7×2 语言=70 条，实测全部落 subset_gap ✓ |
| 双语均衡 100/100±10 | 162/162（差 0）✓ |
| 总体/分意图首解率基线数字 | §3（91.36% 总体；en HoldRegion 77.27% 如实 <80）✓ |
| <80% 项逐条失败清单+原因分类+改进建议（不修 parser） | §4 清单 28 条+TOP3 分类+§6.2 建议 ✓（EraCore 源码零改动） |
| 回归测试数字钉死 | CorpusV1BaselineTests 7 测试全绿 ✓ |
| run_all.sh 全绿 | 见下行实测记录 |

run_all 实测（本 commit 对应）：G2 红线扫描 PASS（9 checks，0 FAIL 0 WARN）/ G2.5 资产校验 PASS（files=140 rejected=0）/ G1 dotnet test 全绿（2486 passed, 0 failed，含本任务 7 个新测试）/ G3 Release 构建零警告 / G4 Godot headless 冒烟 SMOKE_OK。

## 变更记录

- v1.0（2026-10-13）：T-061 首版。corpus-v1.jsonl 324 条入库（种子 60 并入去重）+ 基线报告 + CorpusV1BaselineTests 钉死回归；发现 FuzzyRepair 相邻窗口越权缺陷 2 例（登记 T-069 修复项，本任务不修 parser）。
