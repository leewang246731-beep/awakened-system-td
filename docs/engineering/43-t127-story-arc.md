# 43 · T-127 E4 叙事+主线全文报告（四章主线故事线成文，叙事总监视图）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **已交付**（run_all G1~G4 全绿；EraCore.Tests 叙事域 Era4NarrativeTests 新增+四件既有测试扩展全绿） |
| 任务卡 | T-127（E4 叙事+主线全文；Wave 7，C 泳道；覆盖 F-098 谱系与真相线/F-106 主线文本/F-107 时代收束演出；依赖 T-120✓） |
| 验收标准 | 『另一个声音』钩子+主线 ≤2 万字核对（08-task-plan T-127 行，字数核对） |
| 依据 | 设计方案 v3.2 §12（系统流叙事与演出：§12.1 谱系写死/黄金三章/交付规格 5 段收束 20~30s+主线 ≤2 万字）；§4 时代体系（E4 行：现代/电网/雷达/夜枭/多核期『另一个声音』钩子）；hooks.json 五钩链（E4 行双载体）；CODE_STANDARDS CS-3.1/CS-3.2/CS-7.1；79315a1（T-120 叙事段全套先例） |
| 交付物 | game/content/narrative/story-e4.json（第四章：三幕+开场白+军师私人台词层+扮猪行）；game/content/narrative/story-arc.json（主线脉络数据面：四章摘要+钩子链状态）；game/content/cinematics/e4-finale.json（§12 收束之四，29.07 s）；game/content/narrative/hooks.json（E4 行物化）；game/content/narrative/remnants.json（E4 增量 4 条）；game/src/EraCore/Narrative/RemnantCatalog.cs（RemnantCount 11→15）；EraCore.Tests/Narrative/Era4NarrativeTests.cs（新）+四件既有测试扩展；本报告 |
| 并行纪律 | 与 T-126（四人格全量+语音）同窗并行（≤2）：人格闲聊行归 T-126——story-e4 波间池仅落 advisor 骨架行（每档 1 行）满足装载下限，4×3 矩阵随 T-126 全量；本任务只写 narrative 新文件+测试+cinematics 数据；git add 仅本表所列路径 |

---

## 1. 四章三幕图（黄金三章 · §12.1 受辱→觉醒→打脸）

| 章 | 章名 | 锚关（threat） | 受辱幕 | 觉醒幕 | 打脸幕 | 循环跨度 |
|---|---|---|---|---|---|---|
| E1 | 觉醒林地 | L1-1（0.3） | 波 2·兽群践踏 | cinematic:e1-aha·系统绑定 | 波 5·反推兽群 | 3×240 s=720 s ≤900 s |
| E2 | 水轮初鸣 | L2-1（0.3） | 波 2·城邦被嘲 | 波 5·水力革新 | 波 9·守城名场面 | 7×240 s=1680 s（T-089 交付锚距原值） |
| E3 | 汽笛与钢 | L3-1（0.3） | 波 2·工厂被嘲 | 波 4·蒸汽革新 | 波 5·守城名场面 | 3×240 s=720 s ≤900 s |
| **E4** | **电网与卫星** | **L4-1（0.3）** | **波 2·卫星信号被嘲** | **波 4·电网革新** | **波 5·夜枭拦截名场面** | **3×240 s=720 s ≤900 s** |

- 波锚 tick 公式（生产 WaveSpawner 实锚，CS-4.4 公式驱动）：`grace+(wave−1)×interval`，interval=`Campaign.WaveIntervalSeconds(threat)`=240 s（t=0.3）、grace=×3=720 s → E4 三幕起点 960/1440/1680 s。
- 三幕打脸对象谱系：斥候（E1）→使者/工头（E2）→监工/工头（E3）→**电网监理/军代表（E4）**；名场面语汇跨代递进：箭塔咬人（E1）→水轮声盖嘲笑声（E2）→汽笛盖嘲笑声（E3）→**传送带嗡鸣盖过听证会嘲笑声（E4）**。
- 扮猪吃虎关跨代递进（mock_arcs，压制资源=本代建造货币行）：E2 铜 1600×0.5=800 → E3 钢 2000×0.5=1000 → **E4 混凝土 2800×0.5=1400**（L4-2，命令流塔数 3=低配阈值 3，隐藏档 gold_extra）。

