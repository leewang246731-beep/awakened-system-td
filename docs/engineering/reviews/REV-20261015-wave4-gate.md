# REV：Wave 4 门禁审查（双轴）+ 出口终核（2026-10-15）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-15 |
| 状态 | 双轴发现 F1~F4 处置全部落地（F1 防误回接墙钟守卫=本轮最有价值项）；W4 出口终核 ①②③ ✅、④⑤ 本地绿/远端待 T-096；run_all 全绿（§五） |
| 对象 | Wave 4「v0.1 打磨与内测发包」实现批（`bc7dda7..f808b65`，13 commits）+ 本轮门禁修复批（单 commit） |
| 依据 | 08-task-plan §1 W4 行五项出口条件（v1.11）；13-quality-gates G1~G5；25-t090-holdout §4.1（① 证据）；27-t088-chest-monte-carlo（③ 证据）；REV-20261015-flowfield-determinism / REV-20261015-T098-ci-regression（波内门禁记录）；CODE_STANDARDS CS-x.y（Standards 轴） |

## 一、双轴发现与处置（Standards 轴+Spec 轴，单 commit 落地）

| # | 轴 | 发现 | 裁决与处置 | 落点 |
|---|---|---|---|---|
| F1 | Spec 轴（防回归缺口，本轮最有价值项） | **Simulation→流场确定性绑定无守卫**：T-067 DEBT 清账（`9a64a46`）已把 Step 第 (5) 段切至 `UpdateDeterministic`，但该绑定无测试保护——未来把切片改回墙钟重载 `Update(double?)`（或漏调任何入口）不会红任何测试，只会在 CPU 争用下静默复发 T-086 同种子哈希发散（OffSystemRegression 两次发散实证的根因形态） | 取强方案（行为面观察位，非源码正则）：①`FlowFieldManager` 增 `LastAdvanceMode` 观察位（`FlowFieldAdvanceMode.None/WallClock/Deterministic`），两条入口各自盖章——仅观察，不参与任何行为决策，配额/预算语义零改动；②`Simulation` 增 `Flows` 只读缝（与 `World`/`Stock`/`Ledger` 同款）；③两层守卫：`FlowFieldManagerTests.LastAdvanceModeStampsTheBudgetEntryThatRan`（观察位地基：None→WallClock→Deterministic 盖章序）+ `SimulationFlowFieldBindingTests`（行为面绑定：构造期 None、Step 一拍后必为 Deterministic，含 MarkDirty 脏区重建路径的长跑用例）。**不选源码正则的原因**：Simulation.cs:576 注释里保留着历史调用文本 `_flows.Update(...)`（口径说明），文本扫描须先剥注释、假阳面大且只护文本不护行为 | `game/src/EraCore/Pathfinding/FlowFieldManager.cs`（enum+属性+两入口盖章）；`game/src/EraCore/Sim/Simulation.cs`（`Flows` 只读缝）；`game/src/EraCore.Tests/Pathfinding/FlowFieldManagerTests.cs`；`game/src/EraCore.Tests/Sim/SimulationFlowFieldBindingTests.cs`（新建，2 用例） |
| F2 | Standards 轴（排版微瑕） | `CinematicTimeline.cs:461` `break;` 与下一 `case CinematicStep.Screen` 同行（T-086 批遗留格式） | 拆行，语义零变化 | `game/src/EraCore/Cinematics/CinematicTimeline.cs` |
| F3 | Standards 轴（判断项未登记） | ① CinematicScript JSON helper 五处重复——Cinematics/Narrative 各内容解析器私有的 Require/String/Int/Object/Array helper 族各自复制，应收敛为共享 JsonGuard；② `NarrativeContent.TextFields` 为手维护文本字段清单，与内容 schema 脱钩（新增文本字段靠手抄会漏计字数——F-106 主线字数口径） | T-099 行尾挂 REFACTOR（v0.2 前清），08 头部版本 v1.11+变更记录同批 | `docs/engineering/08-task-plan.md`（T-099 行+头部+变更记录） |
| F4 | 流程 | W4 门禁 REV 本体（本文档：双轴处置表+出口终核表+实现批总账） | 成文归档 | `docs/engineering/reviews/REV-20261015-wave4-gate.md` |

