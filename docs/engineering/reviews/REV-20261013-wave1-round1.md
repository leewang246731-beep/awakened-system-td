# REV-20261013-wave1-round1 — Wave 1 首批（6dad165..HEAD 八提交）code-review 双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 1 首批提交 `6dad165..HEAD`：23db0c1（T-049 E1 内容数据）、4d236cf（T-046 资产校验器）、b45eb8f（T-037 流场寻路）、51c9d16（T-041 SQLite 存档）、7ad6dc4（T-032 网格世界）、026b792（T-036 公式库）、255313a（T-035 数值基准+四组验算）、1ebf699（T-036/T-035 review fix） |
| 审查日期 | 2026-10-13 |
| 双轴 | Standards（CODE_STANDARDS/DOCUMENT 追溯纪律/06 质量门禁）× Spec（08-task-plan 任务卡验收 + 03 §6 契约清单原文） |
| 处置执行 | 修复执行代理（单一 commit "Wave1 batch1 review fixes: doc-defect F.5 group4 + joint-test + gates"）；7 项发现全部本轮修复，无挂账、无驳回 |
| 验证 | §三全过（dotnet test 370/370、redline 7 检查 0 FAIL 0 WARN 且 T3 关联 211/211、Release 构建 0 警 0 错、asset_validator 自测 50/50、run_all.sh G1~G4 全 PASS 含 Godot 冒烟） |

## 一、发现总览

编号 #1~#7 对应协调方发现 F1~F7。分布：Standards 轴 5 项（#1/#2/#5/#6/#7）、Spec 轴 2 项（#3/#4）；#4（联测缺口）为最重要项。

## 二、逐项处置表（轴：S=Standards，P=Spec）