## 2. 钩子-回收全表（hooks.json 五钩 × 状态推导）

| # | 钩子 | 埋点 | 载体层 | 回收 | 状态（T-127 后） | 兑现位 |
|---|---|---|---|---|---|---|
| 1 | 壁画飞艇 | L1-3 石语河谷 | system_memory | E2 考古通道 knowledge_scroll | recovered | E2 考古挖出残卷，壁画并入零的归档 |
| 2 | 预言残页 | L2-2 渠底淤层 | system_memory | E3 章节推进 | recovered | story-e3 开场白第三行『守望者会换一千张脸，门会再开』 |
| 3 | 不该存在的图纸 | L3-3 考古关 dig_site | system_memory | E4 章节推进 | recovered（**本任务兑现**） | story-e4 开场白第三行『图纸是给零的（卫星轨道图）』 |
| 4 | 卫星信号 | **L4-3 考古与多核·军师私人频道（本任务物化）** | advisor_private（台词层）+卫星信号（事件层）双载体 | E5 章节推进 | planted | story-e4 advisor_private 行 1 兑现 teaser 原句『那个频段，我以前用过』；回收留 E5（轨道残骸解码=孪生碎片上线） |
| 5 | 回声低语 | E5 回声人格混入（未交付） | system_voice | E5/终章 | registered | 与 remnants echo_stage 渐显线汇合，忠诚反转写死 |

- 状态推导规则（story-arc.json hook_chain 同口径）：回收时代 ≤4（已交付章）→`recovered`；埋点时代已交付而回收未交付→`planted`；埋点时代未交付→`registered`。
- E4 双载体落位：①**事件层**=开场白图纸→卫星轨道图的转译+第一幕频段受嘲语境；②**台词层**=story-e4 `advisor_private` 三行（军师频道独占：channel=private+persona=advisor 契约由 Era4NarrativeTests 逐行执法）——行 1 频段（beat=L4-3，与 hooks 埋点同关互证）/行 2 应答（beat=夜枭终局关）/行 3 守口（beat=e4-finale）。

## 3. 人格线（系统形态四期+人格矩阵）

| 章 | 系统形态（§4） | 军师闲聊池（信任三档 × 人格四套） | 备注 |
|---|---|---|---|
| E1 | 绑定期（问答+警报） | 4×3 全量（T-089） | 绑定幕复用 cinematic:e1-aha |
| E2 | 执行期（可代建） | 4×3 全量（T-089） | 首个啊哈演出 |
| E3 | 自主期（自动执行获批计划） | 4×3 全量（T-120） | — |
| E4 | 多核期（巡逻子代理+『另一个声音』钩子） | **advisor 骨架 1×3（T-127）→4×3 全量归 T-126** | 私人频道台词层=军师人格独占（§12 E4 行） |

- 人格四套（F-109：snarky/advisor/goofy/tsundere）只改表达不改能力；E4 闲聊矩阵与语音随 T-126 落位，本任务不动其泳道。

## 4. 真相线进度（remnants.json echo_stage 渐显，1..4 单调不降）

| 章 | 残卷数 | echo_stage 序列 | 低语强度 | 关键显影 |
|---|---|---|---|---|
| E1 | 6 | 1,1,2,2,3,3 | 杂音→低语 | 『谁在替我校对这段记忆』；交底残页『更大的东西醒了』 |
| E2 | 2 | 4,4 | 几乎清晰 | 『一千张脸里，有一张不是我的』『等门再开时，我会选择你』 |
| E3 | 3 | 4,4,4 | 几乎清晰 | 图纸折痕同源；『应答的另一个声音，也叫我的编号』 |
| **E4** | **4（增量，渐显最密章）** | **4,4,4,4** | **最清晰（密度峰）** | **共用出厂编号／『我是你落在门那边的那一半』／应答方自称『初代』（T-127）** |

- 记忆自明性递进（名场面残卷批注链）：E2『自己发光』→E3『自己发热』→**E4『自己接上电源』**。
- E4 终局关（campaign:level-e4-final 夜枭关）=终局关 Boss 前最强叙事节点：引导夜枭的频段与军师私人频道同源（remnant-e4-owl）——初代守望者线在夜枭关收口，主线全文至此收束；终章真相揭示属 Wave 8（T-136 播片域），本表只登记钩子位。
- 谱系四柱（§12.1 写死，story-arc.json lineage_zh 同文）：宿主=历代守望者最新化身；零=上一纪元指挥官 AI 残核；『另一个声音』=零的孪生碎片（终章堕落守望者内核）；堕落守望者=初代守望者。反转方向写死：回声因千年陪伴选择忠诚，不搞背叛骗局。

