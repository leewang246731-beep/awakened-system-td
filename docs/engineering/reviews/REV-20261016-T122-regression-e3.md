# REV-20261016-T122-regression-e3 — 无头回归 E3 扩展（Wave 6 收口任务）

| 项 | 内容 |
|---|---|
| 任务卡 | T-122（无头回归 E3 扩展；Wave 6，F 泳道，组件 C-30，测试类型 R[回归]，量 S；覆盖 F-172；前置 T-083✓ e94caf1） |
| 验收（08 行原文） | E3 数值快照扩展无意外漂移 |
| 状态 | **全量交付**：①E3 数值快照 46 参数化断言全绿（塔三行/敌九行/SP-4 十四参数/能源阶梯 E3 行/波次护盾 31/40/50/世博会 1.25×/传承刷新 ×0.5）；②E3 长程快照（关卡内容驱动 40 波×25 轮循环模拟）+首版金样 `regression-e3-v1.json`；③现有金样零漂移（e1/e2/sim/checkpoint/regression-v1 文件零触碰，155 金样/回归族测试全绿）；④守卫首跑数字留档（§3）；⑤本 REV。`bash tools/ci/run_all.sh` 全绿（§5） |
| 提交 | 单 commit（金样首版+产生它的测试代码同 commit——**无产品码**（只触 `EraCore.Tests/`+golden+docs），T8 判定不含产品源即合规，无需 allowlist 豁免；CS-6.3 依据=任务卡 T-122/08 行原文；见 §6） |

---

## 1. 交付物清单

1. **`game/src/EraCore.Tests/Regression/Era3NumericRegressionTests.cs`**（新，46 断言全 Theory/表驱动，期望值逐行注明文档出处——T-083 先例同法）：
   - **E3 塔三行**（加特林 270/240/8.0 穿刺、臼炮 810/480/11.0 溅射、铁甲墙 120/0/0 支援+HP1600；出处=v3.2 §4 L60 塔防特色列+SP-1b L46~L48 定类（T-112 复核追认）+F.5 类推锚带 [NEW]（towers.json 行 _source 推导式））；
   - **塔链法则**（加特林→臼炮：成本 ×3 严格（07 决议 D-15）、DPS ×2 精确落锚（F.5 锚 2.0）、类变步射程带豁免但 11/8=×1.375 仍落 1.25±15% 带内（F.5/07 修正 #2 双保险）；DPS 拆分自洽 16×30/2=240、480×30/30=480；铁甲墙纯阻挡位（SP-1b L48：零伤/零射程/无武器循环/无弹药/tier 1 封顶））；
   - **E3 敌九行**（HP/甲/速度逐行锚：era3/enemies.json 行 _source——行形对位 SP-3b L113~L121+E2 同位 ×2 跨代步 [NEW]；Boss 9000/16=v3.2 §4 L60 逐字锚点；个体盾 240/600 数据位（§6『护盾 E3 起』）；敌表外 id 拒绝（illegal））；
   - **波次护盾公式**（10+8×波，起始波 31=wave-e2-golden.json 冻结头单一事实源互锁：31→258/32→266/40→330/50→410（T-114 任务卡锚波）；波 30/1→0（boundary 不向前代泄漏）；负起始波拒绝（illegal））；
   - **SP-4 风险十四参数**（11-spec-tables L146『E3 过热爆炸』行全值：触发 3+热量动力学 5+半径 1+后果 5=恰 14，防增删漂移断言 Count==14；另钉内容行↔生产 OverheatTuning 装配映射逐字段互锁（T-113 HandcraftedLevelContent.Era3EnergyRisk 通道，百分比列 ÷100 入比值域）——内容表或装配映射任一侧漂移即红）；
   - **能源阶梯 E3 行**（L1=300/L2=1200（T-112 任务卡锚点）、倍率 4∈[3,5]（v3.2 §4 能源阶梯行，带界自 era3/era.json 同文件读）、risk_events=overheat_explosion 开闸）；
   - **世博会 1.25×**（附录 F.3『物流吞吐 +25%』：wonders.json 内容行（era_index 3/bonus_kind logistics_throughput/bonus_value 0.25）→WonderThroughputBoard 生产通道真算：未建 1.0→建成 Π(1+0.25)=1.25 精确；通道级手算对照 E3 矿轨档 10 件/s×1.25=12.5（TransportTier 真算））；
   - **传承刷新 ×0.5**（v3.2 §4 跨代资源行：CrossEraResourcePolicy.Parse(era3/era.json) 真算——铁/铜=0.5（保留 era2 命名空间）、煤/钢原生=1.0、恰两条刷新行且 FromEra=2（『上代』=紧邻前代口径）；乘子 ≥1 拒绝（illegal，T-115 通道守卫））。
