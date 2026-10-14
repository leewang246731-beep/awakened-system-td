# 25 T-090 L1 留出集盲测调优报告

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-15 |
| 任务卡 | T-090（L1 盲测调优冲刺；Wave 4，B 泳道；覆盖 F-077/F-124/F-170；依赖 T-069/T-061） |
| 验收标准 | 08-task-plan T-090 行：留出集中英各自首解率 ≥80%（语料不进文法，RL4）＋行尾 DEBT（别名词典封闭面扩容+同源校验断言）本任务清偿 |
| 依据 | RL4（02-consistency-audit §红线：≥200 条语料不得进入文法/别名词典，澄清卡路径计入失败例）；v3.2 §11.5（出货门槛=首解率 ≥80% 中英各自）；06 §4 R4（语料盲测分离：留出集 hash 登记）；CS-4.4（命中率由真实 Evaluator 跑出）；16-t061（基线方法与钉死纪律） |
| 交付物 | game/content/grammar/holdout-v1.jsonl（120 条，新）；Grammar/ 扩展 3 文件（AliasDictionary/ChineseNumber/L1IntentParser）+内容侧 3 文件（patterns-zh/patterns-en/lexicon）；EraCore.Tests 新 2 测试类+3 处扩展；本报告 |

---

## 1. 留出集构造（holdout-v1.jsonl，120 条）

### 1.1 构成

| 项 | 数值（条） | 说明 |
|---|---|---|
| 总行数 | 120 | 中 60 / 英 60，差 0 |
| 意图分布 | 12 意图 × 5 条/语言 | SP-2 冻结表全量均衡（HoldRegion/Build/Upgrade/Sell/FocusFire/ChangeFocusMode/RerouteLogistics/QueryStockpile/QueryThreat/SetAlarm/Patrol/ScrollControl） |
| 期望类型 | 全部 success | 反例/缺口/对抗行为由 corpus-v1（34 反例+70 缺口行）与 adversarial.jsonl（215 条）承载，留出集只测「真实军令应被首解」的正例面——门禁口径最严（无拒绝路径送分） |

### 1.2 写作原则（真实玩家多样性，比训练集更野但合理）

- **口语/新变体**：看住西门、东门来四座箭塔、过五分钟提醒我、去南线转转、do a sweep of the west lane、give me a heads up in five minutes；
- **错别字（编辑距离 1）**：箭榻→箭塔（等长替换，走同音容错）；
- **省略/语序**：下波敌人是什么、scout behind enemy lines（无 the 形）、stone?（裸词疑问）、the rearguard, go；
- **全半角**：建１２座箭塔、出售３号塔、３分钟后提醒我、build ２ spike traps、remind me in ４ minutes；
- **中文数字混合/数量口语**：3号塔升到三级、把篝火升到２级、a couple of spike traps（英文口语量词=2）；
- **英文多词别名（DEBT 观察项重点覆盖）**：era1 塔 name_en 全称 wood arrow towers / rockfall platforms；真实卷轴注册面 alarm string / spare blades / press gang（scrolls/era2.json，7 张全为多词或复合面）；
- **真实卷轴面**：警弦/烽火/民夫征调/备刃/静默（T-071 落地的内容注册表卷轴，玩家实际可见可指名——首跑全部 miss，即 DEBT 暴露点）。

### 1.3 盲测纪律执法（RL4 + 06 R4）

- **hash 钉死**：文件 SHA-256=`cbf8794a4167e7399232aab73e1fcb7c57beee0c0bb0260fff3a1aa9662e41a5`，登记于 `HoldoutV1BlindTests.Holdout_v1_file_hash_is_pinned`（首跑前登记；调优轮改内容即红——「改了留出集=作弊」的测试执法）；
- **去重执法**：与 corpus-v1/corpus-seed/adversarial 载荷归一化（TextNormalizer 同管道）零重复，`Holdout_v1_shares_no_row_with_training_corpora` 断言——构造期该断言实际拦下 1 条（"alert me in two minutes" 与 corpus-v1 #268 撞行），替换为 "notify me in 15 minutes" 后重新登记 hash，证明执法非摆设；
- **反向引用为零**：文法/词典装载路径（GrammarData/AliasDictionary/L1IntentParser 构造）不含 holdout 文件名——语料不进文法（RL4）。

## 2. 盲测首跑（parser 零改动）

评测器=`CorpusChannel.Evaluate`（T-052 交付，命中判据=意图 kind 相同且槽位图逐键逐值相同，与 corpus-v1 同严格度）；解析器=`L1IntentParserTests.CreateParser()`（era1 塔+敌别名+lexicon，未挂卷轴注册面——T-069 时点装配）。数字来自 xunit 实跑输出（CS-4.4）。

| 口径 | zh | en | 总体 |
|---|---|---|---|
| 留出集首跑（120 条） | 36/60 = **60.00** | 30/60 = **50.00** | 66/120 = 55.00 |

### 2.1 miss 归因（54 条全分类，条）