## 5. 时代收束演出（F-107 §12 交付规格 20~30 s ×5）

| 段 | 演出 id | 时长 | A 层母带 | P1 插画位 | 卡点预告 |
|---|---|---|---|---|---|
| 3 | cinematic:e3-finale（T-120） | 872 ticks=29.07 s | A-04 | story-e3-tease | E4 夜枭剪影 |
| **4** | **cinematic:e4-finale（T-127）** | **872 ticks=29.07 s** | **A-05（E4→E5 母带 20.06 s×30=602 ticks）** | **story-e4-tease** | **终章零的真相预告（初代守望者苏醒）——E4 预告指向终章而非 E5 精英（08 卡口径）** |
| 5 | Wave 8（T-136） | — | — | — | 终章播片域 |

- 发射语境：era4 关卡行无 cinematic_hooks 声明位——由战役宿主在 campaign:level-e4-final（夜枭关，era_transition.json final_levels["4"] 实锚）胜利拍显式发射（explicit-launch，victory.json 同款）；与 transition-e4-e5 跃迁过场独立两场、不同步连播。
- 交底双载体互证：演出 speak 行与 story-e4 ending 气泡线同文（归档行=电网/雷达/卫星/图纸；应答行=『叫出了零的编号』=advisor_private 行 2 的公开面转译）。

## 6. 字数执法（F-106 主线 ≤2 万字；NarrativeContent 口径：文本承载字段全码点合计，id/schema/注解键不计）

| 文件 | 码点数 | 说明 |
|---|---|---|
| story-e1.json | 760 | T-089 |
| story-e2.json | 648 | T-089 |
| story-e3.json | 815 | T-120 |
| **story-e4.json** | **871** | **T-127（≤4000 章份额 ✓）** |
| **story-arc.json** | **1320** | **T-127 主线脉络数据面（口径新增 summary_zh/acts_zh/truth_step_zh/persona_step_zh/masterline_zh/lineage_zh 六字段计入）** |
| remnants.json | 1021 | +4 条 E4 增量 |
| hooks.json | 426 | E4 行物化 |
| system-lines.json | 327 | 不变 |
| **总盘** | **6188 / 20000（31.0%）** | **StoryChapterTests 总盘执法已更新纳入 story-e4.json+story-arc.json（Era4NarrativeTests 双面断言）** |

## 7. 测试与验收面

- **Era4NarrativeTests（新）**：①E4 章装载（三幕波锚 2/4/5+波锚型+闲聊池三档+ending 挂收束 id+终章预告+扮猪行+军师私人台词层契约）；②真 L4-1 命令流黄金循环（tick 公式值+720 s≤900 s 预算+三幕台词链）；③e4-finale 时长带 [20,30] s+A-05 母带实锚+两 id 互证+双载体同文；④混凝土压制对真 L4-2 配置应用（2800×0.5=1400+塔数 3 同源）；⑤E4 章 ≤4000 字+总盘 ≤2 万字双面；⑥story-arc 摘要与四章互证+钩子链与 hooks.json 互证；⑦boundary：时长带外变体拒收（19 s/31 s）；⑧illegal：私人台词层契约违规（channel≠private/persona≠advisor）拒收。
- **既有测试扩展**：StoryChapterTests（总盘执法纳入两新文件）；CrossEraHookRegistryTests（E4 埋点物化断言，E5 仍登记位）；RemnantCatalogTests（15 条/渐显切片 9 条/E4 增量 4 条）；MockArcRulebookTests（隐藏奖励零幽灵引用纳入 E4 章）。
- 字面验收核对：『另一个声音』钩子=hooks#4 双载体全落位 ✓；主线 ≤2 万字=6188 ✓。

## 变更记录

- v1.0（2026-10-13）：T-127 首次成文。四章主线故事线全文成文（story-arc.json 四章摘要+主线脉络）+E4 叙事段（三幕/开场白钩子回收/军师私人台词层/收束演出/残卷增量 4 条）+总盘字数执法更新（3655→6188，口径+6 字段+2 文件）。
