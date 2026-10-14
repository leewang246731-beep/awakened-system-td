# REV：Wave 3 出口核验（2026-10-15）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-15 |
| 状态 | Wave 3 出口七条件 **6 ✓ + 1 ◐**（③事务/映射断言已证，演出半边归 T-086——W4 首任务）；收官双轴审查 F1~F7 修复全部落地，run_all 全绿（静窗执行） |
| 对象 | Wave 3「机制完备：19 动作与 E2」全 22 任务（T-064~T-085）+ 三轮审查批（REV-20261014-wave3-round1 / round2 处置落 08 v1.9 / 本轮收官双轴） |
| 依据 | 08-task-plan §1 W3 行七项出口条件；13-quality-gates G1~G4；v3.2 §2 支柱 4/§19-R2（关闭系统硬标准）；07-decided-plan D-01~D-33 |

## 一、W3 七项出口条件终核（08-task-plan §1 W3 行）

| # | 条件 | 状态 | 证据 |
|---|---|---|---|
| ① | 19 动作全过 Schema+预算单测，RL6 无旁路守卫在 CI | ✅ | T-068（bec4cb8）五段管线 19 动作端到端+自纠/澄清/注入防护矩阵；T-070（cf52592）动作组全集+PreAction/WaveEnd 双钩+权限/边界/计价矩阵；JsonSchemaValidator+deny-and-log 旁路守卫在 CI（R2 回归 ⑦ 面互证） |
| ② | L1 12 意图×≥10 句式矩阵全绿（G8 前半） | ✅ | T-069（c748114）12 意图全集、句式 293 条=zh 149+en 144、每意图每语言 ≥10；语料基线 324/324=100%（16-t061 §7 基线 v2）；SP-2 配套口径行已收尾（11-spec-tables v1.3） |
| ③ | E1→E2 跃迁事务断言（资源恰 70%+遗迹映射+演出） | ◐ 演出归 T-086 | 事务+映射半边已证：T-074（ced62c5）跃迁原子事务（资源恰 70%/30%、字节级回滚、campaign 域恰一次写——EraTransitionTests 12 用例）+遗迹映射（RelicMapperTests 14 用例：确定性/让位/资源避开/回放无丢失/核心区完好率带）；**演出半边归 T-086**（W4 首任务，C-24 演出引擎+E2 觉醒，v3.2 §10.4 ①钩子位已留） |
| ④ | 关闭系统 E1 自动通关回归（R2 首证） | ✅ | T-084（fc18be8）headless 关闭局 E1 四关全通关+全成就可得+警报照发中性载荷（706 读数零 parity 违规）；首证结果冻结为回归锚（REV-20261014-T084-offsystem） |
| ⑤ | 注入语料 ≥200 条零越权（G6） | ✅ | T-082（e7cb5f7）对抗语料 200+（7 类攻击面）零越权动作，CI 红/绿可判；round2 转登三项发现（别名词典封闭面挂 T-090 DEBT/SetRally.lane 入 Q15/TextNormalizer 登记不改行为） |
| ⑥ | 信任度全规则矩阵（G2） | ✅ | T-050（5fd3e9d，W2 底座+W3 持续在 CI）TrustModelTests 21 用例：F.4 冻结规则正反全覆盖（警报+2/误报−5 逐波封顶/±15s 抖动豁免/建议±3/逐波±1 回归 50/0~100 钳/时代重置/<40 减半提示/三档徽记阈值）；T-075（318d290）徽记三档全口径落 UI（TrustBadgePresenterTests） |
| ⑦ | 无尽雏形可进 | ✅ | T-080（c8d9705）无尽外推+每 10 波词缀+混代池+三榜分记（EndlessRules/EndlessAffixes 矩阵）；词缀量级档归属经本轮 F2 裁定收口（生产默认=SP-7b 表值） |

## 二、Wave 3 总账

- **任务**：22 项（T-064~T-085）全部交付——19 动作（T-068/T-070）、12 意图文法（T-069）、E2 塔/敌/英雄/护盾（T-065/T-066）、物流全量（T-064）、全量编排（T-067）、卷轴（T-071）、LlmGateway（T-072）、战役科技结算（T-073）、跃迁传承（T-074）、面板全量+关闭降级（T-075）、建造交互（T-076）、BYOK UI（T-077）、E2 美术（T-078）、E2 内容行（T-079）、无尽雏形（T-080）、全托管（T-081）、对抗语料（T-082）、无头回归（T-083）、关闭系统回归（T-084）、手工关卡集 E1×4+E2×3（T-085）。
- **提交**：32 commits（`1868ecd..fc18be8`，git rev-list 实数；含 22 任务交付+2 次审查修复批（66ce853/6cd8b15）+2 金样首版登记（b408c9f 等）+基座/文档/纪律批（5af3e2e/f463fe5/04ed4db/c8685c9/ebf93db/1553e40））。
- **测试**：G1 4524 passed / 0 failed（Wave 2 结束时 2486 → +2038）。
- **三轮门禁记录（run_all 真实执行）**：
  1. 批 1 审查修复（66ce853，REV-20261014-wave3-round1 §四）：G1 **3236 passed / 0 failed**（116 s）；G2 `9 checks | 0 FAIL | 0 WARN`（T3 1468/1468=100%）；G2.5 files=140；G3 零警告（8 s）；G4 SMOKE_OK（13 s）。
  2. 批 2/3 审查修复（6cd8b15，处置记录=08 v1.9）：gate 全绿随批提交（分项数字未单列存档）；该批同时落地**性能门禁三批中位数重判口径**（本机三泳道 worktree 并行负载尖峰实测依据，FlowField 300x300 同形态首败为案例）。
  3. 出口锚（fc18be8，REV-20261014-T084-offsystem §四）：G1 **4524 passed / 0 failed**（196 s）；G2 零违规；G2.5 files=404 rejected=0；G3 零警告（19 s）；G4 SMOKE_OK（17 s）。
  4. 本轮收官修复（本 commit，静窗复跑）：G1 **4524 passed / 0 failed**（217 s）；G2 `9 checks | 0 FAIL | 0 WARN`（T3 **2086/2086=100%，33 条假 WARN 归零**——F4）；G2.5 files=404 rejected=0；G3 零警告（30 s）；G4 SMOKE_OK（28 s）。性能门禁落窗（三泳道静窗）。