2. **`game/src/EraCore.Tests/Regression/Era3WaveSimulator.cs`**（新）：E3 关卡内容驱动的波级无头平衡模拟器——敌情=生产 `WaveSpawner` 真排程（波表=levels/e3.json 各关 waves 块逐旋钮、roster=各关 regular_roster+精英线、威胁 HP 乘子全走 T-036 公式库、波次盾+个体盾取最大在 CreateUnit 落位=T-114 裁定①②③）；战斗=生产 `Unit.ApplyDamage`+`TickShield` 全真算（盾先吸减后入甲（Damage.DirectInt）、破盾 5s 后 20/s 再生（ShieldState 整数节拍）、穿甲打断 3s（加特林=SP-1a 穿刺类）——E3 盾机制全链路入快照）；防御=各关 command_script build-tower 塔线里程碑采购；经济=钢链供给线 0.2 件/s×每 2 塔 1 线（中间件配方 craft 150t 真算派生=30/150，T-116 弹链瓶颈口径）+弹药点值（配方篮子点值÷10 发/件派生：弹链 (1钢+1铜)/10=0.2 点/发、臼炮弹 (2钢+1煤)/10=0.3 点/发）+每轮初始授予=该关 initial_stock 钢点。循环语义：40 波/轮×25 轮（T-083 回退口径同形），每轮按 levels/e3.json 声明序轮换一关（4 关/轮换），IRng 流跨轮连续、roster 混编计数随新排程器按关重置（确定性重置，非随机）。全随机经注入 IRng（CS-1.4）。
3. **`game/src/EraCore.Tests/Regression/Era3RegressionTests.cs`**（新，4 测试）：主断言（1000 波快照回归，任一窗口漂移→exit 1 首分叉窗口报告）+头部不变量（schema/task/seed/waves/sample/cycle/level_rotation/guards WARN）+**关卡参数快照↔活内容互锁**（金样 levels 块与 levels/e3.json+towers.json 逐字段互锁——E3 内容表漂移先于金样再生即红）；确定性回放（同 seed 双跑摘要哈希全等）；运行预算（<30s，实测 0.52s——见 §3）；WARN 级守卫留档（越带不阻断，验收 08 第 3 条同口径）。
4. **`game/src/EraCore.Tests/Regression/RegressionE3Golden.cs`**（新）：E3 金样信封（source-gen JSON 上下文，AOT 红线；守卫/窗口/摘要记录复用 T-083 公共记录）。
5. **`game/src/EraCore.Tests/Regression/RegressionContent.cs`**（只加不改：+125 行，T-083 既有方法零触碰）：E3 装载器（塔/敌表生产解析器、era.json 能源阶梯+SP-4 十四参数点路径、recipes.json 配方行、towers.json 弹药公约、levels/e3.json 关卡行经 T-116 装载器同源）。
6. **`game/content/golden/regression-e3-v1.json`**（首版新文件，CS-6.1 再生成唯一入口=`ERA_REGRESSION_E3_GOLDEN_UPDATE=1 dotnet test --filter Era3RegressionTests`，禁手改）：20 窗口+全程摘要+四关内容冻结参数快照。
7. 本 REV。

## 2. 首跑基线（金样 summary 留档值）

| 指标 | 值 | 判读 |
|---|---|---|
| 击杀时间中位（全程） | **28.400 s** | 落带 [5,120]s ✓ |
| 通胀率（收入/支出，全程） | **0.232** | **越下带**（[0.8,1.5]）——观察项见 §3 |
| 摘要哈希 | 3686481218696899945 | 确定性锚（同 seed 双跑全等+跨进程复算全等） |
| 泄漏 | 0/20 窗（全部击杀） | 塔线在 40 波外推段仍清场 |
| 1000 波耗时 | 0.52 s | 预算 30s 的 1.7%（含盾再生节拍） |
| seed | 20261021 | T-122 seed family |

窗口表（20 窗全量，wave/enemies/kills/leaks/中位击杀 s/income/spend/stock/塔数/窗均收支比）：

