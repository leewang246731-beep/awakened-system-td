# REV-20261013-wave1-round3 — W1 收口批次（d6f5862..266cd90 后置审查）双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | W1 尾批四提交（d6f5862 T-040 / 20fa553 T-048 / 7a4e873 T-044 / 266cd90 T-043）及 f6cf568（T-045）的后置双轴审查 + 收口处置（本 commit） |
| 审查日期 | 2026-10-13（处置落盘 2026-10-14，见 REV-20261014-wave1-exit） |
| 双轴 | Standards（CODE_STANDARDS CS-2/CS-6/CS-7 逐条）× Spec（08 §Wave1 出口条件①~⑤ 原文 + 任务卡验收） |
| 处置执行 | 收口修复代理（单一 commit "Wave1 closure: …"）；#1/#2 为 MAJOR 级裁定，#3~#5 为处置记录 |
| 验证 | run_all.sh 全绿（G1~G4+G2.5，真实输出见 REV-20261014-wave1-exit §附）；redline_scan 9 检查 0 FAIL；扫描器自测 36/36 |

## 一、发现总览

| # | 轴 | 级别 | 发现 |
|---|---|---|---|
| 1 | S（CS-6.2/CS-6.3） | **MAJOR** | 首版金样 sim-e1-golden.json 与产生它的引擎代码同 commit（d6f5862 同时触 6 个 EraCore 产品文件）；且该 commit message 缺依据编号（CS-6.3） |
| 2 | S（CS-2.x 单源精神） | **MAJOR** | TowerMagazine 双适配器并存：Supply.TowerMagazine（权威实现）与 Simulation 内嵌 TowerMagazinePort（第二份 items→shots 记账）；另 SuppliedWeaponFire 接线直调 WeaponFire.Tick 会双计冷却、TryPayCost 先于 Place 存在窄窗 |
| 3 | S（RL7/CS-2.3） | 处置 | R7 上线后产品代码 7 处整数字面量命中内容锚点 |
| 4 | P（08 挂账纪律） | 处置 | spike1（7 坑）与 spike2（7 坑）报告的"设计输入"未挂任务账 |
| 5 | S（文档同步） | 处置 | 门禁/规范文档与新检查状态不一致（G2 7 条、G6 待启用、CS-6.2 无首版条款） |

## 二、逐项处置表