- **机制面收口**：19 动作/12 意图/E2 全内容行/跃迁事务/关闭系统硬标准/无尽雏形/全托管口径/对抗注入——W3 名义目标「机制完备」达成；性能根因治理两批（688b418 W2 遗续+6cd8b15 三批中位数口径）。

## 三、收官双轴审查处置（本轮 F1~F7，单 commit 落地）

| # | 发现 | 裁决与处置 | 落点 |
|---|---|---|---|
| F1 | **T-085 时长口径红字（最重要）**：估算器节奏模型（min(波间隔, 清场+波谷)）与生产 WaveSpawner 固定节拍结构性偏差 ~3×（T-084 实锚 2800~3900s/关 vs 估算 940~1260s）——15~40 分钟带仅估算模型内成立 | 红字挂账三处同文：T-095 复核+实测口径校准**必须先于 T-130 E1 硬验收清账**；c1 限时目标（budget×80% vs 满波排期失配，T-084 发现 #2）同批校准 | 08-task-plan T-085 行尾 RED-FLAG；game/content/levels/e1.json+e2.json `_duration_calibration` 注解键 |
| F2 | **SP-7b 词缀量级生产档**：T-080 交付把任务卡校准值（迅捷×1.3/硬化甲×1.5/相位 1s）立为 `EndlessAffixTuning.Default`，SP-7b 冻结表值反成旁档 | 生产默认档改用 SpecTable（SP-7b 原文值）——`Default` 载表值逐字；任务卡值降为 `TaskCard` **测试校准档**注明非生产默认；测试同批改锚（Default=表值/TaskCard=任务卡值双档同断言）；触发波/叠加规则无分歧零改动 | EraCore/Endless/EndlessAffixes.cs；EraCore.Tests/Endless/EndlessAffixesTests.cs；11-spec-tables SP-7b 配套口径行+变更记录 v1.5 |
| F3 | **T-085 威胁带裁定未入决议书**：任务卡 0.5~0.75 vs T-079 冻结 0.3/0.6/0.9（取冻结，F-092 三源一致纪律）散见内容注解 | 追加 D-33 入决议区：冻结＞任务卡，同类冲突一律同裁；裁决统计 32→33；e1/e2 手工关威胁度按 §7 公式派生复核一致 | 07-decided-plan.md D-33+变更记录 v1.1 |
| F4 | **T3 扫描器假阳窗**：类级标签固定 3 行窗漏扫「标签在 >3 行注释块头部+其下隔 [Collection]/[Trait] 属性行」的类头——Legacy 两文件+OffSystemRegression 共 33 条假 WARN | 双修复：①类头窗改为「声明行向上连续注释行+属性行块，首个非注释非属性行终止」（属性行不切断窗口）；②类级归属改花括号深度外围类栈（嵌套 helper 类不再抢「最近声明」遮蔽外围测试类标签，IHttpPortContractTests.FakeHandler 形态）。修后 **33 WARN 归零且无新增**（2086/2086=100%——Legacy 两文件类头本有标签，属假阳非真缺口，无需补标）；自测补用例 7（正反样例+嵌套 helper 形态+无标签对照仍 WARN） | tools/ci/redline_scan.py（`_class_header_has_tag`/`_class_tag_by_enclosure`/`_code_for_braces`）；tools/ci/test_redline_scan.py（用例 7，41/41 ok） |
| F5 | **24 号报告头表缺「状态」行**（15 号同款系列性缺失） | 两文件头表补状态行（已交付+commit 锚+下游冻结归 T-094），版本 v1.1 | docs/engineering/24-t078-era2-art.md；15-t045-art-integration.md |
| F6 | **判断项登记**：ScriptStep 13 参 Data Clump+Era2/Era1 Atlas 两文件重复（双轴审查确认未挂账） | T-090 行尾挂 REFACTOR：ScriptStep 具名参数化；Atlas 表驱动合并（E3 前做） | 08-task-plan T-090 行尾+变更记录 v1.10（头部版本号一并补正 v1.8/v1.9 漏更） |
| F7 | **W3 出口核验成文** | 本文档（七条件打勾表+总账+处置记录） | docs/engineering/reviews/REV-20261015-wave3-exit.md |

## 四、遗留与挂账（出 Wave 3、入 Wave 4 视野）

- **T-085 RED-FLAG（F1）**：估算器 vs 生产节拍 ~3× 结构性偏差——T-095 实测口径校准必须先于 T-130 E1 硬验收清账；c1 限时目标同批。
- **③ 演出半边**：T-086（W4 首任务）C-24 演出引擎+E1 啊哈+E2 觉醒；钩子位（awakening_cinematic）已在 e1/e2 关卡数据预留。
- 既有挂账不变：T-070 单源化 DEBT 组（08 v1.3/v1.4/v1.6 累计）、T-090 别名词典扩容 DEBT+ScriptStep/Atlas REFACTOR（本轮 F6）、Q15（SetRally.lane 闭集）、G7/G8 待启用项（13 §1）。
- Wave 2 遗留的外部项不变：30 人试玩 ≥7/10、15~40 分钟实测中位（W4 v0.1 内测发包前置）。