| wave | 敌 | 杀 | 漏 | 中位 s | 收入 | 支出 | 库存 | 塔 | 比 |
|---|---|---|---|---|---|---|---|---|---|
| 50 | 406 | 406 | 0 | 1.633 | 3860.8 | 4195.8 | -270 | 3 | 0.92 |
| 100 | 1153 | 1153 | 0 | 36.5 | 3052.8 | 15269.5 | -4889.7 | 4 | 0.20 |
| 150 | 1091 | 1091 | 0 | 42.034 | 2428 | 19597 | -4492 | 3 | 0.124 |
| 200 | 487 | 487 | 0 | 1.633 | 4080 | 7684.2 | 3435 | 3 | 0.531 |
| 250 | 1112 | 1112 | 0 | 31.5 | 2945.6 | 14665.1 | -2122.5 | 4 | 0.201 |
| 300 | 1092 | 1092 | 0 | 35.7 | 2316 | 19092.2 | -1332 | 3 | 0.121 |
| 350 | 631 | 631 | 0 | 1.633 | 3744 | 11070.4 | 2872.8 | 3 | 0.338 |
| 400 | 1013 | 1013 | 0 | 26 | 3572.8 | 9607.6 | -4597 | 3 | 0.372 |
| 450 | 1093 | 1093 | 0 | 50.966 | 2180.8 | 22151.7 | 595.8 | 2 | 0.098 |
| 500 | 739 | 739 | 0 | 28.5 | 3252 | 13350.2 | 2028.8 | 3 | 0.244 |
| 550 | 775 | 775 | 0 | 14.366 | 3796.8 | 6358.7 | -1968.1 | 3 | 0.597 |
| 600 | 1262 | 1262 | 0 | 55 | 2604.8 | 24300.4 | -16066.7 | 4 | 0.107 |
| 650 | 816 | 816 | 0 | 28.5 | 2604 | 14464.8 | 1170.4 | 3 | 0.18 |
| 700 | 571 | 571 | 0 | 4.4 | 4020.8 | 4481.4 | -725.2 | 3 | 0.897 |
| 750 | 1203 | 1203 | 0 | 49 | 2828.8 | 19146.5 | -9445.9 | 4 | 0.148 |
| 800 | 1081 | 1081 | 0 | 42.034 | 2540 | 20692 | -10031.2 | 3 | 0.123 |
| 850 | 406 | 406 | 0 | 1.633 | 3860.8 | 4195.8 | -270 | 3 | 0.92 |
| 900 | 1153 | 1153 | 0 | 36.5 | 3052.8 | 15269.5 | -4889.7 | 4 | 0.20 |
| 950 | 1091 | 1091 | 0 | 42.034 | 2428 | 19597 | -4492 | 3 | 0.124 |
| 1000 | 487 | 487 | 0 | 1.633 | 4080 | 7684.2 | 3435 | 3 | 0.531 |

（850~1000 与 50~200 逐值重合=确定性旁证：LCM(40,50)=200 波完整周期。）

## 3. 守卫首跑数字与越带观察（验收核心留档）

守卫 WARN **23 条**（不阻断——T-083 验收 08 第 3 条"越带不阻断、逐条留档"同口径）：

- **income_spend_ratio 越下带 17 窗**（0.098~0.597 < 0.8；全程 0.232）——**E3 弹链瓶颈的量化观察**（任务卡指定留档项）：收入侧=钢链供给线 0.2 件/s×每 2 塔 1 线（中间件配方 craft_ticks=150 真算派生——T-116 发现的『钢转换器 0.2 件/s=E3 弹链经济瓶颈』在模拟器经济口径下同值复现），支出侧=弹药点值（加特林 15 发/s×0.2 点/发=3 点/s/塔，远超 0.1 点/s/塔的供给线收入）。**结论：瓶颈越带为结构性（内容口径）而非回归漂移**——与 T-116 headless 通关实证一致（四钢链+杂志缓冲+波谷蓄弹才覆盖 Σ≈9500 发需求），属平衡设计待原型校准面（收入源研究分期/关卡奖励未入 v1 口径同 T-083 注），不构成本任务阻断项；复标随经济任务落位（T-083 金样 notes 同款挂账）。
- **median_kill_s 越下带 6 窗**（1.633~4.4 s < 5 s，全部落在 L1（threat 0.3）/L4（0.75）低威胁轮换窗：3 加特林 240 DPS 对 120~360 HP 散兵/斥候/排枪手秒杀）——低威胁关清场过快是 E3 前两关教学定位的内容属性，同不阻断。
- 上带（>120s）零越带、泄漏零——无平衡崩坏信号。

## 4. 零漂移证明（验收"E3 扩展只加不改"）