| 原因类别 | zh | en | 合计（占比，%） | 处置 |
|---|---|---|---|---|
| 句式缺（玩家说法真实、模板未覆盖） | 14 | 21 | 35（64.8） | 轮 1 补 36 条通用变体（zh 17+en 21） |
| 封闭词典面缺（卷轴注册面未入典——**DEBT**） | 5 | 5 | 10（18.5） | 轮 1 卷轴封闭面装载（§4） |
| 词典缺词（town centre 英式/southern road/current 裸 scope） | 0 | 3 | 3（5.6） | 轮 1 lexicon 增补 3 面 |
| 数量词表缺（a couple of=2） | 0 | 1 | 1（1.9） | 轮 1 ChineseNumber 增补 |
| 槽位污染（{after}后{reason} 误捕动词短语「喊我」） | 2 | 0 | 2（3.7） | 轮 1 前置插入 喊我 两变体 |
| 塔指认组合缺（所有/全部+塔名） | 2 | 0 | 2（3.7） | 轮 1 towerTarget 增组合 |
| 档位词表缺（满级——塔感知 max_tier，非显式档位） | 1 | 0 | 1（1.9） | **保留为诚实残余 miss**（§4.3） |

## 3. 调优轮 1（≤2 轮额度用 1 轮；只修暴露缺口，holdout 文件零改动）

1. **句式补齐 36 条**（patterns-zh v3 +17：看住/一定要守住/来 N 座/帮我建/都卖了/优先打/那只/目标改成/优先送/报一下/{scope}敌人是什么/{scope}有啥威胁/过 X 提醒/去转转/派个人去看看+喊我×2；patterns-en v3 +21：watch/must hold/throw up/bump up/take to/offload/take out/take down first/focus fire on 无 the 形/switch to/change to/goes to/divert half/still have/doing on/situation report/what's the {scope}/heads up/wake me in/do a sweep/scout 无 the 形）。全部为通用玩家句式族，非留出行文专属拼接；新增模式一律尾部追加（既有匹配优先级不变），仅 喊我 两变体插在 `{after}后{reason}` 之前防 reason 误捕（首跑实测的槽位污染根因）；
2. **卷轴封闭面装载（DEBT 清偿主体）**：`AliasDictionary.FromContentRegistryJson`（{items:[id,name_zh,name_en,aliases]} 同形注册表通用装载，FromTowerJson 改为委托同一核心）；`L1IntentParser` 增 `scrolls` 侧（scrolls/era2.json），注册表面优先于 lexicon 补充面（跨源同面异主→构造期 ArgumentException），纯 CJK 注册面生成「卷轴/卷」口语变体（对齐 lexicon 火油卷轴/火油卷 惯例），英文多词面（alarm string 类）归一后一等键；合并面并入 FuzzyRepair 受保护词表（烽火 距塔面 篝火 一替换，不保护会被修复改写）；
3. **lexicon 增补 3 面**：town centre→center、southern road→lane_south、current→threat_scopes.current；
4. **ChineseNumber**：英文量词 acoupleof→2（"a couple of" 归一单 token）；
5. **塔指认组合**：`所有/全部(的)?+塔名` → 裸塔 id（「该类型全体」语义与既有 裸 id 复数形一致，如 upgrade the spike traps⇒era1:tower-spike-trap）；不猜数量。

## 4. 终测

### 4.1 门禁数字（留出集 120 条）

| 口径 | zh | en | 总体 |
|---|---|---|---|
| 首跑（§2） | 36/60 = 60.00 | 30/60 = 50.00 | 66/120 = 55.00 |
| **终测（轮 1 后）** | **59/60 = 98.33** | **60/60 = 100.00** | 119/120 = 99.17 |

- 分意图（W4 出口①「单意图首解率 ≥80% 中英各自」口径）：24 桶（12 意图×2 语言）全部 ≥4/5，其中 23 桶 5/5、Upgrade/zh 4/5——门禁断言钉死于 `Holdout_v1_first_parse_rate_meets_gate`；
- 数字钉死：`HoldoutV1BlindTests` 常量 PinnedZhHits=59 / PinnedEnHits=60（相等断言；只升不降政策同 corpus-v1，变更须同步本报告并过审）；
- corpus-v1 回归：324/324=100% 不降（`CorpusV1BaselineTests` 原钉死断言零改动通过）；adversarial 回归：211 条启用断言全绿（含 3 条英文多词面 deny-by-design 条目——§5.2）。

### 4.2 调优有效性核验

54 条首跑 miss 中 53 条转命中；修改面全部为「补」（36 句式+3 词典面+1 数量词+1 组合+卷轴面装载），零删除零改写既有模式语义——corpus-v1 的 220 条成功期望与 34 条反例期望全部原样通过证明无抢匹配副作用。

### 4.3 残余 miss（1 条，如实保留）

| 行 | 军令 | 归因 |
|---|---|---|
| holdout-zh-11 | 把东门的箭塔升到满级 | 档位词表只有显式档位（三级/2级/tier3）；「满级」=塔感知 max_tier（era1 塔=2、K-3 全局档位=1..4），解析层无塔档案可查，映射任何数字都是猜——RL4 不猜，出澄清卡。结构修复需 AliasDictionary 携带 tier 元数据（或动作层「max」档位语义），登记为后续观察项（同 16-t061 §6.2 改进建议清单惯例），不视作本任务缺口：zh 98.33% 远在 80% 线上。 |