| # | 轴 | 发现 | 处置 | 落点 |
|---|---|---|---|---|
| 1 | S（文档缺陷：口径不可追溯） | T-035 第 4 组（第 30 波 4 火油 57s<107s）以 v2.1 §14.3 转引值为断言门禁，但该对数值的推导未随档（v3.2 附录 F.5/04-arch N1/07 D-15 均为转引；基线 8402414 已查无存）——文档断言了一个无法复算的口径 | ①测试收紧为**重建口径 ±20% 带断言**：实际计算值 12.28s（构成=散兵线 9 敌 540 EHP+攻城线 6 锤 9000 EHP，4 火油有效 960/768 DPS，540÷960+9000÷768=12.28s），断言区间 [9.8,14.8]s（=12.28×0.8/1.2 向外取整）+ 现行波间隔公式断言（120s×1.3@t=0.75=156s），57/107 降为注释（引 v2.1 遗留）；②设计文档 v3.2.1：附录 F.5 第 4 组与 §15 对应句改为带公式的重建口径，文头版本行与变更记录加 v3.2.1；③08-task-plan 新增 §6 开放问题区，**Q11** 挂"追回 v2.1 57s/107s 推导口径（基线 8402414 已查无存），当前以重建口径为准" | `GoldenVerifications.cs`（group 4 重写）；`docs/我在游戏觉醒系统-游戏设计方案.md`（§15/F.5/版本行/变更记录）；`docs/engineering/08-task-plan.md`（§6 Q11+变更记录 v1.1） |
| 2 | S（性能门禁不稳态） | T-037 性能用例（300×300 全泛洪）单次运行的 max-frame 断言被一次性 GC/OS 调度尖峰主导，且无无负载严格断言 | ①max-frame 断言改为**三次独立运行取中位数**（抓持续回归不抓毛刺），每次运行另断言与 one-shot 哈希相等；②新增无负载严格断言：中位均帧 ≤10ms（=8ms×1.25 CI 抖动裕量；空载留档 7~8ms 支持 8ms 线，注明于此）；③注释写明 33.3ms（30Hz 单 tick，K-1 IClock）语义边界与 3.3ms 边界裕量推导 | `FlowFieldManagerTests.cs`（FullFlood300x300 重写+MedianOf 助手） |
| 3 | S（薄弱测试三处） | ①LevelLayoutSnapshotDtoTests 仅 1 条 happy path；②GridWorldTests.Constructor_accepts_extreme_dimensions 的 `NotEqual(0,hash)` 近恒真；③LevelSaveBlobDtoTests 缺边界 | ①补 boundary（空 payload+最小 id / 单字节 payload 原样往返）+illegal（null 经契约视图保持可见为 null——校验归端口层 SqliteSavePort，非 DTO 静默改写）；②改为"同 seed 两次相等+不同 seed 不等+超大图哈希与中等图不同"（确定性+区分度）；③补空 blob 双载荷与最大长度（1MiB×2）+int.MaxValue 检查点边界 | `LevelLayoutSnapshotDtoTests.cs`、`GridWorldTests.cs`、`LevelSaveBlobDtoTests.cs` |
| 4 | P（联测缺口，最重要） | T-032（C-05）与 T-037（C-11）各自单测，缺真实拼装联测：GridWorld 作为 IFlowGrid 进 FlowFieldManager 的端到端行为无人证（RL2 精神缺口） | 新增 `GridWorldFlowFieldIntegrationTests`（4 用例，全部真拼装无 mock）：①种子生成地形→Build→8 个采样起点的 NextStep 链逐步行走到达目标（距离严格递减+不踏不可通行格）；②SetTerrain 加墙→MarkDirty（紧包络区域）→Update 重建（rebuild=2）→新路径到达且绕行（不含任何墙格）；③水=任意模式下不可达+梯度采样永不提议不可通行格；④森林代价经管理器流进积分距离（单行受控图 40→70 精确断言）。**另**：08 T-038 行尾加 `[DEBT: 建筑 HP×5 进流场（BloodCost 接通占位表）在本任务实现——GridWorld.BloodCost 恒 0 为本批已知边界]` | `EraCore.Tests/Pathfinding/GridWorldFlowFieldIntegrationTests.cs`（新文件）；`docs/engineering/08-task-plan.md`（T-038 行+变更记录） |
| 5 | S（zone/lane 双源漂移风险） | F-117 要求"地图分区名与 SetRally lane 枚举同一数据源"，但规则实现只有 GridWorld.IsValidZoneId 一处手写字符扫描——动作 Schema 侧（K-3/T-021）落地时将复制第二份规则 | 新增 `EraCore/World/ZoneId.cs`：`static bool IsValid(string)`，正则 `^[a-z0-9-]+$` 单源（Compiled）；GridWorld.IsValidZoneId/ZoneExists/TryGetZoneCells 全部改调它（薄转发包装保留可读性）；新 `ZoneIdTests`：文档 lane 枚举（east-gate/south-gate=11-spec SP-1，barbican=SC-002 瓮城，north-gate/east-road=T-032 样例族；出处逐条注明）逐个过 IsValid+GridWorld 包装一致，反例（mid_lane 下划线/NorthGate 大写/空/null）红 | `game/src/EraCore/World/ZoneId.cs`（新）；`GridWorld.cs`（改调）；`EraCore.Tests/World/ZoneIdTests.cs`（新） |
| 6 | S（校验器判定强度） | T-046 任务卡验收="尺寸/帧数/调色板不符**拒绝**入库"，但 T-046 交付版：调色板 >64 默认 WARN（须人工记得加 --strict-palette）；帧数仅 INFO 无门禁 | ①调色板 >64 默认 **REJECT**；新增 `--allow-oversized-palette` 逃生阀（供美术特批，降 WARN，审批留痕走审查记录）；删除已冗余的 --strict-palette（默认即拒）；②新增 `--expect-frames N` 声明式帧数门禁：探测帧数≠N（含非横条图无帧数）→REJECT；缺省仍自动探测 INFO 并注明自动门禁 T-045 接入；③自测同步补正反用例（默认拒+逃生阀降 WARN、expect-frames 匹配过/不匹配拒/非横条拒/CLI 旗标端到端含 0 的参数错误） | `tools/ci/asset_validator.py`；`tools/ci/test_asset_validator.py` |
| 7 | S（金样纪律留痕） | T-035 提交 255313a 将金样断言（GoldenVerifications 冻结值）与其"生成器"（T-036 公式库+BenchCalc 算术）落在同一 commit——与 CS-6.2"金样变更 commit 禁同时包含 src/** 产品代码变更"的意图存在张力 | **留痕裁决**：CS-6.2 所约束的对象是 `golden/**` 数据文件框架（`--update-golden` 开关，T-035/T-083 落地启用，CS-6.1~6.4）——本批"金样"为测试内冻结常量+公式真计算（CS-4.4 已隔离期望值回灌），且 T-036 公式库与 T-035 金样按 08 任务表本就是两个先后 commit（026b792 先于 255313a）；同 commit 内出现的仅是 T-035 自带的 BenchCalc 算术助手（测试程序集，非 src/** 产品代码），不构成"改金样凑绿"。事实与裁决记入本 REV 供后续金样框架落地时对照；`golden/**` 落地后严格走 CS-6.2 独立 commit | 本文档（留痕）；无代码变更 |

## 三、完成门槛验证（全部真实执行，输出原文摘录）

① **全量测试**（`export NUGET_PACKAGES='D:\nuget' DOTNET_ROOT=/d/dotnet`；`/d/dotnet/dotnet.exe test game/EraTD.sln --nologo`）：

```
已通过! - 失败:     0，通过:   370，已跳过:     0，总计:   370，持续时间: 3 s - EraCore.Tests.dll (net8.0)
```

（基线 349 + 新增 21：联测 4 + ZoneId 12 + DTO 边界 5；GoldenVerifications/FlowFieldManagerTests 各重写 1 例不增计数。）

② **红线扫描**（`python tools/ci/redline_scan.py`）：

```
summary: 7 checks | 0 FAIL | 0 WARN | exit_code=0
（T3 测试 F-ID 关联覆盖率 211/211 方法已关联 (100%)，含本轮全部新增测试）
```

③ **资产校验器自测**（`python tools/ci/test_asset_validator.py`）：

```
selftest summary: 50 passed, 0 failed
```

④ **本地流水线**（`bash tools/ci/run_all.sh`，含 Godot headless 冒烟；真实输出摘录）：

```
==================== 门禁汇总（G1~G4 ↔ docs/engineering/13-quality-gates.md）====================
  G1   测试全绿   : PASS  dotnet test: 370 passed, 0 failed (7s)
  G2   红线零违规 : PASS  7 checks | 0 FAIL | 0 WARN | exit_code=0
  G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，3s）
  G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，11s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

## 四、遗留与挂账

- **Q11**（08 §6）：v2.1"57s/107s"推导口径追回——过渡期以 v3.2.1 重建口径为准（发现 #1）。
- **DEBT（T-038）**：建筑 HP×5 进流场（BloodCost 接通占位表）随 T-038 实现；GridWorld.BloodCost 恒 0 为本批已知边界（发现 #4）。
- **T-045**：帧数自动门禁（sheet 结构规格接入后替换 --expect-frames 声明式门禁，发现 #6）。