1. **文件面**：`git status` 现有金样 `wave-e2-golden.json`/`sim-e1-golden.json`/`sim-e2-golden.json`/`checkpoint-e1-golden.json`/`regression-v1.json` **全部零修改**（无 M 标记）；T-083 三件（NumericRegressionTests/EndlessRegressionTests/BalanceWaveSimulator）零触碰；本任务触面=EraCore.Tests/Regression/ 五件+golden 新文件+本 REV（见 §6 提交清单）。
2. **测试面**：金样/回归族过滤（Golden|Regression|NumericRegression）**155/155 全绿**——含 T-083 regression-v1 千波重算逐窗零漂移、sim-e1/e2/checkpoint 金样、四组验算（GoldenVerifications）、OffSystemRegression。
3. **全量面**：全测试套件首跑 5923/5924 绿（唯一红=LlmOnlineTests 在线探针，live 网络端点环境依赖——单跑复测 3/3 绿且 §5 run_all 门禁轮 5924/5924 全绿，REV-20261015-T098 §一同型既有披露，与本任务触面零交集）。
4. **确定性面**：E3 金样同 seed 双跑摘要哈希全等+**跨进程**复算逐窗哈希全等（首版曾用 `string.GetHashCode(StringComparison.Ordinal)` 入窗哈希——同进程绿/跨进程红，已改字符逐位 FNV 混合并重生成；03 §1.2 回放根=跨进程位稳定，此修复已入模拟器注释留档）。

## 5. 门禁

`bash tools/ci/run_all.sh`（性能门禁安静窗口执行）——**总体 PASS（G1~G4 全 PASS）**：

| 门禁 | 状态 | 明细 |
|---|---|---|
| G2 红线零违规 | PASS | 9 checks \| 0 FAIL \| 17 WARN \| exit_code=0 |
| G2.5 资产规格校验 | PASS | files=552 rejected=0 warned=0 |
| G2.6 模组离线校验 | PASS | 0 FAIL, 3 mods loaded (8 content documents) |
| G1 测试全绿 | PASS | dotnet test: **5924 passed, 0 failed (477s)**（含本任务新增 50 测试；本轮在线探针亦绿） |
| G3 构建零警告 | PASS | Release 构建成功，TreatWarningsAsErrors=true（零警告，49s） |
| G4 Godot headless 冒烟 | PASS | SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，30s） |

## 6. 提交与金样纪律

- **单 commit**：`Era3NumericRegressionTests.cs`+`Era3WaveSimulator.cs`+`Era3RegressionTests.cs`+`RegressionE3Golden.cs`+`RegressionContent.cs`（只加）+`game/content/golden/regression-e3-v1.json`（首版）+本 REV。
- **T8/CS-6.2 判定**：金样首版随产生它的代码同 commit；触面**不含 `game/src/EraCore/` 产品码**（T8 的产品源前缀排除 Tests），机械判定即合规，**无需 allowlist `T8:` 豁免**（先例=T-083 e94caf1 同形：金样+测试同 commit）。CS-6.3 依据留痕=commit message 含任务 ID T-122+本 REV 编号；CS-6.1 再生成唯一入口=`ERA_REGRESSION_E3_GOLDEN_UPDATE=1`（金样 regen_command 字段同文）。
- **更新通道**（后续）：`regression-e3-v1.json` 任何更新必须独立 commit+依据编号（CS-6.2 原条款，无首版豁免）。

## 7. 如实备注

- 模拟器 v1 口径与 T-083 同族简化如实注明于金样 notes：溅射面伤不入账（臼炮按单体有效伤害计）、里程碑采购不走库存门、批次入场偏移不入击杀钟（波级全入场即交战）。铁甲墙（支援类）不入 E3 命令流（T-116 诚实披露：build-tower 通道拒绝无武器循环塔行）——模拟器对塔线含无武器循环塔显式抛红（内容演进时的显式失败面）。
- 波 31~40 为关卡胜波窗（13~19）外的 WaveFormula 外推段——波次盾公式的长程覆盖面即在此段生效（ShieldStartWave=31 单一事实源=wave-e2-golden.json 冻结头，与 T-083/T-114 互锁同源）。
- E3 四关无终局 Boss 行（levels/e3.json waves 块 boss_wave=null）——堡垒号 Boss 的战斗面已由 FortressBattleTests（T-114）覆盖，波级长程快照不含 Boss 批次（排程器 BossEnemyId=null 禁用，配置如实传递）。