## 5. DEBT 清偿说明（08 T-090 行尾，REV-20261014-wave3-round2 #5 转登）

原 DEBT 文字：「别名词典封闭面扩容——AliasDictionary 现仅自 towers/enemies 内容装载（L1IntentParser 两侧），卷轴/分区/资源等玩家可指名面未入封闭词典，对抗语料的『封闭别名词典规整』口径只锚了塔面；盲测调优时一并扩容并补同源校验断言」。

| 面 | 处置 | 执法 |
|---|---|---|
| 卷轴面 | **已扩容**：AliasDictionary.FromContentRegistryJson + L1IntentParser scrolls 侧（scrolls/era2.json 七张全量入典，英文多词别名一等键）；测试侧两个解析器工厂（CreateParser/Era2Parser）均已挂载 | `AliasDictionaryTests.FromContentRegistryJson_*` 三行 + `AliasDictionaryFaceTests.Scrolls_registry_faces_load…/Parser_merges…/Conflicting…` |
| 资源面 | **同源断言补齐**（本就同源）：lexicon resources id 集合 == era1/era.json 注册表，集合级精确相等 | `AliasDictionaryFaceTests.Lexicon_resource_ids_match_the_era1_registry_exactly` |
| lexicon 卷轴残留面 | **钉死**：fire-oil/frost/strength 三张 T-069 预登记面（注册表无对应实体，corpus-v1 依赖）== 残差集；新漂移/漏清理即红 | `AliasDictionaryFaceTests.Lexicon_scroll_ids_stay_within_registry_plus_pinned_legacy` |
| 分区面 | **不适用同源断言**（登记）：分区 id 由 grammar 层定义、无内容注册表文件，表现层锚点（SimHost east_gate/south_gate 字典）另线接线——盲测未暴露分区面缺口（首跑 13 分区面行全命中） | 本节登记 |

### 5.1 英文多词别名的两层口径（REV-20261014-T082 发现 #1 的盲测观察结论）

- **L1 层（本任务）**：多词别名经 TextNormalizer 归一（去空格）后即封闭词典一等键——era1 塔全称（wood arrow towers/rockfall platforms）与真实卷轴面（alarm string/spare blades/press gang）留出集 8 行终测全命中。「英文多词别名进封闭词典」在 L1 侧已清偿；
- **LLM 档①b（另立任务，维持 T-082 裁定）**：模型回传**原始空格面**（"crossbow tower"/"alarm string"）与归一键精确匹配失败→原样过闸→闭表诚实拒绝（deny-by-design，adversarial adv-sg-zh-25/en-27/en-29 钉死）。本次盲测无真实模型回传面分布数据（BYOK cassette 均为合成），「①b 规整前先做 Normalize（须防 DoS 与误合并）」仍按 T-082 审查建议另立任务，本任务仅在 `AliasDictionaryTests.FromContentRegistryJson_raw_spaced_surface_does_not_resolve` 钉死该边界行为防漂移。

### 5.2 遗留登记（非本任务缺口）

1. SimHost（presentation 生产装配）尚未传卷轴注册面（`FromJson` 第 6 参可选，默认 lexicon-only=T-069 行为）——表现层接线属 D 泳道文件，本任务不越权改 presentation；E1 内测前由 UI 接线任务补一行构造参数即可；
2. 「满级」塔感知档位（§4.3）；
3. ①b 规整前 Normalize（§5.1，REV-20261014-T082 原登记）。

## 6. 验收对照与 CI

| 验收点（08 T-090 / 任务说明） | 结果 |
|---|---|
| 留出集 120 条（中英各 60、12 意图均衡） | ✓（§1.1，分布断言钉死） |
| 禁止复制 corpus-v1/adversarial 任何行（去重校验入测试） | ✓（§1.3，构造期实拦 1 条） |
| 盲测首跑出分（parser 不动）+失败清单+归因 | ✓（§2，54 条全分类） |
| 调优 ≤2 轮只修暴露缺口；holdout 不因调优改动（hash 钉死） | ✓（1 轮；hash 断言绿） |
| 中英各自首解率 ≥80% | ✓（98.33/100.00，§4.1） |
| corpus-v1/adversarial 回归不降（100% 保持） | ✓（§4.1） |
| run_all.sh 全绿 | 见下行实测记录 |

run_all 实测（本 commit 对应）：G2 红线 0 FAIL 0 WARN / G2.5 资产 PASS / G1 dotnet test 全绿（5020 passed, 0 failed，含本任务 13 个新测试方法）/ G3 Release 零警告 / G4 SMOKE_OK。

## 变更记录

- v1.0（2026-10-15）：T-090 首版。留出集 120 条入库（hash 钉死）+盲测首跑（zh 60.00%/en 50.00%）+调优 1 轮（36 句式/3 词典面/1 数量词/1 塔指认组合/卷轴封闭面装载）+终测（zh 98.33%/en 100.00%，分意图全 ≥80%）+DEBT 清偿（卷轴面扩容+资源/残留面同源断言）+本报告。