| # | 裁决与处置 | 落点 |
|---|---|---|
| 1 | **金样同 commit MAJOR 裁决**：判定为可登记偏差而非回滚项——因果倒置（引擎先于金样存在，T-040 首证即金样），回滚将销毁确定性首证。处置四件套：①CODE_STANDARDS CS-6.2 升 v1.1 补首版条款（**首版**金样允许与产生它的引擎代码同 commit 合入，但必须 REV 登记；**更新**金样必须独立 commit，原条款不变）；②redline_scan 新增 **T8** 检查（触金样 commit 同时触 EraCore 产品代码即 FAIL，Tests 除外；自测 5/5b 用例：表驱动判定 8 行 + 真实 git 端到端正反例）；③allowlist 登记 `T8: d6f5862 | first-creation golden…`（本 REV 即登记处）；④**CS-6.3 依据号说明**：d6f5862 message 含任务 ID（T-040）但无 A#/N#/D# 依据号——首版金样的依据号实际存在于金样文件 `notes`/`_source` 注解与 T-035 数值基准（F.5 四组）中，属首版形态缺陷；裁定：登记不回改历史（08 任务纪律：不动历史 commit），**后续金样 commit（尤其更新金样独立 commit）必须 message 带任务 ID+依据号**，由 T8+G6 流程面把关 | `docs/CODE_STANDARDS.md`（CS-6.2 v1.1+§6 尾注+头部清单+§8 映射+变更记录）；`tools/ci/redline_scan.py`（T8）；`tools/ci/test_redline_scan.py`（用例 5/5b）；`tools/ci/allowlist.txt`（T8 条目）；`docs/engineering/13-quality-gates.md`（G6 生效） |
| 2 | **TowerMagazine 重复 MAJOR 裁决**：双适配器（Supply.TowerMagazine 权威 vs Simulation.TowerMagazinePort 内嵌第二份记账）确属同一 seams 的两份实现，后续演进会静默分叉；但收口期合并属结构性改动（牵 Simulation 装配线与既有测试），按 08 挂账纪律记 DEBT 不即改。挂 T-053 行（C-13 五段管线薄版=接线编排落位处）：`[DEBT: TowerMagazine 双适配器合并（Supply.TowerMagazine 为权威，Sim 内嵌 port 委托之）+ SuppliedWeaponFire 接线编排器（直调 WeaponFire.Tick 会双计冷却，必须换 wrapper）+ TryPayCost 先于 Place 的窄窗补偿]` | `docs/engineering/08-task-plan.md`（T-053 行尾，v1.3） |
| 3 | **R7 命中处置（7 处，全部登记豁免、0 处改注册表）**：锚点集=game/content/era1/*.json ≥10 整数值（16 个：15/20/40/45/50/60/70/80/90/100/120/150/220/2500/280/800；域常量白名单扣 10/30；元约定键 tick_rate_hz/cell_size_px/canvas_size_px 不入集）。产品代码命中 7 行，逐一核验均为**文档化域常量**（带 [NEW-T-0xx] 任务卡锚点与出处注释，非无据魔数），按 CS-7.1 行级豁免登记：AmmoConverter.E1CraftTicks=60/HopperCapacityPerInput=40（[NEW-T-034]）、Carrier.LoadTicks=20/UnloadTicks=20（[NEW-T-047]，0.3 件/s 吞吐锚点构成常量）、BottleneckDetector.DefaultConsecutiveTicks=60（[NEW-T-048]）、TowerSupplyMonitor.DefaultWindowTicks=90（[NEW-T-048]）、WaveSpawner.BossWave=20（T-039 E1 终关口径）。改注册表路线留 W2 内容管线（这些常量届时随配方/波表入 content）。R1（波次参数禁硬编码）经 R7 子集覆盖：Balance 命名空间产品代码禁 ≥10 整型魔数（当前 Balance 目录 0 命中，仅白名单 10） | `tools/ci/redline_scan.py`（R7+R1 覆盖）；`tools/ci/test_redline_scan.py`（用例 6）；上述 7 行 `// ci-allow: R7 <任务ID 理由>` |
| 4 | **spike 坑位挂账归位**（逐条最小合理位置；协调方原提示"T-055 内容管线行"经核 08 无内容管线语义行，走廊三坑改挂 T-064 走廊城门交规行）：spike2-pit#2/#3/#4→T-064（衬墙+双代价缺一即漏/lane×costType 分桶/敌走廊自动定价按关键路径推导）；pit#6→T-056（占格放置×站位互斥）；pit#1/#5→T-059（无碰撞语义声明+编排器路线失效重算事件）；pit#7→T-043（coverage 覆盖窗）。spike1-pit#1/#2→T-059（08 无 C-10 弹体后续任务行，按裁决挂关卡循环行） | `docs/engineering/08-task-plan.md`（T-064/T-056/T-059/T-043 行尾，v1.3） |
| 5 | **文档同步**：13-quality-gates v1.1（G2 检查数 7→9+R1 覆盖声明；G6 由"待启用"转"生效（T8 机械核验）"并登记首版例外）；CODE_STANDARDS v1.1（头部已实现清单、§8 映射）；run_all.sh/ci.yml Stage 1 注释 7→9 条；08 v1.3（DEBT 挂账+变更记录） | 各对应文件 |

## 三、完成门槛验证（真实执行，摘录）

- `bash tools/ci/run_all.sh`（NUGET_PACKAGES/DOTNET_ROOT 按 09-env-locked）：**总体 PASS**——G2 9 checks 0 FAIL 0 WARN、G2.5 140 files 0 rejected、G1 1480 passed 0 failed、G3 零警告、G4 SMOKE_OK。全量输出见 REV-20261014-wave1-exit §附。
- `python tools/ci/test_redline_scan.py`：**36 passed, 0 failed**（含 T8 表驱动 8 行+端到端、R7 正反例与豁免）。
- `python tools/ci/redline_scan.py`：9 检查全 PASS；R7 注记 `16 锚点 | 104 产品文件已扫`；T8 注记 `2 金样 commit | 1 首版豁免`；T3 618/618（100%）。

## 四、遗留与挂账

- 本轮新增挂账=处置表 #2/#4（T-053/T-064/T-056/T-059/T-043 行尾 DEBT 标记）；R7 七处行级豁免属登记事实非挂账（豁免理由随行可审）。
- 既有挂账不变：Q11（08 §6）、T-045 帧数自动门禁、G7/G8 待启用（13-quality-gates §1）。
