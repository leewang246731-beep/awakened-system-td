# REV-20261013-wave0-round2 — Wave 0 二批 code-review 双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 0 二批提交 `5eec93d..HEAD`：d861dea（T-001+T-007~T-017 九表+基线核验）、a14a375（T-019~T-025 契约包）、05e9132（T-027 清账+T-018+T-031） |
| 审查日期 | 2026-10-13 |
| 双轴 | Standards（CODE_STANDARDS/DOCUMENT 追溯纪律/06 质量门禁）× Spec（08-task-plan 任务卡验收 + 03 §6 契约清单原文） |
| 处置执行 | 修复执行代理（本 commit）；10 项发现全部本轮修复，无挂账、无驳回 |
| 验证 | §三四项全过（Release 构建 0 警 0 错、测试 37/37、红线 0 FAIL 0 WARN、run_all.sh G1~G4 全 PASS 含 Godot 冒烟） |

## 一、发现总览

MAJOR ×4、MINOR ×6（编号沿用协调方审查发现清单 #1~#10）。commit 标题按协调方指定口径书写。

## 二、逐项处置表（双轴定位：S=Standards 轴，P=Spec 轴）

### MAJOR

| # | 轴 | 发现 | 处置 | 落点 |
|---|---|---|---|---|
| 1 | S+P（溯源真实性） | SP-3b"勇士 300/甲 4、攻城锤 1500/甲 12"标"已在案/逐字引用"，但全仓库（v3.2/07/10-recovered）无显式出处——仅可由 v3.2 附录 F.5 验算式反推 | 两行出处改为"**F.5 反推**"并写明推导式（勇士 300≈2490/(10×0.83)、甲 4=4/弩塔单发 17≈23.5% 对应 F.5"护甲吃 23%"；攻城锤 1500=2250/1.5、甲 12=(160−12)/160=92.5% 有效比）；表后加溯源更正注；SP-3c 结论行改"F.5 反推锚点，互证成立"；文档头状态行加例外声明；**自查记录 1 重写**——删除对该二项的"逐字引用"不实声称，改为"逐字引用/F.5 反推锚点/其余 [NEW] 三类如实分列" | `docs/engineering/11-spec-tables.md`（SP-3 头部定义行、SP-3b 两行+注、SP-3c 两行、状态行、自查记录 1、变更记录 v1.1） |
| 2 | S（与冻结契约矛盾） | 11-spec-tables"仿真 60 tick/s"与已冻结 K-1 `IClock.cs`（XML 注释明文"固定 30 Hz tick 时域"）矛盾 | 裁定**以冻结契约为准（30 Hz）**：文档头"单位约定"与 SP-3/SP-6 表头全部改为"仿真 tick=1/30 s（K-1 IClock 冻结）"；SP-3a/SP-3b/SP-6 速度数值按换算式 **X px/tick（60 旧口径）=2X px/tick（@30 Hz）=60X px/s** 全表重标（格/s 列不变），表头统一声明换算式；SP 表头各加一行"**口径依据：K-1 IClock 30 Hz**"；加载格式 speed_px_per_tick 字段注明 30 Hz 时域；Mindustry 60 tick 换算式降级为"仅设计期参考"；自查记录 2 同步 | `docs/engineering/11-spec-tables.md`（单位约定、SP-3 头部、SP-3a×9 行、SP-3b×9 行、SP-6 头部+2 行、SP-3c 加载格式、自查记录 2） |
| 3 | P（任务卡验收缺口）+S（K 标号错位） | ①T-023 验收"SimSnapshot/SimCommand/IUnit/IProjectile/IBuilding 成文"未交付（v1.0 仅交付智能体管线端口且误挂 K-6 号）；②03 §6 原文 K-5=卡片数据结构（C-13 域）、K-9=LlmGateway 端口（C-16 域），12 号文档却把管线端口与 ILlmTransport 全部标 K-6 | ①新增仿真侧五接口纯草案于新子目录 `Contracts/Sim/`（文件头 `// T2: pure contract — K-6 sim-side draft (T-023 remainder)`，XML 注释逐字段注明语义来自 v3.2 §6：伤害式/甲 clamp/护盾 E3/流场/30 Hz 时域/快照双缓冲/回放不变式）；②12 号文档重标：管线端口五对（IIntentRouter/IPlanner/IApprovalGate/IActionExecutor/IAgentReporter）→**K-5**（03 §6 原文=C-13 卡片域，W3 随 C-13 滚动冻结，与 07"K-5 于 W3 内滚动冻结"一致）、ILlmTransport→**K-9**（C-16 域，W1 末滚动冻结）、K-6 条目改为"仿真侧签名（本轮补草案）"；C# 文件头与 XML 注释 18 处 K-6 引用同步改写；③08 T-023 行尾加 `[PART2-DONE-20261013: 仿真五接口草案已补，见 12 号文档]` | `game/src/EraCore/Contracts/Sim/`（5 新文件）；`Contracts/Agent/`（12 文件头+XML 重标 K-5/K-9）；`docs/engineering/12-contracts-frozen.md`（K-5/K-6/K-9 三节重构+§三映射表 3 行）；`docs/engineering/08-task-plan.md`（T-023 行） |
| 4 | P（K-1 定义缺口） | 03 §6 K-1 原文含 `IJobPort` 与领域原语 CellCoord/EntityId/ResourceKey，T-019 交付未含 | `Contracts/Foundation/` 补四文件：`IJobPort.cs`（含 IJobHandle；XML 注明**依据 D-31**——EraCore 零直接 Thread 依赖、桌面 WorkerThread/Web 分帧切片/测试同步直跑、结果仅 tick 边界换入）、`CellCoord.cs`（record X,Y；1 格=32 px）、`EntityId.cs`（record long，C-12 签发不透明）、`ResourceKey.cs`（record string，包裹 K-2 资源全限定 id）——均纯类型带 T2 声明；12 号文档 K-1 清单同步四行（标 round 2 补缺） | `game/src/EraCore/Contracts/Foundation/`（4 新文件）；`docs/engineering/12-contracts-frozen.md` K-1 表 |