## 二、W4 出口五项终核（08-task-plan §1 W4 行）

| # | 条件 | 状态 | 证据 |
|---|---|---|---|
| ① | 单意图首解率 ≥80% 中英各自（留出集盲测，RL4） | ✅ | T-090（`178b35e`）留出集 120 条 hash 钉死盲测，25-t090-holdout §4.1 终测：zh 59/60=**98.33%**、en 60/60=**100%**（合计 119/120），分意图全 ≥80%；唯一未解句（holdout-zh-11「满级」档位词）已按 RL4 结构登记为观察项，不计缺口 |
| ② | 2 小时体验完整（F-176：新玩家 2h 完成 E1 主线+觉醒演出无阻断） | ✅ | W4 门禁终核判定；可查底盘=T-086（`64e60a4`）觉醒演出可跳过且入口完整+断弹→闪红→民夫送箭必现时序断言、T-084 headless E1 无阻断全通关回归（R2 底盘）、T-093（`4603750`）UI/UX 完备（三层信息/提示队列/瓶颈高亮）。口径注：T-097 专项 2h 人工验收的独立留档未单独成文，如需补档随 T-096 发包批处理 |
| ③ | 转盘先定后演断言+保底 10k 模拟报告 | ✅ | T-087（`81aa9a6`）RL12 先定后演事件序断言（settlement 先裁决、演出后呈现）+四人格点评；T-088（`691bf92`）宝箱 10k 蒙特卡洛分布报告+保底公式定案（27-t088-chest-monte-carlo，断言 CI-pinned） |
| ④ | GitHub Releases 免登录可下载 | ◐ 本地绿/远端待 T-096 | 打包链前置已清：EOL 规范化（`45eec13`，REV-20261015-T098 §一/二——干净检出 CRLF 曾致 2 红，修后 272+98 绿）+美术冻结（T-094 `c066ed8`，sha256 清单）+金样哈希钉绿；发包动作本身=T-096（push tag→zip→Release 免登录可下载+三处科普可查），发包批物料（LICENSE/README/Pages）在制 |
| ⑤ | CI 全绿（单元+集成+回放+性能 TierA+无头回归+金样） | ◐ 本地绿/远端待 T-096 | 本地 `bash tools/ci/run_all.sh` 全绿（§五，含四组验算/金样比对/1000 波无头回归全量）；远端 PR 触发面在位（T-098 `f808b65` 确认 ci.yml `pull_request:` 无分支过滤=全 PR 必跑，金样漂移即红已本地篡改证明）；本 commit push 后 Actions 实跑，远端首跑证据回填归 T-096 批 |

## 三、Wave 4 实现批总账（`bc7dda7..f808b65` + 本门禁批）

- **任务**：名义 14 项（T-086~T-099）。带 commit 交付 10 项——T-086 演出引擎+E1 啊哈+E2 觉醒、T-087 结算演出、T-088 宝箱保底原型、T-089 叙事一/二章+黄金三章、T-090 L1 盲测调优（① 的 98.33/100）、T-091 Llm 流式/超时/错误码/上下文/日志完备、T-092 音频全量 E1/E2、T-093 UI/UX 完备、T-094 美术包冻结、T-098 波次验算回归 CI 固化；计划外清账 2 项——T-067 遗留 DEBT「流场墙钟预算威胁确定性」（T-086 发现→`4116d30` 挂账→`9a64a46` 清账：UpdateDeterministic 2048 格/tick，REV-20261015-flowfield-determinism）+T-096 打包阻塞项 EOL 规范化（`45eec13`）。② 口径下 T-097 终核 ✅（补档口径见 §二）。待办：T-095（关卡时长实测冻结）、T-096（发包）、T-099（金样 nightly；行尾新增 REFACTOR 两项，v0.2 前）。
- **提交**：13 commits（`bc7dda7..f808b65`，git rev-list 实数）+ 本门禁批 1 commit（F1~F4 处置+本 REV）。
- **测试**：W3 出口 G1 4524 → 流场确定性批 5041 → T-098 收尾 5138（均 0 failed）→ 本批 +3（观察位盖章 1+绑定守卫 2）= **5141**（§五实跑）。
- **波内门禁记录（run_all 真实执行）**：
  1. `9a64a46`（流场确定性清账）：G2 9 checks/0 FAIL｜G2.5 files=404 rejected=0｜G1 **5041/0**（187 s）｜G3 零警告（18 s）｜G4 SMOKE_OK（16 s）——REV-20261015-flowfield-determinism §收尾。
  2. `f808b65`（T-098 收尾锚）：G2 9 checks/0 FAIL/0 WARN｜G2.5 files=434 rejected=0｜G1 **5138/0**（162 s）｜G3 零警告（10 s）｜G4 SMOKE_OK（14 s）——REV-20261015-T098-ci-regression §五。
  3. 本门禁批：§五。