### MINOR

| # | 轴 | 发现 | 处置 | 落点 |
|---|---|---|---|---|
| 5 | S | `ContentType.cs` 引用措辞：15 类清单来源表述不准；Era 注释"E1 through E6 plus the finale"重复计数（E6 即终章） | 类注释改为"15 类清单首见于 12-contracts-frozen.md（依据回收的 v2.1 §16 条 4 压缩表述展开，v3.2 附录 G.1 条 4）"；Era 注释改"E1–E5 plus the finale (era index 6; six eras total, no double counting)"（与 ICampaignMeta 既有"E1=1…E6=6, finale=6"一致） | `game/src/EraCore/Contracts/Content/ContentType.cs` |
| 6 | S | `ActionResult.cs`/`Plan.cs`"40% of stock"措辞与 v3.2 §11.4 冻结语义不符（实为上限×40%，非库存×40%） | 两处统一改为"波内系统点支出 ≤上限×40%（v3.2 §11.4 冻结语义；N4）"；12 号文档 K-5 表 IPlanner 行同步 | `Contracts/Actions/ActionResult.cs`、`Contracts/Agent/Plan.cs`、12 号文档 |
| 7 | S | 12 号文档"33 个文件"与实际不符（v1.0 实际 36，登记数即错） | 按实数更正并计入本轮新增：**45**（36+9）；"七个子目录"→八子目录（新增 Sim）；变更记录 v1.1 注明计数更正来源 | `docs/engineering/12-contracts-frozen.md` |
| 8 | S（质量门禁可观测性） | `run_all.sh` trap EXIT 无条件删日志目录——失败时现场丢失，只剩已 tail 的片段 | trap 改为按退出码分流：非 0 退出**不删** `$LOG_DIR` 并打印保留路径（各步完整日志在内，排查后手动清理）；成功路径行为不变。最小改动（仅 trap 一行+注释两行） | `tools/ci/run_all.sh` |
| 9 | S（[NEW] 标注完整性） | SP-1b 瓮城"+20%"缺 [NEW] 标注；铁甲墙/雷达两行 E3+/E4 归类无章节设计复核挂接 | "+20%"补 [NEW-原型校准]；铁甲墙行补"[E3 归类待 T-112 章节设计复核（Wave 6）]"、雷达行补"[E4 归类待 T-123 章节设计复核（Wave 7）]"——注：协调方建议文本为"Wave 1"，但 08 任务表中 E3+/E4 章节内容设计归 T-112（W6）/T-123（W7），Wave 1 无对应任务，为避免再造成虚假溯源（本类缺陷即处置 #1 所纠），按真实任务号/Wave 挂接 | `docs/engineering/11-spec-tables.md` SP-1b 三行 |
| 10 | P（出口条件可验收性） | 08 W0 出口条件①"监督人签字"无签字落位 | ①项内补"签字栏：监督人____ 日期____" | `docs/engineering/08-task-plan.md` §1 W0 行 |

## 三、完成门槛验证（全部真实执行，输出原文摘录）

① **Release 构建**（`export NUGET_PACKAGES='D:\nuget'`；`/d/dotnet/dotnet.exe build game/EraTD.sln -c Release --nologo`）：

```
已成功生成。
    0 个警告
    0 个错误
```

② **全量测试**（`/d/dotnet/dotnet.exe test game/EraTD.sln --nologo -v q`）：

```
已通过! - 失败:     0，通过:    37，已跳过:     0，总计:    37，持续时间: 66 ms - EraCore.Tests.dll (net8.0)
```

③ **红线扫描**（`python tools/ci/redline_scan.py`；新契约文件经目录级 T2 条目 `T2: game/src/EraCore/Contracts` 前缀匹配覆盖，理由文案同步至 K-1~K-9）：

```
summary: 7 checks | 0 FAIL | 0 WARN | exit_code=0
```

④ **本地流水线**（`bash tools/ci/run_all.sh`，含 Godot headless 冒烟）：

```
  G1   测试全绿   : PASS  dotnet test: 37 passed, 0 failed (4s)
  G2   红线零违规 : PASS  7 checks | 0 FAIL | 0 WARN | exit_code=0
  G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，2s）
  G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，9s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

## 四、契约纪律登记（12 号文档 §四流程对照）

本轮改动的契约面（K 标号归位+K-1/K-6 补缺）按 12 号文档 §四流程执行：独立审查处置任务（本 commit）、双轴评审记录归档（本文件）、12 号文档版本 v1.0→v1.1 并于变更记录注明来源、消费泳道影响面——K-5/K-9 仅重编号不改签名（编译零改动的消费侧零影响）、K-1/K-6 为纯新增（无既有消费者）——编译/测试/红线/run_all 四项复验见 §三。