- **发包前置收口**：确定性根因治理（T-086 发现→T-067 DEBT 清账链条走通）、EOL/干净检出一致性、CI PR 触发+漂移即红实证——v0.1 gate 的工程面已备，剩 T-096 发包动作本身。

## 四、遗留与挂账（出 W4 视野）

- **T-096（发包）**：④⑤ 的远端半边——push tag→zip→Release 免登录可下载+三处科普可查+Actions 首跑证据回填；发包批物料在制，完成即 ④⑤ 由 ◐ 转 ✅。
- **T-095**：关卡时长实测冻结（T-085 RED-FLAG 的实测口径校准同批，先于 T-130 E1 硬验收）。
- **T-099**：金样框架完善+1000 波 nightly+统计容差带报告+T8 独立 commit 检查；行尾新增 REFACTOR（JsonGuard 收敛+TextFields 同源生成，v0.2 前）。
- 既有挂账不变：T-070 单源化 DEBT 组（08 v1.3/v1.4/v1.6）、T-090 别名词典扩容 DEBT+ScriptStep/Atlas REFACTOR（REV-20261015-wave3-exit F6）、Q15（SetRally.lane 闭集）、G7/G8 待启用项（13 §1）。
- CI 与本地 run_all 的既有差异留档（非本批范围）：ci.yml Stage 1 未含 asset_validator（REV-20261015-T098 §三如实备注）。

## 五、收尾门禁（run_all 真实执行）

主工作树 `bash tools/ci/run_all.sh`（环境纪律：NUGET_PACKAGES=D:\nuget、DOTNET_ROOT=D:\dotnet，run_all 兜底导出）。执行时点=F1~F3 代码与文档处置全部落地、本 REV 骨架已落盘后（本节五格数字为定稿回填，纯文档增量不触及 G1/G2.5/G3/G4 判定面；回填后 G2 红线对全树复扫通过）。首跑一次 G1 红：`DeterministicQuotaThroughputMatchesWallclockOnFullFlood` 吞吐比中位 0.727<0.9——该用例为墙钟测量，当时工作树内有并行在制的 T-096 发包批（物料写入+CPU 争用），属 08 §3 性能敏感门禁「只在无并行负载的静窗执行」的已知形态而非本批代码（本批新增 3 用例全部通过：5140 passed/1 failed，失败仅此墙钟用例）；静窗复跑该用例单测即绿，随后全量复跑全绿（下表）。汇总：

| 门禁 | 结果 |
|---|---|
| G2 红线 | PASS（9 checks，0 FAIL 0 WARN，exit_code=0） |
| G2.5 资产 | PASS（files=434 rejected=0 warned=0） |
| G1 测试 | PASS（**5141 passed / 0 failed**，176 s——T-098 锚 5138+本批守卫 3） |
| G3 构建 | PASS（Release 零警告，TreatWarningsAsErrors=true，2 s） |
| G4 冒烟 | PASS（SMOKE_OK，12 s；含 ART/ART2/BRIDGE/PANEL/UI_OK 全横幅） |

（工作树内并行在制的 T-096 物料为未跟踪文件，不属于本门禁批 commit 范围。）

## 变更记录

- v1.0（2026-10-15）：首版。W4 门禁双轴审查处置 F1~F4（流场确定性绑定守卫 LastAdvanceMode+SimulationFlowFieldBindingTests、CinematicTimeline 排版、T-099 REFACTOR 登记、本 REV）+出口五项终核+实现批总账。
